using System.Windows;
using Jiaolong.Core.Logging;

namespace Jiaolong.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
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
}
