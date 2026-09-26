namespace Jiaolong.App.Platform;

using System;
using System.Diagnostics;
using System.IO;

public static class AutoStartManager
{
    public const string TaskName = "TDPC_AutoStart";

    /// <summary>
    /// Checks whether the TDPC_AutoStart task exists in Windows Task Scheduler.
    /// </summary>
    public static bool IsScheduled()
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/query /tn \"{TaskName}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            proc?.WaitForExit(3000);
            return proc?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Registers or unregisters TDPC_AutoStart in Windows Task Scheduler.
    /// Uses /sc onlogon /rl highest /delay 0000:30 to bypass UAC and ensure system services are initialized.
    /// </summary>
    public static bool SetAutoStart(bool enable, out string message, int delaySeconds = 30)
    {
        if (enable)
        {
            string exe = Environment.ProcessPath
                ?? Process.GetCurrentProcess().MainModule?.FileName
                ?? "";
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
            {
                message = "无法获取当前程序绝对路径，自启注册失败";
                return false;
            }

            // delay format: mmmm:ss
            string delayStr = $"{delaySeconds / 60:D4}:{delaySeconds % 60:D2}";
            string args = $"/create /tn \"{TaskName}\" /tr \"\\\"{exe}\\\" -silent\" /sc onlogon /rl highest /delay {delayStr} /f";

            try
            {
                using var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });
                string output = proc?.StandardOutput.ReadToEnd() ?? "";
                string error = proc?.StandardError.ReadToEnd() ?? "";
                proc?.WaitForExit(5000);

                if (proc?.ExitCode == 0)
                {
                    message = $"已注册开机任务计划（延迟 {delaySeconds}s · 最高权限免 UAC 静默启动）";
                    return true;
                }
                else
                {
                    message = $"注册计划任务失败 (退出码 {proc?.ExitCode}): {OneLine(error)}";
                    return false;
                }
            }
            catch (Exception ex)
            {
                message = "创建计划任务异常: " + OneLine(ex.Message);
                return false;
            }
        }
        else
        {
            try
            {
                using var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = $"/delete /tn \"{TaskName}\" /f",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });
                proc?.WaitForExit(3000);
                message = proc?.ExitCode == 0 ? "已取消开机计划任务" : "未发现已注册的开机自启任务";
                return proc?.ExitCode == 0;
            }
            catch (Exception ex)
            {
                message = "注销计划任务异常: " + OneLine(ex.Message);
                return false;
            }
        }
    }

    private static string OneLine(string s) => s.Replace("\r", " ").Replace("\n", " ").Trim();
}
