using System.Windows;
using Jiaolong.Core.Logging;

namespace Jiaolong.App;

public partial class App : Application
{
    private Mutex? _instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        var instance = new Mutex(false, @"Local\TDPC.App");
        bool entered;
        try { entered = instance.WaitOne(0); }
        catch (AbandonedMutexException) { entered = true; }
        if (!entered)
        {
            instance.Dispose();
            Shutdown();
            return;
        }
        _instance = instance;
        base.OnStartup(e);

        AppDomain.CurrentDomain.UnhandledException += (s, ev) =>
        {
            if (ev.ExceptionObject is Exception ex)
            {
                FileLogger.Error("未捕获的全局异常 (AppDomain)", ex);
            }
        };

        DispatcherUnhandledException += (s, ev) =>
        {
            FileLogger.Error("未捕获的 UI 调度异常 (Dispatcher)", ev.Exception);
            ev.Handled = true;
            MessageBox.Show($"TDPC 遇到异常但已自动拦截：\n{ev.Exception.Message}\n\n详细信息已自动保存至日志文件。\n日志目录：{FileLogger.LogDirectory}",
                "TDPC 异常提示", MessageBoxButton.OK, MessageBoxImage.Warning);
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.ReleaseMutex();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
