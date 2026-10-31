using System.IO;
using System.Windows.Threading;
using MultiWindowSwitcher.Models;
using static MultiWindowSwitcher.Native.Win32;

namespace MultiWindowSwitcher.Services;

/// <summary>
/// Закрывать дубли папок: одна папка — одно окно Проводника. Открыли папку, которая уже открыта в другом окне, —
/// прежние окна с этой же папкой тихо закрываются, остаётся новое.
/// Папку окна знает только сам Проводник: спрашиваем его через Shell.Application.
/// Окна с несколькими вкладками не трогаем — в них могут быть и другие папки.
/// </summary>
public static class ExplorerDuplicates
{
    private static readonly TimeSpan[] Attempts = { TimeSpan.FromMilliseconds(600), TimeSpan.FromMilliseconds(1500), TimeSpan.FromMilliseconds(3000) };

    /// <summary>Новое окно Проводника: папка в нём появляется не сразу — проверяем с паузами.</summary>
    public static void Schedule(IntPtr hwnd)
    {
        if (!Settings.Current.CloseDuplicateFolders)
        {
            return;
        }

        Try(hwnd, 0);
    }

    /// <summary>
    /// Запуск программы или включение опции: дубли, что уже открыты, закрываются сразу.
    /// Из окон с одной папкой остаётся верхнее — то, с которым работали последним.
    /// </summary>
    public static void CloseAll()
    {
        if (!Settings.Current.CloseDuplicateFolders)
        {
            return;
        }

        var tabs = Snapshot();

        if (tabs is null || tabs.Count < 2)
        {
            return;
        }

        // EnumWindows отдаёт окна сверху вниз
        var zOrder = new List<IntPtr>();
        EnumWindows((h, _) =>
        {
            zOrder.Add(h);
            return true;
        }, IntPtr.Zero);

        var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var closed = 0;

        foreach (var hwnd in zOrder)
        {
            if (!tabs.TryGetValue(hwnd, out var paths) || paths.Count != 1 || paths[0].Length == 0)
            {
                continue;
            }

            if (kept.Add(paths[0]))
            {
                continue;
            }

            Close(hwnd);
            closed++;
            AppLog.Info($"Закрыто повторное окно Проводника: {paths[0]}");
        }

        if (closed > 0)
        {
            AppLog.Info($"Закрыто дублей папок при проверке всех окон: {closed}.");
        }
    }

    private static void Close(IntPtr hwnd)
    {
        if (Parking.IsParked(hwnd))
        {
            Parking.Unpark(hwnd, minimize: true);
        }

        PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    private static void Try(IntPtr hwnd, int attempt)
    {
        var timer = new DispatcherTimer { Interval = Attempts[attempt] };
        timer.Tick += (_, _) =>
        {
            timer.Stop();

            if (!Settings.Current.CloseDuplicateFolders || !IsWindow(hwnd))
            {
                return;
            }

            if (!Check(hwnd) && attempt + 1 < Attempts.Length)
            {
                Try(hwnd, attempt + 1);
            }
        };
        timer.Start();
    }

    /// <summary>true — папка нового окна известна и проверка сделана.</summary>
    private static bool Check(IntPtr newHwnd)
    {
        var tabs = Snapshot();

        if (tabs is null || !tabs.TryGetValue(newHwnd, out var mine) || mine.Count != 1 || mine[0].Length == 0)
        {
            return false;
        }

        var path = mine[0];

        foreach (var (hwnd, paths) in tabs)
        {
            if (hwnd == newHwnd || paths.Count != 1 || !string.Equals(paths[0], path, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Close(hwnd);
            AppLog.Info($"Закрыто повторное окно Проводника: {path}");
        }

        return true;
    }

    /// <summary>Все окна Проводника и папки во вкладках каждого: HWND → пути.</summary>
    private static Dictionary<IntPtr, List<string>>? Snapshot()
    {
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");

            if (type is null)
            {
                return null;
            }

            dynamic shell = Activator.CreateInstance(type)!;
            var result = new Dictionary<IntPtr, List<string>>();

            foreach (var window in shell.Windows())
            {
                try
                {
                    string full = window.FullName ?? string.Empty;

                    if (!full.EndsWith("explorer.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var hwnd = new IntPtr((long)window.HWND);

                    if (!result.TryGetValue(hwnd, out var list))
                    {
                        result[hwnd] = list = new List<string>();
                    }

                    list.Add(PathOf(window));
                }
                catch
                {
                    // окно закрылось, пока перебирали, — пропускаем
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Не узнать папки окон Проводника: " + ex.Message);
            return null;
        }
    }

    /// <summary>Путь папки, в том числе «Этот компьютер» и прочие виртуальные (у них свой ::{GUID}).</summary>
    private static string PathOf(dynamic window)
    {
        try
        {
            string path = window.Document.Folder.Self.Path;
            return Normalize(path);
        }
        catch
        {
            try
            {
                string url = window.LocationURL ?? string.Empty;
                return url.Length == 0 ? string.Empty : Normalize(new Uri(url).LocalPath);
            }
            catch
            {
                return string.Empty;
            }
        }
    }

    private static string Normalize(string path)
    {
        path = path.Trim();

        if (path.Length > 3 && (path.EndsWith('\\') || path.EndsWith('/')))
        {
            path = path.TrimEnd('\\', '/');
        }

        try
        {
            return path.StartsWith("::", StringComparison.Ordinal) ? path : Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }
}
