using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MultiWindowSwitcher.Core;
using MultiWindowSwitcher.Models;
using MultiWindowSwitcher.Native;
using MultiWindowSwitcher.Services;
using MultiWindowSwitcher.Views.Controls;
using MultiWindowSwitcher.Views.Dialogs;
using Wpf.Ui.Controls;
using static MultiWindowSwitcher.Native.Win32;
using MenuItem = System.Windows.Controls.MenuItem;

namespace MultiWindowSwitcher.Views;

/// <summary>Почему окно показывается: от этого зависит, когда оно имеет право само уехать.</summary>
public enum ShowReason
{
    Startup,
    Explicit,
    Hover,
    Corner,
}

/// <summary>
/// Переключатель: все окна Windows живыми превью в ровной сетке.
///
/// Окно выезжает от края (плавающая кнопка, активный угол, трей) поверх любой программы,
/// ничего не сдвигая. Если включено «Прятать за иконку», оно уезжает обратно, когда выбрано
/// окно или мышь отошла дальше запасной зоны в 32 px — чтобы можно было схватить край и
/// поменять размер. Пока человек тянет край, вводит имя или открыто меню — окно стоит.
/// </summary>
public partial class MainWindow : FluentWindow, ITileHost
{
    private const int Zone = 32;
    private const int GraceRadius = 260;
    private const int SlideDistance = 70;
    private static readonly TimeSpan SlideTime = TimeSpan.FromMilliseconds(150);

    private readonly Dictionary<WindowItem, WindowTile> _tiles = new();
    private readonly DispatcherTimer _watch = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly DispatcherTimer _slow = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private readonly DispatcherTimer _toast = new() { Interval = TimeSpan.FromSeconds(2.6) };
    private readonly DispatcherTimer _anim = new() { Interval = TimeSpan.FromMilliseconds(8) };
    private readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    private IntPtr _hwnd;
    private string _filter = "all";
    private int _monitorFilter;
    private string _query = string.Empty;
    private WindowTile? _peek;
    private readonly PreviewWatch _previewWatch = new();
    private int _slowTicks;
    private WindowTile? _renaming;
    private Point _dragGrab;
    private List<UIElement> _dragOrder = new();
    private bool _following;
    private bool _renameCancel;
    private bool _menuOpen;
    private bool _dialogOpen;
    private bool _resizing;
    private bool _armed;
    private POINT? _grace;
    private char _side = 'R';
    private bool _hiding;
    private DateTime _animStart;
    private int _animFrom;
    private int _animTo;
    private int _animY;
    private Action? _animDone;
    private bool _suppressUi;
    private RECT _target;

    public MainWindow()
    {
        InitializeComponent();

        _watch.Tick += (_, _) => Watch();
        _slow.Tick += (_, _) => SlowTick();
        _toast.Tick += (_, _) =>
        {
            _toast.Stop();
            ToastText.Text = string.Empty;
        };
        _anim.Tick += (_, _) => AnimateStep();
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            HideSwitcher(_side);
        };

        Closing += OnClosingWindow;
        StateChanged += OnStateChangedWindow;
        Tiles.LayoutUpdated += (_, _) => UpdateThumbnails(false);
        RenamePopup.Closed += (_, _) => FinishRename();
        SizeChanged += (_, _) => FitBars();
        // окошко «Вид»: значения всегда из настроек, что бы ползунки ни натворили при показе
        TitlesPopup.Opened += (_, _) => ApplyTitleStyle();
        Loaded += (_, _) => FitBars();
        // закрыли окошко, не нажав мышью, — запись нажатия отменяется
        CornersPopup.Closed += (_, _) => App.Current.Gestures?.CancelCapture();
        SettingsPopup.Closed += (_, _) => App.Current.Gestures?.CancelCapture();
        SettingsPopup.Opened += (_, _) => BuildSettings();
        WireDockMenu();
    }

    private WindowTracker Tracker => App.Current.Tracker;

    private IconOverlay? _icons;

    /// <summary>Выход: несохранённые снимки окон из трея — на диск.</summary>
    public void SaveSnapshots() => _snapshots?.Stop();
    private Snapshots _snapshots = null!;
    private readonly DispatcherTimer _snapshotAge = new() { Interval = TimeSpan.FromSeconds(30) };

    private void SyncIcons() => _icons?.Sync(_tiles.Values);

    public bool IsShown { get; private set; }

    private bool _inTaskbar;
    private bool _noSlide;

    /// <summary>Человек что-то делает в окне — уезжать нельзя.</summary>
    public bool IsBusy =>
        SettingsPopup.IsOpen || TitlesPopup.IsOpen || CornersPopup.IsOpen || RenamePopup.IsOpen || _menuOpen || _dialogOpen || _resizing
        || SearchBox.IsKeyboardFocusWithin || _tiles.Values.Any(t => t.IsMouseCaptured);

    /// <summary>Создать окно заранее: нужен его HWND для превью и для сообщений Windows.</summary>
    public void Prepare()
    {
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);

        // «Окно-инструмент»: Chrome и Edge не считают такие окна закрывающими их и продолжают
        // рисовать под переключателем — иначе их превью чернеют. На панели задач нас и так нет.
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(GetExStyle(_hwnd) | WS_EX_TOOLWINDOW));

        // снимки окон, которые прячутся в трей: живого превью у спрятанного окна нет
        _snapshots = new Snapshots(() => Tracker.Windows);
        // снимок один на программу: у свёрнутого окна программы с несколькими окнами он был бы чужой — там живое превью
        WindowTile.SnapshotOf = item => item.InTray || Tracker.Windows.Count(w => w.App == item.App) <= 1 ? _snapshots.Of(item) : null;
        _snapshots.Updated += RepaintSnapshots;
        _snapshots.Start();
        _snapshotAge.Tick += (_, _) => RepaintSnapshots();
        _snapshotAge.Start();

        // крупные значки — в своём прозрачном окне над превью; едет вместе с переключателем
        _icons = new IconOverlay(this);
        LocationChanged += (_, _) => SyncIcons();
        IsVisibleChanged += (_, _) => SyncIcons();
        StateChanged += (_, _) => SyncIcons();

        _suppressUi = true;
        AutoHideSwitch.IsChecked = Settings.Current.AutoHide;
        DedupSwitch.IsChecked = Settings.Current.CloseDuplicateFolders;
        TopmostSwitch.IsChecked = Settings.Current.Topmost;
        ApplyTitleStyle();
        BuildTitleColors();
        Topmost = Settings.Current.Topmost;
        Tiles.Aspect = Settings.Current.Aspect;
        Tiles.IsWide = e => e is WindowTile t && t.Item.IsWide;
        AspectBox.SelectedItem = AspectBox.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(i => Math.Abs(double.Parse((string)i.Tag, System.Globalization.CultureInfo.InvariantCulture) - Settings.Current.Aspect) < 0.01)
            ?? AspectBox.Items[0];
        _suppressUi = false;

        Title = $"Multi Window Switcher {AppVersion.Version}";
        Bar.Title = Title;
        DockMenuTitle.Text = Title;

        Tracker.Changed += Rebuild;
        Tracker.Changed += FitWindowsToStrip;
        Tracker.MoveSizeEnded += hwnd =>
        {
            _stripFit.Moved(hwnd);
            FitWindowsToStrip();
        };
        Tracker.Flashed += _ => App.Current.Fab.SetAttention(true);
        Tracker.TrayOpening += item => Toast(Loc.F("ToastTrayOpening", item.AppTitle));
        UpdateService.StateChanged += PaintUpdate;
        Loc.I.LanguageChanged += OnLanguageChanged;

        FillMonitors();
        PaintHints();
        _slow.Start();
    }

    // ── показать / спрятать ──────────────────────────────────────────────────

    public void ShowSwitcher(char side, ShowReason reason, Point? from = null)
    {
        _hideTimer.Stop();

        // свёрнуто на панель задач — развернуть; дальше покажет OnStateChangedWindow
        if (_inTaskbar)
        {
            WindowState = WindowState.Normal;
            return;
        }

        if (IsShown && !_hiding)
        {
            Raise(reason);
            return;
        }

        _anim.Stop();
        _hiding = false;
        _side = side;

        if (Docked)
        {
            ShowDocked(reason);
            return;
        }


        var cursor = CursorPos();
        var anchor = from is { } f ? new POINT { X = (int)f.X, Y = (int)f.Y } : cursor;
        var monitor = Settings.Current.FollowMouse || reason == ShowReason.Corner
            ? Monitors.FromPoint(anchor.X, anchor.Y)
            : Monitors.Primary();
        var target = TargetRect(monitor);
        AppLog.Info($"Показ: {reason}, край {side}, монитор {monitor.Number}, {target.Width}×{target.Height} в ({target.Left}, {target.Top}).");
        // от иконки у края — выезжает сбоку; из активного угла — просто появляется на своём месте
        _noSlide = reason == ShowReason.Corner;
        var offset = _noSlide ? 0 : (int)(SlideDistance * monitor.Scale) * (side == 'L' ? -1 : 1);
        _target = target;

        SetWindowPos(_hwnd, IntPtr.Zero, target.Left + offset, target.Top, target.Width, target.Height, SWP_NOZORDER | SWP_NOACTIVATE);

        // невидимые «живые» свёрнутые окна — наверх стопки: их не видно и клики идут сквозь них,
        // а браузер, который сверху ничем не закрыт, продолжает рисовать — превью живое
        Parking.RaiseAll();

        if (!IsVisible)
        {
            Show();
        }

        // смена монитора с другим масштабом могла поменять размер — ставим ещё раз
        SetWindowPos(_hwnd, IntPtr.Zero, target.Left + offset, target.Top, target.Width, target.Height, SWP_NOZORDER | SWP_NOACTIVATE);
        Raise(reason);

        IsShown = true;
        _armed = reason == ShowReason.Hover;
        _grace = reason == ShowReason.Corner ? anchor : null;

        FillMonitors();
        Rebuild();

        // смена масштаба монитора при показе приходит с опозданием — ещё раз ставим свой размер
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            SetWindowPos(_hwnd, IntPtr.Zero, target.Left + offset, target.Top, target.Width, target.Height, SWP_NOZORDER | SWP_NOACTIVATE);
            Slide(target.Left + offset, target.Left, target.Top, () => UpdateThumbnails(true));
        }));

        App.Current.Fab.SetShown(true);
        _watch.Start();
    }

    public void HideSwitcher(char? side = null)
    {
        _hideTimer.Stop();

        if (!IsShown || _hiding)
        {
            return;
        }

        SettingsPopup.IsOpen = false;
        TitlesPopup.IsOpen = false;
        CornersPopup.IsOpen = false;
        RenamePopup.IsOpen = false;
        Unpeek();

        if (Docked)
        {
            HideDocked();
            return;
        }

        _hiding = true;
        _watch.Stop();
        AppLog.Info("Скрытие" + (side is { } why ? $" к краю {why}." : "."));
        EdgeFrame.BorderBrush = Brushes.Transparent;

        var s = side ?? _side;
        GetWindowRect(_hwnd, out var rect);
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var offset = _noSlide ? 0 : (int)(SlideDistance * scale) * (s == 'L' ? -1 : 1);

        Slide(rect.Left, rect.Left + offset, rect.Top, () =>
        {
            Hide();
            _hiding = false;
            IsShown = false;
            App.Current.Fab.SetShown(false);
        });
    }

    /// <summary>
    /// Клик по значку в трее: открыто и видно — спрятать; открыто, но закрыто другими окнами, —
    /// вывести на передний план; спрятано — показать (тоже на передний план).
    /// </summary>
    public void ToggleFromTray()
    {
        if (IsShown && !_hiding && !IsCovered())
        {
            AppLog.Info("Значок в трее: спрятать.");
            HideSwitcher();
            return;
        }

        AppLog.Info("Значок в трее: показать.");
        ShowSwitcher(App.Current.Fab.Side, ShowReason.Explicit);
    }

    /// <summary>
    /// Вызвали явно — кнопкой мыши, углом, из трея — окно всегда поверх всех и активное.
    /// По наведению на иконку — тоже наверх, но без перехвата фокуса у текущей программы.
    /// </summary>
    private void Raise(ShowReason reason)
    {
        if (reason == ShowReason.Hover)
        {
            BringToFront();
        }
        else
        {
            ForceToFront();
        }
    }

    /// <summary>Над окном переключателя есть чужое видимое окно, которое его перекрывает.</summary>
    /// <summary>В ленте — только по плиткам: чужое окно над пустой частью полосы ничего не закрывает.</summary>
    public bool IsCovered() => WindowTracker.HasWindowAbove(_hwnd, _railOn ? _stripRegion : 0);

    /// <summary>
    /// Вызвали явно (трей) — окно поверх всех и активное. Короткий «поверх всех» поднимает его над
    /// любым окном, даже если Windows не даёт активировать; потом возвращаем как в настройке.
    /// </summary>
    private void ForceToFront()
    {
        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);

        if (!Topmost)
        {
            SetWindowPos(_hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        if (!SetForegroundWindow(_hwnd) || GetForegroundWindow() != _hwnd)
        {
            keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
            keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            SetForegroundWindow(_hwnd);
        }
    }

    public void OpenSettings()
    {
        ShowSwitcher(App.Current.Fab.Side, ShowReason.Explicit);
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => SettingsPopup.IsOpen = true));
    }

    public void OpenUpdates()
    {
        ShowSwitcher(App.Current.Fab.Side, ShowReason.Explicit);
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(ShowUpdateWindow));
    }

    private void BringToFront()
    {
        var after = Topmost ? HWND_TOPMOST : HWND_TOP;
        SetWindowPos(_hwnd, after, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    /// <summary>Где стоять на этом мониторе: как в прошлый раз (доли рабочей области) или по центру.</summary>
    private static RECT TargetRect(MonitorInfo monitor)
    {
        var work = monitor.Work;
        var p = Settings.Current.Window;
        double x, y, w, h;

        if (p is { W: > 0.1, H: > 0.1 })
        {
            (x, y, w, h) = (p.X, p.Y, Math.Min(p.W, 1), Math.Min(p.H, 1));
        }
        else
        {
            (w, h) = (0.8, 0.82);
            (x, y) = ((1 - w) / 2, (1 - h) / 2);
        }

        var left = work.Left + (int)(x * work.Width);
        var top = work.Top + (int)(y * work.Height);
        var width = Math.Max((int)(440 * monitor.Scale), (int)(w * work.Width));
        var height = Math.Max((int)(320 * monitor.Scale), (int)(h * work.Height));

        // окно целиком на экране, даже если монитор стал меньше
        left = Math.Clamp(left, work.Left, Math.Max(work.Left, work.Right - width));
        top = Math.Clamp(top, work.Top, Math.Max(work.Top, work.Bottom - height));

        return new RECT(left, top, left + Math.Min(width, work.Width), top + Math.Min(height, work.Height));
    }

    /// <summary>Запомнить, где и какого размера окно, — в долях монитора.</summary>
    public void SavePlacement()
    {
        // у ленты своё место: положение обычного окна остаётся на потом, когда ленту выключат
        if (_hwnd == IntPtr.Zero || !IsShown || _hiding || Docked)
        {
            return;
        }

        GetWindowRect(_hwnd, out var rect);
        var monitor = Monitors.FromWindow(_hwnd);
        var work = monitor.Work;

        Settings.Current.Window = new Placement
        {
            Device = monitor.Device,
            X = (rect.Left - work.Left) / (double)work.Width,
            Y = (rect.Top - work.Top) / (double)work.Height,
            W = rect.Width / (double)work.Width,
            H = rect.Height / (double)work.Height,
        };

        Settings.Touch();
    }

    // ── анимация выезда ──────────────────────────────────────────────────────

    private void Slide(int from, int to, int y, Action? done)
    {
        _animFrom = from;
        _animTo = to;
        _animY = y;
        _animDone = done;
        _animStart = DateTime.UtcNow;
        _anim.Start();
        AnimateStep();
    }

    private void AnimateStep()
    {
        var t = Math.Min(1, (DateTime.UtcNow - _animStart).TotalMilliseconds / SlideTime.TotalMilliseconds);
        var eased = 1 - Math.Pow(1 - t, 3);
        var x = (int)Math.Round(_animFrom + (_animTo - _animFrom) * eased);

        SetWindowPos(_hwnd, IntPtr.Zero, x, _animY, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

        if (t >= 1)
        {
            _anim.Stop();
            var done = _animDone;
            _animDone = null;
            done?.Invoke();
        }
    }

    // ── уехать, когда мышь ушла ──────────────────────────────────────────────

    private void Watch()
    {
        // лента стоит на месте, пока её не спрячут явно
        if (Docked)
        {
            WatchDockMenu();
            return;
        }

        if (!IsShown || _hiding || _anim.IsEnabled)
        {
            return;
        }

        var c = CursorPos();
        GetWindowRect(_hwnd, out var r);
        var zone = (int)(Zone * VisualTreeHelper.GetDpi(this).DpiScaleX);
        var inWindow = r.Contains(c.X, c.Y);
        var inZone = new RECT(r.Left - zone, r.Top - zone, r.Right + zone, r.Bottom + zone).Contains(c.X, c.Y);
        var overFab = App.Current.Fab.ContainsScreenPoint(c.X, c.Y);
        var autoHide = Settings.Current.AutoHide;

        // мышь в запасной зоне — рамка подсвечена: ещё чуть дальше, и окно уедет
        EdgeFrame.BorderBrush = autoHide && !inWindow && inZone && _armed
            ? (Brush)FindResource("BrandBrush")
            : Brushes.Transparent;

        if (inZone || overFab)
        {
            _armed = true;
            _grace = null;
            _hideTimer.Stop();
            return;
        }

        if (!autoHide || IsBusy || IsKeyDown(VK_LBUTTON) || IsKeyDown(VK_RBUTTON))
        {
            _hideTimer.Stop();
            return;
        }

        if (!_armed)
        {
            // открыли углом: пока мышь идёт от угла к окну, не уезжаем
            if (_grace is { } g)
            {
                var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;

                if (Math.Sqrt(Math.Pow(c.X - g.X, 2) + Math.Pow(c.Y - g.Y, 2)) < GraceRadius * scale)
                {
                    return;
                }

                _grace = null;
            }
            else
            {
                // открыли кнопкой, из трея или при запуске — ждём, пока мышь хотя бы раз зайдёт в окно
                return;
            }
        }

        if (!_hideTimer.IsEnabled)
        {
            _hideTimer.Start();
        }
    }

    private void SlowTick()
    {
        UpdateThumbnails(true);

        var alive = Tracker.Windows.Where(w => !w.HiddenByUser).ToList();
        App.Current.Fab.SetCount(alive.Count);
        App.Current.Fab.SetAttention(alive.Any(w => w.Attention));
        PaintCounts();

        // окно развернули кнопкой или программа сама сдвинула его: об этом Windows отдельно не сообщает
        FitWindowsToStrip();

        _previewWatch.Calm();

        // раз в ~1,4 с: что происходит в окнах не на виду (только в ленте — она всегда на экране); раз в ~3 с — не опустело ли
        // какое-нибудь превью. Смотрим на экран — только когда нас ничего не закрывает и мышь не над лентой (там всплывает лишнее)
        if (++_slowTicks % 2 == 0 && IsShown && IsVisible && !IsBusy && _dragOrder.Count == 0 && !Tiles.IsMoving && !IsCovered())
        {
            var blank = _slowTicks % 4 == 0;
            var motion = _railOn && !DockMenu.IsOpen && !IsMouseOver;

            if (blank || motion)
            {
                var origin = PointToScreen(new Point(0, 0));
                _previewWatch.Check(_tiles.Values.ToList(), (int)origin.X, (int)origin.Y, blank, motion);
            }
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (DockMessage(hwnd, msg, wParam, lParam, ref handled) is { } answer)
        {
            return answer;
        }

        switch (msg)
        {
            case WM_ENTERSIZEMOVE:
                _resizing = true;
                break;

            case WM_DPICHANGED when !_resizing && _target.Width > 0:
                // Windows предлагает свой размер под новый масштаб; нам нужен наш — в пикселях монитора
                var suggested = System.Runtime.InteropServices.Marshal.PtrToStructure<RECT>(lParam);
                System.Runtime.InteropServices.Marshal.StructureToPtr(
                    new RECT(suggested.Left, _target.Top, suggested.Left + _target.Width, _target.Top + _target.Height), lParam, false);
                break;

            case WM_EXITSIZEMOVE:
                _resizing = false;
                _armed = true;

                if (Docked)
                {
                    FinishDockResize();
                }
                else
                {
                    SavePlacement();
                }

                break;
        }

        return IntPtr.Zero;
    }

    private void OnClosingWindow(object? sender, CancelEventArgs e)
    {
        // крестик только прячет: выход — кнопкой «Выход», из трея или меню иконки у края
        if (!App.Current.IsExiting)
        {
            AppLog.Info("Крестик окна: спрятано в трей.");
            e.Cancel = true;
            HideSwitcher();
        }
    }

    /// <summary>
    /// «Свернуть» — как у обычного окна: на панель задач. Пока окно свёрнуто, оно обычное
    /// (с кнопкой на панели задач); развернули — снова «окно-инструмент» без кнопки.
    /// </summary>
    private void OnStateChangedWindow(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && !_inTaskbar)
        {
            AppLog.Info("Свёрнуто на панель задач.");
            _inTaskbar = true;
            _watch.Stop();
            _hideTimer.Stop();
            SettingsPopup.IsOpen = false;
            TitlesPopup.IsOpen = false;
            IsShown = false;
            App.Current.Fab.SetShown(false);

            ShowWindow(_hwnd, SW_HIDE);
            SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr((GetExStyle(_hwnd) & ~WS_EX_TOOLWINDOW) | WS_EX_APPWINDOW));
            ShowWindow(_hwnd, SW_SHOWMINNOACTIVE);
        }
        else if (WindowState != WindowState.Minimized && _inTaskbar)
        {
            AppLog.Info("Развёрнуто с панели задач.");
            _inTaskbar = false;
            ShowWindow(_hwnd, SW_HIDE);
            SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr((GetExStyle(_hwnd) & ~WS_EX_APPWINDOW) | WS_EX_TOOLWINDOW));
            ShowSwitcher(App.Current.Fab.Side, ShowReason.Explicit);
        }
    }

    private void OnExitClick(object sender, RoutedEventArgs e) => App.Current.Quit("кнопка «Выход» в заголовке");

    /// <summary>Строка состояния для периодического отчёта в журнале.</summary>
    public string StateLine() =>
        $"Окон {Tracker.Windows.Count} (скрыто {Tracker.Windows.Count(w => w.HiddenByUser)}), плиток {_tiles.Count}, превью {_tiles.Values.Count(t => t.HasThumbnail)}, «живых» свёрнутых {Parking.Count}, окно {(IsShown ? "показано" : _inTaskbar ? "на панели задач" : "спрятано")}.";

    // ── сетка ────────────────────────────────────────────────────────────────

    /// <summary>Сверить плитки со списком окон, применить фильтры и порядок.</summary>
    private void Rebuild()
    {
        // во время перетаскивания порядок живой — список окон перестроится после отпускания
        if (_dragOrder.Count > 0)
        {
            return;
        }

        var windows = Tracker.Windows;

        foreach (var gone in _tiles.Keys.Where(w => !windows.Contains(w)).ToList())
        {
            var tile = _tiles[gone];
            tile.Detach();
            ForgetHover(tile);
            Tiles.Children.Remove(tile);
            _tiles.Remove(gone);

            if (_peek == tile)
            {
                _peek = null;
                Tiles.Peek = null;
            }
        }

        foreach (var item in windows)
        {
            if (_tiles.ContainsKey(item))
            {
                continue;
            }

            var tile = new WindowTile(item, this);
            tile.Loaded += (_, _) => tile.Attach(_hwnd);
            _tiles[item] = tile;
            Tiles.Children.Add(tile);
        }

        var visible = windows
            .Where(w => w.HiddenByUser == (_filter == "hidden"))
            .Where(w => _filter switch
            {
                "open" => !w.IsMinimized,
                "min" => w.IsMinimized,
                _ => true,
            })
            .Where(w => _monitorFilter == 0 || w.Monitor == _monitorFilter)
            .Where(w => _query.Length == 0 || w.Matches(_query))
            .OrderByDescending(w => w.IsPinned)
            .ThenBy(w => w.Order)
            .ToList();

        foreach (var (item, tile) in _tiles)
        {
            tile.Visibility = visible.Contains(item) ? Visibility.Visible : Visibility.Collapsed;
        }

        Tiles.Ordered = visible.Select(w => (UIElement)_tiles[w]).ToList();
        EmptyText.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = Loc.T(_filter == "hidden" ? "EmptyHidden" : "Empty");

        PaintCounts();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            GridText.Text = Tiles.Columns > 0 ? Loc.F("StatusGrid", Tiles.Columns, Tiles.Rows) : string.Empty;
            UpdateThumbnails(true);
        }));
    }

    private void UpdateThumbnails(bool querySize)
    {
        foreach (var tile in _tiles.Values)
        {
            tile.UpdateThumbnail(querySize);
        }

        SyncIcons();
    }

    private void PaintCounts()
    {
        var all = Tracker.Windows.Where(w => !w.HiddenByUser).ToList();
        var minimized = all.Count(w => w.IsMinimized);
        var events = all.Count(w => w.Attention);

        var hidden = Tracker.Windows.Count(w => w.HiddenByUser);
        CountAll.Text = all.Count.ToString();
        CountHidden.Text = hidden.ToString();

        // «Скрытые» — только когда есть что показать; последнее вернули — назад ко «Всем»
        var hiddenVisibility = hidden > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (FilterHidden.Visibility != hiddenVisibility)
        {
            FilterHidden.Visibility = hiddenVisibility;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(FitBars));
        }

        if (hidden == 0 && FilterHidden.IsChecked == true)
        {
            FilterAll.IsChecked = true;
        }

        StatusText.Text = Loc.F("StatusCount", all.Count, minimized) + (events > 0 ? Loc.F("StatusEvents", events) : string.Empty);
    }

    private void PaintHints() =>
        HintsText.Text = string.Join("   ·   ", new[] { "HintName", "HintWheel", "HintMiddle", "HintRight", "HintDrag" }.Select(Loc.T));

    private void FillMonitors()
    {
        var count = Monitors.All().Count;
        var selected = _monitorFilter;

        _suppressUi = true;
        MonitorBox.Items.Clear();
        MonitorBox.Items.Add(new ComboBoxItem { Content = Loc.T("AllMonitors"), Tag = 0 });

        for (var i = 1; i <= count; i++)
        {
            MonitorBox.Items.Add(new ComboBoxItem { Content = Loc.F("MonitorN", i), Tag = i });
        }

        if (selected > count)
        {
            selected = 0;
            _monitorFilter = 0;
        }

        MonitorBox.SelectedIndex = selected;
        MonitorBox.Visibility = count > 1 ? Visibility.Visible : Visibility.Collapsed;
        WindowTile.ShowMonitor = count > 1;
        _suppressUi = false;
    }

    // ошибка — мягким красным, как в тёмной теме Windows 11
    private static readonly Brush ErrorBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x99, 0xA4)));

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    private void Toast(string text, bool error = false)
    {
        ToastText.Text = text;
        if (error)
        {
            ToastText.Foreground = ErrorBrush;
        }
        else
        {
            ToastText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "BrandBrush");
        }

        _toast.Stop();
        _toast.Start();
    }

    /// <summary>
    /// Узкое окно: ничего не налезает друг на друга. Сначала у кнопок панели остаются только значки,
    /// потом у переключателей в заголовке — только сами переключатели. Подсказки при наведении остаются.
    /// </summary>
    private bool _toolbarFull = true, _barFull = true;

    private void FitBars()
    {
        if (!IsLoaded || _railOn)
        {
            return;
        }

        // запас, чтобы на самой границе подписи не мигали туда-сюда
        const double hysteresis = 24;

        var row = ((FrameworkElement)Toolbar.Parent).ActualWidth;
        var need = FullWidth(Toolbar, ToolbarLabels, _toolbarFull) + Toolbar.Margin.Left + Toolbar.Margin.Right;
        _toolbarFull = need <= row - (_toolbarFull ? 0 : hysteresis);
        ToolbarLabels(_toolbarFull);

        // в заголовке слева ещё значок и название программы, справа три системные кнопки
        var bar = Bar.ActualWidth - 420;
        var barNeed = FullWidth(BarTrailing, BarLabels, _barFull);
        _barFull = barNeed <= bar - (_barFull ? 0 : hysteresis);
        BarLabels(_barFull);
    }

    /// <summary>Ширина ряда с подписями: каждый видимый элемент меряем сам по себе.</summary>
    private static double FullWidth(Panel panel, Action<bool> labels, bool current)
    {
        if (!current)
        {
            labels(true);
        }

        var sum = 0.0;

        foreach (UIElement child in panel.Children)
        {
            if (child.Visibility == Visibility.Collapsed)
            {
                continue;
            }

            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            sum += child.DesiredSize.Width;
        }

        if (!current)
        {
            labels(false);
        }

        return sum;
    }

    private void ToolbarLabels(bool on)
    {
        var v = on ? Visibility.Visible : Visibility.Collapsed;
        CornersLabel.Visibility = v;
        TitlesLabel.Visibility = v;
        GroupLabel.Visibility = v;
    }

    private void BarLabels(bool on)
    {
        var v = on ? Visibility.Visible : Visibility.Collapsed;
        DedupLabel.Visibility = v;
        AutoHideLabel.Visibility = v;
        TopmostLabel.Visibility = v;
        ExitLabel.Visibility = v;
        ExitIcon.Margin = new Thickness(0, 0, on ? 6 : 0, 0);
    }

    private void OnLanguageChanged()
    {
        FitBars();
        FillMonitors();
        PaintHints();
        PaintCounts();
        PaintUpdate();
        Rebuild();
    }

    // ── панель ───────────────────────────────────────────────────────────────

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        _query = SearchBox.Text.Trim();
        Rebuild();
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        // срабатывает ещё при загрузке разметки, когда остальных кнопок пока нет
        if (FilterHidden is null)
        {
            return;
        }

        _filter = FilterHidden.IsChecked == true ? "hidden" : "all";

        if (IsLoaded)
        {
            Rebuild();
        }
    }

    private void OnMonitorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUi || MonitorBox.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        _monitorFilter = (int)item.Tag;
        Rebuild();
    }

    private void OnAspectChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUi || AspectBox.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        Settings.Current.Aspect = double.Parse((string)item.Tag, System.Globalization.CultureInfo.InvariantCulture);
        Tiles.Aspect = Settings.Current.Aspect;
        Tiles.InvalidateMeasure();
        Settings.Touch();
    }

    private void OnAutoHideToggle(object sender, RoutedEventArgs e)
    {
        Settings.Current.AutoHide = AutoHideSwitch.IsChecked == true;
        Settings.Touch();
        _armed = false;
        Toast(Loc.T(Settings.Current.AutoHide ? "ToastAutoHideOn" : "ToastAutoHideOff"));
    }

    // ── крупные названия ─────────────────────────────────────────────────────

    private static readonly string[] TitlePalette =
        { "#FFFFFF", "#4CA6FF", "#7C5CFF", "#4ECDC4", "#4ADE80", "#FDE68A", "#F59E0B", "#F87171" };

    private void OnTitlesClick(object sender, RoutedEventArgs e) => TitlesPopup.IsOpen = !TitlesPopup.IsOpen;

    private void OnBigTitlesSwitch(object sender, RoutedEventArgs e) => SetBigTitles(BigTitlesSwitch.IsChecked == true);

    private void OnTitlePartChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressUi || !IsLoaded)
        {
            return;
        }

        Settings.Current.TitlePart = PartWindow.IsChecked == true ? "window" : PartApp.IsChecked == true ? "app" : "full";
        Settings.Touch();
        AppLog.Info("Что в названии: " + Settings.Current.TitlePart + ".");
        ApplyTitleStyle();
    }

    private void OnAppFirstSwitch(object sender, RoutedEventArgs e)
    {
        Settings.Current.AppFirst = AppFirstSwitch.IsChecked == true;
        Settings.Touch();
        AppLog.Info($"Перевернуть наименование: {(Settings.Current.AppFirst ? "вкл" : "выкл")}.");
        ApplyTitleStyle();
    }

    private void OnTraySnapshotsSwitch(object sender, RoutedEventArgs e)
    {
        Settings.Current.TraySnapshots = TraySnapshotsSwitch.IsChecked == true;
        Settings.Touch();
        AppLog.Info($"Снимки окон в трее: {(Settings.Current.TraySnapshots ? "вкл" : "выкл")}.");
        RepaintSnapshots();
    }

    /// <summary>Плитки окон в трее: новый снимок или прошла минута — обновить картинку и подпись.</summary>
    private void RepaintSnapshots()
    {
        foreach (var tile in _tiles.Values.Where(t => t.Item.InTray))
        {
            tile.PaintSnapshot();
        }

        SyncIcons();
    }

    private void OnTrayWindowsSwitch(object sender, RoutedEventArgs e)
    {
        Settings.Current.TrayWindows = TrayWindowsSwitch.IsChecked == true;
        TraySnapshotsSwitch.IsEnabled = Settings.Current.TrayWindows;
        Settings.Touch();
        AppLog.Info($"Программы из трея: {(Settings.Current.TrayWindows ? "вкл" : "выкл")}.");
        Tracker.Refresh();
        Rebuild();
    }

    private void OnBigIconsSwitch(object sender, RoutedEventArgs e)
    {
        Settings.Current.BigIcons = BigIconsSwitch.IsChecked == true;
        Settings.Touch();
        AppLog.Info($"Крупные значки: {(Settings.Current.BigIcons ? "вкл" : "выкл")}.");
        SyncIcons();
        RefreshStrip();
    }

    private void OnIconSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (IconSizeText is null)
        {
            return;
        }

        IconSizeText.Text = $"{e.NewValue:0} px";

        if (_suppressUi || !ByHand(IconSizeSlider))
        {
            return;
        }

        // у ленты и обычного окна размер свой
        if (_railOn)
        {
            Settings.Current.StripIconSize = e.NewValue;
        }
        else
        {
            Settings.Current.IconSize = e.NewValue;
        }

        Settings.Touch();
        SyncIcons();
        RefreshStrip();
    }

    private void SetBigTitles(bool value)
    {
        Settings.Current.BigTitles = value;
        Settings.Touch();

        foreach (var tile in _tiles.Values)
        {
            tile.Item.NoBigTitle = false;
        }

        ApplyTitleStyle();
    }

    /// <summary>
    /// Ползунок сдвинул человек — мышью или клавишами. Сам ползунок двигает значение при создании и при
    /// первом показе окошка (встаёт на минимум) — это не выбор, в настройки его не пишем.
    /// </summary>
    private static bool ByHand(Slider slider) => slider.IsMouseCaptureWithin || slider.IsKeyboardFocusWithin;

    private void OnTitleSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TitleSizeText is not null)
        {
            TitleSizeText.Text = $"{e.NewValue:0}%";
        }

        if (_suppressUi || !ByHand(TitleSizeSlider))
        {
            return;
        }

        Settings.Current.TitleScale = TitleSizeSlider.Value / 100;
        Settings.Touch();
        ApplyTitleStyle();
    }

    /// <summary>Цвета текста — кружками; выбранный обведён.</summary>
    private void BuildTitleColors()
    {
        TitleColors.Children.Clear();

        foreach (var hex in TitlePalette)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var chosen = string.Equals(hex, Settings.Current.TitleColor, StringComparison.OrdinalIgnoreCase);
            var swatch = new Border
            {
                Width = 24,
                Height = 24,
                Margin = new Thickness(0, 0, 6, 6),
                CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(color),
                BorderBrush = chosen ? Brushes.White : new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(chosen ? 2.5 : 1),
                Cursor = Cursors.Hand,
            };

            swatch.MouseLeftButtonUp += (_, _) =>
            {
                Settings.Current.TitleColor = hex;
                Settings.Touch();
                ApplyTitleStyle();
                BuildTitleColors();
            };

            TitleColors.Children.Add(swatch);
        }
    }

    /// <summary>Включено ли, размер и цвет — во все плитки, кнопку и панель.</summary>
    private void ApplyTitleStyle()
    {
        var s = Settings.Current;
        WindowTile.BigTitles = s.BigTitles;
        WindowTile.AppFirst = s.AppFirst;
        WindowTile.TitlePart = s.TitlePart is "window" or "app" ? s.TitlePart : "full";
        WindowTile.TitleScale = Math.Clamp(s.TitleScale, 0.5, 2);

        try
        {
            WindowTile.TitleBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(s.TitleColor));
        }
        catch (FormatException)
        {
            WindowTile.TitleBrush = Brushes.White;
        }

        var suppress = _suppressUi;
        _suppressUi = true;
        BigTitlesSwitch.IsChecked = s.BigTitles;
        TitleSizeSlider.Value = Math.Round(WindowTile.TitleScale * 100);
        BigIconsSwitch.IsChecked = s.BigIcons;
        TrayWindowsSwitch.IsChecked = s.TrayWindows;
        TraySnapshotsSwitch.IsChecked = s.TraySnapshots;
        TraySnapshotsSwitch.IsEnabled = s.TrayWindows;
        AppFirstSwitch.IsChecked = s.AppFirst;
        AppFirstSwitch.IsEnabled = WindowTile.TitlePart == "full";
        PartFull.IsChecked = WindowTile.TitlePart == "full";
        PartWindow.IsChecked = WindowTile.TitlePart == "window";
        PartApp.IsChecked = WindowTile.TitlePart == "app";
        DockReserveSwitch.IsChecked = s.DockReserve;
        HoverPreviewSwitch.IsChecked = s.HoverPreview;
        HoverPreviewSizeSlider.Value = Math.Clamp(s.HoverPreviewSize, 30, 90);
        DockWindow.IsChecked = !Docked;
        DockTop.IsChecked = s.Dock == "Top";
        DockBottom.IsChecked = s.Dock == "Bottom";
        // в ленте значок упирается в её высоту: ползунок только до предела, у которого значок ещё растёт
        IconSizeSlider.Maximum = _railOn ? Math.Clamp(Tiles.ActualHeight - 32 - WindowTile.IconPad * 2, 40, 192) : 192;
        IconSizeSlider.Value = Math.Clamp(_railOn ? s.StripIconSize : s.IconSize, 32, IconSizeSlider.Maximum);
        _suppressUi = suppress;

        TitleSizeText.Text = $"{TitleSizeSlider.Value:0}%";
        IconSizeText.Text = $"{IconSizeSlider.Value:0} px";
        SyncIcons();

        foreach (var tile in _tiles.Values)
        {
            tile.Repaint();
        }
    }

    private void OnDedupToggle(object sender, RoutedEventArgs e)
    {
        Settings.Current.CloseDuplicateFolders = DedupSwitch.IsChecked == true;
        Settings.Touch();
        AppLog.Info($"Закрывать дубли папок: {(Settings.Current.CloseDuplicateFolders ? "вкл" : "выкл")}.");
        Toast(Loc.T(Settings.Current.CloseDuplicateFolders ? "ToastDedupOn" : "ToastDedupOff"));
        ExplorerDuplicates.CloseAll();
    }

    private void OnCornersClick(object sender, RoutedEventArgs e)
    {
        if (!CornersPopup.IsOpen)
        {
            CornersHost.Children.Clear();
            CornersHost.Children.Add(new System.Windows.Controls.TextBlock { Text = Loc.T("SetCorners"), FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
            CornersHost.Children.Add(CornerScreen(272, 150));
            CornersHost.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = Loc.T("SetCornersTip"),
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 10, 0, 0),
                Foreground = (Brush)FindResource("TextFillColorTertiaryBrush"),
            });
            CornersHost.Children.Add(new System.Windows.Controls.TextBlock { Text = Loc.T("SetGestures"), FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 6) });
            var mouse = new StackPanel();
            FillMouseBindings(mouse);
            CornersHost.Children.Add(mouse);
        }

        CornersPopup.IsOpen = !CornersPopup.IsOpen;
    }

    private void OnTopmostToggle(object sender, RoutedEventArgs e)
    {
        Settings.Current.Topmost = TopmostSwitch.IsChecked == true;
        Topmost = Settings.Current.Topmost || _railOn;
        Settings.Touch();
    }

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        Tracker.Refresh();
        Rebuild();
        Toast(Loc.T("ToastRefreshed"));
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e) => SettingsPopup.IsOpen = !SettingsPopup.IsOpen;

    /// <summary>
    /// Умная сортировка: окна одной программы рядом. Первыми — программы, у которых окон больше;
    /// внутри группы — окна со своим именем, потом открытые, потом свёрнутые.
    /// </summary>
    private void OnGroup(object sender, RoutedEventArgs e)
    {
        var groups = Tracker.Windows
            .OrderBy(w => w.Order)
            .GroupBy(w => w.App)
            .Select(g => (Items: g.ToList(), First: g.Min(w => w.Order)))
            .OrderByDescending(g => g.Items.Count)
            .ThenBy(g => g.First)
            .ToList();

        var order = 0;

        foreach (var (items, _) in groups)
        {
            foreach (var w in items.OrderBy(w => (w.HasName ? 0 : 2) + (w.IsMinimized ? 1 : 0)).ThenBy(w => w.Order))
            {
                w.Order = order += 10;
            }
        }

        Rebuild();

        var many = groups.Where(g => g.Items.Count > 1).Select(g => $"{g.Items[0].AppTitle} ×{g.Items.Count}").ToList();
        Toast(many.Count > 0 ? Loc.F("ToastGrouped", string.Join(", ", many)) : Loc.T("ToastGroupedNone"));
    }

    // ── обновление ───────────────────────────────────────────────────────────

    private DispatcherTimer? _latestTimer;

    private void PaintUpdate()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(FitBars));
        UpdateButton.Visibility = Visibility.Collapsed;
        UpdateStatus.Visibility = Visibility.Collapsed;

        switch (UpdateService.State)
        {
            case UpdateCheckState.Checking:
                UpdateStatus.Text = Loc.T("UpdChecking");
                UpdateStatus.Visibility = Visibility.Visible;
                break;

            case UpdateCheckState.Available when UpdateService.Available is { } release:
                UpdateButton.Content = Loc.F("UpdAvailable", release.Version);
                UpdateButton.Visibility = Visibility.Visible;
                break;

            case UpdateCheckState.UpToDate:
                // «последняя версия» видна несколько секунд и уходит — шапка не должна шуметь
                UpdateStatus.Text = "✓ " + Loc.T("UpdLatest");
                UpdateStatus.Visibility = Visibility.Visible;
                _latestTimer?.Stop();
                _latestTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                _latestTimer.Tick += (_, _) =>
                {
                    _latestTimer.Stop();
                    UpdateStatus.Visibility = Visibility.Collapsed;
                };
                _latestTimer.Start();
                break;
        }
    }

    private void OnUpdateClick(object sender, RoutedEventArgs e) => ShowUpdateWindow();

    private void ShowUpdateWindow()
    {
        _dialogOpen = true;

        try
        {
            UpdateWindow.Open(this);
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    // ── ITileHost: что делают плитки ─────────────────────────────────────────

    /// <summary>Клик по плитке: как на панели задач — окно уже впереди сворачивается, иначе выходит вперёд.</summary>
    public void TileClicked(WindowItem item)
    {
        HideHover();

        if (!Tracker.IsInFront(item) || IsHiddenBehindMe(item))
        {
            SwitchTo(item);
            return;
        }

        Unpeek();

        if (Tracker.Minimize(item))
        {
            AppLog.Info($"Свёрнуто кликом по плитке: «{item.Title}» ({item.App}).");
            Toast(Loc.F("ToastMinimized", item.Caption));
        }
        else
        {
            AppLog.Warn($"Окно не свернулось: «{item.Title}» ({item.App}).");
            Toast(Loc.F("ToastMinimizeDenied", item.Caption));
        }
    }

    /// <summary>Переключатель открыт поверх окна и закрывает его целиком: человек его не видит, клик значит «вернуться к нему».</summary>
    private bool IsHiddenBehindMe(WindowItem item)
    {
        GetWindowRect(_hwnd, out var me);
        var r = GetVisibleBounds(item.Hwnd);
        return WindowTracker.IsAbove(_hwnd, item.Hwnd)
            && r.Left >= me.Left && r.Top >= me.Top && r.Right <= me.Right && r.Bottom <= me.Bottom;
    }

    public void SwitchTo(WindowItem item)
    {
        Unpeek();
        AppLog.Info($"Переключение на «{item.Title}» ({item.App}).");

        // окно из трея ещё спрятано — его место в стопке окон где-то внизу, под ним не встаём
        var fromTray = item.InTray && IsHiddenByApp(item.Hwnd);
        Tracker.Activate(item);
        Toast(Loc.F("ToastSwitched", item.Caption));

        // лента занимает своё место и никому не мешает
        if (Docked)
        {
            return;
        }

        // окно выбрано — уезжаем за край, не мешаем
        if (Settings.Current.AutoHide)
        {
            HideSwitcher();
            return;
        }

        StandBelow(item, fromTray);
    }

    /// <summary>
    /// Остаёмся открытыми — но под выбранным окном, чтобы оно было на первом плане;
    /// «поверх окон» вернётся при следующем показе.
    /// </summary>
    private void StandBelow(WindowItem item, bool fromTray)
    {
        if (Docked)
        {
            return;
        }

        SetWindowPos(_hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

        if (!fromTray)
        {
            SetWindowPos(_hwnd, item.Hwnd, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
    }

    /// <summary>Кнопка-экран на плитке: окно — на следующий монитор и вперёд. Сами не уезжаем: ещё клик — следующий монитор.</summary>
    public void MoveToNextMonitor(WindowItem item)
    {
        Unpeek();
        var fromTray = item.InTray && IsHiddenByApp(item.Hwnd);
        var number = Tracker.MoveToNextMonitor(item);

        if (number is null)
        {
            AppLog.Warn($"Окно не перенеслось на другой монитор: «{item.Title}» ({item.App}).");
            Toast(Loc.F("ToastMonitorDenied", item.Caption));
            return;
        }

        AppLog.Info($"Окно на мониторе {number}: «{item.Title}» ({item.App}).");
        Toast(Loc.F("ToastMonitorMoved", item.Caption, number));
        StandBelow(item, fromTray);
    }

    public void Rename(WindowTile tile)
    {
        Unpeek();
        _renaming = tile;
        _renameCancel = false;

        var item = tile.Item;
        NameBox.Text = item.HasName ? item.Name : tile.ShowsTitleBanner ? item.Title : string.Empty;
        FragmentBox.Text = WindowTracker.RuleFor(item)?.Fragment ?? WindowTracker.DefaultFragment(item);
        RuleText.Text = Loc.F("NameRule", item.AppTitle);

        var popupWidth = Math.Max(300, tile.ActualWidth - 16);
        ((Border)RenamePopup.Child).Width = popupWidth;
        RenamePopup.PlacementTarget = tile;
        RenamePopup.HorizontalOffset = 8;
        RenamePopup.VerticalOffset = Math.Max(30, tile.ActualHeight - 128);

        // ввод с клавиатуры возможен только в активном окне
        Activate();
        RenamePopup.IsOpen = true;

        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        }));
    }

    private void OnRenameKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            RenamePopup.IsOpen = false;
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _renameCancel = true;
            RenamePopup.IsOpen = false;
            e.Handled = true;
        }
    }

    private void OnRenameOk(object sender, RoutedEventArgs e) => RenamePopup.IsOpen = false;

    /// <summary>
    /// Имя сохраняется правилом «программа + часть заголовка»: после перезапуска окно
    /// получит другой номер, а имя вернётся. Пустое имя — правило удаляется.
    /// </summary>
    private void FinishRename()
    {
        var tile = _renaming;
        _renaming = null;

        if (tile is null || _renameCancel)
        {
            return;
        }

        var item = tile.Item;
        var name = NameBox.Text.Trim();
        var fragment = FragmentBox.Text.Trim();

        if (fragment.Length == 0)
        {
            fragment = WindowTracker.DefaultFragment(item);
        }

        var rules = Settings.Current.Names;

        if (WindowTracker.RuleFor(item) is { } old)
        {
            rules.Remove(old);
        }

        if (name.Length > 0)
        {
            // имя сразу за этим окном: совпадает его заголовок с правилом или нет
            item.NameRule = new NameRule { App = item.App, AppTitle = item.AppTitle, Fragment = fragment, Name = name, Hwnd = item.Hwnd.ToInt64(), ByProject = WindowTracker.ProjectOf(item) is not null };
            rules.Add(item.NameRule);
        }

        AppLog.Info(name.Length > 0 ? $"Имя «{name}» для {item.App}, в заголовке «{fragment}»." : $"Имя убрано у {item.App}.");
        Settings.Touch();
        Tracker.ApplyRules();
        Rebuild();
    }

    public void ClearName(WindowItem item)
    {
        if (WindowTracker.RuleFor(item) is not { } rule)
        {
            return;
        }

        Settings.Current.Names.Remove(rule);
        Settings.Touch();
        Tracker.ApplyRules();
        Rebuild();
    }

    public void TogglePin(WindowItem item)
    {
        var pins = Settings.Current.Pins;

        if (WindowTracker.PinFor(item) is { } existing)
        {
            pins.Remove(existing);
            Toast(Loc.F("ToastUnpinned", item.Caption));
        }
        else
        {
            var fragment = Tracker.KeyFragment(item);
            pins.Add(new PinRule { App = item.App, Fragment = fragment });
            Toast(Loc.F("ToastPinned", item.Caption));
        }

        Settings.Touch();
        Tracker.ApplyRules();
        Rebuild();
    }

    /// <summary>Двойная ширина — правилом «программа + часть заголовка», переживает перезапуск.</summary>
    public void ToggleWide(WindowItem item)
    {
        var wides = Settings.Current.Wides;

        if (WindowTracker.WideFor(item) is { } existing)
        {
            wides.Remove(existing);
        }
        else
        {
            var fragment = Tracker.KeyFragment(item);
            wides.Add(new PinRule { App = item.App, Fragment = fragment });
        }

        AppLog.Info($"Двойная ширина {(WindowTracker.WideFor(item) is null ? "выкл" : "вкл")}: «{item.Title}».");
        Settings.Touch();
        Tracker.ApplyRules();
        Rebuild();
    }

    public void Peek(WindowTile tile)
    {
        HideHover();

        if (_peek == tile)
        {
            return;
        }

        _peek = tile;
        Tiles.Peek = tile;
    }

    public void Unpeek()
    {
        if (_peek is null)
        {
            return;
        }

        _peek = null;
        Tiles.Peek = null;
    }

    public bool IsPeeking(WindowTile tile) => _peek == tile;

    /// <summary>Глаз: из сетки — в раздел «Скрытые»; оттуда — обратно в сетку.</summary>
    public void HideWindow(WindowItem item)
    {
        Unpeek();
        item.HiddenByUser = !item.HiddenByUser;
        AppLog.Info($"{(item.HiddenByUser ? "Скрыто из сетки" : "Возвращено в сетку")}: «{item.Title}».");
        Rebuild();
        Toast(Loc.F(item.HiddenByUser ? "ToastHidden" : "ToastShown", item.Caption));
    }

    public void CloseWindow(WindowItem item)
    {
        Unpeek();
        AppLog.Info($"Закрыть окно «{item.Title}» ({item.App}).");
        var closed = Tracker.Close(item);
        Toast(Loc.F(closed ? "ToastClosed" : "ToastCloseDenied", item.Caption), error: !closed);
    }

    private void Exclude(WindowItem item)
    {
        if (!Settings.Current.Exclusions.Any(x => x.App == item.App))
        {
            Settings.Current.Exclusions.Add(new Exclusion { App = item.App, AppTitle = item.AppTitle });
            Settings.Touch();
        }

        Tracker.Refresh();
        Rebuild();
        Toast(Loc.F("ToastExcluded", item.AppTitle));
    }

    public void ShowMenu(WindowTile tile)
    {
        var item = tile.Item;
        var menu = new ContextMenu { PlacementTarget = tile, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };

        menu.Items.Add(new MenuItem { Header = item.HasName ? $"{item.Name} · {item.Title}" : item.Title, IsEnabled = false });
        menu.Items.Add(new Separator());
        Add("MenuSwitch", () => SwitchTo(item));
        Add("MenuPeek", () => Peek(tile));
        Add("MenuRename", () => Rename(tile));
        Add(item.IsPinned ? "MenuUnpin" : "MenuPin", () => TogglePin(item));
        Add(item.IsWide ? "MenuWideOff" : "MenuWide", () => ToggleWide(item));
        menu.Items.Add(new Separator());
        Add(item.HiddenByUser ? "MenuShowAgain" : "MenuHide", () => HideWindow(item));
        menu.Items.Add(Item(Loc.F("MenuExclude", item.AppTitle), () => Exclude(item)));
        menu.Items.Add(new Separator());
        Add("MenuClose", () => CloseWindow(item));

        menu.Opened += (_, _) => _menuOpen = true;
        menu.Closed += (_, _) => _menuOpen = false;
        menu.IsOpen = true;

        void Add(string key, Action action) => menu.Items.Add(Item(Loc.T(key), action));

        static MenuItem Item(string header, Action action)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (_, _) => action();
            return mi;
        }
    }

    /// <summary>
    /// Начали тащить: плитка пойдёт за курсором, её превью — поверх остальных
    /// (у Windows сверху то превью, что подключено последним).
    /// </summary>
    public void TileDragStart(WindowTile tile, Point grab)
    {
        HideHover();
        Unpeek();
        _dragGrab = grab;
        _dragOrder = Tiles.Ordered.ToList();
        Tiles.AnimateMoves = true;
        tile.Detach();
        tile.Attach(_hwnd);
        StartFollowing();
    }

    /// <summary>Курсор над чужой ячейкой — плитка сразу встаёт туда, остальные плавно расступаются.</summary>
    public void TileDragOver(WindowTile tile, Point screen)
    {
        var cursor = Tiles.PointFromScreen(screen);

        if (Tiles.SlotOf(tile) is { } slot)
        {
            Tiles.Drag(tile, (cursor - slot.TopLeft) - (Vector)_dragGrab);
        }

        var ordered = Tiles.Ordered.ToList();

        foreach (var other in ordered)
        {
            if (other == tile || Tiles.SlotOf(other) is not { } rect || !rect.Contains(cursor))
            {
                continue;
            }

            // вперёд — встаёт после того окна, назад — перед ним
            var to = ordered.IndexOf(other);
            ordered.Remove(tile);
            ordered.Insert(to, tile);
            Tiles.Ordered = ordered;
            break;
        }
    }

    /// <summary>Отпустили — порядок запоминается; сорвалось — всё возвращается как было.</summary>
    public void TileDragEnd(WindowTile tile, bool commit)
    {
        if (!commit && _dragOrder.Count > 0)
        {
            Tiles.Ordered = _dragOrder;
        }

        Tiles.Settle();

        if (commit)
        {
            CommitOrder(Tiles.Ordered.OfType<WindowTile>().Select(t => t.Item).ToList());
        }

        _dragOrder = new();
    }

    /// <summary>Новый порядок видимых окон вписывается в общий: скрытые фильтром остаются на своих местах.</summary>
    private void CommitOrder(List<WindowItem> visible)
    {
        var all = Tracker.Windows.OrderBy(w => w.Order).ToList();
        var slots = all.Select((w, i) => (w, i)).Where(x => visible.Contains(x.w)).Select(x => x.i).ToList();

        for (var k = 0; k < slots.Count && k < visible.Count; k++)
        {
            all[slots[k]] = visible[k];
        }

        var order = 0;

        foreach (var w in all)
        {
            w.Order = order += 10;
        }

        AppLog.Info("Порядок окон изменён перетаскиванием.");

        // перестройка — когда плитка доехала, чтобы не прервать её посадку
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Rebuild));
    }

    /// <summary>Пока плитки едут, превью Windows подтягиваются за ними каждый кадр.</summary>
    private void StartFollowing()
    {
        if (_following)
        {
            return;
        }

        _following = true;
        CompositionTarget.Rendering += FollowFrame;
    }

    private void FollowFrame(object? sender, EventArgs e)
    {
        UpdateThumbnails(false);

        if (!Tiles.IsMoving && _dragOrder.Count == 0)
        {
            _following = false;
            Tiles.AnimateMoves = false;
            CompositionTarget.Rendering -= FollowFrame;
        }
    }
}
