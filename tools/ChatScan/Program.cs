using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

internal static class Program
{
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint PROCESS_VM_READ = 0x0010;
    private const uint PROCESS_VM_WRITE = 0x0020;
    private const uint PROCESS_VM_OPERATION = 0x0008;
    private const uint MEM_COMMIT = 0x1000;
    private const uint PAGE_NOACCESS = 0x01;
    private const uint PAGE_GUARD = 0x100;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr read);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr written);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int VirtualQueryEx(IntPtr h, IntPtr addr, out MEMORY_BASIC_INFORMATION64 info, IntPtr len);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION64
    {
        public ulong BaseAddress;
        public ulong AllocationBase;
        public uint AllocationProtect;
        public uint __alignment1;
        public ulong RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
        public uint __alignment2;
    }

    private static readonly StringBuilder Report = new();
    private static int _pid;

    private static void Log(string line)
    {
        Console.WriteLine(line);
        Report.AppendLine(line);
    }

    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("用法: ChatScan <pid|0> <scan|ptrscan|dumpfile|overwrite|dumpimage|peek> <参数> [新文本|输出前缀] [输出文件]");
            return 1;
        }

        var pid = 0;
        int.TryParse(args[0], out pid);
        if (pid == 0)
        {
            try
            {
                var best = Process.GetProcesses()
                    .Where(p => p.ProcessName.IndexOf("AION", StringComparison.OrdinalIgnoreCase) >= 0)
                    .OrderByDescending(p => p.WorkingSet64)
                    .FirstOrDefault();
                if (best is not null) pid = best.Id;
            }
            catch { }
            if (pid == 0) { Console.WriteLine("没有找到 AION2 进程"); return 2; }
        }

        _pid = pid;
        var mode = args[1];
        var p1 = args.Length > 2 ? args[2] : "";
        var p2 = args.Length > 3 ? args[3] : "";
        var outFile = args.Length > 3 ? args[^1] : "";

        var handle = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_VM_OPERATION, false, pid);
        if (handle == IntPtr.Zero)
        {
            Console.WriteLine("OpenProcess 失败, error=" + Marshal.GetLastWin32Error() + "（需要管理员权限）");
            return 3;
        }
        Log("attached pid=" + pid);

        switch (mode)
        {
            case "scan": ScanForUtf16(handle, p1); break;
            case "ptrscan": PtrScan(handle, p1); break;
            case "dumpfile": DumpFromFile(handle, p1); break;
            case "overwrite": OverwriteSameLength(handle, p1, p2); break;
            case "peek": PeekFields(handle, p1); break;
            case "dumpimage": DumpImage(handle, p1); break;
            default: Log("未知模式 " + mode); break;
        }

        SaveReport(outFile);
        return 0;
    }

    private static void SaveReport(string outFile)
    {
        if (string.IsNullOrWhiteSpace(outFile)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
            File.WriteAllText(outFile, Report.ToString(), new UTF8Encoding(false));
            Console.WriteLine("结果已写入 " + outFile);
        }
        catch (Exception ex)
        {
            Console.WriteLine("写结果文件失败: " + ex.Message);
        }
    }

    private static bool Readable(uint protect)
    {
        if ((protect & PAGE_GUARD) != 0) return false;
        if ((protect & 0xFF) == PAGE_NOACCESS) return false;
        const uint any = 0x02 | 0x04 | 0x08 | 0x20 | 0x40 | 0x80;
        return (protect & any) != 0;
    }

    private static List<ulong> ParseHits(string file)
    {
        var list = new List<ulong>();
        if (!File.Exists(file)) return list;
        foreach (var line in File.ReadAllLines(file))
        {
            var idx = line.IndexOf("hit @", StringComparison.Ordinal);
            if (idx < 0) continue;
            var hex = line[(idx + 5)..].Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase);
            if (ulong.TryParse(hex, NumberStyles.HexNumber, null, out var a)) list.Add(a);
        }
        return list;
    }

    private static void WalkRegions(IntPtr handle, Func<ulong, int, bool> onChunk, int chunkSize = 8 * 1024 * 1024)
    {
        var buffer = new byte[chunkSize];
        var cursor = 0UL;
        const ulong max = 0x7FFFFFFEFFFFUL;
        while (cursor < max)
        {
            if (VirtualQueryEx(handle, (IntPtr)(long)cursor, out var info, (IntPtr)Marshal.SizeOf<MEMORY_BASIC_INFORMATION64>()) == 0) break;
            var baseAddr = info.BaseAddress;
            var size = info.RegionSize;
            if (size == 0) break;
            cursor = baseAddr + size;
            if (info.State != MEM_COMMIT || !Readable(info.Protect)) continue;

            var offset = 0UL;
            while (offset < size)
            {
                var toRead = (int)Math.Min((ulong)chunkSize, size - offset);
                if (!ReadProcessMemory(handle, (IntPtr)(long)(baseAddr + offset), buffer, (IntPtr)toRead, out var got)) break;
                var readLen = (int)got.ToInt64();
                if (readLen <= 0) break;
                if (!onChunk(baseAddr + offset, readLen)) return;
                offset += (ulong)readLen;
            }
        }
    }

    private static void ScanForUtf16(IntPtr handle, string needle)
    {
        if (string.IsNullOrEmpty(needle)) { Log("需要搜索文本"); return; }
        var pattern = Encoding.Unicode.GetBytes(needle);
        var hits = new List<ulong>();
        var sw = Stopwatch.StartNew();
        var buffer = new byte[8 * 1024 * 1024 + pattern.Length];

        WalkRegions(handle, (addr, len) =>
        {
            if (!ReadProcessMemory(handle, (IntPtr)(long)addr, buffer, (IntPtr)len, out var got)) return true;
            var readLen = (int)got.ToInt64();
            var span = buffer.AsSpan(0, readLen);
            var from = 0;
            while (true)
            {
                var idx = span.Slice(from).IndexOf(pattern);
                if (idx < 0) break;
                hits.Add(addr + (ulong)(from + idx));
                from += idx + pattern.Length;
                if (hits.Count > 400) return false;
            }
            return true;
        });

        Log($"扫描耗时 {sw.ElapsedMilliseconds} ms，命中 {hits.Count} 处:");
        foreach (var h in hits) Log("  hit @ 0x" + h.ToString("X"));
    }

    private static void PtrScan(IntPtr handle, string addrFile)
    {
        var targets = new HashSet<ulong>();
        foreach (var a in ParseHits(addrFile))
        {
            targets.Add(a);
            if (a > 16) targets.Add(a - 16);
        }
        if (targets.Count == 0) { Log("候选项为空"); return; }

        var found = new List<(ulong field, ulong value)>();
        var sw = Stopwatch.StartNew();
        var buffer = new byte[8 * 1024 * 1024];

        WalkRegions(handle, (addr, len) =>
        {
            if (!ReadProcessMemory(handle, (IntPtr)(long)addr, buffer, (IntPtr)len, out var got)) return true;
            var readLen = (int)got.ToInt64();
            var span = buffer.AsSpan(0, readLen);
            for (var i = 0; i + 8 <= span.Length; i += 8)
            {
                var value = BitConverter.ToUInt64(span.Slice(i, 8));
                if (targets.Contains(value))
                {
                    found.Add((addr + (ulong)i, value));
                    if (found.Count > 500) return false;
                }
            }
            return true;
        });

        Log($"反向指针扫描耗时 {sw.ElapsedMilliseconds} ms，找到 {found.Count} 个");
        var dumped = 0;
        foreach (var (field, value) in found.OrderBy(x => x.value))
        {
            Log($"  field @ 0x{field:X} -> 0x{value:X}");
            if (dumped++ < 40) DumpOne(handle, field, 96);
        }
    }

    private static void DumpFromFile(IntPtr handle, string hitFile)
    {
        var hits = ParseHits(hitFile);
        Log($"dump {hits.Count} 处");
        foreach (var h in hits) DumpOne(handle, h, 48);
    }

    private static void PeekFields(IntPtr handle, string hitFile)
    {
        var hits = ParseHits(hitFile);
        Log($"peek {hits.Count} 处");
        foreach (var h in hits)
        {
            var head = new byte[16];
            if (!ReadProcessMemory(handle, (IntPtr)(long)h, head, (IntPtr)16, out var got) || got.ToInt64() != 16)
            {
                Log($"0x{h:X}: 读失败");
                continue;
            }
            var ptr = BitConverter.ToUInt64(head, 0);
            var num = BitConverter.ToInt32(head, 8);
            var max = BitConverter.ToInt32(head, 12);
            var text = "";
            if (num > 1 && num < 4096)
            {
                var raw = new byte[Math.Min(num, 200) * 2];
                if (ReadProcessMemory(handle, (IntPtr)(long)ptr, raw, (IntPtr)raw.Length, out var g2) && g2.ToInt64() > 0)
                    text = Encoding.Unicode.GetString(raw, 0, (int)g2.ToInt64()).Replace("\0", "");
            }
            Log($"0x{h:X}: ptr=0x{ptr:X} num={num} max={max} text=\"{text}\"");
        }
    }

    private static void OverwriteSameLength(IntPtr handle, string hitFile, string newText)
    {
        var hits = ParseHits(hitFile);
        Log($"待改写 {hits.Count} 个字段，目标文本 \"{newText}\"");
        foreach (var field in hits)
        {
            var head = new byte[16];
            if (!ReadProcessMemory(handle, (IntPtr)(long)field, head, (IntPtr)16, out var got) || got.ToInt64() != 16)
            {
                Log($"0x{field:X}: 读头失败");
                continue;
            }
            var ptr = BitConverter.ToUInt64(head, 0);
            var num = BitConverter.ToInt32(head, 8);
            var max = BitConverter.ToInt32(head, 12);
            if (ptr < 0x10000 || num <= 1 || max < num)
            {
                Log($"0x{field:X}: 跳过（ptr=0x{ptr:X} num={num} max={max}）");
                continue;
            }
            var chars = num - 1;
            var payload = newText.Length > chars ? newText[..chars] : newText.PadRight(chars, ' ');
            var bytes = Encoding.Unicode.GetBytes(payload + "\0");
            if (!WriteProcessMemory(handle, (IntPtr)(long)ptr, bytes, (IntPtr)bytes.Length, out var wrote))
            {
                Log($"0x{field:X}: 写入失败 error={Marshal.GetLastWin32Error()}");
                continue;
            }
            Log($"0x{field:X}: 写入 {wrote.ToInt64()} 字节 @ 0x{ptr:X}（{chars} 字符）");
        }
    }

    private static void DumpImage(IntPtr handle, string outPath)
    {
        if (string.IsNullOrWhiteSpace(outPath)) { Log("需要输出路径"); return; }
        using var proc = Process.GetProcessById(_pid);
        var module = proc.MainModule!;
        var baseAddr = (ulong)module.BaseAddress.ToInt64();
        var size = (ulong)module.ModuleMemorySize;
        Log($"模块 base=0x{baseAddr:X} size={size / (1024 * 1024)} MB");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        const int chunk = 4 * 1024 * 1024;
        var buffer = new byte[chunk];
        var written = 0UL;
        var sw = Stopwatch.StartNew();
        using var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 22);
        while (written < size)
        {
            var toRead = (int)Math.Min((ulong)chunk, size - written);
            var n = 0;
            if (ReadProcessMemory(handle, (IntPtr)(long)(baseAddr + written), buffer, (IntPtr)toRead, out var got))
                n = (int)got.ToInt64();
            if (n <= 0) { Array.Clear(buffer, 0, toRead); n = toRead; }
            fs.Write(buffer, 0, n);
            written += (ulong)n;
        }
        Log($"dump 完成 {written / (1024 * 1024)} MB，耗时 {sw.ElapsedMilliseconds} ms -> {outPath}");
    }

    private static void DumpOne(IntPtr handle, ulong addr, int back)
    {
        var start = addr >= (ulong)back ? addr - (ulong)back : 0;
        var buf = new byte[256];
        if (!ReadProcessMemory(handle, (IntPtr)(long)start, buf, (IntPtr)buf.Length, out var got) || got.ToInt64() <= 0)
        {
            Log($"0x{addr:X}: 读不到");
            return;
        }
        var len = (int)got.ToInt64();
        Log($"0x{addr:X}: ==== dump ====");
        Log("  hex: " + Convert.ToHexString(buf.AsSpan(0, len)));
        var text = Encoding.Unicode.GetString(buf.AsSpan(0, len));
        Log("  utf16: " + new string(text.Select(c => char.IsControl(c) ? '.' : c).ToArray()));
    }
}
