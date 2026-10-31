using System.Windows.Threading;
using MultiWindowSwitcher.Models;
using MultiWindowSwitcher.Native;

namespace MultiWindowSwitcher.Services;

/// <summary>
/// Активные углы: мышь упёрлась в выбранный угол любого монитора — короткая задержка,
/// чтобы не срабатывать, когда мышь просто пролетает, — и событие. Курсор опрашивается
/// раз в 30 мс: это дешевле хуков мыши и не мешает другим программам.
/// </summary>
public sealed class HotCorners
{
    private const int Hot = 3;
    private static readonly TimeSpan Dwell = TimeSpan.FromMilliseconds(120);

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(30) };
    private IReadOnlyList<MonitorInfo> _monitors = Array.Empty<MonitorInfo>();
    private DateTime _monitorsAt;
    private string? _corner;
    private DateTime _enteredAt;
    private bool _fired;

    public HotCorners() => _timer.Tick += (_, _) => Poll();

    /// <summary>Сторона (L/R), с которой выезжать, и точка угла в пикселях.</summary>
    public event Action<char, System.Windows.Point>? Triggered;

    public void Start() => _timer.Start();

    public void Stop() => _timer.Stop();

    private void Poll()
    {
        var s = Settings.Current;

        if (!s.CornerTopLeft && !s.CornerTopRight && !s.CornerBottomLeft && !s.CornerBottomRight)
        {
            return;
        }

        if (DateTime.UtcNow - _monitorsAt > TimeSpan.FromSeconds(3))
        {
            _monitors = Monitors.All();
            _monitorsAt = DateTime.UtcNow;
        }

        var p = Win32.CursorPos();
        var corner = CornerAt(p.X, p.Y);

        if (corner != _corner)
        {
            _corner = corner;
            _enteredAt = DateTime.UtcNow;
            _fired = false;
            return;
        }

        if (corner is null || _fired || DateTime.UtcNow - _enteredAt < Dwell)
        {
            return;
        }

        // кнопка мыши зажата — человек что-то тащит, угол тут ни при чём
        if (Win32.IsKeyDown(Win32.VK_LBUTTON))
        {
            return;
        }

        var enabled = corner switch
        {
            "tl" => s.CornerTopLeft,
            "tr" => s.CornerTopRight,
            "bl" => s.CornerBottomLeft,
            _ => s.CornerBottomRight,
        };

        if (!enabled)
        {
            return;
        }

        _fired = true;
        Triggered?.Invoke(corner[1] == 'l' ? 'L' : 'R', new System.Windows.Point(p.X, p.Y));
    }

    private string? CornerAt(int x, int y)
    {
        foreach (var m in _monitors)
        {
            var b = m.Bounds;

            if (!b.Contains(x, y))
            {
                continue;
            }

            var left = x <= b.Left + Hot;
            var right = x >= b.Right - 1 - Hot;
            var top = y <= b.Top + Hot;
            var bottom = y >= b.Bottom - 1 - Hot;

            // угол засчитывается, только если мышь упирается в край, а не уходит на соседний монитор
            if (top && left && !Monitors.HasMonitorAt(x - 2, y) && !Monitors.HasMonitorAt(x, y - 2))
            {
                return "tl";
            }

            if (top && right && !Monitors.HasMonitorAt(x + 2, y) && !Monitors.HasMonitorAt(x, y - 2))
            {
                return "tr";
            }

            if (bottom && left && !Monitors.HasMonitorAt(x - 2, y) && !Monitors.HasMonitorAt(x, y + 2))
            {
                return "bl";
            }

            if (bottom && right && !Monitors.HasMonitorAt(x + 2, y) && !Monitors.HasMonitorAt(x, y + 2))
            {
                return "br";
            }
        }

        return null;
    }
}
