using System.Net.Http;
using System.Text.Json;
using System.Xml.Linq;
using Configuration_Management.Models;

namespace Configuration_Management.Services;

/// <summary>
/// Проверка наличия новых версий приложения через GitHub Releases API.
/// Кроссплатформенный: имя искомого asset выбирается по текущей ОС (Windows — exe,
/// Linux — self-contained single-file бинарник без расширения).
/// </summary>
public class GitHubReleaseService
{
    /// <summary>Базовый URL приёмника GitHub Releases (latest release).</summary>
    private const string ApiUrl = "https://api.github.com/repos/sivatorov/ConfigurationManagement/releases/latest";

    /// <summary>Резервный источник — Atom-лента релизов (доступна через обычный github.com).</summary>
    private const string AtomUrl = "https://github.com/sivatorov/ConfigurationManagement/releases.atom";

#if WINDOWS
    /// <summary>Имя Windows-сборки (self-contained exe), на которую указывает прямая ссылка загрузки.</summary>
    private const string AssetName = "ConfigurationManagement.exe";
#else
    /// <summary>Имя Linux-сборки (self-contained single-file бинарник без расширения, см. build-linux-single-file.sh).</summary>
    private const string AssetName = "ConfigurationManagement";
#endif

    /// <summary>Шаблон прямой ссылки на asset release в GitHub Releases (без нормализации тега).</summary>
    private const string DownloadUrlTemplate =
        "https://github.com/sivatorov/ConfigurationManagement/releases/download/{0}/" + AssetName;

    /// <summary>Namespace Atom-ленты GitHub.</summary>
    private static readonly XNamespace AtomNs = XNamespace.Get("http://www.w3.org/2005/Atom");

    private static readonly HttpClient HttpClient = CreateHttpClient();

    /// <summary>
    /// Возвращает информацию о последнем выпуске из GitHub Releases.
    /// Сначала пробуется GitHub Releases API; если он недоступен/не распознан
    /// (api.github.com может не отвечать, хотя github.com работает), используется
    /// резервный источник — Atom-лента релизов. При сбое всех источников возвращает
    /// null и не бросает исключения наружу.
    /// </summary>
    public async Task<ReleaseInfo?> GetLatestReleaseAsync(CancellationToken ct = default)
    {
        // Основной источник — GitHub Releases API.
        var apiRelease = await GetLatestFromApiAsync(ct).ConfigureAwait(false);
        if (apiRelease is not null)
            return apiRelease;

        // Резервный источник — Atom-лента релизов через обычный github.com.
        return await GetLatestFromAtomAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Получает последний выпуск через GitHub Releases API.</summary>
    private static async Task<ReleaseInfo?> GetLatestFromApiAsync(CancellationToken ct)
    {
        try
        {
            using var response = await HttpClient.GetAsync(ApiUrl, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            var release = new ReleaseInfo
            {
                TagName = GetString(root, "tag_name"),
                Name = GetString(root, "name"),
                Body = GetString(root, "body"),
                Prerelease = root.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True,
                PublishedAt = TryGetDateTimeOffset(root, "published_at"),
                HtmlUrl = GetString(root, "html_url"),
            };

            (release.DownloadUrl, release.AssetSize) = FindAsset(root);
            return release;
        }
        catch
        {
            // Сбой сети, HTTP или JSON — переходим к резервному источнику.
            return null;
        }
    }

    /// <summary>
    /// Получает последний выпуск через Atom-ленту релизов (резервный источник).
    /// Из первого <c><entry></c> ленты извлекаются тег, дата и ссылка на страницу
    /// релиза. Прямой ссылки на asset в ленте нет, поэтому она собирается по шаблону
    /// GitHub Releases из полного тега (см. <see cref="BuildWindowsDownloadUrl"/>).
    /// DownloadUrl заполняется, когда тег позволяет собрать валидный URL; HtmlUrl
    /// (страница релиза) заполняется всегда, как запасной путь для ручной загрузки.
    /// </summary>
    private static async Task<ReleaseInfo?> GetLatestFromAtomAsync(CancellationToken ct)
    {
        try
        {
            using var response = await HttpClient.GetAsync(AtomUrl, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            var xml = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var doc = XDocument.Parse(xml);

            var entry = doc.Root?.Element(AtomNs + "entry");
            if (entry is null)
                return null;

            var tag = entry.Element(AtomNs + "title")?.Value ?? string.Empty;
            if (string.IsNullOrWhiteSpace(tag))
                return null;

            var htmlUrl = entry.Elements(AtomNs + "link")
                .Where(l => (string?)l.Attribute("rel") == "alternate")
                .Select(l => (string?)l.Attribute("href"))
                .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

            var publishedAt = DateTimeOffset.TryParse(entry.Element(AtomNs + "updated")?.Value, out var parsed)
                ? parsed
                : (DateTimeOffset?)null;

            return new ReleaseInfo
            {
                TagName = tag,
                Name = tag,
                Body = CleanText(entry.Element(AtomNs + "content")?.Value ?? string.Empty),
                Prerelease = false,
                PublishedAt = publishedAt,
                DownloadUrl = BuildDownloadUrl(tag),
                HtmlUrl = htmlUrl,
            };
        }
        catch
        {
            // Лента недоступна или не распознана — обновление недоступно.
            return null;
        }
    }

    /// <summary>
    /// Строит прямую ссылку на сборку текущей платформы (<see cref="AssetName"/>) для тега
    /// релиза из Atom-ленты. Тег подставляется в путь загрузки БЕЗ нормализации — таким,
    /// каким он указан в <c><title></c> ленты, чтобы ссылка точно совпадала с
    /// фактическим именем release на GitHub. Небезопасные символы пути экранируются,
    /// итоговый URL проверяется через <see cref="Uri.TryCreate(string, UriKind, out Uri)"/>.
    /// Возвращает null, если тег пуст или из него не удалось собрать валидную ссылку.
    /// </summary>
    private static string? BuildDownloadUrl(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return null;

        var url = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            DownloadUrlTemplate,
            Uri.EscapeDataString(tag));

        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? url
            : null;
    }

    /// <summary>
    /// Приводит текстовое содержимое <c><content></c> ленты к читаемому виду:
    /// убирает пустые строки, обрезает краевые пробелы и сворачивает переносы.
    /// HTML-теги отсутствуют, т.к. берётся текстовое значение элемента (XElement.Value).
    /// </summary>
    private static string CleanText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        return string.Join(
            "\n",
            text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0));
    }

    /// <summary>
    /// Сравнивает версию выпуска с текущей версией приложения.
    /// Тег нормализуется (обрезается ведущий «v») и сравнивается как 4-частная версия.
    /// Возвращает true, если выпуск новее текущей версии.
    /// При невозможности распарсить одну из версий возвращает false.
    /// </summary>
    public static bool IsNewerThan(ReleaseInfo release, string currentVersion)
    {
        if (release is null)
            return false;

        if (!TryParseVersion(NormalizeTag(release.TagName), out var releaseVer))
            return false;

        if (!TryParseVersion(NormalizeTag(currentVersion), out var currentVer))
            return false;

        return releaseVer > currentVer;
    }

    /// <summary>
    /// Извлекает из строки тега подстроку, начинающуюся с первой цифры, и обрезает
    /// хвост на первом пробеле, двоеточии или «+». Это устойчиво к произвольным
    /// префиксам тегов: «new-0.3.5.75» → «0.3.5.75», «v0.3.5.74» → «0.3.5.74»,
    /// «new-0.3.5.16: Merge …» → «0.3.5.16», «0.3.5.70» → «0.3.5.70».
    /// </summary>
    private static string NormalizeTag(string tag)
    {
        if (string.IsNullOrEmpty(tag))
            return tag;

        var start = -1;
        for (var i = 0; i < tag.Length; i++)
        {
            if (char.IsDigit(tag[i]))
            {
                start = i;
                break;
            }
        }

        // В теге нет цифр — парсить нечего.
        if (start < 0)
            return tag;

        var end = tag.Length;
        for (var i = start; i < tag.Length; i++)
        {
            var c = tag[i];
            if (c == ' ' || c == ':' || c == '+')
            {
                end = i;
                break;
            }
        }

        return tag.Substring(start, end - start);
    }

    private static bool TryParseVersion(string text, out Version version)
    {
        // Защита от хвостовых суффиксов вида «+sha», если они не были обрезаны ранее.
        var plus = text.IndexOf('+');
        if (plus >= 0)
            text = text.Substring(0, plus);

        // Устойчиво и к 3-частным, и к 4-частным версиям (например «0.3.5.75»).
        return Version.TryParse(text.Trim(), out version!);
    }

    /// <summary>
    /// Ищет в assets выпуска asset, подходящий для текущей платформы, и возвращает
    /// пару «ссылка + точный размер файла в байтах» (поле <c>size</c> ассета из GitHub
    /// API). Размер используется загрузчиком для строгой проверки скачанного файла
    /// (issue #302).
    /// <para>
    /// Поиск двухпроходный (issue #358): сначала ищется ассет с ТОЧНЫМ именем
    /// <see cref="AssetName"/> (bare-exe «ConfigurationManagement.exe» для Windows) —
    /// он имеет жёсткий приоритет; лишь если его нет, принимаются прочие ассеты по
    /// признакам <see cref="IsPlatformAsset"/> (но не ZIP-архивы: они не могут быть
    /// поданы загрузчику напрямую и разрывали бы автообновление).
    /// </para>
    /// </summary>
    internal static (string Url, long Size) FindAsset(JsonElement root)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return (string.Empty, 0);

        // Проход 1: точное совпадение имени (bare-exe / bare-бинарь) — высший приоритет.
        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.ValueKind != JsonValueKind.Object)
                continue;

            var name = GetString(asset, "name");
            if (string.IsNullOrEmpty(name) || !IsExactAssetName(name))
                continue;

            if (TryGetAssetDownload(asset, out var result))
                return result;
        }

        // Проход 2: прочие ассеты по признакам платформы (ZIP-архивы исключены —
        // issue #358: zip с exe внутри не должен попадать в автообновление).
        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.ValueKind != JsonValueKind.Object)
                continue;

            var name = GetString(asset, "name");
            if (string.IsNullOrEmpty(name) || !IsPlatformAsset(name))
                continue;

            if (TryGetAssetDownload(asset, out var result))
                return result;
        }

        return (string.Empty, 0);
    }

    /// <summary>Проверяет, совпадает ли имя ассета с точным именем сборки текущей платформы.</summary>
    private static bool IsExactAssetName(string name) =>
        string.Equals(name.Trim(), AssetName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Извлекает из ассета ссылку на скачивание и размер. Возвращает false, если ссылки нет.
    /// </summary>
    private static bool TryGetAssetDownload(JsonElement asset, out (string Url, long Size) result)
    {
        result = default;
        var url = GetString(asset, "browser_download_url");
        if (string.IsNullOrEmpty(url))
            return false;

        // Размер ассета GitHub отдаёт целым числом байт; отсутствует/не число — 0.
        var size = asset.TryGetProperty("size", out var sizeEl) && sizeEl.ValueKind == JsonValueKind.Number
            && sizeEl.TryGetInt64(out var parsed)
            ? parsed
            : 0;
        result = (url, size);
        return true;
    }

#if WINDOWS
    /// <summary>
    /// Признак Windows-ассета. ZIP-архивы (в том числе «…win-x64.zip» с exe внутри)
    /// всегда false: загрузчик автообновления устанавливает скачанный файл напрямую
    /// (см. UpdatePayload), поэтому подсовывать ему архив нельзя (issue #358).
    /// Точное имя «ConfigurationManagement.exe» обрабатывается отдельным проходом
    /// <see cref="FindAsset"/> (высший приоритет).
    /// </summary>
    internal static bool IsPlatformAsset(string name)
    {
        var lower = name.ToLowerInvariant();
        if (lower.EndsWith(".zip", StringComparison.Ordinal))
            return false;
        return lower.EndsWith(".exe", StringComparison.Ordinal)
            || lower.Contains("win-x64", StringComparison.Ordinal)
            || lower.Contains("configurationmanagement.exe", StringComparison.Ordinal);
    }
#else
    /// <summary>
    /// Признак Linux-ассета. ZIP-архивы исключены: загрузчик ставит скачанный файл
    /// напрямую (issue #358). Linux-сборка — single-file исполняемый файл
    /// «ConfigurationManagement» без расширения (build-linux-single-file.sh),
    /// либо AppImage/архив с признаком linux-x64.
    /// </summary>
    internal static bool IsPlatformAsset(string name)
    {
        var lower = name.ToLowerInvariant();
        if (lower.EndsWith(".zip", StringComparison.Ordinal))
            return false;
        return string.Equals(name.Trim(), "ConfigurationManagement", StringComparison.OrdinalIgnoreCase)
            || lower.Contains("linux-x64", StringComparison.Ordinal)
            || lower.EndsWith(".appimage", StringComparison.Ordinal)
            || lower.EndsWith(".tar.gz", StringComparison.Ordinal);
    }
#endif

    private static string GetString(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    private static DateTimeOffset? TryGetDateTimeOffset(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
            return null;

        return DateTimeOffset.TryParse(value.GetString(), out var parsed) ? parsed : null;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        // GitHub API требует корректный User-Agent.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ConfigurationManagement/1.0");
        client.Timeout = TimeSpan.FromSeconds(15);
        return client;
    }
}