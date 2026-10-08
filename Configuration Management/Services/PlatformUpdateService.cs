using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Configuration_Management.Models;

namespace Configuration_Management.Services;

/// <summary>
/// Реализация <see cref="IPlatformUpdateService"/>: получение списка доступных версий
/// технологической платформы 1С со страниц <c>releases.1c.ru/project/Platform83</c> и
/// <c>Platform85</c> (issue #334), ленивая подгрузка файлов дистрибутива из ответа
/// <c>version_files</c> и выбор файла под текущую ОС/разрядность. Сетевые вызовы
/// выполняются через <see cref="IOneCUpdatesService.FetchPageAsync"/> (готовая авторизация
/// портала, ручное следование редиректам, различение AuthRequired/AuthFailed), парсинг —
/// чистым <see cref="OneCPlatformCatalogParser"/>. Никакие исключения наружу не бросаются:
/// итог всегда описывается статусом <see cref="PortalFetchStatus"/> и ключом локализации
/// «PlatformUpdate.Error.*».
/// </summary>
public sealed class PlatformUpdateService : IPlatformUpdateService
{
    /// <summary>Ключ локализации: требуется вход на портал 1С.</summary>
    public const string ErrorAuthRequired = "PlatformUpdate.Error.AuthRequired";

    /// <summary>Ключ локализации: вход на портал 1С не подтверждён сервером (401).</summary>
    public const string ErrorAuthFailed = "PlatformUpdate.Error.AuthFailed";

    /// <summary>Ключ локализации: исчерпан лимит попыток входа на portal.1c.ru за сессию
    /// (анти-брутфорс; повторить можно позже или после смены учётных данных ИТС).</summary>
    public const string ErrorLoginLimit = "PlatformUpdate.Error.LoginLimit";

    /// <summary>Ключ локализации: форма входа на portal.1c.ru недоступна для программного
    /// входа (изменилась радикально — OAuth/JS-челлендж, либо не получен HTML). Пользователю
    /// предлагается открыть login.1c.ru в браузере (issue #323/#330/#334).</summary>
    public const string ErrorAuthFormUnavailable = "PlatformUpdate.Error.FormUnavailable";

    /// <summary>Ключ локализации: каталог/версия не найдены (404).</summary>
    public const string ErrorNotFound = "PlatformUpdate.Error.NotFound";

    /// <summary>Ключ локализации: сетевая ошибка.</summary>
    public const string ErrorNetwork = "PlatformUpdate.Error.NetworkError";

    /// <summary>Ключ локализации: операция отменена.</summary>
    public const string ErrorCancelled = "PlatformUpdate.Error.Cancelled";

    /// <summary>Маркер страницы входа в тексте ответа (Spring Security CAS перенаправляет
    /// releases.1c.ru сюда при отсутствии сессии).</summary>
    private const string LoginHostMarker = "login.1c.ru";

    private readonly IOneCUpdatesService _updates;
    private readonly IAppLogger _logger;
    private readonly Func<string, CancellationToken, Task<PortalPageResult>> _pageProvider;

    /// <summary>
    /// Основной конструктор (для DI): текст страниц получает через
    /// <see cref="IOneCUpdatesService.FetchPageAsync"/> (статусы авторизации и сети уже
    /// распознаны службой портала).
    /// </summary>
    public PlatformUpdateService(IOneCUpdatesService updates, IAppLogger logger)
        : this(updates, logger, null)
    {
    }

    /// <summary>
    /// Конструктор с инжектируемым провайдером текста страницы (для тестов): принимает URL
    /// и токен отмены, возвращает текст ответа или null/исключение — маппинг ошибок выполняется
    /// по контракту, описанному в <see cref="MapLegacyText"/>. Либо принимает провайдер готовых
    /// результатов <see cref="PortalPageResult"/> (полный контроль статусов).
    /// </summary>
    /// <param name="updates">Сервис портала 1С (используется как источник по умолчанию).</param>
    /// <param name="logger">Журнал приложения.</param>
    /// <param name="textProvider">Провайдер текста страницы; null — <c>FetchPageAsync</c>.</param>
    internal PlatformUpdateService(
        IOneCUpdatesService updates,
        IAppLogger logger,
        Func<string, CancellationToken, Task<string?>>? textProvider)
    {
        _updates = updates ?? throw new ArgumentNullException(nameof(updates));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _pageProvider = textProvider is null
            ? (url, ct) => _updates.FetchPageAsync(url, ct)
            : (url, ct) => MapLegacyText(textProvider(url, ct));
    }

    /// <inheritdoc />
    public async Task<PlatformCatalogResult> GetAvailableReleasesAsync(CancellationToken ct = default)
        => await GetAvailableReleasesForNickAsync(OneCPlatformCatalogParser.Platform83Nick, ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<PlatformCatalogResult> GetAllAvailableReleasesAsync(CancellationToken ct = default)
    {
        // Объединённый каталог платформы: все поддерживаемые линии (8.3, 8.5, issue #334).
        // Сбой одного каталога не роняет общий результат — берём то, что получено;
        // статус Ok, если получен хотя бы один каталог.
        PortalFetchStatus? firstFailure = null;
        string? firstErrorKey = null;
        var all = new List<PlatformRelease>();
        foreach (var nick in OneCPlatformCatalogParser.SupportedPlatformNicks)
        {
            var result = await GetAvailableReleasesForNickAsync(nick, ct).ConfigureAwait(false);
            if (result.Status == PortalFetchStatus.Ok)
            {
                all.AddRange(result.Releases);
            }
            else
            {
                _logger.Warn($"[PlatformUpdate] Каталог {nick} недоступен: {result.ErrorKey ?? result.Status.ToString()}");
                firstFailure ??= result.Status;
                firstErrorKey ??= result.ErrorKey;
            }
        }

        if (all.Count == 0)
            return firstFailure is null ? Failure(PortalFetchStatus.NetworkError) : Failure(firstFailure.Value);

        // Дедупликация по версии (линии 8.3/8.5 не пересекаются, но страховка) и
        // сортировка по убыванию числовыми сегментами.
        var dedup = all
            .GroupBy(r => r.Version, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
        dedup.Sort((x, y) => OneCPlatformCatalogParser.CompareVersions(y.Version, x.Version));
        return new PlatformCatalogResult { Status = PortalFetchStatus.Ok, Releases = dedup };
    }

    /// <inheritdoc />
    public async Task<PlatformCatalogResult> GetAvailableReleasesForNickAsync(string nick, CancellationToken ct = default)
    {
        // Полный адрес каталога с allUpdates=true раскрывает ВСЕ версии, а не только
        // последние релизы (issue #330: «в списке только версия 8.3.27, не все версии»).
        var url = BuildCatalogUrl(nick, allUpdates: true);
        var (status, text) = await FetchPageAsync(url, ct).ConfigureAwait(false);
        if (status != PortalFetchStatus.Ok)
            return Failure(status);

        var releases = OneCPlatformCatalogParser.ParseVersions(text!, nick);
        if (releases.Count == 0)
        {
            // Страница получена, но ни одной версии не распознано — структура каталога
            // могла измениться либо пришёл неожиданный контент. Показываем сетевую ошибку.
            _logger.Warn($"[PlatformUpdate] Каталог получен, но версии не распознаны: {url}");
            return Failure(PortalFetchStatus.NetworkError);
        }

        return new PlatformCatalogResult { Status = PortalFetchStatus.Ok, Releases = releases };
    }

    /// <inheritdoc />
    public async Task<PlatformCatalogResult> LoadReleaseFilesAsync(
        PlatformRelease release, CancellationToken ct = default)
    {
        // Ник каталога для построения URL файлов берётся из релиза: версии 8.5 живут
        // в каталоге Platform85, подстановка Platform83 даёт пустой список файлов и
        // «setup.exe не найден в архиве» (issue #334).
        ArgumentNullException.ThrowIfNull(release);
        var nick = string.IsNullOrWhiteSpace(release.Nick)
            ? OneCPlatformCatalogParser.Platform83Nick
            : release.Nick;
        return await LoadReleaseFilesForNickAsync(release, nick, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PlatformCatalogResult> LoadReleaseFilesForNickAsync(
        PlatformRelease release, string nick, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(release);

        var url = BuildVersionFilesUrl(release, nick);
        var (status, text) = await FetchPageAsync(url, ct).ConfigureAwait(false);
        if (status != PortalFetchStatus.Ok)
            return Failure(status);

        var files = OneCPlatformCatalogParser.ParseDistributionFiles(text!);
        release.VersionFilesUrl = url;
        release.Files.Clear();
        release.Files.AddRange(files);

        return new PlatformCatalogResult { Status = PortalFetchStatus.Ok, Release = release };
    }

    /// <inheritdoc />
    public PlatformReleaseFile? PickDistribution(IReadOnlyList<PlatformReleaseFile> files)
        => PickForPlatform(files, OperatingSystem.IsWindows());

    /// <summary>
    /// Внутренний чистый выбор файла дистрибутива для указанной платформы
    /// (выделен для тестирования обеих веток на любой ОС).
    /// </summary>
    /// <param name="files">Файлы дистрибутива релиза.</param>
    /// <param name="isWindows">True — выбор для Windows, false — для Linux.</param>
    /// <returns>Выбранный файл или null, если подходящего нет.</returns>
    internal static PlatformReleaseFile? PickForPlatform(
        IReadOnlyList<PlatformReleaseFile> files, bool isWindows)
    {
        if (files is null || files.Count == 0)
            return null;

        if (isWindows)
        {
            // Windows: zip-архив с setup.exe; x64 предпочтительнее x86.
            var zips = files.Where(f => f.Kind == PlatformDistributionKind.WindowsSetupZip).ToList();
            if (zips.Count == 0)
                return null;
            return zips.FirstOrDefault(Is64Bit) ?? zips.FirstOrDefault(Is32Bit) ?? zips[0];
        }

        // Linux: пакет .deb/.rpm (x64 предпочтительнее), при отсутствии — .tar.gz.
        var packages = files
            .Where(f => f.Kind is PlatformDistributionKind.LinuxDeb or PlatformDistributionKind.LinuxRpm)
            .ToList();
        return packages.FirstOrDefault(Is64Bit)
            ?? packages.FirstOrDefault()
            ?? files.FirstOrDefault(f => f.Kind == PlatformDistributionKind.LinuxTarGz);
    }

    /// <summary>True — файл собран под 64-битную архитектуру.</summary>
    private static bool Is64Bit(PlatformReleaseFile file)
        => string.Equals(file.Architecture, "x64", StringComparison.OrdinalIgnoreCase);

    /// <summary>True — файл собран под 32-битную архитектуру.</summary>
    private static bool Is32Bit(PlatformReleaseFile file)
        => string.Equals(file.Architecture, "x86", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Запрашивает страницу через провайдера и маппит ошибки по контракту: статус провайдера
    /// (в т.ч. <see cref="PortalFetchStatus.AuthFailed"/> — вход не подтверждён, issue #334)
    /// передаётся как есть; у текста дополнительно проверяется маркер «404 Not Found»
    /// (страница может прийти с HTTP 200). Не бросает исключений.
    /// </summary>
    private async Task<(PortalFetchStatus Status, string? Text)> FetchPageAsync(
        string url, CancellationToken ct)
    {
        try
        {
            var page = await _pageProvider(url, ct).ConfigureAwait(false);
            if (page.Status == PortalFetchStatus.NetworkError)
                _logger.Warn($"[PlatformUpdate] Пустой ответ или HTTP-ошибка: {url}");

            if (page.Status != PortalFetchStatus.Ok)
                return (page.Status, null);

            var text = page.Text;
            if (LooksLikeNotFoundPage(text))
                return (PortalFetchStatus.NotFound, null);

            return (PortalFetchStatus.Ok, text);
        }
        catch (OperationCanceledException)
        {
            return (PortalFetchStatus.Cancelled, null);
        }
        catch (Exception ex)
        {
            _logger.Error($"[PlatformUpdate] Сетевая ошибка при обращении к каталогу: {url}", ex);
            return (PortalFetchStatus.NetworkError, null);
        }
    }

    /// <summary>
    /// Маппит «устаревший» текстовый провайдер (строка или null/исключение) в
    /// <see cref="PortalPageResult"/> по прежнему контракту: null/пусто → NetworkError;
    /// текст со страницей входа (<c>login.1c.ru</c>) → AuthRequired; маркер «404 Not Found» →
    /// NotFound; отмена → Cancelled; прочие исключения → NetworkError.
    /// </summary>
    private static async Task<PortalPageResult> MapLegacyText(Task<string?> textTask)
    {
        try
        {
            var text = await textTask.ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text))
                return Page(PortalFetchStatus.NetworkError);
            if (text.Contains(LoginHostMarker, StringComparison.OrdinalIgnoreCase))
                return Page(PortalFetchStatus.AuthRequired);
            if (LooksLikeNotFoundPage(text))
                return Page(PortalFetchStatus.NotFound);
            return Page(PortalFetchStatus.Ok, text);
        }
        catch (OperationCanceledException)
        {
            return Page(PortalFetchStatus.Cancelled);
        }
        catch
        {
            return Page(PortalFetchStatus.NetworkError);
        }
    }

    private static PortalPageResult Page(PortalFetchStatus status, string? text = null)
        => new() { Status = status, Text = text };

    /// <summary>Эвристический маркер страницы «не найдено»: «404» вместе с «Not Found»/
    /// «страница не найдена» либо только русская фраза.</summary>
    private static bool LooksLikeNotFoundPage(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        if (text.Contains("страница не найдена", StringComparison.OrdinalIgnoreCase))
            return true;
        return text.Contains("404", StringComparison.OrdinalIgnoreCase)
            && text.Contains("not found", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Адрес HTML-каталога версий платформы: <c>releases.1c.ru/project/<nick></c>
    /// (например, Platform83/Platform85). При <paramref name="allUpdates"/> добавляется
    /// параметр <c>allUpdates=true#updates</c>, раскрывающий полный список версий, а не
    /// только последние релизы (issue #330). Пустой/невалидный ник — каталог Platform83.</summary>
    private static string BuildCatalogUrl(string? nick, bool allUpdates = false)
    {
        var safeNick = string.IsNullOrWhiteSpace(nick)
            ? OneCPlatformCatalogParser.Platform83Nick
            : nick.Trim();
        var url = $"{OneCUpdatesService.ReleasesProjectBaseUrl}/{Uri.EscapeDataString(safeNick)}";
        if (allUpdates)
            url += "?allUpdates=true#updates";
        return url;
    }

    /// <summary>Абсолютный адрес страницы файлов релиза. Если у релиза ссылка не задана —
    /// строится по правилу <c>version_files?nick=…&ver=…</c> (ник каталога — параметр,
    /// issue #334); относительная ссылка дополняется хостом портала.</summary>
    private static string BuildVersionFilesUrl(PlatformRelease release, string nick)
    {
        var url = (release.VersionFilesUrl ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(url))
        {
            // Приоритет ника: у релиза (каталог, из которого он получен, issue #334),
            // затем у переданного параметра, затем дефолт Platform83.
            var effectiveNick = !string.IsNullOrWhiteSpace(release.Nick)
                ? release.Nick
                : string.IsNullOrWhiteSpace(nick)
                    ? OneCPlatformCatalogParser.Platform83Nick
                    : nick.Trim();
            url = $"{OneCUpdatesService.ReleasesBaseUrl}?nick={Uri.EscapeDataString(effectiveNick)}" +
                  $"&ver={Uri.EscapeDataString(release.Version)}";
        }
        else if (url.StartsWith("/", StringComparison.Ordinal))
        {
            url = $"https://releases.1c.ru{url}";
        }

        return url;
    }

    /// <summary>Собирает результат ошибки с ключом локализации по статусу.</summary>
    private static PlatformCatalogResult Failure(PortalFetchStatus status)
    {
        var key = status switch
        {
            PortalFetchStatus.AuthRequired => ErrorAuthRequired,
            PortalFetchStatus.AuthFailed => ErrorAuthFailed,
            PortalFetchStatus.LoginLimitReached => ErrorLoginLimit,
            // Форма входа изменилась/недоступна (OAuth/JS-челлендж) — отдельное понятное
            // сообщение с советом открыть login.1c.ru в браузере (issue #323/#330/#334).
            PortalFetchStatus.FormUnavailable => ErrorAuthFormUnavailable,
            PortalFetchStatus.NotFound => ErrorNotFound,
            PortalFetchStatus.Cancelled => ErrorCancelled,
            _ => ErrorNetwork,
        };
        return new PlatformCatalogResult { Status = status, ErrorKey = key };
    }
}