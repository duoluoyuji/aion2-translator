using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace AionTranslator.Core.Game;

/// <summary>
/// 诊断导出：把 AION2.exe 的模块内存镜像（RVA == 文件偏移）落盘，供离线重新定位聊天结构。
/// 客户端带壳，静态文件里查不到 vtable，只能从运行时镜像里找。
/// </summary>
public static class GameDiagnostics
{
    public const long ChunkBytes = 4 * 1024 * 1024;

    /// <summary>找出唯一的 AION2 客户端进程。</summary>
    public static bool TryFindGameProcess(out int pid, out string exePath, out string error)
    {
        pid = 0;
        exePath = "";
        error = "";
        var pids = new HashSet<int>();
        try
        {
            Win32.EnumWindows((hWnd, _) =>
            {
                var cls = new StringBuilder(256);
                Win32.GetClassName(hWnd, cls, cls.Capacity);
                if (cls.ToString() != "UnrealWindow") return true;
                Win32.GetWindowThreadProcessId(hWnd, out var p);
                if (p > 0) pids.Add(p);
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            error = "枚举窗口失败：" + ex.Message;
            return false;
        }
        if (pids.Count == 0) { error = "没有找到 AION2 游戏窗口"; return false; }
        if (pids.Count > 1) { error = "检测到多个游戏窗口，请只保留一个客户端"; return false; }
        pid = pids.First();

        using var query = Win32.OpenProcess(Win32.ProcessQueryLimitedInformation, false, pid);
        if (query.IsInvalid) { error = "打开游戏进程被拒绝（需要管理员权限）"; return false; }
        var sb = new StringBuilder(32768);
        var cap = sb.Capacity;
        if (!Win32.QueryFullProcessImageName(query, 0, sb, ref cap)) { error = "读取游戏路径失败"; return false; }
        exePath = sb.ToString();
        return true;
    }

    /// <summary>按完整路径找到目标进程主模块的基址与镜像大小（IntPtr.Zero 在部分机器上取不到值）。</summary>
    public static bool TryGetMainModule(SafeProcessHandle handle, string exePath, out ulong baseAddress, out ulong imageBytes, out string error)
    {
        baseAddress = 0;
        imageBytes = 0;
        error = "";
        try
        {
            var modules = new IntPtr[1024];
            if (Win32.EnumProcessModules(handle, modules, modules.Length * IntPtr.Size, out var needed))
            {
                var count = Math.Min(needed / IntPtr.Size, modules.Length);
                for (var i = 0; i < count; i++)
                {
                    var name = new StringBuilder(32768);
                    if (Win32.GetModuleFileNameEx(handle, modules[i], name, name.Capacity) == 0) continue;
                    if (!string.Equals(name.ToString(), exePath, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!Win32.GetModuleInformation(handle, modules[i], out var info, Marshal.SizeOf<Win32.ModuleInformation>())) continue;
                    baseAddress = (ulong)info.BaseOfDll.ToInt64();
                    imageBytes = (ulong)info.SizeOfImage;
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            error = "枚举模块失败：" + ex.Message;
            return false;
        }
        if (baseAddress == 0 || imageBytes == 0)
        {
            error = "没能定位到主模块基址（模块列表为空）";
            return false;
        }
        return true;
    }
    /// <summary>把模块镜像写成文件（大小 = SizeOfImage，未提交/不可读区间留零）。</summary>
    public static bool TryDumpModuleImage(string outPath, out string summary, out string error, int pidOverride = 0, string exeOverride = "")
    {
        summary = "";
        error = "";
        int pid;
        string exePath;
        if (pidOverride > 0)
        {
            pid = pidOverride;
            exePath = exeOverride;
        }
        else if (!TryFindGameProcess(out pid, out exePath, out error))
        {
            return false;
        }

        using var handle = Win32.OpenProcess(Win32.ProcessQueryInformation | Win32.ProcessVmRead, false, pid);
        if (handle.IsInvalid) { error = "读取游戏内存被拒绝（需要管理员权限）"; return false; }

        // 用模块列表找到 AION2.exe 本体（和主读取器同样的方法）；IntPtr.Zero 在部分机器上取不到基址
        var baseAddress = 0UL;
        var imageBytes = 0UL;
        try
        {
            var modules = new IntPtr[1024];
            if (Win32.EnumProcessModules(handle, modules, modules.Length * IntPtr.Size, out var needed))
            {
                var count = Math.Min(needed / IntPtr.Size, modules.Length);
                for (var i = 0; i < count; i++)
                {
                    var name = new StringBuilder(32768);
                    if (Win32.GetModuleFileNameEx(handle, modules[i], name, name.Capacity) == 0) continue;
                    if (!string.Equals(name.ToString(), exePath, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!Win32.GetModuleInformation(handle, modules[i], out var info, Marshal.SizeOf<Win32.ModuleInformation>())) continue;
                    baseAddress = (ulong)info.BaseOfDll.ToInt64();
                    imageBytes = (ulong)info.SizeOfImage;
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            error = "枚举模块失败：" + ex.Message;
            return false;
        }

        if (baseAddress == 0 || imageBytes == 0)
        {
            error = "没能定位到 AION2.exe 的模块基址（模块列表为空）";
            return false;
        }
        if (imageBytes == 0 || imageBytes > 2UL * 1024 * 1024 * 1024) { error = "模块大小异常：" + imageBytes; return false; }

        var chunk = new byte[ChunkBytes];
        long readOk = 0, zeroed = 0;
        const uint PAGE_NOACCESS = 0x01, PAGE_GUARD = 0x100, MEM_COMMIT = 0x1000;

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath)) ?? ".");
        using (var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        {
            fs.SetLength((long)imageBytes);
            var imageEnd = baseAddress + imageBytes;
            var addr = baseAddress;
            var mbiSize = Marshal.SizeOf<Win32.MemoryBasicInformation>();

            while (addr < imageEnd)
            {
                // 逐区域推进：模块内部由很多大小不一的区域组成，不能整块读
                if (Win32.VirtualQueryEx(handle, (IntPtr)(long)addr, out var mbi, mbiSize) == IntPtr.Zero)
                {
                    addr += 0x1000;
                    continue;
                }

                var regionBase = (ulong)mbi.BaseAddress.ToInt64();
                var regionSize = (ulong)mbi.RegionSize.ToInt64();
                if (regionSize == 0) break;
                var regionEnd = Math.Min(regionBase + regionSize, imageEnd);
                var start = Math.Max(addr, regionBase);
                if (start >= regionEnd) { addr = regionBase + regionSize; continue; }

                var readable = mbi.State == MEM_COMMIT &&
                               (mbi.Protect & (PAGE_NOACCESS | PAGE_GUARD)) == 0;

                var cur = start;
                while (cur < regionEnd)
                {
                    var want = (int)Math.Min((ulong)chunk.Length, regionEnd - cur);
                    if (readable &&
                        Win32.ReadProcessMemory(handle, (IntPtr)(long)cur, chunk, want, out var got) &&
                        got.ToInt64() == want)
                    {
                        fs.Position = (long)(cur - baseAddress);
                        fs.Write(chunk, 0, want);
                        readOk += want;
                    }
                    else
                    {
                        zeroed += want;   // 不可读区间：保持零
                    }
                    cur += (ulong)want;
                }

                addr = regionBase + regionSize;
            }
        }
        // 自检：模块镜像开头必须是 PE 头，否则说明读错了区域
        var firstTwo = new byte[2];
        using (var check = File.OpenRead(outPath))
        {
            if (check.Read(firstTwo, 0, 2) != 2 || firstTwo[0] != (byte)'M' || firstTwo[1] != (byte)'Z')
            {
                error = "导出的镜像不是有效的 PE 镜像（开头不是 MZ），请重试";
                return false;
            }
        }

        var sha = "";
        try
        {
            using var s = File.OpenRead(exePath);
            sha = Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
        }
        catch { }

        summary =
            "pid=" + pid + Environment.NewLine +
            "exe=" + exePath + Environment.NewLine +
            "sha256=" + sha + Environment.NewLine +
            "base=0x" + baseAddress.ToString("X") + Environment.NewLine +
            "imageBytes=" + imageBytes + Environment.NewLine +
            "read=" + readOk + " zeroed=" + zeroed + Environment.NewLine +
            "dumpedAt=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine;
        return true;
    }
}
