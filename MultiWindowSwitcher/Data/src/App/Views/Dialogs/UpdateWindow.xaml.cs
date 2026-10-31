using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using MultiWindowSwitcher.Core;
using MultiWindowSwitcher.Services;
using Wpf.Ui.Controls;

namespace MultiWindowSwitcher.Views.Dialogs;

public enum StepState
{
    Waiting,
    Running,
    Done,
    Failed,
}

/// <summary>Строка списка шагов: значок состояния слева, текст справа.</summary>
public sealed class UpdateStep : ObservableObject
{
    private StepState _state;

    public UpdateStep(string text) => Text = text;

    public string Text { get; }

    public StepState State
    {
        get => _state;
        set
        {
            if (Set(ref _state, value))
            {
                RaiseMany(nameof(Glyph), nameof(GlyphBrush), nameof(Weight), nameof(Opacity));
            }
        }
    }

    public string Glyph => State switch
    {
        StepState.Running => "➜",
        StepState.Done => "✓",
        StepState.Failed => "✗",
        _ => "○",
    };

    public Brush? GlyphBrush => Application.Current.TryFindResource(State switch
    {
        StepState.Done => "SystemFillColorSuccessBrush",
        StepState.Failed => "SystemFillColorCriticalBrush",
        StepState.Running => "AccentTextFillColorPrimaryBrush",
        _ => "TextFillColorTertiaryBrush",
    }) as Brush;

    public FontWeight Weight => State == StepState.Running ? FontWeights.SemiBold : FontWeights.Normal;

    public double Opacity => State == StepState.Waiting ? 0.6 : 1.0;
}

/// <summary>
/// Обновление целиком в одном окне: проверка, загрузка, распаковка во временную папку,
/// проверка архива — здесь; закрытие программы, замена Data и лаунчера, уборка
/// временной папки и перезапуск — в окне лаунчера, с тем же списком шагов.
/// </summary>
public partial class UpdateWindow : FluentWindow
{
    // шаги 0–2 делает программа, 3–7 — лаунчер после её закрытия
    private readonly UpdateStep[] _steps =
    {
        new(Loc.T("UpdStep1")),
        new(Loc.T("UpdStep2")),
        new(Loc.T("UpdStep3")),
        new(Loc.T("UpdStep4")),
        new(Loc.T("UpdStep5")),
        new(Loc.T("UpdStep6")),
        new(Loc.T("UpdStep7")),
        new(Loc.T("UpdStep8")),
    };

    private CancellationTokenSource? _cts;
    private bool _busy;

    public UpdateWindow()
    {
        InitializeComponent();

        StepList.ItemsSource = _steps;
        Closing += OnClosingWindow;
        CloseButton.Content = Loc.T("UpdClose");

        ShowRelease();

        // окно можно открыть и из настроек — тогда сначала спрашиваем сайт
        if (UpdateService.Available is null)
        {
            Loaded += async (_, _) => await CheckAsync();
        }
    }

    /// <summary>Открыть окно поверх переключателя.</summary>
    public static void Open(Window? owner)
    {
        var window = new UpdateWindow();

        if (owner is { IsVisible: true })
        {
            window.Owner = owner;
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        window.ShowDialog();
    }

    private void ShowRelease()
    {
        var available = UpdateService.Available;

        if (available is null)
        {
            HeadText.Text = Loc.T("UpdHeadChecking");
            SubText.Text = Loc.F("UpdInstalled", AppVersion.Details);
            UpdateButton.Content = Loc.T("UpdUpdate");
            UpdateButton.IsEnabled = false;
            return;
        }

        HeadText.Text = Loc.F("UpdHeadAvailable", available.Caption);
        SubText.Text = Loc.F("UpdSub", AppVersion.Details);
        UpdateButton.IsEnabled = !_busy;
        UpdateButton.Content = Loc.F("UpdButton", available.Version);
    }

    private async Task CheckAsync()
    {
        SetBusy(true);

        try
        {
            await UpdateService.CheckAsync(new Progress<UpdateProgress>(Show));
        }
        catch (Exception ex)
        {
            Show(new UpdateProgress(UpdateStage.Failed, Loc.F("UpdCheckFailed", ex.Message)));
        }
        finally
        {
            SetBusy(false);
            ShowRelease();
        }
    }

    private async void OnUpdate(object sender, RoutedEventArgs e)
    {
        if (_busy || UpdateService.Available is not { } release)
        {
            return;
        }

        foreach (var step in _steps)
        {
            step.State = StepState.Waiting;
        }

        _cts = new CancellationTokenSource();
        SetBusy(true);

        try
        {
            await UpdateService.DownloadAndStageAsync(release, new Progress<UpdateProgress>(Show), _cts.Token);

            // дальше программа закрывается: пусть человек успеет увидеть, что всё готово
            Mark(3);
            Show(new UpdateProgress(UpdateStage.Ready, Loc.T("UpdClosing"), 100));

            await Task.Delay(900);

            _cts.Dispose();
            _cts = null;

            UpdateService.ApplyAndRestart();
        }
        catch (OperationCanceledException)
        {
            Fail(Loc.T("UpdCanceled"));
            UpdateService.ClearTemp();
            AppLog.Info("Обновление отменено.");
        }
        catch (Exception ex)
        {
            Fail(Loc.F("UpdFailed", ex.Message));
            UpdateService.ClearTemp();
            AppLog.Error("Обновление не удалось: " + ex.Message);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            SetBusy(false);
        }
    }

    /// <summary>Отметить текущий шаг: всё до него — сделано, он — идёт.</summary>
    private void Mark(int index)
    {
        for (var i = 0; i < _steps.Length; i++)
        {
            _steps[i].State = i < index ? StepState.Done : i == index ? StepState.Running : StepState.Waiting;
        }
    }

    private void Fail(string text)
    {
        foreach (var step in _steps)
        {
            if (step.State == StepState.Running)
            {
                step.State = StepState.Failed;
            }
        }

        Show(new UpdateProgress(UpdateStage.Failed, text));
    }

    private void Show(UpdateProgress progress)
    {
        switch (progress.Stage)
        {
            case UpdateStage.Downloading:
                Mark(0);
                break;
            case UpdateStage.Unpacking:
                Mark(1);
                break;
            case UpdateStage.Staging:
                Mark(2);
                break;
        }

        StageText.Text = progress.Text;

        if (progress.Percent is { } percent)
        {
            Bar.IsIndeterminate = false;
            Bar.Value = percent;
        }
        else
        {
            Bar.IsIndeterminate = progress.Stage is UpdateStage.Checking or UpdateStage.Staging;
            Bar.Value = 0;
        }

        if (progress.Stage is UpdateStage.UpToDate or UpdateStage.Failed or UpdateStage.Ready)
        {
            Bar.IsIndeterminate = false;
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        UpdateButton.IsEnabled = !busy && UpdateService.Available is not null;
        CloseButton.Content = Loc.T(busy && _cts is not null ? "UpdCancel" : "UpdClose");
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        if (_cts is not null)
        {
            _cts.Cancel();
            return;
        }

        Close();
    }

    /// <summary>Крестик окна посреди загрузки — это отмена, а не брошенная на полпути загрузка.</summary>
    private void OnClosingWindow(object? sender, CancelEventArgs e)
    {
        if (_cts is not null)
        {
            _cts.Cancel();
            e.Cancel = true;
        }
    }
}
