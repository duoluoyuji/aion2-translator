using AionTranslator.Core.Game;

namespace AionTranslator.UI;

/// <summary>从游戏内存读取公屏/队伍聊天，按频道去重后逐条推送。</summary>
public sealed class GameChatSource : IChatSource, IDisposable
{
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly Queue<string> _seenOrder = new();
    private Aion2GameReader? _reader;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private string _status = "未连接";
    private string _lastNote = "";
    private bool _primed;
    /// <summary>启动时每个频道最多翻多少条历史（0=不翻历史）。</summary>
    private int _startupRecent = 0;

    public void SetStartupRecent(int count) => _startupRecent = Math.Max(0, count);
    private byte _channelCode;

    public event Action<IncomingChat>? Message;
    public event Action<string>? Note;
    public string Status => _status;

    /// <summary>最近抓到的聊天文本，用来判断自己发出去的话有没有真的出现在聊天里。</summary>
    public IReadOnlyList<string> RecentTexts()
    {
        try { return _reader?.Snapshot().Select(m => m.Text).ToList() ?? new List<string>(); }
        catch { return new List<string>(); }
    }

    /// <summary>界面上的频道名 → 游戏频道编号。</summary>
    public static byte ChannelCodeOf(string name) => name switch
    {
        "一般" => 1,
        "呐喊" => 2,
        "种族" => 3,
        "军团" => 4,
        "小队" => 5,
        "副本" => 6,
        "部队" => 7,
        "部队通知" => 8,
        "悄悄话" => 9,
        "活动" => 10,
        "全部" => 0,
        _ => 1,
    };

    public void SetChannelName(string channelName) => _channelCode = ChannelCodeOf(channelName);

    public async Task StartAsync(CancellationToken ct)
    {
        _status = "正在查找 AION2 客户端…";
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;

        var error = "";
        var reader = await Task.Run(() => Aion2GameReader.TryAttach(out error), token).ConfigureAwait(false);
        if (reader is null)
        {
            _status = "没连上游戏：" + error;
            throw new InvalidOperationException(error);
        }
        _reader = reader;
        _status = reader.Status;

        _loop = Task.Run(async () =>
        {
            long nextId = 1;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (_reader is not null)
                    {
                        ReadViaReader(nextId, ref nextId);
                    }
                }
                catch (Exception ex)
                {
                    _status = "读取游戏内存出错：" + ex.Message;
                    MainWindow.Log("read snapshot error: " + ex);
                }
                try
                {
                    await Task.Delay(400, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, token);
    }

    private void ReadViaReader(long nextId, ref long counter)
    {
        var messages = _reader!.Snapshot();
        PublishNote(_reader.LastSnapshotNote);
        if (!_primed)
        {
            _primed = true;
            // 首轮历史裁剪：默认 0 条 —— 只从“启动之后的新消息”开始翻译，避免开软件就灌几百条给 AI
            messages = _startupRecent <= 0
                ? new List<GameMessage>()
                : messages.GroupBy(m => m.ChannelCode)
                    .SelectMany(g => g.Skip(Math.Max(0, g.Count() - _startupRecent)))
                    .ToList();
        }
        foreach (var message in messages)
        {
            if (!Accept(message.ChannelCode + ":" + message.Identity)) continue;
            Message?.Invoke(new IncomingChat(counter++, message.Speaker, message.Text, message.ChannelName));
        }
    }

    private bool Accept(string key)
    {
        if (!_seen.Add(key)) return false;
        _seenOrder.Enqueue(key);
        while (_seenOrder.Count > 4096) _seen.Remove(_seenOrder.Dequeue());
        return true;
    }

    private void PublishNote(string note)
    {
        if (string.Equals(note, _lastNote, StringComparison.Ordinal)) return;
        _lastNote = note;
        MainWindow.Log("snapshot: " + note);
        try
        {
            Note?.Invoke(note);
        }
        catch
        {
            // 状态回调失败不影响读取
        }
    }

    public Task StopAsync()
    {
        _cts?.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // 停止时忽略等待异常
        }
        _loop = null;
        _reader?.Dispose();
        _reader = null;
        _status = "已停止";
        return Task.CompletedTask;
    }

    public void Dispose() => StopAsync();
}
