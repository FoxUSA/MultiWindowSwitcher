using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using MultiWindowSwitcher.Models;
using MultiWindowSwitcher.Native;
using MultiWindowSwitcher.Services;
using MultiWindowSwitcher.Views.Controls;
using static MultiWindowSwitcher.Native.Win32;

namespace MultiWindowSwitcher.Views;

/// <summary>
/// Крупные значки программ поверх превью. Превью рисует DWM поверх окна переключателя, поэтому
/// значки — в отдельном прозрачном окне ровно над ним: принадлежит переключателю (всегда сразу
/// над ним), клики проходят насквозь, фокус не берёт.
/// </summary>
public sealed class IconOverlay : Window
{
    private readonly Window _owner;
    private readonly Canvas _canvas = new();
    private readonly Dictionary<IntPtr, Image> _images = new();
    private IntPtr _hwnd;

    public IconOverlay(Window owner)
    {
        _owner = owner;
        Owner = owner;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        IsHitTestVisible = false;
        Content = _canvas;

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            var ex = GetExStyle(_hwnd) | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex));
        };
    }

    /// <summary>Поставить значки над превью видимых плиток; выключено или окно спрятано — убрать.</summary>
    public void Sync(IEnumerable<WindowTile> tiles)
    {
        var s = Settings.Current;
        var show = s.BigIcons && _owner.IsVisible && _owner.WindowState != WindowState.Minimized;

        if (!show)
        {
            if (IsVisible)
            {
                Hide();
            }

            return;
        }

        if (!IsVisible)
        {
            Show();
        }

        // ровно на месте переключателя (пиксели экрана), координаты значков — как у его содержимого
        var ownerHwnd = new WindowInteropHelper(_owner).Handle;
        GetWindowRect(ownerHwnd, out var r);
        SetWindowPos(_hwnd, IntPtr.Zero, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, SWP_NOZORDER | SWP_NOACTIVATE);
        KeepAboveOwner(ownerHwnd);

        var seen = new HashSet<IntPtr>();

        foreach (var tile in tiles)
        {
            // у окна из трея без снимка значок и так крупно по центру — второй в углу не нужен
            var area = tile.IsVisible && (!tile.Item.InTray || tile.ShowsSnapshot) ? tile.PreviewRect(_owner) : null;

            if (area is not { } a || a.Width < 40 || a.Height < 40)
            {
                continue;
            }

            var hwnd = tile.Item.Hwnd;
            seen.Add(hwnd);

            if (!_images.TryGetValue(hwnd, out var image))
            {
                image = new Image { Stretch = Stretch.Uniform };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                _images[hwnd] = image;
                _canvas.Children.Add(image);
            }

            image.Source = BigIcons.Of(tile.Item);

            if (WindowTile.Compact)
            {
                // лента: значок слева по центру высоты, превью сразу за ним
                var side = Math.Max(0, WindowTile.IconZoneFor(a.Height) - WindowTile.IconPad * 2);
                image.Width = image.Height = side;
                Canvas.SetLeft(image, a.Left + WindowTile.IconPad);
                Canvas.SetTop(image, a.Top + (a.Height - side) / 2);
                continue;
            }

            // не больше, чем влезает: значок не должен закрывать само окно целиком
            var size = Math.Min(s.IconSize, Math.Min(a.Height * 0.6, a.Width * 0.45));
            var pad = Math.Clamp(size * 0.14, 6, 14);
            image.Width = image.Height = size;
            Canvas.SetLeft(image, a.Left + pad);
            Canvas.SetTop(image, a.Bottom - size - pad);
        }

        foreach (var gone in _images.Keys.Where(h => !seen.Contains(h)).ToList())
        {
            _canvas.Children.Remove(_images[gone]);
            _images.Remove(gone);
        }
    }

    /// <summary>
    /// Значки должны быть прямо над переключателем. Полноэкранное окно поверх всех (снимок экрана, игра) меняет порядок
    /// окон, и слой значков мог остаться под переключателем — значки исчезали. Вставляем его сразу над ним,
    /// не выше: окна, которые закрывают переключатель, должны закрывать и значки.
    /// </summary>
    private void KeepAboveOwner(IntPtr owner)
    {
        if (WindowTracker.IsAbove(_hwnd, owner))
        {
            return;
        }

        AppLog.Info("Слой крупных значков оказался под окном переключателя — поднимаем.");
        var above = Win32.GetWindow(owner, GW_HWNDPREV);
        var after = above != IntPtr.Zero ? above : _owner.Topmost ? HWND_TOPMOST : HWND_TOP;
        SetWindowPos(_hwnd, after, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }
}
