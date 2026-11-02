using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;
using MultiWindowSwitcher.Core;
using MultiWindowSwitcher.Services;

namespace MultiWindowSwitcher.Models;

/// <summary>
/// Имя окна хранится правилом: программа + часть заголовка. У окна после перезапуска программы
/// или Windows другой внутренний номер, а правило всё равно его узнаёт.
/// </summary>
public sealed class NameRule
{
    /// <summary>Исполняемый файл программы в нижнем регистре, например chrome.exe.</summary>
    public string App { get; set; } = string.Empty;

    /// <summary>Как программа называется для человека — только для списка в настройках.</summary>
    public string AppTitle { get; set; } = string.Empty;

    public string Fragment { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
}

/// <summary>Закреплённое первым окно — тоже правилом, чтобы пережить перезапуск.</summary>
public sealed class PinRule
{
    public string App { get; set; } = string.Empty;

    public string Fragment { get; set; } = string.Empty;
}

/// <summary>Нажатие мыши, записанное пользователем: кнопка, Ctrl/Shift/Alt, одинарный или двойной клик.</summary>
public sealed class MouseBinding
{
    /// <summary>Left, Right, Middle, X1, X2.</summary>
    public string Button { get; set; } = "Middle";

    public bool Ctrl { get; set; }

    public bool Shift { get; set; }

    public bool Alt { get; set; }

    public bool Double { get; set; }

    public bool SameAs(MouseBinding other) =>
        Button == other.Button && Ctrl == other.Ctrl && Shift == other.Shift && Alt == other.Alt && Double == other.Double;

    /// <summary>Как подписать нажатие: «Ctrl + Средняя ×2».</summary>
    public string Describe()
    {
        var parts = new List<string>();

        if (Ctrl)
        {
            parts.Add("Ctrl");
        }

        if (Shift)
        {
            parts.Add("Shift");
        }

        if (Alt)
        {
            parts.Add("Alt");
        }

        parts.Add(Loc.T("Mouse" + Button) + (Double ? " ×2" : string.Empty));
        return string.Join(" + ", parts);
    }
}

/// <summary>Программа, окна которой никогда не показываются.</summary>
public sealed class Exclusion
{
    public string App { get; set; } = string.Empty;

    public string AppTitle { get; set; } = string.Empty;
}

/// <summary>Место окна относительно рабочей области монитора: доли, а не пиксели — переживает смену разрешения.</summary>
public sealed class Placement
{
    public string Device { get; set; } = string.Empty;

    public double X { get; set; }

    public double Y { get; set; }

    public double W { get; set; }

    public double H { get; set; }
}

/// <summary>Где стоит плавающая кнопка: у какого края какого монитора и на какой высоте.</summary>
public sealed class FabPlacement
{
    public string Device { get; set; } = string.Empty;

    /// <summary>L, R — прилипла к краю; пусто — стоит свободно.</summary>
    public string Side { get; set; } = "R";

    public double X { get; set; } = 1;

    public double Y { get; set; } = 0.45;
}

public sealed class AppSettings
{
    /// <summary>auto — по языку Windows, ru или en.</summary>
    public string Language { get; set; } = "auto";

    /// <summary>Тему читает и лаунчер (окно установки обновления).</summary>
    public string Theme { get; set; } = "Dark";

    public bool AutoHide { get; set; }

    /// <summary>Закрывать дубли папок: одна папка — одно окно Проводника.</summary>
    public bool CloseDuplicateFolders { get; set; } = true;

    /// <summary>Заголовки окон без своего имени тоже крупно, на месте имени.</summary>
    public bool BigTitles { get; set; }

    /// <summary>Размер крупных названий: 1 — обычный, от 0.5 до 2.</summary>
    public double TitleScale { get; set; } = 1;

    public string TitleColor { get; set; } = "#FFFFFF";

    /// <summary>Что показывать в названии: full — всё, window — только окно (вкладку), app — только программу.</summary>
    public string TitlePart { get; set; } = "full";

    /// <summary>Сначала программа: «Google Chrome - Панель» вместо «Панель - Google Chrome».</summary>
    public bool AppFirst { get; set; } = true;

    /// <summary>Показывать и программы, спрятанные в трей (Telegram и т. п.): клик — окно разворачивается.</summary>
    public bool TrayWindows { get; set; } = true;

    /// <summary>Окну в трее — последний снимок, пока оно было на экране, вместо значка.</summary>
    public bool TraySnapshots { get; set; } = true;

    /// <summary>Крупный значок программы в левом нижнем углу превью.</summary>
    public bool BigIcons { get; set; }

    /// <summary>Размер крупного значка, px при 100 % масштабе.</summary>
    public double IconSize { get; set; } = 104;

    /// <summary>Размер крупного значка в ленте — свой: там он стоит рядом с превью и упирается в высоту ленты.</summary>
    public double StripIconSize { get; set; } = 104;

    public bool Topmost { get; set; } = true;

    /// <summary>Плавающая иконка у края. Выключена — открывать из трея или активным углом.</summary>
    public bool ShowFab { get; set; } = true;

    public bool Snap { get; set; } = true;

    public bool Tuck { get; set; } = true;

    public bool HoverOpen { get; set; } = true;

    public bool LiveMinimized { get; set; } = true;

    public bool KeepGrouped { get; set; } = true;

    public bool HideJunk { get; set; } = true;

    public bool FollowMouse { get; set; } = true;

    public bool Autostart { get; set; } = true;

    public bool Tray { get; set; } = true;

    public bool CornerTopLeft { get; set; } = true;

    /// <summary>Лента: None — обычное окно; Top, Bottom — на всю ширину монитора, место на экране зарезервировано.</summary>
    public string Dock { get; set; } = "None";

    /// <summary>Монитор ленты (имя устройства).</summary>
    public string DockDevice { get; set; } = string.Empty;

    /// <summary>Высота ленты в единицах WPF.</summary>
    public double DockHeight { get; set; } = 240;

    /// <summary>Лента: мышь на плитке — большое превью окна рядом с лентой.</summary>
    public bool HoverPreview { get; set; } = true;

    /// <summary>Размер большого превью: процент от рабочей области монитора.</summary>
    public double HoverPreviewSize { get; set; } = 60;

    /// <summary>Нажатия мыши по пустому заголовку окна или рабочему столу — показать/спрятать. null — ещё не перенесены из старых настроек.</summary>
    public List<MouseBinding>? MouseBindings { get; set; }

    /// <summary>Клик левой по пустому месту рабочего стола (мимо значков) — показать/спрятать: Off, Single, Double.</summary>
    public string DesktopClick { get; set; } = "Off";

    /// <summary>Старые настройки до записи нажатий — читаются один раз и переносятся в MouseBindings.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? MiddleClickGesture { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? SideButtonsGesture { get; set; }

    public bool CornerTopRight { get; set; }

    public bool CornerBottomLeft { get; set; }

    public bool CornerBottomRight { get; set; }

    public double Aspect { get; set; } = 1.6;

    public List<NameRule> Names { get; set; } = new();

    public List<PinRule> Pins { get; set; } = new();

    /// <summary>Окна двойной ширины — тем же правилом, что и закреплённые.</summary>
    public List<PinRule> Wides { get; set; } = new();

    public List<Exclusion> Exclusions { get; set; } = new();

    public Placement? Window { get; set; }

    public FabPlacement Fab { get; set; } = new();
}

/// <summary>Настройки в UserData\settings.json. Запись — с задержкой, чтобы перетаскивание не писало файл сто раз.</summary>
public static class Settings
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private static DispatcherTimer? _saveTimer;

    public static AppSettings Current { get; private set; } = new();

    public static event Action? Changed;

    public static void Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile), Options) ?? new();
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("settings.json не читается, взяты настройки по умолчанию: " + ex.Message);
            Current = new();
        }

        MigrateMouse(Current);
    }

    /// <summary>Было два переключателя (средняя, боковые) — стали записанные нажатия.</summary>
    private static void MigrateMouse(AppSettings s)
    {
        if (s.MouseBindings is null)
        {
            s.MouseBindings = new();

            if (s.MiddleClickGesture ?? true)
            {
                s.MouseBindings.Add(new MouseBinding { Button = "Middle" });
            }

            if (s.SideButtonsGesture ?? false)
            {
                s.MouseBindings.Add(new MouseBinding { Button = "X1" });
                s.MouseBindings.Add(new MouseBinding { Button = "X2" });
            }
        }

        s.MiddleClickGesture = null;
        s.SideButtonsGesture = null;
    }

    /// <summary>Что-то поменяли: сообщить интерфейсу и записать на диск чуть позже.</summary>
    public static void Touch()
    {
        Changed?.Invoke();

        _saveTimer ??= CreateTimer();
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public static void SaveNow()
    {
        _saveTimer?.Stop();

        try
        {
            var tmp = AppPaths.SettingsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, Options));
            File.Move(tmp, AppPaths.SettingsFile, true);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Не удалось записать settings.json: " + ex.Message);
        }
    }

    private static DispatcherTimer CreateTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        timer.Tick += (_, _) => SaveNow();
        return timer;
    }
}
