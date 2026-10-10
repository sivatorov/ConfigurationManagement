using System;
using System.Collections.Generic;
using System.Linq;
using Configuration_Management.Models;

namespace Configuration_Management.Services;

/// <summary>
/// Тип дистрибутива для скачивания нужной версии платформы 1С (issue #330).
/// «Авто» — рекомендуемый для текущей ОС: на Windows полный клиент (zip), на Linux —
/// пакет .deb/.rpm, при их отсутствии — универсальный .tar.gz.
/// </summary>
public enum PlatformDownloadType
{
    /// <summary>Рекомендуемый дистрибутив для текущей операционной системы.</summary>
    Auto,

    /// <summary>Полный клиент Windows (zip с setup.exe).</summary>
    Client,

    /// <summary>Тонкий клиент Windows (zip с «thin» в имени файла).</summary>
    ThinClient,

    /// <summary>Пакет .deb/.rpm для Linux.</summary>
    Package,

    /// <summary>Универсальный дистрибутив .tar.gz.</summary>
    Archive,
}

/// <summary>
/// Вариант дистрибутива для выбора пользователем (issues #330/#334): конкретный файл
/// с человекочитаемым представлением (тип, разрядность, размер). Используется окном
/// «Скачивание версии платформы 1С» и диалогом выбора варианта в окне обновления.
/// </summary>
public sealed class PlatformDistributionOption
{
    /// <param name="file">Файл дистрибутива релиза.</param>
    /// <param name="isRecommended">True — рекомендуемый вариант для текущей ОС.</param>
    /// <param name="includeFileName">Включать ли имя файла в подпись (issue #330: у файлов
    /// релиза размер неизвестен, имена разные, а подписи типа/разрядности одинаковые —
    /// «все строки одинаковые»). По умолчанию (null) имя включается для Windows-вариантов
    /// (WindowsSetupZip) и не включается для единственных Linux-пакетов.</param>
    public PlatformDistributionOption(PlatformReleaseFile file, bool isRecommended = false, bool? includeFileName = null)
    {
        File = file ?? throw new ArgumentNullException(nameof(file));
        IsRecommended = isRecommended;
        IncludeFileName = includeFileName ?? (File.Kind == PlatformDistributionKind.WindowsSetupZip);
    }

    /// <summary>Файл дистрибутива.</summary>
    public PlatformReleaseFile File { get; }

    /// <summary>True — рекомендуемый вариант для текущей ОС/разрядности.</summary>
    public bool IsRecommended { get; }

    /// <summary>True — имя файла включается в <see cref="DisplayName"/> (issue #330).</summary>
    public bool IncludeFileName { get; }

    /// <summary>Тип дистрибутива по расширению.</summary>
    public PlatformDistributionKind Kind => File.Kind;

    /// <summary>Разрядность («x64»/«x86») или пустая строка.</summary>
    public string Architecture => File.Architecture ?? string.Empty;

    /// <summary>Размер файла, байт (0 — неизвестен).</summary>
    public long SizeBytes => File.SizeBytes;

    /// <summary>
    /// Человекочитаемое представление варианта: «Полный клиент (rar) · x64 ·
    /// setuptc64_8_3_27_2325.rar · 1,2 ГБ» (Windows) или «Пакет deb · amd64 · …» (Linux).
    /// Расширение в скобках — фактическое расширение файла (issue #330: дистрибутивы
    /// платформы отдаются и архивами <c>.rar</c>/<c>.7z</c> — вводить пользователя в
    /// заблуждение подписью «(zip)» нельзя). Имя файла включается, когда вариантов
    /// с одинаковым типом может быть несколько (см. <see cref="IncludeFileName"/>):
    /// подписи строк списка выбора обязаны быть уникальными и различимыми (issue #330).
    /// </summary>
    public string DisplayName
    {
        get
        {
            var type = File.Kind == PlatformDistributionKind.WindowsSetupZip
                ? (PlatformDistributionPicker.IsThinClient(File) ? "Тонкий клиент (" : "Полный клиент (")
                  + GetDistributionExtension(File) + ")"
                : File.Kind switch
                {
                    PlatformDistributionKind.LinuxDeb => "Пакет deb",
                    PlatformDistributionKind.LinuxRpm => "Пакет rpm",
                    PlatformDistributionKind.LinuxTarGz => "Архив tar.gz",
                    _ => File.FileName,
                };
            var arch = string.IsNullOrWhiteSpace(Architecture) ? string.Empty : " · " + Architecture;
            // Для неизвестных типов type уже и есть имя файла — повторять его нельзя.
            var name = IncludeFileName && !string.Equals(type, File.FileName, StringComparison.Ordinal)
                ? " · " + File.FileName
                : string.Empty;
            var size = SizeBytes > 0 ? " · " + FormatSize(SizeBytes) : string.Empty;
            return type + arch + name + size;
        }
    }

    /// <inheritdoc />
    public override string ToString() => DisplayName;

    /// <summary>Фактическое расширение дистрибутива (без точки, нижний регистр):
    /// «zip»/«rar»/«7z»/«exe»/«arj»; при отсутствии — «zip» (привычное значение).</summary>
    private static string GetDistributionExtension(PlatformReleaseFile file)
    {
        var name = file?.FileName ?? string.Empty;
        var dot = name.LastIndexOf('.');
        if (dot < 0 || dot == name.Length - 1)
            return "zip";
        var ext = name[(dot + 1)..].ToLowerInvariant();
        // «tar.gz» — расширение из двух частей; здесь это не Windows-расширение.
        return ext is "gz" or "gzip" ? "zip" : ext;
    }

    private static string FormatSize(long bytes)
    {
        const long kb = 1024;
        const long mb = kb * 1024;
        const long gb = mb * 1024;
        if (bytes >= gb)
            return $"{bytes / (double)gb:0.#} ГБ";
        if (bytes >= mb)
            return $"{bytes / (double)mb:0.#} МБ";
        if (bytes >= kb)
            return $"{bytes / (double)kb:0.#} КБ";
        return bytes.ToString();
    }
}

/// <summary>
/// Чистый выбор файла дистрибутива версии платформы 1С под ОС, разрядность и тип
/// (issue #330): фильтрация файлов релиза каталога <c>releases.1c.ru</c> по типу
/// дистрибутива и приоритет подходящей разрядности. Не выполняет сетевых запросов
/// и не зависит от UI — покрывается юнит-тестами (маппинг «версия+разрядность → файл»).
/// </summary>
public static class PlatformDistributionPicker
{
    /// <summary>Разрядность дистрибутива: 64-битная.</summary>
    public const string Arch64 = "x64";

    /// <summary>Разрядность дистрибутива: 32-битная.</summary>
    public const string Arch32 = "x86";

    /// <summary>
    /// Фильтрует файлы релиза по разрядности (issue #330 п.1): остаются файлы
    /// выбранной разрядности (x64/x86) и файлы без разрядности (Linux-пакеты,
    /// архивы), у которых битность не определена. Чистый метод — единое правило
    /// фильтра для вариантов дистрибутива и для списка файлов окна скачивания.
    /// Пустой список — пустой результат; null — пустой результат.
    /// </summary>
    /// <param name="files">Файлы дистрибутива релиза.</param>
    /// <param name="is64Bit">True — целевая разрядность x64, false — x86.</param>
    public static IReadOnlyList<PlatformReleaseFile> FilterByArchitecture(
        IReadOnlyList<PlatformReleaseFile> files, bool is64Bit)
    {
        if (files is null || files.Count == 0)
            return Array.Empty<PlatformReleaseFile>();

        var arch = is64Bit ? Arch64 : Arch32;
        return files
            .Where(f => string.IsNullOrWhiteSpace(f.Architecture)
                || string.Equals(f.Architecture, arch, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// Выбирает файл дистрибутива из списка файлов релиза по типу и разрядности.
    /// Приоритет: файлы подходящего типа (см. <paramref name="type"/>), затем файлы
    /// нужной разрядности (x64 для 64-битной ОС, x86 для 32-битной), затем первый
    /// из оставшихся. Пустой список или отсутствие подходящего файла — null.
    /// </summary>
    /// <param name="files">Файлы дистрибутива релиза.</param>
    /// <param name="is64Bit">True — целевая разрядность x64, false — x86.</param>
    /// <param name="type">Тип дистрибутива; <see cref="PlatformDownloadType.Auto"/> —
    /// по умолчанию для <paramref name="isWindows"/>.</param>
    /// <param name="isWindows">True — целевая ОС Windows (нужен для режима Auto).</param>
    public static PlatformReleaseFile? PickFile(
        IReadOnlyList<PlatformReleaseFile> files, bool is64Bit, PlatformDownloadType type, bool isWindows)
    {
        if (files is null || files.Count == 0)
            return null;

        var filtered = FilterByType(files, type, isWindows);
        if (filtered.Count == 0)
            return null;

        // 1) Файл нужной разрядности; 2) файл без разрядности; 3) первый любой.
        var arch = is64Bit ? Arch64 : Arch32;
        return filtered.FirstOrDefault(f => string.Equals(f.Architecture, arch, StringComparison.OrdinalIgnoreCase))
            ?? filtered.FirstOrDefault(f => string.IsNullOrWhiteSpace(f.Architecture))
            ?? filtered[0];
    }

    /// <summary>
    /// Типы дистрибутивов, доступных в списке файлов: на Windows — «Полный клиент»
    /// и/или «Тонкий клиент» (при наличии файлов с «thin» в имени); на Linux —
    /// «Пакет» (deb/rpm) и/или «Архив» (tar.gz). Всегда включает «Авто», если есть
    /// хоть один подходящий файл. Список сохраняет порядок значений enum.
    /// </summary>
    public static IReadOnlyList<PlatformDownloadType> AvailableTypes(
        IReadOnlyList<PlatformReleaseFile> files, bool isWindows)
    {
        if (files is null || files.Count == 0)
            return Array.Empty<PlatformDownloadType>();

        var result = new List<PlatformDownloadType>();
        foreach (var type in Enum.GetValues<PlatformDownloadType>())
        {
            if (type == PlatformDownloadType.Auto)
                continue;
            if (FilterByType(files, type, isWindows).Count > 0)
                result.Add(type);
        }

        if (result.Count > 0)
            result.Insert(0, PlatformDownloadType.Auto);
        return result;
    }

    /// <summary>True — файл тонкого клиента Windows (имя содержит токен «thin»).</summary>
    public static bool IsThinClient(PlatformReleaseFile file)
    {
        var name = file?.FileName ?? string.Empty;
        return name.Contains("thin", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True — файл «обновление-сборка дистрибутива» (issue #334): имя содержит
    /// «updsetup»/«update-setup». Такой архив предназначен для сборки обновлений,
    /// а не для установки платформы — при автовыборе он ставится в конец списка
    /// и не рекомендуется, но остаётся доступным пользователю вручную.
    /// </summary>
    public static bool IsUpdateSetupPackage(PlatformReleaseFile file)
    {
        var name = file?.FileName ?? string.Empty;
        return name.Contains("updsetup", StringComparison.OrdinalIgnoreCase)
            || name.Contains("update-setup", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Отображаемое имя типа дистрибутива (ключ локализации).</summary>
    public static string TypeLocalizationKey(PlatformDownloadType type) => type switch
    {
        PlatformDownloadType.Auto => "PlatformDownload.Type.Auto",
        PlatformDownloadType.Client => "PlatformDownload.Type.Client",
        PlatformDownloadType.ThinClient => "PlatformDownload.Type.ThinClient",
        PlatformDownloadType.Package => "PlatformDownload.Type.Package",
        PlatformDownloadType.Archive => "PlatformDownload.Type.Archive",
        _ => "PlatformDownload.Type.Auto",
    };

    /// <summary>
    /// Строит список вариантов дистрибутива, отфильтрованный по целевой ОС (issue #330/#334):
    /// Windows — zip-клиенты (полный и тонкий), Linux — пакеты deb/rpm/tar.gz. Сортировка:
    /// полный клиент → тонкий клиент → пакеты → архив; внутри — x64 перед x86; рекомендуемый
    /// вариант помечается и ставится первым. Пустой список файлов — пустой результат.
    /// </summary>
    /// <param name="files">Файлы дистрибутива релиза.</param>
    /// <param name="isWindows">True — целевая ОС Windows, false — Linux.</param>
    /// <param name="is64Bit">Разрядность ОС для пометки рекомендуемого варианта.</param>
    public static IReadOnlyList<PlatformDistributionOption> BuildOptions(
        IReadOnlyList<PlatformReleaseFile> files, bool isWindows, bool is64Bit)
    {
        var options = new List<PlatformDistributionOption>();
        if (files is null || files.Count == 0)
            return options;

        var relevant = (isWindows
                ? files.Where(f => f.Kind == PlatformDistributionKind.WindowsSetupZip)
                : files.Where(f => f.Kind is PlatformDistributionKind.LinuxDeb or PlatformDistributionKind.LinuxRpm
                    or PlatformDistributionKind.LinuxTarGz))
            .ToList();
        if (relevant.Count == 0)
            return options;

        relevant.Sort((a, b) =>
        {
            var typeRank = TypeRank(a, isWindows).CompareTo(TypeRank(b, isWindows));
            if (typeRank != 0)
                return typeRank;

            var aX64 = string.Equals(a.Architecture, Arch64, StringComparison.OrdinalIgnoreCase);
            var bX64 = string.Equals(b.Architecture, Arch64, StringComparison.OrdinalIgnoreCase);
            if (aX64 != bX64)
                return aX64 ? -1 : 1;

            return string.Compare(a.FileName, b.FileName, StringComparison.OrdinalIgnoreCase);
        });

        var recommended = PickFile(files, is64Bit, PlatformDownloadType.Auto, isWindows);
        var recommendedUrl = recommended?.Url ?? string.Empty;

        // Имя файла включается в подпись всегда для Windows-вариантов и для прочих
        // типов, когда файлов этого типа больше одного — иначе строки списка выбора
        // неотличимы (issue #330). Единственный Linux-пакет допустимо показывать без имени.
        var kindCounts = relevant.GroupBy(f => f.Kind)
            .ToDictionary(g => g.Key, g => g.Count());

        foreach (var file in relevant)
        {
            var isRec = !string.IsNullOrWhiteSpace(recommendedUrl) &&
                        string.Equals(file.Url, recommendedUrl, StringComparison.OrdinalIgnoreCase);
            bool? includeName = file.Kind == PlatformDistributionKind.WindowsSetupZip
                ? null // Windows: имя файла — обязательная часть подписи (умолчание конструктора).
                : kindCounts.TryGetValue(file.Kind, out var count) && count > 1;
            options.Add(new PlatformDistributionOption(file, isRec, includeName));
        }

        return options;
    }

    /// <summary>Приоритет типа при сортировке вариантов (меньше — выше). Файлы
    /// «обновление-сборка дистрибутива» (updsetup, issue #334) уходят в конец списка —
    /// они не подходят для установки платформы напрямую.</summary>
    private static int TypeRank(PlatformReleaseFile file, bool isWindows)
    {
        if (isWindows)
            return IsUpdateSetupPackage(file) ? 2 : (IsThinClient(file) ? 1 : 0);
        return file.Kind switch
        {
            PlatformDistributionKind.LinuxDeb => 0,
            PlatformDistributionKind.LinuxRpm => 1,
            _ => 2,
        };
    }

    /// <summary>Фильтр файлов по типу дистрибутива (для Auto — по целевой ОС).</summary>
    private static List<PlatformReleaseFile> FilterByType(
        IReadOnlyList<PlatformReleaseFile> files, PlatformDownloadType type, bool isWindows)
    {
        return type switch
        {
            PlatformDownloadType.Client =>
                files.Where(f => f.Kind == PlatformDistributionKind.WindowsSetupZip
                    && !IsThinClient(f) && !IsUpdateSetupPackage(f)).ToList(),
            PlatformDownloadType.ThinClient =>
                files.Where(f => f.Kind == PlatformDistributionKind.WindowsSetupZip && IsThinClient(f)).ToList(),
            PlatformDownloadType.Package =>
                files.Where(f => f.Kind is PlatformDistributionKind.LinuxDeb or PlatformDistributionKind.LinuxRpm).ToList(),
            PlatformDownloadType.Archive =>
                files.Where(f => f.Kind == PlatformDistributionKind.LinuxTarGz).ToList(),
            _ => AutoFiles(files, isWindows),
        };
    }

    /// <summary>Рекомендуемые файлы для ОС: Windows — zip-клиенты (полный и тонкий),
    /// Linux — пакеты deb/rpm, при их отсутствии — tar.gz.</summary>
    private static List<PlatformReleaseFile> AutoFiles(IReadOnlyList<PlatformReleaseFile> files, bool isWindows)
    {
        if (isWindows)
        {
            var zips = files.Where(f => f.Kind == PlatformDistributionKind.WindowsSetupZip).ToList();
            if (zips.Count == 0)
                return zips;
            // issue #334: архивы «обновление-сборка дистрибутива» (updsetup) не
            // рекомендуются — полный клиент без них предпочтительнее.
            var full = zips.Where(f => !IsThinClient(f) && !IsUpdateSetupPackage(f)).ToList();
            if (full.Count == 0)
                full = zips.Where(f => !IsThinClient(f)).ToList();
            return full.Count > 0 ? full : zips;
        }

        var packages = files
            .Where(f => f.Kind is PlatformDistributionKind.LinuxDeb or PlatformDistributionKind.LinuxRpm)
            .ToList();
        if (packages.Count > 0)
            return packages;

        return files.Where(f => f.Kind == PlatformDistributionKind.LinuxTarGz).ToList();
    }
}