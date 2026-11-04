using System.Drawing;
using System.Drawing.Imaging;
using MultiWindowSwitcher.Views.Controls;
using static MultiWindowSwitcher.Native.Win32;

namespace MultiWindowSwitcher.Services;

/// <summary>
/// Превью рисует сама Windows, и программе не сообщают, если оно опустело. Поэтому раз в пару секунд
/// смотрим на экран: правая половина превью (слева внизу крупный значок) ровно цвета подложки — картинки нет.
/// Сначала заново подключаем превью, не помогло — просим окно перерисоваться.
/// Причину воспроизвести не удалось, поэтому в лог идёт состояние окна и что сработало.
/// Тот же снимок показывает и движение: картинка окна не на виду меняется — на плитке мигает точка, затихло — гаснет.
/// </summary>
public sealed class PreviewWatch
{
    /// <summary>PreviewBrush в App.xaml.</summary>
    private const int Backdrop = 0x38;
    private const int Tolerance = 4;

    private const uint RDW_INVALIDATE = 0x0001, RDW_ERASE = 0x0004, RDW_ALLCHILDREN = 0x0080, RDW_FRAME = 0x0400;

    /// <summary>Клетка, по которой сравниваем картинку, пиксели экрана. Мелкая: одна набранная буква тоже движение.</summary>
    private const int Cell = 6;

    /// <summary>Сколько последних картинок помним: мигающий курсор возвращает уже виденную, это не движение.</summary>
    private const int Remembered = 6;

    /// <summary>Столько без изменений — движение кончилось.</summary>
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(4);

    /// <summary>Столько после начала слежения изменения не в счёт: превью только что подключили или окно ушло назад, оно дорисовывается.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(3);

    private readonly Dictionary<WindowTile, Trouble> _troubles = new();
    private readonly Dictionary<WindowTile, Motion> _motions = new();

    /// <param name="originX">Левый верхний угол содержимого переключателя, пиксели экрана.</param>
    /// <param name="originY">То же по вертикали.</param>
    /// <param name="blank">Проверить, не опустели ли превью.</param>
    /// <param name="motion">Проверить, не поменялось ли что-то в окнах не на виду.</param>
    public void Check(IReadOnlyCollection<WindowTile> tiles, int originX, int originY, bool blank, bool motion)
    {
        var shown = new List<(WindowTile Tile, Rectangle Rect)>();

        foreach (var tile in tiles)
        {
            if (tile.ThumbnailRect is { Width: > 40, Height: > 40 } r)
            {
                shown.Add((tile, new Rectangle(originX + r.Left, originY + r.Top, r.Width, r.Height)));
            }
        }

        foreach (var gone in _troubles.Keys.Where(t => shown.All(s => s.Tile != t)).ToList())
        {
            _troubles.Remove(gone);
        }

        foreach (var gone in _motions.Keys.Where(t => shown.All(s => s.Tile != t)).ToList())
        {
            gone.Item.Busy = false;
            _motions.Remove(gone);
        }

        if (shown.Count == 0)
        {
            return;
        }

        var area = shown.Select(s => s.Rect).Aggregate(Rectangle.Union);

        try
        {
            using var screen = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppArgb);

            using (var graphics = Graphics.FromImage(screen))
            {
                graphics.CopyFromScreen(area.Left, area.Top, 0, 0, area.Size);
            }

            int[]? pixels = null;

            if (motion)
            {
                var data = screen.LockBits(new Rectangle(0, 0, area.Width, area.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                pixels = new int[area.Width * area.Height];
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
                screen.UnlockBits(data);
            }

            foreach (var (tile, rect) in shown)
            {
                rect.Offset(-area.Left, -area.Top);

                if (blank)
                {
                    Judge(tile, IsBlank(screen, rect));
                }

                if (pixels is not null)
                {
                    Watch(tile, Signature(pixels, area.Width, rect));
                }
            }
        }
        catch (Exception ex)
        {
            // экран заблокирован или идёт смена режима — посмотрим в следующий раз
            AppLog.Info("Проверка превью пропущена: " + ex.Message);
        }
    }

    /// <summary>
    /// Пусто — больше половины проверенных точек цвета подложки. Не все: у окна, потерявшего картинку
    /// (видели у припаркованного ChatGPT), вверху остаётся полоса, а ниже пусто.
    /// </summary>
    private static bool IsBlank(Bitmap screen, Rectangle rect)
    {
        var points = 0;
        var empty = 0;

        foreach (var fx in new[] { 0.55, 0.7, 0.85, 0.95 })
        {
            foreach (var fy in new[] { 0.1, 0.3, 0.5, 0.7, 0.9 })
            {
                var pixel = screen.GetPixel(rect.Left + (int)(rect.Width * fx), rect.Top + (int)(rect.Height * fy));
                points++;

                if (Math.Abs(pixel.R - Backdrop) <= Tolerance && Math.Abs(pixel.G - Backdrop) <= Tolerance && Math.Abs(pixel.B - Backdrop) <= Tolerance)
                {
                    empty++;
                }
            }
        }

        return empty * 2 > points;
    }

    private void Judge(WindowTile tile, bool blank)
    {
        if (!blank)
        {
            if (_troubles.Remove(tile, out var fixedOne) && fixedOne.Step.Length > 0)
            {
                AppLog.Info($"Превью вернулось: «{tile.Item.Title}» ({tile.Item.App}) через {(DateTime.UtcNow - fixedOne.Since).TotalSeconds:0} с; последнее действие — {fixedOne.Step}.");
            }

            return;
        }

        if (!_troubles.TryGetValue(tile, out var trouble))
        {
            // первая встреча: окно могло только что открыться и ещё не нарисоваться
            _troubles[tile] = new Trouble();
            return;
        }

        trouble.Checks++;

        switch (trouble.Checks)
        {
            case 1:
                AppLog.Warn($"Пустое превью: {Describe(tile)}.");
                Reconnect(tile, trouble);
                break;
            case 2:
                Repaint(tile, trouble);
                break;
            case 3:
                AppLog.Warn($"Превью так и не вернулось: «{tile.Item.Title}» ({tile.Item.App}).");
                break;
            default:
                // тихо пробуем дальше: окно может быть и правда такого цвета
                if (trouble.Checks % 20 == 0)
                {
                    Reconnect(tile, trouble);
                    Repaint(tile, trouble);
                }

                break;
        }
    }

    /// <summary>Сумма цветов по клеткам; края не берём — по ним проходит рамка плитки.</summary>
    private static int[] Signature(int[] pixels, int stride, Rectangle rect)
    {
        var inner = Rectangle.Inflate(rect, -4, -4);
        var cols = Math.Max(1, inner.Width / Cell);
        var rows = Math.Max(1, inner.Height / Cell);
        var sums = new int[cols * rows];

        for (var y = 0; y < Math.Min(rows * Cell, inner.Height); y++)
        {
            var line = (inner.Top + y) * stride + inner.Left;
            var cell = y / Cell * cols;

            for (var x = 0; x < Math.Min(cols * Cell, inner.Width); x++)
            {
                var p = pixels[line + x];
                sums[cell + x / Cell] += (p & 0xFF) + (p >> 8 & 0xFF) + (p >> 16 & 0xFF);
            }
        }

        return sums;
    }

    /// <summary>
    /// Картинка, какой у окна не было ни разу из последних, — движение. Окно вышло вперёд или ушло назад, свернулось,
    /// плитка сдвинулась — картинка меняется сама по себе: начинаем смотреть заново.
    /// </summary>
    private void Watch(WindowTile tile, int[] signature)
    {
        var item = tile.Item;
        var key = $"{tile.ThumbnailRect?.Left},{tile.ThumbnailRect?.Top},{tile.ThumbnailRect?.Width},{tile.ThumbnailRect?.Height},{item.IsActive},{item.IsMinimized},{item.InTray}";

        if (!_motions.TryGetValue(tile, out var motion) || motion.Key != key)
        {
            item.Busy = false;
            _motions[tile] = new Motion(key, signature);
            return;
        }

        if (motion.Seen.Any(seen => seen.AsSpan().SequenceEqual(signature)))
        {
            return;
        }

        motion.Seen.Add(signature);

        if (motion.Seen.Count > Remembered)
        {
            motion.Seen.RemoveAt(0);
        }

        if (!item.IsActive && DateTime.UtcNow - motion.Since > Settle)
        {
            motion.Moved = DateTime.UtcNow;
            item.Busy = true;
        }
    }

    /// <summary>Гасит точку у окон, где давно ничего не менялось. Каждый такт, даже когда на экран не смотрим.</summary>
    public void Calm()
    {
        foreach (var (tile, motion) in _motions)
        {
            if (tile.Item.Busy && DateTime.UtcNow - motion.Moved > Quiet)
            {
                tile.Item.Busy = false;
            }
        }
    }

    private static void Reconnect(WindowTile tile, Trouble trouble)
    {
        tile.ReconnectThumbnail();
        trouble.Step = "подключили заново";
    }

    private static void Repaint(WindowTile tile, Trouble trouble)
    {
        RedrawWindow(tile.Item.Hwnd, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_ERASE | RDW_ALLCHILDREN | RDW_FRAME);
        trouble.Step = "попросили окно перерисоваться";
    }

    private static string Describe(WindowTile tile)
    {
        var hwnd = tile.Item.Hwnd;
        var size = tile.SourceSize;
        return $"«{tile.Item.Title}» ({tile.Item.App}), видимо {IsWindowVisible(hwnd)}, спрятано DWM {IsCloaked(hwnd)}, свёрнуто {IsIconic(hwnd)}, "
            + $"припарковано {Parking.IsParked(hwnd)}, размер у DWM {size.cx}×{size.cy}, стиль {GetExStyle(hwnd):X}";
    }

    private sealed class Motion(string key, int[] first)
    {
        public string Key { get; } = key;

        public List<int[]> Seen { get; } = new() { first };

        public DateTime Since { get; } = DateTime.UtcNow;

        /// <summary>Когда картинка в последний раз стала новой.</summary>
        public DateTime Moved { get; set; }
    }

    private sealed class Trouble
    {
        public DateTime Since { get; } = DateTime.UtcNow;

        public int Checks { get; set; }

        public string Step { get; set; } = string.Empty;
    }
}
