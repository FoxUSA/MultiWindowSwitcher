using Microsoft.Win32;
using MultiWindowSwitcher.Core;

namespace MultiWindowSwitcher.Services;

/// <summary>
/// Запуск вместе с Windows: строка в HKCU\…\Run на лаунчер в корне с ключом --autostart.
/// Через лаунчер — чтобы подхватилось скачанное обновление. При отладке из bin ничего не пишется.
/// </summary>
public static class Autostart
{
    public const string Flag = "--autostart";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MultiWindowSwitcher";

    public static void Apply(bool enabled)
    {
        if (!AppPaths.IsPublishedLayout)
        {
            return;
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            var command = $"\"{AppPaths.LauncherFile}\" {Flag}";

            if (enabled)
            {
                // папку могли перенести — строка обновляется при каждом запуске
                if (!string.Equals(key.GetValue(ValueName) as string, command, StringComparison.OrdinalIgnoreCase))
                {
                    key.SetValue(ValueName, command);
                }
            }
            else if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Автозапуск не настроился: " + ex.Message);
        }
    }
}
