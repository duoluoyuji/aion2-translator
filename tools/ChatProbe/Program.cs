using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

internal static class Program
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern uint SendInput(uint n, INPUT[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public U u; }
    [StructLayout(LayoutKind.Explicit)] private struct U { [FieldOffset(0)] public K ki; }
    [StructLayout(LayoutKind.Sequential)] private struct K { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr extra; }

    private const uint WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101;
    private const int VK_RETURN = 0x0D;
    private const uint KEYUP = 0x0002, UNICODE = 0x0004;

    private static IntPtr Find()
    {
        var pids = new HashSet<uint>();
        foreach (var p in Process.GetProcesses())
            if (p.ProcessName.Contains("AION", StringComparison.OrdinalIgnoreCase)) pids.Add((uint)p.Id);
        var best = IntPtr.Zero; var score = -1;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var pid);
            if (!pids.Contains(pid) || !IsWindowVisible(h)) return true;
            var sb = new StringBuilder(256); GetClassName(h, sb, 256);
            var s = sb.ToString().Contains("Unreal", StringComparison.OrdinalIgnoreCase) ? 10 : 1;
            if (s > score) { score = s; best = h; }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    private static void RealEnter()
    {
        var down = new INPUT { type = 1, u = new U { ki = new K { wVk = VK_RETURN } } };
        var up = new INPUT { type = 1, u = new U { ki = new K { wVk = VK_RETURN, dwFlags = KEYUP } } };
        SendInput(1, new[] { down }, Marshal.SizeOf<INPUT>());
        Thread.Sleep(45);
        SendInput(1, new[] { up }, Marshal.SizeOf<INPUT>());
    }

    private static void RealChar(char ch)
    {
        var down = new INPUT { type = 1, u = new U { ki = new K { wVk = 0, wScan = ch, dwFlags = UNICODE } } };
        var up = new INPUT { type = 1, u = new U { ki = new K { wVk = 0, wScan = ch, dwFlags = UNICODE | KEYUP } } };
        SendInput(2, new[] { down, up }, Marshal.SizeOf<INPUT>());
        Thread.Sleep(120);
    }

    private static void RealText(string text)
    {
        foreach (var ch in text)
        {
            var down = new INPUT { type = 1, u = new U { ki = new K { wVk = 0, wScan = ch, dwFlags = UNICODE } } };
            var up = new INPUT { type = 1, u = new U { ki = new K { wVk = 0, wScan = ch, dwFlags = UNICODE | KEYUP } } };
            SendInput(2, new[] { down, up }, Marshal.SizeOf<INPUT>());
            Thread.Sleep(15);
        }
    }

    private static int Main(string[] args)
    {
        var mode = args.Length > 0 ? args[0] : "post";
        var hwnd = Find();
        if (hwnd == IntPtr.Zero) { Console.WriteLine("没找到游戏窗口"); return 1; }
        Console.WriteLine($"游戏窗口 0x{hwnd.ToInt64():X}");
        Console.WriteLine("现在请切回游戏（保持聊天框关着）。我会一直等，直到游戏真的在前台再动手。");
        var waited = 0;
        while (GetForegroundWindow() != hwnd && waited < 120)
        {
            Thread.Sleep(500);
            waited++;
            if (waited % 6 == 0) Console.WriteLine($"  等待中… 当前前台 0x{GetForegroundWindow().ToInt64():X}");
        }
        Console.WriteLine($"执行前前台 0x{GetForegroundWindow().ToInt64():X}（游戏 0x{hwnd.ToInt64():X}）");
        if (GetForegroundWindow() != hwnd) Console.WriteLine("注意：游戏没在前台，这轮结果不可信。");
        Thread.Sleep(400);
        if (hwnd == IntPtr.Zero) { Console.WriteLine("没找到游戏窗口"); return 1; }
        Console.WriteLine($"游戏窗口 0x{hwnd.ToInt64():X}，当前前台 0x{GetForegroundWindow().ToInt64():X}");

        switch (mode)
        {
            case "post":
                Console.WriteLine("== 测试A：只投递回车（不切前台）==");
                PostMessage(hwnd, WM_KEYDOWN, VK_RETURN, 1);
                Thread.Sleep(30);
                PostMessage(hwnd, WM_KEYUP, VK_RETURN, unchecked((IntPtr)(int)0xC0000001));
                break;

            case "real":
                Console.WriteLine("== 测试B：切前台 + 真实回车 ==");
                var t = GetWindowThreadProcessId(hwnd, out _);
                var self = GetCurrentThreadId();
                var fg = GetForegroundWindow();
                var ft = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out _);
                if (ft != 0) AttachThreadInput(self, ft, true);
                if (t != 0) AttachThreadInput(self, t, true);
                BringWindowToTop(hwnd); SetForegroundWindow(hwnd);
                if (t != 0) AttachThreadInput(self, t, false);
                if (ft != 0) AttachThreadInput(self, ft, false);
                Thread.Sleep(200);
                Console.WriteLine($"切换后前台 0x{GetForegroundWindow().ToInt64():X}");
                RealEnter();
                break;

            case "realtext":
                Console.WriteLine("== 测试C：切前台 + 真实回车 + 真实逐字(Unicode) ==");
                var t2 = GetWindowThreadProcessId(hwnd, out _);
                SetForegroundWindow(hwnd);
                Thread.Sleep(200);
                RealEnter();
                Thread.Sleep(250);
                RealText(args.Length > 1 ? args[1] : "hello from probe");
                break;

            case "postchar":
                Console.WriteLine("== 测试F：只投递字符（WM_CHAR），看能不能把聊天框叫出来 ==");
                foreach (var pair in new[] { ("t", 't'), ("slash", '/'), ("y", 'y'), ("enter-char", '\r') })
                {
                    Console.WriteLine("  投递 " + pair.Item1);
                    PostMessage(hwnd, 0x0102, (IntPtr)pair.Item2, (IntPtr)1);
                    Thread.Sleep(1300);
                }
                Console.WriteLine("  看哪一次把框打开了。");
                break;

            case "control":
                Console.WriteLine("== 测试E：聊天框已经手动打开，只做真实逐字输入 ==");
                RealText(args.Length > 1 ? args[1] : "hello from probe");
                break;

            case "trykeys":
                Console.WriteLine("== 测试D：依次尝试 T / 回车 / 斜杠 是否能打开聊天框 ==");
                foreach (var name in new[] { "T(0x54)", "Enter", "Slash(0xBF)" })
                {
                    Console.WriteLine("  试 " + name);
                    if (name.StartsWith("T")) { RealChar('t'); }
                    else if (name.StartsWith("Enter")) RealEnter();
                    else RealChar('/');
                    Thread.Sleep(1200);
                }
                Console.WriteLine("  看哪一次把聊天框打开了（三次都试完，框可能被打开又被关上，注意看）。");
                break;

            case "typing-into-open-box":
                RealText(args.Length > 1 ? args[1] : "hello from probe");
                break;
        }

        Console.WriteLine("已发送。回游戏看聊天框有没有被打开/有没有字。");
        return 0;
    }
}
