using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AionTranslator.Core;

public sealed class TranslationOutcome
{
    public int Id { get; init; }
    public string Text { get; init; } = "";
    public bool Failed { get; init; }
    public string? Error { get; init; }
    public string Provider { get; init; } = "";
    public long ElapsedMs { get; init; }
    public bool FromCache { get; init; }
}

/// <summary>
/// OpenAI 兼容（/v1/chat/completions）批量翻译器：多服务商、多 key 轮换、批内去重、缓存、失败回退原文。
/// 不绑定任何厂商，硅基流动 / DeepSeek / OpenRouter / Ollama / 自建网关 都只是配置不同。
/// </summary>
public sealed class OpenAiTranslator : IDisposable
{
    private static readonly JsonSerializerOptions RequestJson = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly HttpClient _http;
    private readonly Settings _settings;
    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _concurrency;
    private int _roundRobin;

    public event Action<string>? Log;

    public OpenAiTranslator(Settings settings, HttpClient? http = null)
    {
        _settings = settings;
        _http = http ?? new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.All,
            MaxConnectionsPerServer = 8,
        })
        { Timeout = Timeout.InfiniteTimeSpan };
        _concurrency = new SemaphoreSlim(Math.Max(1, settings.MaxConcurrency));
    }

    public async Task<IReadOnlyDictionary<int, TranslationOutcome>> TranslateAsync(
        IReadOnlyList<ChatLine> lines, CancellationToken ct, string? targetOverride = null, string? preferredProvider = null)
    {
        var result = new Dictionary<int, TranslationOutcome>(lines.Count);
        var pending = new List<ChatLine>(lines.Count);

        // 缓存要按“目标语言 + 原文”存，否则先翻成英语再翻日语会直接命中英语译文
        var activeTarget = string.IsNullOrWhiteSpace(targetOverride) ? _settings.TargetLanguage : targetOverride!;

        foreach (var line in lines)
        {
            var key = CacheKey(activeTarget, line.Text);
            if (_cache.TryGetValue(key, out var hit))
                result[line.Id] = new TranslationOutcome { Id = line.Id, Text = hit, Provider = "cache", FromCache = true };
            else
                pending.Add(line);
        }

        if (pending.Count == 0) return result;

        // 同一批里重复的原文只发一次，翻完再按原文回填所有 id（避免同句两条译文不一致 + 省 token）
        var unique = new Dictionary<string, ChatLine>(StringComparer.Ordinal);
        var group = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var repKey = new Dictionary<int, string>();
        foreach (var line in pending)
        {
            var key = Normalize(line.Text);
            if (!unique.ContainsKey(key)) { unique[key] = line; repKey[line.Id] = key; }
            if (!group.TryGetValue(key, out var ids)) group[key] = ids = new List<int>();
            ids.Add(line.Id);
        }

        int batch = Math.Clamp(_settings.BatchSize, 1, 20);
        var uniques = unique.Values.ToList();
        var chunks = new List<List<ChatLine>>();
        for (int i = 0; i < uniques.Count; i += batch)
            chunks.Add(uniques.GetRange(i, Math.Min(batch, uniques.Count - i)));

        var tasks = chunks.Select(async chunk =>
        {
            await _concurrency.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var map = await TranslateChunkAsync(chunk, ct, targetOverride, preferredProvider).ConfigureAwait(false);
                lock (result)
                {
                    foreach (var kv in map)
                    {
                        if (repKey.TryGetValue(kv.Key, out var key) && group.TryGetValue(key, out var ids))
                        {
                            foreach (var id in ids)
                                result[id] = new TranslationOutcome
                                {
                                    Id = id,
                                    Text = kv.Value.Text,
                                    Failed = kv.Value.Failed,
                                    Error = kv.Value.Error,
                                    Provider = kv.Value.Provider,
                                    ElapsedMs = kv.Value.ElapsedMs,
                                };
                        }
                        else
                        {
                            result[kv.Key] = kv.Value;
                        }
                    }
                }
            }
            finally
            {
                _concurrency.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return result;
    }

    private async Task<Dictionary<int, TranslationOutcome>> TranslateChunkAsync(
        List<ChatLine> chunk, CancellationToken ct, string? targetOverride, string? preferredProvider)
    {
        var target = string.IsNullOrWhiteSpace(targetOverride) ? _settings.TargetLanguage : targetOverride!;
        bool needMultiLanguage = !string.Equals(target, "简体中文", StringComparison.Ordinal);

        var providers = _settings.Providers
            .Where(p => p.Usable)
            .Where(p => !needMultiLanguage || p.TargetLanguageCapable)
            .OrderBy(p => p.Priority)
            .ToList();

        // 手动翻译可以单独指定走哪个通道（例如聊天用 B站求快、手动翻译用大模型求多语言）
        if (!string.IsNullOrWhiteSpace(preferredProvider))
        {
            var preferred = providers.FirstOrDefault(p => p.Name.Equals(preferredProvider, StringComparison.Ordinal));
            if (preferred is not null)
            {
                providers.Remove(preferred);
                providers.Insert(0, preferred);
            }
        }

        string lastError = needMultiLanguage
            ? "翻译成" + target + "没有可用通道：请确认通道已启用（B站公共接口免 Key，可直接用）"
            : "没有可用的服务商（请填写 API Key）";

        foreach (var provider in providers)
        {
            var keys = provider.Keyless
                ? new List<string> { "" }
                : provider.Keys.Where(k => !string.IsNullOrWhiteSpace(k)).Distinct().ToList();
            int start = keys.Count == 0 ? 0 : Math.Abs(Interlocked.Increment(ref _roundRobin)) % keys.Count;
            for (int k = 0; k < keys.Count; k++)
            {
                var apiKey = keys[(start + k) % keys.Count];
                for (int attempt = 0; attempt <= _settings.MaxRetries; attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    var sw = Stopwatch.StartNew();
                    try
                    {
                        var raw = await CallAsync(provider, apiKey, chunk, target, ct).ConfigureAwait(false);
                        sw.Stop();
                        var parsed = Parse(raw, chunk);
                        if (parsed.Count == 0)
                        {
                            lastError = provider.Name + ": 返回内容无法解析为 JSON";
                            continue;
                        }

                        var map = new Dictionary<int, TranslationOutcome>();
                        foreach (var line in chunk)
                        {
                            if (parsed.TryGetValue(line.Id, out var translated) && !string.IsNullOrWhiteSpace(translated))
                            {
                                _cache[CacheKey(target, line.Text)] = translated;
                                map[line.Id] = new TranslationOutcome
                                {
                                    Id = line.Id, Text = translated, Provider = provider.Name,
                                    ElapsedMs = sw.ElapsedMilliseconds,
                                };
                            }
                            else
                            {
                                map[line.Id] = new TranslationOutcome
                                {
                                    Id = line.Id, Text = line.Text, Failed = true, Provider = provider.Name,
                                    Error = "该条缺少译文", ElapsedMs = sw.ElapsedMilliseconds,
                                };
                            }
                        }
                        Log?.Invoke($"{provider.Name}/{provider.Model} ok {chunk.Count} 条 {sw.ElapsedMilliseconds}ms");
                        return map;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        sw.Stop();
                        lastError = provider.Name + ": " + Describe(ex);
                        Log?.Invoke($"{provider.Name} 第 {attempt + 1} 次失败: {Describe(ex)}");
                        if (ex is HttpRequestException
                            {
                                StatusCode: HttpStatusCode.Unauthorized
                                    or HttpStatusCode.PaymentRequired
                                    or HttpStatusCode.Forbidden
                            })
                            break; // 这个 key 不行，换下一个
                        if (attempt < _settings.MaxRetries)
                            await Task.Delay(400 * (attempt + 1), ct).ConfigureAwait(false);
                    }
                }
            }
        }

        var failed = new Dictionary<int, TranslationOutcome>();
        foreach (var line in chunk)
            failed[line.Id] = new TranslationOutcome { Id = line.Id, Text = line.Text, Failed = true, Error = lastError };
        Log?.Invoke("整批失败，回退原文：" + lastError);
        return failed;
    }

    private async Task<string> CallAsync(Provider provider, string apiKey, List<ChatLine> chunk, string target, CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = provider.Model,
            ["messages"] = new object[]
            {
                new { role = "system", content = BuildSystemPrompt(target) },
                new { role = "user", content = BuildUserPayload(chunk) },
            },
            ["temperature"] = provider.Temperature,
            ["max_tokens"] = provider.MaxTokens,
            ["stream"] = false,
        };

        if (provider.ExtraBody is { Count: > 0 })
            foreach (var kv in provider.ExtraBody)
                payload[kv.Key] = kv.Value;

        var body = JsonSerializer.Serialize(payload, RequestJson);
        using var req = new HttpRequestMessage(HttpMethod.Post, provider.Endpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (!provider.Keyless && !string.IsNullOrWhiteSpace(apiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        req.Headers.TryAddWithoutValidation("X-Title", "AION2 Live Translate");
        // B站网关对默认 .NET UA 会回 412，统一带标准浏览器 UA
        req.Headers.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, _settings.TimeoutSeconds)));
        using var resp = await _http.SendAsync(req, timeout.Token).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"{provider.Name} HTTP {(int)resp.StatusCode}: {Truncate(text, 180)}", null, resp.StatusCode);

        using var doc = JsonDocument.Parse(text);
        var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        if (string.IsNullOrWhiteSpace(content)) throw new InvalidDataException("返回 content 为空");
        return content!;
    }

    private string? _basePrompt;

    private string BasePrompt()
    {
        if (_basePrompt is not null) return _basePrompt;
        var path = _settings.SystemPromptPath;
        var bundled = System.IO.Path.Combine(AppContext.BaseDirectory, "prompt", "system-prompt.txt");
        if (File.Exists(path))
        {
            _basePrompt = File.ReadAllText(path);
            Log?.Invoke("术语库提示词：" + path);
        }
        else if (File.Exists(bundled))
        {
            _basePrompt = File.ReadAllText(bundled);
            Log?.Invoke("术语库提示词：用户目录缺失，改用程序自带 " + bundled);
        }
        else
        {
            _basePrompt = "你是游戏聊天翻译器，把玩家聊天翻译成" + _settings.TargetLanguage +
                          "。只输出一个 JSON 对象，键与输入相同，值是译文。";
            Log?.Invoke("术语库提示词：未找到，使用内置简版");
        }
        return _basePrompt;
    }

    private string BuildSystemPrompt(string target)
    {
        bool toChinese = target.Contains("中文", StringComparison.Ordinal);
        var sb = new StringBuilder();

        if (toChinese)
        {
            sb.Append(BasePrompt().Replace("{{TARGET_LANGUAGE}}", target));
            sb.Append("\n【目标语言】").Append(target).Append('\n');
        }
        else
        {
            // 翻成外语时不能用"翻成中文"的术语库提示词当主指令，
            // 否则模型会退化成回显/释义（B站这类翻译专用模型的常见坑）。
            sb.Append("你是 MMORPG 游戏聊天本地化翻译。把玩家聊天内容翻译成").Append(target).Append("。\n")
              .Append("要求：口语、自然、符合游戏语境；专有名词（副本、Boss、技能）保持玩家通用叫法。\n")
              .Append("只输出一个 JSON 对象：键与输入完全相同，值是").Append(target).Append("译文。\n")
              .Append("不要输出 markdown 代码块，不要任何解释或原文对照。\n");
        }

        if (_settings.Glossary.Count > 0)
        {
            sb.Append("\n【固定译法】\n");
            foreach (var kv in _settings.Glossary)
                sb.Append("- ").Append(kv.Key).Append(" => ").Append(kv.Value).Append('\n');
        }
        foreach (var line in _settings.ExtraPromptLines)
            if (!string.IsNullOrWhiteSpace(line)) sb.Append(line).Append('\n');
        return sb.ToString();
    }
    private static string BuildUserPayload(List<ChatLine> chunk)
    {
        var dict = new Dictionary<string, string>(chunk.Count);
        foreach (var line in chunk) dict[line.Id.ToString()] = line.Text;
        return JsonSerializer.Serialize(dict, RequestJson);
    }

    private static Dictionary<int, string> Parse(string content, List<ChatLine> chunk)
    {
        var json = ExtractJson(content);
        var map = new Dictionary<int, string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return map;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.String) continue;
                if (int.TryParse(prop.Name, out var id))
                    map[id] = prop.Value.GetString() ?? "";
            }
            return map;
        }
        catch (JsonException)
        {
            return map;
        }
    }

    private static string ExtractJson(string content)
    {
        var s = content.Trim();
        if (s.StartsWith("```", StringComparison.Ordinal))
        {
            int nl = s.IndexOf('\n');
            if (nl >= 0) s = s[(nl + 1)..];
            int fence = s.LastIndexOf("```", StringComparison.Ordinal);
            if (fence >= 0) s = s[..fence];
        }
        int start = s.IndexOf('{');
        int end = s.LastIndexOf('}');
        if (start >= 0 && end > start) s = s[start..(end + 1)];
        return s.Trim();
    }

    private static string Normalize(string text) => text.Trim();

    private static string CacheKey(string target, string text) => target + "|" + Normalize(text);

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    private static string Describe(Exception ex) => ex switch
    {
        TaskCanceledException => "请求超时",
        HttpRequestException hre => hre.Message,
        JsonException => "响应不是合法 JSON",
        _ => ex.Message,
    };

    public void Dispose()
    {
        _http.Dispose();
        _concurrency.Dispose();
    }
}
