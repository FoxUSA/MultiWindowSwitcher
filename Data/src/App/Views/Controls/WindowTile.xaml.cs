using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MultiWindowSwitcher.Core;
using MultiWindowSwitcher.Models;
using static MultiWindowSwitcher.Native.Win32;

namespace MultiWindowSwitcher.Views.Controls;

/// <summary>Что плитка просит у окна-переключателя.</summary>
public interface ITileHost
{
    void SwitchTo(WindowItem item);

    void TileClicked(WindowItem item);

    void Rename(WindowTile tile);

    void ClearName(WindowItem item);

    void TogglePin(WindowItem item);

    void ToggleWide(WindowItem item);

    void TileHover(WindowTile tile, bool over);

    void MoveToNextMonitor(WindowItem item);

    void Peek(WindowTile tile);

    void Unpeek();

    bool IsPeeking(WindowTile tile);

    void HideWindow(WindowItem item);

    void CloseWindow(WindowItem item);

    void ShowMenu(WindowTile tile);

    void TileDragStart(WindowTile tile, Point grab);

    void TileDragOver(WindowTile tile, Point screen);

    void TileDragEnd(WindowTile tile, bool commit);
}

/// <summary>
/// Плитка одного окна. Живое превью — это DWM thumbnail: Windows сама рисует окно-источник
/// в прямоугольнике PreviewHost и обновляет его вживую, без копирования картинок программой.
/// </summary>
public partial class WindowTile : UserControl
{
    private static readonly TimeSpan ClickDelay = TimeSpan.FromMilliseconds(230);

    private readonly ITileHost _host;
    private readonly DispatcherTimer _clickTimer;
    private readonly DispatcherTimer _attentionTimer;
    private IntPtr _thumb;
    private RECT _lastDest;
    private bool _lastVisible;
    private bool _thinLogged;

    /// <summary>Шире этого превью считается ошибкой, а не окном: у свёрнутого окна пропорции около 6:1.</summary>
    private const int MaxThumbnailAspect = 8;
    private SIZE _source;
    private Point _down;
    private bool _pressed;
    private bool _dragging;
    private bool _dropTarget;
    private Storyboard? _pulse;

    public WindowTile(WindowItem item, ITileHost host)
    {
        InitializeComponent();

        Item = item;
        _host = host;

        _clickTimer = new DispatcherTimer { Interval = ClickDelay };
        _clickTimer.Tick += (_, _) =>
        {
            _clickTimer.Stop();
            _host.TileClicked(Item);
        };

        // навёл на плитку с событием и подержал — значит, увидел: метка гаснет
        _attentionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _attentionTimer.Tick += (_, _) =>
        {
            _attentionTimer.Stop();
            Item.Attention = false;
        };

        item.PropertyChanged += OnItemChanged;
        Loc.I.PropertyChanged += (_, _) => Paint();
        SizeChanged += (_, _) =>
        {
            FitName();
            PaintCompact();
            ApplyCompactPreview();
        };
        Unloaded += (_, _) => Detach();

        ApplyCompact();
    }

    /// <summary>Перестроить плитку под обычный вид или ленту.</summary>
    public void ApplyCompact()
    {
        FootRow.Height = new GridLength(Compact ? 0 : 30);
        FootBar.Visibility = Compact ? Visibility.Collapsed : Visibility.Visible;
        CompactActions.Visibility = Compact ? Visibility.Visible : Visibility.Collapsed;

        // в ленте шапка узкая: двойная ширина там ни к чему, а вместо лупы — большое превью при наведении.
        // Заголовку разрешено уступить место кнопкам целиком
        WideButton.Visibility = PeekButton.Visibility = Compact ? Visibility.Collapsed : Visibility.Visible;
        TitleText.MinWidth = Compact ? 0 : 48;
        ApplyCompactPreview();
        Paint();
    }

    public WindowItem Item { get; }

    public bool HasThumbnail => _thumb != IntPtr.Zero;

    /// <summary>Мониторов больше одного — тогда на плитках виден номер монитора.</summary>
    public static bool ShowMonitor { get; set; }

    /// <summary>Заголовки окон без своего имени показываются крупно, на месте имени.</summary>
    public static bool BigTitles { get; set; }

    /// <summary>Лента: в высоту только окно — от плитки остаётся одна шапка, имя в ней же.</summary>
    public static bool Compact { get; set; }

    /// <summary>Лента: поле вокруг значка программы.</summary>
    public const double IconPad = 6;

    /// <summary>Лента: плитка не уже пяти кнопок шапки при наведении; маленький значок в шапке есть, только если нет крупного.</summary>
    private static double CompactMinWidth => Settings.Current.BigIcons ? 141 : 165;

    /// <summary>В ленте крупный значок уже стоит у превью — второй в шапке не нужен.</summary>
    private static bool HeaderIconHidden => Compact && Settings.Current.BigIcons;

    /// <summary>Лента: сколько места занимает значок слева от превью при такой высоте превью.</summary>
    public static double IconZoneFor(double previewHeight) =>
        Settings.Current.BigIcons ? Math.Max(0, Math.Min(Settings.Current.StripIconSize, previewHeight - IconPad * 2)) + IconPad * 2 : 0;

    /// <summary>Лента: ширина плитки — значок и сразу превью в пропорциях окна.</summary>
    public double CompactWidth(double cellHeight)
    {
        var preview = cellHeight - 32; // шапка и рамка
        // справа только рамка и поле превью: плитка кончается сразу за картинкой
        var width = IconZoneFor(preview) + Math.Max(0, preview - 8) * SourceAspect() + 7;
        return Math.Max(CompactMinWidth, width);
    }

    private double SourceAspect()
    {
        var shot = ShowsSnapshot ? SnapshotOf?.Invoke(Item) : null;
        var aspect = shot is { } s && s.Image.PixelHeight > 0 ? (double)s.Image.PixelWidth / s.Image.PixelHeight
            : _source.cx > 0 && _source.cy > 0 ? (double)_source.cx / _source.cy
            : 1.6;
        return Math.Clamp(aspect, 0.3, 4);
    }

    /// <summary>Снимок и запасной значок в ленте — тоже рядом со значком слева, а не по центру.</summary>
    private void ApplyCompactPreview()
    {
        var left = Compact ? Math.Max(4, IconZoneFor(PreviewHost.ActualHeight)) : 4;
        Snapshot.Margin = new Thickness(left, 4, 4, 4);
        Snapshot.HorizontalAlignment = Compact ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        FallbackIcon.Margin = new Thickness(Compact ? 4 : 0);
        FallbackIcon.HorizontalAlignment = Compact ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
    }

    /// <summary>Последний снимок окна (для окон в трее): изображение и когда снято.</summary>
    public static Func<WindowItem, (System.Windows.Media.Imaging.BitmapSource Image, DateTime At)?>? SnapshotOf { get; set; }

    /// <summary>
    /// Окно в трее или свёрнуто, и есть его снимок — показываем снимок. Живое превью свёрнутых окон у таких программ
    /// ломается: у ChatGPT вместо картинки шла полоса поперёк соседних плиток.
    /// </summary>
    public bool ShowsSnapshot => (Item.InTray || Item.IsMinimized) && Settings.Current.TraySnapshots && SnapshotOf?.Invoke(Item) is not null;

    /// <summary>Сначала программа: «Google Chrome - Панель» вместо «Панель - Google Chrome».</summary>
    public static bool AppFirst { get; set; } = true;

    private static readonly string[] Separators = { " - ", " — ", " – ", " | " };

    /// <summary>Что показывать в названии: full, window, app.</summary>
    public static string TitlePart { get; set; } = "full";

    /// <summary>
    /// Заголовок для показа. Последняя часть после разделителя — обычно имя программы, остальное — окно
    /// (вкладка, файл, папка). Нет разделителя — показываем как есть.
    /// </summary>
    public static string Display(string title)
    {
        if (string.IsNullOrEmpty(title))
        {
            return title;
        }

        var (at, sep) = Separators
            .Select(x => (At: title.LastIndexOf(x, StringComparison.Ordinal), Sep: x))
            .MaxBy(x => x.At);

        if (at <= 0 || at + sep.Length >= title.Length)
        {
            return title;
        }

        var window = title[..at].Trim();
        var app = title[(at + sep.Length)..].Trim();

        return TitlePart switch
        {
            "window" => window,
            "app" => app,
            _ => AppFirst ? app + sep + window : title,
        };
    }

    public static double TitleScale { get; set; } = 1;

    public static Brush TitleBrush { get; set; } = Brushes.White;

    /// <summary>В полосе крупно стоит заголовок окна, а не своё имя.</summary>
    public bool ShowsTitleBanner => !Item.HasName && BigTitles && !Item.NoBigTitle && Item.Title.Length > 0;

    public bool DropTarget
    {
        get => _dropTarget;
        set
        {
            _dropTarget = value;
            PaintFrame();
        }
    }

    // ── превью ───────────────────────────────────────────────────────────────

    /// <summary>Подключить живое превью к окну-переключателю.</summary>
    private IntPtr _hostHwnd;

    public void Attach(IntPtr hostHwnd)
    {
        _hostHwnd = hostHwnd;

        // окно в трее невидимо — Windows его не рисует, превью было бы пустым: вместо него снимок или значок
        if (_thumb != IntPtr.Zero || Item.InTray || ShowsSnapshot)
        {
            UpdateThumbnail();
            return;
        }

        var hr = DwmRegisterThumbnail(hostHwnd, Item.Hwnd, out _thumb);

        if (hr != 0)
        {
            _thumb = IntPtr.Zero;
            MultiWindowSwitcher.Services.AppLog.Warn($"Превью не подключилось: «{Item.Title}» ({Item.App}), код 0x{hr:X8}.");
        }

        _lastDest = default;
        _lastVisible = false;
        UpdateThumbnail(querySize: true);
    }

    public void Detach()
    {
        if (_thumb == IntPtr.Zero)
        {
            return;
        }

        DwmUnregisterThumbnail(_thumb);
        _thumb = IntPtr.Zero;
    }

    /// <summary>Где превью сейчас нарисовано: пиксели от левого верхнего угла переключателя; null — не показано.</summary>
    public RECT? ThumbnailRect => _thumb != IntPtr.Zero && _lastVisible ? _lastDest : null;

    /// <summary>Размер окна-источника, как его видит DWM.</summary>
    public SIZE SourceSize => _source;

    /// <summary>Подключить превью заново: если оно опустело.</summary>
    public void ReconnectThumbnail()
    {
        Detach();
        Attach(_hostHwnd);
    }

    /// <summary>Сетка убрала плитку (пока другая увеличена): ячейка нулевая, хотя ActualWidth у содержимого остаётся.</summary>
    private bool Squeezed => LayoutInformation.GetLayoutSlot(this) is var slot && (slot.Width <= 0 || slot.Height <= 0);

    /// <summary>Где область превью внутри окна переключателя (единицы WPF); null — не на экране.</summary>
    public Rect? PreviewRect(Visual window)
    {
        if (Squeezed || !PreviewHost.IsVisible || PreviewHost.ActualWidth <= 0 || !window.IsAncestorOf(PreviewHost))
        {
            return null;
        }

        var topLeft = PreviewHost.TransformToAncestor(window).Transform(new Point(0, 0));
        return new Rect(topLeft, new Size(PreviewHost.ActualWidth, PreviewHost.ActualHeight));
    }

    /// <summary>
    /// Поставить превью ровно в PreviewHost, сохраняя пропорции окна. Вызывается при любой
    /// перестройке сетки; DWM трогается, только если прямоугольник правда поменялся.
    /// </summary>
    public void UpdateThumbnail(bool querySize = false)
    {
        if (_thumb == IntPtr.Zero)
        {
            FallbackIcon.Visibility = ShowsSnapshot ? Visibility.Collapsed : Visibility.Visible;
            return;
        }

        var window = Window.GetWindow(this);
        var source = window is null ? null : PresentationSource.FromVisual(window);
        var visible = window is not null && source is not null && IsVisible && window.IsVisible && !Squeezed
            && PreviewHost.ActualWidth > 8 && PreviewHost.ActualHeight > 8;

        if (!visible)
        {
            if (_lastVisible)
            {
                var hide = new DWM_THUMBNAIL_PROPERTIES { dwFlags = DWM_TNP_VISIBLE, fVisible = false };
                DwmUpdateThumbnailProperties(_thumb, ref hide);
                _lastVisible = false;
            }

            return;
        }

        if (querySize || _source.cx <= 0)
        {
            var was = _source;
            DwmQueryThumbnailSourceSize(_thumb, out _source);

            // в ленте ширина плитки зависит от пропорций окна
            if (Compact && (was.cx != _source.cx || was.cy != _source.cy))
            {
                (VisualTreeHelper.GetParent(this) as UIElement)?.InvalidateMeasure();
            }
        }

        var toDevice = source!.CompositionTarget.TransformToDevice;
        var topLeft = PreviewHost.TransformToAncestor(window!).Transform(new Point(0, 0));
        var inset = 4.0;
        // в ленте слева значок, превью сразу за ним
        var left = Compact ? Math.Max(inset, IconZoneFor(PreviewHost.ActualHeight)) : inset;
        var x = (topLeft.X + left) * toDevice.M11;
        var y = (topLeft.Y + inset) * toDevice.M22;
        var w = Math.Max(1, (PreviewHost.ActualWidth - left - inset) * toDevice.M11);
        var h = Math.Max(1, (PreviewHost.ActualHeight - inset * 2) * toDevice.M22);

        // вписываем окно-источник в ячейку, не растягивая
        if (_source.cx > 0 && _source.cy > 0)
        {
            var scale = Math.Min(w / _source.cx, h / _source.cy);
            var fw = _source.cx * scale;
            var fh = _source.cy * scale;
            x += Compact ? 0 : (w - fw) / 2;
            y += (h - fh) / 2;
            w = fw;
            h = fh;
        }

        var dest = new RECT((int)Math.Round(x), (int)Math.Round(y), (int)Math.Round(x + w), (int)Math.Round(y + h));

        // Полоса поперёк соседних плиток: превью такой формы не бывает полезным. Откуда оно берётся, ещё не поймано —
        // поэтому не рисуем и пишем в лог, что за окно и какие размеры
        if (dest.Height > 0 && dest.Width > dest.Height * MaxThumbnailAspect)
        {
            if (!_thinLogged)
            {
                _thinLogged = true;
                MultiWindowSwitcher.Services.AppLog.Warn(
                    $"Превью-полоса не показана: «{Item.Title}» ({Item.App}), окно у DWM {_source.cx}×{_source.cy}, превью {dest.Width}×{dest.Height}, " +
                    $"область {PreviewHost.ActualWidth:0}×{PreviewHost.ActualHeight:0}, плитка {ActualWidth:0}×{ActualHeight:0}, свёрнуто {IsIconic(Item.Hwnd)}, увеличено {_host.IsPeeking(this)}.");
            }

            if (_lastVisible)
            {
                var hideThin = new DWM_THUMBNAIL_PROPERTIES { dwFlags = DWM_TNP_VISIBLE, fVisible = false };
                DwmUpdateThumbnailProperties(_thumb, ref hideThin);
                _lastVisible = false;
            }

            FallbackIcon.Visibility = Visibility.Visible;
            return;
        }

        _thinLogged = false;

        if (_lastVisible && dest.Left == _lastDest.Left && dest.Top == _lastDest.Top
            && dest.Right == _lastDest.Right && dest.Bottom == _lastDest.Bottom)
        {
            return;
        }

        var props = new DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = DWM_TNP_RECTDESTINATION | DWM_TNP_VISIBLE | DWM_TNP_OPACITY | DWM_TNP_SOURCECLIENTAREAONLY,
            rcDestination = dest,
            opacity = 255,
            fVisible = true,
            fSourceClientAreaOnly = false,
        };

        DwmUpdateThumbnailProperties(_thumb, ref props);
        _lastDest = dest;
        _lastVisible = true;
        FallbackIcon.Visibility = Visibility.Collapsed;
    }

    // ── внешний вид ──────────────────────────────────────────────────────────

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        // окно вернули из трея или развернули — живое превью; ушло в трей или свернулось — снимок
        if (e.PropertyName is nameof(WindowItem.InTray) or nameof(WindowItem.IsMinimized) && _hostHwnd != IntPtr.Zero)
        {
            if (Item.InTray || ShowsSnapshot)
            {
                Detach();
                UpdateThumbnail();
            }
            else
            {
                Attach(_hostHwnd);
            }
        }

        Paint();
    }

    private void Paint()
    {
        TitleText.Text = Compact && Item.HasName ? Item.Name : Display(Item.Title);
        TitleText.ToolTip = Item.Title;
        AppIcon.Source = Item.Icon;
        AppIcon.Visibility = HeaderIconHidden ? Visibility.Collapsed : Visibility.Visible;
        FallbackIcon.Source = Item.InTray ? MultiWindowSwitcher.Services.BigIcons.Of(Item) : Item.Icon;
        FallbackIcon.Width = FallbackIcon.Height = Compact ? double.NaN : Item.InTray ? 96 : 64;
        PaintSnapshot();
        FallbackIcon.Opacity = Item.InTray ? 0.9 : 0.55;

        var banner = !Compact && (Item.HasName || ShowsTitleBanner);
        Banner.Visibility = banner ? Visibility.Visible : Visibility.Collapsed;
        NameText.Text = Item.HasName ? Item.Name : Display(Item.Title);

        // крупно уже виден сам заголовок — мелкий в шапке лишний: там остаются значок, метки и кнопки.
        // У окна со своим именем мелкий заголовок нужен — он говорит, что это за окно на самом деле
        TitleText.Visibility = banner && !Item.HasName ? Visibility.Collapsed : Visibility.Visible;
        NameText.ToolTip = Item.Title;
        NameText.Foreground = TitleBrush;
        BannerActions.Visibility = banner && IsMouseOver ? Visibility.Visible : Visibility.Collapsed;
        FitName();

        // глаз перечёркнут — скрыть из сетки; открытый глаз в «Скрытых» — вернуть
        HideGlyph.Text = Item.HiddenByUser ? "\uE7B3" : "\uED1A";
        HideButton.ToolTip = Loc.T(Item.HiddenByUser ? "ShowAgainTip" : "HideTip");
        WideButton.Foreground = Item.IsWide ? (Brush)FindResource("BrandBrush") : (Brush)FindResource("TextFillColorSecondaryBrush");
        WideButton.ToolTip = Loc.T(Item.IsWide ? "WideOffTip" : "WideTip");
        PinButton.Foreground = Item.IsPinned ? new SolidColorBrush(Color.FromRgb(0xFD, 0xC7, 0x2F)) : (Brush)FindResource("TextFillColorSecondaryBrush");
        CompactPin.Foreground = PinButton.Foreground;
        CompactMonitor.Visibility = ShowMonitor ? Visibility.Visible : Visibility.Collapsed;
        PaintHoverButtons();
        AttentionChip.Visibility = Item.Attention ? Visibility.Visible : Visibility.Collapsed;

        MonitorText.Text = Item.Monitor.ToString();
        PaintCompact();
        MonitorBadge.ToolTip = Loc.F("MonitorTip", Item.Monitor);

        StateChip.Background = (Brush)FindResource(Item.IsMinimized || Item.InTray ? "MinimizedChipBrush" : "OpenChipBrush");
        StateText.Foreground = (Brush)FindResource(Item.IsMinimized ? "MinimizedTextBrush" : "OpenTextBrush");
        StateText.Text = Loc.T(Item.InTray ? "InTray" : Item.IsMinimized ? "Minimized" : "Open");

        PaintFrame();
    }

    private void PaintFrame()
    {
        if (Item.Attention && !_dropTarget)
        {
            StartPulse();
            return;
        }

        StopPulse();

        if (_dropTarget)
        {
            Frame.BorderBrush = (Brush)FindResource("BrandBrush");
            Frame.BorderThickness = new Thickness(2);
        }
        else
        {
            Frame.BorderBrush = IsMouseOver
                ? (Brush)FindResource("TileHoverStrokeBrush")
                : (Brush)FindResource("CardStrokeColorDefaultBrush");
            Frame.BorderThickness = new Thickness(1);
        }
    }

    /// <summary>Окно мигает на панели задач — плитка мягко пульсирует оранжевым.</summary>
    private void StartPulse()
    {
        if (_pulse is not null)
        {
            return;
        }

        var brush = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
        Frame.BorderBrush = brush;
        Frame.BorderThickness = new Thickness(2);

        var animation = new ColorAnimation(Color.FromRgb(0xF5, 0x9E, 0x0B), Color.FromArgb(0x55, 0xF5, 0x9E, 0x0B), TimeSpan.FromMilliseconds(650))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
        };

        Storyboard.SetTarget(animation, Frame);
        Storyboard.SetTargetProperty(animation, new PropertyPath("BorderBrush.Color"));
        _pulse = new Storyboard();
        _pulse.Children.Add(animation);
        _pulse.Begin(this, true);
    }

    private void StopPulse()
    {
        if (_pulse is null)
        {
            return;
        }

        _pulse.Stop(this);
        _pulse = null;
    }

    /// <summary>Окно в трее: последний снимок и сколько ему минут; снимка нет — крупный значок.</summary>
    public void PaintSnapshot()
    {
        var shot = ShowsSnapshot ? SnapshotOf?.Invoke(Item) : null;

        if (shot is { } s)
        {
            Snapshot.Source = s.Image;
            Snapshot.Visibility = Visibility.Visible;
            SnapshotAge.Visibility = Compact ? Visibility.Collapsed : Visibility.Visible;
            var minutes = (int)(DateTime.Now - s.At).TotalMinutes;
            SnapshotAgeText.Text = minutes < 1 ? Loc.T("SnapNow") : Loc.F("SnapAgo", minutes);
            FallbackIcon.Visibility = Visibility.Collapsed;
        }
        else
        {
            Snapshot.Source = null;
            Snapshot.Visibility = Visibility.Collapsed;
            SnapshotAge.Visibility = Visibility.Collapsed;

            if (Item.InTray)
            {
                FallbackIcon.Visibility = Visibility.Visible;
            }
        }
    }

    /// <summary>Кнопки на плитке — при наведении; включённое избранное видно всегда. Hidden, а не Collapsed: место не прыгает.</summary>
    private void PaintHoverButtons()
    {
        var hover = IsMouseOver ? Visibility.Visible : Visibility.Hidden;
        // при наведении метки уступают место кнопкам — всё у правого края, ничего не прыгает посередине
        CornerActions.Visibility = IsMouseOver ? Visibility.Visible : Visibility.Collapsed;
        Chips.Visibility = IsMouseOver ? Visibility.Collapsed : Visibility.Visible;
        // с крупным названием карандаш — на его полосе, рядом с ластиком; внизу его нет
        RenameButton.Visibility = Banner.Visibility == Visibility.Visible ? Visibility.Collapsed : hover;
        PinButton.Visibility = Item.IsPinned ? Visibility.Visible : hover;
        MonitorButton.Visibility = ShowMonitor ? hover : Visibility.Collapsed;
    }

    /// <summary>
    /// Метки (событие, монитор, состояние) в обычном виде не прячутся: не влезают — уменьшаются целиком.
    /// Место — ширина шапки минус значок программы, поля и, если он виден, мелкий заголовок.
    /// В ленте монитора и состояния нет: в шапке только название окна.
    /// </summary>
    private void PaintCompact()
    {
        MonitorBadge.Visibility = ShowMonitor && !Compact ? Visibility.Visible : Visibility.Collapsed;
        StateChip.Visibility = Compact ? Visibility.Collapsed : Visibility.Visible;
        ActiveDot.Visibility = Compact && Item.IsActive && !Item.IsMinimized ? Visibility.Visible : Visibility.Collapsed;

        if (ActualWidth <= 0)
        {
            return;
        }

        Chips.LayoutTransform = Transform.Identity;
        Chips.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var need = Chips.DesiredSize.Width;
        var title = TitleText.Visibility == Visibility.Visible ? 60 : 0;
        var room = ActualWidth - 16 - 24 - title;
        var scale = need > 0 ? Math.Clamp(room / need, 0.5, 1) : 1;

        if (scale < 1)
        {
            Chips.LayoutTransform = new ScaleTransform(scale, scale);
        }
    }

    /// <summary>Имя — крупно, но в меру: от размера плитки.</summary>
    public void Repaint() => Paint();

    private void FitName()
    {
        if (Banner.Visibility != Visibility.Visible || ActualWidth <= 0)
        {
            return;
        }

        NameText.FontSize = Math.Clamp(Math.Min(ActualWidth / 11, ActualHeight / 7), 14, 40) * TitleScale;
    }

    // ── мышь ─────────────────────────────────────────────────────────────────

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        _host.TileHover(this, true);
        BannerActions.Visibility = Banner.Visibility;
        PaintHoverButtons();
        PaintFrame();

        if (Item.Attention)
        {
            _attentionTimer.Start();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _host.TileHover(this, false);
        BannerActions.Visibility = Visibility.Collapsed;
        PaintHoverButtons();
        _attentionTimer.Stop();
        PaintFrame();

        if (_host.IsPeeking(this) && !_dragging)
        {
            _host.Unpeek();
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        // двойной клик — дать имя; одиночный ждёт чуть-чуть, вдруг будет второй
        if (e.ClickCount >= 2)
        {
            _clickTimer.Stop();
            _pressed = false;
            _host.Rename(this);
            e.Handled = true;
            return;
        }

        _pressed = true;
        _down = PointToScreen(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (!_pressed || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var now = PointToScreen(e.GetPosition(this));

        if (!_dragging && (Math.Abs(now.X - _down.X) > 8 || Math.Abs(now.Y - _down.Y) > 8))
        {
            _dragging = true;
            _clickTimer.Stop();
            CaptureMouse();
            _host.TileDragStart(this, PointFromScreen(_down));
        }

        if (_dragging)
        {
            _host.TileDragOver(this, now);
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        if (_dragging)
        {
            _dragging = false;
            _pressed = false;
            ReleaseMouseCapture();
            _host.TileDragEnd(this, commit: true);
            e.Handled = true;
            return;
        }

        if (_pressed)
        {
            _pressed = false;
            _clickTimer.Stop();
            _clickTimer.Start();
            e.Handled = true;
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);

        if (_dragging)
        {
            _dragging = false;
            _pressed = false;
            _host.TileDragEnd(this, commit: false);
        }
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);

        // средняя кнопка — закрыть окно
        if (e.ChangedButton == MouseButton.Middle)
        {
            _host.CloseWindow(Item);
            e.Handled = true;
        }
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        _host.ShowMenu(this);
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);

        // колесо вверх — увеличить на всю сетку, вниз — обратно
        if (e.Delta > 0)
        {
            _host.Peek(this);
        }
        else if (_host.IsPeeking(this))
        {
            _host.Unpeek();
        }

        e.Handled = true;
    }

    private void OnPin(object sender, RoutedEventArgs e) => _host.TogglePin(Item);

    private void OnPeek(object sender, RoutedEventArgs e) => _host.Peek(this);

    private void OnWide(object sender, RoutedEventArgs e) => _host.ToggleWide(Item);

    private void OnNextMonitor(object sender, RoutedEventArgs e) => _host.MoveToNextMonitor(Item);

    private void OnRename(object sender, RoutedEventArgs e) => _host.Rename(this);

    private void OnHide(object sender, RoutedEventArgs e)
    {
        _host.HideWindow(Item);
        Paint();
    }

    private void OnClose(object sender, RoutedEventArgs e) => _host.CloseWindow(Item);

    /// <summary>Крестик на полосе: своё имя — удалить, крупный заголовок — убрать у этого окна.</summary>
    private void OnBannerRemove(object sender, RoutedEventArgs e)
    {
        if (Item.HasName)
        {
            _host.ClearName(Item);
        }
        else
        {
            Item.NoBigTitle = true;
            Paint();
        }
    }
}
