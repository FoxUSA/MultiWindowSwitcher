using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MultiWindowSwitcher.Core;
using MultiWindowSwitcher.Models;
using MultiWindowSwitcher.Native;
using MultiWindowSwitcher.Services;
using MultiWindowSwitcher.Views.Controls;
using Point = System.Windows.Point;
using static MultiWindowSwitcher.Native.Win32;

namespace MultiWindowSwitcher.Views;

/// <summary>
/// Лента: переключатель на всю ширину монитора вверху или внизу, его полоса зарезервирована (см. AppBar).
/// В высоту — только окна: сбоку две кнопки, «в режим окна» и «меню»; у плиток остаётся одна шапка.
/// Заголовок и панель на время ленты переезжают в меню и выглядят там как в обычном окне.
/// </summary>
public partial class MainWindow
{
    private const int WM_ACTIVATE = 0x0006, WM_WINDOWPOSCHANGED = 0x0047, WM_NCHITTEST = 0x0084, WM_DISPLAYCHANGE = 0x007E;
    private const int HTCLIENT = 1, HTTOP = 12, HTBOTTOM = 15;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_DEFAULT = 0, DWMWCP_DONOTROUND = 1;
    private const double DockMinHeight = 110, DockGrip = 6;

    private static readonly int AppBarMessage = RegisterWindowMessage("MultiWindowSwitcher.AppBar");
    private static readonly int TaskbarCreated = RegisterWindowMessage("TaskbarCreated");

    private bool _railOn;

    private static bool Docked => Settings.Current.Dock is "Top" or "Bottom";

    private void ShowDocked(ShowReason reason)
    {
        ApplyDockChrome(true);
        Parking.RaiseAll();

        if (!IsVisible)
        {
            Show();
        }

        PlaceDock();
        Raise(reason);

        IsShown = true;
        _armed = true;
        _grace = null;

        FillMonitors();
        Rebuild();

        // смена масштаба монитора при показе приходит с опозданием — ещё раз ставим свой размер
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (Docked && IsShown)
            {
                PlaceDock();
                UpdateThumbnails(true);
            }
        }));

        App.Current.Fab.SetShown(true);
        _watch.Start();
    }

    private void HideDocked()
    {
        HideHover();
        _watch.Stop();
        DockMenu.IsOpen = false;
        EdgeFrame.BorderBrush = Brushes.Transparent;
        AppLog.Info("Лента спрятана: место на экране освобождено.");
        Hide();
        ReleaseDock();
        IsShown = false;
        App.Current.Fab.SetShown(false);
    }

    /// <summary>Отдать зарезервированную полосу обратно: при скрытии и при выходе.</summary>
    public void ReleaseDock()
    {
        AppBar.Remove(_hwnd);
        _stripFit.Release();
    }

    private readonly StripFit _stripFit = new();

    private void FitWindowsToStrip()
    {
        if (_railOn && IsShown)
        {
            _stripFit.FitAll(Tracker.Windows);
        }
    }

    private MonitorInfo DockMonitor() =>
        Monitors.All().FirstOrDefault(m => m.Device == Settings.Current.DockDevice) ?? Monitors.Primary();

    private static int DockPixels(MonitorInfo monitor) =>
        Math.Min((int)(Math.Max(Settings.Current.DockHeight, DockMinHeight) * monitor.Scale), monitor.Bounds.Height / 2);

    private void PlaceDock()
    {
        var s = Settings.Current;
        var monitor = DockMonitor();

        ApplyDockMargins();
        AppBar.Register(_hwnd, AppBarMessage);

        // зарегистрирована панель всегда: так приходит сигнал о полноэкранных окнах. Место занимает, только если просили
        var rect = s.DockReserve
            ? AppBar.Place(_hwnd, monitor, s.Dock == "Top", DockPixels(monitor))
            : AppBar.Where(_hwnd, monitor, s.Dock == "Top", DockPixels(monitor));

        if (!SameRect(rect, _target))
        {
            AppLog.Info($"Лента: {s.Dock}, монитор {monitor.Number}, {rect.Width}×{rect.Height} в ({rect.Left}, {rect.Top}).");
        }

        _target = rect;
        SetWindowPos(_hwnd, HWND_TOPMOST, rect.Left, rect.Top, rect.Width, rect.Height, SWP_NOACTIVATE);
        _stripFit.Apply(rect, s.Dock == "Top", Tracker.Windows);
    }

    private static bool SameRect(RECT a, RECT b) =>
        a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    /// <summary>Панели у края сдвинулись (в том числе от нашего же занятия места): встаём заново, только если место правда другое.</summary>
    private void OnAppBarsMoved()
    {
        if (!Docked || !IsShown || _resizing)
        {
            return;
        }

        var monitor = DockMonitor();

        if (!SameRect(AppBar.Where(_hwnd, monitor, Settings.Current.Dock == "Top", DockPixels(monitor)), _target))
        {
            PlaceDock();
        }
    }

    private void OnDockChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressUi || !IsLoaded)
        {
            return;
        }

        var s = Settings.Current;
        var dock = DockTop.IsChecked == true ? "Top" : DockBottom.IsChecked == true ? "Bottom" : "None";

        if (dock == s.Dock)
        {
            return;
        }

        if (!Docked)
        {
            s.DockDevice = Monitors.FromWindow(_hwnd).Device;
        }

        s.Dock = dock;
        Settings.Touch();
        AppLog.Info("Расположение: " + dock + ".");
        TitlesPopup.IsOpen = false;

        if (Docked)
        {
            ShowDocked(ShowReason.Explicit);
        }
        else
        {
            Undock();
        }
    }

    /// <summary>Снова обычное окно, там же, где стояло до ленты.</summary>
    private void Undock()
    {
        HideHover();
        DockMenu.IsOpen = false;
        ReleaseDock();
        ApplyDockChrome(false);
        var target = TargetRect(Monitors.FromWindow(_hwnd));
        _target = target;
        SetWindowPos(_hwnd, Topmost ? HWND_TOPMOST : HWND_NOTOPMOST, target.Left, target.Top, target.Width, target.Height, SWP_NOACTIVATE);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => UpdateThumbnails(true)));
    }

    private Panel? _toolbarHome;

    /// <summary>Вид ленты: сбоку две кнопки, заголовок и панель — в меню, в высоту только окна.</summary>
    private void ApplyDockChrome(bool on)
    {
        if (on == _railOn)
        {
            return;
        }

        _railOn = on;
        var main = on ? Visibility.Collapsed : Visibility.Visible;
        Bar.Visibility = main;
        StatusBar.Visibility = main;
        DockRail.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        RailMonitorButton.Visibility = WindowTile.ShowMonitor ? Visibility.Visible : Visibility.Collapsed;

        if (on)
        {
            // в меню места хватает: подписи как в широком окне
            ToolbarLabels(true);
            BarLabels(true);
            _toolbarFull = _barFull = true;

            Bar.TrailingContent = null;
            DockMenuBar.Children.Insert(0, BarTrailing);
            _toolbarHome = (Panel)Toolbar.Parent;
            _toolbarHome.Children.Remove(Toolbar);
            DockMenuHost.Children.Add(Toolbar);

            // кнопки этих окошек — в меню, а меню при их открытии закрывается и закрыло бы их с собой: в ленте они привязаны к ней
            foreach (var popup in new[] { SettingsPopup, TitlesPopup, CornersPopup })
            {
                popup.PlacementTarget = DockRail;
                popup.Placement = System.Windows.Controls.Primitives.PlacementMode.Right;
            }
        }
        else
        {
            SettingsPopup.PlacementTarget = SettingsButton;
            TitlesPopup.PlacementTarget = BigTitlesButton;
            CornersPopup.PlacementTarget = CornersButton;

            foreach (var popup in new[] { SettingsPopup, TitlesPopup, CornersPopup })
            {
                popup.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            }

            DockMenuHost.Children.Remove(Toolbar);
            _toolbarHome?.Children.Add(Toolbar);
            DockMenuBar.Children.Remove(BarTrailing);
            Bar.TrailingContent = BarTrailing;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(FitBars));
            UpdateStripRegion();
        }

        // на всю ширину и без скруглений: полоса прилегает к краю экрана
        EdgeFrame.CornerRadius = new CornerRadius(on ? 0 : 8);
        EdgeFrame.BorderThickness = new Thickness(on ? 0 : 1);
        var corners = on ? DWMWCP_DONOTROUND : DWMWCP_DEFAULT;
        DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corners, sizeof(int));

        ApplyDockMargins();

        MinHeight = on ? DockMinHeight : 320;
        Topmost = on || Settings.Current.Topmost;
        Tiles.HeaderHeight = on ? 30 : 61;
        Tiles.StripWidth = on ? (element, height) => ((WindowTile)element).CompactWidth(height) : null;
        WindowTile.Compact = on;

        foreach (var tile in _tiles.Values)
        {
            tile.ApplyCompact();
        }

        Tiles.InvalidateMeasure();
    }

    /// <summary>
    /// У края, который тянут мышью, — зазор: иначе он отбирает клики у плиток. Кнопки начинаются вровень с плитками.
    /// Зависит от края ленты, поэтому и при смене верх/низ.
    /// </summary>
    private void ApplyDockMargins()
    {
        var grip = (int)DockGrip + 2;
        TilesHost.Margin = !_railOn ? new Thickness(12, 0, 12, 0)
            : Settings.Current.Dock == "Top" ? new Thickness(2, 6, 6, grip) : new Thickness(2, grip, 6, 6);
        DockRail.Margin = new Thickness(6, TilesHost.Margin.Top, 4, 0);
    }

    private void OnRailHide(object sender, RoutedEventArgs e) => HideSwitcher();

    /// <summary>Сейчас окно обрезано до этой ширины в пикселях; 0 — не обрезано.</summary>
    private int _stripRegion;

    /// <summary>
    /// Лента занимает всю ширину монитора, а плитки — только часть. Окно обрезается по последней плитке: пустая часть
    /// перестаёт быть окном, сквозь неё виден рабочий стол и проходят клики; полоса остаётся зарезервированной.
    /// Пока тащат плитку, не обрезаем: она может выйти за последнюю.
    /// </summary>
    private void UpdateStripRegion()
    {
        var width = 0;

        if (_railOn && _dragOrder.Count == 0)
        {
            GetWindowRect(_hwnd, out var r);
            var used = Tiles.TranslatePoint(new Point(Tiles.UsedWidth, 0), this).X;
            var needed = (int)Math.Ceiling((used + 6) * VisualTreeHelper.GetDpi(this).DpiScaleX);
            width = needed < r.Width ? needed : 0;
        }

        if (width == _stripRegion)
        {
            return;
        }

        _stripRegion = width;

        // по высоте с запасом: край ленты можно тянуть, и обрезка не должна за ним гнаться
        SetWindowRgn(_hwnd, width > 0 ? CreateRectRgn(0, 0, width, 30000) : IntPtr.Zero, true);
    }

    /// <summary>Поменялись крупные значки: в ленте от них зависят шапка, ширина плиток и место превью.</summary>
    private void RefreshStrip()
    {
        if (!_railOn)
        {
            return;
        }

        foreach (var tile in _tiles.Values)
        {
            tile.ApplyCompact();
        }

        Tiles.InvalidateMeasure();
        UpdateThumbnails(false);
    }

    /// <summary>Кнопка на ленте: снова обычное окно.</summary>
    private void OnDockToWindow(object sender, RoutedEventArgs e)
    {
        Settings.Current.Dock = "None";
        Settings.Touch();
        AppLog.Info("Расположение: None.");
        Undock();
    }

    private void OnDockMenu(object sender, RoutedEventArgs e)
    {
        HideHover();
        DockMenu.IsOpen = !DockMenu.IsOpen;
    }

    // ── большое превью при наведении ─────────────────────────────────────────

    private readonly DispatcherTimer _hoverTimer = new();
    private HoverPreview? _hoverPreview;
    private WindowTile? _hoverTile;

    /// <summary>Мышь зашла на плитку или ушла. С задержкой: мышь, идущая через ленту, не должна мигать превью.</summary>
    public void TileHover(WindowTile tile, bool over)
    {
        if (!_railOn || !Settings.Current.HoverPreview)
        {
            return;
        }

        if (!over && _hoverTile != tile)
        {
            return;
        }

        _hoverTile = over ? tile : null;

        // превью уже видно — следующая плитка показывается сразу; мышь ушла — чуть ждём: вдруг на соседнюю
        var delay = over && _hoverPreview is not { IsVisible: true } ? 300 : 70;
        _hoverTimer.Stop();
        _hoverTimer.Interval = TimeSpan.FromMilliseconds(delay);
        _hoverTimer.Tick -= OnHoverTimer;
        _hoverTimer.Tick += OnHoverTimer;
        _hoverTimer.Start();
    }

    private void OnHoverTimer(object? sender, EventArgs e)
    {
        _hoverTimer.Stop();

        if (_hoverTile is not { } tile || !tile.IsMouseOver || !_railOn || IsBusy || DockMenu.IsOpen || _peek is not null || !IsShown)
        {
            _hoverPreview?.HideNow();
            return;
        }

        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var left = tile.PointToScreen(new Point(0, 0)).X;
        var right = left + tile.ActualWidth * scale;
        var snapshot = tile.ShowsSnapshot ? WindowTile.SnapshotOf?.Invoke(tile.Item)?.Image : null;

        _hoverPreview ??= new HoverPreview();
        _hoverPreview.ShowFor(tile.Item, snapshot, (int)left, (int)right, DockMonitor(), Settings.Current.Dock == "Top", Math.Clamp(Settings.Current.HoverPreviewSize, 30, 90) / 100, _target);
    }

    private void HideHover()
    {
        _sizeDemoTimer.Stop();
        _hoverTimer.Stop();
        _hoverTile = null;
        _hoverPreview?.HideNow();
    }

    private void ForgetHover(WindowTile tile)
    {
        if (_hoverTile == tile)
        {
            HideHover();
        }
    }

    private void OnDockReserveSwitch(object sender, RoutedEventArgs e)
    {
        Settings.Current.DockReserve = DockReserveSwitch.IsChecked == true;
        Settings.Touch();
        AppLog.Info($"Резервировать место под ленту: {(Settings.Current.DockReserve ? "вкл" : "выкл")}.");

        if (_railOn && IsShown)
        {
            // занятое место отдаётся только снятием панели
            ReleaseDock();
            PlaceDock();
        }
    }

    private void OnHoverPreviewSwitch(object sender, RoutedEventArgs e)
    {
        Settings.Current.HoverPreview = HoverPreviewSwitch.IsChecked == true;
        Settings.Touch();
        AppLog.Info($"Большое превью при наведении: {(Settings.Current.HoverPreview ? "вкл" : "выкл")}.");
        HideHover();
    }

    private void OnHoverPreviewSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (HoverPreviewSizeText is null)
        {
            return;
        }

        HoverPreviewSizeText.Text = $"{e.NewValue:0}%";

        if (_suppressUi || !ByHand(HoverPreviewSizeSlider))
        {
            return;
        }

        Settings.Current.HoverPreviewSize = e.NewValue;
        Settings.Touch();
        ShowSizeDemo();
    }

    private readonly DispatcherTimer _sizeDemoTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    /// <summary>
    /// Двигают ползунок размера — сразу показываем, каким будет большое превью. Образец — самое большое окно:
    /// маленькое не дорастёт до предела (крупнее своего размера превью не бывает), и ползунок казался бы сломанным.
    /// Превью стоит правее окошка «Вид», а окошко остаётся сверху: ползунок всегда виден.
    /// </summary>
    private void ShowSizeDemo()
    {
        var sample = _tiles.Values
            .Where(t => t.IsVisible && t.HasThumbnail)
            .OrderByDescending(t => (long)t.SourceSize.cx * t.SourceSize.cy)
            .FirstOrDefault();

        if (sample is null || TitlesPopup.Child is not FrameworkElement view || PresentationSource.FromVisual(view) is not HwndSource popup)
        {
            return;
        }

        var edge = (int)view.PointToScreen(new Point(view.ActualWidth, 0)).X;
        var monitor = _railOn ? DockMonitor() : Monitors.FromWindow(_hwnd);

        _hoverPreview ??= new HoverPreview();
        _hoverPreview.ShowFor(sample.Item, null, 0, 0, monitor, Settings.Current.Dock != "Bottom", Math.Clamp(Settings.Current.HoverPreviewSize, 30, 90) / 100, _railOn ? _target : null, edge);
        SetWindowPos(popup.Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

        _sizeDemoTimer.Stop();
        _sizeDemoTimer.Start();
    }

    /// <summary>Подключается один раз: выбрали пункт меню и открылось его окошко — само меню закрывается, как у обычного меню.</summary>
    private void WireDockMenu()
    {
        void Close(object? sender, EventArgs e) => DockMenu.IsOpen = false;
        SettingsPopup.Opened += Close;
        TitlesPopup.Opened += Close;
        CornersPopup.Opened += Close;

        Tiles.LayoutUpdated += (_, _) => UpdateStripRegion();

        // закрыли «Вид» — образец размера превью убираем сразу
        TitlesPopup.Closed += (_, _) => HideHover();
        _sizeDemoTimer.Tick += (_, _) => HideHover();
    }

    /// <summary>Нажали мимо меню и мимо кнопок ленты — закрыть (поиск и выпадающий список мониторов — внутри меню).</summary>
    private void WatchDockMenu()
    {
        if (!DockMenu.IsOpen || MonitorBox.IsDropDownOpen || !(IsKeyDown(VK_LBUTTON) || IsKeyDown(VK_RBUTTON)))
        {
            return;
        }

        var c = CursorPos();

        if (!IsOver(DockMenuFrame, c) && !IsOver(DockRail, c))
        {
            DockMenu.IsOpen = false;
        }
    }

    private static bool IsOver(FrameworkElement element, POINT point)
    {
        if (PresentationSource.FromVisual(element) is null)
        {
            return false;
        }

        var topLeft = element.PointToScreen(new Point(0, 0));
        var bottomRight = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
        return point.X >= topLeft.X && point.X < bottomRight.X && point.Y >= topLeft.Y && point.Y < bottomRight.Y;
    }

    /// <summary>Лента — на следующий монитор, по кругу.</summary>
    private void OnRailMonitor(object sender, RoutedEventArgs e)
    {
        var all = Monitors.All();
        var next = all[DockMonitor().Number % all.Count];
        Settings.Current.DockDevice = next.Device;
        Settings.Touch();
        PlaceDock();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => UpdateThumbnails(true)));
    }

    /// <summary>Отпустили край: запомнить высоту (в единицах WPF) и занять полосу уже такой.</summary>
    private void FinishDockResize()
    {
        GetWindowRect(_hwnd, out var rect);
        Settings.Current.DockHeight = Math.Round(rect.Height / DockMonitor().Scale);
        Settings.Touch();
        PlaceDock();
    }

    /// <summary>Сообщения Windows для ленты. Вернёт значение — сообщение обработано здесь.</summary>
    private IntPtr? DockMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == AppBarMessage && AppBar.Registered)
        {
            switch ((int)wParam)
            {
                case AppBar.ABN_POSCHANGED:
                    Dispatcher.BeginInvoke(new Action(OnAppBarsMoved));
                    break;

                case AppBar.ABN_FULLSCREENAPP:
                    // игра или видео на весь экран — лента не должна быть поверх
                    SetWindowPos(_hwnd, lParam != IntPtr.Zero ? HWND_BOTTOM : HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                    break;
            }

            return IntPtr.Zero;
        }

        if (!_railOn || !Docked)
        {
            return null;
        }

        if (msg == TaskbarCreated)
        {
            // перезапустился Проводник: он забыл все панели
            ReleaseDock();
            PlaceDock();
            return null;
        }

        switch (msg)
        {
            case WM_NCHITTEST:
                // тянуть можно только внутренний край; остальные края к экрану прижаты
                GetWindowRect(hwnd, out var r);
                var y = unchecked((short)((long)lParam >> 16));
                var grip = (int)(DockGrip * VisualTreeHelper.GetDpi(this).DpiScaleY);
                var top = Settings.Current.Dock == "Top";
                var inner = top ? r.Bottom - y <= grip : y - r.Top <= grip;
                handled = true;
                return new IntPtr(inner ? (top ? HTBOTTOM : HTTOP) : HTCLIENT);

            case WM_ACTIVATE:
                AppBar.Notify(hwnd, AppBar.ABM_ACTIVATE);
                break;

            case WM_WINDOWPOSCHANGED:
                AppBar.Notify(hwnd, AppBar.ABM_WINDOWPOSCHANGED);
                break;

            case WM_DISPLAYCHANGE when IsShown:
                Dispatcher.BeginInvoke(new Action(PlaceDock));
                break;
        }

        return null;
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
