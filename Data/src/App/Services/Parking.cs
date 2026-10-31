using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using MultiWindowSwitcher.Core;
using static MultiWindowSwitcher.Native.Win32;

namespace MultiWindowSwitcher.Services;

/// <summary>
/// Живые превью свёрнутых окон.
///
/// Превью Windows (DWM) у свёрнутого окна замирает на последнем кадре: свёрнутое окно
/// просто не рисуется. Поэтому свёрнутое окно «паркуется»: оно невидимо разворачивается
/// (прозрачность 1/255, клики проходят насквозь, без фокуса, в самом низу стопки окон).
/// Для человека оно всё так же свёрнуто, а программа внутри продолжает рисовать —
/// и превью в сетке живое.
///
/// Окно возвращается в обычный вид, как только его выбирают — в сетке, на панели задач
/// или через Alt+Tab. При выходе все припаркованные окна снова сворачиваются как были.
/// Список хранится в UserData\parked.json: если программа упала, при следующем запуске
/// окна приводятся в порядок.
/// </summary>
public static class Parking
{
    private const uint RDW_INVALIDATE = 0x0001;
    private const uint RDW_ERASE = 0x0004;
    private const uint RDW_ALLCHILDREN = 0x0080;
    private const uint RDW_FRAME = 0x0400;
    private const int WPF_RESTORETOMAXIMIZED = 0x0002;

    private static readonly Dictionary<IntPtr, ParkedWindow> Parked = new();

    /// <summary>Окна, которые мы сами только что трогали: их событие «стало активным» — не от человека.</summary>
    private static readonly Dictionary<IntPtr, DateTime> Quiet = new();

    public static bool IsParked(IntPtr hwnd) => Parked.ContainsKey(hwnd);

    public static int Count => Parked.Count;

    /// <summary>Событие от этого окна сейчас лучше пропустить: его меняем мы сами.</summary>
    public static bool IsQuiet(IntPtr hwnd) =>
        Quiet.TryGetValue(hwnd, out var until) && until > DateTime.UtcNow;

    /// <summary>Свёрнутое окно — в «живой» режим. false — окно не подходит (уже прозрачное, от администратора…).</summary>
    public static bool TryPark(IntPtr hwnd)
    {
        if (Parked.ContainsKey(hwnd))
        {
            // программа сама свернула уже припаркованное окно — разворачиваем обратно невидимым
            if (IsIconic(hwnd))
            {
                Hush(hwnd);
                ShowWindow(hwnd, SW_SHOWNOACTIVATE);
                SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }

            return true;
        }

        if (!IsWindow(hwnd) || !IsIconic(hwnd))
        {
            return false;
        }

        var ex = GetExStyle(hwnd);

        // окно и так полупрозрачное или поверх всех — не наше дело, лучше не трогать
        if ((ex & WS_EX_LAYERED) != 0 || (ex & WS_EX_TOPMOST) != 0)
        {
            return false;
        }

        var placement = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        GetWindowPlacement(hwnd, ref placement);
        var wasMaximized = (placement.flags & WPF_RESTORETOMAXIMIZED) != 0;

        Marshal.SetLastPInvokeError(0);
        var result = SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_LAYERED | WS_EX_TRANSPARENT));

        // окно процесса с правами администратора менять нельзя — так и остаётся обычным свёрнутым
        if (result == IntPtr.Zero && Marshal.GetLastPInvokeError() != 0)
        {
            return false;
        }

        if ((GetExStyle(hwnd) & WS_EX_LAYERED) == 0)
        {
            return false;
        }

        SetLayeredWindowAttributes(hwnd, 0, 1, LWA_ALPHA);

        Hush(hwnd);
        ShowWindow(hwnd, SW_SHOWNOACTIVATE);
        SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

        Parked[hwnd] = new ParkedWindow(hwnd.ToInt64(), ex, wasMaximized);
        SaveList();
        return true;
    }

    /// <summary>
    /// Вернуть окну обычный вид. <paramref name="minimize"/> — снова свернуть (выход, выключили опцию),
    /// иначе окно остаётся развёрнутым: его выбрали.
    /// </summary>
    public static void Unpark(IntPtr hwnd, bool minimize)
    {
        if (!Parked.Remove(hwnd, out var parked))
        {
            return;
        }

        Restore(hwnd, parked, minimize);
        SaveList();
    }

    /// <summary>Поднять припаркованные окна наверх (под переключатель). Невидимы и прозрачны для мыши — не мешают.</summary>
    public static void RaiseAll()
    {
        Enforce();

        foreach (var hwnd in Parked.Keys.ToList())
        {
            if (!IsWindow(hwnd))
            {
                continue;
            }

            Hush(hwnd);
            SetWindowPos(hwnd, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
    }

    /// <summary>
    /// Программы на Electron/Chromium (Claude, VS Code…) иногда сами сбрасывают окну прозрачность —
    /// и невидимое окно кусками проступает поверх переключателя. Возвращаем как было.
    /// </summary>
    public static void Enforce()
    {
        foreach (var hwnd in Parked.Keys.ToList())
        {
            if (!IsWindow(hwnd) || IsIconic(hwnd))
            {
                continue;
            }

            var ex = GetExStyle(hwnd);
            var layered = (ex & WS_EX_LAYERED) != 0;

            if (layered && (ex & WS_EX_TRANSPARENT) != 0
                && GetLayeredWindowAttributes(hwnd, out _, out var alpha, out var flags)
                && (flags & LWA_ALPHA) != 0 && alpha == 1)
            {
                continue;
            }

            AppLog.Info($"Припаркованное окно стало видимым — снова прячем: {hwnd.ToInt64():X}.");
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_LAYERED | WS_EX_TRANSPARENT));
            SetLayeredWindowAttributes(hwnd, 0, 1, LWA_ALPHA);
        }
    }

    public static void UnparkAll()
    {
        foreach (var (hwnd, parked) in Parked.ToList())
        {
            Restore(hwnd, parked, minimize: true);
        }

        Parked.Clear();
        SaveList();
    }

    /// <summary>Окно закрылось — забыть.</summary>
    public static void Forget(IntPtr hwnd)
    {
        if (Parked.Remove(hwnd))
        {
            SaveList();
        }
    }

    /// <summary>После сбоя: окна, которые остались невидимыми, возвращаются свёрнутыми.</summary>
    public static void RecoverAfterCrash()
    {
        try
        {
            if (!File.Exists(AppPaths.ParkedFile))
            {
                return;
            }

            var list = JsonSerializer.Deserialize<List<ParkedWindow>>(File.ReadAllText(AppPaths.ParkedFile)) ?? new();
            var fixedCount = 0;

            foreach (var parked in list)
            {
                var hwnd = new IntPtr(parked.Hwnd);

                if (IsWindow(hwnd) && (GetExStyle(hwnd) & (WS_EX_LAYERED | WS_EX_TRANSPARENT)) == (WS_EX_LAYERED | WS_EX_TRANSPARENT))
                {
                    Restore(hwnd, parked, minimize: true);
                    fixedCount++;
                }
            }

            File.Delete(AppPaths.ParkedFile);

            if (fixedCount > 0)
            {
                AppLog.Warn($"После прошлого сбоя возвращено в обычный вид окон: {fixedCount}.");
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("parked.json не разобрать: " + ex.Message);
        }
    }

    private static void Restore(IntPtr hwnd, ParkedWindow parked, bool minimize)
    {
        if (!IsWindow(hwnd))
        {
            return;
        }

        Hush(hwnd);
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(parked.ExStyle));
        RedrawWindow(hwnd, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_ERASE | RDW_FRAME | RDW_ALLCHILDREN);

        if (minimize)
        {
            // через placement: так окно помнит, что разворачиваться ему надо на весь экран
            var placement = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
            GetWindowPlacement(hwnd, ref placement);
            placement.showCmd = SW_SHOWMINNOACTIVE;

            if (parked.WasMaximized)
            {
                placement.flags |= WPF_RESTORETOMAXIMIZED;
            }

            SetWindowPlacementSafe(hwnd, ref placement);
        }
        else if (parked.WasMaximized)
        {
            ShowWindow(hwnd, 3); // SW_MAXIMIZE
        }
    }

    private static void Hush(IntPtr hwnd) => Quiet[hwnd] = DateTime.UtcNow.AddMilliseconds(700);

    private static void SaveList()
    {
        try
        {
            if (Parked.Count == 0)
            {
                if (File.Exists(AppPaths.ParkedFile))
                {
                    File.Delete(AppPaths.ParkedFile);
                }

                return;
            }

            File.WriteAllText(AppPaths.ParkedFile, JsonSerializer.Serialize(Parked.Values.ToList()));
        }
        catch
        {
            // список нужен только на случай сбоя
        }
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowPlacement")]
    private static extern bool SetWindowPlacementSafe(IntPtr hWnd, ref WINDOWPLACEMENT placement);

    public sealed record ParkedWindow(long Hwnd, long ExStyle, bool WasMaximized);
}
