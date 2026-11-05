using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AionTranslator.UI;

/// <summary>
/// 把文字送进游戏聊天。
/// 走「往游戏窗口投递消息」这条路（第一版就是它，实测能把英文打进聊天框）：
///   回车 → 逐字 WM_CHAR → 回车。
/// 投递不需要游戏在前台；但开聊天框那一下仍会先尝试把游戏切到前台。
/// 中日韩在这条路上会变成方框（游戏按 ASCII 处理），所以中日韩一律退回“复制”。
/// </summary>
public static class ChatSender
{
    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_KEYUP = 0x0101;
    private const uint WM_CHAR = 0x0102;
    private const int VK_RETURN = 0x0D;

    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder buffer, int length);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr handle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr handle);


    // ---------- 真实按键（开聊天框用：游戏只认真实回车） ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    private static void RealKeyTap(int vk, int holdMs = 45)
    {
        var down = new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = (ushort)vk } } };
        var up = new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = (ushort)vk, dwFlags = KEYEVENTF_KEYUP } } };
        SendInput(1, new[] { down }, Marshal.SizeOf<INPUT>());
        Thread.Sleep(holdMs);
        SendInput(1, new[] { up }, Marshal.SizeOf<INPUT>());
        Thread.Sleep(40);
    }
    public static string LastError { get; private set; } = "";

    /// <summary>由主窗口注入：返回最近聊天文本，用来判断消息到底有没有发出去。</summary>
    public static Func<IReadOnlyList<string>>? ChatSnapshot;

    // ---------- 找窗口 ----------

    public static IntPtr FindGameWindow()
    {
        var pids = new HashSet<uint>();
        foreach (var p in Process.GetProcesses())
        {
            if (p.ProcessName.IndexOf("AION", StringComparison.OrdinalIgnoreCase) < 0) continue;
            pids.Add((uint)p.Id);
        }
        if (pids.Count == 0) return IntPtr.Zero;

        var best = IntPtr.Zero;
        var bestScore = -1;
        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out var pid);
            if (!pids.Contains(pid) || !IsWindowVisible(hWnd)) return true;
            var cls = new StringBuilder(256);
            GetClassName(hWnd, cls, cls.Capacity);
            var score = cls.ToString().Contains("Unreal", StringComparison.OrdinalIgnoreCase) ? 10 : 1;
            if (score > bestScore)
            {
                bestScore = score;
                best = hWnd;
            }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    /// <summary>尽量把游戏切到前台；失败不致命（投递消息本身不要求前台）。</summary>
    public static bool ForceForeground(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return false;
        if (GetForegroundWindow() == hWnd) return true;

        var targetThread = GetWindowThreadProcessId(hWnd, out _);
        var self = GetCurrentThreadId();
        var fg = GetForegroundWindow();
        var fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out _);

        if (fgThread != 0) AttachThreadInput(self, fgThread, true);
        if (targetThread != 0) AttachThreadInput(self, targetThread, true);
        BringWindowToTop(hWnd);
        SetForegroundWindow(hWnd);
        if (targetThread != 0) AttachThreadInput(self, targetThread, false);
        if (fgThread != 0) AttachThreadInput(self, fgThread, false);

        for (var i = 0; i < 8; i++)
        {
            if (GetForegroundWindow() == hWnd) return true;
            Thread.Sleep(25);
        }
        return GetForegroundWindow() == hWnd;
    }

    // ---------- 投递 ----------

    private static void PostKey(IntPtr hWnd, int vk)
    {
        PostMessage(hWnd, WM_KEYDOWN, (IntPtr)vk, (IntPtr)1);
        Thread.Sleep(25);
        PostMessage(hWnd, WM_KEYUP, (IntPtr)vk, unchecked((IntPtr)(int)0xC0000001));
        Thread.Sleep(35);
    }

    public static bool IsAsciiOnly(string text) => text.All(ch => ch < 0x80);

    /// <summary>是否含中文/日文/韩文（这些字进游戏会变方框）。</summary>
    public static bool HasCjk(string text)
    {
        foreach (var ch in text)
        {
            if (ch >= 0x2E80 && ch <= 0x9FFF) return true;   // 中日韩统一表意文字/部首
            if (ch >= 0xAC00 && ch <= 0xD7AF) return true;   // 韩文
            if (ch >= 0x3040 && ch <= 0x30FF) return true;   // 平假名/片假名
            if (ch >= 0xF900 && ch <= 0xFAFF) return true;   // 兼容表意
            if (ch >= 0xFF00 && ch <= 0xFF60) return true;   // 全角标点
            if (ch >= 0x3000 && ch <= 0x303F) return true;   // 中文标点
        }
        return false;
    }

    /// <summary>把英文里常见的弯引号、破折号等换成 ASCII，避免被误判成"非英文"。</summary>
    public static string NormalizeForGame(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            sb.Append(ch switch
            {
                '\u2018' or '\u2019' => '\'',
                '\u201C' or '\u201D' => '"',
                '\u2013' or '\u2014' => '-',
                '\u2026' => '.',
                '\u00A0' => ' ',
                '\u00B7' => '-',
                _ => ch
            });
        }
        return sb.ToString();
    }

    /// <summary>打开聊天框并逐字输入，不发送。</summary>
    public static bool TypeIntoChat(string text)
    {
        LastError = "";
        text = (text ?? "").Trim('\r', '\n');
        if (text.Length == 0) { LastError = "没有可输入的文字"; return false; }

        var hWnd = FindGameWindow();
        if (hWnd == IntPtr.Zero) { LastError = "没找到游戏窗口"; return false; }

        // 只投递，不切前台：实测游戏忽略真实注入按键，但认投递消息
        Thread.Sleep(80);

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (char.IsHighSurrogate(ch) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                PostMessage(hWnd, WM_CHAR, (IntPtr)ch, (IntPtr)1);
                PostMessage(hWnd, WM_CHAR, (IntPtr)text[i + 1], (IntPtr)1);
                i++;
            }
            else
            {
                PostMessage(hWnd, WM_CHAR, (IntPtr)ch, (IntPtr)1);
            }
            Thread.Sleep(22 + Random.Shared.Next(0, 18));
        }
        Thread.Sleep(90);
        return true;
    }

    /// <summary>带扫描码的回车（UE 的 Slate 需要 lParam 里有扫描码才认这个键）。</summary>
    private static void PostEnterWithScan(IntPtr hWnd)
    {
        const int scan = 0x1C;
        PostMessage(hWnd, WM_KEYDOWN, (IntPtr)VK_RETURN, (IntPtr)(1 | (scan << 16)));
        Thread.Sleep(30);
        PostMessage(hWnd, WM_KEYUP, (IntPtr)VK_RETURN, unchecked((IntPtr)(int)(1 | (scan << 16) | 0xC0000000)));
        Thread.Sleep(40);
    }

    /// <summary>回车字符（有些客户端把 CR 当提交）。</summary>
    private static void PostEnterChar(IntPtr hWnd)
    {
        PostMessage(hWnd, WM_CHAR, (IntPtr)VK_RETURN, (IntPtr)1);
        Thread.Sleep(40);
    }

    private static bool VerifySent(string text)
    {
        try
        {
            var lines = ChatSnapshot?.Invoke();
            if (lines is null || lines.Count == 0) return false;
            var probe = text.Length > 24 ? text[..24] : text;
            return lines.Any(l => l.Contains(probe, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    // 上次成功的发送方式，先试它（-1 = 未知）
    private static int _lastWorkingStrategy = -1;

    /// <summary>
    /// 把文字打进聊天框并尝试回车发送，最后用聊天记录确认有没有真的发出去。
    /// 客户端是否接受合成回车由实测决定：不接受时返回 false，由用户自己按回车。
    /// </summary>
    public static bool Send(string text)
    {
        if (!TypeIntoChat(text)) return false;
        var hWnd = FindGameWindow();
        if (hWnd == IntPtr.Zero) { LastError = "没找到游戏窗口"; return false; }
        if (ChatSnapshot is null) return true;   // 没有验证通道时不做多余动作

        var plans = new List<(string Name, Action Act)>
        {
            ("扫描码回车", () => PostEnterWithScan(hWnd)),
            ("回车字符", () => PostEnterChar(hWnd)),
        };
        if (_lastWorkingStrategy >= 0 && _lastWorkingStrategy < plans.Count)
        {
            var preferred = plans[_lastWorkingStrategy];
            plans.RemoveAt(_lastWorkingStrategy);
            plans.Insert(0, preferred);
        }

        for (var i = 0; i < plans.Count; i++)
        {
            plans[i].Act();
            Thread.Sleep(500);
            if (VerifySent(text))
            {
                _lastWorkingStrategy = i;
                MainWindow.Log("send ok via " + plans[i].Name);
                return true;
            }
            MainWindow.Log("send attempt failed: " + plans[i].Name);
        }

        LastError = "译文已在聊天框里，按回车发出";
        return false;
    }
    // ---------- 剪贴板 ----------

    private static bool SetClipboardText(string text)
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    EmptyClipboard();
                    var bytes = Encoding.Unicode.GetBytes(text + "\0");
                    var hMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes.Length);
                    if (hMem == IntPtr.Zero) return false;
                    var target = GlobalLock(hMem);
                    if (target == IntPtr.Zero)
                    {
                        GlobalFree(hMem);
                        return false;
                    }
                    Marshal.Copy(bytes, 0, target, bytes.Length);
                    GlobalUnlock(hMem);
                    if (SetClipboardData(CF_UNICODETEXT, hMem) == IntPtr.Zero)
                    {
                        GlobalFree(hMem);
                        return false;
                    }
                    return true;
                }
                finally
                {
                    CloseClipboard();
                }
            }
            Thread.Sleep(25);
        }
        return false;
    }

    private static string? GetClipboardText()
    {
        if (!IsClipboardFormatAvailable(CF_UNICODETEXT)) return null;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    var handle = GetClipboardData(CF_UNICODETEXT);
                    if (handle == IntPtr.Zero) return null;
                    var target = GlobalLock(handle);
                    if (target == IntPtr.Zero) return null;
                    try { return Marshal.PtrToStringUni(target); }
                    finally { GlobalUnlock(handle); }
                }
                finally
                {
                    CloseClipboard();
                }
            }
            Thread.Sleep(20);
        }
        return null;
    }

    public static bool CopyToClipboard(string text)
    {
        LastError = "";
        if (!SetClipboardText(text)) { LastError = "写剪贴板失败（剪贴板被别的程序占用）"; return false; }
        var check = GetClipboardText();
        if (check is null || !check.StartsWith(text, StringComparison.Ordinal))
        {
            LastError = "剪贴板内容没写进去";
            return false;
        }
        return true;
    }
}
