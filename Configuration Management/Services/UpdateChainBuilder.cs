using System;
using System.Collections.Generic;
using System.Linq;
using Configuration_Management.Models;

namespace Configuration_Management.Services;

/// <summary>
/// Чистый построитель цепочек обновлений конфигурации (issue #352): по каталогу версий
/// с колонкой «Список версий» (<see cref="PlatformRelease.Sources"/>) определяет, можно ли
/// обновить текущую версию напрямую до последней, и строит варианты цепочки:
///   1) «снизу вверх» — каждый шаг максимальная версия, на которую можно прыгнуть с текущей
///      (жадный поиск, идёт до нужной версии);
///   2) «оптимальный» — минимальное число прыжков (поиск в ширину по графу совместимости),
///      показывается, если построен и отличается от п.1.
/// Без сети и UI — покрывается тестами.
/// </summary>
public static class UpdateChainBuilder
{
    /// <summary>
    /// Строит цепочки обновления от <paramref name="currentVersion"/> до
    /// <paramref name="targetVersion"/> по каталогу <paramref name="releases"/>.
    /// Пустая/непарсимая текущая версия или пустой каталог — пустой результат
    /// (без исключений).
    /// </summary>
    public static UpdateChainSet Build(
        string currentVersion,
        string targetVersion,
        IReadOnlyList<PlatformRelease> releases)
    {
        if (string.IsNullOrWhiteSpace(currentVersion) || string.IsNullOrWhiteSpace(targetVersion))
            return new UpdateChainSet();

        var catalog = (releases ?? Array.Empty<PlatformRelease>())
            .Where(r => !string.IsNullOrWhiteSpace(r.Version))
            .ToList();
        if (catalog.Count == 0)
            return new UpdateChainSet();

        // Нет данных о совместимости версий — цепочка не строится (деградация к прежнему
        // поведению «скачать только последнюю версию»).
        var hasSourceData = catalog.Any(r => r.Sources is { Count: > 0 });
        if (!hasSourceData)
            return new UpdateChainSet { HasSourceData = false };

        // Текущая версия отсутствует в каталоге (issue #352): 1С отзывает релизы —
        // версия не находится ни как релиз, ни в «Списках версий» других релизов.
        var currentMissing = IsCurrentVersionMissing(currentVersion, catalog);

        var target = catalog.FirstOrDefault(r =>
            SameVersion(r.Version, targetVersion));
        if (target is null)
            return new UpdateChainSet { HasSourceData = true, IsCurrentVersionMissing = currentMissing };

        if (CanJump(currentVersion, target))
            return new UpdateChainSet { HasSourceData = true, IsDirectUpdate = true, IsCurrentVersionMissing = currentMissing };

        var bottomUp = BuildBottomUp(currentVersion, targetVersion, catalog);
        var optimal = BuildOptimal(currentVersion, targetVersion, catalog);

        var variants = new List<UpdateChainVariant>();
        if (bottomUp is not null)
        {
            variants.Add(new UpdateChainVariant
            {
                Number = variants.Count + 1,
                Kind = UpdateChainKind.BottomUp,
                Steps = bottomUp,
            });
        }

        if (optimal is not null && !SameChain(bottomUp, optimal))
        {
            variants.Add(new UpdateChainVariant
            {
                Number = variants.Count + 1,
                Kind = UpdateChainKind.Optimal,
                Steps = optimal,
            });
        }

        return new UpdateChainSet
        {
            HasSourceData = true,
            IsDirectUpdate = false,
            IsCurrentVersionMissing = currentMissing,
            Variants = variants,
        };
    }

    /// <summary>
    /// Целевая версия с учётом ограничения «Не повышать» (issue #352.4): максимум среди
    /// релизов каталога с теми же первыми двумя числами версии (major.minor), что у
    /// текущей. Пример: текущая 3.1.2.345 → может быть выбрана 3.1.3.456, но НЕ 3.2.3.456.
    /// Null — текущая версия пустая/непарсимая (галочка не влияет) либо в каталоге нет
    /// ни одной версии той же линии (цепочка не строится). Internal — для юнит-тестов.
    /// </summary>
    internal static string? SelectCappedTarget(
        string currentVersion, IReadOnlyList<PlatformRelease> releases)
    {
        if (string.IsNullOrWhiteSpace(currentVersion))
            return null;

        var parts = currentVersion.Trim().Split('.');
        if (parts.Length < 2
            || !int.TryParse(parts[0], out var major)
            || !int.TryParse(parts[1], out var minor))
            return null;

        string? best = null;
        foreach (var release in releases ?? Array.Empty<PlatformRelease>())
        {
            var version = release?.Version;
            if (string.IsNullOrWhiteSpace(version))
                continue;

            var versionParts = version.Trim().Split('.');
            if (versionParts.Length < 2
                || !int.TryParse(versionParts[0], out var versionMajor)
                || !int.TryParse(versionParts[1], out var versionMinor)
                || versionMajor != major || versionMinor != minor)
                continue;

            if (best is null || OneCPlatformCatalogParser.CompareVersions(version, best) > 0)
                best = version.Trim();
        }

        return best;
    }

    /// <summary>
    /// Адрес для кнопки «Скачать» одиночного обновления (issue #352.1, регрессия «Не
    /// повышать»): при включённой галочке «Не повышать» и вычислимом кап-таргете
    /// ВСЕГДА строится прямая ссылка <c>version_files?nick=&ver=<кап></c> — даже
    /// когда кап совпадает с отображаемой «Последней версией» (ранее при равенстве
    /// возвращался сырой URL каталога, и сервис резолвил глобальную последнюю версию,
    /// предлагая файлы выше текущей). Дополнительно (рекомендация плана): при известной
    /// целевой версии <paramref name="latestVersion"/> и адресе без <c>ver</c> строится
    /// <c>version_files</c> по этой версии — без лишнего запроса каталога.
    /// Чистый статический метод без UI — покрыт юнит-тестами.
    /// </summary>
    /// <param name="currentVersion">Текущая версия конфигурации (может быть пустой/непарсимой).</param>
    /// <param name="latestVersion">Отображаемая целевая («Последняя») версия, известная окну.</param>
    /// <param name="noVersionBump">Значение галочки «Не повышать».</param>
    /// <param name="releases">Кэш каталога версий (может быть пустым).</param>
    /// <param name="url">Исходный адрес строки (каталог проекта / релиз / файл).</param>
    public static string ResolveSingleDownloadUrl(
        string? currentVersion,
        string? latestVersion,
        bool noVersionBump,
        IReadOnlyList<PlatformRelease>? releases,
        string? url)
    {
        var sourceUrl = url ?? string.Empty;
        if (string.IsNullOrWhiteSpace(sourceUrl))
            return sourceUrl;

        var nick = OneCUpdatesService.ExtractNickAndVersion(sourceUrl).Nick;
        if (string.IsNullOrWhiteSpace(nick))
            nick = OneCUpdatesService.ExtractNickFromProjectUrl(sourceUrl);
        if (string.IsNullOrWhiteSpace(nick))
            return sourceUrl;

        string? target = null;
        if (noVersionBump
            && !string.IsNullOrWhiteSpace(currentVersion)
            && releases is { Count: > 0 })
        {
            // «Не повышать»: кап-версия линии major.minor, БЕЗ сравнения с отображаемой
            // «Последней версией» (issue #352.1: после ApplyNoVersionBump LatestVersion
            // равна капу, и старая проверка равенства откатывалась к сырому URL).
            target = SelectCappedTarget(currentVersion, releases);
        }

        if (string.IsNullOrWhiteSpace(target)
            && string.IsNullOrWhiteSpace(OneCUpdatesService.ExtractNickAndVersion(sourceUrl).Ver)
            && !string.IsNullOrWhiteSpace(latestVersion))
        {
            // Рекомендация плана: известная целевая версия + адрес без ver — строим
            // version_files напрямую (каталог не запрашивается).
            target = latestVersion.Trim();
        }

        if (string.IsNullOrWhiteSpace(target))
            return sourceUrl;

        return OneCUpdatesService.ToAbsoluteVersionFilesUrl(
            $"/version_files?nick={Uri.EscapeDataString(nick)}&ver={Uri.EscapeDataString(target)}", sourceUrl);
    }

    /// <summary>
    /// True — версию <paramref name="currentVersion"/> нет в каталоге: она не совпадает
    /// ни с одной версией релизов и не упомянута в «Списках версий» ни одного релиза
    /// (issue #352: признак отозванного релиза). Пустая текущая версия — не «отсутствует».
    /// Internal — для юнит-тестов.
    /// </summary>
    internal static bool IsCurrentVersionMissing(string currentVersion, IReadOnlyList<PlatformRelease> catalog)
    {
        if (string.IsNullOrWhiteSpace(currentVersion))
            return false;

        foreach (var release in catalog)
        {
            if (SameVersion(release.Version, currentVersion))
                return false;
            if (release.Sources is { Count: > 0 })
            {
                foreach (var source in release.Sources)
                {
                    if (SameVersion(source, currentVersion))
                        return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// True — версию <paramref name="from"/> можно обновить напрямую до релиза
    /// <paramref name="to"/>: <paramref name="to"/> численно новее и содержит
    /// <paramref name="from"/> в своём «Списке версий» (<see cref="PlatformRelease.Sources"/>).
    /// Сравнение версий — числовыми сегментами, регистронезависимое.
    /// </summary>
    public static bool CanJump(string from, PlatformRelease to)
    {
        if (string.IsNullOrWhiteSpace(from) || to is null || string.IsNullOrWhiteSpace(to.Version))
            return false;

        if (OneCPlatformCatalogParser.CompareVersions(to.Version, from) <= 0)
            return false;

        var fromNorm = Normalize(from);
        return (to.Sources ?? new List<string>()).Any(s =>
            string.Equals(Normalize(s), fromNorm, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Вариант 1 «снизу вверх»: каждый шаг — максимальная версия, на которую можно
    /// прыгнуть с текущей (см. <see cref="CanJump"/>), повтор до достижения последней
    /// версии. Возвращает шаги (от текущей к последней, включая последнюю) или null,
    /// если на каком-то шаге кандидатов нет (тупик).
    /// </summary>
    private static IReadOnlyList<PlatformRelease>? BuildBottomUp(
        string currentVersion,
        string targetVersion,
        IReadOnlyList<PlatformRelease> catalog)
    {
        var steps = new List<PlatformRelease>();
        var current = currentVersion;

        // Защита от бесконечного цикла на ошибочных данных: каждый шаг строго увеличивает
        // версию, поэтому шагов не больше числа релизов каталога.
        for (var guard = 0; guard <= catalog.Count; guard++)
        {
            if (OneCPlatformCatalogParser.CompareVersions(current, targetVersion) >= 0)
                return steps.Count > 0 ? steps : null;

            var next = catalog
                .Where(r => CanJump(current, r))
                .OrderByDescending(r => r.Version, VersionComparer.Instance)
                .FirstOrDefault();
            if (next is null)
                return null;

            steps.Add(next);
            current = next.Version;
        }

        return null;
    }

    /// <summary>
    /// Вариант 2 «оптимальный»: поиск в ширину (BFS) по направленному графу
    /// «версия → более новая версия, в чей «Список версий» входит исходная».
    /// Веса рёбер единичные, поэтому первый найденный путь к последней версии —
    /// кратчайший (минимальное число прыжков). Null — цель недостижима.
    /// </summary>
    private static IReadOnlyList<PlatformRelease>? BuildOptimal(
        string currentVersion,
        string targetVersion,
        IReadOnlyList<PlatformRelease> catalog)
    {
        // Версии строго новее стартовой, по возрастанию — стабильный порядок обхода.
        var newer = catalog
            .Where(r => OneCPlatformCatalogParser.CompareVersions(r.Version, currentVersion) > 0)
            .OrderBy(r => r.Version, VersionComparer.Instance)
            .ToList();

        // prev[версия] — релиз, из которого пришли в эту версию; null — стартовая версия.
        var prev = new Dictionary<string, PlatformRelease?>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Normalize(currentVersion) };
        var queue = new Queue<PlatformRelease>();

        // Первый слой: релизы, достижимые напрямую из текущей версии.
        foreach (var release in newer)
        {
            if (!CanJump(currentVersion, release))
                continue;

            prev[Normalize(release.Version)] = null;
            if (SameVersion(release.Version, targetVersion))
                return new[] { release };

            if (visited.Add(Normalize(release.Version)))
                queue.Enqueue(release);
        }

        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            foreach (var release in newer)
            {
                var key = Normalize(release.Version);
                if (visited.Contains(key))
                    continue;
                if (!CanJump(node.Version, release))
                    continue;

                prev[key] = node;
                if (SameVersion(release.Version, targetVersion))
                    return Reconstruct(prev, release);

                visited.Add(key);
                queue.Enqueue(release);
            }
        }

        return null;
    }

    /// <summary>Восстанавливает путь к цели по карте предшественников (без стартовой версии).</summary>
    private static IReadOnlyList<PlatformRelease> Reconstruct(
        IReadOnlyDictionary<string, PlatformRelease?> prev, PlatformRelease target)
    {
        var path = new List<PlatformRelease> { target };
        var key = Normalize(target.Version);
        while (prev.TryGetValue(key, out var node) && node is not null)
        {
            path.Add(node);
            key = Normalize(node.Version);
        }

        path.Reverse();
        return path;
    }

    /// <summary>True — последовательности версий двух вариантов совпадают (одна строка).</summary>
    private static bool SameChain(IReadOnlyList<PlatformRelease>? a, IReadOnlyList<PlatformRelease>? b)
    {
        if (ReferenceEquals(a, b))
            return true;
        if (a is null || b is null)
            return false;
        if (a.Count != b.Count)
            return false;

        for (var i = 0; i < a.Count; i++)
        {
            if (!SameVersion(a[i].Version, b[i].Version))
                return false;
        }

        return true;
    }

    private static bool SameVersion(string a, string b)
        => string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string version)
        => (version ?? string.Empty).Trim();

    /// <summary>Компаратор версий по убыванию/возрастанию числовыми сегментами
    /// (через <see cref="OneCPlatformCatalogParser.CompareVersions"/>).</summary>
    private sealed class VersionComparer : IComparer<string>
    {
        /// <summary>Единственный экземпляр компаратора.</summary>
        public static readonly VersionComparer Instance = new();

        /// <inheritdoc />
        public int Compare(string? x, string? y)
            => OneCPlatformCatalogParser.CompareVersions(x ?? string.Empty, y ?? string.Empty);
    }
}