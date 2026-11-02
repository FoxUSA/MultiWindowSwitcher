using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MultiWindowSwitcher.Core;
using MultiWindowSwitcher.Models;
using static MultiWindowSwitcher.Native.Win32;

namespace MultiWindowSwitcher.Services;

/// <summary>
/// Последний снимок окна для программ, которые прячутся в трей. Спрятанное окно Windows не рисует —
/// живого превью у него нет, поэтому, пока окно на экране, каждые 2 с его тихо снимаем
/// (PrintWindow, в фоне, уменьшенной копией). Ушло в трей или свернулось — на плитке остаётся последний снимок.
/// Снимок — на программу (telegram.exe), а не на окно: переживает перезапуск и её, и наш.
/// На диск (UserData\snapshots) пишем, только когда окно ушло в трей и при выходе: UserData
/// часто лежит в Dropbox, писать туда каждые 2 с нельзя.
/// </summary>
public sealed class Snapshots
{
    private const uint PW_RENDERFULLCONTENT = 2;
    private const int MaxWidth = 960;

    private readonly Dictionary<string, (BitmapSource Image, DateTime At)> _shots = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dirty = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _onScreen = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly Func<IEnumerable<WindowItem>> _windows;
    private int _busy;

    public Snapshots(Func<IEnumerable<WindowItem>> windows)
    {
        _windows = windows;
        _timer.Tick += (_, _) => Tick();
    }

    private static string Folder => Path.Combine(AppPaths.UserDataDir, "snapshots");

    /// <summary>Появился новый снимок — плиткам окон в трее стоит перерисоваться.</summary>
    public event Action? Updated;

    public void Start()
    {
        Load();
        _timer.Start();
    }

    /// <summary>Выход: несохранённые снимки — на диск.</summary>
    public void Stop()
    {
        _timer.Stop();

        foreach (var app in _dirty.ToList())
        {
            Save(app);
        }
    }

    public (BitmapSource Image, DateTime At)? Of(WindowItem item) => _shots.TryGetValue(item.App, out var s) ? s : null;

    private void Tick()
    {
        var s = Settings.Current;

        if (!s.TrayWindows || !s.TraySnapshots)
        {
            return;
        }

        var all = _windows().ToList();

        // окно, которое только что было на экране, ушло в трей — его последний снимок на диск
        foreach (var item in all.Where(w => (w.InTray || w.IsMinimized) && _onScreen.Contains(w.App)))
        {
            _onScreen.Remove(item.App);
            Save(item.App);
        }

        var targets = all
            .Where(w => !w.InTray && !w.IsMinimized && !IsHiddenByApp(w.Hwnd) && !IsIconic(w.Hwnd) && TrayIcons.Find(w.AppTitle, w.App, w.Title) is not null)
            .Select(w => (w.Hwnd, w.App))
            .ToList();

        foreach (var (_, app) in targets)
        {
            _onScreen.Add(app);
        }

        if (targets.Count == 0 || Interlocked.Exchange(ref _busy, 1) == 1)
        {
            return;
        }

        var dispatcher = Application.Current.Dispatcher;

        Task.Run(() =>
        {
            var taken = new List<(string, BitmapSource)>();

            foreach (var (hwnd, app) in targets)
            {
                var image = Capture(hwnd);

                if (image is not null)
                {
                    taken.Add((app, image));
                }
            }

            dispatcher.BeginInvoke(new Action(() =>
            {
                foreach (var (app, image) in taken)
                {
                    _shots[app] = (image, DateTime.Now);
                    _dirty.Add(app);
                }

                Interlocked.Exchange(ref _busy, 0);

                if (taken.Count > 0)
                {
                    Updated?.Invoke();
                }
            }));
        });
    }

    private void Save(string app)
    {
        if (!_shots.TryGetValue(app, out var shot) || !_dirty.Remove(app))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Folder);
            var path = Path.Combine(Folder, app + ".png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(shot.Image));

            using (var file = File.Create(path))
            {
                encoder.Save(file);
            }

            File.SetLastWriteTime(path, shot.At);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Снимок окна не записан: " + app + " — " + ex.Message);
        }
    }

    private void Load()
    {
        try
        {
            if (!Directory.Exists(Folder))
            {
                return;
            }

            foreach (var path in Directory.GetFiles(Folder, "*.png"))
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(path);
                image.EndInit();
                image.Freeze();
                _shots[Path.GetFileNameWithoutExtension(path)] = (image, File.GetLastWriteTime(path));
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Снимки окон не прочитались: " + ex.Message);
        }
    }

    /// <summary>Снимок окна уменьшенной копией; чёрный (программа не отрисовалась) — не годится.</summary>
    private static BitmapSource? Capture(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var r))
        {
            return null;
        }

        int w = r.Right - r.Left, h = r.Bottom - r.Top;

        if (w < 50 || h < 50 || w > 16000 || h > 16000)
        {
            return null;
        }

        var screen = GetDC(IntPtr.Zero);
        var dc = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, w, h);
        var old = SelectObject(dc, bitmap);

        try
        {
            if (!PrintWindow(hwnd, dc, PW_RENDERFULLCONTENT))
            {
                return null;
            }

            SelectObject(dc, old);
            var full = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var scale = Math.Min(1.0, (double)MaxWidth / w);
            BitmapSource image = scale < 1 ? new TransformedBitmap(full, new ScaleTransform(scale, scale)) : full;
            image = new WriteableBitmap(image);
            image.Freeze();
            return IsBlack(image) ? null : image;
        }
        catch
        {
            return null;
        }
        finally
        {
            SelectObject(dc, old);
            DeleteObject(bitmap);
            DeleteDC(dc);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private static bool IsBlack(BitmapSource image)
    {
        var bgra = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
        var pixels = new byte[stride * h];
        bgra.CopyPixels(pixels, stride, 0);

        for (var i = 0; i < pixels.Length; i += 4 * 97)
        {
            if (pixels[i] > 12 || pixels[i + 1] > 12 || pixels[i + 2] > 12)
            {
                return false;
            }
        }

        return true;
    }

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr dc);
}
