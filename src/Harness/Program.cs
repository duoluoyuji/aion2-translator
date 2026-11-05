using System.Diagnostics;
using AionTranslator.Core;

var settingsPath = args.Length > 0
    ? args[0]
    : Path.Combine(AppContext.BaseDirectory, "settings.json");

var settings = Settings.Load(settingsPath);
var keyFromEnv = Environment.GetEnvironmentVariable("AION2_KEY");
if (!string.IsNullOrWhiteSpace(keyFromEnv) && settings.Providers.Count > 0)
{
    settings.Providers[0].Keys.Clear();
    settings.Providers[0].Keys.Add(keyFromEnv.Trim());
    settings.Save(settingsPath);
    Console.WriteLine($"[i] key 写入 {settings.Providers[0].Name}");
}
settings.Save(settingsPath);

foreach (var p in settings.Providers)
    Console.WriteLine($"[i] provider {p.Name} model={p.Model} keys={p.Keys.Count} enabled={p.Enabled} endpoint={p.Endpoint}");

var lines = new List<ChatLine>
{
    new(1, "LFG ferocios conquest need healer tank", "Sitkarp"),
    new(2, "gg wp"),
    new(3, "i got it when i bought it right before"),
    new(4, "whisper me pls, need dps for boss"),
    new(5, "this lag is unplayable, everyone rubberbanding?"),
    new(6, "anyone selling legendary weapon mats cheap?"),
    new(7, "LFG ferocios conquest need healer tank", "Sitkarp"), // 重复 -> 命中缓存
};

using var translator = new OpenAiTranslator(settings);
translator.Log += m => Console.WriteLine("  · " + m);

var sw = Stopwatch.StartNew();
var result = await translator.TranslateAsync(lines, CancellationToken.None);
sw.Stop();

Console.WriteLine($"\n== 结果（总耗时 {sw.ElapsedMilliseconds} ms）==");
foreach (var line in lines)
{
    var o = result.TryGetValue(line.Id, out var v) ? v : null;
    var tag = o is null ? "??" : o.FromCache ? "cache" : o.Failed ? "FAIL" : o.Provider;
    Console.WriteLine($"[{line.Id}] ({tag}, {o?.ElapsedMs ?? 0}ms) {line.Text}");
    Console.WriteLine($"      -> {o?.Text}{(o?.Error is { Length: > 0 } e ? "   [" + e + "]" : "")}");
}
