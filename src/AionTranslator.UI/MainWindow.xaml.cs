using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using AionTranslator.Core;

namespace AionTranslator.UI;

public sealed class ChatRow : INotifyPropertyChanged
{
    public long Id { get; init; }
    public string Speaker { get; init; } = "";
    public string Channel { get; init; } = "";
    public string Original { get; init; } = "";

    public string SpeakerTag => string.IsNullOrEmpty(Speaker) ? "" : "[" + Speaker + "] ";

    /// <summary>主界面列表里的 ID 颜色（白底上看不见纯白，所以"附近"用浅灰蓝）。</summary>
    public System.Windows.Media.Brush SpeakerBrush => ChannelColors.ForMain(Channel);

    /// <summary>悬浮窗里的 ID 颜色（深色底，"附近"就是纯白）。</summary>
    public System.Windows.Media.Brush OverlaySpeakerBrush => ChannelColors.ForOverlay(Channel);

    private string _translation = "";
    public string Translation
    {
        get => _translation;
        set
        {
            if (_translation == value) return;
            _translation = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Translation)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class MainWindow : Window
{
    /// <summary>手动翻译可选的目标语言（中文名字，模型能看懂）。</summary>
    public static readonly string[] ChatLanguages =
    {
        "简体中文", "英语", "日语", "韩语", "繁体中文", "俄语", "西班牙语", "法语",
        "德语", "葡萄牙语", "泰语", "越南语",
    };

    public static readonly string[] ManualLanguages =
    {
        "英语", "日语", "韩语", "繁体中文", "俄语", "西班牙语", "法语",
        "德语", "葡萄牙语", "泰语", "越南语",
    };

    internal static readonly ObservableCollection<ChatRow> SharedRows = new();

    private readonly ConcurrentQueue<IncomingChat> _queue = new();
    private IChatSource _source = new GameChatSource();
    private IChatSource CreateSource()
    {
        var source = new GameChatSource();
        source.SetStartupRecent(CurrentSettings.StartupRecentMessages);
        return source;
    }
    private readonly System.Windows.Threading.DispatcherTimer _flushTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };

    internal Settings CurrentSettings;
    internal OpenAiTranslator Translator;
    private OverlayWindow? _overlay;
    private bool _running;
    private int _nextId = 1;
    private string _channelFilter = "全部";
    private int _skippedSameLanguage;
    private string _settingsPath = "";
    private int _filteredOut;
    private int _noiseDropped;
    private DateTime _lastFilterLog = DateTime.MinValue;

    public MainWindow()
    {
        InitializeComponent();
        ChatList.ItemsSource = SharedRows;
        AionTranslator.Core.Game.Aion2GameReader.DiagLog = Log;
        AionTranslator.Core.Game.ChatProfileCache.Configure(System.IO.Path.Combine(AppPaths.DataDir, "discovered-layout.json"));
        ChatSender.ChatSnapshot = () => (_source as GameChatSource)?.RecentTexts() ?? (IReadOnlyList<string>)Array.Empty<string>();
        CurrentSettings = AppPaths.LoadSettings(out var settingsPath);
        _settingsPath = settingsPath;
        Translator = CreateTranslator();


        ChannelBox.SelectionChanged += (_, _) =>
        {
            _channelFilter = (ChannelBox.SelectedItem as ComboBoxItem)?.Content as string ?? "全部";
            (_source as GameChatSource)?.SetChannelName(_channelFilter);
            Log("channel filter = " + _channelFilter);
        };

        _source = CreateSource();
        _source.Message += OnMessage;
        _source.Note += note => Dispatcher.BeginInvoke(new Action(() => StatusText.Text = note));
        _flushTimer.Tick += async (_, _) => await FlushAsync();
        Closed += (_, _) =>
        {
            try
            {
                _overlay?.Close();
            }
            catch
            {
                // 关闭悬浮窗失败不影响退出
            }
        };
        SetupTray();
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized) HideToTray();
        };
        Closing += (_, e) =>
        {
            if (_exitRequested) return;
            e.Cancel = true;   // 点 X 收进托盘，真要退出走托盘菜单
            HideToTray();
        };
        Loaded += (_, _) =>
        {
            HotKey.Register(this, 1, HotKey.F9, ToggleOverlayManual);
            HotKey.Register(this, 2, HotKey.F10, () => Dispatcher.BeginInvoke(new Action(ShowFromTray)));
            StatusText.Text = "未开始";
            Log("started, providers=" + string.Join(",", CurrentSettings.Providers.Select(p => p.Name + ":" + p.Enabled)));
            HideToTray();          // 启动即缩到托盘：不在桌面/任务栏出现，F10 才唤得出来
            Log("startup: hidden to tray");
        };
    }

    internal static void Log(string message) => AppLog.Write(message);

    // ── 系统托盘 ─────────────────────────────────────────────
    private System.Windows.Forms.NotifyIcon? _tray;
    private bool _exitRequested;

    private void SetupTray()
    {
        if (_tray is not null) return;
        var menu = new System.Windows.Forms.ContextMenuStrip();
        var showItem = new System.Windows.Forms.ToolStripMenuItem("显示主界面");
        showItem.Click += (_, _) => Dispatcher.BeginInvoke(new Action(ShowFromTray));
        var settingsItem = new System.Windows.Forms.ToolStripMenuItem("设置");
        settingsItem.Click += (_, _) => Dispatcher.BeginInvoke(new Action(ShowSettings));
        var overlayItem = new System.Windows.Forms.ToolStripMenuItem("显示/隐藏悬浮窗手动条");
        overlayItem.Click += (_, _) => Dispatcher.BeginInvoke(new Action(ToggleOverlayManual));
        var exitItem = new System.Windows.Forms.ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => Dispatcher.BeginInvoke(new Action(ExitApp));
        menu.Items.Add(showItem);
        menu.Items.Add(settingsItem);
        menu.Items.Add(overlayItem);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        _tray = new System.Windows.Forms.NotifyIcon
        {
            Text = "AION2 实时翻译",
            Visible = true,
            ContextMenuStrip = menu,
        };
        try
        {
            var exePath = Environment.ProcessPath ?? "";
            _tray.Icon = string.IsNullOrEmpty(exePath)
                ? System.Drawing.SystemIcons.Application
                : System.Drawing.Icon.ExtractAssociatedIcon(exePath) ?? System.Drawing.SystemIcons.Application;
        }
        catch
        {
            _tray.Icon = System.Drawing.SystemIcons.Application;
        }
        _tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(new Action(ShowFromTray));
        Log("tray icon created");
    }

    /// <summary>
    /// 从托盘/热键恢复主界面。
    /// 关键：不抢激活、不进入任务栏 —— 否则游戏（全屏/无边框全屏）会被顶掉，
    /// 桌面和任务栏就会露出来。这里只把窗口压到最前，激活要等用户自己去点。
    /// </summary>
    internal void ShowFromTray()
    {
        if (!IsVisible)
        {
            ShowInTaskbar = false;
            Show();
        }
        WindowState = WindowState.Normal;
        ShowInTaskbar = false;   // 不占任务栏，避免把桌面/任务栏顶出来
        Topmost = true;
        Activate();              // 必须激活，否则窗口会被无边框全屏的游戏盖住（F10 看起来没反应）
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            SetWindowPos(handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }
        Log("main window shown (tray or hotkey, no activate)");
    }

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    private void HideToTray()
    {
        ShowInTaskbar = false;
        Hide();
    }

    private void ExitApp()
    {
        _exitRequested = true;
        try
        {
            _tray?.Dispose();
            _tray = null;
        }
        catch { }
        try { _overlay?.Close(); } catch { }
        Application.Current.Shutdown();
    }

    private OpenAiTranslator CreateTranslator()
    {
        var translator = new OpenAiTranslator(CurrentSettings);
        translator.Log += m => Dispatcher.BeginInvoke(() => StatusText.Text = m);
        return translator;
    }

    // 后台线程回调，不能碰 WPF 控件
    private static bool AlreadyInTarget(string text, string target)
    {
        if (target is not ("简体中文" or "繁體中文" or "中文")) return false;
        var cjk = 0;
        var letters = 0;
        foreach (var ch in text)
        {
            if (ch >= 0x4E00 && ch <= 0x9FFF) cjk++;
            else if (char.IsLetter(ch)) letters++;
        }
        var total = cjk + letters;
        return total > 0 && cjk * 100 / total >= 30;
    }

    /// <summary>NPC、系统、战斗记录这类刷屏频道：默认不翻译，否则会把翻译额度全吃掉。</summary>
    private static bool IsNoiseChannel(string channel) => channel switch
    {
        "NPC" or "伤害" or "恢复" or "状态" or "系统" or "战斗记录" or "获得物品"
            or "队伍系统" or "部队系统" or "军团系统" or "其他系统" or "社交动作" => true,
        _ => false,
    };

    private void OnMessage(IncomingChat chat)
    {
        // 中文（目标语言）不翻译，但照样进队列、照样显示，免得漏看信息
        var filter = _channelFilter;
        if (filter != "全部" && !string.Equals(filter, chat.Channel, StringComparison.Ordinal))
        {
            _filteredOut++;
            var now = DateTime.UtcNow;
            if ((now - _lastFilterLog).TotalSeconds > 5)
            {
                _lastFilterLog = now;
                Log("频道筛选「" + filter + "」已丢弃 " + _filteredOut + " 条其它频道消息（要看全部请把频道切到「全部」）");
            }
            return;
        }
        // NPC / 系统 / 战斗记录这类频道是刷屏噪音：默认不进翻译队列（设置里可打开）
        if (!CurrentSettings.TranslateNoiseChannels && IsNoiseChannel(chat.Channel))
        {
            _noiseDropped++;
            return;
        }
        _queue.Enqueue(chat);
    }

    private async Task FlushAsync()
    {
        if (_queue.IsEmpty) return;
        var batch = new List<IncomingChat>();
        while (batch.Count < Math.Max(1, CurrentSettings.BatchSize) && _queue.TryDequeue(out var item))
            batch.Add(item);
        if (batch.Count == 0) return;

        var rows = new List<ChatRow>();
        var lines = new List<ChatLine>();
        var rowOfLine = new Dictionary<int, int>();   // 送翻译的行号 → rows 下标
        foreach (var msg in batch)
        {
            // 已经是目标语言（中文）的：不占翻译额度，但原样显示在列表和悬浮窗里
            var sameLanguage = AlreadyInTarget(msg.Text, CurrentSettings.TargetLanguage);
            if (sameLanguage) _skippedSameLanguage++;
            var row = new ChatRow
            {
                Id = msg.Id,
                Speaker = msg.Speaker,
                Channel = msg.Channel,
                Original = msg.Text,
                Translation = sameLanguage ? msg.Text : "…",
            };
            SharedRows.Add(row);
            rows.Add(row);
            if (sameLanguage) continue;
            var lineId = _nextId++;
            lines.Add(new ChatLine(lineId, msg.Text, msg.Speaker));
            rowOfLine[lineId] = rows.Count - 1;
        }
        var keep = Math.Clamp(CurrentSettings.MaxHistory, 20, 5000);
        while (SharedRows.Count > keep) SharedRows.RemoveAt(0);
        ChatList.ScrollIntoView(SharedRows[^1]);

        if (lines.Count == 0) return;
        try
        {
            // 游戏聊天翻译语言由设置决定，默认简体中文
            var result = await Translator.TranslateAsync(lines, CancellationToken.None, CurrentSettings.TargetLanguage, CurrentSettings.Provider);
            for (int i = 0; i < lines.Count; i++)
            {
                var row = rows[rowOfLine[lines[i].Id]];
                if (result.TryGetValue(lines[i].Id, out var outcome))
                    row.Translation = outcome.Failed
                        ? "（翻译失败：" + (string.IsNullOrWhiteSpace(outcome.Error) ? "通道没返回结果" : outcome.Error) + "）"
                        : outcome.Text;
                else
                    row.Translation = "（无结果）" + row.Original;
            }
        }
        catch (Exception ex)
        {
            Log("translate exception: " + ex);
            foreach (var pair in rowOfLine) rows[pair.Value].Translation = "（出错）" + rows[pair.Value].Original;
        }
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_running)
            {
                _running = true;
                StartButton.Content = "停止翻译";
                try
                {
                    await _source.StartAsync(CancellationToken.None);
                }
                catch
                {
                    _running = false;
                    StartButton.Content = "开始翻译";
                    throw;
                }
                (_source as GameChatSource)?.SetChannelName(_channelFilter);
                _flushTimer.Start();
                StatusText.Text = _source.Status;
                Log("source started: " + _source.Status);
            }
            else
            {
                _running = false;
                StartButton.Content = "开始翻译";
                _flushTimer.Stop();
                await _source.StopAsync();
                StatusText.Text = "已停止";
                Log("source stopped");
            }
        }
        catch (Exception ex)
        {
            Log("start/stop exception: " + ex);
            StatusText.Text = "启动失败：" + ex.Message;
            if (ex.Message.Contains("结构核对", StringComparison.Ordinal) || ex.Message.Contains("没连上游戏", StringComparison.Ordinal))
            {
                try
                {
                    Clipboard.SetText(ex.Message);
                }
                catch
                {
                    // 复制失败不影响提示
                }
                MessageBox.Show(ex.Message + Environment.NewLine + Environment.NewLine + "（这段话已经复制到剪贴板，直接发给开发者即可）",
                    "读取游戏聊天失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            if (ex.Message.Contains("管理员", StringComparison.Ordinal))
            {
                var answer = MessageBox.Show(
                    "读取游戏内存需要管理员权限（游戏通常以更高权限运行）。" + Environment.NewLine +
                    "要用管理员身份重新打开本程序吗？",
                    "需要管理员权限", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer == MessageBoxResult.Yes) RestartElevated();
            }
        }
    }

    private static void RestartElevated()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            var info = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" };
            System.Diagnostics.Process.Start(info);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            Log("elevate failed: " + ex.Message);
            MessageBox.Show("以管理员身份重启失败：" + ex.Message, "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
    private void OverlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (_overlay is { IsVisible: true })
        {
            _overlay.Hide();
            return;
        }
        _overlay ??= new OverlayWindow(SharedRows, CurrentSettings, Translator);
        _overlay.Show();
        _overlay.ApplySettings();
    }

    internal void ToggleOverlayManual()
    {
        if (_overlay is null || !_overlay.IsVisible)
        {
            OverlayButton_Click(this, new RoutedEventArgs());
            _overlay?.ToggleManual(true);
        }
        else
        {
            _overlay.ToggleManual(null);
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => ShowSettings();

    private SettingsWindow? _settingsWindow;

    internal void ShowSettings()
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            return;
        }
        Log("settings opened (hotkey or button)");
        if (!IsVisible)
        {
            Show();
            WindowState = WindowState.Normal;
        }
        var win = new SettingsWindow(CurrentSettings) { Owner = this };
        _settingsWindow = win;
        var accepted = win.ShowDialog() == true;
        _settingsWindow = null;
        if (accepted)
        {
            var saved = AppPaths.SaveSettings(CurrentSettings, _settingsPath);
            if (saved is null)
            {
                MessageBox.Show(
                    "设置没能写入磁盘（配置文件可能被占用或目录没有写权限）。" + Environment.NewLine +
                    "这次的改动在本次运行里仍然有效，但重启后会恢复原样。",
                    "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                StatusText.Text = "设置未能保存";
            }
            else
            {
                _settingsPath = saved;
                StatusText.Text = "设置已保存";
            }
            Translator.Dispose();
            Translator = CreateTranslator();
            _overlay?.RefreshTranslator(Translator);
            _overlay?.ApplySettings();
            _overlay?.RefreshSendButton();
            _overlay?.RefreshQuickPhrases();
        }
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e) => SharedRows.Clear();
}
