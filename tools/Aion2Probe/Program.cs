using System.Buffers.Binary;
using System.Text;
using AionTranslator.Core.Game;
using Microsoft.Win32.SafeHandles;

// 聊天结构探测器：不假设偏移，用结构打分找出 owner→chat 与频道表偏移，并反推记录布局。
// 用法：管理员运行，游戏已进角色。输出直接贴回来。

Console.OutputEncoding = Encoding.UTF8;
// 所有报告同时写进 UTF-8 文件，避免控制台代码页把中文显示成问号
var logPath = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "probe-log.txt");
var log = new StringBuilder();
void W(string s = "") { Console.WriteLine(s); log.AppendLine(s); }
int ExitWithLog()
{
    try
    {
        File.WriteAllText(logPath, log.ToString(), new UTF8Encoding(false));
        Console.WriteLine("LOG FILE: " + logPath);
    }
    catch (Exception ex)
    {
        Console.WriteLine("write log failed: " + ex.Message);
    }
    return ExitWithLog();
}

if (!GameDiagnostics.TryFindGameProcess(out var pid, out var exePath, out var findError))
{
    W("找不到游戏进程：" + findError);
    return ExitWithLog();
}

W($"pid={pid}");
W($"exe={exePath}");

using var handle = Win32.OpenProcess(Win32.ProcessQueryInformation | Win32.ProcessVmRead, false, pid);
if (handle.IsInvalid)
{
    W("打开进程被拒绝：请以管理员身份运行");
    return ExitWithLog();
}

if (!GameDiagnostics.TryGetMainModule(handle, exePath, out var baseAddress, out var imageBytes, out var moduleError))
{
    W("读不到模块信息：" + moduleError);
    return ExitWithLog();
}
var memory = new ProcessMemory(handle);
W($"模块 base=0x{baseAddress:X} size={imageBytes}");

// 1) 用特征码定位 RootGetter，再由 rel32 反推 weak/count/chunks
var (pattern, mask) = ChatBuildProfileCatalog.CompilePattern(ChatBuildProfileCatalog.RootGetter);
var hit = memory.FindPattern(baseAddress, baseAddress + imageBytes, pattern, mask);
if (hit is null)
{
    W("模块里没找到 RootGetter 特征码");
    return ExitWithLog();
}
var rootRva = hit.Value - baseAddress;
var rg = new byte[89];
if (!memory.Read(hit.Value, rg))
{
    W("读不到 RootGetter 代码");
    return ExitWithLog();
}
long Rel(int off) => BinaryPrimitives.ReadInt32LittleEndian(rg.AsSpan(off));
var weakRva = (ulong)((long)rootRva + 19 + Rel(15));
var countRva = (ulong)((long)rootRva + 29 + Rel(25));
var chunksRva = (ulong)((long)rootRva + 52 + Rel(48));
W($"RootGetter=0x{rootRva:X} Weak=0x{weakRva:X} Count=0x{countRva:X} Chunks=0x{chunksRva:X}");

// 2) 复制主读取器的链路，但只到 owner
var weak = new byte[8];
if (!memory.Read(baseAddress + weakRva, weak))
{
    W("读不到全局指针");
    return ExitWithLog();
}
var index = BinaryPrimitives.ReadInt32LittleEndian(weak);
var serial = BinaryPrimitives.ReadInt32LittleEndian(weak.AsSpan(4));
if (!memory.ReadI32(baseAddress + countRva, out var count))
{
    W("读不到计数");
    return ExitWithLog();
}
W($"index={index} serial={serial} count={count}");

if (!memory.ReadU64(baseAddress + chunksRva, out var chunks) || !memory.IsPointer(chunks))
{
    W($"区块表不可用 chunks=0x{chunks:X}");
    return ExitWithLog();
}
if (!memory.ReadU64(chunks + (ulong)((long)(index >> 16) * 8), out var map) || !memory.IsPointer(map))
{
    W("区块 map 不可用");
    return ExitWithLog();
}
var slotAddress = map + (ulong)((long)(index & 0xFFFF) * 24);
var slot = new byte[24];
if (!memory.Read(slotAddress, slot))
{
    W("读不到区块槽");
    return ExitWithLog();
}
var owner = BinaryPrimitives.ReadUInt64LittleEndian(slot);
W($"slot: ptr=0x{owner:X} flags=0x{BinaryPrimitives.ReadInt32LittleEndian(slot.AsSpan(8)):X} serialInSlot={BinaryPrimitives.ReadInt32LittleEndian(slot.AsSpan(16))}");
if (!memory.IsPointer(owner))
{
    W("slot 里的 owner 不是有效指针");
    return ExitWithLog();
}

var ownerHeader = new byte[64];
memory.Read(owner, ownerHeader);
var ownerIndex = BinaryPrimitives.ReadInt32LittleEndian(ownerHeader.AsSpan(12));
W($"owner=0x{owner:X} owner+12={ownerIndex} (期望 {index})");

// 3) 扫描：owner + off → chat 指针；chat + tableOff → 频道表
var results = new List<(int OwnerOff, ulong Chat, int TableOff, string Note, int Score, string Sample)>();

for (var ownerOff = 1024; ownerOff <= 1408; ownerOff += 8)
{
    if (!memory.ReadU64(owner + (ulong)ownerOff, out var chatPtr)) continue;
    if (!memory.IsPointer(chatPtr) || (chatPtr & 7) != 0) continue;

    for (var tableOff = 32; tableOff <= 112; tableOff += 4)
    {
        var table = new byte[80];
        if (!memory.Read(chatPtr + (ulong)tableOff, table)) continue;

        var entriesPtr = BinaryPrimitives.ReadUInt64LittleEndian(table);
        var used = BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(8));
        var cap = BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(12));
        var entriesCount = BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(40));
        var entriesCap = BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(44));
        var removed = BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(52));

        if (used < 0 || used > 128 || cap < used || cap > 4096) continue;
        if (entriesCount != used || entriesCap < entriesCount || entriesCap > 4096) continue;
        if (removed < 0 || removed > used) continue;
        if (used > 0 && !memory.IsPointer(entriesPtr)) continue;

        var entries = new byte[Math.Max(1, used * 32)];
        if (used > 0 && !memory.Read(entriesPtr, entries)) continue;

        var validCodes = 0;
        var nonEmpty = 0;
        var codes = new List<int>();
        var firstArray = 0UL;
        var firstCode = 0;
        var firstCapacity = 0;
        for (var i = 0; i < used; i++)
        {
            var entry = entries.AsSpan(i * 32, 32);
            var code = entry[0];
            var address = BinaryPrimitives.ReadUInt64LittleEndian(entry.Slice(8));
            var recUsed = BinaryPrimitives.ReadInt32LittleEndian(entry.Slice(16));
            var recCap = BinaryPrimitives.ReadInt32LittleEndian(entry.Slice(20));
            if (code < 1 || code > 23) continue;
            if (recUsed < 0 || recUsed > recCap || recCap > 8192) continue;
            validCodes++;
            codes.Add(code);
            if (recCap > 0 && memory.IsPointer(address))
            {
                nonEmpty++;
                if (firstArray == 0 && recUsed > 0)
                {
                    firstArray = address;
                    firstCode = code;
                    firstCapacity = recCap;
                }
            }
        }

        if (validCodes < 3) continue;

        var score = nonEmpty * 10 + validCodes;
        var note = $"used={used} cap={cap} removed={removed} validCodes={validCodes} nonEmpty={nonEmpty} codes=[{string.Join(",", codes.Take(12))}]";

        var sample = "";
        if (firstArray != 0)
        {
            sample = TrySample(memory, firstArray, firstCode, firstCapacity, out var layoutNote) + " | " + layoutNote;
        }

        results.Add((ownerOff, chatPtr, tableOff, note, score, sample));
    }
}

W();
W("=== 候选（按分数排序，取前 12） ===");
foreach (var r in results.OrderByDescending(x => x.Score).Take(12))
{
    W($"score={r.Score,4} owner+{r.OwnerOff} chat=0x{r.Chat:X} table+{r.TableOff}");
    W($"       {r.Note}");
    if (r.Sample.Length > 0) W($"       sample: {r.Sample}");
}

if (results.Count == 0)
{
    W("没有找到任何结构匹配的候选：说明频道表结构本身变了，需要人工分析镜像。");
}

try
{
    File.WriteAllText(logPath, log.ToString(), new UTF8Encoding(false));
    Console.WriteLine();
    Console.WriteLine("LOG FILE: " + logPath);
}
catch (Exception ex)
{
    Console.WriteLine("write log failed: " + ex.Message);
}

return 0;

// 在一段记录数组里尝试常见的 stride / 字段偏移，验证能解出正文
static string TrySample(ProcessMemory memory, ulong arrayBase, int channelCode, int capacity, out string layoutNote)
{
    var strides = new[] { 336, 320, 352, 328, 344, 304, 288, 272 };
    var bodyOffsets = new[] { 16, 8, 24, 0, 32 };
    var senderOffsets = new[] { 40, 32, 48, 24 };
    var channelOffsets = new[] { 120, 112, 128, 104, 136 };

    foreach (var stride in strides)
    {
        var take = Math.Min(capacity, 3);
        var buf = new byte[take * stride];
        if (!memory.Read(arrayBase, buf)) continue;

        foreach (var bodyOff in bodyOffsets)
        {
            foreach (var chOff in channelOffsets)
            {
                var okRecords = 0;
                var text = "";
                for (var i = 0; i < take; i++)
                {
                    var rec = buf.AsSpan(i * stride, stride);
                    if (chOff + 1 > stride) continue;
                    if (rec[chOff] != channelCode) continue;
                    var len = BinaryPrimitives.ReadInt32LittleEndian(rec.Slice(bodyOff, 4));
                    if (len <= 4 || len > 800 || bodyOff + 4 + len > stride) continue;
                    try
                    {
                        var s = Encoding.Unicode.GetString(rec.Slice(bodyOff + 4, len));
                        if (s.Length == 0) continue;
                        if (s.Any(c => char.IsControl(c) && c != '\n' && c != '\t')) continue;
                        okRecords++;
                        if (text.Length == 0) text = s.Length > 40 ? s[..40] : s;
                    }
                    catch
                    {
                        // 非 UTF-16，跳过
                    }
                }

                if (okRecords > 0)
                {
                    layoutNote = $"stride={stride} body+{bodyOff} ch+{chOff} sender+? okRecords={okRecords}";
                    return $"\"{text}\"";
                }
            }
        }
    }
    layoutNote = "未能反推出记录布局";
    return "";
}
