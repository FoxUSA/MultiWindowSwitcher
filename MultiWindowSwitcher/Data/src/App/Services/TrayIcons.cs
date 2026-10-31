using System.Text;
using System.Windows.Automation;
using MultiWindowSwitcher.Native;

namespace MultiWindowSwitcher.Services;

/// <summary>
/// Значки программ в трее — через специальные возможности Windows (UI Automation): у каждого есть
/// подпись («Telegram Desktop (440)», «DontSleep») и его можно «нажать». Так окно из трея открывает
/// сама программа, своим обычным способом, — показывать её окно в обход нельзя: Qt-программы
/// (Telegram) после этого перестают открываться даже со своего значка.
/// Список читается в фоне, не чаще раза в несколько секунд: запросы к панели задач небыстрые.
/// </summary>
public static class TrayIcons
{
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(4);
    private static string[] _names = Array.Empty<string>();
    private static DateTime _at = DateTime.MinValue;
    private static int _busy;

    /// <summary>Список значков поменялся — стоит пересобрать сетку.</summary>
    public static event Action? Updated;

    /// <summary>Подписи значков в трее (последнее прочитанное); заодно просит обновить список, если он устарел.</summary>
    public static IReadOnlyList<string> Names
    {
        get
        {
            RefreshInBackground();
            return _names;
        }
    }

    /// <summary>Значок этой программы в трее: подпись, по которой его потом нажать; null — не нашли.</summary>
    public static string? Find(string appTitle, string app, string windowTitle)
    {
        var keys = Keys(appTitle, app);
        var window = Normalize(windowTitle);

        return Names.FirstOrDefault(name =>
        {
            var n = Normalize(name);

            // по программе — в обе стороны; по заголовку окна — только если подпись значка содержит его целиком
            return n.Length > 0 && (keys.Any(k => n.Contains(k) || k.Contains(n)) || (window.Length >= 6 && n.Contains(window)));
        });
    }

    private static readonly Dictionary<string, DateTime> LastClick = new();

    /// <summary>Значки из последнего чтения: нажимаем сразу, не обходя панель задач заново (это долго).</summary>
    private static Dictionary<string, AutomationElement> _elements = new();

    /// <summary>
    /// Значок этой программы нажимали только что — второе нажатие у многих программ закрывает окно,
    /// а у установщиков вроде AMD Install Manager закрыть окно значит выйти из программы.
    /// </summary>
    public static bool ClickedRecently(string name) =>
        LastClick.TryGetValue(name, out var at) && DateTime.UtcNow - at < TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// Нажать значок в трее — как человек мышью. Qt-программам (Telegram) — сразу их же сообщением
    /// «клик по значку», без панели задач: быстрее и не зависит от подписи, которая меняется
    /// со счётчиком непрочитанных. done(false) — значок не нашёлся.
    /// </summary>
    public static void Click(string name, IntPtr window, Action<bool> done)
    {
        LastClick[name] = DateTime.UtcNow;

        Task.Run(() =>
        {
            var ok = false;

            try
            {
                if (ClickQt(window))
                {
                    done(true);
                    return;
                }

                ok = Invoke(_elements.GetValueOrDefault(name));

                // значок пересоздан (программа перезапустилась, панель задач обновилась) — ищем заново
                if (!ok)
                {
                    Remember(Query());
                    ok = Invoke(_elements.GetValueOrDefault(name));
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("Значок в трее не нажался: " + name + " — " + ex.Message);
            }

            done(ok);
        });
    }

    private static void RefreshInBackground()
    {
        if (DateTime.UtcNow - _at < Every || Interlocked.Exchange(ref _busy, 1) == 1)
        {
            return;
        }

        Task.Run(() =>
        {
            try
            {
                var names = Remember(Query());
                var changed = !names.SequenceEqual(_names);
                _names = names;

                if (changed)
                {
                    Updated?.Invoke();
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("Значки в трее не прочитались: " + ex.Message);
            }
            finally
            {
                _at = DateTime.UtcNow;
                Interlocked.Exchange(ref _busy, 0);
            }
        });
    }

    // Qt: у каждого значка своё скрытое окно «…TrayIconMessageWindowClass», Windows шлёт ему
    // WM_APP+101, в младшем слове lParam — событие (NIN_SELECT — клик), в старшем — номер значка (0)
    private const int QtTrayMessage = 0x8000 + 101;
    private const int NIN_SELECT = 0x0400;

    private static bool ClickQt(IntPtr window)
    {
        Win32.GetWindowThreadProcessId(window, out var pid);
        var found = new List<IntPtr>();

        Win32.EnumWindows((hwnd, _) =>
        {
            Win32.GetWindowThreadProcessId(hwnd, out var owner);

            if (owner == pid && Win32.GetClassName(hwnd).EndsWith("TrayIconMessageWindowClass", StringComparison.Ordinal))
            {
                found.Add(hwnd);
            }

            return true;
        }, IntPtr.Zero);

        // значков у программы несколько — какой из них главный, не угадать; пусть решает панель задач
        if (found.Count != 1)
        {
            return false;
        }

        // окно откроет программа, а не мы: разрешаем ей выйти на передний план, как это делает панель задач
        Win32.AllowSetForegroundWindow((int)pid);

        if (!Win32.PostMessage(found[0], QtTrayMessage, IntPtr.Zero, new IntPtr(NIN_SELECT)))
        {
            return false;
        }

        // Telegram, спрятанный DWM-маской, иногда считает себя открытым и первый клик только «прячет» его —
        // окно так и не появилось: жмём ещё раз
        for (var i = 0; i < 40 && Win32.IsHiddenByApp(window); i++)
        {
            Thread.Sleep(25);
        }

        if (Win32.IsHiddenByApp(window))
        {
            AppLog.Info("Окно из трея не открылось с первого клика — нажимаем значок ещё раз.");
            Win32.AllowSetForegroundWindow((int)pid);
            Win32.PostMessage(found[0], QtTrayMessage, IntPtr.Zero, new IntPtr(NIN_SELECT));
        }

        return true;
    }

    private static bool Invoke(AutomationElement? icon)
    {
        try
        {
            if (icon?.GetCurrentPattern(InvokePattern.Pattern) is InvokePattern invoke)
            {
                invoke.Invoke();
                return true;
            }
        }
        catch (ElementNotAvailableException)
        {
            // значка уже нет
        }

        return false;
    }

    private static string[] Remember(List<AutomationElement> icons)
    {
        var map = new Dictionary<string, AutomationElement>();

        foreach (var icon in icons)
        {
            var name = icon.Current.Name;

            if (!string.IsNullOrWhiteSpace(name))
            {
                map[name] = icon;
            }
        }

        _elements = map;
        return map.Keys.ToArray();
    }

    /// <summary>Кнопки-значки программ на панели задач (и в раскрытом списке скрытых значков, если он открыт).</summary>
    private static List<AutomationElement> Query()
    {
        var result = new List<AutomationElement>();
        var icon = new PropertyCondition(AutomationElement.AutomationIdProperty, "NotifyItemIcon");

        foreach (var cls in new[] { "Shell_TrayWnd", "TopLevelWindowForOverflowXamlIsland" })
        {
            var host = AutomationElement.RootElement.FindFirst(TreeScope.Children, new PropertyCondition(AutomationElement.ClassNameProperty, cls));

            if (host is not null)
            {
                result.AddRange(host.FindAll(TreeScope.Descendants, icon).Cast<AutomationElement>());
            }
        }

        return result;
    }

    /// <summary>
    /// По чему узнать программу в подписи значка: её название, имя exe без хвостов (_x64 и т. п.)
    /// и заголовок окна — у MihomoLauncher значок подписан «Запуск программ — …», как и его окно.
    /// </summary>
    private static List<string> Keys(string appTitle, string app)
    {
        var keys = new List<string>();

        var title = Normalize(appTitle);

        if (title.Length >= 4)
        {
            keys.Add(title);
        }

        var exe = System.IO.Path.GetFileNameWithoutExtension(app);
        var first = Normalize(exe.Split('_', '-', '.', ' ')[0]);

        if (first.Length >= 4)
        {
            keys.Add(first);
        }

        return keys;
    }

    private static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);

        foreach (var c in text)
        {
            if (char.IsLetter(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }

        return sb.ToString();
    }
}
