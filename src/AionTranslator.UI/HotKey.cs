using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AionTranslator.UI;

internal static class HotKey
{
    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public const uint F9 = 0x78;
    public const uint F10 = 0x79;

    public static void Register(Window window, int id, uint vkCode, Action action)
    {
        var helper = new HwndSourceHookHelper(window, id, vkCode, action);
        window.Closed += (_, _) => helper.Dispose();
    }

    private sealed class HwndSourceHookHelper : IDisposable
    {
        private readonly int _id;
        private readonly Action _action;
        private readonly HwndSource? _source;
        private readonly IntPtr _hwnd;

        public HwndSourceHookHelper(Window window, int id, uint vkCode, Action action)
        {
            _id = id;
            _action = action;
            _vk = vkCode;
            _hwnd = new WindowInteropHelper(window).EnsureHandle();
            _source = HwndSource.FromHwnd(_hwnd);
            _source?.AddHook(Hook);
            if (!RegisterHotKey(_hwnd, id, 0, _vk))
                MainWindow.Log("hotkey register failed: id=" + id + " vk=0x" + _vk.ToString("X") + " err=" + Marshal.GetLastWin32Error());
            else
                MainWindow.Log("hotkey registered: id=" + id + " vk=0x" + _vk.ToString("X"));
        }

        private readonly uint _vk;

        private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == 0x0312 && wParam.ToInt32() == _id)
            {
                _action();
                handled = true;
            }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            _source?.RemoveHook(Hook);
            UnregisterHotKey(_hwnd, _id);
        }
    }
}