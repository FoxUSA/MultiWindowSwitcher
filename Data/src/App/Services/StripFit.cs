using MultiWindowSwitcher.Models;
using MultiWindowSwitcher.Native;
using static MultiWindowSwitcher.Native.Win32;

namespace MultiWindowSwitcher.Services;

/// <summary>
/// Окна, зашедшие на полосу ленты, подгоняем сами, и развёрнутые тоже (если место не зарезервировано, Windows их не
/// подстроит): край окна встаёт по краю ленты, другая сторона остаётся на месте. Не уменьшается — окно сдвигается целиком. Ленту убрали — окна получают прежний размер; если человек успел подвинуть окно сам, не трогаем.
/// Мешает окнам только часть полосы до последней плитки: правее лента прозрачна для кликов, и окно там никому не мешает.
/// </summary>
public sealed class StripFit
{
    private const int MinHeight = 150;

    /// <summary>Что было до подгонки и что стало: совпадает с нынешним — окно никто не трогал и его можно вернуть.</summary>
    private readonly Dictionary<IntPtr, (RECT Original, RECT Applied)> _saved = new();

    /// <summary>Не влезло или Windows не дала (окно от администратора): больше не пробуем, пока его не подвинут.</summary>
    private readonly HashSet<IntPtr> _gaveUp = new();

    /// <summary>Программа упрямо возвращает окно обратно: сколько раз подгоняли и с какого момента считаем.</summary>
    private readonly Dictionary<IntPtr, (int Count, DateTime Since)> _tries = new();

    private RECT? _band;
    private bool _top;

    /// <summary>Сколько пикселей ленты от левого края заняты плитками; пока не известно — окна не трогаем.</summary>
    private int? _used;

    /// <summary>Лента встала сюда. То же место — ничего не делаем; другое — вернуть окна и подогнать заново.</summary>
    public void Apply(RECT band, bool top, IEnumerable<WindowItem> windows)
    {
        if (_band is { } now && Same(now, band) && _top == top)
        {
            return;
        }

        RestoreAll();
        _band = band;
        _top = top;
        FitAll(windows);
    }

    /// <summary>Ленты больше нет: окна получают прежний размер.</summary>
    public void Release()
    {
        RestoreAll();
        _band = null;
    }

    /// <summary>Плиток стало больше или меньше: занятая часть ленты теперь другая.</summary>
    public void SetUsed(int width, IEnumerable<WindowItem> windows)
    {
        if (_used == width)
        {
            return;
        }

        _used = width;
        FitAll(windows);
    }

    /// <summary>Окно отпустили после перетаскивания: можно пробовать снова.</summary>
    public void Moved(IntPtr hwnd)
    {
        _gaveUp.Remove(hwnd);
        _tries.Remove(hwnd);
    }

    /// <summary>Подогнать всё, что зашло на ленту. Пока кнопка мыши нажата, не трогаем: окно могут как раз тащить.</summary>
    public void FitAll(IEnumerable<WindowItem> windows)
    {
        if (_band is not { } full || _used is not { } used || IsKeyDown(VK_LBUTTON))
        {
            return;
        }

        var band = new RECT(full.Left, full.Top, Math.Min(full.Left + used, full.Right), full.Bottom);
        RestoreFreed(band);

        foreach (var item in windows.ToList())
        {
            Fit(item, band);
        }
    }

    /// <summary>Плитки ушли, и прежнему месту окна лента больше не мешает: возвращаем.</summary>
    private void RestoreFreed(RECT band)
    {
        foreach (var (hwnd, saved) in _saved.ToList())
        {
            if (!Overlaps(saved.Original, band))
            {
                Restore(hwnd, saved);
                _saved.Remove(hwnd);
                _gaveUp.Remove(hwnd);
                _tries.Remove(hwnd);
            }
        }
    }

    private void Fit(WindowItem item, RECT band)
    {
        var hwnd = item.Hwnd;

        if (_gaveUp.Contains(hwnd) || item.InTray || item.IsMinimized || !IsWindow(hwnd) || IsIconic(hwnd)
            || Parking.IsParked(hwnd) || IsHiddenByApp(hwnd) || (GetExStyle(hwnd) & WS_EX_TOPMOST) != 0)
        {
            return;
        }

        var visible = GetVisibleBounds(hwnd);

        if (!Overlaps(visible, band))
        {
            return;
        }

        var monitor = Monitors.FromWindow(hwnd);
        var screen = monitor.Bounds;

        // на весь экран: игра, видео — это не наше дело
        if (visible.Left <= screen.Left && visible.Top <= screen.Top && visible.Right >= screen.Right && visible.Bottom >= screen.Bottom)
        {
            return;
        }

        // окно упрямится: не дёргаем его каждые полсекунды
        var tries = _tries.TryGetValue(hwnd, out var seen) && (DateTime.UtcNow - seen.Since).TotalSeconds < 10 ? seen : (Count: 0, Since: DateTime.UtcNow);

        if (tries.Count >= 3)
        {
            AppLog.Info($"Окно само возвращается на ленту, больше не подгоняем: «{item.Title}» ({item.App}).");
            _gaveUp.Add(hwnd);
            return;
        }

        _tries[hwnd] = (tries.Count + 1, tries.Since);

        GetWindowRect(hwnd, out var r);

        if (_saved.TryGetValue(hwnd, out var known) && !Same(known.Applied, r))
        {
            _saved.Remove(hwnd);
        }

        var original = _saved.TryGetValue(hwnd, out known) ? known.Original : r;
        var delta = _top ? band.Bottom - visible.Top : visible.Bottom - band.Top;
        var keepsSize = r.Height - delta < (int)(MinHeight * monitor.Scale);
        var y = _top ? r.Top + delta : keepsSize ? r.Top - delta : r.Top;
        var height = keepsSize ? r.Height : r.Height - delta;

        SetWindowPos(hwnd, IntPtr.Zero, r.Left, y, r.Width, height, SWP_NOZORDER | SWP_NOACTIVATE);

        // у окна есть наименьший размер: край не поднялся — поднимаем окно целиком, но не выше экрана
        visible = GetVisibleBounds(hwnd);

        if (!_top && Overlaps(visible, band))
        {
            GetWindowRect(hwnd, out var now);
            var up = Math.Min(visible.Bottom - band.Top, visible.Top - monitor.Work.Top);

            if (up > 0)
            {
                SetWindowPos(hwnd, IntPtr.Zero, now.Left, now.Top - up, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            }
        }

        GetWindowRect(hwnd, out var result);

        if (Same(result, r))
        {
            AppLog.Info($"Окно не подогналось под ленту: «{item.Title}» ({item.App}).");
            _gaveUp.Add(hwnd);
            return;
        }

        if (Overlaps(GetVisibleBounds(hwnd), band))
        {
            _gaveUp.Add(hwnd);
        }

        _saved[hwnd] = (original, result);
    }

    private void RestoreAll()
    {
        foreach (var (hwnd, saved) in _saved)
        {
            Restore(hwnd, saved);
        }

        _saved.Clear();
        _gaveUp.Clear();
        _tries.Clear();
    }

    private static void Restore(IntPtr hwnd, (RECT Original, RECT Applied) saved)
    {
        if (!IsWindow(hwnd) || IsIconic(hwnd))
        {
            return;
        }

        GetWindowRect(hwnd, out var r);

        if (Same(r, saved.Applied))
        {
            var o = saved.Original;
            SetWindowPos(hwnd, IntPtr.Zero, o.Left, o.Top, o.Width, o.Height, SWP_NOZORDER | SWP_NOACTIVATE);
        }
    }

    private static bool Overlaps(RECT a, RECT b) => a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;

    private static bool Same(RECT a, RECT b) => a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;
}
