using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;

namespace AionTranslator.Core.Game;

/// <summary>只读取进程内存，带可读区间校验，读不到就返回 false。</summary>
internal sealed class ProcessMemory : IDisposable
{
    private readonly SafeProcessHandle _handle;

    public ProcessMemory(SafeProcessHandle handle) => _handle = handle;

    public bool Alive
    {
        get
        {
            try
            {
                return Win32.GetExitCodeProcess(_handle, out var code) && code == 259;
            }
            catch
            {
                return false;
            }
        }
    }

    private bool IsReadable(ulong address, int size)
    {
        if (address < 0x10000 || address > 0x7FFF_FFFF_FFFFUL) return false;
        if (size <= 0 || size > 4 * 1024 * 1024) return false;
        if (address > 0x7FFF_FFFF_FFFFUL - (ulong)size) return false;

        var queried = Win32.VirtualQueryEx(_handle, (IntPtr)(long)address, out var info, (IntPtr)System.Runtime.InteropServices.Marshal.SizeOf<Win32.MemoryBasicInformation>());
        if (queried == IntPtr.Zero) return false;
        var committed = (info.State & Win32.MemCommit) != 0;
        var noAccess = (info.Protect & Win32.PageNoAccess) != 0;
        var guard = (info.Protect & Win32.PageGuard) != 0;
        if (!committed || noAccess || guard) return false;

        var regionEnd = (ulong)info.BaseAddress.ToInt64() + (ulong)info.RegionSize.ToInt64();
        return address + (ulong)size <= regionEnd;
    }

    public bool Read(ulong address, Span<byte> buffer)
    {
        if (buffer.Length == 0) return false;
        if (!IsReadable(address, buffer.Length)) return false;
        var array = new byte[buffer.Length];
        if (Win32.ReadProcessMemory(_handle, (IntPtr)(long)address, array, array.Length, out var read) &&
            read.ToInt64() == array.Length)
        {
            array.CopyTo(buffer);
            return true;
        }
        return false;
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

    /// <summary>在模块范围内按字节模式（支持 ?? 通配）搜索，返回绝对地址。</summary>
    public ulong? FindPattern(ulong start, ulong end, byte[] pattern, byte[] mask, int maxBytes = 512 * 1024 * 1024)
    {
        const int chunkSize = 4 * 1024 * 1024;
        var buffer = new byte[chunkSize + pattern.Length];
        var address = start;
        long scanned = 0;
        while (address < end && scanned < maxBytes)
        {
            if (Win32.VirtualQueryEx(_handle, (IntPtr)(long)address, out var info, (IntPtr)System.Runtime.InteropServices.Marshal.SizeOf<Win32.MemoryBasicInformation>()) == IntPtr.Zero) break;
            var regionStart = (ulong)info.BaseAddress.ToInt64();
            var regionSize = (ulong)info.RegionSize.ToInt64();
            var regionEnd = regionStart + regionSize;
            var committed = (info.State & Win32.MemCommit) != 0;
            var readable = committed && (info.Protect & Win32.PageNoAccess) == 0 && (info.Protect & Win32.PageGuard) == 0;
            if (readable)
            {
                var cursor = Math.Max(address, regionStart);
                while (cursor < regionEnd && scanned < maxBytes)
                {
                    var size = (int)Math.Min((ulong)chunkSize, regionEnd - cursor);
                    if (size < pattern.Length) break;
                    var block = new byte[size + pattern.Length];
                    if (!Read(cursor, block.AsSpan(0, size))) break;
                    for (var i = 0; i + pattern.Length <= size; i++)
                    {
                        var hit = true;
                        for (var j = 0; j < pattern.Length; j++)
                        {
                            if (mask[j] == 0) continue;
                            if (block[i + j] != pattern[j]) { hit = false; break; }
                        }
                        if (hit) return cursor + (ulong)i;
                    }
                    cursor += (ulong)size;
                    scanned += size;
                }
            }
            address = regionEnd;
        }
        return null;
    }
    public void Dispose() => _handle.Dispose();
}
