using System.Text.Json;
using System.Text.Json.Serialization;

namespace AionTranslator.Core;

/// <summary>一个翻译通道。BaseUrl 填到 /v1 为止，或直接填完整 chat/completions 地址。</summary>
public sealed class Provider
{
    public string Name { get; set; } = "SiliconFlow";
    public string BaseUrl { get; set; } = "https://api.siliconflow.cn/v1";
    public string Model { get; set; } = "deepseek-ai/DeepSeek-V4.1";
    /// <summary>可放多个 key，遇到 401/402/403 自动换下一个。</summary>
    public List<string> Keys { get; set; } = new();
    /// <summary>公共接口（不需要 key，例如 B站 index-translate）：为 true 时不发 Authorization。</summary>
    public bool Keyless { get; set; }
    /// <summary>该通道能否翻译成中文以外的目标语言（B站公共接口只做中译，故为 false）。</summary>
    public bool TargetLanguageCapable { get; set; } = true;
    /// <summary>数值越小越优先。</summary>
    public int Priority { get; set; }
    public bool Enabled { get; set; } = true;
    public int MaxTokens { get; set; } = 512;
    public double Temperature { get; set; }
    /// <summary>额外塞进请求体的字段，例如 enable_thinking。</summary>
    public Dictionary<string, JsonElement>? ExtraBody { get; set; }

    [JsonIgnore]
    public string Endpoint => BaseUrl.TrimEnd('/').EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
        ? BaseUrl.TrimEnd('/')
        : BaseUrl.TrimEnd('/') + "/chat/completions";

    [JsonIgnore]
    public bool Usable => Enabled && (Keyless || Keys.Any(k => !string.IsNullOrWhiteSpace(k)));
}

public sealed class Settings
{
    public string TargetLanguage { get; set; } = "简体中文";
    public List<Provider> Providers { get; set; } = new();
    /// <summary>每批送多少条聊天进一次请求；实时聊天建议 1~3。</summary>
    public int BatchSize { get; set; } = 3;
    public int MaxConcurrency { get; set; } = 3;
    public int TimeoutSeconds { get; set; } = 45;
    public int MaxRetries { get; set; } = 2;
    /// <summary>术语库/规则提示词文件，相对 settings.json 所在目录。</summary>
    public string SystemPromptFile { get; set; } = "prompt/system-prompt.txt";
    /// <summary>追加的固定译法。</summary>
    public Dictionary<string, string> Glossary { get; set; } = new();
    public string[] ExtraPromptLines { get; set; } = Array.Empty<string>();
    /// <summary>悬浮窗背景不透明度（20-95）。</summary>
    public int OverlayOpacity { get; set; } = 72;
    /// <summary>悬浮窗鼠标穿透。</summary>
    /// <summary>悬浮窗不进入录屏/截图（N卡录制、OBS 都拍不到它）。</summary>
    public bool HideFromCapture { get; set; }

    /// <summary>配置版本，用于老配置的一次性迁移。</summary>
    public int ConfigVersion { get; set; }

    /// <summary>手动翻译条上方的快捷用语（设置里自己加，默认空）。</summary>
    public List<string> QuickPhrases { get; set; } = new();

    /// <summary>悬浮窗鼠标穿透（默认关闭：默认让它能拖动、能点）。</summary>
    public bool ClickThrough { get; set; }
    /// <summary>悬浮窗译文文字大小（12-30）。</summary>
    public int OverlayFontSize { get; set; } = 15;
    /// <summary>手动翻译上一次选的目标语言。</summary>
    public string ManualLanguage { get; set; } = "英语";
    /// <summary>翻译通道：聊天和悬浮窗手动翻译共用这一条。</summary>
    public string Provider { get; set; } = "B站公共接口";
    /// <summary>老版本遗留字段，只为读旧配置时迁移用，不再参与逻辑。</summary>
    public string AutoProvider { get; set; } = "";
    /// <summary>老版本遗留字段，只为读旧配置时迁移用，不再参与逻辑。</summary>
    public string ManualProvider { get; set; } = "";
    /// <summary>聊天来源：游戏内存 或 演示数据。</summary>
    public string SourceMode { get; set; } = "游戏内存";
    /// <summary>主窗口保留的对话条数（只存在内存，超出丢最旧）。</summary>
    public int MaxHistory { get; set; } = 50;

    /// <summary>启动时只翻译每个频道最近 N 条历史（0 = 历史一条都不翻，只翻启动之后的新消息）。</summary>
    public int StartupRecentMessages { get; set; } = 0;

    /// <summary>是否翻译 NPC/系统/战斗记录等刷屏频道（默认否，省额度）。</summary>
    public bool TranslateNoiseChannels { get; set; }
    /// <summary>输入法兼容模式：关闭悬浮窗的透明合成，某些第三方输入法（豆包等）打不进字时用。</summary>
    public bool OverlayImeCompat { get; set; }
    /// <summary>一键发送：手动条上的按钮把译文自动输入到游戏聊天框（仅英文可靠），你再自己按回车发出。</summary>
    public bool OneKeySend { get; set; }


    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static Settings Load(string path)
    {
        var settings = File.Exists(path)
            ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Json) ?? CreateDefault()
            : CreateDefault();
        settings.BaseDirectory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        EnsureBuiltInProviders(settings);

        // 旧版本默认把悬浮窗排除在录屏之外，导致 N卡/OBS 拍不到它。
        // 老配置文件里存的是 true，这里强制改回可录制。
        settings.HideFromCapture = false;

        // 悬浮窗鼠标穿透默认关闭：默认要能拖动、能点，想穿透自己去设置里开。
        // 老配置里存的是 true，这里一次性改掉（记版本号，之后尊重用户自己的选择）。
        if (settings.ConfigVersion < 2)
        {
            settings.ClickThrough = false;
            settings.ConfigVersion = 2;
        }

        // 老版本把通道拆成"自动/手动"两条，现在合并成一条，读旧配置时迁移过来
        if (string.IsNullOrWhiteSpace(settings.Provider))
            settings.Provider = !string.IsNullOrWhiteSpace(settings.AutoProvider)
                ? settings.AutoProvider
                : !string.IsNullOrWhiteSpace(settings.ManualProvider)
                    ? settings.ManualProvider
                    : "B站公共接口";
        settings.AutoProvider = "";
        settings.ManualProvider = "";

        // 硅基流动默认模型换成 DeepSeek V4.1（只在用户没自己改过的时候替换）
        var silicon = settings.Providers.FirstOrDefault(p => p.Name == "SiliconFlow");
        if (silicon is not null && silicon.Model is "zai-org/GLM-5.3-Flash" or "")
            silicon.Model = "deepseek-ai/DeepSeek-V4.1";
        return settings;
    }

    private static void EnsureBuiltInProviders(Settings settings)
    {
        if (!settings.Providers.Any(p => p.Name.StartsWith("自定义", StringComparison.Ordinal)))
        {
            settings.Providers.Add(new Provider
            {
                Name = "自定义（OpenAI 兼容）",
                BaseUrl = "",
                Model = "",
                Priority = 3,
                Enabled = false,
                MaxTokens = 512,
            });
        }
    }
    public void Save(string path)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var json = JsonSerializer.Serialize(this, Json);
        try
        {
            WriteAtomically(full, json);
        }
        catch (UnauthorizedAccessException)
        {
            TryClearReadOnly(full);
            WriteAtomically(full, json);
        }
    }

    private static void WriteAtomically(string path, string content)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, content, new System.Text.UTF8Encoding(false));
        if (File.Exists(path))
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Replace(temp, path, null, true);
        }
        else
        {
            File.Move(temp, path);
        }
    }

    private static void TryClearReadOnly(string path)
    {
        try
        {
            if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
        }
        catch
        {
            // 清不掉就交给上层兜底
        }
    }

    [JsonIgnore]
    public string BaseDirectory { get; private set; } = ".";

    [JsonIgnore]
    public string SystemPromptPath => Path.IsPathRooted(SystemPromptFile)
        ? SystemPromptFile
        : Path.Combine(BaseDirectory, SystemPromptFile);

    public static Settings CreateDefault() => new()
    {
        Providers =
        {
            new Provider
            {
                Name = "B站公共接口",
                BaseUrl = "https://index-translate.bilibili.com/v1",
                Model = "Index-Translate-35B-A3B",
                Keyless = true,
                // 只要 system 里写明目标语言，它中翻英/日/韩同样可用
                TargetLanguageCapable = true,
                Priority = 0,
                MaxTokens = 2048,
            },
            new Provider
            {
                Name = "SiliconFlow",
                BaseUrl = "https://api.siliconflow.cn/v1",
                Model = "deepseek-ai/DeepSeek-V4.1",
                Priority = 1,
                MaxTokens = 512,
                Enabled = false,
            },
            new Provider
            {
                Name = "DeepSeek 官方",
                BaseUrl = "https://api.deepseek.com",
                Model = "deepseek-chat",
                Priority = 2,
                Enabled = false,
                MaxTokens = 512,
            },
            new Provider
            {
                Name = "自定义（OpenAI 兼容）",
                BaseUrl = "",
                Model = "",
                Priority = 3,
                Enabled = false,
                MaxTokens = 512,
            },
        },
    };
}

public sealed record ChatLine(int Id, string Text, string Speaker = "");