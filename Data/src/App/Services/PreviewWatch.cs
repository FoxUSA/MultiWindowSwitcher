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
/// </summary>
public sealed class PreviewWatch
{
    /// <summary>PreviewBrush в App.xaml.</summary>
    private const int Backdrop = 0x38;
    private const int Tolerance = 4;

    private const uint RDW_INVALIDATE = 0x0001, RDW_ERASE = 0x0004, RDW_ALLCHILDREN = 0x0080, RDW_FRAME = 0x0400;

    private readonly Dictionary<WindowTile, Trouble> _troubles = new();

    /// <param name="originX">Левый верхний угол содержимого переключателя, пиксели экрана.</param>
    /// <param name="originY">То же по вертикали.</param>
    public void Check(IReadOnlyCollection<WindowTile> tiles, int originX, int originY)
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

            foreach (var (tile, rect) in shown)
            {
                rect.Offset(-area.Left, -area.Top);
                Judge(tile, IsBlank(screen, rect));
            }
        }
        catch (Exception ex)
        {
            // экран заблокирован или идёт смена режима — посмотрим в следующий раз
            AppLog.Info("Проверка превью пропущена: " + ex.Message);
        }
    }

    private static bool IsBlank(Bitmap screen, Rectangle rect)
    {
        foreach (var fx in new[] { 0.55, 0.7, 0.85, 0.95 })
        {
            foreach (var fy in new[] { 0.1, 0.3, 0.5, 0.7, 0.9 })
            {
                var pixel = screen.GetPixel(rect.Left + (int)(rect.Width * fx), rect.Top + (int)(rect.Height * fy));

                if (Math.Abs(pixel.R - Backdrop) > Tolerance || Math.Abs(pixel.G - Backdrop) > Tolerance || Math.Abs(pixel.B - Backdrop) > Tolerance)
                {
                    return false;
                }
            }
        }

        return true;
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

    private sealed class Trouble
    {
        public DateTime Since { get; } = DateTime.UtcNow;

        public int Checks { get; set; }

        public string Step { get; set; } = string.Empty;
    }
}
