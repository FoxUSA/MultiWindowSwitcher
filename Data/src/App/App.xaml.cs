using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using MultiWindowSwitcher.Core;
using MultiWindowSwitcher.Models;
using MultiWindowSwitcher.Services;
using MultiWindowSwitcher.Views;
using Wpf.Ui.Appearance;

namespace MultiWindowSwitcher;

/// <summary>
/// Сборка всего вместе: переключатель (главное окно), плавающая кнопка у края, значок в трее,
/// активные углы и слежка за окнами Windows.
/// </summary>
public partial class App : Application
{
    private bool _exiting;

    public WindowTracker Tracker { get; } = new();

    public MainWindow Switcher { get; private set; } = null!;

    public FloatingButton Fab { get; private set; } = null!;

    public TrayIcon Tray { get; private set; } = null!;

    public HotCorners Corners { get; private set; } = null!;

    public MouseGestures Gestures { get; private set; } = null!;

    public static new App Current => (App)Application.Current;

    protected override void OnStartup(StartupEventArgs e)
    {
        Settings.Load();
        Loc.I.Init(Settings.Current.Language);

        var quit = e.Args.Contains("--quit", StringComparer.OrdinalIgnoreCase);

        // повторный запуск не открывает вторую копию: работающая просто показывается
        if (!SingleInstance.TryBecomePrimary())
        {
            if (quit)
            {
                SingleInstance.Send(SingleInstance.QuitMessage);
            }
            else if (!SingleInstance.SignalShow())
            {
                MessageBox.Show(Loc.T("AlreadyRunning"), AppVersion.ProductName, MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            Shutdown();
            return;
        }

        if (quit)
        {
            Shutdown();
            return;
        }

        // журнал всего: ключи запуска, кто запустил, как кончился прошлый запуск, все ошибки
        AppLog.Prune();
        Diagnostics.Start(e.Args);
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        base.OnStartup(e);

        ApplicationThemeManager.Apply(ApplicationTheme.Dark);
        ApplicationAccentColorManager.Apply(Color.FromRgb(0x4C, 0xA6, 0xFF), ApplicationTheme.Dark);


        // прошлый запуск мог упасть, оставив окна невидимыми
        Parking.RecoverAfterCrash();

        // иконки папок чинятся при каждом запуске: архиваторы и переносы между ПК их съедают
        Task.Run(FolderIcons.Restore);

        Autostart.Apply(Settings.Current.Autostart);

        // Интерфейс рисуется программно, без Direct3D. Тяжёлое — живые превью — и так рисует
        // сама Windows (DWM), а на гибридных видеокартах (встроенная + дискретная) аппаратный
        // WPF иногда показывает пустое окно. Так надёжнее и дискретная карта лишний раз не просыпается.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        Switcher = new MainWindow();
        Switcher.Prepare();

        Tracker.Start(Switcher);

        Fab = new FloatingButton();
        Fab.Show();
        Fab.ApplySettings();

        Tray = new TrayIcon();
        // без иконки и без трея до программы не добраться
        Settings.Current.Tray |= !Settings.Current.ShowFab;
        Tray.Apply(Settings.Current.Tray);

        Corners = new HotCorners();
        Corners.Triggered += OnCorner;
        Corners.Start();

        Gestures = new MouseGestures(Dispatcher);
        Gestures.Triggered += OnGesture;
        Gestures.Start();

        SingleInstance.StartListener(message => Dispatcher.BeginInvoke(new Action(() =>
        {
            AppLog.Info("Команда от другого запуска: " + message);

            if (message == SingleInstance.QuitMessage)
            {
                Quit("команда --quit от другого запуска");
            }
            else
            {
                Switcher.ShowSwitcher(Fab.Side, ShowReason.Explicit);
            }
        })));

        SessionEnding += (_, args) => Quit("Windows завершает сеанс: " + args.ReasonSessionEnding);

        Diagnostics.AttachUi(Dispatcher, () => Switcher.StateLine());

        // дубли папок, открытые до запуска, — закрыть сразу, не дожидаясь новых окон
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(ExplorerDuplicates.CloseAll));

        // при автозапуске — тихо, только иконка у края; иначе сразу показываем, что умеем
        if (!e.Args.Contains(Autostart.Flag, StringComparer.OrdinalIgnoreCase) || Settings.Current.Dock is "Top" or "Bottom")
        {
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                Switcher.ShowSwitcher(Fab.Side, ShowReason.Startup)));
        }

        // при каждом запуске спрашиваем byfox.dev о новой версии: если есть — плашка в шапке
        _ = UpdateService.CheckOnStartupAsync();
    }

    /// <summary>Активный угол: спрятано — выехать с его стороны, открыто — уехать туда же.</summary>
    private void OnCorner(char side, System.Windows.Point point)
    {
        if (Switcher.IsShown && !Switcher.IsCovered())
        {
            if (!Switcher.IsBusy)
            {
                Switcher.HideSwitcher(side);
            }
        }
        else
        {
            // спрятано или закрыто другими окнами — показать поверх всех
            Switcher.ShowSwitcher(side, ShowReason.Corner, point);
        }
    }

    /// <summary>Записанное нажатие мыши, где бы ни было: спрятано — показать, открыто — спрятать.</summary>
    private void OnGesture(System.Windows.Point point)
    {
        AppLog.Info("Жест мышью: " + (Switcher.IsShown ? "спрятать" : "показать") + ".");

        // явная команда — прячем всегда, даже если открыто меню или курсор в поиске
        // видно — спрятать; открыто, но закрыто другими окнами, — вывести вперёд
        if (Switcher.IsShown && !Switcher.IsCovered())
        {
            Switcher.HideSwitcher();
        }
        else
        {
            Switcher.ShowSwitcher(Fab.Side, ShowReason.Corner, point);
        }
    }

    /// <summary>
    /// Иконка у края — по желанию. Без неё программу открывают из трея, поэтому трей
    /// при этом всегда включается: иначе до программы не добраться.
    /// </summary>
    public void SetFabVisible(bool visible)
    {
        var s = Settings.Current;
        s.ShowFab = visible;

        if (!visible && !s.Tray)
        {
            s.Tray = true;
            Tray.Apply(true);
        }

        Settings.Touch();
        Fab.ApplySettings();
    }

    /// <summary>Настоящий выход: «Выход» в заголовке, трей, меню иконки. Крестик окна только прячет.</summary>
    public void Quit(string reason)
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;

        try
        {
            Corners.Stop();
            Gestures.Stop();
            Switcher.ReleaseDock();
            Tracker.Stop(Switcher);
            Parking.UnparkAll();
            Switcher.SavePlacement();
            Switcher.SaveSnapshots();
            Settings.SaveNow();
            Tray.Dispose();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Выход с ошибкой: " + ex);
        }

        Diagnostics.Stop(reason);

        Shutdown();
    }

    public bool IsExiting => _exiting;

    protected override void OnExit(ExitEventArgs e)
    {
        // на случай выхода мимо Quit(): окна не должны остаться невидимыми
        Parking.UnparkAll();

        if (!_exiting && Switcher is not null)
        {
            Diagnostics.Stop($"закрытие мимо выхода программы, код {e.ApplicationExitCode}");
        }

        base.OnExit(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error("Ошибка интерфейса (перехвачена, программа работает дальше): " + e.Exception);
        e.Handled = true;
    }
}
