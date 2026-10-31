using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using MultiWindowSwitcher.Core;
using MultiWindowSwitcher.Models;
using MultiWindowSwitcher.Native;

namespace MultiWindowSwitcher.Services;

/// <summary>
/// Всё, что помогает понять «почему вылетело»: кто и с какими ключами запустил программу,
/// чем закончился прошлый запуск (и что записала о нём Windows), все необработанные ошибки,
/// ошибки разметки и привязок WPF, подвисания интерфейса, рост памяти и дескрипторов,
/// причина выхода. Пишет в тот же журнал UserData\logs.
/// </summary>
public static class Diagnostics
{
    private const int MaxSwallowed = 60;

    private static readonly HashSet<string> SeenSwallowed = new();
    private static readonly HashSet<int> SeenObjects = new();
    private static readonly DateTime Started = DateTime.Now;

    private static Thread? _watchdog;
    private static volatile bool _stopping;
    private static long _uiPulse;
    private static DispatcherTimer? _heartbeat;
    private static Func<string>? _state;
    private static int _inFirstChance;

    private static string RunningFlag => Path.Combine(AppPaths.LogsDir, "running.flag");

    /// <summary>Сразу после загрузки настроек: ловушки ошибок и сведения о запуске.</summary>
    public static void Start(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            AppLog.Error($"Необработанная ошибка (программа {(e.IsTerminating ? "закроется" : "продолжит работу")}): {e.ExceptionObject}");

            // что бы ни случилось — невидимые «живые» окна должны вернуться
            Parking.UnparkAll();
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Error("Ошибка в фоновой задаче: " + e.Exception);
            e.SetObserved();
        };

        // перехваченные и «проглоченные» ошибки нашего кода — тоже в журнал, но без повторов
        AppDomain.CurrentDomain.FirstChanceException += (_, e) => OnFirstChance(e.Exception);

        CheckPreviousRun();
        WriteRunningFlag();

        var self = Process.GetCurrentProcess();
        AppLog.Info($"Запуск. Версия {AppVersion.Details}. PID {self.Id}, ключи: {(args.Length == 0 ? "нет" : string.Join(' ', args))}");
        AppLog.Info($"Запустил: {ParentDescription(self.Id)}. Программа: {AppPaths.DataDir}");
        AppLog.Info($"Windows {Environment.OSVersion.Version}, .NET {Environment.Version}, {(Environment.Is64BitProcess ? "x64" : "x86")}, язык {System.Globalization.CultureInfo.CurrentUICulture.Name}");
    }

    /// <summary>Когда интерфейс собран: WPF-предупреждения, сторож зависаний, мониторы и периодический отчёт.</summary>
    public static void AttachUi(Dispatcher dispatcher, Func<string> state)
    {
        _state = state;

        // ошибки привязок и ресурсов разметки WPF обычно молча теряются — пишем их
        PresentationTraceSources.Refresh();
        var listener = new LogTraceListener();
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        PresentationTraceSources.ResourceDictionarySource.Listeners.Add(listener);
        PresentationTraceSources.ResourceDictionarySource.Switch.Level = SourceLevels.Error;

        foreach (var m in Monitors.All())
        {
            AppLog.Info($"Монитор {m.Number}{(m.IsPrimary ? " (основной)" : string.Empty)}: {m.Bounds.Width}×{m.Bounds.Height} в ({m.Bounds.Left}, {m.Bounds.Top}), масштаб {m.Scale * 100:0}%");
        }

        StartWatchdog(dispatcher);

        _heartbeat = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMinutes(15) };
        _heartbeat.Tick += (_, _) => Heartbeat();
        _heartbeat.Start();
        Heartbeat();
    }

    /// <summary>Чистый выход: флаг «работает» снимается, иначе следующий запуск сочтёт это сбоем.</summary>
    public static void Stop(string reason)
    {
        _stopping = true;
        _heartbeat?.Stop();
        Heartbeat();
        AppLog.Info($"Выход: {reason}. Проработала {Uptime()}.");

        try
        {
            File.Delete(RunningFlag);
        }
        catch
        {
            // следующий запуск напишет «прошлый запуск не закрылся» — переживём
        }
    }

    public static string Uptime()
    {
        var t = DateTime.Now - Started;
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours} ч {t.Minutes} мин" : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} мин {t.Seconds} с" : $"{t.Seconds} с";
    }

    // ── прошлый запуск ───────────────────────────────────────────────────────

    private static void WriteRunningFlag()
    {
        try
        {
            File.WriteAllText(RunningFlag, $"{Environment.ProcessId}\n{DateTime.Now:O}");
        }
        catch (Exception ex)
        {
            AppLog.Warn("Не записать running.flag: " + ex.Message);
        }
    }

    /// <summary>Флаг остался — прошлый запуск не дошёл до выхода: упал, убит или Windows выключили.</summary>
    private static void CheckPreviousRun()
    {
        try
        {
            if (!File.Exists(RunningFlag))
            {
                return;
            }

            var lines = File.ReadAllLines(RunningFlag);
            var since = lines.Length > 1 && DateTime.TryParse(lines[1], null, System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t : File.GetLastWriteTime(RunningFlag);
            AppLog.Error($"Прошлый запуск (PID {lines.FirstOrDefault()}, с {since:HH:mm:ss dd.MM}) завершился аварийно: выхода не было — сбой, снятие задачи или выключение Windows.");
            ReportWindowsEvents(since);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Не проверить прошлый запуск: " + ex.Message);
        }
    }

    /// <summary>Что записала Windows о падении: события .NET Runtime и Application Error с нашим exe.</summary>
    private static void ReportWindowsEvents(DateTime since)
    {
        try
        {
            var ms = Math.Max(0, (long)(DateTime.Now - since).TotalMilliseconds) + 60_000;
            var query = new EventLogQuery("Application", PathType.LogName,
                $"*[System[(Provider[@Name='.NET Runtime'] or Provider[@Name='Application Error'] or Provider[@Name='Windows Error Reporting']) and TimeCreated[timediff(@SystemTime) <= {ms}]]]");
            using var reader = new EventLogReader(query);
            var found = 0;

            for (var record = reader.ReadEvent(); record is not null && found < 5; record = reader.ReadEvent())
            {
                using (record)
                {
                    var text = record.FormatDescription() ?? string.Empty;

                    if (!text.Contains(AppPaths.AppExeName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    found++;
                    AppLog.Error($"Windows записала ({record.ProviderName}, {record.TimeCreated:HH:mm:ss}): {Shorten(text, 3000)}");
                }
            }

            if (found == 0)
            {
                AppLog.Info("В журнале Windows о падении записей нет — скорее всего, процесс сняли или Windows выключили.");
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Журнал Windows не прочитать: " + ex.Message);
        }
    }

    // ── ошибки ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Каждое исключение в момент броска. Пишем только те, что прошли через наш код,
    /// каждое место — один раз, не больше <see cref="MaxSwallowed"/> за запуск.
    /// </summary>
    private static void OnFirstChance(Exception ex)
    {
        if (Interlocked.Exchange(ref _inFirstChance, 1) == 1)
        {
            return;
        }

        try
        {
            if (SeenSwallowed.Count >= MaxSwallowed || ex is OperationCanceledException)
            {
                return;
            }

            var stack = new StackTrace(1, false).ToString();

            if (!stack.Contains("MultiWindowSwitcher.", StringComparison.Ordinal) || stack.Contains("MultiWindowSwitcher.Services.AppLog", StringComparison.Ordinal))
            {
                return;
            }

            // одно и то же исключение, проброшенное выше, и одно и то же место — по разу
            var key = ex.GetType().Name + "|" + ex.Message;

            lock (SeenSwallowed)
            {
                if (!SeenSwallowed.Add(key) || !SeenObjects.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(ex)))
                {
                    return;
                }
            }

            AppLog.Write(LogLevel.Trace, $"Исключение {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{Shorten(OurFrames(stack), 1500)}");
        }
        catch
        {
            // журнал ошибок не должен сам порождать ошибки
        }
        finally
        {
            Interlocked.Exchange(ref _inFirstChance, 0);
        }
    }

    private static string OurFrames(string stack) =>
        string.Join(Environment.NewLine, stack.Split(Environment.NewLine)
            .Where(l => l.Contains("MultiWindowSwitcher.", StringComparison.Ordinal) && !l.Contains("Services.Diagnostics.", StringComparison.Ordinal))
            .Take(8));

    // ── подвисания ───────────────────────────────────────────────────────────

    /// <summary>Фоновый сторож: если интерфейс не отвечает дольше 2 секунд — в журнал, сколько висел.</summary>
    private static void StartWatchdog(Dispatcher dispatcher)
    {
        _uiPulse = Environment.TickCount64;

        _watchdog = new Thread(() =>
        {
            var frozenSince = 0L;

            while (!_stopping)
            {
                Thread.Sleep(500);
                dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => Interlocked.Exchange(ref _uiPulse, Environment.TickCount64)));

                var silent = Environment.TickCount64 - Interlocked.Read(ref _uiPulse);

                if (silent > 2000 && frozenSince == 0)
                {
                    frozenSince = Environment.TickCount64 - silent;
                    AppLog.Warn("Интерфейс не отвечает больше 2 с…");
                }
                else if (silent < 1000 && frozenSince != 0)
                {
                    AppLog.Warn($"Интерфейс снова отвечает: висел {(Environment.TickCount64 - frozenSince) / 1000.0:0.0} с.");
                    frozenSince = 0;
                }
            }
        })
        {
            IsBackground = true,
            Name = "UI watchdog",
            Priority = ThreadPriority.BelowNormal,
        };

        _watchdog.Start();
    }

    // ── периодический отчёт ─────────────────────────────────────────────────

    /// <summary>Память и дескрипторы: если что-то течёт (превью, иконки), это видно по росту.</summary>
    private static void Heartbeat()
    {
        try
        {
            var p = Process.GetCurrentProcess();
            var gdi = GetGuiResources(p.Handle, 0);
            var user = GetGuiResources(p.Handle, 1);
            AppLog.Info($"Состояние: память {p.WorkingSet64 / 1048576} МБ (управляемая {GC.GetTotalMemory(false) / 1048576} МБ), GDI {gdi}, USER {user}, дескрипторов {p.HandleCount}, потоков {p.Threads.Count}. {_state?.Invoke()}");
        }
        catch (Exception ex)
        {
            AppLog.Warn("Состояние не снять: " + ex.Message);
        }
    }

    // ── кто запустил ─────────────────────────────────────────────────────────

    private static string ParentDescription(int pid)
    {
        try
        {
            var info = new PROCESS_BASIC_INFORMATION();

            if (NtQueryInformationProcess(Process.GetCurrentProcess().Handle, 0, ref info, Marshal.SizeOf(info), out _) != 0)
            {
                return "неизвестно";
            }

            var parentPid = (uint)info.InheritedFromUniqueProcessId;
            var path = Win32.GetProcessPath(parentPid);

            // лаунчер запускает программу и сразу закрывается — кто запустил его, пишет он сам
            return string.IsNullOrEmpty(path)
                ? $"процесс {parentPid}, уже закрыт (обычно это лаунчер MultiWindowSwitcher.exe)"
                : $"{Path.GetFileName(path)} (PID {parentPid}, {path})";
        }
        catch
        {
            return "неизвестно";
        }
    }

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2a;
        public IntPtr Reserved2b;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, ref PROCESS_BASIC_INFORMATION info, int size, out int returned);

    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr process, uint flags);

    /// <summary>Сообщения WPF о привязках и ресурсах — в журнал, по одному на строку.</summary>
    private sealed class LogTraceListener : TraceListener
    {
        private int _count;

        public override void Write(string? message)
        {
        }

        public override void WriteLine(string? message)
        {
            if (message is null || Interlocked.Increment(ref _count) > 200)
            {
                return;
            }

            AppLog.Warn("WPF: " + message);
        }
    }
}
