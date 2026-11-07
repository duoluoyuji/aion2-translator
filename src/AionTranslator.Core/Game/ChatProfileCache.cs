using System.Text.Json;
using System.Text.Json.Serialization;

namespace AionTranslator.Core.Game;

/// <summary>
/// 本机“已发现的布局”缓存：按客户端 exe 的 SHA256 记住一次成功定位到的 RVA，
/// 以后启动直接命中，不再依赖扫描代码段（扫描在带壳客户端上时好时坏）。
/// </summary>
public static class ChatProfileCache
{
    public sealed record Entry
    {
        public string Sha256 { get; set; } = "";
        public string Name { get; set; } = "";
        public ulong ImageBytes { get; set; }
        public ulong RootGetterRva { get; set; }
        public ulong WeakRva { get; set; }
        public ulong CountRva { get; set; }
        public ulong ChunksRva { get; set; }
        public int OwnerOffset { get; set; }
        public int TableOffset { get; set; }
        public string SavedAt { get; set; } = "";
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static string? _path;

    /// <summary>由 UI 层在启动时指定缓存文件路径（放在数据目录里）。</summary>
    public static void Configure(string path) => _path = path;

    private static List<Entry> LoadAll()
    {
        try
        {
            if (_path is null || !File.Exists(_path)) return new List<Entry>();
            var list = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(_path), Json);
            return list ?? new List<Entry>();
        }
        catch
        {
            return new List<Entry>();
        }
    }

    public static Entry? Find(string sha256)
    {
        if (string.IsNullOrWhiteSpace(sha256)) return null;
        return LoadAll().FirstOrDefault(e => e.Sha256.Equals(sha256, StringComparison.OrdinalIgnoreCase));
    }

    public static void Save(Entry entry)
    {
        if (_path is null || string.IsNullOrWhiteSpace(entry.Sha256)) return;
        try
        {
            var list = LoadAll();
            list.RemoveAll(e => e.Sha256.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase));
            entry.SavedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            list.Add(entry);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path)) ?? ".");
            File.WriteAllText(_path, JsonSerializer.Serialize(list, Json), new System.Text.UTF8Encoding(false));
        }
        catch
        {
            // 缓存写失败不影响运行
        }
    }

    /// <summary>把缓存条目转成可直接使用的 profile（布局为“宽松”，不需要 vtable/class 校验）。</summary>
    public static ChatBuildProfile ToProfile(Entry entry) => new(
        "缓存-" + entry.Sha256[..Math.Min(8, entry.Sha256.Length)],
        entry.Sha256,
        entry.ImageBytes,
        entry.WeakRva,
        entry.ChunksRva,
        entry.CountRva,
        0, 0, 0, 0, 0,
        entry.RootGetterRva,
        0)
    {
        RelaxedLayout = true,
    };
}
