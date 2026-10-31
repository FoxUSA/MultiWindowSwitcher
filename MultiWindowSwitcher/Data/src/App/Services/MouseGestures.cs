using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using MultiWindowSwitcher.Models;
using MultiWindowSwitcher.Native;

namespace MultiWindowSwitcher.Services;

/// <summary>
/// Жест мышью: записанное пользователем нажатие (любая кнопка, Ctrl/Shift/Alt, одинарный
/// или двойной) срабатывает всегда и везде, в том числе в окне самой программы. Сработавший клик
/// дальше не передаётся — выбирайте нажатие, которое больше ни для чего не нужно.
///
/// Отдельно — клик левой по пустому месту рабочего стола (по желанию): его не забираем, а после
/// отпускания смотрим, выделен ли на столе хоть один значок. Клик по значку его выделяет,
/// клик мимо — снимает выделение со всех: ноль выделенных и значит «мимо значков».
/// </summary>
public sealed class MouseGestures
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONUP = 0x0202;
    private const int LVM_GETSELECTEDCOUNT = 0x1032;
    private const uint GA_ROOT = 2, SMTO_ABORTIFHUNG = 0x2;
    private const int WM_LBUTTONDOWN = 0x0201, WM_RBUTTONDOWN = 0x0204, WM_MBUTTONDOWN = 0x0207, WM_XBUTTONDOWN = 0x020B;
    private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;
    private const int SM_CXDOUBLECLK = 36, SM_CYDOUBLECLK = 37;
    private const uint LLMHF_INJECTED = 0x1;

    private readonly LowLevelMouseProc _proc;
    private readonly Dispatcher _dispatcher;
    private IntPtr _hook;

    /// <summary>Отпускание кнопки, нажатие которой забрали (WM_xBUTTONUP = нажатие + 1).</summary>
    private int _swallowUp;

    /// <summary>Прошлое нажатие — для двойного клика.</summary>
    private (string Button, uint Time, Win32.POINT Pt)? _last;

    /// <summary>Левое нажатие по рабочему столу: список значков (Zero — значки скрыты), точка, время.</summary>
    private (IntPtr List, Win32.POINT Pt, uint Time)? _desktopDown;

    /// <summary>Прошлый клик по пустому столу — для двойного; в режиме одинарного — чтобы второй клик двойного не прятал обратно.</summary>
    private (uint Time, Win32.POINT Pt)? _desktopLast;

    private Action<MouseBinding?>? _capture;
    private DispatcherTimer? _captureTimeout;
    private MouseBinding? _pending;
    private DispatcherTimer? _pendingTimer;

    public MouseGestures(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _proc = OnMouse;
    }

    /// <summary>Сработал жест: точка в пикселях экрана.</summary>
    public event Action<System.Windows.Point>? Triggered;

    public void Start()
    {
        if (_hook != IntPtr.Zero)
        {
            return;
        }

        using var module = Process.GetCurrentProcess().MainModule;
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(module?.ModuleName), 0);

        if (_hook == IntPtr.Zero)
        {
            AppLog.Warn("Жесты мыши не включились: код " + Marshal.GetLastWin32Error());
        }
    }

    public void Stop()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    /// <summary>
    /// Запись нажатия: следующее нажатие мыши где угодно не уходит программам, а становится жестом.
    /// Обычный левый клик — отмена. done(null) — отменили или не дождались.
    /// </summary>
    public void BeginCapture(Action<MouseBinding?> done)
    {
        CancelCapture();
        _capture = done;
        _captureTimeout = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher) { Interval = TimeSpan.FromSeconds(15) };
        _captureTimeout.Tick += (_, _) => FinishCapture(null);
        _captureTimeout.Start();
    }

    public void CancelCapture() => FinishCapture(null);

    public bool IsCapturing => _capture is not null;

    private void FinishCapture(MouseBinding? result)
    {
        _captureTimeout?.Stop();
        _captureTimeout = null;
        _pendingTimer?.Stop();
        _pendingTimer = null;
        _pending = null;

        var done = _capture;
        _capture = null;
        done?.Invoke(result);
    }

    /// <summary>Вызывается Windows на каждое движение и нажатие мыши — только быстрые проверки.</summary>
    private IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (code >= 0)
            {
                var msg = (int)wParam;

                if (msg == WM_LBUTTONUP && _desktopDown is { } down)
                {
                    _desktopDown = null;
                    _dispatcher.BeginInvoke(new Action(() => CheckDesktopClick(down.List, down.Pt, down.Time)));
                }

                // отпускание той кнопки, нажатие которой мы забрали, — тоже наше
                if (_swallowUp != 0 && msg == _swallowUp)
                {
                    _swallowUp = 0;
                    return new IntPtr(1);
                }

                var button = ButtonOf(msg, lParam, out var data);

                if (button is not null && (data.flags & LLMHF_INJECTED) == 0)
                {
                    var press = new MouseBinding
                    {
                        Button = button,
                        Ctrl = Win32.IsKeyDown(VK_CONTROL),
                        Shift = Win32.IsKeyDown(VK_SHIFT),
                        Alt = Win32.IsKeyDown(VK_MENU),
                    };

                    var second = IsSecondClick(press, data);

                    if (_capture is not null)
                    {
                        _swallowUp = msg + 1;
                        Capture(press, second);
                        return new IntPtr(1);
                    }

                    var bindings = Settings.Current.MouseBindings ?? new();
                    var wanted = bindings.Any(b => !b.Double && b.SameAs(press))
                        || (second && bindings.Any(b => b.Double && b.Button == press.Button && b.Ctrl == press.Ctrl && b.Shift == press.Shift && b.Alt == press.Alt));

                    if (wanted)
                    {
                        _last = null;
                        _swallowUp = msg + 1;
                        var point = new System.Windows.Point(data.pt.X, data.pt.Y);
                        _dispatcher.BeginInvoke(new Action(() => Triggered?.Invoke(point)));
                        return new IntPtr(1);
                    }

                    _desktopDown = msg == WM_LBUTTONDOWN && !press.Ctrl && !press.Shift && !press.Alt
                        && Settings.Current.DesktopClick is "Single" or "Double"
                        && DesktopListAt(data.pt) is { } list
                        ? (list, data.pt, data.time)
                        : null;
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Ошибка в жестах мыши: " + ex);
        }

        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    /// <summary>
    /// Под точкой рабочий стол? Да — список его значков (Zero, если значки скрыты и клик заведомо мимо);
    /// нет — null. Только быстрые вызовы: работает внутри перехвата мыши.
    /// </summary>
    private static IntPtr? DesktopListAt(Win32.POINT pt)
    {
        var hwnd = WindowFromPoint(pt);

        if (hwnd == IntPtr.Zero || Win32.GetClassName(GetAncestor(hwnd, GA_ROOT)) is not ("Progman" or "WorkerW"))
        {
            return null;
        }

        return Win32.GetClassName(hwnd) == "SysListView32" ? hwnd : IntPtr.Zero;
    }

    /// <summary>
    /// Отпустили: чуть ждём, пока Explorer обработает клик, и смотрим, выделено ли что-нибудь.
    /// Сдвиг мыши не важен — рамка, зацепившая значок, его выделит, и клик не засчитается.
    /// </summary>
    private void CheckDesktopClick(IntPtr list, Win32.POINT pt, uint time)
    {
        var wait = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher) { Interval = TimeSpan.FromMilliseconds(40) };
        wait.Tick += (_, _) =>
        {
            wait.Stop();

            if (list != IntPtr.Zero && Win32.IsWindowVisible(list))
            {
                if (Win32.SendMessageTimeout(list, LVM_GETSELECTEDCOUNT, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 200, out var selected) == IntPtr.Zero || selected != IntPtr.Zero)
                {
                    return;
                }
            }

            var last = _desktopLast;
            var paired = last is { } l && time - l.Time <= GetDoubleClickTime();
            var isDouble = Settings.Current.DesktopClick == "Double";
            var fire = isDouble ? paired : !paired;
            _desktopLast = paired && isDouble ? null : (time, pt);

            if (fire)
            {
                AppLog.Info("Клик по пустому месту рабочего стола.");
                Triggered?.Invoke(new System.Windows.Point(pt.X, pt.Y));
            }
        };
        wait.Start();
    }

    /// <summary>Запись: первое нажатие ждёт, не будет ли второго, — так различаем одинарный и двойной.</summary>
    private void Capture(MouseBinding press, bool second)
    {
        if (second && _pending is not null && _pending.SameAs(press))
        {
            press.Double = true;
            _last = null;
            _dispatcher.BeginInvoke(new Action(() => FinishCapture(press)));
            return;
        }

        _pending = press;
        _dispatcher.BeginInvoke(new Action(() =>
        {
            _pendingTimer?.Stop();
            _pendingTimer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher) { Interval = TimeSpan.FromMilliseconds(GetDoubleClickTime() + 30) };
            _pendingTimer.Tick += (_, _) =>
            {
                var single = _pending;
                // обычный левый клик — отмена: им же нажимают кнопки в окне
                var cancel = single is { Button: "Left", Ctrl: false, Shift: false, Alt: false };
                FinishCapture(cancel ? null : single);
            };
            _pendingTimer.Start();
        }));
    }

    /// <summary>Второе нажатие той же кнопки рядом и вовремя — двойной клик.</summary>
    private bool IsSecondClick(MouseBinding press, MSLLHOOKSTRUCT data)
    {
        var last = _last;
        _last = (press.Button, data.time, data.pt);

        if (last is not { } l || l.Button != press.Button || data.time - l.Time > GetDoubleClickTime())
        {
            return false;
        }

        if (Math.Abs(data.pt.X - l.Pt.X) > GetSystemMetrics(SM_CXDOUBLECLK) || Math.Abs(data.pt.Y - l.Pt.Y) > GetSystemMetrics(SM_CYDOUBLECLK))
        {
            return false;
        }

        _last = null;
        return true;
    }

    /// <summary>Какая кнопка нажата: Left, Right, Middle, X1, X2; null — не нажатие.</summary>
    private static string? ButtonOf(int msg, IntPtr lParam, out MSLLHOOKSTRUCT data)
    {
        data = default;

        if (msg is not (WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN or WM_XBUTTONDOWN))
        {
            return null;
        }

        data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);

        return msg switch
        {
            WM_LBUTTONDOWN => "Left",
            WM_RBUTTONDOWN => "Right",
            WM_MBUTTONDOWN => "Middle",
            _ => (data.mouseData >> 16) == 2 ? "X2" : "X1",
        };
    }

    private delegate IntPtr LowLevelMouseProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public Win32.POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr extra;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int id, LowLevelMouseProc proc, IntPtr module, uint thread);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Win32.POINT pt);
    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
