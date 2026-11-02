using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MultiWindowSwitcher.Models;
using MultiWindowSwitcher.Native;
using static MultiWindowSwitcher.Native.Win32;

namespace MultiWindowSwitcher.Services;

/// <summary>
/// Все окна Windows, которые человек видит на панели задач и в Alt+Tab.
/// Список обновляется по событиям Windows (появилось, закрылось, свернулось, стало активным)
/// и раз в секунду на всякий случай — заголовки меняются без событий.
/// </summary>
public sealed class WindowTracker
{
    private static readonly HashSet<string> JunkClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow",
        "XamlExplorerHostIslandWindow", "TopLevelWindowForOverflowXamlIsland", "NotifyIconOverflowWindow",
        "ForegroundStaging", "MultitaskingViewFrame", "Windows.Internal.Shell.TabProxyWindow",
    };

    private readonly Dictionary<IntPtr, WindowItem> _map = new();
    private readonly Dictionary<uint, (long Started, string App, string Title, string Path)> _apps = new();
    private readonly Dictionary<string, ImageSource?> _exeIcons = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _soon;
    private readonly uint _ownPid = (uint)Environment.ProcessId;
    private readonly List<IntPtr> _hooks = new();
    private WinEventProc? _hookProc;
    private int _shellMessage;
    private int _order;

    public WindowTracker()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Refresh();

        _soon = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _soon.Tick += (_, _) =>
        {
            _soon.Stop();
            Refresh();
        };
    }

    /// <summary>Все окна, кроме исключённых. Скрытые крестиком тоже здесь — с отметкой.</summary>
    public List<WindowItem> Windows { get; } = new();

    /// <summary>Список или состояние окон поменялись — сетке пора перестроиться.</summary>
    public event Action? Changed;

    /// <summary>Окно замигало на панели задач.</summary>
    public event Action<WindowItem>? Flashed;

    public IntPtr ActiveHwnd { get; private set; }

    public void Start(Window host)
    {
        var handle = new WindowInteropHelper(host).Handle;
        var source = HwndSource.FromHwnd(handle);

        // значки в трее читаются в фоне: появился или пропал значок — пересобрать сетку
        TrayIcons.Updated += () => host.Dispatcher.BeginInvoke(new Action(RefreshSoon));

        // мигание на панели задач приходит только через shell hook
        _shellMessage = RegisterWindowMessage("SHELLHOOK");
        RegisterShellHookWindow(handle);
        source?.AddHook(ShellHook);

        _hookProc = OnWinEvent;
        Hook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND);
        Hook(EVENT_SYSTEM_MOVESIZEEND, EVENT_SYSTEM_MOVESIZEEND);
        Hook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND);
        Hook(EVENT_OBJECT_DESTROY, EVENT_OBJECT_HIDE);

        Refresh();
        _timer.Start();

        if (Settings.Current.LiveMinimized)
        {
            ParkAllMinimized();
        }

        void Hook(uint min, uint max) =>
            _hooks.Add(SetWinEventHook(min, max, IntPtr.Zero, _hookProc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS));
    }

    public void Stop(Window host)
    {
        _timer.Stop();

        foreach (var hook in _hooks)
        {
            UnhookWinEvent(hook);
        }

        _hooks.Clear();
        DeregisterShellHookWindow(new WindowInteropHelper(host).Handle);
    }

    public void RefreshSoon()
    {
        _soon.Stop();
        _soon.Start();
    }

    // ── события Windows ──────────────────────────────────────────────────────

    /// <summary>
    /// Вызывается самой Windows: ошибка здесь роняет процесс мимо всех ловушек WPF,
    /// поэтому всё под try и в журнал.
    /// </summary>
    private void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            HandleWinEvent(evt, hwnd, idObject, idChild);
        }
        catch (Exception ex)
        {
            AppLog.Error($"Ошибка в событии Windows 0x{evt:X4}: {ex}");
        }
    }

    private const uint EVENT_SYSTEM_MOVESIZEEND = 0x000B;

    /// <summary>Человек отпустил окно после перетаскивания или изменения размера.</summary>
    public event Action<IntPtr>? MoveSizeEnded;

    private void HandleWinEvent(uint evt, IntPtr hwnd, int idObject, int idChild)
    {
        if (idObject != OBJID_WINDOW || idChild != 0 || hwnd == IntPtr.Zero)
        {
            return;
        }

        switch (evt)
        {
            case EVENT_SYSTEM_FOREGROUND:
                OnForeground(hwnd);
                break;

            case EVENT_SYSTEM_MOVESIZEEND:
                MoveSizeEnded?.Invoke(hwnd);
                RefreshSoon();
                break;

            case EVENT_SYSTEM_MINIMIZESTART:
                // окно свернули: ждём конца анимации и паркуем
                if (Settings.Current.LiveMinimized && !Parking.IsQuiet(hwnd))
                {
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
                    timer.Tick += (_, _) =>
                    {
                        timer.Stop();

                        if (IsIconic(hwnd) && _map.ContainsKey(hwnd))
                        {
                            Parking.TryPark(hwnd);
                        }

                        RefreshSoon();
                    };
                    timer.Start();
                }

                RefreshSoon();
                break;

            case EVENT_OBJECT_DESTROY:
                if (_map.ContainsKey(hwnd))
                {
                    Parking.Forget(hwnd);
                    RefreshSoon();
                }

                break;

            default:
                RefreshSoon();
                break;
        }
    }

    private void OnForeground(IntPtr hwnd)
    {
        // припаркованное окно выбрали на панели задач или через Alt+Tab — пора стать видимым
        if (Parking.IsParked(hwnd) && !Parking.IsQuiet(hwnd))
        {
            Parking.Unpark(hwnd, minimize: false);
        }

        if (_map.TryGetValue(hwnd, out var item))
        {
            item.Attention = false;
            SetActive(hwnd);
        }

        RefreshSoon();
    }

    private IntPtr ShellHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != _shellMessage || _shellMessage == 0)
        {
            return IntPtr.Zero;
        }

        var code = wParam.ToInt32();

        if (code == HSHELL_FLASH && _map.TryGetValue(lParam, out var item) && lParam != GetForegroundWindow())
        {
            item.Attention = true;
            Flashed?.Invoke(item);
            Changed?.Invoke();
        }
        else if (code is HSHELL_WINDOWCREATED or HSHELL_WINDOWDESTROYED or HSHELL_REDRAW)
        {
            RefreshSoon();
        }

        return IntPtr.Zero;
    }

    private void SetActive(IntPtr hwnd)
    {
        ActiveHwnd = hwnd;

        foreach (var w in Windows)
        {
            w.IsActive = w.Hwnd == hwnd;
        }
    }

    // ── опрос ────────────────────────────────────────────────────────────────

    public void Refresh()
    {
        try
        {
            RefreshCore();
        }
        finally
        {
            _primed = true;
        }
    }

    /// <summary>Первый обход при запуске прошёл: дальше новые окна — действительно новые.</summary>
    private bool _primed;

    private void RefreshCore()
    {
        Parking.Enforce();
        var settings = Settings.Current;
        var monitors = Monitors.All();
        var seen = new HashSet<IntPtr>();
        var changed = false;

        EnumWindows((hwnd, _) =>
        {
            if (!IsCandidate(hwnd, settings.HideJunk))
            {
                return true;
            }

            GetWindowThreadProcessId(hwnd, out var pid);

            if (pid == _ownPid)
            {
                return true;
            }

            var (app, appTitle, path) = AppOf(hwnd, pid);

            if (settings.Exclusions.Any(e => string.Equals(e.App, app, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            seen.Add(hwnd);

            if (!_map.TryGetValue(hwnd, out var item))
            {
                item = new WindowItem(hwnd, pid, app, appTitle, path) { Order = NextOrder(app) };
                item.Icon = IconOf(hwnd, path);
                _map[hwnd] = item;
                Windows.Add(item);
                changed = true;

                // новое окно Проводника (не те, что были при запуске) — не дубль ли открытой папки
                if (_primed && app == "explorer.exe")
                {
                    ExplorerDuplicates.Schedule(hwnd);
                }
            }

            var title = GetTitle(hwnd);
            var minimized = IsIconic(hwnd) || Parking.IsParked(hwnd);
            var monitor = Monitors.NumberOf(hwnd, monitors);

            if (item.Title != title || item.IsMinimized != minimized || item.Monitor != monitor || item.InTray)
            {
                item.Title = title;
                item.IsMinimized = minimized;
                item.Monitor = monitor;
                item.InTray = false;
                changed = true;
            }

            return true;
        }, IntPtr.Zero);

        if (settings.TrayWindows)
        {
            changed |= AddTrayWindows(settings, monitors, seen);
        }

        foreach (var gone in _map.Keys.Where(h => !seen.Contains(h)).ToList())
        {
            var item = _map[gone];
            _map.Remove(gone);
            Windows.Remove(item);
            Parking.Forget(gone);
            changed = true;
        }

        if (ApplyRules())
        {
            changed = true;
        }

        var foreground = GetForegroundWindow();

        if (_map.ContainsKey(foreground) && foreground != ActiveHwnd)
        {
            SetActive(foreground);
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    // ── окна в трее ──────────────────────────────────────────────────────────

    /// <summary>Служебные невидимые окна, которые похожи на главные, но ими не являются.</summary>
    private static readonly HashSet<string> TrayJunkClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "ConsoleWindowClass", "OperationStatusWindow", "MS_WebcheckMonitor", "TtkMonitorClass", "CrossDeviceResumeWindowClass",
    };

    private static readonly HashSet<string> TrayJunkApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "applicationframehost.exe", "textinputhost.exe", "shellexperiencehost.exe", "searchhost.exe",
        "startmenuexperiencehost.exe", "systemsettings.exe", "lockapp.exe", "widgets.exe", "crossdeviceresume.exe",
        "msedgewebview2.exe", "conhost.exe", "cmd.exe",
    };

    /// <summary>
    /// Программы, спрятанные в трей: их главное окно существует, но невидимо, других видимых окон
    /// у программы нет и у неё есть значок в трее. Окно — по виду (заголовок, кнопка «свернуть»,
    /// нормальный размер), от программы одно, самое большое. Открывать — нажатием её значка.
    /// </summary>
    private bool AddTrayWindows(AppSettings settings, IReadOnlyList<MonitorInfo> monitors, HashSet<IntPtr> seen)
    {
        var visiblePids = new HashSet<uint>(_map.Where(p => seen.Contains(p.Key)).Select(p => p.Value.Pid));
        var best = new Dictionary<uint, (IntPtr Hwnd, int Area)>();

        EnumWindows((hwnd, _) =>
        {
            if (!IsHiddenByApp(hwnd) || GetWindow(hwnd, GW_OWNER) != IntPtr.Zero)
            {
                return true;
            }

            var style = GetStyle(hwnd);
            var ex = GetExStyle(hwnd);

            if ((style & WS_MINIMIZEBOX) == 0 || ((ex & WS_EX_TOOLWINDOW) != 0 && (ex & WS_EX_APPWINDOW) == 0) || (ex & WS_EX_NOACTIVATE) != 0)
            {
                return true;
            }

            var cls = GetClassName(hwnd);
            var title = GetTitle(hwnd);

            if (string.IsNullOrWhiteSpace(title) || title == "Hidden Window" || JunkClasses.Contains(cls) || TrayJunkClasses.Contains(cls)
                || cls.StartsWith("HwndWrapper[PowerToys", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var placement = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
            GetWindowPlacement(hwnd, ref placement);
            var r = placement.rcNormalPosition;
            var area = (r.Right - r.Left) * (r.Bottom - r.Top);

            if (r.Right - r.Left < 300 || r.Bottom - r.Top < 200)
            {
                return true;
            }

            GetWindowThreadProcessId(hwnd, out var pid);

            if (pid == _ownPid || visiblePids.Contains(pid))
            {
                return true;
            }

            if (!best.TryGetValue(pid, out var known) || area > known.Area)
            {
                best[pid] = (hwnd, area);
            }

            return true;
        }, IntPtr.Zero);

        var changed = false;

        foreach (var (pid, (hwnd, _)) in best)
        {
            var (app, appTitle, path) = AppOf(hwnd, pid);

            if (TrayJunkApps.Contains(app) || settings.Exclusions.Any(e => string.Equals(e.App, app, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            // нет значка в трее — открыть окно правильно нечем, значит и показывать не будем
            var trayName = TrayIcons.Find(appTitle, app, GetTitle(hwnd));

            if (trayName is null)
            {
                continue;
            }

            seen.Add(hwnd);

            if (!_map.TryGetValue(hwnd, out var item))
            {
                item = new WindowItem(hwnd, pid, app, appTitle, path) { Order = NextOrder(app) };
                item.Icon = IconOf(hwnd, path);
                _map[hwnd] = item;
                Windows.Add(item);
                changed = true;
            }

            var title = GetTitle(hwnd);
            item.TrayName = trayName;

            if (!item.InTray || item.Title != title || item.IsMinimized)
            {
                item.Title = title;
                item.IsMinimized = false;
                item.InTray = true;
                item.Monitor = Monitors.NumberOf(hwnd, monitors);
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>Как Alt+Tab: видимое, не служебное, без владельца, не на другом рабочем столе.</summary>
    private static bool IsCandidate(IntPtr hwnd, bool hideJunk)
    {
        if (!IsWindowVisible(hwnd) || IsCloaked(hwnd))
        {
            return false;
        }

        var ex = GetExStyle(hwnd);
        var appWindow = (ex & WS_EX_APPWINDOW) != 0;

        if ((ex & WS_EX_TOOLWINDOW) != 0 && !appWindow)
        {
            return false;
        }

        if (GetWindow(hwnd, GW_OWNER) != IntPtr.Zero && !appWindow)
        {
            return false;
        }

        if ((ex & WS_EX_NOACTIVATE) != 0 && !appWindow)
        {
            return false;
        }

        if (JunkClasses.Contains(GetClassName(hwnd)))
        {
            return false;
        }

        if (!hideJunk)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(GetTitle(hwnd)))
        {
            return false;
        }

        if (!IsIconic(hwnd))
        {
            var bounds = GetVisibleBounds(hwnd);

            if (bounds.Width < 48 || bounds.Height < 32)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Чьё окно: исполняемый файл и человеческое название. Окна приложений из Магазина
    /// принадлежат ApplicationFrameHost — настоящую программу ищем среди дочерних окон.
    /// </summary>
    private (string App, string Title, string Path) AppOf(IntPtr hwnd, uint pid)
    {
        // номер закрывшегося процесса Windows выдаёт новому: запись верна, только пока это тот же запуск
        var started = GetProcessStart(pid);

        if (_apps.TryGetValue(pid, out var known) && known.Started == started && known.App != "applicationframehost.exe")
        {
            return (known.App, known.Title, known.Path);
        }

        var path = GetProcessPath(pid) ?? string.Empty;
        var exe = Path.GetFileName(path).ToLowerInvariant();

        if (exe == "applicationframehost.exe")
        {
            uint inner = 0;

            EnumChildWindows(hwnd, (child, _) =>
            {
                GetWindowThreadProcessId(child, out var childPid);

                if (childPid != pid)
                {
                    inner = childPid;
                    return false;
                }

                return true;
            }, IntPtr.Zero);

            if (inner != 0)
            {
                var innerPath = GetProcessPath(inner);

                if (!string.IsNullOrEmpty(innerPath))
                {
                    return (Path.GetFileName(innerPath).ToLowerInvariant(), Describe(innerPath), innerPath);
                }
            }

            return (exe, GetTitle(hwnd), path);
        }

        var result = (exe.Length > 0 ? exe : "unknown", Describe(path), path);
        _apps[pid] = (started, result.Item1, result.Item2, result.Item3);
        return result;
    }

    private static string Describe(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "?";
        }

        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            var text = !string.IsNullOrWhiteSpace(info.FileDescription) ? info.FileDescription
                : !string.IsNullOrWhiteSpace(info.ProductName) ? info.ProductName
                : null;

            if (!string.IsNullOrWhiteSpace(text))
            {
                return text.Trim();
            }
        }
        catch
        {
            // описания нет — будет имя файла
        }

        return Path.GetFileNameWithoutExtension(path);
    }

    /// <summary>Новое окно — в конец, а при «ставить к своим» — сразу за окнами той же программы.</summary>
    private int NextOrder(string app)
    {
        _order += 10;

        if (!Settings.Current.KeepGrouped)
        {
            return _order;
        }

        var same = Windows.Where(w => w.App == app).OrderBy(w => w.Order).LastOrDefault();

        if (same is null)
        {
            return _order;
        }

        // освобождаем место сразу за последним окном этой программы
        foreach (var w in Windows.Where(w => w.Order > same.Order))
        {
            w.Order += 10;
        }

        return same.Order + 5;
    }

    // ── правила: имена и закреплённые ────────────────────────────────────────

    /// <summary>Пройти правила по всем окнам. true — что-то поменялось.</summary>
    public bool ApplyRules()
    {
        var settings = Settings.Current;
        var changed = false;

        BindNames();

        foreach (var w in Windows)
        {
            var name = w.NameRule?.Name ?? string.Empty;
            var pinned = settings.Pins.Any(p => Fits(w, p.App, p.Fragment));
            var wide = settings.Wides.Any(p => Fits(w, p.App, p.Fragment));

            if (w.Name != name || w.IsPinned != pinned || w.IsWide != wide)
            {
                w.Name = name;
                w.IsPinned = pinned;
                w.IsWide = wide;
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>
    /// Имя держится за окном: заголовок нужен только чтобы узнать окно, у которого имени ещё нет (новое окно той же
    /// программы). Одно имя — одному окну: другое окно не заберёт его, даже если его заголовок совпал с правилом.
    /// В редакторе кода имя держится за проектом: в окне открыли другой проект — имя другое.
    /// </summary>
    private void BindNames()
    {
        var rules = Settings.Current.Names;
        var taken = new HashSet<NameRule>();

        foreach (var w in Windows)
        {
            if (w.NameRule is { } bound && (!rules.Contains(bound) || !Holds(w, bound)))
            {
                w.NameRule = null;
            }

            w.NameRule ??= rules.FirstOrDefault(r => r.Hwnd == w.Hwnd.ToInt64() && string.Equals(r.App, w.App, StringComparison.OrdinalIgnoreCase) && !taken.Contains(r) && Holds(w, r));

            if (w.NameRule is { } rule)
            {
                taken.Add(rule);
            }
        }

        // сначала проект один в один: иначе правило «foo» заберёт окно проекта «foobar»
        foreach (var w in Windows.Where(w => w.NameRule is null && ProjectOf(w) is not null))
        {
            var project = DefaultFragment(w);
            w.NameRule = rules.FirstOrDefault(r => !taken.Contains(r) && string.Equals(r.App, w.App, StringComparison.OrdinalIgnoreCase) && string.Equals(Clean(r.Fragment), project, StringComparison.OrdinalIgnoreCase));

            if (w.NameRule is { } rule)
            {
                taken.Add(rule);
            }
        }

        foreach (var w in Windows.Where(w => w.NameRule is null))
        {
            w.NameRule = rules.FirstOrDefault(r => !taken.Contains(r) && Fits(w, r.App, r.Fragment));

            if (w.NameRule is { } rule)
            {
                taken.Add(rule);
            }
        }

        foreach (var w in Windows)
        {
            if (w.NameRule is not { } rule)
            {
                continue;
            }

            if (rule.Hwnd != w.Hwnd.ToInt64())
            {
                rule.Hwnd = w.Hwnd.ToInt64();
                Settings.Touch();
            }

            // правило старое, с названием вкладки: окно нашлось, теперь оно будет узнаваться по проекту
            if (!rule.ByProject && ProjectOf(w) is not null)
            {
                rule.Fragment = DefaultFragment(w);
                rule.ByProject = true;
                Settings.Touch();
            }
        }
    }

    /// <summary>Заголовок на секунду без проекта (окно загружается) имя не отбирает.</summary>
    private static bool Holds(WindowItem w, NameRule rule) =>
        !rule.ByProject || ProjectOf(w) is null || Fits(w, rule.App, rule.Fragment);

    private static readonly HashSet<string> CodeEditors = new(StringComparer.OrdinalIgnoreCase)
    {
        "code.exe", "code - insiders.exe", "cursor.exe", "windsurf.exe", "vscodium.exe",
    };

    /// <summary>Проект в редакторе кода: заголовок «вкладка - проект - Visual Studio Code», вкладка всё время другая, проект — нет.</summary>
    public static string? ProjectOf(WindowItem w)
    {
        if (!CodeEditors.Contains(w.App))
        {
            return null;
        }

        var parts = w.Title.Split(" - ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length >= 2 ? parts[^2].TrimStart('●', ' ') : null;
    }

    public static NameRule? RuleFor(WindowItem w) =>
        w.NameRule is { } bound && Settings.Current.Names.Contains(bound)
            ? bound
            : Settings.Current.Names.FirstOrDefault(r => Fits(w, r.App, r.Fragment));

    public static PinRule? PinFor(WindowItem w) =>
        Settings.Current.Pins.FirstOrDefault(p => Fits(w, p.App, p.Fragment));

    public static PinRule? WideFor(WindowItem w) =>
        Settings.Current.Wides.FirstOrDefault(p => Fits(w, p.App, p.Fragment));

    private static bool Fits(WindowItem w, string app, string fragment) =>
        string.Equals(w.App, app, StringComparison.OrdinalIgnoreCase)
        && Clean(w.Title).Contains(Clean(fragment), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Заголовок без того, что меняется само: счётчиков непрочитанных «(659)» и невидимых знаков направления текста
    /// (у Telegram: «‎Fox (659)» → «Fox»). Иначе правило слетает при первом новом сообщении.
    /// </summary>
    public static string Clean(string title) =>
        Regex.Replace(title.Replace("\u200E", "").Replace("\u200F", ""), @"\s*[(\[]\d+[)\]]", "").Trim();

    /// <summary>
    /// Для закрепления и двойной ширины: единственное окно программы — правило на всю программу
    /// (у Telegram заголовок — открытый чат, он всё время другой), несколько окон — по части заголовка.
    /// </summary>
    public string KeyFragment(WindowItem w) =>
        Windows.Count(o => string.Equals(o.App, w.App, StringComparison.OrdinalIgnoreCase)) <= 1 ? string.Empty : DefaultFragment(w);

    /// <summary>Часть заголовка по умолчанию: всё до « — » или « - », без названия программы в конце; в редакторе кода — проект.</summary>
    public static string DefaultFragment(WindowItem w)
    {
        var title = ProjectOf(w) ?? w.Title.Trim();

        foreach (var separator in new[] { " — ", " - ", " | " })
        {
            var index = title.IndexOf(separator, StringComparison.Ordinal);

            if (index > 0)
            {
                title = title[..index];
                break;
            }
        }

        title = Clean(title);
        return title.Length > 40 ? title[..40] : title;
    }

    // ── живые свёрнутые ──────────────────────────────────────────────────────

    public void ParkAllMinimized()
    {
        foreach (var w in Windows.Where(w => IsIconic(w.Hwnd)))
        {
            Parking.TryPark(w.Hwnd);
        }
    }

    // ── действия с окнами ────────────────────────────────────────────────────

    /// <summary>Переключиться на окно: развернуть, если свёрнуто, и сделать активным.</summary>
    /// <summary>Окно из трея уже открывается — второй раз значок не нажимаем, только сообщаем.</summary>
    public event Action<WindowItem>? TrayOpening;

    public void Activate(WindowItem item)
    {
        var hwnd = item.Hwnd;
        item.Attention = false;

        if (Parking.IsParked(hwnd))
        {
            Parking.Unpark(hwnd, minimize: false);
        }

        // спрятано в трей — нажимаем значок программы: окно откроет она сама, своим обычным способом
        if (item.InTray && IsHiddenByApp(hwnd))
        {
            // подпись значка меняется (у Telegram — со счётчиком непрочитанных): ищем по свежему списку
            var name = TrayIcons.Find(item.AppTitle, item.App, item.Title) ?? item.TrayName;

            if (name is null)
            {
                AppLog.Warn($"Значок в трее не найден: {item.App}.");
                return;
            }

            if (TrayIcons.ClickedRecently(name))
            {
                AppLog.Info($"Окно из трея: «{item.Title}» — значок уже нажат, ждём, пока программа откроет окно.");
                TrayOpening?.Invoke(item);
                return;
            }

            AppLog.Info($"Окно из трея: «{item.Title}» ({item.App}) — нажимаем значок «{name}».");
            TrayIcons.Click(name, hwnd, ok =>
            {
                if (!ok)
                {
                    AppLog.Warn($"Значок «{name}» в трее не нажался.");
                    return;
                }

                // ждём, пока программа покажет окно, и выводим его вперёд — иначе его может
                // закрыть собой переключатель («Поверх окон»), и кажется, что клик не сработал
                for (var i = 0; i < 80 && IsHiddenByApp(hwnd); i++)
                {
                    Thread.Sleep(25);
                }

                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    RefreshSoon();

                    if (!IsHiddenByApp(hwnd))
                    {
                        Activate(item);
                    }
                }));
            });
            return;
        }

        if (IsIconic(hwnd))
        {
            ShowWindow(hwnd, SW_RESTORE);
        }

        // Windows не любит, когда фокус отдаёт чужой процесс: подсоединяемся к потоку активного окна
        var foreground = GetForegroundWindow();
        var foregroundThread = GetWindowThreadProcessId(foreground, out _);
        var ownThread = GetCurrentThreadId();
        var attached = foregroundThread != ownThread && AttachThreadInput(ownThread, foregroundThread, true);

        try
        {
            SetWindowPos(hwnd, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            BringWindowToTop(hwnd);

            // Windows не отдаёт фокус, если «последний ввод» был не у нас: короткое нажатие Alt
            // снимает этот запрет — так делают все переключатели окон
            if (!SetForegroundWindow(hwnd) || GetForegroundWindow() != hwnd)
            {
                keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
                keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                SetForegroundWindow(hwnd);
            }

            if (GetForegroundWindow() != hwnd)
            {
                AppLog.Warn($"Windows не дала вывести на передний план «{item.Title}» ({item.App}).");
            }
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(ownThread, foregroundThread, false);
            }
        }

        SetActive(hwnd);
        Changed?.Invoke();
    }

    /// <summary>Окно уже впереди: активное последним, на экране, и чужие окна его не закрывают. Сам переключатель не считается.</summary>
    public bool IsInFront(WindowItem item)
    {
        var hwnd = item.Hwnd;
        return hwnd == ActiveHwnd && !item.InTray && !IsIconic(hwnd) && !Parking.IsParked(hwnd) && !IsHiddenByApp(hwnd) && !HasWindowAbove(hwnd);
    }

    /// <summary>Свернуть, как кнопкой окна. false — Windows не дала (окно от администратора).</summary>
    public bool Minimize(WindowItem item)
    {
        ShowWindow(item.Hwnd, SW_MINIMIZE);
        RefreshSoon();
        return IsIconic(item.Hwnd);
    }

    /// <summary>Выше в стопке есть чужое окно, которое заходит на это. Панель задач, свои и припаркованные окна не в счёт.</summary>
    public static bool HasWindowAbove(IntPtr hwnd)
    {
        GetWindowRect(hwnd, out var me);
        var ownPid = (uint)Environment.ProcessId;

        for (var h = GetWindow(hwnd, GW_HWNDPREV); h != IntPtr.Zero; h = GetWindow(h, GW_HWNDPREV))
        {
            if (!IsWindowVisible(h) || IsCloaked(h) || IsIconic(h) || Parking.IsParked(h))
            {
                continue;
            }

            GetWindowThreadProcessId(h, out var pid);

            if (pid == ownPid || GetClassName(h) is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            {
                continue;
            }

            GetWindowRect(h, out var r);

            if (r.Left < me.Right && r.Right > me.Left && r.Top < me.Bottom && r.Bottom > me.Top && r.Right - r.Left > 50 && r.Bottom - r.Top > 50)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Окно upper выше в стопке, чем lower.</summary>
    public static bool IsAbove(IntPtr upper, IntPtr lower)
    {
        for (var h = GetWindow(lower, GW_HWNDPREV); h != IntPtr.Zero; h = GetWindow(h, GW_HWNDPREV))
        {
            if (h == upper)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// На следующий монитор по кругу и на передний план. Вернёт номер монитора; null — мониторов один
    /// или Windows не дала перенести.
    /// </summary>
    public int? MoveToNextMonitor(WindowItem item)
    {
        var hwnd = item.Hwnd;
        var all = Monitors.All();

        if (all.Count < 2)
        {
            return null;
        }

        if (Parking.IsParked(hwnd))
        {
            Parking.Unpark(hwnd, minimize: false);
        }

        if (IsIconic(hwnd) && !IsHiddenByApp(hwnd))
        {
            ShowWindow(hwnd, SW_RESTORE);
        }

        var number = Monitors.NumberOf(hwnd, all);
        var to = all[number % all.Count];

        if (!Monitors.MoveWindow(hwnd, all[number - 1], to))
        {
            return null;
        }

        Activate(item);
        RefreshSoon();
        return to.Number;
    }

    /// <summary>Попросить окно закрыться — как крестиком: программа может спросить про несохранённое.
    /// false — просьба не дошла: окно запущено от администратора (Диспетчер задач и т.п.),
    /// а Windows не пускает сообщения от программы с меньшими правами.</summary>
    public bool Close(WindowItem item)
    {
        if (Parking.IsParked(item.Hwnd))
        {
            Parking.Unpark(item.Hwnd, minimize: true);
        }

        var sent = PostMessage(item.Hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        if (!sent)
        {
            AppLog.Info($"Окно «{item.Title}» ({item.App}) не приняло просьбу закрыться, код {Marshal.GetLastWin32Error()} (5 — запущено от администратора).");
        }

        RefreshSoon();
        return sent;
    }

    // ── иконки ───────────────────────────────────────────────────────────────

    private ImageSource? IconOf(IntPtr hwnd, string path)
    {
        var handle = IntPtr.Zero;

        foreach (var kind in new[] { ICON_BIG, ICON_SMALL2, ICON_SMALL })
        {
            if (SendMessageTimeout(hwnd, WM_GETICON, new IntPtr(kind), IntPtr.Zero, SMTO_ABORTIFHUNG, 100, out handle) != IntPtr.Zero
                && handle != IntPtr.Zero)
            {
                break;
            }

            handle = IntPtr.Zero;
        }

        if (handle == IntPtr.Zero)
        {
            handle = GetClassLongPtr(hwnd, GCLP_HICON);
        }

        if (handle != IntPtr.Zero)
        {
            var image = FromHandle(handle, destroy: false);

            if (image is not null)
            {
                return image;
            }
        }

        return ExeIcon(path);
    }

    /// <summary>Крупная иконка программы из exe — для плитки, у которой ещё нет превью.</summary>
    public ImageSource? ExeIcon(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        if (_exeIcons.TryGetValue(path, out var cached))
        {
            return cached;
        }

        ImageSource? image = null;
        var large = new IntPtr[1];

        if (ExtractIconEx(path, 0, large, null, 1) > 0 && large[0] != IntPtr.Zero)
        {
            image = FromHandle(large[0], destroy: true);
        }

        _exeIcons[path] = image;
        return image;
    }

    private static ImageSource? FromHandle(IntPtr handle, bool destroy)
    {
        try
        {
            var image = Imaging.CreateBitmapSourceFromHIcon(handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (destroy)
            {
                DestroyIcon(handle);
            }
        }
    }
}
