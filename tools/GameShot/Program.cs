using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

internal static class Program
{
    private delegate bool EnumProc(IntPtr h, IntPtr l);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr h, ref POINT p);

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

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

    private static int Main(string[] args)
    {
        var outPath = args.Length > 0 ? args[0] : "shot.png";
        IntPtr hwnd;
        if (args.Length > 1 && args[1].StartsWith("0x"))
            hwnd = (IntPtr)Convert.ToInt64(args[1], 16);
        else hwnd = Find();
        if (hwnd == IntPtr.Zero) { Console.WriteLine("no game window"); return 1; }
        GetClientRect(hwnd, out var rect);
        var w = rect.R - rect.L; var h = rect.B - rect.T;
        Console.WriteLine($"hwnd=0x{hwnd.ToInt64():X} client={w}x{h} foreground=0x{GetForegroundWindow().ToInt64():X}");
        if (w <= 0 || h <= 0) { Console.WriteLine("bad client size"); return 2; }

        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            var hdc = g.GetHdc();
            var ok = PrintWindow(hwnd, hdc, 2);   // PW_RENDERFULLCONTENT
            g.ReleaseHdc(hdc);
            Console.WriteLine("PrintWindow=" + ok);
        }
        bmp.Save(outPath, ImageFormat.Png);
        Console.WriteLine("saved " + outPath);
        return 0;
    }
}
