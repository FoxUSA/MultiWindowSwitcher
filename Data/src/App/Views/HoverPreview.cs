using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MultiWindowSwitcher.Models;
using MultiWindowSwitcher.Native;
using static MultiWindowSwitcher.Native.Win32;

namespace MultiWindowSwitcher.Views;

/// <summary>
/// Лента: большое превью окна под мышью, рядом с лентой. Это своё окно поверх всех: фокус не берёт, клики
/// проходят насквозь. Картинку рисует DWM, у окна в трее живой картинки нет — показывается последний снимок.
/// </summary>
public sealed class HoverPreview : Window
{
    private const int WM_NCHITTEST = 0x0084, HTTRANSPARENT = -1;
    private const int DWMWA_TRANSITIONS_FORCEDISABLED = 3;
    private const double Gap = 8, Frame = 3;

    private readonly Image _snapshot = new() { Stretch = Stretch.Uniform };
    private IntPtr _hwnd;
    private IntPtr _thumb;

    public HoverPreview()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        Topmost = true;
        Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));
        Content = new Grid { Children = { _snapshot } };

        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(GetExStyle(_hwnd) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT));
        HwndSource.FromHwnd(_hwnd)?.AddHook((IntPtr _, int msg, IntPtr _, IntPtr _, ref bool handled) =>
        {
            if (msg != WM_NCHITTEST)
            {
                return IntPtr.Zero;
            }

            handled = true;
            return new IntPtr(HTTRANSPARENT);
        });

        var disabled = 1;
        DwmSetWindowAttribute(_hwnd, DWMWA_TRANSITIONS_FORCEDISABLED, ref disabled, sizeof(int));
    }

    /// <summary>
    /// Размер картинки в пикселях экрана. Настройка задаёт долю рабочей области монитора: так на широком мониторе и на
    /// обычном превью одинаково заметно. Окно целиком влезает в неё с сохранением пропорций, но не крупнее, чем оно есть
    /// на самом деле с учётом масштаба мониторов: увеличенная сверх этого картинка была бы только мутнее.
    /// </summary>
    public static (int Width, int Height) Fit(int sourceWidth, int sourceHeight, RECT work, double share, double largest)
    {
        var scale = Math.Min(Math.Min(work.Width * share / sourceWidth, work.Height * share / sourceHeight), largest);
        return (Math.Max(1, (int)(sourceWidth * scale)), Math.Max(1, (int)(sourceHeight * scale)));
    }

    /// <summary>Показать окно рядом с лентой, напротив плитки.</summary>
    /// <param name="item">Окно.</param>
    /// <param name="snapshot">Снимок на случай, если окно в трее.</param>
    /// <param name="tileLeft">Левый край плитки, пиксели экрана.</param>
    /// <param name="tileRight">Правый край плитки.</param>
    /// <param name="monitor">Монитор ленты.</param>
    /// <param name="top">Лента вверху: превью под ней; внизу — над ней.</param>
    /// <param name="share">Доля рабочей области, 0..1.</param>
    /// <param name="leftEdge">Поставить левым краем сюда, а не напротив плитки.</param>
    public void ShowFor(WindowItem item, BitmapSource? snapshot, int tileLeft, int tileRight, MonitorInfo monitor, bool top, double share, int? leftEdge = null)
    {
        Release();

        // рабочая область свежая: её только что уменьшила сама лента
        var work = Monitors.All().FirstOrDefault(m => m.Device == monitor.Device)?.Work ?? monitor.Work;
        var live = !item.InTray && !IsHiddenByApp(item.Hwnd);
        var size = default(SIZE);

        if (live && DwmRegisterThumbnail(_hwnd, item.Hwnd, out _thumb) == 0)
        {
            DwmQueryThumbnailSourceSize(_thumb, out size);
        }

        (int Width, int Height) picture;

        if (size.cx > 0 && size.cy > 0)
        {
            picture = Fit(size.cx, size.cy, work, share, monitor.Scale / Monitors.FromWindow(item.Hwnd).Scale);
        }
        else if (snapshot is { PixelWidth: > 0, PixelHeight: > 0 })
        {
            // снимок хранится уменьшенным: ему можно чуть подрасти
            picture = Fit(snapshot.PixelWidth, snapshot.PixelHeight, work, share, monitor.Scale);
        }
        else
        {
            Hide();
            Release();
            return;
        }

        _snapshot.Source = _thumb == IntPtr.Zero ? snapshot : null;
        _snapshot.Margin = new Thickness(Frame);

        var frame = (int)(Frame * monitor.Scale);
        var gap = (int)(Gap * monitor.Scale);
        var width = picture.Width + frame * 2;
        var height = picture.Height + frame * 2;
        var left = Math.Clamp(leftEdge ?? (tileLeft + tileRight - width) / 2, work.Left + gap, Math.Max(work.Left + gap, work.Right - gap - width));
        var y = top ? work.Top + gap : work.Bottom - gap - height;

        if (!IsVisible)
        {
            // сначала место, потом показ: окно не должно мелькнуть там, где было
            SetWindowPos(_hwnd, HWND_TOPMOST, left, y, width, height, SWP_NOACTIVATE);
            Show();
        }

        // при первом показе на мониторе с другим масштабом размер мог поменяться — ставим ещё раз
        SetWindowPos(_hwnd, HWND_TOPMOST, left, y, width, height, SWP_NOACTIVATE);

        if (_thumb != IntPtr.Zero)
        {
            var props = new DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = DWM_TNP_RECTDESTINATION | DWM_TNP_VISIBLE | DWM_TNP_OPACITY | DWM_TNP_SOURCECLIENTAREAONLY,
                rcDestination = new RECT(frame, frame, frame + picture.Width, frame + picture.Height),
                opacity = 255,
                fVisible = true,
                fSourceClientAreaOnly = false,
            };
            DwmUpdateThumbnailProperties(_thumb, ref props);
        }
    }

    public void HideNow()
    {
        if (IsVisible)
        {
            Hide();
        }

        Release();
    }

    private void Release()
    {
        if (_thumb != IntPtr.Zero)
        {
            DwmUnregisterThumbnail(_thumb);
            _thumb = IntPtr.Zero;
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
