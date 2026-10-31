using System.Windows.Media;
using MultiWindowSwitcher.Core;

namespace MultiWindowSwitcher.Models;

/// <summary>Одно окно Windows в сетке. Живёт, пока живёт окно; номер (HWND) после перезапуска другой.</summary>
public sealed class WindowItem : ObservableObject
{
    private string _title = string.Empty;
    private string _name = string.Empty;
    private bool _isMinimized;
    private bool _inTray;
    private bool _isPinned;
    private bool _isWide;
    private bool _attention;
    private bool _isActive;
    private int _monitor = 1;
    private ImageSource? _icon;

    public WindowItem(IntPtr hwnd, uint pid, string app, string appTitle, string exePath)
    {
        Hwnd = hwnd;
        Pid = pid;
        App = app;
        AppTitle = appTitle;
        ExePath = exePath;
    }

    public IntPtr Hwnd { get; }

    public uint Pid { get; }

    /// <summary>Исполняемый файл в нижнем регистре: chrome.exe, explorer.exe. Ключ для правил и группировки.</summary>
    public string App { get; }

    /// <summary>Как программа называется для человека: Google Chrome, Проводник.</summary>
    public string AppTitle { get; }

    public string ExePath { get; }

    /// <summary>Порядок появления — для сортировки внутри сетки, пока человек сам не переставит.</summary>
    public int Order { get; set; }

    /// <summary>Скрыто глазом из сетки — видно в разделе «Скрытые», пока не вернут или не перезапустят программу.</summary>
    public bool HiddenByUser { get; set; }

    /// <summary>Крупный заголовок у этого окна убран крестиком — до перезапуска.</summary>
    public bool NoBigTitle { get; set; }

    public string Title
    {
        get => _title;
        set
        {
            if (Set(ref _title, value))
            {
                Raise(nameof(Caption));
            }
        }
    }

    /// <summary>Своё имя окна из правил; пусто — нет.</summary>
    public string Name
    {
        get => _name;
        set
        {
            if (Set(ref _name, value))
            {
                RaiseMany(nameof(HasName), nameof(Caption));
            }
        }
    }

    public bool HasName => !string.IsNullOrEmpty(Name);

    public string Caption => HasName ? Name : Title;

    public bool IsMinimized
    {
        get => _isMinimized;
        set => Set(ref _isMinimized, value);
    }

    /// <summary>Подпись значка программы в трее — по ней его нажать, чтобы окно открыла сама программа.</summary>
    public string? TrayName { get; set; }

    /// <summary>Программа спрятала окно в трей: окно есть, но невидимо. Живого превью нет — только значок.</summary>
    public bool InTray
    {
        get => _inTray;
        set => Set(ref _inTray, value);
    }

    public bool IsPinned
    {
        get => _isPinned;
        set => Set(ref _isPinned, value);
    }

    /// <summary>В сетке занимает две ячейки в ширину.</summary>
    public bool IsWide
    {
        get => _isWide;
        set => Set(ref _isWide, value);
    }

    /// <summary>Окно мигает на панели задач: пришло сообщение, закончилась загрузка.</summary>
    public bool Attention
    {
        get => _attention;
        set => Set(ref _attention, value);
    }

    public bool IsActive
    {
        get => _isActive;
        set => Set(ref _isActive, value);
    }

    public int Monitor
    {
        get => _monitor;
        set => Set(ref _monitor, value);
    }

    public ImageSource? Icon
    {
        get => _icon;
        set => Set(ref _icon, value);
    }

    public bool Matches(string query) =>
        Title.Contains(query, StringComparison.OrdinalIgnoreCase)
        || Name.Contains(query, StringComparison.OrdinalIgnoreCase)
        || AppTitle.Contains(query, StringComparison.OrdinalIgnoreCase)
        || App.Contains(query, StringComparison.OrdinalIgnoreCase);
}
