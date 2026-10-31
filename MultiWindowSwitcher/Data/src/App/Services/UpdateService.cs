using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using MultiWindowSwitcher.Core;

namespace MultiWindowSwitcher.Services;

public enum UpdateStage
{
    Idle,
    Checking,
    Downloading,
    Unpacking,
    Staging,
    Ready,
    UpToDate,
    Failed,
}

/// <summary>Что видно в шапке окна: проверяем, есть новая, последняя, проверить не вышло.</summary>
public enum UpdateCheckState
{
    Idle,
    Checking,
    Available,
    UpToDate,
    Failed,
}

/// <summary>Что происходит прямо сейчас: текст и, если известно, процент для полосы.</summary>
public sealed record UpdateProgress(UpdateStage Stage, string Text, double? Percent = null);

/// <summary>Версия, которая лежит на сайте.</summary>
public sealed class RemoteRelease
{
    public required string Version { get; init; }

    public string Built { get; init; } = string.Empty;

    public string PackageUrl { get; init; } = UpdateService.PackageUrl;

    public string Caption =>
        string.IsNullOrWhiteSpace(Built) ? Version : $"{Version} · {Built}";
}

/// <summary>
/// Обновление программы с byfox.dev.
///
/// Порядок жёсткий: сначала манифест — если там версия свежее, скачивается архив,
/// распаковывается и проверяется, и только потом, отдельным шагом, подменяются файлы.
/// Меняются ровно две вещи: папка Data целиком и лаунчер в корне. Всё остальное из
/// архива (там лежит полная программа, включая UserData) отбрасывается — настройки
/// и имена окон пользователя обновление не касается.
///
/// Подменяет файлы не сама программа, а лаунчер: он дожидается выхода программы,
/// перекладывает подготовленное и запускает уже новую версию.
/// </summary>
public static class UpdateService
{
    public const string SiteUrl = "https://byfox.dev";

    /// <summary>Страница утилиты на сайте: описание, демо, загрузка.</summary>
    public const string PageUrl = "https://byfox.dev/multiwindowswitcher/";

    public const string ManifestUrl = "https://byfox.dev/data/multiwindowswitcher/manifest.json";

    /// <summary>Тот же архив, что качается со страницы утилиты: программа и обновление — один файл.</summary>
    public const string PackageUrl = "https://byfox.dev/data/multiwindowswitcher/MultiWindowSwitcher.zip";

    private static readonly HttpClient Http = CreateClient();

    private static RemoteRelease? _available;
    private static UpdateCheckState _state;

    /// <summary>Найденное при последней проверке обновление. Пусто — либо не проверяли, либо версия последняя.</summary>
    public static RemoteRelease? Available
    {
        get => _available;
        private set => _available = value;
    }

    public static UpdateCheckState State
    {
        get => _state;
        private set
        {
            _state = value;

            // плашка в шапке окна меняется по этому событию
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
            {
                StateChanged?.Invoke();
            }
            else
            {
                dispatcher.BeginInvoke(new Action(() => StateChanged?.Invoke()));
            }
        }
    }

    public static event Action? StateChanged;

    /// <summary>Обновление уже скачано и ждёт перезапуска.</summary>
    public static bool StagedReady => File.Exists(AppPaths.StagedMarkerFile);

    // ── проверка ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Прочитать манифест с сайта. Возвращает версию, только если она свежее
    /// установленной; иначе null.
    /// </summary>
    public static async Task<RemoteRelease?> CheckAsync(
        IProgress<UpdateProgress>? progress = null,
        CancellationToken token = default)
    {
        State = UpdateCheckState.Checking;
        progress?.Report(new UpdateProgress(UpdateStage.Checking, Loc.T("UpdManifest")));

        try
        {
            var remote = await FetchManifestAsync(token);

            if (!IsNewer(remote.Version, AppVersion.Version))
            {
                Available = null;
                State = UpdateCheckState.UpToDate;
                progress?.Report(new UpdateProgress(UpdateStage.UpToDate, Loc.F("UpdUpToDate", AppVersion.Version)));
                return null;
            }

            Available = remote;
            State = UpdateCheckState.Available;
            progress?.Report(new UpdateProgress(UpdateStage.Checking, Loc.F("UpdFound", remote.Caption)));
            return remote;
        }
        catch
        {
            State = UpdateCheckState.Failed;
            throw;
        }
    }

    /// <summary>Проверка при каждом запуске: в журнал — итог, в шапку окна — кнопка, если есть что ставить.</summary>
    public static async Task CheckOnStartupAsync()
    {
        try
        {
            var release = await CheckAsync();

            AppLog.Info(release is null
                ? $"Обновлений нет, установлена последняя версия {AppVersion.Version}."
                : $"Доступно обновление {release.Caption}.");
        }
        catch (Exception ex)
        {
            AppLog.Warn("Проверить обновления не удалось: " + ex.Message);
        }
    }

    private static async Task<RemoteRelease> FetchManifestAsync(CancellationToken token)
    {
        // против кэша прокси и CDN: манифест должен читаться свежим
        var url = ManifestUrl + "?t=" + DateTime.UtcNow.Ticks;

        using var response = await Http.GetAsync(url, token);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException(Loc.T("UpdNoManifest"));
        }

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(token);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var version = Text(root, "version");

        if (string.IsNullOrWhiteSpace(version))
        {
            throw new InvalidOperationException(Loc.T("UpdNoVersion"));
        }

        var packageUrl = Text(root, "url");

        return new RemoteRelease
        {
            Version = version.Trim(),
            Built = Text(root, "built").Trim(),
            PackageUrl = string.IsNullOrWhiteSpace(packageUrl) ? PackageUrl : packageUrl.Trim(),
        };

        static string Text(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;
    }

    /// <summary>Сравнение номеров вида 1.2.3. Что не разбирается — считаем нулём.</summary>
    public static bool IsNewer(string remote, string local) => Parse(remote) > Parse(local);

    private static Version Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new Version(0, 0, 0, 0);
        }

        var digits = new string(text.Trim().Where(c => char.IsDigit(c) || c == '.').ToArray()).Trim('.');
        var parts = digits.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var numbers = new int[4];

        for (var i = 0; i < numbers.Length && i < parts.Length; i++)
        {
            _ = int.TryParse(parts[i], out numbers[i]);
        }

        return new Version(numbers[0], numbers[1], numbers[2], numbers[3]);
    }

    // ── скачивание и подготовка ──────────────────────────────────────────────

    /// <summary>
    /// Скачать архив, распаковать, проверить и сложить в Update\staged готовую замену.
    /// Ничего работающего на этом шаге не трогается: программа продолжает работать.
    /// </summary>
    public static async Task<string> DownloadAndStageAsync(
        RemoteRelease release,
        IProgress<UpdateProgress> progress,
        CancellationToken token = default)
    {
        var downloadDir = Path.Combine(AppPaths.UpdateDir, "download");
        var unpackedDir = Path.Combine(AppPaths.UpdateDir, "unpacked");
        var zipPath = Path.Combine(downloadDir, "MultiWindowSwitcher.zip");

        Drop(downloadDir);
        Drop(unpackedDir);
        Drop(AppPaths.StagedDir);

        Directory.CreateDirectory(downloadDir);

        await DownloadAsync(release.PackageUrl, zipPath, progress, token);
        await Task.Run(() => Unpack(zipPath, unpackedDir, progress, token), token);

        progress.Report(new UpdateProgress(UpdateStage.Staging, Loc.T("UpdChecking2")));

        var version = await Task.Run(() => Stage(unpackedDir, release, progress), token);

        Drop(downloadDir);
        Drop(unpackedDir);

        progress.Report(new UpdateProgress(UpdateStage.Ready, Loc.F("UpdReady", version)));

        AppLog.Success($"Обновление {version} скачано и подготовлено.");
        return version;
    }

    private static async Task DownloadAsync(
        string url,
        string target,
        IProgress<UpdateProgress> progress,
        CancellationToken token)
    {
        progress.Report(new UpdateProgress(UpdateStage.Downloading, Loc.T("UpdConnecting"), 0));

        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? 0;

        await using var source = await response.Content.ReadAsStreamAsync(token);
        await using var file = File.Create(target);

        var buffer = new byte[128 * 1024];
        long done = 0;
        var lastReport = -1L;
        int read;

        while ((read = await source.ReadAsync(buffer, token)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), token);
            done += read;

            // полоса не должна дёргаться на каждом килобайте
            if (done - lastReport < 256 * 1024 && done != total)
            {
                continue;
            }

            lastReport = done;

            progress.Report(new UpdateProgress(
                UpdateStage.Downloading,
                total > 0 ? Loc.F("UpdDownloading", Size(done), Size(total)) : Loc.F("UpdDownloadingNoTotal", Size(done)),
                total > 0 ? done * 100.0 / total : null));
        }

        progress.Report(new UpdateProgress(UpdateStage.Downloading, Loc.F("UpdDownloaded", Size(done)), 100));
    }

    private static void Unpack(
        string zipPath,
        string targetDir,
        IProgress<UpdateProgress> progress,
        CancellationToken token)
    {
        Directory.CreateDirectory(targetDir);

        using var archive = ZipFile.OpenRead(zipPath);

        var root = Path.GetFullPath(targetDir) + Path.DirectorySeparatorChar;
        var total = archive.Entries.Count;
        var done = 0;

        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();

            var target = Path.GetFullPath(Path.Combine(targetDir, entry.FullName));

            // архив не должен уметь писать мимо своей папки
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(Loc.F("UpdBadPath", entry.FullName));
            }

            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(target);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, true);
            }

            done++;

            if (done % 20 == 0 || done == total)
            {
                progress.Report(new UpdateProgress(
                    UpdateStage.Unpacking,
                    Loc.F("UpdUnpacking", done, total),
                    total > 0 ? done * 100.0 / total : null));
            }
        }
    }

    /// <summary>
    /// Отобрать из распакованного только нужное: папку Data и лаунчер.
    /// UserData из архива сознательно игнорируется — там настройки того, кто собирал.
    /// </summary>
    private static string Stage(string unpackedDir, RemoteRelease release, IProgress<UpdateProgress> progress)
    {
        var newData = FindDataDir(unpackedDir)
            ?? throw new InvalidOperationException(Loc.T("UpdNoData"));

        var packageRoot = Directory.GetParent(newData)!.FullName;
        var version = ReadVersion(Path.Combine(newData, "manifest.json")) ?? release.Version;

        if (!IsNewer(version, AppVersion.Version))
        {
            throw new InvalidOperationException(Loc.F("UpdNotNewer", version, AppVersion.Version));
        }

        Directory.CreateDirectory(AppPaths.StagedDir);

        var stagedData = Path.Combine(AppPaths.StagedDir, "Data");
        Directory.Move(newData, stagedData);

        var newLauncher = FindLauncher(packageRoot);

        if (newLauncher is not null)
        {
            File.Copy(newLauncher, Path.Combine(AppPaths.StagedDir, AppPaths.LauncherName), true);
        }
        else
        {
            AppLog.Warn("В архиве нет лаунчера — обновится только папка Data.");
        }

        progress.Report(new UpdateProgress(UpdateStage.Staging, Loc.T("UpdPreparing")));

        var marker = JsonSerializer.Serialize(
            new
            {
                version,
                built = release.Built,
                staged = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                launcher = newLauncher is not null,
            },
            new JsonSerializerOptions { WriteIndented = true });

        File.WriteAllText(AppPaths.StagedMarkerFile, marker);

        return version;
    }

    /// <summary>Ищем Data с программой внутри — архив может быть и с папкой сверху, и без неё.</summary>
    private static string? FindDataDir(string unpackedDir)
    {
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((unpackedDir, 0));

        while (queue.Count > 0)
        {
            var (path, depth) = queue.Dequeue();

            foreach (var dir in Directory.GetDirectories(path))
            {
                if (string.Equals(Path.GetFileName(dir), "Data", StringComparison.OrdinalIgnoreCase)
                    && File.Exists(Path.Combine(dir, "manifest.json"))
                    && File.Exists(Path.Combine(dir, AppPaths.AppExeName)))
                {
                    return dir;
                }

                if (depth < 3)
                {
                    queue.Enqueue((dir, depth + 1));
                }
            }
        }

        return null;
    }

    private static string? FindLauncher(string packageRoot)
    {
        var expected = Path.Combine(packageRoot, AppPaths.LauncherName);

        if (File.Exists(expected))
        {
            return expected;
        }

        return Directory.GetFiles(packageRoot, "*.exe").FirstOrDefault();
    }

    private static string? ReadVersion(string manifestPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));

            return doc.RootElement.TryGetProperty("version", out var version)
                ? version.GetString()?.Trim()
                : null;
        }
        catch
        {
            return null;
        }
    }

    // ── установка ────────────────────────────────────────────────────────────

    /// <summary>
    /// Передать подготовленное обновление лаунчеру и выйти. Дальше всё делает он:
    /// дождётся закрытия программы, заменит Data и себя, запустит новую версию.
    /// </summary>
    public static void ApplyAndRestart()
    {
        if (!StagedReady)
        {
            throw new InvalidOperationException(Loc.T("UpdNotStaged"));
        }

        if (!File.Exists(AppPaths.LauncherFile))
        {
            throw new InvalidOperationException(Loc.F("UpdNoLauncher", AppPaths.LauncherFile));
        }

        var info = new ProcessStartInfo(AppPaths.LauncherFile)
        {
            WorkingDirectory = AppPaths.RootDir,
            UseShellExecute = false,
        };

        info.ArgumentList.Add("--apply-update");
        info.ArgumentList.Add(Environment.ProcessId.ToString());

        Process.Start(info);

        AppLog.Info("Программа закрывается: лаунчер устанавливает обновление.");
        ((App)Application.Current).Quit("установка обновления");
    }

    /// <summary>Убрать временную папку после отмены или сбоя. Найденная версия остаётся — можно повторить.</summary>
    public static void ClearTemp() => Drop(AppPaths.UpdateDir);

    // ── мелочи ───────────────────────────────────────────────────────────────

    private static void Drop(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                ClearReadOnly(new DirectoryInfo(path));
                Directory.Delete(path, true);
            }
        }
        catch
        {
            // не удалилось — дальше упадёт понятнее, на попытке записи
        }
    }

    private static void ClearReadOnly(DirectoryInfo dir)
    {
        dir.Attributes = FileAttributes.Directory;

        foreach (var file in dir.GetFiles())
        {
            file.Attributes = FileAttributes.Normal;
        }

        foreach (var sub in dir.GetDirectories())
        {
            ClearReadOnly(sub);
        }
    }

    private static string Size(long bytes)
    {
        var ru = Loc.I.IsRu;

        return bytes switch
        {
            >= 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024 / 1024:0.0} {(ru ? "ГБ" : "GB")}",
            >= 1024L * 1024 => $"{bytes / 1024.0 / 1024:0.0} {(ru ? "МБ" : "MB")}",
            >= 1024 => $"{bytes / 1024.0:0} {(ru ? "КБ" : "KB")}",
            _ => $"{bytes} {(ru ? "Б" : "B")}",
        };
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(20),
        };

        // в заголовке только ASCII
        var version = new string(AppVersion.Version
            .Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-')
            .ToArray());

        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "MultiWindowSwitcher/" + (version.Length > 0 ? version : "0.0.0"));
        client.DefaultRequestHeaders.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
        {
            NoCache = true,
        };

        return client;
    }
}
