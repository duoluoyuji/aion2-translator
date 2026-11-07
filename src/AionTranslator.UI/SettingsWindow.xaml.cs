using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using AionTranslator.Core;
using AionTranslator.Core.Game;

namespace AionTranslator.UI;

public partial class SettingsWindow : Window
{
    private const string InviteUrl = "https://cloud.siliconflow.cn/i/diwJ6XDk";
    private const string AuthorBiliUrl = "https://space.bilibili.com/3379443";
    private const string AuthorName = "@吃素的佩奇";

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int newStyle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    private IntPtr _previousWindow;


    private const string HelpContent =
        "【翻译通道】\n" +
        "聊天（公屏、队伍、部队…）和悬浮窗里你手打的那句，都走同一条通道，只在上面配一次。\n" +
        "默认 B站公共接口：免 Key，中翻外也通。它哪天不好用了，在下拉里换成硅基流动/DeepSeek 等，填上 Key 就接着用。\n" +
        "Key 填一行就够；换 Key 时把原来那行替换掉即可。\n\n" +
        "【提示词】\n" +
        "技能、副本、Boss、黑话的译名不对，可以改“提示词文件”里的词条，保存后立即生效。\n" +
        "不知道怎么写，可以把整段内容发给 AI，让它按你想要的叫法改，再粘回去。\n\n" +
        "【玩家 ID 颜色＝频道】\n" +
        "世界/种族频道 = 黄色；附近频道（一般、呐喊）= 白色；\n" +
        "组队类（小队、部队、副本、军团）= 蓝色；其它频道 = 灰蓝色。\n" +
        "中文消息不翻译、不花额度，但同样会显示在主界面和悬浮窗里。\n\n" +
        "【对话保留】\n" +
        "只保留最近若干条，超出的自动丢掉最早的。\n\n" +
        "【手动条按钮】\n" +
        "复制：点一下，译文进剪贴板，到游戏聊天里粘贴发出。\n" +
        "发送：目标语言是英语时出现。先在游戏里按回车打开聊天框，点「发送」，译文进框后按回车发出。";
    /// <summary>导出诊断包：模块镜像 + 环境信息 + 日志，压成一个 zip，方便玩家回传。</summary>
    private async void ExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        DiagButton.IsEnabled = false;
        DiagStatus.Text = "正在读取游戏内存…（约 400 MB，需要几十秒）";
        var work = Path.Combine(Path.GetTempPath(), "aion2-diag-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        try
        {
            var ok = await Task.Run(() =>
            {
                var dump = GameDiagnostics.TryDumpModuleImage(Path.Combine(work, "image.bin"), out var summary, out var err);
                if (dump) File.WriteAllText(Path.Combine(work, "env.txt"), summary, new System.Text.UTF8Encoding(false));
                else File.WriteAllText(Path.Combine(work, "env.txt"), "dump failed: " + err, new System.Text.UTF8Encoding(false));
                return dump;
            });

            var log = Path.Combine(AppPaths.DataDir, "app.log");
            if (File.Exists(log)) File.Copy(log, Path.Combine(work, "app.log"), true);
            File.WriteAllText(Path.Combine(work, "profiles.txt"),
                string.Join(Environment.NewLine, AionTranslator.Core.Game.ChatBuildProfileCatalog.All.Select(p =>
                    p.Name + "  sha256=" + p.ImageSha256 + "  image=" + p.ImageBytes)),
                new System.Text.UTF8Encoding(false));

            var zipPath = Path.Combine(AppPaths.DataDir, "诊断-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip");
            if (File.Exists(zipPath)) File.Delete(zipPath);
            System.IO.Compression.ZipFile.CreateFromDirectory(work, zipPath);
            DiagStatus.Text = ok ? "已导出：" + zipPath : "导出失败，见 env.txt";
            try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + zipPath + "\""); } catch { }
        }
        catch (Exception ex)
        {
            DiagStatus.Text = "导出失败：" + ex.Message;
            AppLog.Write("export diagnostics failed: " + ex);
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { }
            DiagButton.IsEnabled = true;
        }
    }
    public Settings Settings { get; private set; }

    /// <summary>
    /// 只是把设置页贴在游戏上层（Topmost），行为跟普通窗口一样：
    /// 能获得焦点、下拉框能弹、按钮能点。
    /// 千万不要在这里加 WS_EX_NOACTIVATE / ShowActivated=false —— 那样窗口永远拿不到焦点，
    /// WPF 的下拉框弹不出来、按钮点击也会失效（之前那版就是栽在这）。
    /// </summary>
    private void ConfigureOverlayBehavior()
    {
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        ShowActivated = true;
        Topmost = true;
        _previousWindow = GetForegroundWindow();

        // 关掉设置页时把焦点还给原来的窗口（通常是游戏），省得鼠标还压在桌面
        Closed += (_, _) =>
        {
            if (_previousWindow == IntPtr.Zero) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try { ForceForeground(_previousWindow); } catch { }
            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        };
    }


    public SettingsWindow(Settings settings)
    {
        InitializeComponent();
        Settings = settings;
        HelpText.Text = HelpContent;
        ConfigureOverlayBehavior();

        try
        {
            foreach (var lang in MainWindow.ChatLanguages) LanguageBox.Items.Add(lang);
            LanguageBox.SelectedItem = Settings.TargetLanguage;
            if (LanguageBox.SelectedItem is null) LanguageBox.SelectedIndex = 0;

            foreach (var provider in Settings.Providers.OrderBy(p => p.Priority))
                ProviderBox.Items.Add(provider.Name);
            ProviderBox.SelectedItem = Settings.Provider;
            if (ProviderBox.SelectedItem is null) ProviderBox.SelectedIndex = 0;

            LoadSlot(ProviderBox, BaseUrlBox, ModelBox, KeyBox);


            ClickThroughBox.IsChecked = Settings.ClickThrough;
            OpacitySlider.Value = Settings.OverlayOpacity;
            OpacityText.Text = Settings.OverlayOpacity + "%";
            OpacitySlider.ValueChanged += (_, e) => OpacityText.Text = (int)e.NewValue + "%";
            FontSlider.Value = Settings.OverlayFontSize;
            FontText.Text = Settings.OverlayFontSize + " 号";
            FontSlider.ValueChanged += (_, e) => FontText.Text = (int)e.NewValue + " 号";
            ImeCompatBox.IsChecked = Settings.OverlayImeCompat;
            OneKeySendBox.IsChecked = Settings.OneKeySend;
        StartupRecentBox.Text = Settings.StartupRecentMessages.ToString();
        NoiseChannelsBox.IsChecked = Settings.TranslateNoiseChannels;
        QuickPhrasesBox.Text = string.Join(Environment.NewLine, Settings.QuickPhrases ?? new List<string>());
            HistoryBox.Text = Settings.MaxHistory.ToString();
            PromptPathText.Text = EffectivePromptPath();
        }
        catch (Exception ex)
        {
            AppLog.Write("设置窗口初始化异常: " + ex);
            MessageBox.Show("设置窗口初始化出错：" + ex.Message, "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private Provider? Find(string? name) =>
        Settings.Providers.FirstOrDefault(p => p.Name.Equals(name, StringComparison.Ordinal));

    private void LoadSlot(System.Windows.Controls.ComboBox box, System.Windows.Controls.TextBox url, System.Windows.Controls.TextBox model, System.Windows.Controls.TextBox key)
    {
        if (Find(box.SelectedItem as string) is not { } provider) return;
        url.Text = provider.BaseUrl ?? "";
        model.Text = provider.Model ?? "";
        key.Text = provider.Keys is { Count: > 0 } keys ? keys[0] : "";
    }

    private void ProviderBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        try
        {
            if (!IsLoaded) return;
            LoadSlot(ProviderBox, BaseUrlBox, ModelBox, KeyBox);
        }
        catch (Exception ex)
        {
            AppLog.Write("切换翻译通道异常: " + ex);
        }
    }

    private string SafePromptPath()
    {
        try
        {
            return Settings.SystemPromptPath;
        }
        catch
        {
            return Path.Combine(AppPaths.DataDir, "prompt", "system-prompt.txt");
        }
    }

    private string BundledPromptPath => Path.Combine(AppContext.BaseDirectory, "prompt", "system-prompt.txt");

    private string EffectivePromptPath()
    {
        var user = SafePromptPath();
        if (File.Exists(user)) return user;
        if (File.Exists(BundledPromptPath)) return BundledPromptPath;
        return user;
    }

    private void OpenPromptButton_Click(object sender, RoutedEventArgs e)
    {
        var path = EffectivePromptPath();
        try
        {
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "你是一名《永恒之塔2》游戏聊天翻译。\n", new System.Text.UTF8Encoding(false));
            }
            Process.Start(new ProcessStartInfo("notepad.exe") { Arguments = "\"" + path + "\"", UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Write("打开提示词失败: " + ex);
            MessageBox.Show("没能自动打开，请手动打开：" + Environment.NewLine + path, "提示词文件",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }


    private void GetKeyButton_Click(object sender, RoutedEventArgs e)
    {
        OpenUrl(InviteUrl);
    }

    /// <summary>打开作者 B站 主页。</summary>
    private void Bili_Click(object sender, RoutedEventArgs e) => OpenUrl(AuthorBiliUrl);

    private static void OpenUrl(string url)
    {
        try
        {
            var before = new HashSet<int>();
            foreach (var p in Process.GetProcesses())
            {
                before.Add(p.Id);
                p.Dispose();
            }

            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            AppLog.Write("open url: " + url);

            // 浏览器已在运行时会直接在后台开新标签，用户以为“点了没反应”，
            // 所以这里把最上层的浏览器窗口抬到前台。
            Task.Run(() => BringBrowserToFront());
        }
        catch (Exception ex)
        {
            AppLog.Write("打开链接失败: " + ex);
            MessageBox.Show("打不开浏览器，链接是：" + Environment.NewLine + url, "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private static readonly string[] BrowserNames =
    {
        "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "360se", "360chrome",
        "qqbrowser", "sogouexplorer", "browser", "arc", "zen",
    };

    /// <summary>把任意一个可见的浏览器窗口抬到最前（EnumWindows 是 z 序，第一个命中的就是最上层那个）。</summary>
    private static void BringBrowserToFront()
    {
        for (int attempt = 0; attempt < 25; attempt++)
        {
            Thread.Sleep(200);
            IntPtr hit = IntPtr.Zero;
            try
            {
                EnumWindows((hwnd, _) =>
                {
                    if (!IsWindowVisible(hwnd)) return true;
                    if (GetWindowTextLength(hwnd) == 0) return true;
                    GetWindowThreadProcessId(hwnd, out uint pid);
                    if (pid == 0) return true;
                    string name;
                    try
                    {
                        using var proc = Process.GetProcessById((int)pid);
                        name = proc.ProcessName.ToLowerInvariant();
                    }
                    catch { return true; }
                    if (!BrowserNames.Any(n => name.Contains(n))) return true;
                    hit = hwnd;
                    return false; // 找到最上层那个就停
                }, IntPtr.Zero);
            }
            catch (Exception ex)
            {
                AppLog.Write("enum windows failed: " + ex.Message);
                return;
            }

            if (hit != IntPtr.Zero)
            {
                ForceForeground(hit);
                AppLog.Write("browser brought to front: 0x" + hit.ToString("X"));
                return;
            }
        }
        AppLog.Write("browser window not found (可能默认浏览器还没启动)");
    }

    private static void ForceForeground(IntPtr hwnd)
    {
        try
        {
            var targetThread = GetWindowThreadProcessId(hwnd, IntPtr.Zero);
            var foreground = GetForegroundWindow();
            var foregroundThread = GetWindowThreadProcessId(foreground, IntPtr.Zero);
            var self = GetCurrentThreadId();

            var attachedTarget = AttachThreadInput(self, targetThread, true);
            var attachedForeground = foregroundThread != 0 && foregroundThread != targetThread
                                     && AttachThreadInput(self, foregroundThread, true);
            ShowWindow(hwnd, SW_RESTORE);
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
            if (attachedForeground) AttachThreadInput(self, foregroundThread, false);
            if (attachedTarget) AttachThreadInput(self, targetThread, false);
        }
        catch (Exception ex)
        {
            AppLog.Write("bring browser to front failed: " + ex.Message);
        }
    }

    private void ApplySlot(System.Windows.Controls.ComboBox box, System.Windows.Controls.TextBox url, System.Windows.Controls.TextBox model, System.Windows.Controls.TextBox key)
    {
        if (Find(box.SelectedItem as string) is not { } provider) return;
        provider.BaseUrl = url.Text.Trim();
        provider.Model = model.Text.Trim();
        var keyText = key.Text.Trim();
        provider.Keys = string.IsNullOrWhiteSpace(keyText) ? new List<string>() : new List<string> { keyText };
        provider.Enabled = provider.BaseUrl.Length > 0 && provider.Model.Length > 0;
    }

    private async void TestButton_Click(object sender, RoutedEventArgs e)
    {
        TestResultText.Text = "正在测试…";
        OpenAiTranslator? translator = null;
        try
        {
            ApplySlot(ProviderBox, BaseUrlBox, ModelBox, KeyBox);

            var provider = Find(ProviderBox.SelectedItem as string ?? "");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            translator = new OpenAiTranslator(Settings);

            async Task<string> TestOne(string label, Provider? provider, string target, string text)
            {
                if (provider is null)
                    return label + "：未连通（这条通道不存在）";
                if (!provider.Usable)
                    return label + "：未连通（地址/模型没填全，或 Key 为空）";
                try
                {
                    var map = await translator.TranslateAsync(new[] { new ChatLine(1, text) }, cts.Token, target, provider.Name);
                    if (!map.TryGetValue(1, out var outcome))
                        return label + "：未连通（通道没有返回结果）";
                    return outcome.Failed
                        ? label + "：未连通（" + (string.IsNullOrWhiteSpace(outcome.Error) ? "通道报错" : outcome.Error) + "）"
                        : label + "：连通（" + outcome.Text + "）";
                }
                catch (OperationCanceledException)
                {
                    return label + "：未连通（45 秒没有返回）";
                }
                catch (Exception ex)
                {
                    return label + "：未连通（" + ex.Message + "）";
                }
            }

            // 同一条通道，两个方向都要通：外文→中文、中文→外语
            var toChinese = await TestOne("外文→中文", provider, Settings.TargetLanguage, "Hello, anyone want to party?");
            var toForeign = await TestOne("中文→外语", provider,
                string.IsNullOrWhiteSpace(Settings.ManualLanguage) ? "英语" : Settings.ManualLanguage,
                "你好，一起做任务吗？");

            TestResultText.Text = toChinese + "；" + toForeign;

        }
        catch (OperationCanceledException)
        {
            TestResultText.Text = "测试超时：通道没有在 45 秒内返回，检查网络或 Key";
        }
        catch (Exception ex)
        {
            AppLog.Write("测试通道异常: " + ex);
            TestResultText.Text = "测试出错：" + ex.Message;
        }
        finally
        {
            translator?.Dispose();
        }
    }

    private void CopyDiagButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var text = AppLog.Dump();
            if (string.IsNullOrWhiteSpace(text)) text = "（还没有诊断信息，先在主窗口点一次“开始翻译”）";
            Clipboard.SetText(text);
            TestResultText.Text = "诊断信息已复制，直接粘贴发出去即可";
        }
        catch (Exception ex)
        {
            AppLog.Write("复制诊断失败: " + ex);
            TestResultText.Text = "复制失败：" + ex.Message;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ApplySlot(ProviderBox, BaseUrlBox, ModelBox, KeyBox);
            Settings.Provider = ProviderBox.SelectedItem as string ?? "";
            if (LanguageBox.SelectedItem is string lang) Settings.TargetLanguage = lang;
            Settings.SourceMode = "游戏内存";
            Settings.ClickThrough = ClickThroughBox.IsChecked == true;
            Settings.OverlayOpacity = (int)Math.Round(OpacitySlider.Value);
            Settings.OverlayFontSize = (int)Math.Round(FontSlider.Value);
            Settings.OverlayImeCompat = ImeCompatBox.IsChecked == true;
            Settings.OneKeySend = OneKeySendBox.IsChecked == true;
        if (int.TryParse(StartupRecentBox.Text.Trim(), out var startupRecent))
            Settings.StartupRecentMessages = Math.Clamp(startupRecent, 0, 200);
        Settings.TranslateNoiseChannels = NoiseChannelsBox.IsChecked == true;
        Settings.QuickPhrases = (QuickPhrasesBox.Text ?? "")
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();

            if (int.TryParse(HistoryBox.Text.Trim(), out var history) && history >= 20 && history <= 5000)
                Settings.MaxHistory = history;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            AppLog.Write("保存设置失败: " + ex);
            MessageBox.Show("保存设置出错：" + ex.Message, "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
