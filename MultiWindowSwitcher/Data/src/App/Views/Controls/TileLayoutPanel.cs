using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MultiWindowSwitcher.Views.Controls;

/// <summary>
/// Сетка плиток: все ячейки одинаковые и вместе занимают всё место. Число колонок
/// подбирается так, чтобы превью в ячейке было как можно крупнее при заданных пропорциях.
/// Последний неполный ряд — из таких же ячеек, по левому краю. Окно «двойной ширины» занимает две ячейки.
/// Увеличенная плитка (Peek) занимает всё место, остальные на это время прячутся.
/// Пока перетаскивают плитку, она идёт за курсором, а остальные плавно переезжают на новые места.
/// </summary>
public sealed class TileLayoutPanel : Panel
{
    private const double Gap = 8;

    private static readonly Duration MoveTime = TimeSpan.FromMilliseconds(180);

    private readonly Dictionary<UIElement, Rect> _slots = new();
    private IReadOnlyList<UIElement> _ordered = Array.Empty<UIElement>();
    private UIElement? _peek;
    private UIElement? _dragged;
    private Vector _dragOffset;
    private int _moving;

    /// <summary>Пропорции превью (ширина / высота), под которые подбирается сетка.</summary>
    public double Aspect { get; set; } = 1.6;

    /// <summary>Высота шапки плитки: превью начинается под ней.</summary>
    public double HeaderHeight { get; set; } = 61;

    public int Columns { get; private set; }

    public int Rows { get; private set; }

    /// <summary>
    /// Видимые плитки по порядку. Остальные дети панели в раскладке не участвуют.
    /// Набор или порядок поменялся (окно открылось, закрылось, фильтр) — плитки плавно переезжают
    /// и меняют размер, а не перескакивают; при смене размера окна сетка встаёт сразу.
    /// </summary>
    public IReadOnlyList<UIElement> Ordered
    {
        get => _ordered;
        set
        {
            if (!value.SequenceEqual(_ordered) && _dragged is null && !AnimateMoves && _shown.Count > 0)
            {
                StartGlide();
            }

            _ordered = value;
            InvalidateMeasure();
        }
    }

    // ── плавная перекладка: каждый кадр плитка заново раскладывается в промежуточной ячейке —
    // текст и превью Windows остаются чёткими (масштабирование картинкой их бы размыло)

    private static readonly TimeSpan GlideTime = TimeSpan.FromMilliseconds(220);
    private readonly Dictionary<UIElement, Rect> _shown = new();
    private Dictionary<UIElement, Rect> _glideFrom = new();
    private DateTime _glideStart;
    private bool _gliding;

    private void StartGlide()
    {
        _glideFrom = new Dictionary<UIElement, Rect>(_shown);
        _glideStart = DateTime.UtcNow;

        if (!_gliding)
        {
            _gliding = true;
            CompositionTarget.Rendering += GlideFrame;
        }
    }

    private void StopGlide()
    {
        if (_gliding)
        {
            _gliding = false;
            CompositionTarget.Rendering -= GlideFrame;
        }
    }

    private double GlideProgress()
    {
        var t = Math.Clamp((DateTime.UtcNow - _glideStart).TotalMilliseconds / GlideTime.TotalMilliseconds, 0, 1);
        return 1 - Math.Pow(1 - t, 3);
    }

    private void GlideFrame(object? sender, EventArgs e)
    {
        InvalidateArrange();

        if (GlideProgress() >= 1)
        {
            StopGlide();
        }
    }

    private static Rect Lerp(Rect a, Rect b, double t) => new(
        a.X + (b.X - a.X) * t,
        a.Y + (b.Y - a.Y) * t,
        Math.Max(0, a.Width + (b.Width - a.Width) * t),
        Math.Max(0, a.Height + (b.Height - a.Height) * t));

    /// <summary>Плитка на две ячейки в ширину.</summary>
    public Func<UIElement, bool> IsWide { get; set; } = _ => false;

    public UIElement? Peek
    {
        get => _peek;
        set
        {
            _peek = value;
            InvalidateMeasure();
        }
    }

    protected override Size MeasureOverride(Size available)
    {
        var size = new Size(
            double.IsInfinity(available.Width) ? 800 : available.Width,
            double.IsInfinity(available.Height) ? 600 : available.Height);

        foreach (var (child, rect) in Layout(size))
        {
            child.Measure(rect.Size);
        }

        foreach (UIElement child in InternalChildren)
        {
            if (!child.IsMeasureValid)
            {
                child.Measure(new Size(0, 0));
            }
        }

        return size;
    }

    /// <summary>Переезды плиток анимируются только при перетаскивании — при смене размера окна сетка встаёт сразу.</summary>
    public bool AnimateMoves { get; set; }

    /// <summary>Что-то ещё едет: превью надо подтягивать каждый кадр.</summary>
    public bool IsMoving => _moving > 0 || _dragged is not null || _gliding;

    /// <summary>Ячейка плитки без учёта анимации: по ней определяют, над чем курсор.</summary>
    public Rect? SlotOf(UIElement child) => _slots.TryGetValue(child, out var rect) ? rect : null;

    /// <summary>Плитка под курсором: смещение от её ячейки, без анимации, поверх остальных.</summary>
    public void Drag(UIElement child, Vector offset)
    {
        if (_dragged != child)
        {
            StopGlide();
            _dragged = child;
            SetZIndex(child, 100);
        }

        _dragOffset = offset;
        var move = Transform(child);
        move.BeginAnimation(TranslateTransform.XProperty, null);
        move.BeginAnimation(TranslateTransform.YProperty, null);
        move.X = offset.X;
        move.Y = offset.Y;
    }

    /// <summary>Отпустили: плитка мягко садится в свою ячейку.</summary>
    public void Settle()
    {
        if (_dragged is not { } child)
        {
            return;
        }

        _dragged = null;
        var move = Transform(child);
        Animate(child, move, new Vector(move.X, move.Y), () => SetZIndex(child, 0));
    }

    protected override Size ArrangeOverride(Size final)
    {
        var placed = new HashSet<UIElement>();
        var glide = _gliding ? GlideProgress() : 1;

        foreach (var (child, rect) in Layout(final))
        {
            // новая плитка (раньше её не было видно) встаёт сразу на место, остальные едут со старого
            var shown = glide < 1 && child != _dragged && _glideFrom.TryGetValue(child, out var from)
                ? Lerp(from, rect, glide)
                : rect;

            child.Arrange(shown);
            _shown[child] = shown;
            placed.Add(child);

            var had = _slots.TryGetValue(child, out var old);
            _slots[child] = rect;

            if (child == _dragged)
            {
                // ячейка под курсором сменилась — плитка остаётся там же, где курсор
                var move = Transform(child);
                var delta = had ? old.TopLeft - rect.TopLeft : default;
                _dragOffset += delta;
                move.X = _dragOffset.X;
                move.Y = _dragOffset.Y;
            }
            else if (AnimateMoves && had && old.TopLeft != rect.TopLeft)
            {
                // плитка переехала: стартует со старого места (с учётом недоехавшей анимации) и плавно едет к новому
                var move = Transform(child);
                Animate(child, move, old.TopLeft - rect.TopLeft + new Vector(move.X, move.Y), null);
            }
        }

        foreach (UIElement child in InternalChildren)
        {
            if (!placed.Contains(child))
            {
                child.Arrange(new Rect(0, 0, 0, 0));
                _slots.Remove(child);
                _shown.Remove(child);
            }
        }

        return final;
    }

    private static TranslateTransform Transform(UIElement child)
    {
        if (child.RenderTransform is TranslateTransform move && !move.IsFrozen)
        {
            return move;
        }

        move = new TranslateTransform();
        child.RenderTransform = move;
        return move;
    }

    private void Animate(UIElement child, TranslateTransform move, Vector from, Action? done)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var x = new DoubleAnimation(from.X, 0, MoveTime) { EasingFunction = ease };
        var y = new DoubleAnimation(from.Y, 0, MoveTime) { EasingFunction = ease };

        _moving++;
        x.Completed += (_, _) =>
        {
            _moving = Math.Max(0, _moving - 1);
            done?.Invoke();
        };

        move.BeginAnimation(TranslateTransform.XProperty, x);
        move.BeginAnimation(TranslateTransform.YProperty, y);
    }

    private IEnumerable<(UIElement Child, Rect Rect)> Layout(Size size)
    {
        if (_peek is not null && InternalChildren.Contains(_peek))
        {
            yield return (_peek, new Rect(0, 0, size.Width, size.Height));
            yield break;
        }

        var items = _ordered.Where(InternalChildren.Contains).ToList();
        var n = items.Count;

        if (n == 0 || size.Width <= 0 || size.Height <= 0)
        {
            Columns = Rows = 0;
            yield break;
        }

        var wide = items.Select(IsWide).ToList();
        var (cols, rows, cellHeight, cells) = Best(wide, size);
        Columns = cols;
        Rows = rows;

        var cellWidth = (size.Width - Gap * (cols - 1)) / cols;

        for (var i = 0; i < n; i++)
        {
            var (row, col, span) = cells[i];

            yield return (items[i], new Rect(
                col * (cellWidth + Gap),
                row * (cellHeight + Gap),
                Math.Max(0, cellWidth * span + Gap * (span - 1)),
                Math.Max(0, cellHeight)));
        }
    }

    /// <summary>Сколько колонок дают самое крупное превью; пустые ячейки чуть штрафуются.</summary>
    private (int Cols, int Rows, double CellHeight, (int Row, int Col, int Span)[] Cells) Best(List<bool> wide, Size size)
    {
        var units = wide.Count + wide.Count(w => w);
        var best = (Cols: 1, Rows: 1, CellHeight: 0.0, Cells: Array.Empty<(int Row, int Col, int Span)>(), Score: double.MinValue);

        for (var cols = 1; cols <= units; cols++)
        {
            var (rows, cells) = Pack(wide, cols);
            var cw = (size.Width - Gap * (cols - 1)) / cols;
            var ch = (size.Height - Gap * (rows - 1)) / rows;
            var picture = Math.Min(cw, Math.Max(0, ch - HeaderHeight) * Aspect);
            var used = cells.Sum(c => c.Span);
            var score = picture - (rows * cols - used) * 2;

            if (score > best.Score)
            {
                best = (cols, rows, ch, cells, score);
            }
        }

        return (best.Cols, best.Rows, best.CellHeight, best.Cells);
    }

    /// <summary>
    /// Раскладка по рядам: широкое окно занимает две ячейки. Не влезает в остаток ряда —
    /// туда встаёт следующее обычное окно, чтобы не оставалось дыр.
    /// </summary>
    private static (int Rows, (int Row, int Col, int Span)[] Cells) Pack(List<bool> wide, int cols)
    {
        var cells = new (int Row, int Col, int Span)[wide.Count];
        var queue = Enumerable.Range(0, wide.Count).ToList();
        var row = 0;
        var col = 0;

        while (queue.Count > 0)
        {
            var pick = queue.FindIndex(i => SpanOf(i) <= cols - col);

            if (pick < 0)
            {
                row++;
                col = 0;
                continue;
            }

            var index = queue[pick];
            queue.RemoveAt(pick);
            var span = SpanOf(index);
            cells[index] = (row, col, span);
            col += span;

            if (col >= cols)
            {
                row++;
                col = 0;
            }
        }

        return (col == 0 ? row : row + 1, cells);

        int SpanOf(int i) => wide[i] && cols >= 2 ? 2 : 1;
    }
}
