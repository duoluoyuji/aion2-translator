using System.Runtime.InteropServices;
using System.Text;

internal static class Program
{
    private delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

    private static int Main()
    {
        Console.WriteLine("foreground=0x" + GetForegroundWindow().ToInt64().ToString("X"));
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var pid);
            var cls = new StringBuilder(256); GetClassName(h, cls, 256);
            var txt = new StringBuilder(512); GetWindowText(h, txt, 512);
            var vis = IsWindowVisible(h) ? "V" : "-";
            if (cls.ToString().Contains("Unreal", StringComparison.OrdinalIgnoreCase) || txt.ToString().Contains("AION", StringComparison.OrdinalIgnoreCase))
                Console.WriteLine($"0x{h.ToInt64():X} pid={pid} {vis} class={cls} title={txt}");
            return true;
        }, IntPtr.Zero);
        return 0;
    }
}
