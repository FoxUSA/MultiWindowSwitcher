using System.IO;

namespace MultiWindowSwitcher.Core;

/// <summary>
/// Раскладка на диске:
///   &lt;корень&gt;\MultiWindowSwitcher.exe  — лаунчер
///   &lt;корень&gt;\Data\                    — программа, библиотеки, иконки и manifest.json (при обновлении затирается целиком)
///   &lt;корень&gt;\UserData\                — всё пользовательское: настройки, имена окон, логи
///   &lt;корень&gt;\Update\                  — только во время обновления: скачанный архив и подготовленная замена
/// </summary>
public static class AppPaths
{
    /// <summary>Имя лаунчера в корне. Обновление кладёт на его место файл из архива.</summary>
    public const string LauncherName = "MultiWindowSwitcher.exe";

    /// <summary>Имя основной программы внутри Data. По нему проверяется, что архив годный.</summary>
    public const string AppExeName = "MultiWindowSwitcher.App.exe";

    static AppPaths()
    {
        DataDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

        var parent = Directory.GetParent(DataDir)?.FullName;
        IsPublishedLayout = string.Equals(
            new DirectoryInfo(DataDir).Name, "Data", StringComparison.OrdinalIgnoreCase);

        RootDir = IsPublishedLayout && parent is not null ? parent : DataDir;
        UserDataDir = Path.Combine(RootDir, "UserData");
        LogsDir = Path.Combine(UserDataDir, "logs");

        Directory.CreateDirectory(UserDataDir);
        Directory.CreateDirectory(LogsDir);
    }

    /// <summary>Программа запущена из своей папки Data, а не из bin при отладке.</summary>
    public static bool IsPublishedLayout { get; }

    public static string RootDir { get; }

    public static string DataDir { get; }

    public static string UserDataDir { get; }

    public static string LogsDir { get; }

    /// <summary>Лаунчер в корне: он же применяет подготовленное обновление.</summary>
    public static string LauncherFile => Path.Combine(RootDir, LauncherName);

    /// <summary>Рабочая папка обновления. Лежит в корне: Data при обновлении заменяется целиком.</summary>
    public static string UpdateDir => Path.Combine(RootDir, "Update");

    /// <summary>Скачанное и проверенное обновление, готовое к подмене лаунчером.</summary>
    public static string StagedDir => Path.Combine(UpdateDir, "staged");

    /// <summary>Отметка готовности: пока файла нет, лаунчер обновление не применяет.</summary>
    public static string StagedMarkerFile => Path.Combine(StagedDir, "update.json");

    public static string SettingsFile => Path.Combine(UserDataDir, "settings.json");

    /// <summary>Окна, которым программа временно включила «живой» режим свёрнутого. Нужен, чтобы вернуть их после сбоя.</summary>
    public static string ParkedFile => Path.Combine(UserDataDir, "parked.json");

    /// <summary>Версия программы. Кладётся сборкой в Data и обновляется вместе с программой.</summary>
    public static string ManifestFile => Path.Combine(DataDir, "manifest.json");

    /// <summary>Иконка папки: все три лежат в Data и обновляются вместе с программой.</summary>
    public static string IconFile(string name) => Path.Combine(DataDir, name);

    public static string LogFileForToday(DateTime now) =>
        Path.Combine(LogsDir, $"{now:yyyy-MM-dd}.log");
}
