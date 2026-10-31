using System.IO;
using System.Text;
using MultiWindowSwitcher.Core;

namespace MultiWindowSwitcher.Services;

public enum LogLevel
{
    /// <summary>Перехваченные ошибки и подробности — для разбора, не для тревоги.</summary>
    Trace,
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>Журнал в файл UserData\logs\ГГГГ-ММ-ДД.log. Окна журнала нет: программа про окна, а не про логи.</summary>
public static class AppLog
{
    private static readonly Lock FileSync = new();

    public static void Info(string text) => Write(LogLevel.Info, text);

    public static void Success(string text) => Write(LogLevel.Success, text);

    public static void Warn(string text) => Write(LogLevel.Warning, text);

    public static void Error(string text) => Write(LogLevel.Error, text);

    public static void Write(LogLevel level, string text)
    {
        var now = DateTime.Now;

        try
        {
            lock (FileSync)
            {
                File.AppendAllText(
                    AppPaths.LogFileForToday(now),
                    $"{now:yyyy-MM-dd HH:mm:ss} [{level}] {text}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch
        {
            // лог не должен ронять приложение
        }
    }

    /// <summary>Старые журналы не копятся вечно: храним последние две недели.</summary>
    public static void Prune(int keepDays = 14)
    {
        try
        {
            foreach (var file in Directory.GetFiles(AppPaths.LogsDir, "*.log"))
            {
                if (File.GetLastWriteTime(file) < DateTime.Now.AddDays(-keepDays))
                {
                    File.Delete(file);
                }
            }
        }
        catch
        {
            // не страшно
        }
    }
}
