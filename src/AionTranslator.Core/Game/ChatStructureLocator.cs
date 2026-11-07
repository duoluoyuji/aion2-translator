using System.Buffers.Binary;

namespace AionTranslator.Core.Game;

/// <summary>
/// 聊天结构定位：不依赖任何写死的偏移，靠“频道表结构”在 owner 附近搜索聊天对象。
/// 目的：换登录渠道、例行维护改了偏移时，也能自己找对位置。
/// </summary>
public static class ChatStructureLocator
{
    /// <summary>找到的聊天对象与它内部的频道表偏移。</summary>
    public sealed record Location(ulong Chat, int OwnerOffset, int TableOffset, int Score, string Note);

    private const int OwnerScanFrom = 512;
    private const int OwnerScanTo = 4096;
    private const int TableScanFrom = 24;
    private const int TableScanTo = 256;
    private const int MinScore = 40;

    /// <summary>在 owner 对象附近扫描“像频道表”的数据块，返回得分最高的候选。</summary>
    public static Location? Find(ProcessMemory memory, ulong owner, IReadOnlyList<int>? tableOffsetHints = null)
    {
        // 先试“已知/上次命中”的偏移（快，命中就直接返回，不扫全表）
        var quick = QuickProbe(memory, owner, 1208, 48);
        if (quick is not null) return quick;

        Location? best = null;
        var table = new byte[80];

        var tableOffsets = tableOffsetHints is { Count: > 0 }
            ? tableOffsetHints.ToList()
            : Enumerable.Range(TableScanFrom / 4, (TableScanTo - TableScanFrom) / 4 + 1).Select(x => x * 4).ToList();

        for (var ownerOffset = OwnerScanFrom; ownerOffset <= OwnerScanTo; ownerOffset += 8)
        {
            if (!memory.ReadU64(owner + (ulong)ownerOffset, out var chat)) continue;
            if (!memory.IsPointer(chat) || (chat & 7) != 0) continue;

            foreach (var tableOffset in tableOffsets)
            {
                if (!memory.Read(chat + (ulong)tableOffset, table)) continue;
                var score = ScoreTable(memory, table, out var note);
                if (score <= 0) continue;
                if (best is null || score > best.Score)
                    best = new Location(chat, ownerOffset, tableOffset, score, note);
            }
        }

        return best is not null && best.Score >= MinScore ? best : null;
    }

    /// <summary>直接检验一对已知偏移（owner+ownerOffset 取指针，chat+tableOffset 当频道表）。</summary>
    private static Location? QuickProbe(ProcessMemory memory, ulong owner, int ownerOffset, int tableOffset)
    {
        if (!memory.ReadU64(owner + (ulong)ownerOffset, out var chat)) return null;
        if (!memory.IsPointer(chat) || (chat & 7) != 0) return null;
        var table = new byte[80];
        if (!memory.Read(chat + (ulong)tableOffset, table)) return null;
        var score = ScoreTable(memory, table, out var note);
        if (score <= 0 || score < MinScore) return null;
        return new Location(chat, ownerOffset, tableOffset, score, note);
    }

    /// <summary>给一张 80 字节的“频道表”打分：结构越像聊天频道表分越高。</summary>
    public static int ScoreTable(ProcessMemory memory, ReadOnlySpan<byte> table, out string note)
    {
        note = "";
        var entriesPtr = BinaryPrimitives.ReadUInt64LittleEndian(table);
        var maskPtr = BinaryPrimitives.ReadUInt64LittleEndian(table.Slice(32));
        var used = BinaryPrimitives.ReadInt32LittleEndian(table.Slice(8));
        var capacity = BinaryPrimitives.ReadInt32LittleEndian(table.Slice(12));
        var entriesCount = BinaryPrimitives.ReadInt32LittleEndian(table.Slice(40));
        var entriesCapacity = BinaryPrimitives.ReadInt32LittleEndian(table.Slice(44));
        var removed = BinaryPrimitives.ReadInt32LittleEndian(table.Slice(52));

        if (used < 1 || used > 128) return 0;
        if (capacity < used || capacity > 4096) return 0;
        if (entriesCount != used || entriesCapacity < entriesCount || entriesCapacity > 4096) return 0;
        if (removed < 0 || removed > used) return 0;
        if (entriesPtr == 0 && used > 0) return 0;

        var maskBytes = (used + 7) / 8;
        var mask = new byte[Math.Max(1, maskBytes)];
        if (memory.IsPointer(maskPtr))
        {
            if (!memory.Read(maskPtr, mask)) return 0;
        }
        else
        {
            if (maskPtr != 0 || maskBytes > 16) return 0;
            table.Slice(16, maskBytes).CopyTo(mask);
        }

        var entries = new byte[used * 32];
        if (!memory.Read(entriesPtr, entries)) return 0;

        var validCodes = 0;
        var nonEmpty = 0;
        var withRecords = 0;
        for (var i = 0; i < used; i++)
        {
            if ((mask[i / 8] & (1 << (i % 8))) == 0) continue;
            var entry = entries.AsSpan(i * 32, 32);
            var code = entry[0];
            var address = BinaryPrimitives.ReadUInt64LittleEndian(entry.Slice(8));
            var recUsed = BinaryPrimitives.ReadInt32LittleEndian(entry.Slice(16));
            var recCapacity = BinaryPrimitives.ReadInt32LittleEndian(entry.Slice(20));

            if (code < 1 || code > 23) return 0;
            if (recUsed < 0 || recUsed > recCapacity || recCapacity > 8192) return 0;
            validCodes++;
            if (recCapacity > 0 && memory.IsPointer(address))
            {
                nonEmpty++;
                if (recUsed > 0) withRecords++;
            }
        }

        if (validCodes < 3) return 0;

        note = $"used={used} cap={capacity} validCodes={validCodes} nonEmpty={nonEmpty} withRecords={withRecords}";
        return validCodes * 2 + nonEmpty * 10 + withRecords * 8 + (used <= 32 ? 4 : 0);
    }
}
