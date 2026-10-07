using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Configuration_Management.Models;

namespace Configuration_Management.Services;

/// <summary>
/// Чистый парсер каталога технологической платформы 1С на <c>releases.1c.ru</c>:
/// список версий со страницы <c>project/<ник></c> (все строки таблицы
/// <c>#versionsTable</c>, не только первая) и файлы дистрибутива из ответа
/// <c>version_files?nick=…&ver=…</c>. Не выполняет сетевых запросов, устойчив
/// к изменению структуры HTML/JSON портала и не бросает исключений на битом входе.
/// </summary>
public static class OneCPlatformCatalogParser
{
    /// <summary>
    /// Ник каталога технологической платформы 8.3 на <c>releases.1c.ru/project/<ник></c>.
    /// Значение сверено с фактическим адресом каталога на портале (п. 2.7 плана 0.3.9.208–216).
    /// </summary>
    public const string Platform83Nick = "Platform83";

    /// <summary>
    /// Ник каталога технологической платформы 8.5 на <c>releases.1c.ru/project/<ник></c>
    /// (issue #334: «стоит проверять и releases.1c.ru/project/Platform85»).
    /// </summary>
    public const string Platform85Nick = "Platform85";

    /// <summary>Алиас для обратной совместимости (использовался до введения списка ников).</summary>
    public const string PlatformNick = Platform83Nick;

    /// <summary>Поддерживаемые ники каталогов технологической платформы на releases.1c.ru.</summary>
    public static readonly IReadOnlyList<string> SupportedPlatformNicks =
        new[] { Platform83Nick, Platform85Nick };

    /// <summary>Регулярное выражение для ссылки на страницу файлов релиза вида
    /// <c>/version_files?nick=…&ver=…</c> с захватом адреса и текста (номера версии).</summary>
    private static readonly Regex VersionFilesLinkRegex = new(
        @"href\s*=\s*[""'](?<href>[^""']*version_files[^""']*ver\s*=[^""']*)[""'][^>]*>\s*(?<ver>[^<]+?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>Регулярное выражение таблицы <c>id="versionsTable"</c> (список версий каталога).</summary>
    private static readonly Regex VersionsTableRegex = new(
        @"<table[^>]*id\s*=\s*[""']versionsTable[""'][^>]*>(?<table>.*?)</table>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>Регулярное выражение строки <c><tr>…</tr></c> таблицы версий.</summary>
    private static readonly Regex TableRowRegex = new(
        @"<tr[^>]*>(?<row>.*?)</tr>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>Регулярное выражение для поиска прямых ссылок на файлы дистрибутивов платформы
    /// (<c>.zip</c>/<c>.deb</c>/<c>.rpm</c>/<c>.tar.gz</c>) в ответе <c>version_files</c>
    /// (JSON или HTML), устойчиво к неизвестной структуре. Query-часть ссылки учитывается.</summary>
    private static readonly Regex DistributionFileLinkRegex = new(
        @"(?<url>(?:https?://|/)[^""'\s<>]*?\.(?:zip|deb|rpm|tar\.gz)(?:[?#][^""'\s<>]*)?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Регулярное выражение для извлечения размера файла из JSON-полей
    /// <c>size</c>/<c>filesize</c>.</summary>
    private static readonly Regex SizeFieldRegex = new(
        @"""(?:size|filesize)""\s*:\s*(?<bytes>\d{1,15})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Регулярное выражение HTML-тегов (для снятия разметки строки таблицы при
    /// извлечении колонки «Список версий», issue #352).</summary>
    private static readonly Regex HtmlTagRegex = new("<[^>]+>", RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>Регулярное выражение числового токена версии («3.0.167.18», «8.3.27.2214») —
    /// поиск версий в тексте ячейки «Список версий» (issue #352).</summary>
    private static readonly Regex VersionTokenRegex = new(
        @"\b(?<v>\d{1,4}(\.\d{1,4}){1,3})\b", RegexOptions.Compiled);

    /// <summary>Регулярное выражение ячейки <c><td>…</td></c> строки таблицы
    /// (для пропуска первой ячейки с версией при извлечении «Списка версий», issue #352).</summary>
    private static readonly Regex TableCellRegex = new(
        @"<td[^>]*>(?<cell>.*?)</td>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>Регулярное выражение даты «дд.мм.гггг»: токены вида «01.10.2026» из соседних
    /// колонок (например, «Дата выхода») не считаются версиями «Списка версий» (issue #352).</summary>
    private static readonly Regex DateLikeTokenRegex = new(
        @"^\d{1,2}\.\d{1,2}\.(?:19|20)\d{2}$", RegexOptions.Compiled);

    /// <summary>
    /// Возвращает все версии со страницы каталога <c>releases.1c.ru/project/Platform83</c>:
    /// перебирает все строки таблицы <c>id="versionsTable"</c> и извлекает из каждой первый
    /// элемент <c><a href="/version_files?nick=…&ver=…">ВЕРСИЯ</a></c>. Если
    /// таблица не найдена — ищет все ссылки <c>version_files?...&ver=</c> по всему HTML
    /// (запасной путь). Версии нормализуются (HTML-сущности, пробелы/переносы), дубликаты
    /// удаляются, результат сортируется по убыванию числовыми сегментами.
    /// Пустой/битый HTML возвращает пустой список без исключений.
    /// </summary>
    public static IReadOnlyList<PlatformRelease> ParseVersions(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return Array.Empty<PlatformRelease>();

        // Тройка «версия → ссылка на страницу файлов → список версий для обновления»
        // (колонка «Список версий», issue #352); сохраняем порядок появления в HTML.
        var entries = new List<(string Version, string Href, List<string> Sources)>();

        // 1) Основной путь: таблица #versionsTable, все строки <tr>, первый version_files-линк.
        var tableMatch = VersionsTableRegex.Match(html);
        if (tableMatch.Success)
        {
            var table = tableMatch.Groups["table"].Value;
            foreach (Match rowMatch in TableRowRegex.Matches(table))
            {
                var row = rowMatch.Groups["row"].Value;
                var link = VersionFilesLinkRegex.Match(row);
                if (!link.Success)
                    continue;

                var version = NormalizeVersion(link.Groups["ver"].Value);
                if (!IsValidVersionText(version))
                    continue;

                // Колонка «Список версий»: остальной текст строки (версии, из которых
                // можно обновиться напрямую до этой версии).
                var sources = ExtractSources(row, version);
                entries.Add((version, link.Groups["href"].Value.Trim(), sources));
            }
        }

        // 2) Запасной путь: регресс к поиску всех version_files-ссылок во всём HTML.
        if (entries.Count == 0)
        {
            foreach (Match link in VersionFilesLinkRegex.Matches(html))
            {
                var version = NormalizeVersion(link.Groups["ver"].Value);
                if (IsValidVersionText(version))
                    entries.Add((version, link.Groups["href"].Value.Trim(), new List<string>()));
            }
        }

        // Дедупликация по версии: сохраняем первую встреченную ссылку и список версий.
        var byVersion = new Dictionary<string, (string Href, List<string> Sources)>(StringComparer.Ordinal);
        foreach (var (version, href, sources) in entries)
        {
            if (!byVersion.ContainsKey(version))
                byVersion[version] = (href, sources);
        }

        var releases = byVersion
            .Select(kv => new PlatformRelease
            {
                Version = kv.Key,
                VersionFilesUrl = kv.Value.Href,
                Sources = kv.Value.Sources,
            })
            .ToList();
        releases.Sort((x, y) => CompareVersions(y.Version, x.Version));
        return releases;
    }

    /// <summary>
    /// Извлекает «Список версий» из строки таблицы #versionsTable (issue #352): перебирает
    /// ячейки строки ПОСЛЕ первой (первая содержит ссылку version_files с самой версией),
    /// снимает HTML-разметку и ищет числовые токены версий вида «3.0.167.18». Даты
    /// («01.10.2026») из соседних колонок не считаются версиями. Устойчиво к форматам
    /// ячейки: список через запятую, диапазон через «—», произвольный текст вокруг.
    /// Пустая/отсутствующая колонка даёт пустой список без исключений.
    /// </summary>
    private static List<string> ExtractSources(string row, string ownVersion)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(row))
            return result;

        var cells = TableCellRegex.Matches(row);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 1; i < cells.Count; i++)
        {
            var cellText = HtmlTagRegex.Replace(cells[i].Groups["cell"].Value, " ");
            foreach (Match match in VersionTokenRegex.Matches(cellText))
            {
                var candidate = NormalizeVersion(match.Groups["v"].Value);
                if (!IsValidVersionText(candidate))
                    continue;
                // Даты («01.10.2026») версиями не являются.
                if (DateLikeTokenRegex.IsMatch(candidate))
                    continue;
                if (!string.IsNullOrWhiteSpace(ownVersion) &&
                    string.Equals(candidate, ownVersion, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!seen.Add(candidate))
                    continue;
                result.Add(candidate);
            }
        }

        return result;
    }

    /// <summary>
    /// Возвращает файлы дистрибутива релиза из ответа <c>version_files?nick=…&ver=…</c>
    /// (JSON или HTML): ищет ссылки на <c>.zip</c>/<c>.deb</c>/<c>.rpm</c>/<c>.tar.gz</c>,
    /// классифицирует тип по расширению, разрядность — по токенам имени файла, размер —
    /// из JSON-полей <c>size</c>/<c>filesize</c> (иначе 0). Ссылки с query-частью учитываются.
    /// Неизвестные расширения пропускаются; битый ответ возвращает пустой список.
    /// </summary>
    public static IReadOnlyList<PlatformReleaseFile> ParseDistributionFiles(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return Array.Empty<PlatformReleaseFile>();

        var result = new List<PlatformReleaseFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in DistributionFileLinkRegex.Matches(body))
        {
            var url = match.Groups["url"].Value.Trim().Trim('"', '\'', '\\');
            if (string.IsNullOrWhiteSpace(url))
                continue;
            if (!seen.Add(url))
                continue;

            var fileName = ExtractFileNameFromUrl(url);
            if (string.IsNullOrWhiteSpace(fileName))
                continue;

            result.Add(new PlatformReleaseFile
            {
                FileName = fileName,
                Url = url,
                SizeBytes = FindSizeNear(body, fileName),
                Architecture = DetectArchitecture(fileName),
                Kind = ClassifyKind(fileName),
            });
        }

        return result;
    }

    /// <summary>
    /// Численное сравнение версий («8.3.27.2214» vs «8.3.27.1688»): >0, если
    /// <paramref name="a"/> новее <paramref name="b"/>. Переиспользует
    /// <see cref="OneCUpdatesService.TryParseVersion"/>; если хотя бы одна сторона не
    /// разбирается как версия — сравнивает числовые сегменты (паттерн
    /// <c>OneCPlatformLocator.CompareVersions</c>).
    /// </summary>
    public static int CompareVersions(string a, string b)
    {
        if (OneCUpdatesService.TryParseVersion(a, out var parsedA) &&
            OneCUpdatesService.TryParseVersion(b, out var parsedB))
            return parsedA.CompareTo(parsedB);

        return CompareBySegments(a, b);
    }

    /// <summary>Нормализует текст версии из ссылки: HTML-сущности, затем удаление всех
    /// пробельных символов (переносы разметки), trim.</summary>
    private static string NormalizeVersion(string raw)
    {
        var decoded = WebUtility.HtmlDecode(raw ?? string.Empty);
        if (string.IsNullOrEmpty(decoded))
            return string.Empty;

        var sb = new StringBuilder(decoded.Length);
        foreach (var c in decoded)
        {
            if (!char.IsWhiteSpace(c))
                sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>True, если строка похожа на номер версии («8.3.27.2214»): непустая,
    /// содержит точку и хотя бы одну цифру.</summary>
    private static bool IsValidVersionText(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return false;
        return version.IndexOf('.') >= 0 && version.Any(char.IsDigit);
    }

    /// <summary>Извлекает имя файла из ссылки: последний сегмент пути до <c>?</c>/<c>#</c>,
    /// с декодированием процент-экранирования и HTML-сущностей.</summary>
    private static string ExtractFileNameFromUrl(string url)
    {
        var query = url.IndexOfAny(new[] { '?', '#' });
        var path = query >= 0 ? url.Substring(0, query) : url;
        var slash = path.LastIndexOf('/');
        var name = slash >= 0 ? path.Substring(slash + 1) : path;

        try
        {
            name = Uri.UnescapeDataString(name);
        }
        catch
        {
            // Оставляем имя как есть при некорректном экранировании.
        }

        return WebUtility.HtmlDecode(name);
    }

    /// <summary>Тип дистрибутива по расширению имени файла.</summary>
    private static PlatformDistributionKind ClassifyKind(string fileName)
    {
        var lower = fileName.ToLowerInvariant();
        if (lower.EndsWith(".zip"))
            return PlatformDistributionKind.WindowsSetupZip;
        if (lower.EndsWith(".deb"))
            return PlatformDistributionKind.LinuxDeb;
        if (lower.EndsWith(".rpm"))
            return PlatformDistributionKind.LinuxRpm;
        if (lower.EndsWith(".tar.gz"))
            return PlatformDistributionKind.LinuxTarGz;
        return PlatformDistributionKind.Other;
    }

    /// <summary>Разрядность дистрибутива по токенам имени файла
    /// (<c>x64</c>/<c>x86_64</c>/<c>amd64</c>/<c>64</c> → «x64»;
    /// <c>x86</c>/<c>32</c>/<c>i386</c> → «x86») или null, если не определена.</summary>
    private static string? DetectArchitecture(string fileName)
    {
        var lower = fileName.ToLowerInvariant();
        if (lower.Contains("x86_64") || lower.Contains("amd64") || lower.Contains("x64"))
            return "x64";
        if (lower.Contains("i386") || lower.Contains("i686") || lower.Contains("x86"))
            return "x86";
        if (lower.Contains("64"))
            return "x64";
        if (lower.Contains("32"))
            return "x86";
        return null;
    }

    /// <summary>Ищет размер файла в JSON-полях <c>size</c>/<c>filesize</c> в окрестности
    /// первого упоминания имени файла в теле ответа. Устойчиво к неизвестной схеме JSON.
    /// Возвращает 0, если размер не найден.</summary>
    private static long FindSizeNear(string body, string fileName)
    {
        if (string.IsNullOrWhiteSpace(body) || string.IsNullOrWhiteSpace(fileName))
            return 0;

        foreach (Match occurrence in Regex.Matches(body, Regex.Escape(fileName), RegexOptions.IgnoreCase))
        {
            // Ищем размер только ПОСЛЕ вхождения имени файла и до конца JSON-объекта (ближайший «}»),
            // чтобы не захватить size соседнего файла в списке.
            var start = occurrence.Index + occurrence.Length;
            var end = Math.Min(body.Length, start + 500);
            var window = body.Substring(start, end - start);
            var closeBrace = window.IndexOf('}');
            if (closeBrace >= 0)
                window = window.Substring(0, closeBrace);

            var sizeMatch = SizeFieldRegex.Match(window);
            if (sizeMatch.Success && long.TryParse(sizeMatch.Groups["bytes"].Value, out var bytes))
                return bytes;
        }

        return 0;
    }

    /// <summary>Численное сравнение по сегментам (паттерн
    /// <c>OneCPlatformLocator.CompareVersions</c>): нечисловые сегменты игнорируются,
    /// недостающие части считаются нулями.</summary>
    private static int CompareBySegments(string a, string b)
    {
        static int[] Parts(string v)
        {
            return (v ?? string.Empty)
                .Split(new[] { '.', ' ', '(' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(p => int.TryParse(p, out _))
                .Select(int.Parse)
                .ToArray();
        }

        var pa = Parts(a);
        var pb = Parts(b);
        var len = Math.Max(pa.Length, pb.Length);
        for (var i = 0; i < len; i++)
        {
            var va = i < pa.Length ? pa[i] : 0;
            var vb = i < pb.Length ? pb[i] : 0;
            if (va != vb)
                return va.CompareTo(vb);
        }

        return 0;
    }
}