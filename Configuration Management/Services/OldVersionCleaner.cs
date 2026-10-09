using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Configuration_Management.Models;

namespace Configuration_Management.Services;

/// <summary>
/// Чистый отбор кандидатов на удаление старых версий технологической платформы 1С
/// (этап 0.3.9.215, функция «Автообновление платформы»). Класс без платформенных
/// зависимостей (обе платформы) — только правила отбора, без файловой системы,
/// сети и UI; покрывается юнит-тестами.
/// </summary>
public static class OldVersionCleaner
{
    /// <summary>
    /// Отбирает версии, которые можно предложить к удалению:
    /// <list type="bullet">
    /// <item>исключается новейшая установленная версия (численное сравнение чистой
    /// версии из <see cref="PlatformVersionInfo.Display"/> через
    /// <see cref="OneCPlatformCatalogParser.CompareVersions"/>; обе разрядности
    /// новейшего номера — «8.3.27.2214 (64)» и «8.3.27.2214 (32)» — исключаются);</item>
    /// <item>исключаются версии, на которые ссылаются базы репозитория
    /// (<see cref="Infobase.PlatformVersion"/> начинается с префикса версии —
    /// численное совпадение либо сегментный префикс, регистронезависимо;
    /// пустая версия у базы не считается);</item>
    /// <item>исключаются версии запущенных процессов: путь процесса
    /// (<paramref name="runningBinPaths"/>) сопоставляется с каталогом версии
    /// (<see cref="PlatformVersionInfo.Path"/>) префиксом каталога — процесс
    /// запущен из bin этой версии.</item>
    /// </list>
    /// Результат отсортирован по убыванию версии. Пустой вход — пустой список
    /// без исключений.
    /// </summary>
    /// <param name="installed">Установленные версии платформы (Display + путь каталога).</param>
    /// <param name="bases">Базы репозитория (совместимость по <see cref="Infobase.PlatformVersion"/>).</param>
    /// <param name="runningBinPaths">Пути к исполняемым файлам запущенных процессов 1С
    /// (например «C:\Program Files\1cv8\8.3.27.1688\bin\1cv8c.exe»).</param>
    /// <returns>Кандидаты на удаление, отсортированные по убыванию версии.</returns>
    public static List<PlatformVersionInfo> SelectCandidates(
        IReadOnlyList<PlatformVersionInfo> installed,
        IReadOnlyList<Infobase> bases,
        IReadOnlyList<string> runningBinPaths)
    {
        var versions = (installed ?? Array.Empty<PlatformVersionInfo>())
            .Where(v => v is not null && !string.IsNullOrWhiteSpace(v.Display))
            .ToList();
        if (versions.Count == 0)
            return new List<PlatformVersionInfo>();

        // Новейшая установленная версия: максимальный числовой номер Display.
        var newestDisplay = versions[0].Display;
        foreach (var version in versions.Skip(1))
        {
            if (OneCPlatformCatalogParser.CompareVersions(version.Display, newestDisplay) > 0)
                newestDisplay = version.Display;
        }

        var result = new List<PlatformVersionInfo>(versions.Count);
        foreach (var version in versions)
        {
            if (OneCPlatformCatalogParser.CompareVersions(version.Display, newestDisplay) == 0)
                continue; // новейшая установленная (любая разрядность этого номера)

            if (IsReferencedByBase(version.Display, bases))
                continue; // на версию ссылается хотя бы одна база

            if (IsRunningFromDirectory(version.Path, runningBinPaths))
                continue; // из этой версии запущен процесс 1С

            result.Add(version);
        }

        result.Sort((a, b) => OneCPlatformCatalogParser.CompareVersions(b.Display, a.Display));
        return result;
    }

    /// <summary>
    /// Строит ПОЛНЫЙ список установленных версий с признаками риска для диалога
    /// удаления (issue #334, требование автора issue: пользователь сам решает, что
    /// считать старым). В отличие от <see cref="SelectCandidates"/>, НИЧЕГО не
    /// отфильтровывает: новейшая, используемые базами и запущенные версии остаются
    /// в списке — только с выставленными признаками
    /// (<see cref="OldVersionCleanupEntry.IsNewest"/>,
    /// <see cref="OldVersionCleanupEntry.IsUsedByBases"/>,
    /// <see cref="OldVersionCleanupEntry.IsUsedByProcesses"/>). Результат отсортирован
    /// по убыванию версии. Пустой вход — пустой список.
    /// </summary>
    /// <param name="installed">Установленные версии платформы (Display + путь каталога).</param>
    /// <param name="bases">Базы репозитория (совместимость по <see cref="Infobase.PlatformVersion"/>).</param>
    /// <param name="runningBinPaths">Пути к исполняемым файлам запущенных процессов 1С.</param>
    public static List<OldVersionCleanupEntry> SelectDeletionEntries(
        IReadOnlyList<PlatformVersionInfo> installed,
        IReadOnlyList<Infobase> bases,
        IReadOnlyList<string> runningBinPaths)
    {
        var versions = (installed ?? Array.Empty<PlatformVersionInfo>())
            .Where(v => v is not null && !string.IsNullOrWhiteSpace(v.Display))
            .ToList();

        // Новейшая установленная версия: максимальный числовой номер Display.
        var newestDisplay = string.Empty;
        foreach (var version in versions)
        {
            if (newestDisplay.Length == 0
                || OneCPlatformCatalogParser.CompareVersions(version.Display, newestDisplay) > 0)
            {
                newestDisplay = version.Display;
            }
        }

        var result = new List<OldVersionCleanupEntry>(versions.Count);
        foreach (var version in versions)
        {
            var baseNames = GetReferencingBaseNames(version.Display, bases);
            var processPaths = GetRunningProcessPaths(version.Path, runningBinPaths);
            var isNewest = newestDisplay.Length > 0
                && OneCPlatformCatalogParser.CompareVersions(version.Display, newestDisplay) == 0;
            result.Add(new OldVersionCleanupEntry(version, isNewest, baseNames, processPaths));
        }

        result.Sort((a, b) => OneCPlatformCatalogParser.CompareVersions(b.Version.Display, a.Version.Display));
        return result;
    }

    /// <summary>
    /// Строит предупреждения для подтверждения удаления выбранных пользователем версий
    /// (issue #334): для каждой отмеченной версии с признаками риска возвращается
    /// предупреждение с видом риска и деталями (имена баз, пути процессов). Версии без
    /// признаков риска предупреждений не порождают. Чистая функция — локализованный
    /// текст строит ViewModel.
    /// </summary>
    /// <param name="entries">Полный список версий с признаками
    /// (<see cref="SelectDeletionEntries"/>).</param>
    /// <param name="selected">Версии, отмеченные пользователем к удалению.</param>
    public static List<OldVersionCleanupWarning> BuildDeletionConfirmations(
        IReadOnlyList<OldVersionCleanupEntry> entries,
        IReadOnlyList<PlatformVersionInfo> selected)
    {
        var result = new List<OldVersionCleanupWarning>();
        if (entries is null || selected is null || selected.Count == 0)
            return result;

        var selectedDisplays = new HashSet<string>(
            selected.Where(v => v is not null).Select(v => v.Display),
            StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            if (!selectedDisplays.Contains(entry.Version.Display))
                continue;

            if (entry.IsNewest)
                result.Add(new OldVersionCleanupWarning(
                    OldVersionCleanupRiskKind.Newest, entry.Version.Display, Array.Empty<string>()));

            if (entry.IsUsedByBases)
                result.Add(new OldVersionCleanupWarning(
                    OldVersionCleanupRiskKind.UsedByBases, entry.Version.Display, entry.ReferencingBaseNames));

            if (entry.IsUsedByProcesses)
                result.Add(new OldVersionCleanupWarning(
                    OldVersionCleanupRiskKind.UsedByProcesses, entry.Version.Display, entry.RunningProcessPaths));
        }

        return result;
    }

    /// <summary>
    /// Чистая версия из Display: суффикс разрядности «(64)»/«(32)» отбрасывается
    /// («8.3.27.2214 (64)» → «8.3.27.2214»). Строка без суффикса возвращается как есть.
    /// </summary>
    public static string CleanVersion(string? display)
    {
        var value = (display ?? string.Empty).Trim();
        var end = value.LastIndexOf(')');
        var start = value.LastIndexOf('(');
        if (end > start && start >= 0)
        {
            var arch = value.Substring(start + 1, end - start - 1).Trim();
            if (arch is "64" or "32")
                return value.Substring(0, start).Trim();
        }

        return value;
    }

    /// <summary>
    /// Извлекает путь исполняемого файла из командной строки процесса (первый токен
    /// с учётом обрамляющих кавычек): «"C:\…\1cv8c.exe" /F …» → «C:\…\1cv8c.exe»,
    /// «/opt/1cv8/8.3.27.2214/bin/1cv8c /F …» → «/opt/1cv8/8.3.27.2214/bin/1cv8c».
    /// null — командная строка пуста или кавычка не закрыта.
    /// </summary>
    public static string? ExtractExecutablePath(string? commandLine)
    {
        var line = (commandLine ?? string.Empty).TrimStart();
        if (line.Length == 0)
            return null;

        if (line[0] == '"')
        {
            var end = line.IndexOf('"', 1);
            return end < 0 ? null : line.Substring(1, end - 1).Trim();
        }

        var space = line.IndexOf(' ');
        var tab = line.IndexOf('\t');
        var cut = space < 0 ? tab : (tab < 0 ? space : Math.Min(space, tab));
        return cut < 0 ? line.Trim() : line.Substring(0, cut).Trim();
    }

    /// <summary>Ссылается ли хотя бы одна база на указанную версию платформы.
    /// Обёртка над <see cref="GetReferencingBaseNames"/>.</summary>
    private static bool IsReferencedByBase(string versionDisplay, IReadOnlyList<Infobase>? bases)
        => GetReferencingBaseNames(versionDisplay, bases).Count > 0;

    /// <summary>Список имён баз репозитория, ссылающихся на указанную версию платформы:
    /// численное равенство (учитывает суффикс разрядности базы) либо база задана
    /// частичной версией-префиксом, охватывающей кандидата («8.3.26» → «8.3.26.1890»).
    /// Используется диалогом удаления старых версий (issue #334) для показа пометок
    /// «используется базами» и предупреждений при подтверждении удаления.</summary>
    public static IReadOnlyList<string> GetReferencingBaseNames(string versionDisplay, IReadOnlyList<Infobase>? bases)
    {
        var names = new List<string>();
        var candidateVersion = CleanVersion(versionDisplay);
        if (candidateVersion.Length == 0)
            return names;

        foreach (var infobase in bases ?? Array.Empty<Infobase>())
        {
            var baseVersion = (infobase?.PlatformVersion ?? string.Empty).Trim();
            if (baseVersion.Length == 0)
                continue;

            // Численное совпадение: «8.3.27.1688» и «8.3.27.1688 (64)» — одна версия.
            if (OneCPlatformCatalogParser.CompareVersions(baseVersion, candidateVersion) == 0
                || MatchesVersionPrefix(candidateVersion, baseVersion))
            {
                names.Add(string.IsNullOrWhiteSpace(infobase?.Name)
                    ? (infobase?.Id ?? string.Empty)
                    : infobase.Name);
            }
        }

        return names;
    }

    /// <summary>Запущен ли процесс из каталога версии. Обёртка над
    /// <see cref="GetRunningProcessPaths"/>.</summary>
    private static bool IsRunningFromDirectory(string? versionDirectory, IReadOnlyList<string>? runningBinPaths)
        => GetRunningProcessPaths(versionDirectory, runningBinPaths).Count > 0;

    /// <summary>Пути исполняемых файлов процессов 1С, запущенных из каталога указанной
    /// версии: путь процесса начинается с каталога версии (регистронезависимо, с учётом
    /// границы пути). Используется диалогом удаления старых версий (issue #334) для
    /// показа пометки «используется запущенными процессами» и предупреждений.</summary>
    public static IReadOnlyList<string> GetRunningProcessPaths(string? versionDirectory, IReadOnlyList<string>? runningBinPaths)
    {
        var paths = new List<string>();
        var dir = (versionDirectory ?? string.Empty).Trim().TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (dir.Length == 0)
            return paths;

        foreach (var binPath in runningBinPaths ?? Array.Empty<string>())
        {
            var processPath = (binPath ?? string.Empty).Trim();
            if (processPath.Length == 0)
                continue;

            if (processPath.Equals(dir, StringComparison.OrdinalIgnoreCase))
            {
                paths.Add(processPath);
                continue;
            }

            // Граница пути: следующий символ — разделитель каталогов, иначе
            // «8.3.27.1688» поймал бы и «8.3.27.16882» (ложное срабатывание).
            if (StartsWithBoundary(processPath, dir + Path.DirectorySeparatorChar)
                || (Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar
                    && StartsWithBoundary(processPath, dir + Path.AltDirectorySeparatorChar)))
            {
                paths.Add(processPath);
            }
        }

        return paths;
    }

    private static bool StartsWithBoundary(string processPath, string prefix)
        => processPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Начинается ли фактическая версия с указанного префикса по сегментам
    /// («8.3.27.1688» соответствует «8.3.27», но не «8.5» или «8.3.2»). Паттерн
    /// приватного хелпера <c>PlatformUpdateMatcher.MatchesVersionPrefix</c>.</summary>
    private static bool MatchesVersionPrefix(string version, string prefix)
    {
        var vParts = (version ?? string.Empty).Split('.');
        var pParts = (prefix ?? string.Empty).Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (vParts.Length < pParts.Length)
            return false;

        for (var i = 0; i < pParts.Length; i++)
        {
            if (!string.Equals(vParts[i].Trim(), pParts[i].Trim(), StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }
}

/// <summary>Вид риска при удалении установленной версии платформы 1С (issue #334):
/// используется для предупреждений подтверждения в диалоге удаления старых версий.</summary>
public enum OldVersionCleanupRiskKind
{
    /// <summary>Новейшая установленная версия (любая разрядность).</summary>
    Newest,

    /// <summary>На версию ссылается хотя бы одна база репозитория.</summary>
    UsedByBases,

    /// <summary>Из каталога версии запущен процесс 1С.</summary>
    UsedByProcesses,
}

/// <summary>
/// Строка диалога удаления старых версий платформы 1С (issue #334): версия и признаки
/// риска («новейшая», «используется базами», «используется запущенными процессами»).
/// В отличие от <see cref="OldVersionCleaner.SelectCandidates"/>, ВСЕ установленные
/// версии включаются в список — решение, что считать старым, принимает пользователь;
/// по умолчанию отмечаются только версии без признаков риска.
/// </summary>
public sealed class OldVersionCleanupEntry
{
    /// <param name="version">Информация об установленной версии.</param>
    /// <param name="isNewest">True — новейшая установленная версия (любая разрядность).</param>
    /// <param name="referencingBaseNames">Имена баз, ссылающихся на версию.</param>
    /// <param name="runningProcessPaths">Пути процессов, запущенных из каталога версии.</param>
    public OldVersionCleanupEntry(
        PlatformVersionInfo version,
        bool isNewest,
        IReadOnlyList<string> referencingBaseNames,
        IReadOnlyList<string> runningProcessPaths)
    {
        Version = version ?? throw new ArgumentNullException(nameof(version));
        IsNewest = isNewest;
        ReferencingBaseNames = referencingBaseNames ?? Array.Empty<string>();
        RunningProcessPaths = runningProcessPaths ?? Array.Empty<string>();
        IsUsedByBases = ReferencingBaseNames.Count > 0;
        IsUsedByProcesses = RunningProcessPaths.Count > 0;
    }

    /// <summary>Информация об установленной версии (Display + путь каталога).</summary>
    public PlatformVersionInfo Version { get; }

    /// <summary>True — новейшая установленная версия (любая разрядность этого номера).</summary>
    public bool IsNewest { get; }

    /// <summary>True — на версию ссылается хотя бы одна база репозитория.</summary>
    public bool IsUsedByBases { get; }

    /// <summary>True — из каталога версии запущен процесс 1С.</summary>
    public bool IsUsedByProcesses { get; }

    /// <summary>Имена баз, ссылающихся на версию (пусто — не используется базами).</summary>
    public IReadOnlyList<string> ReferencingBaseNames { get; }

    /// <summary>Пути процессов, запущенных из каталога версии (пусто — не используется).</summary>
    public IReadOnlyList<string> RunningProcessPaths { get; }

    /// <summary>True — есть хотя бы один признак риска (новейшая/используется).</summary>
    public bool HasRiskMarkers => IsNewest || IsUsedByBases || IsUsedByProcesses;

    /// <summary>Состояние флажка по умолчанию: снят у версий с признаками риска,
    /// установлен у «просто старых» версий (защита от удаления нужного).</summary>
    public bool IsCheckedByDefault => !HasRiskMarkers;
}

/// <summary>Предупреждение при подтверждении удаления спорной версии (issue #334):
/// вид риска, версия и детали (имена баз / пути процессов).</summary>
public sealed class OldVersionCleanupWarning
{
    /// <param name="kind">Вид риска.</param>
    /// <param name="versionDisplay">Отображаемая строка версии.</param>
    /// <param name="details">Детали: имена баз или пути процессов (может быть пусто).</param>
    public OldVersionCleanupWarning(
        OldVersionCleanupRiskKind kind, string versionDisplay, IReadOnlyList<string> details)
    {
        Kind = kind;
        VersionDisplay = versionDisplay ?? string.Empty;
        Details = details ?? Array.Empty<string>();
    }

    /// <summary>Вид риска.</summary>
    public OldVersionCleanupRiskKind Kind { get; }

    /// <summary>Отображаемая строка версии («8.3.27.2214 (64)»).</summary>
    public string VersionDisplay { get; }

    /// <summary>Детали: имена баз или пути процессов (пусто — деталей нет).</summary>
    public IReadOnlyList<string> Details { get; }
}