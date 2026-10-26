using System.Windows;
using System.Windows.Threading;

namespace GameLauncher;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandledException;
        base.OnStartup(e);
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            $"发生了意外错误：{e.Exception.Message}\n\n应用会继续运行；如问题反复出现，请关闭后重新打开。",
            "StarRailAutoBattle 启动器",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
