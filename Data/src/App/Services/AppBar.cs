using System.Runtime.InteropServices;
using MultiWindowSwitcher.Native;
using static MultiWindowSwitcher.Native.Win32;

namespace MultiWindowSwitcher.Services;

/// <summary>
/// Панель приложения Windows — как панель задач: занятая полоса вычитается из рабочей области монитора,
/// и развёрнутые окна и программы с автопозицией считают её край границей экрана.
/// </summary>
public static class AppBar
{
    public const uint ABM_ACTIVATE = 6, ABM_WINDOWPOSCHANGED = 9;
    public const int ABN_POSCHANGED = 1, ABN_FULLSCREENAPP = 2;

    private const uint ABM_NEW = 0, ABM_REMOVE = 1, ABM_QUERYPOS = 2, ABM_SETPOS = 3;
    private const uint ABE_TOP = 1, ABE_BOTTOM = 3;

    public static bool Registered { get; private set; }

    public static void Register(IntPtr hwnd, int callbackMessage)
    {
        if (Registered)
        {
            return;
        }

        var data = Data(hwnd);
        data.uCallbackMessage = (uint)callbackMessage;
        Registered = SHAppBarMessage(ABM_NEW, ref data) != IntPtr.Zero;

        if (!Registered)
        {
            AppLog.Warn("Windows не зарегистрировала панель приложения: место на экране не зарезервировано.");
        }
    }

    public static void Remove(IntPtr hwnd)
    {
        if (!Registered)
        {
            return;
        }

        var data = Data(hwnd);
        SHAppBarMessage(ABM_REMOVE, ref data);
        Registered = false;
    }

    /// <summary>Занять полосу у края монитора. Вернёт, где встать окну.</summary>
    public static RECT Place(IntPtr hwnd, MonitorInfo monitor, bool top, int height)
    {
        var data = Query(hwnd, monitor, top, height);
        SHAppBarMessage(ABM_SETPOS, ref data);
        return data.rc;
    }

    /// <summary>Где окажется полоса сейчас, ничего не занимая: Windows учитывает панель задач и другие панели у этого края.</summary>
    public static RECT Where(IntPtr hwnd, MonitorInfo monitor, bool top, int height) => Query(hwnd, monitor, top, height).rc;

    private static APPBARDATA Query(IntPtr hwnd, MonitorInfo monitor, bool top, int height)
    {
        var data = Data(hwnd);
        data.uEdge = top ? ABE_TOP : ABE_BOTTOM;
        data.rc = monitor.Bounds;
        SHAppBarMessage(ABM_QUERYPOS, ref data);

        if (top)
        {
            data.rc.Bottom = data.rc.Top + height;
        }
        else
        {
            data.rc.Top = data.rc.Bottom - height;
        }

        return data;
    }

    /// <summary>Windows просит сообщать об активации и перемещении панели.</summary>
    public static void Notify(IntPtr hwnd, uint message)
    {
        var data = Data(hwnd);
        SHAppBarMessage(message, ref data);
    }

    private static APPBARDATA Data(IntPtr hwnd) => new() { cbSize = (uint)Marshal.SizeOf<APPBARDATA>(), hWnd = hwnd };

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    [DllImport("shell32.dll")]
    private static extern IntPtr SHAppBarMessage(uint message, ref APPBARDATA data);
}
