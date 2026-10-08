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
    public PlatformDistributionOption(PlatformReleaseFile file, bool isRecommended = false)
    {
        File = file ?? throw new ArgumentNullException(nameof(file));
        IsRecommended = isRecommended;
    }

    /// <summary>Файл дистрибутива.</summary>
    public PlatformReleaseFile File { get; }

    /// <summary>True — рекомендуемый вариант для текущей ОС/разрядности.</summary>
    public bool IsRecommended { get; }

    /// <summary>Тип дистрибутива по расширению.</summary>
    public PlatformDistributionKind Kind => File.Kind;

    /// <summary>Разрядность («x64»/«x86») или пустая строка.</summary>
    public string Architecture => File.Architecture ?? string.Empty;

    /// <summary>Размер файла, байт (0 — неизвестен).</summary>
    public long SizeBytes => File.SizeBytes;

    /// <summary>
    /// Человекочитаемое представление варианта: «Полный клиент (zip) · x64 · 1,2 ГБ»
    /// (Windows) или «Пакет deb · amd64 · …» (Linux).
    /// </summary>
    public string DisplayName
    {
        get
        {
            var type = File.Kind == PlatformDistributionKind.WindowsSetupZip
                ? (PlatformDistributionPicker.IsThinClient(File) ? "Тонкий клиент (zip)" : "Полный клиент (zip)")
                : File.Kind switch
                {
                    PlatformDistributionKind.LinuxDeb => "Пакет deb",
                    PlatformDistributionKind.LinuxRpm => "Пакет rpm",
                    PlatformDistributionKind.LinuxTarGz => "Архив tar.gz",
                    _ => File.FileName,
                };
            var arch = string.IsNullOrWhiteSpace(Architecture) ? string.Empty : " · " + Architecture;
            var size = SizeBytes > 0 ? " · " + FormatSize(SizeBytes) : string.Empty;
            return type + arch + size;
        }
    }

    /// <inheritdoc />
    public override string ToString() => DisplayName;

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
        foreach (var file in relevant)
        {
            var isRec = !string.IsNullOrWhiteSpace(recommendedUrl) &&
                        string.Equals(file.Url, recommendedUrl, StringComparison.OrdinalIgnoreCase);
            options.Add(new PlatformDistributionOption(file, isRec));
        }

        return options;
    }

    /// <summary>Приоритет типа при сортировке вариантов (меньше — выше).</summary>
    private static int TypeRank(PlatformReleaseFile file, bool isWindows)
    {
        if (isWindows)
            return IsThinClient(file) ? 1 : 0;
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
                files.Where(f => f.Kind == PlatformDistributionKind.WindowsSetupZip && !IsThinClient(f)).ToList(),
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
            var full = zips.Where(f => !IsThinClient(f)).ToList();
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