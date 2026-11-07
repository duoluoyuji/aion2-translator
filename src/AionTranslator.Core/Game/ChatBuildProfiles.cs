using System.Text;

namespace AionTranslator.Core.Game;

/// <summary>一个已验证的游戏版本布局（RVA 来自逆向得到的构建配置表）。</summary>
public sealed record ChatBuildProfile(
    string Name,
    string ImageSha256,
    ulong ImageBytes,
    ulong WeakRva,
    ulong ChunksRva,
    ulong CountRva,
    ulong ClassRva,
    ulong VtableRva,
    ulong DataRva,
    ulong ControllerRva,
    ulong PropertyVtableRva,
    ulong RootGetterRva,
    ulong ChatNameRva)
{
    // 目前两种构建都是 A 版记录布局
    public int RecordBytes => 336;
    public int BodyOffset => 16;
    public int SenderOffset => 40;
    public int ChannelOffset => 120;
    public int MaximumRecordsPerChannel => 120;
    /// <summary>true 表示偏移是按特征码现场定位出来的，vtable 类校验已跳过。</summary>
    public bool RelaxedLayout { get; init; }
}

public static class ChatBuildProfileCatalog
{
    public static IEnumerable<ChatBuildProfile> All => ChatBuildProfiles.All;

    public const string RootGetter = ChatBuildProfiles.RootGetterPattern;

    /// <summary>把特征码字符串编译成 (字节, 掩码)，供诊断工具使用。</summary>
    public static (byte[] Pattern, byte[] Mask) CompilePattern(string pattern) => ChatBuildProfiles.Compile(pattern);
}

internal static class ChatBuildProfiles
{
    public static readonly ChatBuildProfile Steam20261024 = new(
        "Steam-20261024",
        "ce978f7525c05589cb1f1ff8a9c03628148bba3665da9594dbae8fa983d9b107",
        427397120UL, 254820872UL, 248031648UL, 248031668UL, 254425808UL, 230177968UL,
        209441584UL, 209437488UL, 224620872UL, 151244320UL, 230198032UL);

    public static readonly ChatBuildProfile Steam20261028 = new(
        "Steam-20261028",
        "7dfbcde8d97f42a7cef9261ad7ceef7f1e1cd773c7b2b4ca3c7cd54005389530",
        427474944UL, 254841896UL, 248052000UL, 248052020UL, 254446488UL, 230191584UL,
        209459712UL, 209455616UL, 224633096UL, 151251312UL, 230211624UL);

    /// <summary>
    /// 紫P（PURPLE）登录器渠道、2026-11-07 实测的一台机器：
    /// 特征码可命中，但 Vtable/Class 位置与 Steam 版不同，因此标记为宽松布局（不做 vtable 校验）。
    /// 数值来自 probe-log：RootGetter=151498448 Weak=255086616 Count=248289716 Chunks=248289696。
    /// </summary>
    public static readonly ChatBuildProfile Purple20261107 = new ChatBuildProfile(
        "PURPLE-20261107",
        "a650524f5dc93d5ffbb23c567b98225d56fb22a936f53dc5e6e1116316b51e3b",
        427851776UL, 255086616UL, 248289696UL, 248289716UL,
        0, 0, 0, 0, 0, 151498448UL, 0)
    {
        RelaxedLayout = true,
    };

    public static IEnumerable<ChatBuildProfile> All => new[] { Steam20261024, Steam20261028, Purple20261107 };

    public const string RootGetterPattern =
        "44 8B 0D ?? ?? ?? ?? 45 85 C9 74 4A 48 8B 05 ?? ?? ?? ?? 85 C0 78 3F 3B 05 ?? ?? ?? ?? 7D 37 44 8B C0 49 C1 E8 10 0F B7 C0 48 8D 14 40 48 8B 05 ?? ?? ?? ?? 4A 8B 0C C0 4C 8D 04 D1 4D 85 C0 74 15 45 39 48 10 75 0F 41 8B 40 08 A9 00 00 20 30 75 04 49 8B 00 C3 33 C0 C3";

    public const string VtablePattern = "48 8B 41 18 48 89 02 48 8B C2 C3";
    public const string DataPattern = "48 8B 41 38 48 85 C0 75 04 48 8D 41 28 C3";
    public const string DataTailPattern = "48 8D 41 10 C3";

    public const string PropertyVtablePattern =
        "48 89 5C 24 08 57 48 83 EC 20 48 8B D9 48 8B FA 48 8B 0A E8 ?? ?? ?? ?? 80 BB A8 00 00 00 00 48 8D 53 08 B8 B0 00 00 00 41 B8 60 00 00 00 44 0F 44 C0 48 8B CF 4C 03 C3 E8 ?? ?? ?? ?? 48 8B 0F 48 8B 5C 24 30 48 83 C4 20 5F E9 ?? ?? ?? ?? ";

    public static byte[] ChatSystemName => Encoding.Unicode.GetBytes("UChatSystem\0");

    /// <summary>把模式串拆成字节和掩码，便于扫描。</summary>
    public static (byte[] Pattern, byte[] Mask) Compile(string pattern)
    {
        var tokens = pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var bytes = new byte[tokens.Length];
        var mask = new byte[tokens.Length];
        for (var i = 0; i < tokens.Length; i++)
        {
            if (tokens[i] == "??") continue;
            bytes[i] = Convert.ToByte(tokens[i], 16);
            mask[i] = 1;
        }
        return (bytes, mask);
    }

    /// <summary>按字节模式匹配（"??" 为通配）。</summary>
    public static bool MatchPattern(ReadOnlySpan<byte> data, string pattern)
    {
        var tokens = pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (data.Length < tokens.Length) return false;
        for (var i = 0; i < tokens.Length; i++)
        {
            if (tokens[i] == "??") continue;
            if (data[i] != Convert.ToByte(tokens[i], 16)) return false;
        }
        return true;
    }
}
