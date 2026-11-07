using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;

namespace AionTranslator.Core.Game;

/// <summary>
/// 只读取进程内存。关键点：按内存区域逐段读取 —— 游戏模块由上百个大小不一的区域拼成，
/// “整段跨区域一次读完”会失败，导致明明能读的地方读不到（历史上多次因此误判“找不到特征码”）。
/// </summary>
public sealed class ProcessMemory : IDisposable
{
    private const uint PageNoAccess = 0x01;
    private const uint PageGuard = 0x100;
    private const uint MemCommit = 0x1000;

    private SafeProcessHandle _handle;

    /// <summary>最近一次读取失败的系统错误码（5=拒绝访问，常见于反作弊回收句柄权限）。</summary>
    public int LastError { get; private set; }

    /// <summary>换一个进程句柄（权限被回收后重新 OpenProcess 用）。</summary>
    public void ReplaceHandle(SafeProcessHandle handle) => _handle = handle;

    public ProcessMemory(SafeProcessHandle handle) => _handle = handle;

    public bool Alive
    {
        get
        {
            try { return Win32.GetExitCodeProcess(_handle, out var code) && code == 259; }
            catch { return false; }
        }
    }

    private static bool IsAddressValid(ulong address) => address >= 0x10000 && address <= 0x7FFF_FFFF_FFFFUL;

    private bool Query(ulong address, out Win32.MemoryBasicInformation info)
    {
        info = default;
        if (!IsAddressValid(address)) return false;
        var size = (IntPtr)System.Runtime.InteropServices.Marshal.SizeOf<Win32.MemoryBasicInformation>();
        return Win32.VirtualQueryEx(_handle, (IntPtr)(long)address, out info, size) != IntPtr.Zero;
    }

    private static bool IsReadableRegion(in Win32.MemoryBasicInformation info) =>
        (info.State & MemCommit) != 0 &&
        (info.Protect & PageNoAccess) == 0 &&
        (info.Protect & PageGuard) == 0;

    /// <summary>在单个区域内读一段（长度由调用方保证不超过区域剩余长度）。</summary>
    private bool ReadWithinRegion(ulong address, Span<byte> buffer)
    {
        if (buffer.Length == 0) return false;
        var array = new byte[buffer.Length];
        if (Win32.ReadProcessMemory(_handle, (IntPtr)(long)address, array, array.Length, out var read) &&
            read.ToInt64() == array.Length)
        {
            array.CopyTo(buffer);
            return true;
        }
        LastError = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
        return false;
    }

    /// <summary>直接读（不做区域检查）：有些页 VirtualQueryEx 报的状态和实际可读性不一致，先试再说。</summary>
    private bool TryDirectRead(ulong address, Span<byte> buffer)
    {
        if (!IsAddressValid(address) || buffer.Length == 0) return false;
        return ReadWithinRegion(address, buffer);
    }

    /// <summary>读任意长度：先直接读；失败再按区域逐段读；都失败才算失败（带重试）。</summary>
    public bool Read(ulong address, Span<byte> buffer)
    {
        if (buffer.Length == 0) return false;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (TryDirectRead(address, buffer)) return true;
            if (ReadByRegions(address, buffer)) return true;
            if (attempt < 2) Thread.Sleep(3);
        }
        return false;
    }

    private bool ReadByRegions(ulong address, Span<byte> buffer)
    {
        var offset = 0;
        var cursor = address;
        while (offset < buffer.Length)
        {
            if (!Query(cursor, out var info)) return false;
            if (!IsReadableRegion(info)) return false;
            var regionEnd = (ulong)info.BaseAddress.ToInt64() + (ulong)info.RegionSize.ToInt64();
            if (cursor >= regionEnd) return false;
            var want = (int)Math.Min((ulong)(buffer.Length - offset), regionEnd - cursor);
            if (!ReadWithinRegion(cursor, buffer.Slice(offset, want))) return false;
            offset += want;
            cursor += (ulong)want;
        }
        return true;
    }

    public bool Read(ulong address, byte[] buffer) => Read(address, buffer.AsSpan());

    public bool ReadU64(ulong address, out ulong value)
    {
        Span<byte> buffer = stackalloc byte[8];
        value = 0;
        if (!Read(address, buffer)) return false;
        value = BinaryPrimitives.ReadUInt64LittleEndian(buffer);
        return true;
    }

    public bool ReadI32(ulong address, out int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        value = 0;
        if (!Read(address, buffer)) return false;
        value = BinaryPrimitives.ReadInt32LittleEndian(buffer);
        return true;
    }

    public bool IsPointer(ulong value) => value >= 0x10000 && value < 0x7FFF_FFFF_FFFFUL && (value & 7) == 0;

    /// <summary>按字节模式（支持 ?? 通配）搜索，返回绝对地址。逐区域扫描，某段读失败只跳过该段。</summary>
    public ulong? FindPattern(ulong start, ulong end, byte[] pattern, byte[] mask, int maxBytes = 512 * 1024 * 1024)
    {
        if (pattern.Length == 0) return null;
        const int chunkSize = 2 * 1024 * 1024;
        var overlap = pattern.Length - 1;
        var buffer = new byte[chunkSize + overlap];
        long scanned = 0;
        var address = start;

        while (address < end && scanned < maxBytes)
        {
            if (!Query(address, out var info)) break;
            var regionStart = (ulong)info.BaseAddress.ToInt64();
            var regionSize = (ulong)info.RegionSize.ToInt64();
            if (regionSize == 0) break;
            var regionEnd = regionStart + regionSize;

            if (IsReadableRegion(info))
            {
                var cursor = Math.Max(address, regionStart);
                while (cursor < regionEnd && scanned < maxBytes)
                {
                    var size = (int)Math.Min((ulong)chunkSize, regionEnd - cursor);
                    if (size <= 0) break;
                    if (size < pattern.Length) break;
                    var span = buffer.AsSpan(0, size);
                    if (!TryDirectRead(cursor, span) && !ReadWithinRegion(cursor, span))
                    {
                        cursor += (ulong)size;   // 这一段读失败：跳过，继续后面，别整轮放弃
                        continue;
                    }
                    for (var i = 0; i + pattern.Length <= size; i++)
                    {
                        var hit = true;
                        for (var j = 0; j < pattern.Length; j++)
                        {
                            if (mask[j] == 0) continue;
                            if (buffer[i + j] != pattern[j]) { hit = false; break; }
                        }
                        if (hit) return cursor + (ulong)i;
                    }
                    scanned += size;
                    cursor += (ulong)size;
                }
            }

            address = regionEnd;
        }
        return null;
    }

    public void Dispose() => _handle.Dispose();
}
