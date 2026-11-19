using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace AionTranslator.UI;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppPaths.EnsureDataFolder();
        // 主窗口关掉时不能直接退出：托盘图标还要活着
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.Write("UI 未处理异常: " + args.Exception);
            MessageBox.Show(args.Exception.ToString(), "程序出错", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var text = "后台未处理异常（进程即将退出）: " + args.ExceptionObject + Environment.NewLine +
                       "时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            AppLog.Write(text);
            try { File.WriteAllText(Path.Combine(AppPaths.DataDir, "崩溃.log"), text, new System.Text.UTF8Encoding(true)); } catch { }
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Write("未观察任务异常: " + args.Exception);
            args.SetObserved();
        };
    }
}