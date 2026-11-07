using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AionTranslator.Core.Game;

public sealed record GameMessage(string Identity, string Speaker, string Text, byte ChannelCode, string ChannelName);

/// <summary>从 AION2.exe 内存里读出聊天记录。</summary>
public sealed class Aion2GameReader : IDisposable
{
    private static readonly UnicodeEncoding Utf16 = new(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);

    private ChatStructureLocator.Location? _chatLocation;
    private DateTime _lastHandleRenew = DateTime.MinValue;
    private int _handleRenewMaskIndex;

    private readonly SafeProcessHandle _handle;
    private readonly ProcessMemory _memory;

    public int ProcessId { get; }
    public ulong ImageBase { get; }
    public ulong ImageBytes { get; }
    public ChatBuildProfile Profile { get; }
    public string Status { get; }

    private Aion2GameReader(SafeProcessHandle handle, int pid, ulong baseAddress, ulong imageBytes, ChatBuildProfile profile, string status)
    {
        _handle = handle;
        _memory = new ProcessMemory(handle);
        ProcessId = pid;
        ImageBase = baseAddress;
        ImageBytes = imageBytes;
        Profile = profile;
        Status = status;
    }

    /// <summary>诊断日志钩子，由 UI 层接上。</summary>
    public static Action<string>? DiagLog;
    public string LastSnapshotNote { get; private set; } = "";
    public bool Alive => _memory.Alive;

    public static string ChannelName(byte code) => code switch
    {
        1 => "一般",
        2 => "呐喊",
        3 => "种族",
        4 => "军团",
        5 => "小队",
        6 => "副本",
        7 => "部队",
        8 => "部队通知",
        9 => "悄悄话",
        10 => "活动",
        11 => "NPC",
        12 => "伤害",
        13 => "恢复",
        14 => "状态",
        15 => "系统",
        16 => "战斗记录",
        17 => "获得物品",
        18 => "队伍系统",
        19 => "部队系统",
        20 => "军团系统",
        21 => "其他系统",
        22 => "社交动作",
        23 => "指挥",
        _ => "未知频道",
    };

    /// <summary>找 AION2 客户端并完成版本与聊天结构核对。失败时通过 error 返回原因。</summary>
    public static Aion2GameReader? TryAttach(out string error)
    {
        error = "";
        var windowPids = new HashSet<int>();
        try
        {
            Win32.EnumWindows((hWnd, _) =>
            {
                var className = new StringBuilder(256);
                Win32.GetClassName(hWnd, className, className.Capacity);
                if (className.ToString() != "UnrealWindow") return true;
                Win32.GetWindowThreadProcessId(hWnd, out var pid);
                if (pid > 0) windowPids.Add(pid);
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            error = "枚举游戏窗口失败：" + ex.Message;
            return null;
        }

        if (windowPids.Count == 0)
        {
            error = "没有找到 AION2 游戏窗口，请先开游戏并登录。";
            return null;
        }
        if (windowPids.Count > 1)
        {
            error = "检测到多个游戏窗口，请只保留一个客户端。";
            return null;
        }

        var pid = windowPids.First();
        string? exePath = null;
        try
        {
            using var queryHandle = Win32.OpenProcess(Win32.ProcessQueryLimitedInformation, false, pid);
            if (queryHandle.IsInvalid)
            {
                error = "读取游戏路径被拒绝，请用管理员身份运行本程序。";
                return null;
            }
            var path = new StringBuilder(32768);
            var capacity = path.Capacity;
            if (!Win32.QueryFullProcessImageName(queryHandle, 0, path, ref capacity))
            {
                error = "读取游戏路径失败，请用管理员身份运行本程序。";
                return null;
            }
            exePath = path.ToString();
        }
        catch (Exception ex)
        {
            error = "读取游戏路径失败：" + ex.Message;
            return null;
        }

        if (!string.Equals(Path.GetFileName(exePath), "AION2.exe", StringComparison.OrdinalIgnoreCase))
        {
            error = "窗口不是 AION2 客户端（" + Path.GetFileName(exePath) + "）。";
            return null;
        }

        SafeProcessHandle handle;
        try
        {
            handle = Win32.OpenProcess(Win32.ProcessQueryInformation | Win32.ProcessVmRead, false, pid);
        }
        catch (Exception ex)
        {
            error = "打开游戏进程失败：" + ex.Message;
            return null;
        }
        if (handle.IsInvalid)
        {
            error = "读取游戏内存被拒绝，请用管理员身份运行本程序。";
            return null;
        }

        try
        {
            // 先按模块列表找到 AION2.exe 的真实装载基址，取不到再退回主模块信息
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
                        if (!Win32.GetModuleInformation(handle, modules[i], out var info, System.Runtime.InteropServices.Marshal.SizeOf<Win32.ModuleInformation>())) continue;
                        baseAddress = (ulong)info.BaseOfDll.ToInt64();
                        imageBytes = (ulong)info.SizeOfImage;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                DiagLog?.Invoke("枚举模块失败：" + ex.Message);
            }

            if (baseAddress == 0)
            {
                if (!Win32.GetModuleInformation(handle, IntPtr.Zero, out var module, System.Runtime.InteropServices.Marshal.SizeOf<Win32.ModuleInformation>()))
                {
                    handle.Dispose();
                    error = "读取游戏模块信息失败，请重试。";
                    return null;
                }
                baseAddress = (ulong)module.BaseOfDll.ToInt64();
                imageBytes = (ulong)module.SizeOfImage;
            }

            DiagLog?.Invoke("模块: base=0x" + baseAddress.ToString("X") + " size=" + imageBytes);

            // 版本号取自磁盘上的可执行文件（和原版一致），内存里读整块 400MB 会失败
            string sha;
            try
            {
                using var stream = File.OpenRead(exePath);
                sha = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            }
            catch (Exception ex)
            {
                handle.Dispose();
                error = "读取游戏文件失败：" + ex.Message;
                return null;
            }

            var memory = new ProcessMemory(handle);
            ChatBuildProfile? matched = null;
            var reason = "";
            foreach (var profile in ChatBuildProfiles.All)
            {
                if (!profile.ImageSha256.Equals(sha, StringComparison.OrdinalIgnoreCase)) continue;
                if (profile.RelaxedLayout)
                {
                    // 宽松布局：偏移来自实测，跳过 vtable/class 校验（这两个值本来就为 0）
                    matched = profile;
                    DiagLog?.Invoke("命中已实测的宽松布局：" + profile.Name);
                    break;
                }
                if (ValidateProfile(memory, baseAddress, imageBytes, profile, out reason))
                {
                    matched = profile;
                    break;
                }
                DiagLog("结构核对失败 " + profile.Name + "：" + reason);
            }
            if (matched is null)
            {
                // 本机缓存：这台客户端的布局以前成功定位过，直接复用，不再扫描代码段
                var cached = ChatProfileCache.Find(sha);
                if (cached is not null && cached.ImageBytes == imageBytes)
                {
                    var cacheProfile = ChatProfileCache.ToProfile(cached);
                    DiagLog?.Invoke("使用本机缓存布局：" + cacheProfile.Name + "（" + cached.SavedAt + "）");
                    return new Aion2GameReader(handle, pid, baseAddress, imageBytes, cacheProfile, "已连接 AION2 · 缓存布局");
                }

                var located = LocateByPattern(memory, baseAddress, imageBytes, sha, out var locateReason);
                if (located is null)
                {
                    handle.Dispose();
                    error = "当前游戏版本（" + sha[..12] + "…）未通过结构核对：" +
                            (reason.Length > 0 ? reason : "哈希不在适配表里") + "；自动定位也失败：" + locateReason;
                    return null;
                }
                matched = located;
                DiagLog?.Invoke("已按特征码自动定位布局：" + matched.Name);
                ChatProfileCache.Save(new ChatProfileCache.Entry
                {
                    Sha256 = sha,
                    Name = matched.Name,
                    ImageBytes = imageBytes,
                    RootGetterRva = matched.RootGetterRva,
                    WeakRva = matched.WeakRva,
                    CountRva = matched.CountRva,
                    ChunksRva = matched.ChunksRva,
                    OwnerOffset = 1208,
                    TableOffset = 48,
                });
            }

            var status = "已连接 AION2 · " + matched.Name;
            return new Aion2GameReader(handle, pid, baseAddress, imageBytes, matched, status);
        }
        catch (Exception ex)
        {
            handle.Dispose();
            error = "核对游戏版本失败：" + ex.Message;
            return null;
        }
    }

    /// <summary>按特征码在模块内搜索聊天系统入口，反推各段偏移（适配表对不上时使用）。</summary>
    private static ChatBuildProfile? LocateByPattern(ProcessMemory memory, ulong baseAddress, ulong imageBytes, string sha, out string reason)
    {
        reason = "";
        var baseProfile = ChatBuildProfiles.All.FirstOrDefault(p => p.ImageSha256.Equals(sha, StringComparison.OrdinalIgnoreCase))
                          ?? ChatBuildProfiles.Steam20261028;

        var (pattern, mask) = ChatBuildProfiles.Compile(ChatBuildProfiles.RootGetterPattern);

        // ① 先按已知 profile 的 RootGetter 位置直接读（最省、最稳）：命中就不用全模块扫描
        ulong? hit = null;
        foreach (var known in ChatBuildProfiles.All)
        {
            if (known.RootGetterRva == 0 || known.RootGetterRva + 89 >= imageBytes) continue;
            var probe = new byte[89];
            if (!memory.Read(baseAddress + known.RootGetterRva, probe)) continue;
            if (!ChatBuildProfiles.MatchPattern(probe, ChatBuildProfiles.RootGetterPattern)) continue;
            hit = baseAddress + known.RootGetterRva;
            DiagLog?.Invoke("按已知位置命中 RootGetter：" + known.Name + " RVA=0x" + known.RootGetterRva.ToString("X"));
            break;
        }

        // ② 再在已知位置附近 ±128KB 里找（换渠道/例行维护通常只是轻微挪动）
        if (hit is null)
        {
            foreach (var known in ChatBuildProfiles.All)
            {
                if (known.RootGetterRva == 0) continue;
                var from = (ulong)Math.Max(0, (long)known.RootGetterRva - 0x100000);
                var to = Math.Min(imageBytes, known.RootGetterRva + 0x100000);
                var near = memory.FindPattern(baseAddress + from, baseAddress + to, pattern, mask, 8 * 1024 * 1024);
                if (near is not null)
                {
                    hit = near;
                    DiagLog?.Invoke("已知位置邻域命中 RootGetter：RVA=0x" + (near.Value - baseAddress).ToString("X"));
                    break;
                }
            }
        }

        // ③ 最后才全模块扫描，再不行才全进程
        if (hit is null)
        {
            hit = memory.FindPattern(baseAddress, baseAddress + imageBytes, pattern, mask);
            if (hit is null)
            {
                DiagLog?.Invoke("模块范围没找到，改为全进程扫描");
                hit = memory.FindPattern(0x10000, 0x7FFF_FFFF_FFFFUL, pattern, mask, 1024 * 1024 * 1024);
            }
        }

        if (hit is null)
        {
            reason = "聊天系统入口特征码定位失败（已知位置、邻域、全模块、全进程都没命中）";
            return null;
        }

        var rootRva = hit.Value - baseAddress;
        var rootGetter = new byte[89];
        if (!memory.Read(hit.Value, rootGetter))
        {
            reason = "读不到特征码命中的代码段";
            return null;
        }
        if (!ChatBuildProfiles.MatchPattern(rootGetter, ChatBuildProfiles.RootGetterPattern))
        {
            reason = "特征码命中处复核失败";
            return null;
        }

        long Rel(int offset) => BinaryPrimitives.ReadInt32LittleEndian(rootGetter.AsSpan(offset));
        var weakRva = (ulong)((long)rootRva + 19 + Rel(15));
        var countRva = (ulong)((long)rootRva + 29 + Rel(25));
        var chunksRva = (ulong)((long)rootRva + 52 + Rel(48));
        if (countRva != chunksRva + 20)
        {
            reason = "反推出的 Count/Chunks 偏移关系不对（" + countRva + " / " + chunksRva + "）";
            return null;
        }
        if (weakRva >= imageBytes || countRva >= imageBytes || chunksRva >= imageBytes)
        {
            reason = "反推的偏移超出模块范围";
            return null;
        }

        DiagLog?.Invoke("自动定位：RootGetter=" + rootRva + " Weak=" + weakRva + " Count=" + countRva + " Chunks=" + chunksRva);
        return baseProfile with
        {
            RootGetterRva = rootRva,
            WeakRva = weakRva,
            CountRva = countRva,
            ChunksRva = chunksRva,
            VtableRva = 0,
            DataRva = 0,
            ClassRva = 0,
            PropertyVtableRva = 0,
            ChatNameRva = 0,
            RelaxedLayout = true,
        };
    }
    private static bool ValidateProfile(ProcessMemory memory, ulong baseAddress, ulong imageBytes, ChatBuildProfile profile, out string reason)
    {
        reason = "";
        if (imageBytes < 268435456 || imageBytes > 536870912)
        {
            reason = "模块大小异常 " + imageBytes;
            return false;
        }

        var rootGetter = new byte[89];
        if (!memory.Read(baseAddress + profile.RootGetterRva, rootGetter))
        {
            reason = "读不到 RootGetter 代码段（基址 0x" + baseAddress.ToString("X") + "）";
            return false;
        }
        if (!ChatBuildProfiles.MatchPattern(rootGetter, ChatBuildProfiles.RootGetterPattern))
        {
            reason = "RootGetter 特征码不符，实际开头 " + Convert.ToHexString(rootGetter.AsSpan(0, 12));
            return false;
        }

        long Rva(int offset) => BinaryPrimitives.ReadInt32LittleEndian(rootGetter.AsSpan(offset));
        if ((long)(profile.RootGetterRva + 7) + Rva(3) != (long)(profile.WeakRva + 4))
        {
            reason = "WeakRva 引用偏移不符";
            return false;
        }
        if ((long)(profile.RootGetterRva + 19) + Rva(15) != (long)profile.WeakRva)
        {
            reason = "WeakRva 引用偏移不符(2)";
            return false;
        }
        if ((long)(profile.RootGetterRva + 29) + Rva(25) != (long)profile.CountRva)
        {
            reason = "CountRva 引用偏移不符";
            return false;
        }
        if ((long)(profile.RootGetterRva + 52) + Rva(48) != (long)profile.ChunksRva)
        {
            reason = "ChunksRva 引用偏移不符";
            return false;
        }
        if (profile.CountRva != profile.ChunksRva + 20)
        {
            reason = "CountRva 与 ChunksRva 间距不符";
            return false;
        }

        var name = new byte[ChatBuildProfiles.ChatSystemName.Length];
        if (!memory.Read(baseAddress + profile.ChatNameRva, name))
        {
            reason = "读不到 ChatName 段";
            return false;
        }
        if (!name.AsSpan().SequenceEqual(ChatBuildProfiles.ChatSystemName))
        {
            reason = "ChatName 不是 UChatSystem，实际 " + Encoding.Unicode.GetString(name).TrimEnd('\0');
            return false;
        }

        if (!CheckPointerPattern(memory, baseAddress, imageBytes, profile.VtableRva, 3, ChatBuildProfiles.VtablePattern, out reason)) return false;
        if (!CheckPointerPattern(memory, baseAddress, imageBytes, profile.DataRva, 2, ChatBuildProfiles.DataPattern, out reason)) return false;
        if (!CheckPointerPattern(memory, baseAddress, imageBytes, profile.DataRva - 2584, 2, ChatBuildProfiles.DataTailPattern, out reason)) return false;
        if (!CheckPointerPattern(memory, baseAddress, imageBytes, profile.PropertyVtableRva, 6, ChatBuildProfiles.PropertyVtablePattern, out reason)) return false;
        return true;
    }
    private static bool CheckPointerPattern(ProcessMemory memory, ulong baseAddress, ulong imageBytes, ulong rva, int slot, string pattern, out string reason)
    {
        reason = "";
        Span<byte> pointer = stackalloc byte[8];
        if (!memory.Read(baseAddress + rva + (ulong)(slot * 8), pointer))
        {
            reason = "读不到指针槽 RVA=0x" + rva.ToString("X") + " 槽位 " + slot;
            return false;
        }
        var target = BinaryPrimitives.ReadUInt64LittleEndian(pointer);
        if (target < baseAddress || target >= baseAddress + imageBytes)
        {
            reason = "指针槽 RVA=0x" + rva.ToString("X") + " 槽位 " + slot + " 指向模块外 0x" + target.ToString("X");
            return false;
        }
        var length = pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        var buffer = new byte[length];
        if (!memory.Read(target, buffer))
        {
            reason = "读不到指针目标 0x" + target.ToString("X");
            return false;
        }
        if (!ChatBuildProfiles.MatchPattern(buffer, pattern))
        {
            reason = "RVA=0x" + rva.ToString("X") + " 槽位 " + slot + " 目标代码特征不符，实际 " + Convert.ToHexString(buffer.AsSpan(0, Math.Min(8, buffer.Length)));
            return false;
        }
        return true;
    }
    private sealed record ChannelInfo(byte Code, ulong Address, int Count, int Capacity);

    /// <summary>读一次聊天快照（每个频道最多最近 120 条）。</summary>
    /// <summary>两种记录布局：A（336 字节）与 B（288 字节）。哪套能解出数据就用哪套。</summary>
    internal readonly record struct RecordLayout(string Name, int RecordBytes, int BodyOffset, int SenderOffset, int ChannelOffset);

    private static readonly RecordLayout[] Layouts =
    {
        new("A", 336, 16, 40, 120),
        new("B", 288, 80, 24, 104),
    };

    public IReadOnlyList<GameMessage> Snapshot()
    {
        var snapshot = ReadChatHeaderWithRetry();
        if (snapshot is null)
        {
            LastSnapshotNote = "聊天结构读取失败（" + LastHeaderError + "）";
            return Array.Empty<GameMessage>();
        }

        List<GameMessage> best = new();
        var usedLayout = "-";
        foreach (var layout in Layouts)
        {
            var candidate = ReadChannels(snapshot, layout);
            if (candidate.Count > best.Count)
            {
                best = candidate;
                usedLayout = layout.Name;
            }
            if (best.Count > 0) break;
        }

        if (best.Count > 0)
        {
            var perChannel = best.GroupBy(m => m.ChannelName).Select(g => g.Key + ":" + g.Count());
            LastSnapshotNote = "读到 " + best.Count + " 条（布局 " + usedLayout + "，频道 " + string.Join(",", perChannel) + "）";
            return best;
        }

        var channelNote = string.Join(",", snapshot.Channels.Values.Select(c => c.Code + ":" + c.Count));
        LastSnapshotNote = "频道 " + snapshot.Channels.Count + " 个 [" + channelNote + "]，但记录一条都没解出来";

        var detected = DetectLayout(snapshot);
        if (detected is { } detectedLayout)
        {
            var detectedMessages = ReadChannels(snapshot, detectedLayout);
            if (detectedMessages.Count > 0)
            {
                LastSnapshotNote = "读到 " + detectedMessages.Count + " 条（按内存实际布局自动适配）";
                return detectedMessages;
            }
        }

        var scanned = ReadByScanning(snapshot);
        if (scanned.Count > 0)
        {
            LastSnapshotNote = "读到 " + scanned.Count + " 条（按记录内文字扫描解码）";
            return scanned;
        }

        DumpDiagnostics(snapshot);

        // 取一条样本出来看看结构，方便定位偏移
        var sample = snapshot.Channels.Values.Where(c => c.Count > 0).OrderByDescending(c => c.Count).FirstOrDefault();
        if (sample is not null)
        {
            var probe = new byte[Math.Min(64, sample.Count * 288)];
            var probeAddress = sample.Address + (ulong)(Math.Max(0, sample.Count - 1) * 288L);
            if (_memory.Read(probeAddress, probe))
                DiagLog?.Invoke("样本频道 " + sample.Code + " 条数 " + sample.Count + " 末条前64字节 " + Convert.ToHexString(probe));
            else
                DiagLog?.Invoke("样本频道 " + sample.Code + " 读不到记录内容（地址 0x" + sample.Address.ToString("X") + "）");
        }
        return Array.Empty<GameMessage>();
    }

    /// <summary>两套写死的布局都解不出来时，直接从内存里推断记录步长和字符串偏移。</summary>
    private RecordLayout? DetectLayout(ChatSnapshot snapshot)
    {
        var channel = snapshot.Channels.Values
            .Where(c => c.Count > 0)
            .OrderByDescending(c => c.Count)
            .FirstOrDefault();
        if (channel is null) return null;

        var buffer = new byte[8192];
        var readAddress = channel.Address + (ulong)(Math.Max(0, channel.Count - 6) * 288L);
        if (!_memory.Read(readAddress, buffer)) return null;

        // 1) 找出所有能解出文字的槽位
        var slots = new List<(int Offset, int Length, string Text)>();
        for (var offset = 0; offset + 24 <= buffer.Length; offset += 8)
        {
            var text = ReadString(buffer.AsSpan(offset, 24), 512);
            if (text is null || text.Length < 1) continue;
            slots.Add((offset, text.Length, text));
        }
        if (slots.Count < 4)
        {
            DiagLog?.Invoke("布局推断：能解出的文字槽太少（" + slots.Count + " 个）");
            return null;
        }

        // 2) 槽位之间的间隔众数 ≈ 单条记录的字节数
        var gapCount = new Dictionary<int, int>();
        foreach (var left in slots)
            foreach (var right in slots)
            {
                var gap = right.Offset - left.Offset;
                if (gap < 200 || gap > 400) continue;
                gapCount[gap] = gapCount.TryGetValue(gap, out var n) ? n + 1 : 1;
            }
        if (gapCount.Count == 0) return null;
        var stride = gapCount.OrderByDescending(kv => kv.Value).First().Key;

        // 3) 同一 stride 下，取出现最多的两个偏移：长的当正文，短的当发言者
        var offsetStats = new Dictionary<int, (int Count, double AvgLength)>();
        foreach (var slot in slots)
        {
            var mod = slot.Offset % stride;
            offsetStats[mod] = offsetStats.TryGetValue(mod, out var stat)
                ? (stat.Count + 1, (stat.AvgLength * stat.Count + slot.Length) / (stat.Count + 1))
                : (1, slot.Length);
        }
        var ranked = offsetStats.OrderByDescending(kv => kv.Value.Count).ToList();
        var bodyOffset = ranked.OrderByDescending(kv => kv.Value.AvgLength).First().Key;
        var senderOffset = ranked.OrderBy(kv => kv.Value.AvgLength).First().Key;

        // 4) 频道号偏移：整段里每条记录该位置都等于该频道号的字节
        var channelOffset = 0;
        var misaligned = (int)((readAddress - channel.Address) % (ulong)stride);
        for (var offset = 0; offset < stride; offset++)
        {
            var ok = true;
            for (var record = 0; record < 3; record++)
            {
                var index = offset + record * stride - misaligned;
                if (index < 0 || index >= buffer.Length) { ok = false; break; }
                if (buffer[index] != channel.Code) { ok = false; break; }
            }
            if (ok)
            {
                channelOffset = offset;
                break;
            }
        }

        DiagLog?.Invoke("布局推断：步长 " + stride + "，正文偏移 " + bodyOffset + "，发言者偏移 " + senderOffset + "，频道偏移 " + channelOffset);
        if (bodyOffset == senderOffset) return null;
        return new RecordLayout("推断", stride, bodyOffset, senderOffset, (int)channelOffset);
    }

    /// <summary>兜底：直接在记录里扫出可读字符串，按内容判断发言者和正文。</summary>
    private List<GameMessage> ReadByScanning(ChatSnapshot snapshot)
    {
        var messages = new List<GameMessage>();
        foreach (var channel in snapshot.Channels.Values.Where(c => c.Count > 0).OrderByDescending(c => c.Count).Take(4))
        {
            var take = Math.Min(channel.Count, 120);
            var block = new byte[take * 336];
            var start = channel.Address + (ulong)((channel.Count - take) * 336);
            if (!_memory.Read(start, block)) continue;

            for (var i = 0; i < take; i++)
            {
                var record = block.AsSpan(i * 336, 336);
                var slots = new List<(int Offset, string Text)>();
                for (var offset = 0; offset + 24 <= 328; offset += 8)
                {
                    var text = ReadStringRef(record.Slice(offset, 16), 1024)
                               ?? ReadString(record.Slice(offset, 24), 1024);
                    if (text is null || text.Length == 0) continue;
                    slots.Add((offset, text));
                }
                if (slots.Count < 2) continue;

                var identitySlot = slots.FirstOrDefault(s => s.Text.Length is >= 16 and <= 24 && s.Text.All(c => c is >= '0' and <= '9'));
                var identity = identitySlot.Text;
                if (string.IsNullOrEmpty(identity)) continue;

                var candidates = slots.Where(s => s.Text != identity).ToList();
                var body = candidates.Where(s => s.Text.Length >= 2 && !s.Text.Any(char.IsControl))
                    .OrderByDescending(s => s.Text.Length).FirstOrDefault();
                if (string.IsNullOrEmpty(body.Text)) continue;
                var sender = candidates.Where(s => s.Offset != body.Offset && s.Text.Length <= 32 && !s.Text.Any(char.IsControl))
                    .OrderBy(s => s.Text.Length).FirstOrDefault();
                if (string.IsNullOrEmpty(sender.Text)) sender = (0, ChannelName(channel.Code));

                var cleaned = StripTags(body.Text);
                if (string.IsNullOrWhiteSpace(cleaned)) continue;
                messages.Add(new GameMessage(channel.Code + ":" + identity, sender.Text, cleaned, channel.Code, ChannelName(channel.Code)));
            }
        }
        return messages;
    }
    private bool _dumped;

    private void DumpDiagnostics(ChatSnapshot snapshot)
    {
        if (_dumped) return;
        _dumped = true;

        DiagLog?.Invoke("诊断 base=0x" + ImageBase.ToString("X") + " imageBytes=" + ImageBytes + " 布局=" + Profile.Name
                        + (Profile.RelaxedLayout ? "(自动定位)" : "(表内偏移)"));
        DiagLog?.Invoke("诊断 WeakRva=" + Profile.WeakRva + " CountRva=" + Profile.CountRva + " ChunksRva=" + Profile.ChunksRva);
        DiagLog?.Invoke("诊断 owner=0x" + snapshot.Owner.ToString("X") + " chat=0x" + snapshot.Chat.ToString("X") + " serial=" + snapshot.Serial);

        var table = new byte[80];
        if (_memory.Read(snapshot.Chat + 48, table))
            DiagLog?.Invoke("诊断 频道表80字节 " + Convert.ToHexString(table));
        else
            DiagLog?.Invoke("诊断 频道表读不到");

        var tabs = new byte[64];
        if (_memory.Read(snapshot.Chat + 208, tabs))
            DiagLog?.Invoke("诊断 标签页64字节 " + Convert.ToHexString(tabs));

        foreach (var channel in snapshot.Channels.Values.OrderByDescending(c => c.Count).Take(6))
        {
            DiagLog?.Invoke("诊断 频道 " + channel.Code + " 条数=" + channel.Count + " 容量=" + channel.Capacity + " 地址=0x" + channel.Address.ToString("X"));
            if (channel.Count <= 0) continue;
            var full = new byte[336];
            var a336 = channel.Address + (ulong)((channel.Count - 1) * 336);
            if (_memory.Read(a336, full))
            {
                DiagLog?.Invoke("诊断  " + channel.Code + " 末条336 " + Convert.ToHexString(full));
                for (var offset = 0; offset + 24 <= 320; offset += 8)
                {
                    var text = ReadString(full.AsSpan(offset, 24), 1024);
                    if (text is null || text.Length == 0) continue;
                    var preview = text.Length > 24 ? text[..24] : text;
                    DiagLog?.Invoke("诊断  串@" + offset + " (" + text.Length + ") " + preview.Replace("\r", " ").Replace("\n", " "));
                }
            }
            var first = new byte[336];
            if (_memory.Read(channel.Address, first))
                DiagLog?.Invoke("诊断  " + channel.Code + " 首条336 " + Convert.ToHexString(first));
        }
    }
    private List<GameMessage> ReadChannels(ChatSnapshot snapshot, RecordLayout layout)
    {
        var messages = new List<GameMessage>();
        foreach (var channel in snapshot.Channels.Values)
        {
            if (channel.Count <= 0) continue;
            var take = Math.Min(channel.Count, Profile.MaximumRecordsPerChannel);
            var start = channel.Address + (ulong)((channel.Count - take) * layout.RecordBytes);
            var block = new byte[take * layout.RecordBytes];
            if (!_memory.Read(start, block)) continue;

            for (var i = 0; i < take; i++)
            {
                var span = block.AsSpan(i * layout.RecordBytes, layout.RecordBytes);
                if (span[layout.ChannelOffset] != channel.Code) continue;
                var message = Decode(span, channel.Code, layout);
                if (message is not null) messages.Add(message);
            }
        }
        return messages;
    }
    private sealed record ChatSnapshot(ulong Owner, ulong Chat, int Serial, Dictionary<byte, ChannelInfo> Channels);

    private string LastHeaderError = "";

    private ChatSnapshot? ReadChatHeaderWithRetry()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var snap = ReadChatHeader();
            if (snap is not null) return snap;
            if (attempt < 2) Thread.Sleep(40);
        }
        return null;
    }

    private ChatSnapshot? ReadChatHeader()
    {
        var baseAddress = ImageBase;

        var weak = new byte[8];
        var weakOk = false;
        for (var attempt = 0; attempt < 10 && !weakOk; attempt++)
        {
            weakOk = _memory.Read(baseAddress + Profile.WeakRva, weak);
            if (weakOk) break;

            var readErr = _memory.LastError;
            if (attempt == 0) DiagLog?.Invoke("读取全局指针失败：Win32 错误 " + readErr);

            // 反作弊可能回收句柄的读权限：错误 5 时重新打开进程再试
            if ((readErr == 5 || readErr == 6) && (DateTime.UtcNow - _lastHandleRenew).TotalMilliseconds > 500)
            {
                try
                {
                    // 轮换访问掩码：QUERY_INFORMATION 有时会踩到反作弊，换 LIMITED / 只读掩码反而能读
                    var masks = new[]
                    {
                        Win32.ProcessVmRead | Win32.ProcessQueryInformation,
                        Win32.ProcessVmRead | Win32.ProcessQueryLimitedInformation,
                        Win32.ProcessVmRead,
                    };
                    var accessMask = masks[_handleRenewMaskIndex % masks.Length];
                    _handleRenewMaskIndex++;
                    var fresh = Win32.OpenProcess(accessMask, false, ProcessId);
                    if (!fresh.IsInvalid)
                    {
                        _memory.ReplaceHandle(fresh);
                        _lastHandleRenew = DateTime.UtcNow;
                        DiagLog?.Invoke("已重新打开进程句柄后重试（原错误 " + readErr + "，掩码 0x" + accessMask.ToString("X") + "）");
                    }
                }
                catch
                {
                    // 重开失败就继续退避重试
                }
            }
            Thread.Sleep(40);
        }
        if (!weakOk) { LastHeaderError = "读不到全局指针（错误 " + _memory.LastError + "）"; return null; }
        var index = BinaryPrimitives.ReadInt32LittleEndian(weak);
        var serial = BinaryPrimitives.ReadInt32LittleEndian(weak.AsSpan(4));

        if (!_memory.ReadI32(baseAddress + Profile.CountRva, out var count)) { LastHeaderError = "读不到计数"; return null; }
        if (index < 0 || index >= count || count > 3_000_000 || serial <= 0) { LastHeaderError = "索引/序号异常 index=" + index + " count=" + count + " serial=" + serial; return null; }

        if (!_memory.ReadU64(baseAddress + Profile.ChunksRva, out var chunks)) { LastHeaderError = "读不到区块表"; return null; }
        if (!_memory.IsPointer(chunks)) { LastHeaderError = "区块表指针无效"; return null; }
        if (!_memory.ReadU64(chunks + (ulong)((long)(index >> 16) * 8), out var map)) { LastHeaderError = "读不到区块 map"; return null; }
        if (!_memory.IsPointer(map)) { LastHeaderError = "区块 map 指针无效"; return null; }

        var slotAddress = map + (ulong)((long)(index & 0xFFFF) * 24);
        var slot = new byte[24];
        if (!_memory.Read(slotAddress, slot)) { LastHeaderError = "读不到区块槽"; return null; }
        if (BinaryPrimitives.ReadInt32LittleEndian(slot.AsSpan(16)) != serial) { LastHeaderError = "区块槽序号不符"; return null; }
        if ((BinaryPrimitives.ReadInt32LittleEndian(slot.AsSpan(8)) & 0x30200000) != 0) return null;

        var owner = BinaryPrimitives.ReadUInt64LittleEndian(slot);
        if (!_memory.IsPointer(owner)) return null;

        var ownerHeader = new byte[40];
        if (!_memory.Read(owner, ownerHeader)) return null;
        if (BinaryPrimitives.ReadInt32LittleEndian(ownerHeader.AsSpan(12)) != index) return null;

        // 聊天对象的偏移不再写死：先用上次命中的偏移，失败再按“频道表结构”现场扫描
        ulong chat = 0;
        var tableOffset = 0;
        if (_chatLocation is not null)
        {
            chat = _chatLocation.Chat;
            tableOffset = _chatLocation.TableOffset;
        }
        else
        {
            var located = ChatStructureLocator.Find(_memory, owner);
            if (located is not null)
            {
                _chatLocation = located;
                chat = located.Chat;
                tableOffset = located.TableOffset;
                DiagLog?.Invoke("结构定位成功：owner+" + located.OwnerOffset + " chat=0x" + chat.ToString("X") +
                                " table+" + located.TableOffset + " (" + located.Note + ", score=" + located.Score + ")");
            }
            else if (_memory.ReadU64(owner + 1208, out var fallbackChat) && _memory.IsPointer(fallbackChat))
            {
                // 结构扫描失败时退回原偏移，至少保持旧版本可用
                chat = fallbackChat;
                tableOffset = 48;
            }
            else
            {
                LastHeaderError = "结构定位失败：owner 附近没有像聊天频道表的数据块";
                return null;
            }
        }
        if (!_memory.IsPointer(chat)) return null;

        var chatHeader = new byte[40];
        if (!_memory.Read(chat, chatHeader)) return null;
        if (!Profile.RelaxedLayout && Profile.VtableRva != 0 && Profile.ClassRva != 0)
        {
            if (BinaryPrimitives.ReadUInt64LittleEndian(chatHeader) != baseAddress + Profile.VtableRva) return null;
            if (!_memory.ReadU64(baseAddress + Profile.ClassRva, out var classPtr)) return null;
            if (BinaryPrimitives.ReadUInt64LittleEndian(chatHeader.AsSpan(16)) != classPtr) return null;
            if ((BinaryPrimitives.ReadInt32LittleEndian(chatHeader.AsSpan(8)) & 0x60000010) != 0) return null;
        }

        var table = new byte[80];
        if (!_memory.Read(chat + (ulong)tableOffset, table)) { LastHeaderError = "读不到频道表"; return null; }
        if (tableOffset != 48 && ChatStructureLocator.ScoreTable(_memory, table, out _) <= 0)
        {
            LastHeaderError = "频道表结构校验失败（偏移 " + tableOffset + "）";
            return null;
        }

        var entriesPtr = BinaryPrimitives.ReadUInt64LittleEndian(table);
        var maskPtr = BinaryPrimitives.ReadUInt64LittleEndian(table.AsSpan(32));
        var channelsUsed = BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(8));
        var channelsCapacity = BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(12));
        var entriesCount = BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(40));
        var entriesCapacity = BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(44));
        var removed = BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(52));

        if (channelsUsed < 0 || channelsUsed > 128 || channelsUsed > channelsCapacity || channelsCapacity > 4096) return null;
        if (entriesCount != channelsUsed || entriesCapacity < entriesCount || entriesCapacity > 4096) return null;
        if (removed < 0 || removed > channelsUsed) return null;
        if (channelsUsed > 0 && !_memory.IsPointer(entriesPtr)) return null;

        var maskBytes = (channelsUsed + 7) / 8;
        var mask = new byte[maskBytes];
        if (_memory.IsPointer(maskPtr))
        {
            if (maskBytes > 0 && !_memory.Read(maskPtr, mask)) return null;
        }
        else
        {
            if (maskPtr != 0 || maskBytes > 16) return null;
            table.AsSpan(16, maskBytes).CopyTo(mask);
        }

        var entries = new byte[channelsUsed * 32];
        if (entries.Length > 0 && !_memory.Read(entriesPtr, entries)) return null;

        var channels = new Dictionary<byte, ChannelInfo>();
        for (var i = 0; i < channelsUsed; i++)
        {
            if ((mask[i / 8] & (1 << (i % 8))) == 0) continue;
            var entry = entries.AsSpan(i * 32, 32);
            var code = entry[0];
            var address = BinaryPrimitives.ReadUInt64LittleEndian(entry.Slice(8));
            var used = BinaryPrimitives.ReadInt32LittleEndian(entry.Slice(16));
            var capacity = BinaryPrimitives.ReadInt32LittleEndian(entry.Slice(20));
            if (code < 1 || code > 23 || channels.ContainsKey(code)) return null;
            if (used < 0 || used > capacity || capacity > 8192) return null;
            if (capacity > 0 && !_memory.IsPointer(address)) return null;
            if (capacity == 0 && address != 0) return null;
            channels[code] = new ChannelInfo(code, address, used, capacity);
        }

        if (channels.Count != channelsUsed - removed) { LastHeaderError = "频道数不符（解析 " + channels.Count + "，期望 " + (channelsUsed - removed) + "）"; return null; }

        var tabs = new byte[16];
        if (!_memory.Read(chat + 208, tabs)) return null;
        var tabsPtr = BinaryPrimitives.ReadUInt64LittleEndian(tabs);
        var tabCount = BinaryPrimitives.ReadInt32LittleEndian(tabs.AsSpan(8));
        var tabCapacity = BinaryPrimitives.ReadInt32LittleEndian(tabs.AsSpan(12));
        if (tabCount < 0 || tabCount > tabCapacity || tabCapacity > 64) return null;
        if (tabCount > 0 && !_memory.IsPointer(tabsPtr)) return null;
        var tabBytes = new byte[tabCount];
        if (tabCount > 0 && !_memory.Read(tabsPtr, tabBytes)) return null;
        if (tabBytes.Any(b => b < 1 || b > 23)) return null;

        return new ChatSnapshot(owner, chat, serial, channels);
    }

    private GameMessage? Decode(ReadOnlySpan<byte> record, byte channelCode, RecordLayout layout)
    {
        var identity = ReadIdentity(record, layout);
        if (identity is null) return null;

        var isLocal = identity.StartsWith("local:", StringComparison.Ordinal);
        // 先按原工具的引用链读（记录里存的是 {指针, 指针-16}），再退回直接内联串
        var body = ReadStringRef(record.Slice(layout.BodyOffset, 16), 1024)
                   ?? ReadString(record.Slice(layout.BodyOffset, 24), 1024);
        var sender = ReadStringRef(record.Slice(layout.SenderOffset, 16), 64)
                     ?? ReadString(record.Slice(layout.SenderOffset, 24), 64, isLocal);
        if (isLocal && sender == "") sender = ChannelName(channelCode);

        if (body is null || string.IsNullOrWhiteSpace(sender)) return null;
        if (sender.Contains('\0') || body.Contains('\0')) return null;
        if (sender.Any(char.IsControl)) return null;

        var text = StripTags(body);
        if (string.IsNullOrWhiteSpace(text)) return null;
        return new GameMessage(identity, sender, text, channelCode, ChannelName(channelCode));
    }

    /// <summary>按原工具的引用链读文本：引用是 {指针, 指针-16}，再顺着对象里的内联串取文字。</summary>
    private string? ReadStringRef(ReadOnlySpan<byte> reference, int maxChars)
    {
        if (reference.Length < 16) return null;
        var target = BinaryPrimitives.ReadUInt64LittleEndian(reference);
        var back = BinaryPrimitives.ReadUInt64LittleEndian(reference.Slice(8));
        if (!_memory.IsPointer(target)) return null;

        var objectAddress = back == target - 16 && _memory.IsPointer(back) ? back : target;
        var span = new byte[80];
        if (!_memory.Read(objectAddress, span)) return null;

        var inline = ReadString(span.AsSpan(56, 16), maxChars, allowEmpty: true);
        if (!string.IsNullOrEmpty(inline)) return inline;

        var indirect = BinaryPrimitives.ReadUInt64LittleEndian(span.AsSpan(72, 8));
        if (_memory.IsPointer(indirect))
        {
            var inner = new byte[16];
            if (_memory.Read(indirect, inner))
            {
                var text = ReadString(inner, maxChars);
                if (text is not null) return text;
            }
        }

        return ReadString(span.AsSpan(32, 16), maxChars);
    }
    private string? ReadIdentity(ReadOnlySpan<byte> record, RecordLayout layout)
    {
        var text = ReadString(record[..24], 24);
        if (text is { Length: >= 16 and <= 24 } && text.All(c => c is >= '0' and <= '9')) return text;

        if (record.Length < 224) return null;
        if (record[layout.ChannelOffset] < 11 || record[layout.ChannelOffset] > 23) return null;
        if (BinaryPrimitives.ReadInt32LittleEndian(record.Slice(8)) != 0) return null;
        var sequence = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(160));
        var ticks = BinaryPrimitives.ReadInt64LittleEndian(record.Slice(216));
        if (sequence == 0 || ticks < 630822816000000000L || ticks > 662380416000000000L) return null;
        return "local:" + record[layout.ChannelOffset] + ":" + sequence + ":" + ticks;
    }

    /// <summary>读一段 UE 的 FString（指针 + 长度 + 容量，UTF-16）。</summary>
    private string? ReadString(ReadOnlySpan<byte> header, int maxChars, bool allowEmpty = false)
    {
        if (header.Length < 16) return null;
        var pointer = BinaryPrimitives.ReadUInt64LittleEndian(header);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(8));
        var capacity = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(12));

        if (allowEmpty && pointer == 0 && length == 0 && capacity == 0) return "";
        if (length < 1 || length > maxChars + 1 || capacity < length || capacity > 4096 || (pointer & 1) != 0) return null;

        var bytes = checked(length * 2);
        if (!_memory.IsPointer(pointer) && pointer != 0) return null;
        if (pointer == 0) return null;

        var buffer = new byte[bytes];
        if (!_memory.Read(pointer, buffer)) return null;
        if (buffer[^1] != 0 || buffer[^2] != 0) return null;
        try
        {
            return Utf16.GetString(buffer, 0, bytes - 2);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>去掉 &lt;a Color=Common&gt; 这类富文本标签，只留可读文字。</summary>
    public static string StripTags(string text)
    {
        var builder = new StringBuilder(text.Length);
        var depth = 0;
        var index = 0;
        string[] kept = { "Hero", "Rare", "Legend", "Unique", "Mythic", "Normal", "Common", "system_point" };
        while (index < text.Length)
        {
            if (text.AsSpan(index).StartsWith("<a ", StringComparison.Ordinal))
            {
                var end = text.IndexOf('>', index + 3);
                if (end >= 0 && end - index <= 512)
                {
                    depth++;
                    index = end + 1;
                    continue;
                }
            }
            if (depth > 0 && text.AsSpan(index).StartsWith("</>", StringComparison.Ordinal))
            {
                depth--;
                index += 3;
                continue;
            }
            if (depth > 0 && text[index] == '<')
            {
                var end = text.IndexOf('>', index + 1);
                if (end > index && end - index < 64)
                {
                    var tag = text.Substring(index + 1, end - index - 1);
                    if (tag == "/" || kept.Contains(tag))
                    {
                        index = end + 1;
                        continue;
                    }
                }
            }
            builder.Append(text[index++]);
        }
        return builder.ToString();
    }

    public void Dispose()
    {
        _memory.Dispose();
    }
}
