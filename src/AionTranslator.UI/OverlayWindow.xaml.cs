using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AionTranslator.Core;

namespace AionTranslator.UI;

public partial class OverlayWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int newStyle);

    // 让窗口对录屏/截图软件不可见（Win10 2004+）
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;
    private const uint WDA_NONE = 0x00000000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);

    private readonly Settings _settings;
    private readonly ObservableCollection<ChatRow> _rows;
    private OpenAiTranslator _translator;
    private DispatcherTimer? _sendFeedbackTimer;
    private DispatcherTimer? _followTimer;
    private bool _autoFollow = true;

    public OverlayWindow(ObservableCollection<ChatRow> rows, Settings settings, OpenAiTranslator translator)
    {
        InitializeComponent();
        _settings = settings;
        _translator = translator;
        _rows = rows;
        OverlayList.ItemsSource = rows;

        rows.CollectionChanged += (_, _) =>
        {
            if (!_autoFollow) return;
            Dispatcher.BeginInvoke(new Action(ScrollToEnd), DispatcherPriority.Background);
        };
        // 译文是后到的，行高会变，定时补一次滚动到底
        _followTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _followTimer.Tick += (_, _) =>
        {
            if (_autoFollow) ScrollToEnd();
        };
        _followTimer.Start();
        // 鼠标穿透开着的时候，手动条默认不显示（显示了也点不到），按 F9 才调出来
        ManualStrip.Visibility = Visibility.Collapsed;
        foreach (var lang in MainWindow.ManualLanguages) LangBox.Items.Add(lang);
        LangBox.SelectedItem = _settings.ManualLanguage;
        LangBox.SelectionChanged += (_, _) =>
        {
            if (LangBox.SelectedItem is string lang) _settings.ManualLanguage = lang;
            RefreshSendButton();
        };
        RefreshSendButton();
        RefreshQuickPhrases();
        if (LangBox.SelectedItem is null) LangBox.SelectedIndex = 0;
        if (settings.OverlayImeCompat)
        {
            AllowsTransparency = false;
            WindowStyle = WindowStyle.SingleBorderWindow;
            Background = new SolidColorBrush(Color.FromRgb(0x14, 0x1B, 0x2B));
            Shell.Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x22, 0x33));
            Shell.CornerRadius = new CornerRadius(0);
            ManualStrip.Background = new SolidColorBrush(Color.FromRgb(0x24, 0x2E, 0x42));
            ManualInput.Background = new SolidColorBrush(Color.FromRgb(0x33, 0x3D, 0x52));
        }
        // 默认放屏幕左侧、垂直居中（不再压右上角，右上角容易挡住小地图/任务栏信息）
        var area = SystemParameters.WorkArea;
        Left = area.Left + 40;
        Top = area.Top + Math.Max(0, (area.Height - Height) / 2);
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
        // 悬浮窗平时不抢焦点，游戏保持前台，任务栏不会翻出来
        ShowActivated = false;
        SetWindowLong(hwnd, GWL_EXSTYLE,
            GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        ApplyCaptureAffinity();
    }

    public string SelectedLanguage => LangBox.SelectedItem as string ?? "英语";

    private void ScrollToEnd()
    {
        try
        {
            OverlayScroll.ScrollToEnd();
        }
        catch
        {
            // 滚动失败不影响显示
        }
    }

    private void OverlayScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeight <= 0) return;
        // 用户手动往回翻就停止自动跟随；滚到底部再恢复
        _autoFollow = OverlayScroll.VerticalOffset + OverlayScroll.ViewportHeight >= OverlayScroll.ExtentHeight - 4;
    }

    protected override void OnClosed(EventArgs e)
    {
        _followTimer?.Stop();
        base.OnClosed(e);
    }

    public void ApplySettings()
    {
        ApplyClickThrough(ManualStrip.Visibility != Visibility.Visible ? null : false);
        var fontSize = Math.Clamp(_settings.OverlayFontSize, 12, 30);
        OverlayList.FontSize = fontSize;
        if (!_settings.OverlayImeCompat)
        {
            var alpha = (byte)(Math.Clamp(_settings.OverlayOpacity, 20, 95) * 255 / 100);
            Shell.Background = new SolidColorBrush(Color.FromArgb(alpha, 0x14, 0x1B, 0x2B));
        }
        ApplyClickThrough(null);
        ApplyCaptureAffinity();
    }

    public void RefreshTranslator(OpenAiTranslator translator) => _translator = translator;

    /// <summary>null = 切换；true = 显示；false = 隐藏。</summary>
    public void ToggleManual(bool? visible)
    {
        var show = visible ?? ManualStrip.Visibility != Visibility.Visible;
        ManualStrip.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        // 手动翻译条打开时临时取消鼠标穿透，否则根本点不到输入框
        ApplyClickThrough(show ? false : null);
        SetNoActivate(!show);
        if (show)
        {
            try
            {
                Activate();
            }
            catch
            {
                // 激活失败不影响输入
            }
            Dispatcher.BeginInvoke(new Action(() =>
            {
                ManualInput.Focus();
                ManualInput.SelectAll();
            }), System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    /// <summary>不开手动条时禁止激活，避免把游戏顶下去。</summary>
    private void SetNoActivate(bool enabled)
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            var style = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, enabled ? style | WS_EX_NOACTIVATE : style & ~WS_EX_NOACTIVATE);
        }
        catch (Exception ex)
        {
            MainWindow.Log("no-activate apply failed: " + ex.Message);
        }
    }

    /// <summary>
    /// 悬浮窗必须能被录进去（N卡/OBS 要拍的是"翻译效果"），
    /// 所以永远不设置 WDA_EXCLUDEFROMCAPTURE —— 那个标记会让录制软件直接抓不到这一层。
    /// </summary>
    private void ApplyCaptureAffinity()
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            SetWindowDisplayAffinity(hwnd, WDA_NONE);
        }
        catch (Exception ex)
        {
            MainWindow.Log("display affinity exception: " + ex.Message);
        }
    }

    /// <summary>强制值或按设置值应用鼠标穿透。</summary>
    private void ApplyClickThrough(bool? force)
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            var enable = force ?? _settings.ClickThrough;
            var style = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, enable ? style | WS_EX_TRANSPARENT : style & ~WS_EX_TRANSPARENT);
        }
        catch (Exception ex)
        {
            MainWindow.Log("click-through apply failed: " + ex.Message);
        }
    }

    private void Shell_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && !_settings.ClickThrough) DragMove();
    }

    private async void ManualTranslate_Click(object sender, RoutedEventArgs e)
    {
        var text = ManualInput.Text.Trim();
        if (text.Length == 0) return;
        try
        {
            TranslateButton.IsEnabled = false;
            ManualOutput.Text = "";
            var result = await _translator.TranslateAsync(new[] { new ChatLine(1, text) }, CancellationToken.None, SelectedLanguage, _settings.Provider);
            ManualOutput.Text = result.TryGetValue(1, out var outcome)
            ? outcome.Failed ? "翻译失败：" + (string.IsNullOrWhiteSpace(outcome.Error) ? "通道没返回结果" : outcome.Error) : outcome.Text
            : "翻译失败：通道没返回结果";
        }
        catch (Exception ex)
        {
            ManualOutput.Text = "出错：" + ex.Message;
        }
        finally
        {
            TranslateButton.IsEnabled = true;
        }
    }

    private async void ManualSend_Click(object sender, RoutedEventArgs e)
    {
        var text = ManualOutput.Text.Trim();
        if (text.Length == 0 || text == "翻译失败") return;
        if (text.Length > 100) text = text[..100];

        SendButton.IsEnabled = false;
        try
        {
            if (!CanSendToGame)
            {
                // 默认：只复制，用户自己粘进游戏
                try
                {
                    if (!ChatSender.CopyToClipboard(text)) throw new InvalidOperationException(ChatSender.LastError);
                    ShowSendFeedback(true);
                }
                catch (Exception ex)
                {
                    ManualOutput.ToolTip = "复制失败：" + ex.Message;
                    ShowSendFeedback(false);
                }
                return;
            }

            // 一键发送：含中日韩文字就退回复制（那些字进游戏会变方框）
            var cleaned = ChatSender.NormalizeForGame(text);
            if (ChatSender.HasCjk(cleaned))
            {
                ChatSender.CopyToClipboard(text);
                ManualOutput.ToolTip = "已复制，到游戏聊天里粘贴发出";
                ShowSendFeedback(false);
                return;
            }

            ChatSender.CopyToClipboard(text);   // 同时放进剪贴板（不给用户提示）
            var ok = ChatSender.Send(cleaned);
            if (ok)
            {
                ShowSendFeedback(true);
            }
            else
            {
                // 发不出去就不要再闪：字已经在聊天框里，告诉他按回车即可
                SendButton.ToolTip = ChatSender.LastError.Length > 0
                    ? ChatSender.LastError
                    : "译文已在聊天框里，按回车发出";
            }
        }
        finally
        {
            SendButton.IsEnabled = true;
        }
    }

    /// <summary>按设置刷新手动条按钮文字：关闭一键发送时是「复制」，开启时是「回车」。</summary>
    /// <summary>按设置重建快捷用语按钮（设置里没有就不显示，不占位置）。</summary>
    internal void RefreshQuickPhrases()
    {
        QuickBar.Children.Clear();
        var phrases = _settings.QuickPhrases ?? new List<string>();
        foreach (var raw in phrases)
        {
            var phrase = (raw ?? "").Trim();
            if (phrase.Length == 0) continue;
            var btn = new Button
            {
                Content = phrase,
                Height = 24,
                Margin = new Thickness(0, 0, 4, 4),
                Padding = new Thickness(8, 0, 8, 0),
                FontSize = 12,
                Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                ToolTip = "填进输入框，同时复制到剪贴板",
            };
            btn.Click += (_, _) =>
            {
                ManualInput.Text = phrase;
                ChatSender.CopyToClipboard(phrase);
                ManualInput.CaretIndex = ManualInput.Text.Length;
            };
            QuickBar.Children.Add(btn);
        }
        QuickBar.Visibility = QuickBar.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    /// <summary>当前目标语言是不是英语（只有英语才走“发送”）。</summary>
    private bool IsEnglishTarget =>
        SelectedLanguage.StartsWith("英", StringComparison.Ordinal) ||
        SelectedLanguage.StartsWith("English", StringComparison.OrdinalIgnoreCase);

    /// <summary>能不能直接发进游戏：勾了一键发送、且目标是英语。</summary>
    private bool CanSendToGame => _settings.OneKeySend && IsEnglishTarget;

    internal void RefreshSendButton()
    {
        SendButton.Content = CanSendToGame ? "发送" : "复制";
        SendButton.ToolTip = CanSendToGame
            ? "先在游戏里按回车打开聊天框，点它把译文填进框里，再按回车发出"
            : _settings.OneKeySend
                ? "点一下复制，到游戏聊天里粘贴发出；切成英语就变成「发送」"
                : "把译文复制到剪贴板，自己粘进游戏聊天";
    }


    private void ShowSendFeedback(bool ok)
    {
        SendButton.Background = new SolidColorBrush(ok
            ? Color.FromArgb(0xB0, 0x2E, 0xA0, 0x6A)
            : Color.FromArgb(0xB0, 0xA0, 0x3E, 0x3E));
        if (!ok && ChatSender.LastError.Length > 0)
        {
            ManualOutput.ToolTip = ChatSender.LastError;
        }

        _sendFeedbackTimer?.Stop();
        _sendFeedbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1400) };
        _sendFeedbackTimer.Tick += (_, _) =>
        {
            _sendFeedbackTimer!.Stop();
            SendButton.Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        };
        _sendFeedbackTimer.Start();
    }
}