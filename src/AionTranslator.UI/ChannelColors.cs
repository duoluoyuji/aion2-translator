using System.Windows.Media;

namespace AionTranslator.UI;

/// <summary>
/// 按频道给玩家 ID 上色，方便一眼分清是哪个频道在说话：
/// 世界（种族）= 黄，附近 = 白，组队/部队/副本 = 蓝。
/// </summary>
public static class ChannelColors
{
    private static readonly Brush WorldMain = Frozen(0xC7, 0x78, 0x00);      // 主界面：琥珀黄
    private static readonly Brush WorldOverlay = Frozen(0xFF, 0xE2, 0x4A);   // 悬浮窗：亮黄
    private static readonly Brush NearbyMain = Frozen(0x6C, 0x7A, 0x93);     // 主界面：浅灰蓝（白底可见）
    private static readonly Brush NearbyOverlay = Frozen(0xFF, 0xFF, 0xFF);  // 悬浮窗：纯白
    private static readonly Brush PartyMain = Frozen(0x2F, 0x6B, 0xFF);      // 主界面：蓝
    private static readonly Brush PartyOverlay = Frozen(0x8C, 0xBC, 0xFF);   // 悬浮窗：亮蓝
    private static readonly Brush NeutralMain = Frozen(0x4C, 0x58, 0x72);
    private static readonly Brush NeutralOverlay = Frozen(0xD8, 0xE2, 0xF5);

    private static bool IsWorld(string channel) =>
        channel.Contains("种族") || channel.Contains("世界");

    private static bool IsNearby(string channel) =>
        channel.Contains("附近") || channel == "一般" || channel.Contains("呐喊");

    private static bool IsParty(string channel) =>
        channel.Contains("小队") || channel.Contains("队伍") || channel.Contains("部队")
        || channel.Contains("副本") || channel.Contains("军团") || channel.Contains("指挥");

    public static Brush ForMain(string channel) =>
        IsWorld(channel) ? WorldMain
        : IsNearby(channel) ? NearbyMain
        : IsParty(channel) ? PartyMain
        : NeutralMain;

    public static Brush ForOverlay(string channel) =>
        IsWorld(channel) ? WorldOverlay
        : IsNearby(channel) ? NearbyOverlay
        : IsParty(channel) ? PartyOverlay
        : NeutralOverlay;

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
