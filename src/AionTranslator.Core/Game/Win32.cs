using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AionTranslator.Core.Game;

public static class Win32
{
    public const uint ProcessQueryInformation = 0x0400;
    public const uint ProcessQueryLimitedInformation = 0x1000;
    public const uint ProcessVmRead = 0x0010;
    public const uint ProcessVmWrite = 0x0020;
    public const uint ProcessVmOperation = 0x0008;

    public const uint MemCommit = 0x1000;
    public const uint MemImage = 0x1000000;
    public const uint PageNoAccess = 0x01;
    public const uint PageGuard = 0x100;

    [StructLayout(LayoutKind.Sequential)]
    public struct MemoryBasicInformation
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ModuleInformation
    {
        public IntPtr BaseOfDll;
        public uint SizeOfImage;
        public IntPtr EntryPoint;
    }

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder name, int maxCount);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll", EntryPoint = "K32GetModuleInformation", SetLastError = true)]
    public static extern bool GetModuleInformation(SafeProcessHandle process, IntPtr module, out ModuleInformation info, int size);

    [DllImport("kernel32.dll", EntryPoint = "K32EnumProcessModules", SetLastError = true)]
    public static extern bool EnumProcessModules(SafeProcessHandle process, IntPtr[] modules, int size, out int needed);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "K32GetModuleFileNameExW")]
    public static extern uint GetModuleFileNameEx(SafeProcessHandle process, IntPtr module, StringBuilder name, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadProcessMemory(SafeProcessHandle process, IntPtr address, byte[] buffer, int size, out IntPtr read);

    [DllImport("kernel32.dll")]
    public static extern IntPtr VirtualQueryEx(SafeProcessHandle process, IntPtr address, out MemoryBasicInformation info, IntPtr length);

    [DllImport("kernel32.dll")]
    public static extern bool IsWow64Process2(SafeProcessHandle process, out ushort processMachine, out ushort nativeMachine);

    [DllImport("kernel32.dll")]
    public static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);
}
