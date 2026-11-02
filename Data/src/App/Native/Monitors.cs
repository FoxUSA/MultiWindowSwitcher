using static MultiWindowSwitcher.Native.Win32;

namespace MultiWindowSwitcher.Native;

/// <summary>
/// Монитор: границы целиком и без панели задач, в физических пикселях. Device (\\.\DISPLAY1) Windows раздаёт заново,
/// когда монитор выключили и включили, — для запоминания монитора есть Id, он у монитора свой.
/// </summary>
public sealed record MonitorInfo(int Number, string Device, RECT Bounds, RECT Work, bool IsPrimary, double Scale, string Id);

/// <summary>
/// Мониторы по порядку: основной — первый, остальные слева направо.
/// Номер монитора показывается на плитках и в фильтре.
/// </summary>
public static class Monitors
{
    public static IReadOnlyList<MonitorInfo> All()
    {
        var raw = new List<(IntPtr Handle, MONITORINFOEX Info)>();

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr handle, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var info = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };

            if (GetMonitorInfo(handle, ref info))
            {
                raw.Add((handle, info));
            }

            return true;
        }, IntPtr.Zero);

        var ordered = raw
            .OrderByDescending(m => (m.Info.dwFlags & MONITORINFOF_PRIMARY) != 0)
            .ThenBy(m => m.Info.rcMonitor.Left)
            .ThenBy(m => m.Info.rcMonitor.Top)
            .ToList();

        var result = new List<MonitorInfo>();

        for (var i = 0; i < ordered.Count; i++)
        {
            var (handle, info) = ordered[i];
            var scale = GetDpiForMonitor(handle, 0, out var dpi, out _) == 0 ? dpi / 96.0 : 1.0;
            result.Add(new MonitorInfo(
                i + 1,
                info.szDevice,
                info.rcMonitor,
                info.rcWork,
                (info.dwFlags & MONITORINFOF_PRIMARY) != 0,
                scale,
                IdOf(info.szDevice)));
        }

        return result;
    }

    public static MonitorInfo Primary() => All().First();

    /// <summary>Запомненный монитор: по Id; имя устройства — только у старых настроек, где Id ещё нет. Не нашёлся — основной.</summary>
    public static MonitorInfo Find(string id, string device)
    {
        var all = All();
        return (id.Length > 0 ? all.FirstOrDefault(m => m.Id == id) : all.FirstOrDefault(m => m.Device == device)) ?? all.First();
    }

    /// <summary>Путь монитора как устройства («\\?\DISPLAY#SAM7179#…»): в нём модель и разъём, при переподключении он тот же.</summary>
    private static string IdOf(string device)
    {
        var display = new DISPLAY_DEVICE { cb = System.Runtime.InteropServices.Marshal.SizeOf<DISPLAY_DEVICE>() };
        return EnumDisplayDevices(device, 0, ref display, EDD_GET_DEVICE_INTERFACE_NAME) ? display.DeviceID : string.Empty;
    }

    private const uint EDD_GET_DEVICE_INTERFACE_NAME = 1;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;

        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;

        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;

        public int StateFlags;

        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;

        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string device, uint index, ref DISPLAY_DEVICE display, uint flags);

    public static MonitorInfo FromPoint(int x, int y)
    {
        var all = All();
        return all.FirstOrDefault(m => m.Bounds.Contains(x, y))
            ?? all.OrderBy(m => Distance(m.Bounds, x, y)).First();
    }

    public static MonitorInfo FromWindow(IntPtr hWnd)
    {
        var all = All();
        var handle = MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };

        if (handle != IntPtr.Zero && GetMonitorInfo(handle, ref info))
        {
            return all.FirstOrDefault(m => m.Device == info.szDevice) ?? all.First();
        }

        return all.First();
    }

    /// <summary>Номер монитора, на котором окно; для свёрнутого — где оно было развёрнуто.</summary>
    public static int NumberOf(IntPtr hWnd, IReadOnlyList<MonitorInfo> all)
    {
        RECT rect;

        if (IsIconic(hWnd))
        {
            var placement = new WINDOWPLACEMENT { length = System.Runtime.InteropServices.Marshal.SizeOf<WINDOWPLACEMENT>() };
            GetWindowPlacement(hWnd, ref placement);
            rect = placement.rcNormalPosition;
        }
        else
        {
            rect = GetVisibleBounds(hWnd);
        }

        var cx = rect.Left + rect.Width / 2;
        var cy = rect.Top + rect.Height / 2;
        return (all.FirstOrDefault(m => m.Bounds.Contains(cx, cy))
            ?? all.OrderBy(m => Distance(m.Bounds, cx, cy)).First()).Number;
    }

    /// <summary>
    /// Перенести окно на другой монитор: на то же относительное место, развёрнутое остаётся развёрнутым.
    /// Спрятанное в трей не показываем — только двигаем. false — Windows не дала (окно от администратора).
    /// </summary>
    public static bool MoveWindow(IntPtr hWnd, MonitorInfo from, MonitorInfo to)
    {
        var hidden = IsHiddenByApp(hWnd);
        var maximized = !hidden && IsZoomed(hWnd);

        if (maximized)
        {
            ShowWindow(hWnd, SW_RESTORE);
        }

        GetWindowRect(hWnd, out var r);
        var x = to.Work.Left + (int)((r.Left - from.Work.Left) / (double)from.Work.Width * to.Work.Width);
        var y = to.Work.Top + (int)((r.Top - from.Work.Top) / (double)from.Work.Height * to.Work.Height);

        if (!SetWindowPos(hWnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE))
        {
            return false;
        }

        // на мониторе с другим масштабом окно могло само поменять размер — теперь вмещаем в экран
        GetWindowRect(hWnd, out r);
        var width = Math.Min(r.Width, to.Work.Width);
        var height = Math.Min(r.Height, to.Work.Height);
        x = Math.Clamp(r.Left, to.Work.Left, to.Work.Right - width);
        y = Math.Clamp(r.Top, to.Work.Top, to.Work.Bottom - height);
        SetWindowPos(hWnd, IntPtr.Zero, x, y, width, height, SWP_NOZORDER | SWP_NOACTIVATE);

        if (maximized)
        {
            ShowWindow(hWnd, 3); // SW_MAXIMIZE
        }

        return true;
    }

    /// <summary>Есть ли за этой точкой другой монитор (тогда прятать кнопку за край нельзя — вылезет на соседнем).</summary>
    public static bool HasMonitorAt(int x, int y) =>
        MonitorFromPoint(new POINT { X = x, Y = y }, MONITOR_DEFAULTTONULL) != IntPtr.Zero;

    private static double Distance(RECT r, int x, int y)
    {
        var dx = Math.Max(Math.Max(r.Left - x, 0), x - r.Right);
        var dy = Math.Max(Math.Max(r.Top - y, 0), y - r.Bottom);
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
