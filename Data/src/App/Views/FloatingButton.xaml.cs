using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using MultiWindowSwitcher.Core;
using MultiWindowSwitcher.Models;
using MultiWindowSwitcher.Native;
using static MultiWindowSwitcher.Native.Win32;

namespace MultiWindowSwitcher.Views;

/// <summary>
/// Плавающая кнопка поверх всех окон. Клик — показать/спрятать переключатель, наведение —
/// показать, перетаскивание — к любому краю любого монитора. У края прилипает и через 2 с
/// уходит за край наполовину: иконка всегда видна и в неё легко попасть.
/// Фокус не забирает никогда: работа в активной программе не прерывается.
/// </summary>
public partial class FloatingButton : Window
{
    private const int SnapDistance = 90;
    private static readonly TimeSpan TuckDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan HoverDelay = TimeSpan.FromMilliseconds(80);

    private readonly DispatcherTimer _tuckTimer = new() { Interval = TuckDelay };
    private readonly DispatcherTimer _hoverTimer = new() { Interval = HoverDelay };
    private readonly DispatcherTimer _anim = new() { Interval = TimeSpan.FromMilliseconds(8) };

    private IntPtr _hwnd;
    private bool _tucked;

    /// <summary>
    /// Спрятана наполовину у края, за которым соседний монитор: окно сужено вдвое и показывает
    /// только свою половину — за край ничего не вылезает.
    /// </summary>
    private bool _narrow;
    private bool _pressed;
    private bool _moved;
    private POINT _downCursor;
    private RECT _downRect;
    private Storyboard? _pulse;
    private DateTime _animStart;
    private int _animFrom;
    private int _animTo;
    private int _animY;

    public FloatingButton()
    {
        InitializeComponent();

        _tuckTimer.Tick += (_, _) =>
        {
            _tuckTimer.Stop();
            Tuck();
        };

        _hoverTimer.Tick += (_, _) =>
        {
            _hoverTimer.Stop();

            if (IsMouseOver && !App.Current.Switcher.IsShown)
            {
                App.Current.Switcher.ShowSwitcher(Side, ShowReason.Hover);
            }
        };

        _anim.Tick += (_, _) => AnimateStep();

        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) => Place(tucked: false);

        Root.MouseLeftButtonDown += OnDown;
        Root.MouseMove += OnMove;
        Root.MouseLeftButtonUp += OnUp;
        Root.MouseRightButtonUp += (_, e) =>
        {
            ShowMenu();
            e.Handled = true;
        };

        SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(new Action(() => Place(tucked: false)));
        Loc.I.LanguageChanged += () => SetShown(App.Current.Switcher.IsShown);

        SetShown(false);
    }

    /// <summary>С какой стороны выезжать переключателю.</summary>
    public char Side
    {
        get
        {
            var fab = Settings.Current.Fab;

            return fab.Side switch
            {
                "L" => 'L',
                "R" => 'R',
                _ => fab.X < 0.5 ? 'L' : 'R',
            };
        }
    }

    // ── окно без фокуса ──────────────────────────────────────────────────────

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;

        // ни на панели задач, ни в Alt+Tab, и клик не отбирает фокус у активной программы
        var ex = GetExStyle(_hwnd);
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));

        HwndSource.FromHwnd(_hwnd)?.AddHook((IntPtr _, int msg, IntPtr _, IntPtr _, ref bool handled) =>
        {
            if (msg == WM_MOUSEACTIVATE)
            {
                handled = true;
                return new IntPtr(MA_NOACTIVATE);
            }

            return IntPtr.Zero;
        });
    }

    // ── положение ────────────────────────────────────────────────────────────

    private MonitorInfo CurrentMonitor()
    {
        var all = Monitors.All();
        return all.FirstOrDefault(m => m.Device == Settings.Current.Fab.Device) ?? all.First();
    }

    /// <summary>Поставить кнопку по настройкам: у края или свободно, на сохранённой высоте.</summary>
    private void Place(bool tucked)
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        _anim.Stop();

        var monitor = CurrentMonitor();
        var size = (int)Math.Round(72 * monitor.Scale);
        var (x, y) = HomePosition(monitor, size);

        Widen();
        SetWindowPos(_hwnd, HWND_TOPMOST, x, y, size, size, SWP_NOACTIVATE);
        _tucked = false;

        if (tucked)
        {
            Tuck();
        }
        else
        {
            ScheduleTuck();
        }
    }

    private static (int X, int Y) HomePosition(MonitorInfo monitor, int size)
    {
        var fab = Settings.Current.Fab;
        var work = monitor.Work;
        var y = work.Top + (int)(Math.Clamp(fab.Y, 0, 1) * Math.Max(0, work.Height - size));

        var x = fab.Side switch
        {
            "L" => work.Left,
            "R" => work.Right - size,
            _ => work.Left + (int)(Math.Clamp(fab.X, 0, 1) * Math.Max(0, work.Width - size)),
        };

        return (x, y);
    }

    private void ScheduleTuck()
    {
        _tuckTimer.Stop();

        if (Settings.Current.Tuck && Settings.Current.Fab.Side is "L" or "R" && !IsMouseOver)
        {
            _tuckTimer.Start();
        }
    }

    /// <summary>
    /// Уйти за край наполовину. За краем соседний монитор — не выезжаем на него, а сужаем окно
    /// вдвое: видна та же половина кнопки, что и у обычного края.
    /// </summary>
    private void Tuck()
    {
        var fab = Settings.Current.Fab;

        if (_tucked || _pressed || !Settings.Current.Tuck || fab.Side is not ("L" or "R"))
        {
            return;
        }

        GetWindowRect(_hwnd, out var r);
        var monitor = CurrentMonitor();
        var cy = r.Top + r.Height / 2;
        var half = r.Width / 2;
        var neighbour = fab.Side == "L"
            ? Monitors.HasMonitorAt(monitor.Bounds.Left - 2, cy)
            : Monitors.HasMonitorAt(monitor.Bounds.Right + 1, cy);

        if (neighbour)
        {
            _tucked = true;
            _narrow = true;
            _anim.Stop();

            // кнопка остаётся своего размера, окно её обрезает: справа видна левая половина, слева — правая
            Root.Width = Width;
            Root.HorizontalAlignment = fab.Side == "R" ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            var x = fab.Side == "R" ? monitor.Work.Right - half : monitor.Work.Left;
            SetWindowPos(_hwnd, HWND_TOPMOST, x, r.Top, half, r.Height, SWP_NOACTIVATE);
            return;
        }

        _tucked = true;
        var target = fab.Side == "L" ? monitor.Work.Left - half : monitor.Work.Right - r.Width + half;
        SlideTo(r.Left, target, r.Top);
    }

    private void Untuck()
    {
        _tuckTimer.Stop();

        if (!_tucked)
        {
            return;
        }

        _tucked = false;

        if (_narrow)
        {
            // суженная у соседнего монитора — просто снова во всю ширину на своём месте
            GetWindowRect(_hwnd, out var n);
            var full = n.Height;
            var (hx, _) = HomePosition(CurrentMonitor(), full);
            Widen();
            SetWindowPos(_hwnd, HWND_TOPMOST, hx, n.Top, full, full, SWP_NOACTIVATE);
            return;
        }

        GetWindowRect(_hwnd, out var r);
        var monitor = CurrentMonitor();
        var (home, _) = HomePosition(monitor, r.Width);
        SlideTo(r.Left, home, r.Top);
    }

    /// <summary>Вернуть кнопке обычную ширину после «наполовину у соседнего монитора».</summary>
    private void Widen()
    {
        if (!_narrow)
        {
            return;
        }

        _narrow = false;
        Root.Width = double.NaN;
        Root.HorizontalAlignment = HorizontalAlignment.Stretch;
    }

    private void SlideTo(int from, int to, int y)
    {
        _animFrom = from;
        _animTo = to;
        _animY = y;
        _animStart = DateTime.UtcNow;
        _anim.Start();
    }

    private void AnimateStep()
    {
        var t = Math.Min(1, (DateTime.UtcNow - _animStart).TotalMilliseconds / 180);
        var eased = 1 - Math.Pow(1 - t, 3);
        var x = (int)Math.Round(_animFrom + (_animTo - _animFrom) * eased);
        SetWindowPos(_hwnd, HWND_TOPMOST, x, _animY, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);

        if (t >= 1)
        {
            _anim.Stop();
        }
    }

    public void ApplySettings()
    {
        if (!Settings.Current.ShowFab)
        {
            Hide();
            return;
        }

        if (!IsVisible)
        {
            Show();
            Place(tucked: false);
        }

        if (!Settings.Current.Tuck)
        {
            Untuck();
        }
        else
        {
            ScheduleTuck();
        }
    }

    public bool ContainsScreenPoint(int x, int y)
    {
        if (_hwnd == IntPtr.Zero || !IsVisible)
        {
            return false;
        }

        GetWindowRect(_hwnd, out var r);
        return r.Contains(x, y);
    }

    // ── мышь ─────────────────────────────────────────────────────────────────

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        _tuckTimer.Stop();

        // по наведению сразу выезжает переключатель; сама кнопка остаётся у края и не выезжает первой
        if (Settings.Current.HoverOpen && !App.Current.Switcher.IsShown)
        {
            _hoverTimer.Start();
        }
        else
        {
            Untuck();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverTimer.Stop();

        if (!_pressed)
        {
            ScheduleTuck();
        }
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        _pressed = true;
        _moved = false;
        _hoverTimer.Stop();
        _tuckTimer.Stop();
        _anim.Stop();
        _downCursor = CursorPos();
        GetWindowRect(_hwnd, out _downRect);
        Root.CaptureMouse();
        e.Handled = true;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!_pressed)
        {
            return;
        }

        var c = CursorPos();
        var dx = c.X - _downCursor.X;
        var dy = c.Y - _downCursor.Y;

        if (!_moved && Math.Abs(dx) + Math.Abs(dy) > 4)
        {
            _moved = true;
            _tucked = false;

            // тащат суженную — сначала во всю ширину, иначе за курсором поедет половинка
            if (_narrow)
            {
                Widen();
                var full = _downRect.Height;
                var left = Settings.Current.Fab.Side == "R" ? _downRect.Right - full : _downRect.Left;
                _downRect = new RECT(left, _downRect.Top, left + full, _downRect.Top + full);
                SetWindowPos(_hwnd, HWND_TOPMOST, left, _downRect.Top, full, full, SWP_NOACTIVATE);
            }
        }

        if (_moved)
        {
            SetWindowPos(_hwnd, HWND_TOPMOST, _downRect.Left + dx, _downRect.Top + dy, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
        }
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (!_pressed)
        {
            return;
        }

        _pressed = false;
        Root.ReleaseMouseCapture();
        e.Handled = true;

        if (!_moved)
        {
            // клик: показать или спрятать переключатель
            var switcher = App.Current.Switcher;

            if (switcher.IsShown)
            {
                switcher.HideSwitcher(Side);
            }
            else
            {
                switcher.ShowSwitcher(Side, ShowReason.Explicit);
            }

            return;
        }

        DropHere();
    }

    /// <summary>Отпустили после перетаскивания: прилипнуть к ближнему краю или остаться где бросили.</summary>
    private void DropHere()
    {
        GetWindowRect(_hwnd, out var r);
        var monitor = Monitors.FromPoint(r.Left + r.Width / 2, r.Top + r.Height / 2);
        var work = monitor.Work;
        var snap = (int)(SnapDistance * monitor.Scale);
        var fab = Settings.Current.Fab;

        fab.Device = monitor.Device;
        fab.Y = Math.Clamp((r.Top - work.Top) / (double)Math.Max(1, work.Height - r.Height), 0, 1);

        if (Settings.Current.Snap && r.Left - work.Left < snap)
        {
            fab.Side = "L";
            fab.X = 0;
        }
        else if (Settings.Current.Snap && work.Right - r.Right < snap)
        {
            fab.Side = "R";
            fab.X = 1;
        }
        else
        {
            fab.Side = string.Empty;
            fab.X = Math.Clamp((r.Left - work.Left) / (double)Math.Max(1, work.Width - r.Width), 0, 1);
        }

        Settings.Touch();
        Place(tucked: false);
    }

    private void ShowMenu()
    {
        var switcher = App.Current.Switcher;
        var menu = new ContextMenu();

        Add(Loc.T(switcher.IsShown ? "FabHide" : "FabShow"), () =>
        {
            if (switcher.IsShown)
            {
                switcher.HideSwitcher(Side);
            }
            else
            {
                switcher.ShowSwitcher(Side, ShowReason.Explicit);
            }
        });
        Add(Loc.T("TraySettings"), switcher.OpenSettings);
        Add(Loc.T("TrayUpdates"), switcher.OpenUpdates);
        Add(Loc.T("FabHideIcon"), () => App.Current.SetFabVisible(false));
        menu.Items.Add(new Separator());
        Add(Loc.T("TrayExit"), () => App.Current.Quit("меню иконки у края"));

        menu.PlacementTarget = Root;
        menu.IsOpen = true;

        void Add(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
    }

    // ── состояние ────────────────────────────────────────────────────────────

    public void SetShown(bool shown)
    {
        StateDot.Fill = new SolidColorBrush(shown ? Color.FromRgb(0x6C, 0xCB, 0x5F) : Color.FromRgb(0x6B, 0x72, 0x80));
        ToolTip = Loc.T(shown ? "FabHide" : "FabShow");
        Face.Opacity = shown ? 1 : 0.92;
    }

    public void SetCount(int count) => BadgeText.Text = count.ToString();

    /// <summary>Где-то окно мигает на панели задач — кнопка пульсирует оранжевым, даже когда всё спрятано.</summary>
    public void SetAttention(bool on)
    {
        if (on && _pulse is null)
        {
            var animation = new DoubleAnimation(0.15, 1, TimeSpan.FromMilliseconds(650))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
            };

            Storyboard.SetTarget(animation, AttentionRing);
            Storyboard.SetTargetProperty(animation, new PropertyPath(OpacityProperty));
            _pulse = new Storyboard();
            _pulse.Children.Add(animation);
            _pulse.Begin(this, true);
        }
        else if (!on && _pulse is not null)
        {
            _pulse.Stop(this);
            _pulse = null;
            AttentionRing.Opacity = 0;
        }
    }
}
