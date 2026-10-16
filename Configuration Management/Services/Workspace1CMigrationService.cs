using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Configuration_Management.Models;

namespace Configuration_Management.Services;

/// <summary>
/// Сервис перехода на новую программу Workspace 1C (0.3.12.70). Получает последний
/// выпуск репозитория https://github.com/sivatorov/Workspace1C через GitHub API,
/// выбирает ассет по текущей ОС (Windows — <c>Workspace1C.exe</c>, Linux —
/// <c>Workspace1C-linux-x64</c>) и скачивает его в папку «Загрузки» пользователя
/// с отчётом о прогрессе. Кроссплатформенный: используется и Windows/WPF,
/// и Linux/Avalonia. При сбое возвращает null и не бросает исключения наружу,
/// чтобы диалог перехода не ронял приложение.
/// </summary>
public sealed class Workspace1CMigrationService
{
    /// <summary>URL последнего выпуска Workspace 1C (GitHub API).</summary>
    private const string ApiUrl = "https://api.github.com/repos/sivatorov/Workspace1C/releases/latest";

    /// <summary>Страница проекта Workspace 1C на GitHub.</summary>
    public const string ProjectUrl = "https://github.com/sivatorov/Workspace1C";

#if WINDOWS
    /// <summary>Имя Windows-ассета (self-contained exe).</summary>
    private const string AssetName = "Workspace1C.exe";
#else
    /// <summary>Имя Linux-ассета (self-contained single-file бинарник).</summary>
    private const string AssetName = "Workspace1C-linux-x64";
#endif

    private readonly HttpClient _http;

    /// <summary>
    /// Событие прогресса скачивания. Значение — проценты 0–100; −1 означает, что
    /// размер файла заранее неизвестен (индикатор переводится в неопределённый режим).
    /// Вызывается из фонового потока, поэтому подписчик должен сам перейти в UI-поток.
    /// </summary>
    public event Action<double>? DownloadProgressChanged;

    public Workspace1CMigrationService()
    {
        _http = new HttpClient();
        // GitHub требует корректный User-Agent.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("ConfigurationManagement/1.0");
        // Single-file exe может весить десятки МБ — таймаут больше, чем у API.
        _http.Timeout = TimeSpan.FromMinutes(20);
    }

    /// <summary>
    /// Получает последний выпуск Workspace 1C через GitHub API. При сбое сети,
    /// HTTP или парсинга возвращает null и не бросает исключения наружу.
    /// </summary>
    public async Task<ReleaseInfo?> GetLatestReleaseAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.GetAsync(ApiUrl, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ParseRelease(json);
        }
        catch
        {
            // Сбой сети/HTTP/JSON — переход недоступен, но приложение продолжает работать.
            return null;
        }
    }

    /// <summary>
    /// Разбирает ответ GitHub API <c>releases/latest</c> в <see cref="ReleaseInfo"/>.
    /// Возвращает null при нераспознанном JSON.
    /// </summary>
    internal static ReleaseInfo? ParseRelease(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            var release = new ReleaseInfo
            {
                TagName = GetString(root, "tag_name"),
                Name = GetString(root, "name"),
                HtmlUrl = GetString(root, "html_url"),
            };

            (release.DownloadUrl, release.AssetSize) = FindAsset(root);
            return release;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Ищет в assets выпуска ассет, подходящий для текущей платформы
    /// (<see cref="AssetName"/>), и возвращает пару «ссылка + точный размер
    /// в байтах» (поле <c>size</c> ассета из GitHub API).
    /// </summary>
    internal static (string Url, long Size) FindAsset(JsonElement root)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return (string.Empty, 0);

        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.ValueKind != JsonValueKind.Object)
                continue;

            var name = GetString(asset, "name");
            if (string.IsNullOrEmpty(name) || !IsPlatformAsset(name))
                continue;

            var url = GetString(asset, "browser_download_url");
            if (string.IsNullOrEmpty(url))
                continue;

            // Размер ассета GitHub отдаёт целым числом байт; отсутствует/не число — 0.
            var size = asset.TryGetProperty("size", out var sizeEl) && sizeEl.ValueKind == JsonValueKind.Number
                && sizeEl.TryGetInt64(out var parsed)
                ? parsed
                : 0;
            return (url, size);
        }

        return (string.Empty, 0);
    }

    /// <summary>
    /// Проверяет, подходит ли имя ассета для текущей ОС: Windows — имя содержит
    /// «workspace1c» и оканчивается на «.exe»; Linux — имя содержит «workspace1c»
    /// и «linux-x64».
    /// </summary>
    internal static bool IsPlatformAsset(string name)
    {
        var lower = name.ToLowerInvariant();
#if WINDOWS
        return lower.Contains("workspace1c", StringComparison.Ordinal)
            && lower.EndsWith(".exe", StringComparison.Ordinal);
#else
        return lower.Contains("workspace1c", StringComparison.Ordinal)
            && lower.Contains("linux-x64", StringComparison.Ordinal);
#endif
    }

    /// <summary>
    /// Каталог «Загрузки» пользователя: Windows — <c>%USERPROFILE%\Downloads</c>,
    /// Linux — <c>~/Downloads</c>. При недоступности профиля — временный каталог.
    /// </summary>
    internal static string GetDownloadsDirectory()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(profile))
            return Path.GetTempPath();
        return Path.Combine(profile, "Downloads");
    }

    /// <summary>
    /// Скачивает файл последнего выпуска в папку «Загрузки» с отчётом о прогрессе
    /// через <see cref="DownloadProgressChanged"/>. Возвращает путь к сохранённому
    /// файлу или null при сбое (сеть, пустой файл, недоступная папка). Частично
    /// скачанный файл при неудаче удаляется.
    /// </summary>
    public async Task<string?> DownloadAsync(ReleaseInfo release, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(release.DownloadUrl))
            return null;

        var dir = GetDownloadsDirectory();
        try
        {
            Directory.CreateDirectory(dir);
        }
        catch
        {
            // Папка «Загрузки» недоступна — скачивание невозможно.
            return null;
        }

        var dest = Path.Combine(dir, ResolveFileName(release.DownloadUrl));

        try
        {
            // ResponseHeadersRead: тело пишется на диск потоком, а не буферизуется
            // целиком в памяти (файл весит десятки МБ).
            using var response = await _http
                .GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            // Общий размер сервер сообщает не всегда: без него доля неизвестна,
            // и индикатор переводится в неопределённый режим (−1).
            var totalBytes = response.Content.Headers.ContentLength ?? -1;
            await using var target = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);

            var buffer = new byte[81920];
            long readTotal = 0;
            var lastPercent = -1;
            ReportProgress(totalBytes > 0 ? 0 : -1);

            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                readTotal += read;

                if (totalBytes <= 0)
                    continue;

                // Отчёт только на смене целого процента: иначе на каждый блок
                // в 80 КБ приходилась бы отправка в поток интерфейса.
                var percent = (int)Math.Min(100, readTotal * 100 / totalBytes);
                if (percent == lastPercent)
                    continue;
                lastPercent = percent;
                ReportProgress(percent);
            }

            await target.FlushAsync(ct).ConfigureAwait(false);

            // Пустой файл — считаем загрузку неудачной.
            if (new FileInfo(dest).Length <= 0)
            {
                TryDelete(dest);
                return null;
            }

            return dest;
        }
        catch
        {
            TryDelete(dest);
            return null;
        }
    }

    /// <summary>Имя файла из URL ассета; при неудаче — имя ассета платформы.</summary>
    private static string ResolveFileName(string url)
    {
        try
        {
            var name = Path.GetFileName(new Uri(url).AbsolutePath);
            return string.IsNullOrWhiteSpace(name) ? AssetName : name;
        }
        catch
        {
            return AssetName;
        }
    }

    private void ReportProgress(double percent)
    {
        try
        {
            DownloadProgressChanged?.Invoke(percent);
        }
        catch
        {
            // Подписчик не должен ронять загрузку.
        }
    }

    private static string GetString(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Не критично — частичный файл останется в «Загрузках».
        }
    }
}