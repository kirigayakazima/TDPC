namespace Jiaolong.Core.Logging;

using System;
using System.Diagnostics;
using System.IO;

/// <summary>
/// Thread-safe file logger.
/// Writes to %AppData%\TDPC\logs\tdpc_yyyyMMdd.log with rolling 30-day retention.
/// </summary>
public static class FileLogger
{
    private static readonly object _lock = new();
    private static string? _logDir;
    private static DateTime _lastCleanDate = DateTime.MinValue;

    public static string LogDirectory
    {
        get
        {
            if (_logDir is null)
            {
                _logDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "TDPC", "logs");
                try { Directory.CreateDirectory(_logDir); } catch { }
            }
            return _logDir;
        }
    }

    public static string CurrentLogFile =>
        Path.Combine(LogDirectory, $"tdpc_{DateTime.Now:yyyyMMdd}.log");

    /// <summary>
    /// Appends a message to the daily log file with timestamp.
    /// Automatically cleans up logs older than 30 days.
    /// </summary>
    public static void Log(string message, string level = "INFO")
    {
        try
        {
            EnsureCleanOldLogs(30);

            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}{Environment.NewLine}";
            lock (_lock)
            {
                File.AppendAllText(CurrentLogFile, line);
            }
        }
        catch
        {
            // Logging must never crash the application
        }
    }

    public static void Info(string message) => Log(message, "INFO");

    public static void Warn(string message) => Log(message, "WARN");

    public static void Error(string message, Exception? ex = null)
    {
        string full = ex is null ? message : $"{message} | 异常: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}";
        Log(full, "ERROR");
    }

    /// <summary>
    /// Removes any log files older than maxDays in %AppData%\TDPC\logs.
    /// </summary>
    public static void EnsureCleanOldLogs(int maxDays = 30)
    {
        if ((DateTime.Now - _lastCleanDate).TotalHours < 12) return;
        _lastCleanDate = DateTime.Now;

        try
        {
            var dir = new DirectoryInfo(LogDirectory);
            if (!dir.Exists) return;

            var cutoff = DateTime.Now.AddDays(-maxDays);
            foreach (var file in dir.GetFiles("tdpc_*.log"))
            {
                if (file.LastWriteTime < cutoff)
                {
                    try { file.Delete(); } catch { }
                }
            }
            foreach (var file in dir.GetFiles("tdpc_*.txt"))
            {
                if (file.LastWriteTime < cutoff)
                {
                    try { file.Delete(); } catch { }
                }
            }
        }
        catch { }
    }

    public static void OpenLogDirectory()
    {
        try
        {
            string dir = LogDirectory;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{dir}\"",
                UseShellExecute = true
            });
        }
        catch { }
    }
}
