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
            AppLog.Write("后台未处理异常: " + args.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Write("未观察任务异常: " + args.Exception);
            args.SetObserved();
        };
    }
}