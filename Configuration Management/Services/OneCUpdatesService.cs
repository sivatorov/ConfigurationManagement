using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Configuration_Management.Models;

namespace Configuration_Management.Services;

/// <summary>
/// Реализация <see cref="IOneCUpdatesService"/>: формирование web-адреса обновлений
/// по правилу 1С, проверка наличия новых релизов в каталоге и загрузка дистрибутива
/// с прогрессом. Паттерны сети (static <c>HttpClient</c>, User-Agent, таймаут,
/// устойчивый парсинг, отсутствие падения при ошибках) — по образцу
/// <see cref="GitHubReleaseService"/> и <see cref="UpdateService"/>.
/// </summary>
public class OneCUpdatesService : IOneCUpdatesService
{
    /// <summary>Базовый адрес web-ресурса обновлений 1С (сегмент <c>1cbsl</c> для типовых решений).
    /// Используется как устаревший механизм формирования адреса, когда ник конфигурации не задан.</summary>
    public const string DefaultBaseUrl = "https://downloads.1c.ru/ipp/1cbsl/";

    /// <summary>Базовый адрес ресурса обновлений 1С в формате <c>version_files?nick=…&ver=…</c>.</summary>
    public const string ReleasesBaseUrl = "https://releases.1c.ru/version_files";

    /// <summary>Базовый адрес HTML-списка версий конфигурации по нику: <c>project/<nick></c>.</summary>
    public const string ReleasesProjectBaseUrl = "https://releases.1c.ru/project";

    private const int TimeoutSeconds = 15;

    /// <summary>Таймаут одной попытки многопоточной загрузки (дольше основного — CDN дистрибутивов).</summary>
    private static readonly TimeSpan ParallelAttemptTimeout = TimeSpan.FromMinutes(20);

    /// <summary>Максимальное число переходов при ручном следовании редиректам (защита от зацикливания).</summary>
    private const int MaxRedirects = 10;

    /// <summary>User-Agent запросов к порталу и CDN дистрибутивов (единый для всех клиентов службы).
    /// Версия подставляется из сборки, чтобы сервер не считал клиент устаревшим (issue #334).</summary>
    private static readonly string UserAgent =
        $"ConfigurationManagement/{VersionInfo.Display()} (+https://github.com/sivatorov/ConfigurationManagement)";

    /// <summary>
    /// Адрес формы входа на портал 1С (сервис «1С:Обновление программ»). Ресурс
    /// releases.1c.ru при отсутствии сессии перенаправляет сюда (Spring Security CAS):
    /// сначала нужно GET'ом получить страницу с формой и скрытым токеном <c>execution</c>,
    /// затем POST'ом отправить логин/пароль вместе с этим токеном. После успешного входа
    /// сервер выставляет сессионные cookie, которые сохраняются в <see cref="CookieContainer"/>.
    /// </summary>
    private const string PortalLoginUrl = "https://login.1c.ru/login";

    /// <summary>Хранилище session-cookie гибридной авторизации портала 1С: общее для основного
    /// клиента и клиента многопоточной загрузки (после входа cookie попадают в оба).</summary>
    private readonly CookieContainer _cookieContainer = new();

    /// <summary>Версия HTTP для запросов к форме входа portal.1c.ru: часть Spring Security CAS
    /// некорректно обрабатывает HTTP/2 (ответ 401 вместо формы/редиректа), поэтому вход
    /// выполняется принудительно по HTTP/1.1 (issue #334).</summary>
    private static readonly Version LoginHttpVersion = HttpVersion.Version11;

    /// <summary>Максимальное число попыток программного входа на portal.1c.ru за сессию службы
    /// (защита от анти-брутфорс блокировки портала; счётчик сбрасывается при смене учётной
    /// записи, при успешном входе и автоматически через <see cref="LoginLimitCooldown"/>,
    /// см. <see cref="CanAttemptPortalLogin"/>).</summary>
    private const int MaxPortalLoginAttempts = 3;

    /// <summary>Максимальное число попыток программного входа на portal.1c.ru в рамках ОДНОГО
    /// вызова (операции) <see cref="SendWithAuthAsync"/>: при повторном 302→login после
    /// «успешного» входа выполняется повторный вход со свежей формой (новый execution/lt),
    /// а не мгновенный AuthRequired (issue #323/#330/#334). Сессионный лимит
    /// <see cref="MaxPortalLoginAttempts"/> при этом не тратится впустую.</summary>
    private const int MaxLoginAttemptsPerOperation = 2;

    /// <summary>Период автосброса лимита попыток входа на portal.1c.ru: после исчерпания
    /// лимита (<see cref="MaxPortalLoginAttempts"/>) новая попытка входа разрешается не ранее
    /// чем через этот интервал (анти-брутфорс портала; issue #334/#330/#323).</summary>
    private static readonly TimeSpan LoginLimitCooldown = TimeSpan.FromMinutes(10);

    /// <summary>HTTP-обработчик, инжектируемый в тестах (fake вместо реальной сети); null — реальный стек.</summary>
    private readonly HttpMessageHandler? _handlerOverride;

    private readonly HttpClient _httpClient;

    private readonly IInfobaseRepository _repository;
    private readonly IItsAccountsStore? _itsAccounts;
    private readonly IAppLogger _logger;

    /// <summary>Число выполненных попыток входа на portal.1c.ru (не более
    /// <see cref="MaxPortalLoginAttempts"/> за сессию службы; сессионные cookie хранятся
    /// в <see cref="CookieContainer"/> клиента). В отличие от прежнего «одноразового» флага
    /// позволяет повторять вход для каждого нового окна/операции (issue #330/#323: одна ошибка
    /// входа не должна «отравлять» всю сессию).</summary>
    private int _portalLoginAttempts;

    /// <summary>Сигнатура учётной записи последней попытки входа (без пароля): при её смене
    /// счётчик <see cref="_portalLoginAttempts"/> сбрасывается.</summary>
    private string? _lastAttemptAccountSignature;

    /// <summary>Результат последней попытки входа на portal.1c.ru (для различения
    /// AuthRequired / AuthFailed в результатах проверок).</summary>
    private PortalLoginResult _lastLoginResult = PortalLoginResult.NoCredentials;

    /// <summary>Причина отклонения входа, найденная в теле ответа (issue #323): «капча» —
    /// портал запросил подтверждение и автоматический вход временно невозможен; null —
    /// причина не определена. Заполняется в <see cref="LogAnonymizedAuthFailure"/>; используется
    /// <see cref="AuthErrorKey"/> для выбора ключа <c>Updates.CaptchaRequired</c>.</summary>
    private string? _lastAuthFailureReason;

    /// <summary>Момент исчерпания лимита попыток входа (для автосброса по
    /// <see cref="LoginLimitCooldown"/>); default — лимит не исчерпан.</summary>
    private DateTime _limitReachedAt;

    /// <summary>Источник текущего времени для автосброса лимита попыток входа
    /// (в проде — <see cref="DateTime.UtcNow"/>; в тестах подменяется фиктивными часами,
    /// чтобы проверить повторную попытку после <see cref="LoginLimitCooldown"/>).</summary>
    internal Func<DateTime> UtcNowProvider = () => DateTime.UtcNow;

    /// <summary>
    /// Создаёт экземпляр службы. <paramref name="repository"/> (singleton) используется для
    /// чтения настроек (выбор учётной записи ИТС) на каждый сетевой запрос;
    /// <paramref name="logger"/> — для диагностики сетевых ошибок проверки обновлений.
    /// Учётные данные берутся из справочника <see cref="IItsAccountsStore"/> (issue #333):
    /// выбранная в настройках запись или «Основная». Без хранилища (конструктор без
    /// параметра) используются только устаревшие поля настроек
    /// <see cref="AppSettings.UpdatesLogin"/>/<see cref="AppSettings.UpdatesPassword"/> —
    /// обратная совместимость для прямых созданий в тестах.
    /// </summary>
    public OneCUpdatesService(IInfobaseRepository repository, IAppLogger logger)
        : this(repository, logger, handler: null, itsAccounts: null)
    {
    }

    /// <summary>
    /// Основной конструктор для DI: помимо репозитория и журнала внедряет хранилище
    /// учётных записей ИТС <see cref="IItsAccountsStore"/> (issue #333). Без него креды
    /// из справочника <c>its_accounts.json</c> не доходили бы до <see cref="GetCredentials"/>
    /// (вход на portal.1c.ru сообщал «не задан логин» — issue #334).
    /// </summary>
    public OneCUpdatesService(IInfobaseRepository repository, IAppLogger logger, IItsAccountsStore itsAccounts)
        : this(repository, logger, handler: null, itsAccounts: itsAccounts)
    {
    }

    /// <summary>
    /// Конструктор с инжектируемым HTTP-обработчиком (для тестов): весь сетевой стек
    /// службы — основной клиент и клиент многопоточной загрузки — работает через
    /// fake-обработчик, реальная сеть не используется.
    /// </summary>
    internal OneCUpdatesService(
        IInfobaseRepository repository,
        IAppLogger logger,
        HttpMessageHandler? handler,
        IItsAccountsStore? itsAccounts = null)
    {
        _repository = repository;
        _itsAccounts = itsAccounts;
        _logger = logger;
        _handlerOverride = handler;
        _httpClient = CreateHttpClient(handler);
    }

    /// <summary>Шаблон ссылки на архив дистрибутива конфигурации на странице каталога.</summary>
    private static readonly Regex ArchiveLinkRegex =
        new(@"href\s*=\s*[""'](?<url>[^""']*(?:setup|1c[^""']*)\.zip)[""']",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <inheritdoc />
    public IReadOnlyList<OneCConfigType> BuiltInConfigTypes => BuiltInConfigTypesHolder.All;

    private static class BuiltInConfigTypesHolder
    {
        internal static readonly IReadOnlyList<OneCConfigType> All =
            Services.BuiltInConfigTypes.All;
    }

    private HttpClient CreateHttpClient(HttpMessageHandler? handler)
    {
        var client = new HttpClient(handler ?? new HttpClientHandler
        {
            // Перенаправления обрабатываем вручную (см. SendWithAuthAsync): автоперенаправление
            // .NET снимает заголовок Authorization при переходе на другой хост (CDN), из-за чего
            // Basic Auth теряется. Поэтому AllowAutoRedirect=false, а редиректы следуем сами,
            // заново добавляя заголовок на каждом шаге.
            AllowAutoRedirect = false,
            // Хранилище session-cookie для гибридной авторизации (вход на portal.1c.ru):
            // после успешного входа cookie автоматически добавляются к последующим запросам.
            CookieContainer = _cookieContainer,
        })
        {
            Timeout = TimeSpan.FromSeconds(TimeoutSeconds),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,*/*;q=0.8");
        return client;
    }

    /// <inheritdoc />
    public string BuildUpdateUrl(OneCConfigType? config, OneCConfigEdition? edition, string? urlOverride,
        string? urlSegment = null)
    {
        if (!string.IsNullOrWhiteSpace(urlOverride))
            return urlOverride.Trim();

        if (config is null)
            return string.Empty;

        // Если у редакции есть собственная переопределённая ссылка — используем её.
        if (edition is { HasUrlOverride: true })
            return edition.UrlOverride.Trim();

        // Персональный сегмент (ник) базы приоритетнее ника типовой конфигурации (issue #322):
        // пользователь видит и правит ключевой кусочек адреса после releases.1c.ru/project/
        // в окне «Связать с конфигурацией», не меняя общую карточку конфигурации.
        var nick = !string.IsNullOrWhiteSpace(urlSegment) ? urlSegment.Trim() : config.Nick;
        return BuildNickUrl(nick);
    }

    /// <summary>
    /// Строит URL каталога релизов ресурса <c>releases.1c.ru/project/<nick></c> — HTML-список
    /// версий; последняя (самая новая) версия находится в первой строке таблицы #versionsTable.
    /// Если ник не задан — корректный URL построить невозможно (старый сегментный путь
    /// <c>downloads.1c.ru/ipp/.../Configs/...</c> более не работает и даёт 404): возвращается
    /// пустая строка, чтобы проверка честно завершилась со статусом Failed.
    /// </summary>
    internal static string BuildNickUrl(string? nick)
    {
        if (string.IsNullOrWhiteSpace(nick))
            return string.Empty;

        // Экранируем явно и возвращаем экранированную строку: Uri.ToString() «разворачивает»
        // %XX-последовательности обратно в читаемые символы, что ломало бы URL с кириллицей
        // или пробелами в нике. Латиница/цифры (AccountingCorp30) EscapeDataString не меняет.
        var nickUrl = $"{ReleasesProjectBaseUrl}/{Uri.EscapeDataString(nick.Trim())}";
        return Uri.TryCreate(nickUrl, UriKind.Absolute, out _) ? nickUrl : string.Empty;
    }

    /// <summary>
    /// Дополняет адрес страницы релизов параметром <c>allUpdates=true#updates</c> (issue #352):
    /// без него releases.1c.ru отдаёт только ПОСЛЕДНИЕ релизы, и цепочки обновлений не
    /// строятся — каталог не видит полной таблицы версий. Идемпотентно: параметр не
    /// дублируется. Разделитель выбирается по наличию уже существующего query.
    /// Internal — для юнит-тестов (OneCUpdatesUrlTests).
    /// </summary>
    internal static string BuildAllUpdatesCatalogUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return string.Empty;

        if (url.Contains("allUpdates=true", StringComparison.OrdinalIgnoreCase))
            return url;

        var separator = url.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return $"{url.TrimEnd('/')}{separator}allUpdates=true#updates";
    }

    /// <inheritdoc />
    public async Task<ConfigUpdateCheckResult> CheckForUpdatesAsync(
        string configName, string currentVersion, string url, CancellationToken ct = default)
    {
        var result = new ConfigUpdateCheckResult
        {
            ConfigName = configName ?? string.Empty,
            CurrentVersion = currentVersion ?? string.Empty,
            Url = url ?? string.Empty,
        };

        if (string.IsNullOrWhiteSpace(url))
        {
            _logger.Warn($"[Updates] Пустой URL (config='{configName}') — проверка не выполнялась.");
            result.Status = ConfigUpdateStatus.Failed;
            result.Error = "Updates.NoUrl";
            return result;
        }

        _logger.Info($"[Updates] Проверка: config='{configName}', url={url}");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await SendWithAuthAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // Каталог не существует — недоступно / неверная ссылка.
                _logger.Warn($"[Updates] Каталог не найден (404): {url}");
                result.Status = ConfigUpdateStatus.Failed;
                result.Error = "Updates.NotFound";
                return result;
            }

            // Пользователь мог попасть на страницу входа portal.1c.ru/login: ресурс releases.1c.ru
            // при отсутствии сессии перенаправляет туда, а программный вход не удался (нет логина
            // в настройках или неверные учётные данные). Показываем понятную ошибку авторизации,
            // а не «каталог доступен, но версия не распарсена».
            var authFailed = _lastLoginResult is PortalLoginResult.AuthFailed or PortalLoginResult.RedirectFailed;
            var isLoginRedirect =
                response.Headers.Location?.Host.Contains("login.1c.ru", StringComparison.OrdinalIgnoreCase) == true ||
                response.RequestMessage?.RequestUri?.Host.Contains("login.1c.ru", StringComparison.OrdinalIgnoreCase) == true;
            if (isLoginRedirect)
            {
                _logger.Warn($"[Updates] Требуется вход на portal.1c.ru (запрос ушёл на {response.RequestMessage?.RequestUri}) для '{url}'");
                result.Status = ConfigUpdateStatus.Failed;
                // Лимит попыток входа исчерпан — отдельная понятная ошибка с советом
                // (issue #334/#330/#323); вход предпринимался и не подтверждён сервером (401) —
                // «не подтверждён» (issue #334); иначе — «требуется вход».
                result.Error = AuthErrorKey(authFailed);
                return result;
            }

            if (!response.IsSuccessStatusCode)
            {
                // Диагностика: фиксируем реальный код ответа сервера (401/403 — нет доступа,
                // 5xx — проблемы на стороне 1С) и конечный URI после возможных редиректов.
                var code = (int)response.StatusCode;
                _logger.Warn($"[Updates] HTTP {code} для '{url}' (requestUri={request.RequestUri})");
                result.Status = ConfigUpdateStatus.Failed;
                // 401/403 и редиректы 3xx (в т.ч. 302 без Location от CAS releases.1c.ru) —
                // понятная ошибка авторизации (неверный/пустой логин-пароль сайта 1С, issue #323);
                // лимит попыток исчерпан — отдельный ключ; если вход предпринимался и не
                // подтверждён — «вход не подтверждён (401)»; остальные коды — техническая диагностика.
                result.Error = code is 401 or 403 or (>= 300 and < 400)
                    ? AuthErrorKey(authFailed)
                    : $"HTTP {code}";
                return result;
            }

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            // Страница входа может прийти с HTTP 200: CAS возвращает форму (поля execution/lt)
            // вместо редиректа, когда сессия не установлена (фантомный успех, issue #330).
            // Распознаём по содержимому и показываем понятную ошибку авторизации, а не
            // «каталог доступен, но версия не распарсена» (issue #323/#334).
            if (LooksLikeLoginForm(body))
            {
                _logger.Warn($"[Updates] Получена страница входа вместо содержимого каталога ({url}).");
                result.Status = ConfigUpdateStatus.Failed;
                result.Error = AuthErrorKey(authFailed);
                return result;
            }

            // Формат ответа определяется по URL:
            //   - project/<nick>            → HTML-список версий (#versionsTable), парсим из него;
            //   - version_files?nick=&ver=  → JSON/HTML со списком файлов релиза (прежний парсер);
            //   - прочее                    → устаревший HTML-каталог (ссылки на архивы).
            var isProject = url.Contains("/project/", StringComparison.OrdinalIgnoreCase);
            var isVersionFiles = url.Contains("version_files", StringComparison.OrdinalIgnoreCase);
            var latest = isProject
                ? ParseLatestVersionFromProjectHtml(body)
                : isVersionFiles
                    ? ParseLatestVersionFromJson(body)
                    : ParseLatestVersion(body);
            result.LatestVersion = latest;

            if (string.IsNullOrWhiteSpace(latest))
            {
                // Каталог доступен, но точную последнюю версию распарсить не удалось.
                _logger.Warn($"[Updates] Каталог доступен, но версия не распарсена (len={body.Length}): {url}");
                result.Status = ConfigUpdateStatus.Unavailable;
                return result;
            }

            result.Status = IsNewer(latest, result.CurrentVersion)
                ? ConfigUpdateStatus.NewerAvailable
                : ConfigUpdateStatus.UpToDate;
            return result;
        }
        catch (OperationCanceledException)
        {
            result.Status = ConfigUpdateStatus.Failed;
            result.Error = "Updates.Cancelled";
            return result;
        }
        catch (Exception ex)
        {
            // Диагностика: фиксируем точный тип/сообщение исключения, чтобы отличить таймаут,
            // ошибку построения URI при редиректе, DNS/TLS и т.п. UI не роняем — возвращаем Failed.
            _logger.Error($"[Updates] Ошибка сети/парсинга для '{url}': {ex.GetType().Name}: {ex.Message}", ex);
            result.Status = ConfigUpdateStatus.Failed;
            result.Error = "Updates.NetworkError";
            return result;
        }
    }

    /// <summary>
    /// Устойчиво ищет в HTML-странице каталога ссылки на архивы дистрибутивов вида
    /// <c>*setup*.zip</c> и возвращает максимальную версию, извлечённую из имён файлов.
    /// При невозможности распарсить ни одну версию возвращает пустую строку.
    /// </summary>
    internal static string ParseLatestVersion(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return string.Empty;

        Version? best = null;
        string bestText = string.Empty;

        foreach (Match match in ArchiveLinkRegex.Matches(html))
        {
            var fileName = match.Groups["url"].Value;
            var version = ExtractVersionFromFileName(fileName);
            if (string.IsNullOrWhiteSpace(version))
                continue;

            if (!TryParseVersion(version, out var parsed))
                continue;

            if (best is null || parsed > best)
            {
                best = parsed;
                bestText = version;
            }
        }

        return bestText;
    }

    /// <summary>Регулярное выражение для ссылки на страницу файлов релиза вида
    /// <c>/version_files?nick=…&ver=…</c> (текст ссылки — номер версии).</summary>
    private static readonly Regex VersionFilesLinkRegex =
        new(@"href\s*=\s*[""'][^""']*version_files[^""']*ver\s*=[^""']*[""'][^>]*>\s*(?<ver>[^<]+?)</a>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

    /// <summary>
    /// Извлекает последнюю (самую новую) версию из HTML-страницы <c>releases.1c.ru/project/<nick></c>.
    /// Ищет таблицу <c>id="versionsTable"</c> и в её первой строке <c><tr></c> первый элемент
    /// <c><a href="/version_files?nick=…&ver=…">ВЕРСИЯ</a></c>. Устойчив к пробелам и
    /// переносам строк. Если таблица не найдена — как запасной вариант ищет первую ссылку
    /// <c>version_files?...&ver=</c> во всём HTML. Возвращает найденную версию или пустую строку.
    /// </summary>
    internal static string ParseLatestVersionFromProjectHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return string.Empty;

        // 1) Основной путь: таблица #versionsTable, первая строка <tr>, первая ссылка version_files.
        var tableMatch = Regex.Match(html,
            @"<table[^>]*id\s*=\s*[""']versionsTable[""'][^>]*>(?<table>.*?)</table>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (tableMatch.Success)
        {
            var table = tableMatch.Groups["table"].Value;
            var firstRow = Regex.Match(table, @"<tr[^>]*>(?<row>.*?)</tr>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (firstRow.Success)
            {
                var row = firstRow.Groups["row"].Value;
                var link = VersionFilesLinkRegex.Match(row);
                if (link.Success)
                {
                    var ver = WebUtility.HtmlDecode(link.Groups["ver"].Value.Trim());
                    if (!string.IsNullOrWhiteSpace(ver))
                        return ver;
                }
            }
        }

        // 2) Запасной путь: регресс к поиску первой ссылки version_files во всём HTML.
        var fallback = VersionFilesLinkRegex.Match(html);
        if (fallback.Success)
        {
            var ver = WebUtility.HtmlDecode(fallback.Groups["ver"].Value.Trim());
            if (!string.IsNullOrWhiteSpace(ver))
                return ver;
        }

        return string.Empty;
    }

    /// <summary>Регулярное выражение для извлечения 3–4-частных номеров версий из текста (JSON и пр.).</summary>
    private static readonly Regex VersionTokenRegex =
        new(@"\b(?<v>\d{1,4}(\.\d{1,4}){1,3})\b", RegexOptions.Compiled);

    /// <summary>
    /// Извлекает из JSON-ответа ресурса <c>releases.1c.ru/version_files</c> все кандидаты версий
    /// и возвращает максимальную. Точная схема ответа неизвестна, поэтому поиск ведётся по числовым
    /// токенам вида <c>3.0.206.19</c> (устойчиво к структуре JSON и изменению полей). При невозможности
    /// распарсить ни одну версию возвращает пустую строку.
    /// </summary>
    private static string ParseLatestVersionFromJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return string.Empty;

        Version? best = null;
        string bestText = string.Empty;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match match in VersionTokenRegex.Matches(json))
        {
            var candidate = match.Groups["v"].Value;
            if (!seen.Add(candidate))
                continue;
            if (!TryParseVersion(candidate, out var parsed))
                continue;

            if (best is null || parsed > best)
            {
                best = parsed;
                bestText = candidate;
            }
        }

        return bestText;
    }

    /// <summary>
    /// Извлекает подстроку версии из имени файла дистрибутива (устойчиво к префиксам).
    /// Пропускает цифровые группы без точек («1c», «setup_2…», префиксы) и возвращает
    /// первую группу вида «3.0.13.7» (суффиксы «.zip»/«.rar» отбрасываются).
    /// </summary>
    private static string ExtractVersionFromFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return string.Empty;

        var i = 0;
        while (i < fileName.Length)
        {
            while (i < fileName.Length && !char.IsDigit(fileName[i]))
                i++;
            if (i >= fileName.Length)
                break;

            var start = i;
            var end = fileName.Length;
            for (var j = start; j < fileName.Length; j++)
            {
                var c = fileName[j];
                if (c is ' ' or '_' or '-' or '+' or '\\' or '/')
                {
                    end = j;
                    break;
                }
            }

            var candidate = fileName.Substring(start, end - start);
            // Отбрасываем хвостовые суффиксы вида «.zip»/«.rar»/«setup» и прочие нечисловые хвосты:
            // версия обычно выглядит как «3.0.142.32», и суффикс не должен мешать парсингу.
            var lastDot = candidate.LastIndexOf('.');
            if (lastDot > 0 && lastDot < candidate.Length - 1)
            {
                var tail = candidate.Substring(lastDot + 1);
                if (tail.Length > 0 && !char.IsDigit(tail[0]))
                    candidate = candidate.Substring(0, lastDot);
            }
            if (candidate.Contains('.'))
                return candidate;

            i = end;
        }

        return string.Empty;
    }

    /// <summary>
    /// Разбирает строку как 3–5-частную версию <see cref="Version"/>. При невозможности
    /// распарсить возвращает false. Хвостовые суффиксы после «+» обрезаются.
    /// </summary>
    internal static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var value = text.Trim();
        var plus = value.IndexOf('+');
        if (plus >= 0)
            value = value.Substring(0, plus);

        if (!Version.TryParse(value, out var parsed) || parsed is null)
            return false;

        // Version поддерживает до 4 частей; приводим к 4-частному виду для корректного сравнения.
        version = Normalize(parsed);
        return true;
    }

    private static Version Normalize(Version v)
    {
        var major = Math.Max(v.Major, 0);
        var minor = v.Minor < 0 ? 0 : v.Minor;
        var build = v.Build < 0 ? 0 : v.Build;
        var revision = v.Revision < 0 ? 0 : v.Revision;
        return new Version(major, minor, build, revision);
    }

    /// <summary>True, если <paramref name="latestVersion"/> новее <paramref name="currentVersion"/>.</summary>
    internal static bool IsNewer(string? latestVersion, string? currentVersion)
    {
        if (!TryParseVersion(latestVersion, out var latest))
            return false;
        // Пустая текущая версия считается «ниже» любой известной последней.
        if (!TryParseVersion(currentVersion, out var current))
            return true;
        return latest > current;
    }

    /// <summary>Максимальное число переходов по промежуточным страницам при разрешении
    /// конечного адреса дистрибутива (issue #352): каталог релизов → страница файлов
    /// релиза (version_files) → страница скачивания файла → сам файл. Защита от зацикливания.</summary>
    private const int MaxDistributionResolutionDepth = 5;

    /// <summary>Суффикс временного файла загрузки (issue #352, комментарий 7OH от
    /// 2026-10-09): файл качается как <c><имя>.download</c> и лишь по успешном
    /// завершении переименовывается в финальное имя. Прерванная загрузка не оставляет
    /// «недокачанного» файла под финальным именем, а остаток <c>.download</c> при
    /// следующем запуске скачанным не считается. Суффикс <c>.part</c> не используется —
    /// он занят частичными файлами сегментов многопоточной загрузки
    /// (<see cref="ParallelDownloader"/>: <c><имя>.<i>.part</c>).</summary>
    internal const string PartialSuffix = ".download";

    /// <inheritdoc />
    public async Task<string?> DownloadUpdateAsync(
        string url, string targetPath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        try
        {
            var dir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            // issue #352: адрес из окна проверки обновлений — это цепочка страниц, а не сам
            // дистрибутив: каталог релизов (project/<nick>) → страница файлов релиза
            // (version_files) → страница скачивания конкретного файла (её URL тоже может
            // заканчиваться на «.zip»). Каждый HTML-ответ анализируем и переходим по найденной
            // ссылке, пока не получим бинарный контент дистрибутива; сохраняем только его.
            var currentUrl = url;
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (var depth = 0; depth < MaxDistributionResolutionDepth; depth++)
            {
                if (!visited.Add(currentUrl.TrimEnd('/')))
                {
                    _logger.Warn($"[Updates] Циклические переходы при поиске дистрибутива: {currentUrl}");
                    return null;
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, currentUrl);
                using var response = await SendWithAuthAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.Warn($"[Updates] HTTP {(int)response.StatusCode} при загрузке обновления: {currentUrl}");
                    return null;
                }

                var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
                var isHtml = contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase)
                             || contentType.Contains("application/xhtml", StringComparison.OrdinalIgnoreCase);
                var isVersionFiles = currentUrl.Contains("version_files", StringComparison.OrdinalIgnoreCase);

                if (!isHtml && !isVersionFiles)
                {
                    // Бинарный контент — это дистрибутив. Расширение итогового файла приводим
                    // к расширению конечного адреса: диалог сохранения предлагает «.zip», а
                    // дистрибутив может оказаться «.rar»/«.exe» и т.п.
                    var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? currentUrl;
                    targetPath = AdjustTargetExtension(targetPath, finalUrl);
                    return await SaveResponseToFileAsync(response, targetPath, progress, ct).ConfigureAwait(false);
                }

                // HTML-страница (или JSON-список файлов релиза): находим следующую ссылку
                // на пути к дистрибутиву и повторяем запрос.
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (LooksLikeLoginForm(body))
                {
                    _logger.Warn($"[Updates] Получена страница входа portal.1c.ru вместо дистрибутива: {currentUrl}");
                    return null;
                }

                var next = SelectNextDistributionUrl(body, currentUrl, visited);
                if (string.IsNullOrWhiteSpace(next))
                {
                    // Файловый эндпоинт (transfer_file/additional_file) отдал сам файл,
                    // пометив его HTML-типом контента и без ссылки на следующую страницу
                    // (issue #352, комментарий 7OH от 2026-10-09: additional_file для
                    // Trade110\RasshirenieGISMTsRPT.cf). Такой ответ сохраняем как файл.
                    // Настоящую HTML-страницу (документ) без ссылки на файл не сохраняем —
                    // это ошибка портала, а не дистрибутив.
                    if (IsFileEndpointUrl(currentUrl) && !LooksLikeHtmlDocument(body))
                    {
                        var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? currentUrl;
                        targetPath = AdjustTargetExtension(targetPath, finalUrl);
                        return await SaveResponseToFileAsync(response, targetPath, progress, ct)
                            .ConfigureAwait(false);
                    }

                    _logger.Warn($"[Updates] В ответе не найдена ссылка на дистрибутив: {currentUrl}");
                    return null;
                }

                currentUrl = ResolveUrl(currentUrl, next);
            }

            _logger.Warn($"[Updates] Превышен предел переходов ({MaxDistributionResolutionDepth}) при поиске дистрибутива: {url}");
            return null;
        }
        catch (OperationCanceledException)
        {
            TryDelete(targetPath + PartialSuffix);
            TryDelete(targetPath);
            return null;
        }
        catch (Exception ex)
        {
            _logger.Warn($"[Updates] Ошибка загрузки обновления: {ex.GetType().Name}: {ex.Message}");
            TryDelete(targetPath + PartialSuffix);
            TryDelete(targetPath);
            return null;
        }
    }

    /// <summary>True, если адрес — конечный эндпоинт передачи файла портала
    /// (<c>transfer_file?…</c>/<c>additional_file?…</c>): его ответ считается самим файлом,
    /// а не страницей с дальнейшей ссылкой (issue #352, комментарий 7OH от 2026-10-09:
    /// additional_file отдаёт расширение конфигурации напрямую). Internal — для юнит-тестов.</summary>
    internal static bool IsFileEndpointUrl(string? url)
        => !string.IsNullOrWhiteSpace(url) &&
           (url.Contains("transfer_file?", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("additional_file?", StringComparison.OrdinalIgnoreCase));

    /// <summary>True, если текст ответа выглядит настоящим HTML-документом (начинается с
    /// <c><!DOCTYPE html></c>/<c><html></c>/<c><?xml</c>): такой ответ файлом
    /// дистрибутива быть не может (issue #352). Binary-контент, ошибочно помеченный сервером
    /// как text/html, документом не является и сохраняется как файл.</summary>
    private static bool LooksLikeHtmlDocument(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return false;

        var trimmed = body.TrimStart();
        return trimmed.StartsWith("<!doctype", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Сохраняет уже полученный (бинарный) ответ в файл с индикацией прогресса.
    /// Сначала ответ пишется во временный файл <c><имя>.download</c>, и лишь по
    /// успешном завершении атомарно переименовывается в финальное имя (issue #352,
    /// комментарий 7OH от 2026-10-09): прерванная загрузка не оставляет «недокачанного»
    /// файла под финальным именем, который при повторном запуске считался бы скачанным.
    /// При сбое временный файл удаляется.</summary>
    private static async Task<string?> SaveResponseToFileAsync(
        HttpResponseMessage response, string targetPath, IProgress<double>? progress, CancellationToken ct)
    {
        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
        var partPath = targetPath + PartialSuffix;

        try
        {
            await using (var contentStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var fileStream = new FileStream(partPath, FileMode.Create, FileAccess.Write,
                             FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long readTotal = 0;
                int read;
                while ((read = await contentStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    readTotal += read;
                    if (totalBytes > 0 && progress is not null)
                        progress.Report(Math.Min(1.0, (double)readTotal / totalBytes));
                }
            }

            // Атомарная публикация: переименовываем временный файл в финальное имя
            // (перезапись существующего файла — прежнее поведение одиночного скачивания).
            File.Move(partPath, targetPath, overwrite: true);
        }
        catch
        {
            TryDelete(partPath);
            throw;
        }

        return File.Exists(targetPath) ? targetPath : null;
    }

    /// <inheritdoc />
    public async Task<string?> DownloadDistributionAsync(
        string url, string targetPath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(targetPath))
            return null;

        try
        {
            var dir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);

                // Проверка свободного места на этом этапе не блокирует загрузку — только
                // предупреждение в журнал; блокирующий вопрос пользователю — этап 0.3.9.214.
                LogFreeSpaceWarning(dir, expectedSizeBytes: 0);
            }

            // issue #334.2: ссылка с новой разметки портала — эндпоинт `version_file?…`
            // (единственное число): это HTML-страница-посредник, а не сам дистрибутив.
            // Многопоточная загрузка «как файла» сохраняла страницу/срывалась. Сначала
            // резолвим конечный адрес (страница → кнопка «Скачать дистрибутив» →
            // transfer_file), и только затем качаем.
            var effectiveUrl = url;
            if (IsPageEndpointUrl(url))
            {
                var resolved = await ResolveDistributionUrlAsync(url, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(resolved) &&
                    !string.Equals(resolved, url, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.Info($"[Platform] Промежуточная страница разрешилась в файл: {url} -> {resolved}");
                    effectiveUrl = resolved;
                }
                else
                {
                    _logger.Warn($"[Platform] Страница-посредник не разрешилась в файл: {url}");
                }
            }

            _logger.Info($"[Platform] Загрузка дистрибутива: {effectiveUrl} -> {targetPath}");

            // 1) Многопоточная загрузка: отдельный HttpClient с авторизацией портала
            //    (Basic Auth + общий CookieContainer) и AllowAutoRedirect=true для CDN;
            //    клиент создаётся только на время операции и освобождается после неё.
            using (var parallelClient = CreateParallelClient())
            {
                var parallelResult = await ParallelDownloader.TryDownloadAsync(
                        parallelClient, effectiveUrl, targetPath,
                        percent => progress?.Report(Math.Clamp(percent / 100.0, 0.0, 1.0)),
                        expectedSize: 0, ct)
                    .ConfigureAwait(false);

                if (parallelResult is not null)
                {
                    // Защита (issue #334.2): многопоточный «файл», оказавшийся HTML-документом
                    // (ссылка вела на страницу, а сервер не поддержал Range/резолвинг не сработал),
                    // не принимается — файл удаляется, выполняется однопоточный резолвинг.
                    if (LooksLikeHtmlFile(parallelResult))
                    {
                        _logger.Warn(
                            $"[Platform] Многопоточная загрузка сохранила HTML-страницу вместо дистрибутива: " +
                            $"{effectiveUrl} ({parallelResult}) — файл удалён, повтор однопоточным резолвингом.");
                        TryDelete(parallelResult);
                        TryDelete(parallelResult + ".etag");
                    }
                    else
                    {
                        _logger.Info($"[Platform] Дистрибутив загружен многопоточно: {targetPath}");
                        // Метка докачки для разового временного файла не нужна — очищаем.
                        TryDelete(targetPath + ".etag");
                        return parallelResult;
                    }
                }
            }

            // 2) Fallback: существующий однопоточный путь с авторизацией
            //    (SendWithAuthAsync + ReadAsStream) — тот же прогресс и удаление файла
            //    при ошибке/отмене.
            _logger.Info("[Platform] Многопоточная загрузка недоступна (мал файл/нет Range/сбой) — однопоточная.");
            return await DownloadUpdateAsync(effectiveUrl, targetPath, progress, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryDelete(targetPath);
            return null;
        }
        catch
        {
            TryDelete(targetPath);
            return null;
        }
    }

    /// <summary>True, если адрес — промежуточная HTML-страница портала, а не конечный
    /// файл дистрибутива (issue #334.2): <c>version_file?…</c> (страница скачивания
    /// одного файла с новой разметки) и <c>version_files?…</c> (список файлов релиза).
    /// Файловые эндпоинты (<c>transfer_file?…</c>/<c>additional_file?…</c>, см.
    /// <see cref="IsFileEndpointUrl"/>) и прямые ссылки на архивы страницами не являются.
    /// Internal — для юнит-тестов.</summary>
    internal static bool IsPageEndpointUrl(string? url)
        => !string.IsNullOrWhiteSpace(url) &&
           (url.Contains("version_file?", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("version_files?", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Резолвит конечный URL дистрибутива по цепочке промежуточных страниц (issue #334.2):
    /// переиспользует логику <see cref="DownloadUpdateAsync"/> до шага сохранения. Бинарный
    /// ответ (или файловый эндпоинт, отдавший сам файл) даёт финальный адрес; HTML-страница
    /// ведёт к следующей ссылке; страница входа/цикл/предел переходов — null. Ошибки сети
    /// не бросают исключение — возвращается null.
    /// </summary>
    internal async Task<string?> ResolveDistributionUrlAsync(string url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        var currentUrl = url;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var depth = 0; depth < MaxDistributionResolutionDepth; depth++)
        {
            if (!visited.Add(currentUrl.TrimEnd('/')))
            {
                _logger.Warn($"[Platform] Циклические переходы при резолвинге дистрибутива: {currentUrl}");
                return null;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, currentUrl);
            using var response = await SendWithAuthAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            var isHtml = contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase)
                         || contentType.Contains("application/xhtml", StringComparison.OrdinalIgnoreCase);
            var isVersionFiles = currentUrl.Contains("version_files", StringComparison.OrdinalIgnoreCase);

            if (!isHtml && !isVersionFiles)
                return response.RequestMessage?.RequestUri?.ToString() ?? currentUrl;

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (LooksLikeLoginForm(body))
            {
                _logger.Warn($"[Platform] Страница входа portal.1c.ru вместо дистрибутива: {currentUrl}");
                return null;
            }

            var next = SelectNextDistributionUrl(body, currentUrl, visited);
            if (string.IsNullOrWhiteSpace(next))
            {
                // Файловый эндпоинт отдал сам файл, пометив его HTML-типом контента.
                return IsFileEndpointUrl(currentUrl) && !LooksLikeHtmlDocument(body)
                    ? currentUrl
                    : null;
            }

            currentUrl = ResolveUrl(currentUrl, next);
        }

        _logger.Warn($"[Platform] Превышен предел переходов ({MaxDistributionResolutionDepth}) при резолвинге дистрибутива: {url}");
        return null;
    }

    /// <summary>
    /// Получает варианты файлов релиза для одиночного скачивания (issue #352.1).
    /// Из ссылки <c>additional_file?nick=…&ver=…</c> (или <c>version_file?…</c>) извлекаются
    /// nick/ver, строится адрес страницы файлов версии и с неё собираются кандидаты:
    /// подписи «Дистрибутив обновления» (приоритет) и «Полный дистрибутив»; каждый кандидат
    /// резолвится до конечного адреса. Без nick/ver (страница каталога) — пустой список.
    /// </summary>
    public async Task<IReadOnlyList<UpdateFileChoice>> GetReleaseFileChoicesAsync(
        string url, CancellationToken ct = default)
    {
        var result = new List<UpdateFileChoice>();
        if (string.IsNullOrWhiteSpace(url))
            return result;

        try
        {
            var (nick, ver) = ExtractNickAndVersion(url);
            if (string.IsNullOrWhiteSpace(nick) || string.IsNullOrWhiteSpace(ver))
            {
                _logger.Info($"[Updates] Файлы релиза не запрошены: в ссылке нет nick/ver ({url}).");
                return result;
            }

            var versionFilesUrl = ToAbsoluteVersionFilesUrl(
                $"/version_files?nick={Uri.EscapeDataString(nick)}&ver={Uri.EscapeDataString(ver)}", url);
            var body = await GetPageTextAsync(versionFilesUrl, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body))
            {
                _logger.Warn($"[Updates] Страница файлов версии недоступна: {versionFilesUrl}");
                return result;
            }

            var candidates = ParseReleaseFileLinks(body, versionFilesUrl);
            foreach (var (captionKey, pageUrl, fileName) in candidates)
            {
                var resolved = await ResolveDistributionUrlAsync(pageUrl, ct).ConfigureAwait(false);
                var finalUrl = string.IsNullOrWhiteSpace(resolved) ? pageUrl : resolved;
                var name = ExtractFileNameFromUrl(finalUrl);
                if (string.IsNullOrEmpty(name))
                    name = fileName;
                if (string.IsNullOrEmpty(name))
                    name = ExtractFileNameFromUrl(pageUrl);
                result.Add(new UpdateFileChoice(captionKey, finalUrl, name));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn($"[Updates] Ошибка получения вариантов файлов релиза: {ex.GetType().Name}: {ex.Message}");
        }

        return result;
    }

    /// <summary>Извлекает параметры nick и ver из query-части ссылки портала
    /// (additional_file/version_file/version_files). Internal — для юнит-тестов.</summary>
    internal static (string Nick, string Ver) ExtractNickAndVersion(string url)
    {
        var nick = Uri.UnescapeDataString(ExtractQueryValue(url, "nick") ?? string.Empty).Trim();
        var ver = Uri.UnescapeDataString(ExtractQueryValue(url, "ver") ?? string.Empty).Trim();
        return (nick, ver);
    }

    /// <summary>Значение параметра query (без декодирования) или null.</summary>
    private static string? ExtractQueryValue(string url, string name)
    {
        var question = url.IndexOf('?', StringComparison.Ordinal);
        if (question < 0 || question >= url.Length - 1)
            return null;

        var query = url[(question + 1)..];
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            var key = eq < 0 ? pair : pair[..eq];
            if (string.Equals(key.Trim(), name, StringComparison.OrdinalIgnoreCase))
                return eq < 0 ? string.Empty : pair[(eq + 1)..];
        }

        return null;
    }

    /// <summary>Регулярное выражение для ссылок страницы файлов версии
    /// (<c>version_file?…</c> и файловые эндпоинты) с подписью якоря (issue #352.1).</summary>
    private static readonly Regex ReleaseFileLinkRegex = new(
        @"<a\b[^>]*?href\s*=\s*[""'](?<url>[^""']*(?:version_file|transfer_file|additional_file)\?[^""']*)[""'][^>]*>(?<text>[\s\S]{0,300}?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Разбирает страницу файлов версии на кандидаты (issue #352.1): ссылки
    /// <c>version_file?…</c> с подписями «Дистрибутив обновления» (приоритет) /
    /// «Полный дистрибутив»; классификация по подписи, при её отсутствии — по имени
    /// файла (updsetup = обновление). Относительные ссылки дополняются хостом.
    /// Internal — для юнит-тестов.
    /// </summary>
    internal static List<(string CaptionKey, string Url, string FileName)> ParseReleaseFileLinks(
        string body, string baseUrl)
    {
        var result = new List<(string, string, string)>();
        if (string.IsNullOrWhiteSpace(body))
            return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in ReleaseFileLinkRegex.Matches(body))
        {
            var href = WebUtility.HtmlDecode(m.Groups["url"].Value.Trim());
            if (string.IsNullOrWhiteSpace(href) || href.StartsWith("#", StringComparison.Ordinal)
                || href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
                continue;

            var absolute = ToAbsoluteVersionFilesUrl(href, baseUrl);
            if (!seen.Add(absolute))
                continue;

            var caption = GetAnchorPlainText(m.Groups["text"].Value);
            var fileName = ExtractFileNameFromUrl(absolute);
            var captionKey = ClassifyReleaseFile(caption, fileName);
            if (captionKey is null)
                continue; // Ссылка не похожа на дистрибутив (служебные файлы и пр.).

            result.Add((captionKey, absolute, fileName));
        }

        // Приоритет: сначала «Дистрибутив обновления» (это .cf-обновление).
        result.Sort((a, b) => string.Equals(a.Item1, UpdateFileChoice.UpdateDistributionCaptionKey, StringComparison.Ordinal)
            ? -1
            : string.Equals(b.Item1, UpdateFileChoice.UpdateDistributionCaptionKey, StringComparison.Ordinal) ? 1 : 0);
        return result;
    }

    /// <summary>Классифицирует файл релиза по подписи/имени (issue #352.1): null — не
    /// дистрибутив (служебные файлы, отчёты и пр.); иначе ключ подписи варианта.</summary>
    private static string? ClassifyReleaseFile(string caption, string fileName)
    {
        var cap = caption ?? string.Empty;
        var name = fileName ?? string.Empty;
        var isUpdate = cap.Contains("дистрибутив обновления", StringComparison.OrdinalIgnoreCase)
            || cap.Contains("update distribution", StringComparison.OrdinalIgnoreCase)
            || name.Contains("updsetup", StringComparison.OrdinalIgnoreCase);
        var isFull = cap.Contains("полный дистрибутив", StringComparison.OrdinalIgnoreCase)
            || cap.Contains("full distribution", StringComparison.OrdinalIgnoreCase);

        if (isUpdate)
            return UpdateFileChoice.UpdateDistributionCaptionKey;
        if (isFull)
            return UpdateFileChoice.FullDistributionCaptionKey;

        // Без узнаваемой подписи берём только файлы дистрибутивов по расширению:
        // «Полный дистрибутив» может быть подписан иначе, но расширение характерное.
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return ext is ".cf" or ".cfu" or ".zip" or ".rar" or ".7z" or ".arj"
            ? UpdateFileChoice.FullDistributionCaptionKey
            : null;
    }

    /// <summary>Извлекает имя файла из адреса: последний сегмент пути либо значение
    /// параметра path/file в query (разделители «/» и «\» декодированные).
    /// Internal — для юнит-тестов.</summary>
    internal static string ExtractFileNameFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return string.Empty;

        var decoded = Uri.UnescapeDataString(url);

        // Параметр path/file в query (transfer_file?path=Trade\…\file.rar).
        var pathValue = ExtractQueryValue(decoded, "path") ?? ExtractQueryValue(decoded, "file");
        if (!string.IsNullOrWhiteSpace(pathValue))
        {
            var decodedPath = Uri.UnescapeDataString(pathValue);
            var name = TakeLastSegment(decodedPath);
            if (name.Contains('.', StringComparison.Ordinal))
                return name;
        }

        // Последний сегмент адреса до query.
        var withoutQuery = decoded;
        var question = withoutQuery.IndexOf('?', StringComparison.Ordinal);
        if (question >= 0)
            withoutQuery = withoutQuery[..question];
        var candidate = TakeLastSegment(withoutQuery);
        return candidate.Contains('.', StringComparison.Ordinal) ? candidate : string.Empty;
    }

    /// <summary>Последний сегмент пути (разделители «/» и «\»).</summary>
    private static string TakeLastSegment(string path)
    {
        var slash = path.LastIndexOfAny(['/', '\\']);
        return slash >= 0 && slash < path.Length - 1 ? path[(slash + 1)..] : path;
    }

    /// <summary>True, если начало существующего файла выглядит HTML-документом
    /// (<c><!doctype</c>/<c><html</c>/<c><?xml</c>): защищает от приёма
    /// HTML-страницы-посредника как скачанного дистрибутива (issue #334.2).
    /// Internal — для юнит-тестов.</summary>
    internal static bool LooksLikeHtmlFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var buffer = new byte[4096];
            var read = stream.Read(buffer, 0, buffer.Length);
            var text = Encoding.UTF8.GetString(buffer, 0, read).TrimStart();
            return text.StartsWith("<!doctype", StringComparison.OrdinalIgnoreCase) ||
                   text.StartsWith("<html", StringComparison.OrdinalIgnoreCase) ||
                   text.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Создаёт HTTP-клиент для многопоточной загрузки дистрибутива: автоперенаправление
    /// включено (CDN отдаёт финальный адрес редиректом), cookie-контейнер общий с основным
    /// клиентом (сессия портала), Basic Auth — как в <see cref="AddBasicAuth"/> (учётные
    /// данные читаются из настроек на каждый вызов; заголовок Authorization и пароль не
    /// логируются), User-Agent/Accept — как у основного клиента, таймаут дольше (крупные
    /// файлы). Клиент используется только в пределах одной операции загрузки и освобождается.
    /// </summary>
    private HttpClient CreateParallelClient()
    {
        var handler = _handlerOverride ?? new HttpClientHandler
        {
            // Для CDN дистрибутивов 1С автоследование безопаснее: сервер редиректит на
            // хранилище файлов, учётные данные передаются заголовком DefaultRequestHeaders
            // в пределах этой операции (клиент создаётся локально и освобождается).
            AllowAutoRedirect = true,
            CookieContainer = _cookieContainer,
        };

        var client = new HttpClient(handler)
        {
            Timeout = ParallelAttemptTimeout,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,*/*;q=0.8");

        var (login, password) = GetCredentials();
        if (!string.IsNullOrEmpty(login))
        {
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{login}:{password}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
        }

        return client;
    }

    /// <summary>
    /// Выбор стратегии загрузки дистрибутива: многопоточность имеет смысл, когда размер
    /// файла известен и даёт более одного сегмента по <c>MinSegmentBytes</c> (1 МБ), т.е.
    /// файл не меньше 2 МБ. Переиспользует <see cref="ParallelDownloader.CanParallelize"/>.
    /// </summary>
    internal static bool ChooseDownloadStrategy(long totalBytes)
        => ParallelDownloader.CanParallelize(totalBytes, ParallelDownloader.DefaultMaxParallelism);

    /// <summary>
    /// Абсолютный адрес страницы файлов релиза (<c>version_files</c>) из href каталога
    /// (issue #352): относительные ссылки («/version_files?nick=…&ver=…») дополняются
    /// хостом портала, абсолютные и пустые возвращаются без изменений. Запасной базой
    /// для резолва служит адрес страницы каталога.
    /// </summary>
    /// <param name="href">Ссылка из таблицы каталога (может быть относительной).</param>
    /// <param name="fallbackBaseUrl">Адрес страницы каталога (для резолва относительной ссылки).</param>
    internal static string ToAbsoluteVersionFilesUrl(string? href, string? fallbackBaseUrl)
    {
        var url = (href ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(url))
            return fallbackBaseUrl ?? string.Empty;

        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return url;

        if (url.StartsWith("/", StringComparison.Ordinal))
            return $"https://releases.1c.ru{url}";

        if (!string.IsNullOrWhiteSpace(fallbackBaseUrl) &&
            Uri.TryCreate(fallbackBaseUrl, UriKind.Absolute, out var baseUri))
        {
            try
            {
                return new Uri(baseUri, url).ToString();
            }
            catch
            {
                // Оставляем ссылку как есть при некорректном слиянии.
            }
        }

        return url;
    }

    /// <summary>
    /// Формирует имя итогового файла дистрибутива платформы: префикс версии + имя файла
    /// (символы, недопустимые в имени файла, заменяются на '_'). Пустые части пропускаются.
    /// </summary>
    /// <param name="version">Версия платформы, например «8.3.27.2214».</param>
    /// <param name="fileName">Имя файла из каталога, например «8.3.27.2214_x64.zip».</param>
    internal static string BuildTargetFileName(string version, string fileName)
    {
        var versionPart = SanitizeFileName(version);
        var namePart = SanitizeFileName(fileName);
        if (string.IsNullOrWhiteSpace(versionPart))
            return namePart;
        if (string.IsNullOrWhiteSpace(namePart))
            return versionPart;
        return $"{versionPart}_{namePart}";
    }

    /// <summary>Заменяет символы, недопустимые в имени файла, на '_' (пустая строка — пустая).</summary>
    private static string SanitizeFileName(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        return sb.ToString().Trim();
    }

    /// <summary>
    /// Журналирует предупреждение о малом свободном месте перед загрузкой (не блокирует).
    /// Порог: свободно меньше <c>размер файла + 1 ГБ</c> (минимум 1 ГБ при неизвестном размере).
    /// </summary>
    private void LogFreeSpaceWarning(string targetDir, long expectedSizeBytes)
    {
        const long gb = 1024L * 1024 * 1024;
        try
        {
            var info = DiskFreeSpaceHelper.TryGetInfo(targetDir, DiskFreeSpaceHelper.DefaultDriveResolver);
            if (info is null)
                return;

            var requiredBytes = Math.Max(expectedSizeBytes, 0) + gb;
            var requiredGb = (int)Math.Clamp((requiredBytes + gb - 1) / gb, 1, int.MaxValue);
            if (DiskFreeSpaceHelper.IsWarning(info.FreeBytes, requiredGb))
            {
                _logger.Warn(
                    $"[Platform] Мало свободного места на диске {DiskFreeSpaceHelper.ResolveDriveName(targetDir)}: " +
                    $"свободно {DiskFreeSpaceHelper.FormatBytes(info.FreeBytes)}, " +
                    $"требуется не менее {DiskFreeSpaceHelper.FormatBytes(requiredBytes)} ({targetDir}).");
            }
        }
        catch
        {
            // Проверка места никогда не мешает загрузке.
        }
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
            // Игнорируем ошибки удаления временного файла.
        }
    }

    /// <summary>Регулярное выражение для поиска прямых ссылок на дистрибутивы
    /// (<c>*.zip</c>, <c>*.rar</c>, <c>*.7z</c>, <c>*.exe</c>, <c>*.arj</c>, <c>*.cf</c>,
    /// <c>*.cfu</c>) в ответе списка файлов релиза (issue #352).</summary>
    private static readonly Regex DistributionUrlRegex = new(
        @"(?<url>(?:https?://|/)[^""'\s<>]*?\.(?:zip|rar|7z|exe|arj|cf|cfu)(?:[?#][^""'\s<>]*)?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Регулярное выражение для поиска ссылок на страницу файлов релиза
    /// (<c>version_files?nick=…&ver=…</c>) в HTML-ответе каталога релизов (issue #352).</summary>
    private static readonly Regex VersionFilesHrefRegex = new(
        @"href\s*=\s*[""'](?<url>[^""']*version_files[^""']*)[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Регулярное выражение для поиска ссылок на передачу файла дистрибутива
    /// (<c>transfer_file?…</c>) в HTML-ответе страницы скачивания файла (issue #352).</summary>
    private static readonly Regex TransferFileHrefRegex = new(
        @"href\s*=\s*[""'](?<url>[^""']*transfer_file[^""']*)[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Регулярное выражение для якорных ссылок с атрибутом href: подпись ссылки
    /// (текст между тегами) используется как признак кнопки скачивания — на промежуточной
    /// странице скачивания кнопка подписана «Скачать дистрибутив»/«Скачать файл» (issue
    /// #352, комментарий 7OH от 2026-10-08), а её href может не содержать transfer_file
    /// и не заканчиваться расширением дистрибутива.</summary>
    private static readonly Regex AnchorHrefRegex = new(
        @"<a\b[^>]*?href\s*=\s*[""'](?<url>[^""']+)[""'][^>]*>(?<text>[\s\S]{0,300}?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Регулярное выражение для адресов передачи/дополнительных файлов
    /// (<c>transfer_file?…</c>, <c>additional_file?…</c>) в ЛЮБОМ месте ответа страницы
    /// скачивания — не только в атрибуте href: JS-редиректы, onclick-обработчики,
    /// встроенные JSON-конфигурации страницы (issue #352).</summary>
    private static readonly Regex FileEndpointUrlRegex = new(
        @"(?<url>(?:https?://[^\s""'<>]*)?(?:transfer_file|additional_file)\?[^\s""'<>]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Собирает упорядоченный список кандидатов на следующую ссылку на пути к дистрибутиву
    /// из ответа (JSON/HTML) (issue #352). Приоритет: якорные ссылки с подписью скачивания
    /// («Скачать дистрибутив»/«Скачать файл»/Download — кнопка последней страницы) →
    /// <c>setup*.zip</c> → полный <c>*.zip</c> → <c>setup*.exe</c>/<c>*.rar</c>/<c>*.7z</c> →
    /// <c>1cv8.cf</c> → ссылки на передачу файла (<c>transfer_file</c>, в том числе вне
    /// атрибута href) → ссылки на страницы файлов релиза (<c>version_files</c>) — для страниц
    /// каталога релизов. Поиск ведётся регулярными выражениями, устойчивыми к неизвестной
    /// структуре ответа. Internal — для юнит-тестов.
    /// </summary>
    internal static List<string> SelectDistributionCandidates(string body)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(body))
            return result;

        // Кнопка «Скачать дистрибутив»/«Скачать файл» на промежуточной странице скачивания
        // (issue #352): подпись якоря — самый надёжный признак следующей ссылки, даже если
        // href не содержит transfer_file и не является архивом (комментарий 7OH 2026-10-08).
        AddDownloadAnchorCandidates(body, result);

        string? setupZip = null;
        string? fullZip = null;
        string? setupExecutable = null;
        string? cf = null;

        foreach (Match m in DistributionUrlRegex.Matches(body))
        {
            var raw = m.Groups["url"].Value.Trim().Trim('"', '\'', '\\');
            if (string.IsNullOrWhiteSpace(raw) || result.Contains(raw))
                continue;

            var lower = raw.ToLowerInvariant();
            var isZip = lower.EndsWith(".zip") || lower.Contains(".zip?") || lower.Contains(".zip#");
            var isCf = lower.EndsWith(".cf") || lower.Contains(".cf?") || lower.Contains(".cf#");
            if (isZip && lower.Contains("setup"))
                setupZip ??= raw;
            else if (isZip)
                fullZip ??= raw;
            else if (isCf)
                cf ??= raw;
            else if (lower.Contains("setup") &&
                     (lower.EndsWith(".exe") || lower.EndsWith(".rar") ||
                      lower.EndsWith(".7z") || lower.EndsWith(".arj")))
                setupExecutable ??= raw;
        }

        if (setupZip is not null)
            result.Add(setupZip);
        if (fullZip is not null)
            result.Add(fullZip);
        if (setupExecutable is not null)
            result.Add(setupExecutable);
        if (cf is not null)
            result.Add(cf);

        // Страница скачивания конкретного файла: ссылка на передачу самого файла
        // (issue #352: адрес промежуточной страницы может совпадать с именем файла).
        foreach (Match m in TransferFileHrefRegex.Matches(body))
        {
            var raw = m.Groups["url"].Value.Trim().Trim('"', '\'');
            if (!string.IsNullOrWhiteSpace(raw) && !result.Contains(raw))
                result.Add(raw);
        }

        // Адреса transfer_file/additional_file вне атрибута href (JS-редиректы,
        // onclick-обработчики, встроенный JSON страницы) — запасной путь, если кнопка
        // не найдена ни по подписи, ни среди href-ссылок (issue #352, 2026-10-08).
        foreach (Match m in FileEndpointUrlRegex.Matches(body))
        {
            var raw = m.Groups["url"].Value.Trim().Trim('"', '\'');
            if (!string.IsNullOrWhiteSpace(raw) && !result.Contains(raw))
                result.Add(raw);
        }

        // Страница каталога релизов: ссылка на файлы последней (самой новой) версии —
        // первая ссылка version_files в таблице (список отсортирован от новых к старым).
        foreach (Match m in VersionFilesHrefRegex.Matches(body))
        {
            var raw = m.Groups["url"].Value.Trim().Trim('"', '\'');
            if (!string.IsNullOrWhiteSpace(raw) && !result.Contains(raw))
                result.Add(raw);
        }

        return result;
    }

    /// <summary>Добавляет в <paramref name="result"/> href-адреса якорей, подпись которых
    /// означает скачивание файла («Скачать дистрибутив», «Скачать файл», Download) — кнопка
    /// конечного скачивания на промежуточной странице (issue #352). Пустые, «заглушечные»
    /// («#», «javascript:») и уже добавленные адреса пропускаются; HTML-сущности в href
    /// (например «&amp;») декодируются.</summary>
    private static void AddDownloadAnchorCandidates(string body, List<string> result)
    {
        foreach (Match m in AnchorHrefRegex.Matches(body))
        {
            var href = WebUtility.HtmlDecode(m.Groups["url"].Value.Trim());
            if (string.IsNullOrWhiteSpace(href) || result.Contains(href))
                continue;

            var lower = href.ToLowerInvariant();
            if (lower.StartsWith("#") || lower.StartsWith("javascript:"))
                continue;

            var text = GetAnchorPlainText(m.Groups["text"].Value);
            if (!text.Contains("скачать", StringComparison.OrdinalIgnoreCase) &&
                !text.Contains("download", StringComparison.OrdinalIgnoreCase))
                continue;

            result.Add(href);
        }
    }

    /// <summary>Снимает HTML-разметку с текста якоря (внутренние теги заменяются пробелами,
    /// HTML-сущности декодируются) для распознавания подписи кнопки скачивания (issue #352).</summary>
    private static string GetAnchorPlainText(string html)
        => WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]*>", " "));

    /// <summary>
    /// Выбирает следующую ссылку на пути к дистрибутиву: первый кандидат, чей абсолютный
    /// адрес ещё не посещён (защита от зацикливания на странице, ссылающейся на саму себя,
    /// issue #352). При отсутствии подходящих кандидатов возвращает null.
    /// </summary>
    private static string? SelectNextDistributionUrl(string body, string baseUrl, HashSet<string> visited)
    {
        foreach (var candidate in SelectDistributionCandidates(body))
        {
            var absolute = ResolveUrl(baseUrl, candidate).TrimEnd('/');
            if (!visited.Contains(absolute))
                return candidate;
        }

        return null;
    }

    /// <summary>
    /// Приводит расширение имени сохраняемого файла к расширению конечного адреса
    /// дистрибутива (issue #352): диалог сохранения предлагает «.zip», а реальный
    /// дистрибутив может оказаться «.rar»/«.exe»/«.7z» и т.п. Изменение выполняется
    /// только если у адреса есть распознанное расширение дистрибутива и оно отличается
    /// от текущего. Internal — для юнит-тестов.
    /// </summary>
    internal static string AdjustTargetExtension(string targetPath, string? finalUrl)
    {
        var ext = GetDistributionExtension(finalUrl);
        if (string.IsNullOrEmpty(ext))
            return targetPath;

        var current = Path.GetExtension(targetPath);
        return string.Equals(current, ext, StringComparison.OrdinalIgnoreCase)
            ? targetPath
            : Path.ChangeExtension(targetPath, ext);
    }

    /// <summary>Расширение дистрибутива в адресе файла (нижний регистр, с точкой) —
    /// если адрес оканчивается на распознанное расширение дистрибутива; иначе пустая
    /// строка. Если путь адреса не имеет распознанного расширения, проверяется имя файла
    /// в query-параметрах <c>path</c>/<c>file</c>/<c>filename</c> (issue #352: адрес
    /// <c>transfer_file?path=…\file.cf</c> несёт расширение только в query). Фрагмент
    /// адреса игнорируется. Internal — для юнит-тестов.</summary>
    internal static string GetDistributionExtension(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return string.Empty;

        var clean = url.Split('?', '#')[0];
        var ext = Path.GetExtension(clean);
        var known = ext.ToLowerInvariant();
        if (known is ".zip" or ".rar" or ".7z" or ".exe" or ".arj" or ".cf" or ".cfu")
            return known;

        var query = url[(url.IndexOf('?') + 1)..];
        var hash = query.IndexOf('#');
        if (hash >= 0)
            query = query[..hash];

        foreach (var pair in query.Split('&'))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0)
                continue;

            var name = pair[..eq].ToLowerInvariant();
            if (name is not ("path" or "file" or "filename"))
                continue;

            var value = pair[(eq + 1)..];
            var slash = Math.Max(value.LastIndexOf('/'), value.LastIndexOf('\\'));
            var fileName = slash >= 0 ? value[(slash + 1)..] : value;
            var candidate = Path.GetExtension(fileName).ToLowerInvariant();
            if (candidate is ".zip" or ".rar" or ".7z" or ".exe" or ".arj" or ".cf" or ".cfu")
                return candidate;
        }

        return string.Empty;
    }

    /// <summary>Сравнивает два URI без учёта регистра (для распознавания циклических
    /// редиректов на тот же адрес, issue #323).</summary>
    private static bool SameUri(Uri? a, Uri? b)
    {
        if (a is null || b is null)
            return false;
        return string.Equals(a.AbsoluteUri, b.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Преобразует относительную ссылку из списка файлов релиза в абсолютную
    /// относительно <paramref name="baseUrl"/>. Абсолютные ссылки возвращаются без изменений.</summary>
    private static string ResolveUrl(string baseUrl, string direct)
    {
        if (direct.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            direct.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return direct;

        try
        {
            return new Uri(new Uri(baseUrl), direct).ToString();
        }
        catch
        {
            return direct;
        }
    }

    /// <inheritdoc />
    public async Task<string?> GetPageTextAsync(string url, CancellationToken ct = default)
    {
        var page = await FetchPageCoreAsync(url, ct).ConfigureAwait(false);
        if (page.Status != PortalFetchStatus.Ok)
        {
            _logger.Warn($"[Updates] Не удалось получить страницу ({page.Status}): {url}");
            return null;
        }

        return page.Text;
    }

    /// <inheritdoc />
    public async Task<PortalPageResult> FetchPageAsync(string url, CancellationToken ct = default)
        => await FetchPageCoreAsync(url, ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<ConfigUpdateCatalogResult> GetUpdateCatalogAsync(string url, CancellationToken ct = default)
    {
        var page = await FetchPageAsync(url, ct).ConfigureAwait(false);
        if (page.Status != PortalFetchStatus.Ok)
            return CatalogFailure(page.Status);

        var releases = OneCPlatformCatalogParser.ParseVersions(page.Text ?? string.Empty);
        if (releases.Count == 0)
        {
            // Страница получена, но ни одной версии не распознано — структура каталога
            // могла измениться либо пришёл неожиданный контент (issue #352).
            _logger.Warn($"[Updates] Каталог получен, но версии не распознаны: {url}");
            return new ConfigUpdateCatalogResult
            {
                Status = PortalFetchStatus.NetworkError,
                ErrorKey = "Updates.Unavailable",
            };
        }

        return new ConfigUpdateCatalogResult { Status = PortalFetchStatus.Ok, Releases = releases };
    }

    /// <summary>Собирает результат ошибки каталога с ключом локализации «Updates.*» по статусу
    /// (конвенция ключей окна F9, issue #352).</summary>
    private static ConfigUpdateCatalogResult CatalogFailure(PortalFetchStatus status)
    {
        var key = status switch
        {
            PortalFetchStatus.AuthRequired => "Updates.AuthRequired",
            PortalFetchStatus.AuthFailed => "Updates.AuthFailed",
            PortalFetchStatus.LoginLimitReached => "Updates.LoginLimitReached",
            PortalFetchStatus.FormUnavailable => "Updates.FormUnavailable",
            PortalFetchStatus.NotFound => "Updates.NotFound",
            PortalFetchStatus.Cancelled => "Updates.Cancelled",
            _ => "Updates.NetworkError",
        };
        return new ConfigUpdateCatalogResult { Status = status, ErrorKey = key };
    }

    /// <summary>Выполняет авторизованный GET страницы портала и возвращает текст ответа вместе
    /// со статусом: Ok — тело получено; AuthRequired — требуется вход (редирект/страница входа,
    /// а вход не выполнялся либо не настроен); AuthFailed — вход предпринимался, но сервер не
    /// подтвердил учётные данные (401/цепочка на странице входа, issue #334/#330/#323);
    /// NetworkError — сеть/HTTP/пусто; Cancelled — отмена. Исключения наружу не бросаются.</summary>
    private async Task<PortalPageResult> FetchPageCoreAsync(string url, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await SendWithAuthAsync(request, HttpCompletionOption.ResponseContentRead, ct)
                .ConfigureAwait(false);

            var authFailed = _lastLoginResult is PortalLoginResult.AuthFailed or PortalLoginResult.RedirectFailed;
            // Форма входа изменилась радикально (OAuth/JS-челлендж) или не получена — отдельный
            // статус с понятным сообщением (issue #323/#330/#334).
            var formUnavailable = _lastLoginResult == PortalLoginResult.FormUnavailable;

            // Ответ со страницей входа либо редирект на неё — требуется авторизация.
            // Если лимит попыток входа исчерпан — отдельный статус (issue #334/#330/#323);
            // если форма входа недоступна — FormUnavailable; если вход предпринимался и не
            // подтверждён сервером — это именно AuthFailed.
            var isLoginRedirect =
                response.Headers.Location?.Host.Contains("login.1c.ru", StringComparison.OrdinalIgnoreCase) == true ||
                response.RequestMessage?.RequestUri?.Host.Contains("login.1c.ru", StringComparison.OrdinalIgnoreCase) == true;
            if (isLoginRedirect)
                return Page(IsPortalLoginLimitReached() ? PortalFetchStatus.LoginLimitReached
                    : formUnavailable ? PortalFetchStatus.FormUnavailable
                    : authFailed ? PortalFetchStatus.AuthFailed
                    : PortalFetchStatus.AuthRequired);

            if (!response.IsSuccessStatusCode)
            {
                var code = (int)response.StatusCode;
                if (code is 401 or 403 or (>= 300 and < 400))
                    return Page(IsPortalLoginLimitReached() ? PortalFetchStatus.LoginLimitReached
                        : formUnavailable ? PortalFetchStatus.FormUnavailable
                        : authFailed ? PortalFetchStatus.AuthFailed
                        : PortalFetchStatus.AuthRequired);
                return Page(PortalFetchStatus.NetworkError);
            }

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body))
                return Page(PortalFetchStatus.NetworkError);

            // Страница входа может прийти с HTTP 200 (CAS отдаёт форму без сессии,
            // issue #330): распознаём по содержимому вместо парсинга версий.
            if (LooksLikeLoginForm(body))
            {
                _logger.Warn($"[Updates] Получена страница входа вместо содержимого ({url}) — авторизация не выполнена.");
                return Page(IsPortalLoginLimitReached() ? PortalFetchStatus.LoginLimitReached
                    : formUnavailable ? PortalFetchStatus.FormUnavailable
                    : authFailed ? PortalFetchStatus.AuthFailed
                    : PortalFetchStatus.AuthRequired);
            }

            return Page(PortalFetchStatus.Ok, body);
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

    /// <summary>
    /// Отправляет запрос с HTTP Basic Auth и вручную следует перенаправлениям (до
    /// <see cref="MaxRedirects"/> шагов), заново добавляя заголовок Authorization на каждом
    /// переходе. Необходимо, потому что автоперенаправление <c>HttpClientHandler</c> снимает
    /// заголовок Authorization при переходе на другой хост (например, CDN дистрибутивов 1С),
    /// из-за чего авторизованный запрос после редиректа выполнялся бы без учётных данных.
    /// </summary>
    private async Task<HttpResponseMessage> SendWithAuthAsync(
        HttpRequestMessage request,
        HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead,
        CancellationToken ct = default)
    {
        var current = request;

        // Вход на portal.1c.ru в рамках одного вызова ограничен MaxLoginAttemptsPerOperation
        // (issue #323/#330/#334): при повторном 302→login после «успешного» входа выполняется
        // ПОВТОРНЫЙ вход со свежей формой (новый execution/lt) — а не мгновенный AuthRequired,
        // как было при одноразовом флаге loginTried. За пределом повторов (либо при исчерпанном
        // лимите сессии) ответ возвращается как есть, и CheckForUpdatesAsync/FetchPageCoreAsync
        // распознают страницу входа и вернут AuthRequired/AuthFailed/LoginLimitReached.
        var loginTriedCount = 0;
        for (var i = 0; i <= MaxRedirects; i++)
        {
            AddBasicAuth(current);

            var response = await _httpClient.SendAsync(current, completionOption, ct).ConfigureAwait(false);

            var status = (int)response.StatusCode;

            // Гибридная авторизация. Ресурс releases.1c.ru при отсутствии сессии НЕ выдаёт 401,
            // а перенаправляет на login.1c.ru (Spring Security CAS). Поэтому вход запускаем при:
            //   - HTTP 401/403 (возможен Basic-вариант), либо
            //   - редиректе на login.1c.ru (нужна cookie-сессия).
            // Попытки ограничены MaxPortalLoginAttempts на сессию (сброс при смене учётной записи
            // и при успешном входе), при успехе повторяем исходный запрос с сохранёнными cookie.
            var needsLogin =
                (status is 401 or 403) ||
                (response.Headers.Location?.Host.Contains("login.1c.ru", StringComparison.OrdinalIgnoreCase) == true);

            if (needsLogin && loginTriedCount < MaxLoginAttemptsPerOperation && CanAttemptPortalLogin())
            {
                loginTriedCount++;
                // Диагностика (issue #323/#330/#334): причина запуска входа и его результат —
                // по журналу должно быть видно, на каком звене CAS-цепочка рвётся.
                var reason = status is 401 or 403 ? $"http-{status}" : "redirect-login";
                var locText = response.Headers.Location?.ToString() ?? "<нет>";
                LogRedirectInfo($"[Updates] Вход запущен: reason={reason}, url='{current.RequestUri}', location='{locText}'");
                // Передаём полный URL редиректа (login.1c.ru/login?service=...): форма входа
                // получит service= исходного каталога, и CAS после входа вернёт верный адрес.
                // probeUrl (исходный URL операции) позволяет проверить «живость» уже установленной
                // сессии (A-1): если сессия жива — вход не выполняется и лимит не тратится.
                var loginResult = await TryLoginPortalAsync(response.Headers.Location?.ToString(), ct,
                    current.RequestUri?.ToString()).ConfigureAwait(false);
                LogRedirectInfo($"[Updates] Вход запущен: результат={loginResult} (попытка {loginTriedCount}/{MaxLoginAttemptsPerOperation}), " +
                                $"повтор исходного запроса={(loginResult == PortalLoginResult.Success)}");
                if (loginResult == PortalLoginResult.Success)
                {
                    response.Dispose();
                    var rebuilt = new HttpRequestMessage(current.Method, current.RequestUri!);
                    current.Dispose();
                    current = rebuilt;
                    continue;
                }
                // Вход не удался — продолжаем обычную обработку редиректа/ответа ниже.
            }

            // Диагностика (issue #323): каждый редирект логируем с номером шага и Location,
            // чтобы по журналу был виден реальный путь CAS-авторизации portal.1c.ru.
            if (status is >= 300 and < 400)
            {
                var locText = response.Headers.Location?.ToString() ?? "<нет Location>";
                LogRedirectInfo($"[Updates] Редирект {status} (шаг {i}): '{locText}' для '{current.RequestUri}'");
            }

            // releases.1c.ru при отсутствии сессии может вернуть 302 БЕЗ заголовка Location
            // либо 302 на тот же адрес (циклический редирект, issue #323). Это не сбой
            // протокола, а требование авторизации (CAS): пробуем войти и повторить исходный
            // запрос с сохранёнными cookie (попытки ограничены счётчиком сессии).
            var selfRedirect = status is >= 300 and < 400 &&
                               (response.Headers.Location is null ||
                                SameUri(current.RequestUri, response.Headers.Location));
            if (selfRedirect && loginTriedCount < MaxLoginAttemptsPerOperation && CanAttemptPortalLogin())
            {
                loginTriedCount++;
                // Диагностика (issue #323/#330/#334): циклический/пустой редирект — тот же
                // признак требования авторизации, что и прямой редирект на login.1c.ru.
                LogRedirectInfo($"[Updates] Вход запущен: reason={(response.Headers.Location is null ? "redirect-no-location" : "self-redirect")}, url='{current.RequestUri}'");
                // Location отсутствует (302 без заголовка) — форма входа по базовому адресу.
                var loginResult = await TryLoginPortalAsync(null, ct, current.RequestUri?.ToString()).ConfigureAwait(false);
                LogRedirectInfo($"[Updates] Вход запущен: результат={loginResult} (попытка {loginTriedCount}/{MaxLoginAttemptsPerOperation}), " +
                                $"повтор исходного запроса={(loginResult == PortalLoginResult.Success)}");
                if (loginResult == PortalLoginResult.Success)
                {
                    response.Dispose();
                    var rebuilt = new HttpRequestMessage(current.Method, current.RequestUri!);
                    current.Dispose();
                    current = rebuilt;
                    continue;
                }
                _logger.Warn($"[Updates] HTTP {status} без полезного Location для '{current.RequestUri}' — вход на portal.1c.ru не выполнен (проверьте учётные данные ИТС).");
            }

            // Диагностика «фантомного успеха» (issue #323/#330/#334): вход в рамках этой операции
            // выполнялся и завершился «успехом», но повтор исходного запроса СНОВА дал 302 на
            // login.1c.ru — сервер не принял cookie. Повторный вход со свежей формой (новый
            // execution/lt) выполняется выше в ветке needsLogin до MaxLoginAttemptsPerOperation
            // (A-2/A-3); маркер здесь остаётся ТОЛЬКО для случая, когда повторы исчерпаны либо
            // лимит сессии заблокирован — тогда ответ возвращается как есть (ниже) и UI получит
            // честный AuthRequired/AuthFailed/LoginLimitReached.
            if (loginTriedCount > 0 &&
                _lastLoginResult == PortalLoginResult.Success &&
                response.Headers.Location?.Host.Contains("login.1c.ru", StringComparison.OrdinalIgnoreCase) == true)
            {
                _logger.Warn("[Updates] retryAfterLoginStill302=true: после «успешного» входа повтор " +
                             "исходного запроса снова дал 302 на login.1c.ru (фантомный успех); " +
                             "попытки входа в рамках операции исчерпаны " +
                             $"({MaxLoginAttemptsPerOperation}) либо заблокирован лимит сессии.");
            }

            // На страницу входа portal.1c.ru редирект НЕ следуем: если сессии нет, а программный
            // вход не удался, GET формы входа вернёт HTML без версий, и проверка ложно завершится
            // статусом Unavailable («каталог доступен, точная версия не определена»). Возвращаем
            // редирект как есть, а CheckForUpdatesAsync распознает login.1c.ru и покажет ошибку
            // авторизации (issue #323).
            if (response.Headers.Location?.Host.Contains("login.1c.ru", StringComparison.OrdinalIgnoreCase) == true)
                return response;

            if (status is < 300 or >= 400 || response.Headers.Location is null)
                return response;

            // Перенаправление: освобождаем ответ и строим следующий запрос по Location.
            var location = response.Headers.Location;
            response.Dispose();

            var target = location.IsAbsoluteUri
                ? location
                : new Uri(current.RequestUri!, location);
            var next = new HttpRequestMessage(current.Method, target);

            // Переносим только заголовок Authorization (Basic Auth); прочие приватные заголовки
            // для нового хоста берутся из дефолтов клиента (User-Agent, Accept).
            if (current.Headers.Authorization is not null)
                next.Headers.Authorization = current.Headers.Authorization;

            current = next;
        }

        throw new InvalidOperationException("Слишком много перенаправлений при проверке обновлений.");
    }

    /// <summary>
    /// Добавляет HTTP Basic Auth заголовок (<c>Authorization: Basic base64(логин:пароль)</c>)
    /// на основе учётной записи ИТС (issue #333): выбранной в настройках (<see cref="AppSettings.ItsAccountId"/>)
    /// или «Основной» из справочника <see cref="IItsAccountsStore"/>. Если логин не задан — запрос
    /// выполняется без авторизации (обратная совместимость). Учётные данные читаются на каждый
    /// запрос, поэтому смена записи в справочнике не требует перезапуска.
    /// Заголовок Authorization и пароль не логируются.
    /// </summary>
    private void AddBasicAuth(HttpRequestMessage request)
    {
        var (login, password) = GetCredentials();
        if (string.IsNullOrEmpty(login))
            return;

        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{login}:{password}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
    }

    /// <summary>
    /// Возвращает логин/пароль для авторизации на сайте 1С: учётная запись из справочника
    /// <see cref="IItsAccountsStore"/> (выбранная в настройках <see cref="AppSettings.ItsAccountId"/>
    /// или «Основная»), при её отсутствии — устаревшие поля
    /// <see cref="AppSettings.UpdatesLogin"/>/<see cref="AppSettings.UpdatesPassword"/> (миграция
    /// ещё не выполнена либо справочник пуст). Пароль никогда не логируется.
    /// </summary>
    private (string Login, string Password) GetCredentials()
    {
        var settings = _repository.LoadSettings();
        var account = _itsAccounts?.Resolve(settings.ItsAccountId);
        if (account is not null && !string.IsNullOrWhiteSpace(account.Login))
            return (account.Login ?? string.Empty, account.Password ?? string.Empty);

        return (settings.UpdatesLogin ?? string.Empty, settings.UpdatesPassword ?? string.Empty);
    }

    /// <summary>
    /// Программный вход на portal.1c.ru (гибридная авторизация, Spring Security CAS).
    /// Поток (issue #323/#334): GET формы входа (по URL редиректа сервера, если он известен —
    /// так форма получает <c>service=</c> исходного каталога), извлечение скрытого токена
    /// <c>execution</c>, POST логина и доведение до конца цепочки редиректов после входа
    /// (обычно 302 на <c>releases.1c.ru/public/security_check?ticket=ST-…</c> — сессионная
    /// cookie TGC/JSESSIONID выставляется именно при обращении по этому адресу). Cookie
    /// сохраняются в общем <see cref="CookieContainer"/>, поэтому последующие запросы
    /// проходят авторизацию автоматически.
    /// Возвращает true, если вход завершился успешно (финальный ответ вне страницы входа).
    /// </summary>
    /// <param name="loginUrl">Полный URL редиректа с сервера (<c>login.1c.ru/login?service=…</c>)
    /// или null — тогда используется базовый <see cref="PortalLoginUrl"/>.</param>
    /// <param name="probeUrl">Исходный URL операции (каталог), по которому выполняется пробный
    /// GET проверки «живости» уже установленной сессии (A-1, issue #323/#330/#334). Если сессия
    /// жива — полный вход не выполняется и лимит попыток не тратится; null — проверка пропускается.</param>
    private async Task<PortalLoginResult> TryLoginPortalAsync(string? loginUrl, CancellationToken ct, string? probeUrl = null)
    {
        // A-6 (0.3.9.307): инвентаризация cookie до принятия решения — по журналу видно, какая
        // cookie присутствовала в контейнере и почему вход был/не был запущен (issue #323/#330/#334).
        LogRedirectInfo($"[Updates] Вход: cookie контейнера: {DescribeContainerCookies()}");

        // Новая попытка входа обнуляет ранее найденную причину отказа (капча и пр., issue #323):
        // ключ локализации выбирается по причине ПОСЛЕДНЕЙ попытки.
        _lastAuthFailureReason = null;

        // A-1 (0.3.9.307): вместо мгновенного «успеха» по имени cookie (0.3.9.306: ранний выход
        // при HasPortalSessionCookie(), лог 7OH: «результат=Success» без единого GET/POST входа) —
        // честная проверка «живой» сессии пробным GET по целевому URL операции. Имя cookie в
        // контейнере не гарантирует, что сервер примет сессию: после входа в контейнер попадают
        // cookie-заглушки WAF/CDN, которые releases.1c.ru при следующем запросе не принимает
        // (повторный 302 → retryAfterLoginStill302 → AuthRequired). Пробный GET показывает
        // реальную живость сессии; при мёртвой сессии cookie снимаются и выполняется полный вход.
        if (HasPortalSessionCookie())
        {
            var alive = await IsPortalSessionAliveAsync(probeUrl, ct).ConfigureAwait(false);
            if (alive)
            {
                LogRedirectInfo("[Updates] Вход: сессионная cookie жива (пробный GET вернул контент " +
                                "каталога) — полный вход не выполняется, лимит попыток не тратится.");
                _portalLoginAttempts = 0;
                _lastLoginResult = PortalLoginResult.Success;
                LogPortalCookieInventory();
                return PortalLoginResult.Success;
            }

            _logger.Warn("[Updates] Вход: сессионная cookie в контейнере, но пробный GET показал " +
                         "мёртвую сессию — cookie портала удаляются, выполняется полный вход со свежей формой.");
            ClearPortalCookies();
        }

        var (login, password) = GetCredentials();
        if (string.IsNullOrEmpty(login))
        {
            _logger.Warn("[Updates] Для входа на portal.1c.ru не задан логин.");
            _lastLoginResult = PortalLoginResult.NoCredentials;
            return PortalLoginResult.NoCredentials;
        }

        try
        {
            var formUrl = ResolveLoginFormUrl(loginUrl);
            LogRedirectInfo($"[Updates] Вход на portal.1c.ru: учётная запись '{ResolveAccountName()}', " +
                            $"credsPresent={!string.IsNullOrEmpty(login)}, форма: {formUrl}");

            // Шаг 1: GET формы входа — получаем HTML и все скрытые поля Spring Security CAS
            // (execution, lt, CSRF и пр.). Запрос идёт по HTTP/1.1: часть CAS-серверов некорректно
            // обрабатывает HTTP/2 (ответ 401), см. LoginHttpVersion.
            using (var formRequest = new HttpRequestMessage(HttpMethod.Get, formUrl) { Version = LoginHttpVersion })
            using (var formResponse =
                   await _httpClient.SendAsync(formRequest, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false))
            {
                var formStatus = (int)formResponse.StatusCode;
                var html = await formResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var fields = ExtractFormFields(html);
                var hasExecution = fields.TryGetValue("execution", out var execution) && !string.IsNullOrWhiteSpace(execution);
                var hasLt = fields.ContainsKey("lt");
                var formAction = ExtractFormAction(html);
                var fieldNames = fields.Count == 0
                    ? "<нет>"
                    : string.Join(",", fields.Keys.OrderBy(k => k, StringComparer.Ordinal));
                LogRedirectInfo($"[Updates] Вход: GET формы status={formStatus}, execution={(hasExecution ? "есть" : "нет")}, " +
                                $"lt={(hasLt ? "есть" : "нет")}, action='{formAction ?? "<нет>"}', поля: {fieldNames}");

                if (!hasExecution)
                {
                    LogAnonymizedAuthFailure("[Updates] Не удалось извлечь токен 'execution' из формы входа.", html, fields);
                    // Распознавание «протокол изменился» (issue #323/#330/#334): если в форме
                    // нет классических токенов CAS (execution/lt), но есть признаки OAuth/JS-
                    // челленджа — программный вход невозможен в принципе; пользователю нужен
                    // браузер, а не очередная попытка POST.
                    if (LooksLikeOAuthOrChallenge(html))
                    {
                        _logger.Warn("[Updates] Форма входа изменилась (признаки OAuth/JS-челленджа): " +
                                     "автоматический вход временно недоступен, откройте login.1c.ru в браузере.");
                    }
                    _lastLoginResult = PortalLoginResult.FormUnavailable;
                    return PortalLoginResult.FormUnavailable;
                }

                // Шаг 2: POST на АТРИБУТ action формы (issue #323/#330/#334, третья итерация):
                // ранее POST всегда шёл на URL GET-формы, а Spring Security CAS часто указывает
                // отдельный action («/login/cas?service=…») — запрос уходил не туда (401/404).
                // Если action отсутствует/пуст — POST остаётся на адресе формы (прежнее поведение).
                // Набор полей строится ДИНАМИЧЕСКИ из фактической формы (execution, lt, CSRF
                // и пр.) + обязательные username/password/_eventId=submit — жёсткий список
                // 0.3.9.297 отклоняется сервером 401 при изменении формы входа (issue #334).
                // C-2 (0.3.9.313): выбор адреса POST (эталон рабочего кода 1С, комментарий 23
                // issue #323): если action не несёт собственного пути и у GET-формы есть параметр
                // service — POST на ПОЛНЫЙ URL GET-формы с service= (Spring Security CAS выпускает
                // билет для указанного service только при его наличии в POST); иначе — на action.
                // Причина выбора всегда пишется в журнал — по нему видно поведение (критерий 3).
                var (postUrl, postUrlReason) = ResolveFormPostUrl(formUrl, formAction);
                LogRedirectInfo($"[Updates] Вход: POST на '{postUrl}' (выбран: {postUrlReason}; GET-форма: {formUrl})");
                // issue #323 (0.3.9.319, девятая итерация): тело POST приводится к эталону
                // рабочего кода 1С (комментарий 23): динамический набор полей формы +
                // username/password/_eventId=submit + обязательные inviteCode (пусто),
                // geolocation (пусто), submit=Войти, rememberMe=on — если их нет в форме.
                // Без них Spring Security CAS может отвечать 200 кабинетом вместо 302 с билетом.
                var form = BuildLoginPostBody(fields, login, password);
                LogRedirectInfo($"[Updates] Вход: POST поля: {string.Join(",", form.Keys.OrderBy(k => k, StringComparer.Ordinal))}");

                using var postRequest = new HttpRequestMessage(HttpMethod.Post, postUrl) { Version = LoginHttpVersion };
                postRequest.Content = new FormUrlEncodedContent(form);
                postRequest.Content.Headers.ContentType =
                    new MediaTypeHeaderValue("application/x-www-form-urlencoded");
                // Часть CAS-развёртываний проверяет Origin/Referer при POST формы.
                postRequest.Headers.Referrer = new Uri(formUrl);
                postRequest.Headers.TryAddWithoutValidation("Origin", new Uri(formUrl).GetLeftPart(UriPartial.Authority));

                using var postResponse =
                    await _httpClient.SendAsync(postRequest, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

                var postStatus = (int)postResponse.StatusCode;
                var postLocation = postResponse.Headers.Location?.ToString() ?? "<нет>";
                // Cookie из ответа POST явно добавляем в общее хранилище: страховка для
                // кастомных транспортов/тестов с fake-обработчиками (в проде HttpClientHandler
                // уже обрабатывает Set-Cookie; повторное добавление той же cookie безопасно).
                // После этого sessionCookie корректно отражает факт установки сессии.
                ApplySetCookieToContainer(postResponse, postRequest.RequestUri ?? new Uri(postUrl));
                LogRedirectInfo($"[Updates] Вход: POST status={postStatus}, location='{postLocation}', " +
                                $"sessionCookie={HasPortalSessionCookie()}");
                LogRedirectInfo($"[Updates] Вход: POST Set-Cookie: {DescribeSetCookies(postResponse)}");
                // issue #323 (0.3.9.323): по списку имён заголовков ответа видно, приходит ли
                // Location (7OH: «сайт релизов возвращает один заголовок с Большой буквой —
                // а именно Location») и с каким фактическим именем — без гаданий по статусу.
                LogRedirectInfo($"[Updates] Вход: POST заголовки ответа: {DescribeResponseHeaders(postResponse)}");

                // Шаг 3: доводим CAS-цепочку до конца. После успешного входа сервер отвечает
                // 302 на releases.1c.ru/public/security_check?ticket=ST-…; сессионная cookie
                // устанавливается при обращении по этому адресу. Без этого шага повторный
                // запрос каталога снова уходил бы в 302 (issue #323/#334).
                if (postStatus is >= 300 and < 400 && postResponse.Headers.Location is not null)
                {
                    // issue #323 (0.3.9.319): Location после POST — это URL с CAS-билетом
                    // (эталон рабочего кода 1С): GET по нему с cookie login.1c.ru доводит сессию
                    // releases.1c.ru до живой. AllowAutoRedirect=false, поэтому Location читается
                    // из ответа до какого-либо следования.
                    var completed = await FollowLoginRedirectsAsync(
                        postResponse.Headers.Location, ct).ConfigureAwait(false);
                    LogRedirectInfo($"[Updates] Вход: FollowLoginRedirectsAsync={completed}, " +
                                    $"sessionCookie={HasPortalSessionCookie()}");
                    if (completed)
                    {
                        // «Успех цепочки» ещё не гарантирует живую сессию каталога (SESSION
                        // для login.1c.ru ≠ сессии releases.1c.ru) — честная alive-проверка.
                        var alive = await IsPortalSessionAliveAsync(probeUrl, ct).ConfigureAwait(false);
                        LogRedirectInfo($"[Updates] Вход: alive={(alive ? "True" : "False")} после цепочки редиректов POST");
                        if (alive)
                        {
                            LogRedirectInfo("[Updates] Вход на portal.1c.ru выполнен (цепочка редиректов пройдена, сессия жива).");
                            // Успешный вход сбрасывает счётчик попыток (issue #334/#330/#323):
                            // сессия установлена, следующие операции могут входить заново.
                            _portalLoginAttempts = 0;
                            _lastLoginResult = PortalLoginResult.Success;
                            LogPortalCookieInventory();
                            return PortalLoginResult.Success;
                        }
                    }

                    _logger.Warn("[Updates] Вход на portal.1c.ru не подтверждён: цепочка редиректов завершилась на странице входа/без живой сессии.");
                    _lastLoginResult = PortalLoginResult.RedirectFailed;
                    return PortalLoginResult.RedirectFailed;
                }

                // Успех без редиректа: 2xx. ВАЖНО: при неверном логине CAS может вернуть 200
                // с телом формы входа (поля execution/lt, сообщение об ошибке) и БЕЗ сессионной
                // cookie — такой ответ НЕ является успехом («фантомный успех», issue #330):
                // проверяем содержимое тела, наличие Set-Cookie и сессионной cookie, а не
                // только код ответа.
                if (postResponse.IsSuccessStatusCode)
                {
                    var postBody = await ReadBodyQuietlyAsync(postResponse, ct).ConfigureAwait(false);
                    // Расширенная диагностика ветки 2xx (issue #323/#330/#334): contentType,
                    // длина тела, превью (без секретов) и Set-Cookie только именами/флагами.
                    LogPost2xxDiagnostics(postStatus, postResponse, postBody, fields, login, password);

                    // Страница ЛИЧНОГО КАБИНЕТА после POST — вход фактически УСПЕШЕН (issue #323):
                    // сервер принял креды и открыл кабинет («Личные данные», лог 7OH), а не форму
                    // входа с ошибкой. В 0.3.9.313 детектор кабинета вызывается РАНЬШЕ детектора
                    // формы входа (C-1/A-3 плана): страница кабинета содержит execution-форму смены
                    // аккаунта и JS-подсказку «Неверный логин или пароль», из-за которых
                    // LooksLikeLoginForm ложно объявлял AuthFailed (лог 7OH 2026-10-05: status=200,
                    // title='Личные данные'). Кабинет → Success; повтор исходного запроса каталога
                    // штатно выполняется в SendWithAuthAsync после Success; при 302 там сработает
                    // повторный вход.
                    if (DetectPersonalAreaPage(postBody))
                    {
                        LogRedirectInfo("[Updates] Вход на portal.1c.ru выполнен (POST 200: страница личного кабинета), " +
                                        $"sessionCookie={HasPortalSessionCookie()} ({DescribeSessionCookies()}).");
                        // D-1 (0.3.9.316, issue #323, восьмая итерация): POST кабинета «Личные
                        // данные» устанавливает SESSION ТОЛЬКО для login.1c.ru (лог 7OH 0.3.9.313),
                        // а для releases.1c.ru сессия выпускается на звене CAS security_check —
                        // GET на адрес service формы / корень releases.1c.ru (эталон рабочего
                        // кода 1С, комментарий 23 в #323). Без этого звена повтор исходного
                        // запроса каталога снова даёт 302 на login (retryAfterLoginStill302),
                        // и после исчерпания повторов пользователь видит «Требуется вход».
                        // issue #323 (0.3.9.319, девятая итерация): билет CAS приходит через
                        // Location POST (эталон 1С) либо meta-refresh/JS в теле кабинета.
                        // GET по ticket-URL (а не голый security_check) доводит сессию
                        // releases.1c.ru; голый security_check БЕЗ билета остался только
                        // запасным путём (когда билета нет вовсе) — в живом сценарии он не
                        // выполняется (лог 7OH 0.3.9.316: 302 → /error/403).
                        var securityCheckOk = await RunTicketSecurityCheckAsync(
                            postBody, postUrl, formUrl, probeUrl, ct).ConfigureAwait(false);
                        if (securityCheckOk)
                        {
                            _portalLoginAttempts = 0;
                            _lastLoginResult = PortalLoginResult.Success;
                            LogPortalCookieInventory();
                            return PortalLoginResult.Success;
                        }

                        _logger.Warn("[Updates] Вход на portal.1c.ru не подтверждён: POST 200 со страницей " +
                                     "личного кабинета, но билет CAS не получен/звено security_check не " +
                                     "установило живую сессию для releases.1c.ru — повтор исходного запроса " +
                                     "не выполняется (честный AuthFailed, issue #323).");
                        _lastLoginResult = PortalLoginResult.RedirectFailed;
                        return PortalLoginResult.RedirectFailed;
                    }

                    if (LooksLikeLoginForm(postBody))
                    {
                        LogAnonymizedAuthFailure(
                            $"[Updates] Вход на portal.1c.ru не подтверждён (status={postStatus}: в теле форма входа).",
                            postBody, fields);
                        _lastLoginResult = PortalLoginResult.AuthFailed;
                        return PortalLoginResult.AuthFailed;
                    }

                    // Следование JS/meta-refresh-редиректу в теле 2xx (issue #323/#330/#334):
                    // CAS-цепочка часто доводится до releases.1c.ru/public/security_check?ticket=…
                    // именно JS-редиректом, и сессионная cookie выставляется на этом звене.
                    var bodyRedirect = ExtractBodyRedirectUrl(postBody);
                    if (bodyRedirect is not null)
                    {
                        LogRedirectInfo($"[Updates] Вход: в теле 2xx найден JS/meta-refresh редирект на '{bodyRedirect}' — следуем.");
                        var target = ResolveBodyRedirectTarget(postUrl, bodyRedirect);
                        if (target is not null)
                        {
                            var jsCompleted = await FollowLoginRedirectsAsync(target, ct).ConfigureAwait(false);
                            LogRedirectInfo($"[Updates] Вход: FollowLoginRedirectsAsync(js)={jsCompleted}, " +
                                            $"sessionCookie={HasPortalSessionCookie()}");
                            if (jsCompleted)
                            {
                                LogRedirectInfo("[Updates] Вход на portal.1c.ru выполнен (JS/meta-refresh цепочка пройдена).");
                                _portalLoginAttempts = 0;
                                _lastLoginResult = PortalLoginResult.Success;
                                LogPortalCookieInventory();
                                return PortalLoginResult.Success;
                            }
                        }
                    }

                    // «Фантомный успех» (issue #330): сервер вернул 200, но сессионная cookie
                    // НЕ установлена и в ответе нет ни одного Set-Cookie — вход фактически не
                    // выполнен. Такой ответ успехом больше НЕ считается (раньше 2xx + тело без
                    // формы входа проходило как Success, и следующий запрос каталога снова давал
                    // 302 → повторный вход → исчерпание лимита за одну операцию, лог 7OH).
                    var hasSession = HasPortalSessionCookie();
                    var hasSetCookie = HasSetCookieHeader(postResponse);
                    // Диагностика (issue #323): какая именно «сессионная» cookie стоит после
                    // POST — SESSION Domain=login.1c.ru (сессия страницы входа) НЕ означает
                    // авторизованную сессию каталога (там домен .1c.ru / releases.1c.ru).
                    if (hasSession)
                        LogRedirectInfo($"[Updates] Вход: POST session-cookie: {DescribeSessionCookies()}");
                    if (!hasSession && !hasSetCookie)
                    {
                        _logger.Warn("[Updates] Вход на portal.1c.ru не подтверждён: сервер вернул 200 " +
                                     "без установки сессии и без Set-Cookie (фантомный успех) — вход " +
                                     "не засчитан, повтор исходного запроса и лимит попыток не тратятся.");
                        _lastLoginResult = PortalLoginResult.AuthFailed;
                        return PortalLoginResult.AuthFailed;
                    }

                    LogRedirectInfo($"[Updates] Вход на portal.1c.ru выполнен (status={postStatus}), " +
                                    $"sessionCookie={hasSession}, setCookie={hasSetCookie}.");
                    _portalLoginAttempts = 0;
                    _lastLoginResult = PortalLoginResult.Success;
                    LogPortalCookieInventory();
                    return PortalLoginResult.Success;
                }

                // 401 либо 200 с формой ошибки — читаем тело и логируем анонимизированные
                // признаки (без пароля/логина/значений токенов), чтобы отличить «неверный
                // пароль» от «изменилась форма» от «требуется капча» (issue #334).
                var body = postStatus is 200 or 401
                    ? await ReadBodyQuietlyAsync(postResponse, ct).ConfigureAwait(false)
                    : string.Empty;
                LogAnonymizedAuthFailure($"[Updates] Вход на portal.1c.ru не подтверждён (status={postStatus}).", body, fields);
                LogRedirectInfo($"[Updates] Вход: итог=AuthFailed, status={postStatus}");
                _lastLoginResult = PortalLoginResult.AuthFailed;
                return PortalLoginResult.AuthFailed;
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"[Updates] Ошибка входа на portal.1c.ru: {ex.GetType().Name}: {ex.Message}");
            _lastLoginResult = PortalLoginResult.FormUnavailable;
            return PortalLoginResult.FormUnavailable;
        }
    }

    /// <summary>
    /// Звено CAS <c>security_check</c> после «кабинетного» успеха POST входа (issue #323,
    /// восьмая итерация). POST кабинета («Личные данные», лог 7OH 0.3.9.313) НЕ устанавливает
    /// сессионную cookie для releases.1c.ru — SESSION выпускается только для login.1c.ru,
    /// и повтор исходного запроса каталога снова уходит в 302 на login
    /// (<c>retryAfterLoginStill302</c> → AuthRequired). Эталон — рабочий код 1С (комментарий 23
    /// в #323): после POST формы выполняется GET корня <c>releases.1c.ru</c> (или
    /// <c>public/security_check</c>), который выдаёт Set-Cookie SESSION/JSESSIONID для хоста
    /// releases.1c.ru (возможно через цепочку редиректов); ТОЛЬКО затем повторяется исходный
    /// запрос. URL звена берётся из параметра <c>service</c> GET-формы входа (в логе 7OH —
    /// <c>https://releases.1c.ru/public/security_check</c>), иначе — корень releases.1c.ru.
    /// Все Set-Cookie звена сохраняются в общий контейнер через
    /// <see cref="ApplySetCookieToContainer"/> (важно и для тестов с fake-обработчиками).
    /// Возвращает true, если после звена пробная проверка живой сессии
    /// (<see cref="IsPortalSessionAliveAsync"/>) вернула alive=True; иначе — false (звено
    /// недоступно/не отдало cookie/сессия мертва — вызывающий возвращает честный AuthFailed).
    /// </summary>
    private async Task<bool> RunSecurityCheckAsync(string? formUrl, string? probeUrl, CancellationToken ct)
    {
        var target = ResolveSecurityCheckTarget(formUrl);
        var source = ExtractSecurityCheckService(formUrl) is not null
            ? "service GET-формы"
            : "корень releases.1c.ru (эталон кода 1С)";
        LogRedirectInfo($"[Updates] Вход: security_check '{target}' (источник: {source})");

        var current = target;
        var finished = false;
        for (var i = 0; i <= MaxRedirects; i++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                using var response =
                    await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
                ApplySetCookieToContainer(response, request.RequestUri ?? current);

                var status = (int)response.StatusCode;
                var setCookie = DescribeSetCookies(response);
                LogRedirectInfo($"[Updates] Вход: security_check '{current}' => status={status}, setCookie={setCookie}");

                if (status is >= 300 and < 400 && response.Headers.Location is not null)
                {
                    var next = response.Headers.Location;
                    var nextTarget = next.IsAbsoluteUri ? next : new Uri(current, next);
                    // Редирект на страницу входа — билет не принят, звено не отработало:
                    // дальше по login-цепочке идти бессмысленно (результат заведомо ложный).
                    if (nextTarget.Host.Contains("login.1c.ru", StringComparison.OrdinalIgnoreCase))
                    {
                        LogRedirectInfo($"[Updates] Вход: security_check завершился редиректом на login.1c.ru ({status} '{next}') — звено не отработало.");
                        finished = false;
                        break;
                    }

                    LogRedirectInfo($"[Updates] Вход: security_check редирект {status} '{next}' для '{current}'");
                    current = nextTarget;
                    continue;
                }

                var body = await ReadBodyQuietlyAsync(response, ct).ConfigureAwait(false);
                var finalHost = response.RequestMessage?.RequestUri?.Host ?? current.Host;
                finished = status is >= 200 and < 300 &&
                           !finalHost.Contains("login.1c.ru", StringComparison.OrdinalIgnoreCase) &&
                           !LooksLikeLoginForm(body);
                break;
            }
            catch (Exception ex)
            {
                _logger.Warn($"[Updates] Вход: ошибка звена security_check ({ex.GetType().Name}: {ex.Message}).");
                finished = false;
                break;
            }
        }

        if (!finished)
            LogRedirectInfo($"[Updates] Вход: звено security_check не завершилось на целевом контенте (последний адрес '{current}').");

        var alive = await IsPortalSessionAliveAsync(probeUrl, ct).ConfigureAwait(false);
        LogRedirectInfo($"[Updates] Вход: alive={(alive ? "True" : "False")} после security_check");
        return alive;
    }

    /// <summary>
    /// Звено CAS после «кабинетного» успеха POST (issue #323, девятая итерация): билет CAS
    /// приходит через Location ответа POST (эталон рабочего кода 1С, комментарий 23) ЛИБО
    /// через meta-refresh/JS в теле страницы кабинета. Здесь POST уже вернул 200 без Location —
    /// ищем билет в теле: <c>https://releases.1c.ru/public/security_check?ticket=ST-…</c>.
    /// GET по ticket-URL с cookie login.1c.ru устанавливает SESSION/JSESSIONID для
    /// releases.1c.ru; затем alive-проверка. Голый GET security_check БЕЗ билета выполняется
    /// только как ЗАПАСНОЙ путь, когда билета в теле нет вовсе (в живом сценарии не достигается —
    /// лог 7OH 0.3.9.316: голый security_check → 302 → /error/403).
    /// </summary>
    private async Task<bool> RunTicketSecurityCheckAsync(
        string postBody, string postUrl, string? formUrl, string? probeUrl, CancellationToken ct)
    {
        // ВАЖНО: ticket-URL берётся ТОЛЬКО при реально найденном билете. Передача пустой
        // строки в ResolveBodyRedirectTarget НЕ является «билетом»: new Uri(base, "") в .NET
        // резолвится в сам base-URL, и ветка «билета нет» молча превращалась бы в
        // FollowLoginRedirectsAsync(postUrl) — GET формы входа вместо запасного пути
        // security_check (регресс тестов кабинета 0.3.9.316).
        var rawTicket = ExtractBodyRedirectUrl(postBody);
        var ticketTarget = rawTicket is null
            ? null
            : ResolveBodyRedirectTarget(postUrl, rawTicket);
        if (ticketTarget is not null)
        {
            LogRedirectInfo($"[Updates] Вход: ticket-URL из тела кабинета '{ticketTarget}'");
            var completed = await FollowLoginRedirectsAsync(ticketTarget, ct).ConfigureAwait(false);
            LogRedirectInfo($"[Updates] Вход: FollowLoginRedirectsAsync(ticket)={completed}, " +
                            $"sessionCookie={HasPortalSessionCookie()}");
            if (!completed)
            {
                LogRedirectInfo("[Updates] Вход: ticket-цепочка не завершилась на целевом контенте — живая сессия не подтверждена.");
                return false;
            }
        }
        else
        {
            // Запасной путь: билет в теле не найден — пробуем прежнее звено security_check
            // (service GET-формы или корень releases.1c.ru). В живом сценарии с билетом
            // через Location/meta-refresh эта ветка не достигается.
            LogRedirectInfo("[Updates] Вход: билет CAS в теле кабинета не найден — запасной путь звена security_check.");
            var fallbackOk = await RunSecurityCheckAsync(formUrl, probeUrl, ct).ConfigureAwait(false);
            return fallbackOk;
        }

        var alive = await IsPortalSessionAliveAsync(probeUrl, ct).ConfigureAwait(false);
        LogRedirectInfo($"[Updates] Вход: alive={(alive ? "True" : "False")} после ticket-звена");
        return alive;
    }

    /// <summary>URL звена CAS <c>security_check</c> после успешного POST входа: параметр
    /// <c>service</c> GET-формы входа (обычно <c>https://releases.1c.ru/public/security_check</c> —
    /// туда CAS направляет браузер с билетом ST-…), иначе корень <c>https://releases.1c.ru/</c>
    /// (эталон рабочего кода 1С, комментарий 23 в #323: GET корня после POST формы).</summary>
    internal static Uri ResolveSecurityCheckTarget(string? formUrl)
    {
        var service = ExtractSecurityCheckService(formUrl);
        return service ?? new Uri("https://releases.1c.ru/");
    }

    /// <summary>Извлекает URL звена <c>security_check</c> из параметра <c>service</c> GET-формы
    /// входа, если он валиден и ведёт на releases.1c.ru; иначе null.</summary>
    internal static Uri? ExtractSecurityCheckService(string? formUrl)
    {
        if (string.IsNullOrWhiteSpace(formUrl) ||
            !Uri.TryCreate(formUrl, UriKind.Absolute, out var form))
        {
            return null;
        }

        var service = GetQueryParam(form, "service");
        if (string.IsNullOrWhiteSpace(service) ||
            !Uri.TryCreate(service, UriKind.Absolute, out var serviceUri) ||
            !serviceUri.Host.Contains("releases.1c.ru", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return serviceUri;
    }

    /// <summary>Значение query-параметра URL (значение декодируется через
    /// <see cref="Uri.UnescapeDataString"/>) либо null при отсутствии.</summary>
    internal static string? GetQueryParam(Uri uri, string name)
    {
        var query = uri.Query.TrimStart('?');
        if (query.Length == 0)
            return null;

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0)
                continue;
            if (pair.Substring(0, eq).Equals(name, StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(pair.Substring(eq + 1));
        }

        return null;
    }

    /// <summary>Пробная проверка «живости» сессии portal.1c.ru: GET по целевому URL операции
    /// (каталогу). Сессия жива, если ответ 2xx и тело НЕ является страницей входа
    /// (<see cref="LooksLikeLoginForm"/>). Редирект на login.1c.ru либо страница входа в теле —
    /// сессия мертва (cookie-заглушка). При невалидном URL/сетевой ошибке — false (безопасный
    /// выбор: будет выполнен полный вход; оффлайн-fallback-пути не ломаются).</summary>
    private async Task<bool> IsPortalSessionAliveAsync(string? probeUrl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(probeUrl) ||
            !Uri.TryCreate(probeUrl, UriKind.Absolute, out var probeUri))
        {
            LogRedirectInfo("[Updates] Вход: пробный GET живости сессии пропущен (URL не задан) — выполняется полный вход.");
            return false;
        }

        try
        {
            var current = probeUri;
            for (var i = 0; i <= MaxRedirects; i++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                AddBasicAuth(request);
                using var response =
                    await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);

                var status = (int)response.StatusCode;
                var body = await ReadBodyQuietlyAsync(response, ct).ConfigureAwait(false);
                var isLoginForm = LooksLikeLoginForm(body);
                var finalHost = response.RequestMessage?.RequestUri?.Host ?? current.Host;
                var isLoginHost = finalHost.Contains("login.1c.ru", StringComparison.OrdinalIgnoreCase);
                var isError403 = response.RequestMessage?.RequestUri?.AbsolutePath
                                     .Contains("/error/403", StringComparison.OrdinalIgnoreCase) == true;

                // issue #323 (0.3.9.323, десятая итерация): releases.1c.ru отвечает 302
                // с Location и для ЖИВОЙ сессии (редирект на канонический URL — то самое
                // «один заголовок с Большой буквой — Location», о котором пишет 7OH).
                // Следуем за редиректом (до MaxRedirects шагов) и оцениваем ФИНАЛЬНЫЙ
                // ответ; 2xx вне страницы входа/ошибки => сессия жива (вход не нужен).
                // Редирект на login.1c.ru или /error/403 — признак мёртвой сессии (не следуем).
                if (status is >= 300 and < 400 && response.Headers.Location is not null &&
                    !isLoginHost && !isError403)
                {
                    var loc = response.Headers.Location;
                    LogRedirectInfo($"[Updates] Вход: probe редирект {status} (шаг {i}): '{loc}' для '{current}'");
                    ApplySetCookieToContainer(response, current);
                    current = loc.IsAbsoluteUri ? loc : new Uri(current, loc);
                    continue;
                }

                var alive = status is >= 200 and < 300 && !isLoginForm && !isLoginHost;
                LogRedirectInfo($"[Updates] Вход: пробная проверка живой сессии '{probeUri}' => status={status}, " +
                                $"bodyLength={body.Length}, loginForm={(isLoginForm ? "да" : "нет")}, alive={alive}");
                return alive;
            }

            LogRedirectInfo($"[Updates] Вход: пробная проверка живой сессии '{probeUri}' => слишком много редиректов, alive=False");
            return false;
        }
        catch (Exception ex)
        {
            _logger.Warn($"[Updates] Вход: пробная проверка живой сессии не выполнена ({ex.GetType().Name}: {ex.Message}) — выполняется полный вход.");
            return false;
        }
    }

    /// <summary>Результат программного входа на portal.1c.ru (гибридная авторизация CAS).</summary>
    public enum PortalLoginResult
    {
        /// <summary>Вход выполнен успешно (цепочка редиректов пройдена либо ответ 2xx).</summary>
        Success,

        /// <summary>Учётные данные не приняты сервером (HTTP 401 либо форма ошибки после POST).</summary>
        AuthFailed,

        /// <summary>Учётные данные не заданы (логин пуст).</summary>
        NoCredentials,

        /// <summary>Форма входа недоступна: не получен HTML или не извлечён токен execution.</summary>
        FormUnavailable,

        /// <summary>Цепочка редиректов после входа не завершилась (страница входа / слишком много переходов).</summary>
        RedirectFailed,
    }

    /// <summary>
    /// True — допустима ещё одна попытка программного входа на portal.1c.ru. Лимит —
    /// <see cref="MaxPortalLoginAttempts"/> попыток за сессию службы; счётчик сбрасывается при
    /// смене учётной записи (логин/выбранная запись ИТС), при УСПЕШНОМ входе
    /// (см. <see cref="TryLoginPortalAsync"/>) и автоматически через <see cref="LoginLimitCooldown"/>
    /// после исчерпания (issue #330/#323/#334). При отсутствии учётных данных вход
    /// не «тратит» попытки: каждая операция быстро вернёт NoCredentials и понятное предупреждение.
    /// </summary>
    private bool CanAttemptPortalLogin()
    {
        var (login, _) = GetCredentials();
        if (string.IsNullOrEmpty(login))
        {
            _logger.Warn("[Updates] Для входа на portal.1c.ru не задан логин.");
            return false;
        }

        var signature = ComputeAccountSignature();
        if (!string.Equals(_lastAttemptAccountSignature, signature, StringComparison.Ordinal))
        {
            _lastAttemptAccountSignature = signature;
            _portalLoginAttempts = 0;
            _limitReachedAt = default;
        }

        if (_portalLoginAttempts >= MaxPortalLoginAttempts)
        {
            // Автосброс лимита по таймеру: через LoginLimitCooldown после исчерпания
            // лимита новая попытка входа разрешается автоматически (issue #334/#330/#323).
            if (_limitReachedAt == default)
                _limitReachedAt = UtcNowProvider();

            if (UtcNowProvider() - _limitReachedAt >= LoginLimitCooldown)
            {
                _portalLoginAttempts = 0;
                _limitReachedAt = default;
                _logger.Info($"[Updates] Лимит попыток входа на portal.1c.ru автоматически сброшен (прошло более {(int)LoginLimitCooldown.TotalMinutes} мин).");
            }
            else
            {
                var remaining = (int)(LoginLimitCooldown - (UtcNowProvider() - _limitReachedAt)).TotalMinutes;
                _logger.Warn("[Updates] Исчерпан лимит попыток входа на portal.1c.ru (" +
                             $"{MaxPortalLoginAttempts}) за сессию (повторная попытка через ~{remaining} мин). " +
                             "Проверьте учётные данные ИТС в «Настройки → Учётные данные ИТС»; " +
                             "при неверном пароле портал может временно блокировать аккаунт.");
                return false;
            }
        }

        _portalLoginAttempts++;
        return true;
    }

    /// <summary>True — лимит попыток входа на portal.1c.ru исчерпан (до автосброса по
    /// <see cref="LoginLimitCooldown"/> либо явного <see cref="ResetPortalLoginAttempts"/>).
    /// Используется для показа отдельной ошибки «лимит исчерпан» в результатах проверок
    /// вместо вводящего в заблуждение AuthRequired/AuthFailed (issue #334/#330/#323).
    /// Лимит «исчерпан» только если попытка входа была фактически ЗАБЛОКИРОВАНА
    /// (<see cref="_limitReachedAt"/> взведён): если же счётчик достиг максимума штатными
    /// неудачными попытками (попытка №Max была разрешена и не подтверждена сервером),
    /// результат остаётся AuthFailed — статус «лимит» наступает со следующей
    /// заблокированной попытки.</summary>
    internal bool IsPortalLoginLimitReached()
    {
        if (_limitReachedAt == default)
            return false;

        // Если время автосброса уже наступило — лимит считается снятым.
        return UtcNowProvider() - _limitReachedAt < LoginLimitCooldown;
    }

    /// <summary>Принудительно сбрасывает счётчик попыток входа на portal.1c.ru — например,
    /// после явного действия пользователя (смена учётных данных ИТС в настройках).
    /// Сбрасывается только локальный счётчик; анти-брутфорс-защита самого портала
    /// (временная блокировка аккаунта при многократных неверных входах) не отменяется.</summary>
    public void ResetPortalLoginAttempts()
    {
        _portalLoginAttempts = 0;
        _limitReachedAt = default;
    }

    /// <summary>Сигнатура учётной записи для сброса счётчика попыток входа: выбранная запись
    /// справочника (или устаревшие поля настроек) + логин. Пароль в сигнатуру НЕ входит.</summary>
    private string ComputeAccountSignature()
    {
        var settings = _repository.LoadSettings();
        var account = _itsAccounts?.Resolve(settings.ItsAccountId);
        var login = account is not null ? account.Login : settings.UpdatesLogin;
        return $"{settings.ItsAccountId ?? string.Empty}|{login ?? string.Empty}";
    }

    /// <summary>Читает тело ответа без исключений (для анонимизированной диагностики 401).</summary>
    private static async Task<string> ReadBodyQuietlyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Явно добавляет cookie из заголовков <c>Set-Cookie</c> ответа в общее хранилище
    /// (issue #323/#330/#334): в проде эту работу выполняет <c>HttpClientHandler</c>, но для
    /// кастомных транспортов и тестов с fake-обработчиками обработка дублируется здесь, чтобы
    /// <see cref="HasPortalSessionCookie"/> корректно отражал факт установки сессии. Повторное
    /// добавление той же cookie в контейнер безопасно (заменяет предыдущую). Некорректные
    /// заголовки игнорируются — вход не роняется.
    /// Сюда же попадает sticky-session cookie балансировщика <c>SERVERID</c> (домен <c>.1c.ru</c>,
    /// Path=/; эталон кода 1С передаёт её в запросы к releases.1c.ru, issue #323, п.2.3): она
    /// сохраняется в контейнере и видна в инвентаризации, хотя сессией портала НЕ является
    /// (<see cref="HasPortalSessionCookie"/> её не учитывает).
    /// </summary>
    private void ApplySetCookieToContainer(HttpResponseMessage response, Uri requestUri)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values))
            return;

        foreach (var header in values)
        {
            try
            {
                var cookie = ParseSetCookie(header, requestUri);
                if (cookie is null)
                    continue;

                var host = cookie.Domain.StartsWith(".", StringComparison.Ordinal)
                    ? cookie.Domain.TrimStart('.')
                    : cookie.Domain;
                _cookieContainer.Add(new Uri($"https://{host}/"), cookie);
            }
            catch
            {
                // Некорректный Set-Cookie не должен ронять вход.
            }
        }
    }

    /// <summary>
    /// Разбирает один заголовок <c>Set-Cookie</c> в <see cref="Cookie"/>: имя/значение и
    /// атрибуты Path/Domain/Expires/HttpOnly/Secure. Возвращает null при отсутствии пары
    /// name=value или пустом имени. Значения cookie в журнал не выводятся
    /// (issue #323/#330/#334).
    /// </summary>
    internal static Cookie? ParseSetCookie(string header, Uri fallbackUri)
    {
        if (string.IsNullOrWhiteSpace(header))
            return null;

        var parts = header.Split(';');
        var first = parts[0];
        var eq = first.IndexOf('=');
        if (eq <= 0)
            return null;

        var name = first.Substring(0, eq).Trim();
        var value = first.Substring(eq + 1).Trim();
        if (name.Length == 0)
            return null;

        var cookie = new Cookie(name, value);
        for (var i = 1; i < parts.Length; i++)
        {
            var p = parts[i].Trim();
            if (p.Length == 0)
                continue;

            var eq2 = p.IndexOf('=');
            var attrName = eq2 > 0 ? p.Substring(0, eq2).Trim() : p;
            var attrValue = eq2 > 0 ? p.Substring(eq2 + 1).Trim() : string.Empty;

            if (string.Equals(attrName, "path", StringComparison.OrdinalIgnoreCase) && attrValue.Length > 0)
                cookie.Path = attrValue;
            else if (string.Equals(attrName, "domain", StringComparison.OrdinalIgnoreCase) && attrValue.Length > 0)
                cookie.Domain = attrValue;
            else if (string.Equals(attrName, "expires", StringComparison.OrdinalIgnoreCase) && attrValue.Length > 0
                     && DateTime.TryParse(attrValue, CultureInfo.InvariantCulture, DateTimeStyles.None, out var expires))
                cookie.Expires = expires;
            else if (string.Equals(attrName, "httponly", StringComparison.OrdinalIgnoreCase))
                cookie.HttpOnly = true;
            else if (string.Equals(attrName, "secure", StringComparison.OrdinalIgnoreCase))
                cookie.Secure = true;
        }

        if (cookie.Domain.Length == 0)
            cookie.Domain = fallbackUri.Host;

        return cookie;
    }

    /// <summary>True — в заголовках ответа есть хотя бы один <c>Set-Cookie</c>.</summary>
    private static bool HasSetCookieHeader(HttpResponseMessage response)
        => response.Headers.TryGetValues("Set-Cookie", out _);

    /// <summary>Имена и флаги cookie из всех заголовков <c>Set-Cookie</c> ответа POST входа
    /// (БЕЗ значений) — диагностика «фантомного успеха» (issue #323/#330/#334).</summary>
    internal static string DescribeSetCookies(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values))
            return "<нет>";
        return string.Join(" | ", values.Select(DescribeSetCookieHeader));
    }

    /// <summary>Превращает один заголовок <c>Set-Cookie</c> в строку «имя; атрибуты» БЕЗ
    /// значения: <c>JSESSIONID; HttpOnly; Secure; Path=/; Domain=login.1c.ru</c>.</summary>
    internal static string DescribeSetCookieHeader(string header)
    {
        if (string.IsNullOrWhiteSpace(header))
            return "<пустой>";

        var parts = header.Split(';');
        var name = parts[0].Split('=')[0].Trim();
        var flags = new List<string>();
        for (var i = 1; i < parts.Length; i++)
        {
            var p = parts[i].Trim();
            if (p.Length == 0)
                continue;

            var eq = p.IndexOf('=');
            var attrName = eq > 0 ? p.Substring(0, eq).Trim() : p;
            var attrValue = eq > 0 ? p.Substring(eq + 1).Trim() : string.Empty;
            if (string.Equals(attrName, "httponly", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(attrName, "secure", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(attrName, "path", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(attrName, "domain", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(attrName, "samesite", StringComparison.OrdinalIgnoreCase))
            {
                flags.Add(attrValue.Length > 0 ? $"{attrName}={attrValue}" : attrName);
            }
        }

        return name.Length == 0 ? "<безымянная>" : flags.Count == 0 ? name : $"{name}; {string.Join("; ", flags)}";
    }

    /// <summary>Имена всех заголовков ответа (с фактическим регистром) и значение Location —
    /// диагностика issue #323 (десятая итерация): 7OH подозревает, что releases.1c.ru
    /// «возвращает один заголовок с Большой буквой — а именно Location»; по списку имён
    /// видно, какие заголовки реально пришли в ответ POST и есть ли среди них Location.</summary>
    internal static string DescribeResponseHeaders(HttpResponseMessage response)
    {
        if (response is null)
            return "<нет>";
        var names = response.Headers.Select(h => h.Key).ToList();
        var location = response.Headers.Location?.ToString() ?? "<нет>";
        return $"имена=[{string.Join(",", names)}], Location={location}";
    }

    /// <summary>
    /// Строит тело POST формы входа на portal.1c.ru (issue #323, девятая/десятая итерации).
    /// Для КЛАССИЧЕСКОЙ CAS-формы login.1c.ru (execution + inviteCode/geolocation/rememberMe/
    /// submit) — тело СТРОГО как в рабочем коде 1С (@7OH, комментарий 23): inviteCode(пусто),
    /// username, password, execution, _eventId=submit, geolocation(пусто), submit=Войти,
    /// rememberMe=on — без прочих hidden-полей формы (anotherComputer, inviteType и пр.),
    /// на которые CAS может отвечать 200 кабинетом вместо 302 с билетом (лог 7OH 0.3.9.319:
    /// POST status=200, location='<нет>'). Для НЕ-классической формы — прежний
    /// динамический набор (поля формы + username/password/_eventId + эталонные поля при
    /// отсутствии; страховка issue #334/#330).
    /// </summary>
    internal static Dictionary<string, string> BuildLoginPostBody(
        IReadOnlyDictionary<string, string> formFields, string login, string password)
    {
        // issue #323 (0.3.9.323, десятая итерация): классическая CAS-форма (login.1c.ru
        // с execution + полями приглашений/геолокации) — тело СТРОГО как в рабочем коде 1С
        // (комментарий 23): inviteCode(пусто), username, password, execution, _eventId=submit,
        // geolocation(пусто), submit=Войти, rememberMe=on. Лишние hidden-поля формы
        // (anotherComputer, inviteType и пр.) НЕ отправляются: Spring Security CAS на их
        // наличие может отвечать 200 кабинетом вместо 302 с билетом (лог 7OH 0.3.9.319:
        // POST status=200, location='<нет>').
        if (IsClassicCasForm(formFields))
        {
            var execution = formFields.TryGetValue("execution", out var exec) ? exec : string.Empty;
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["inviteCode"] = string.Empty,
                ["username"] = login ?? string.Empty,
                ["password"] = password ?? string.Empty,
                ["execution"] = execution,
                ["_eventId"] = "submit",
                ["geolocation"] = string.Empty,
                ["submit"] = "Войти",
                ["rememberMe"] = "on"
            };
        }

        // Не-классическая форма (иная разметка/токены) — прежний динамический набор:
        // поля формы + username/password/_eventId + эталонные поля при отсутствии
        // (страховка для неклассических форм, issue #334/#330: жёсткий список
        // отклоняется сервером 401 при изменении формы).
        var form = new Dictionary<string, string>(
            formFields ?? new Dictionary<string, string>(StringComparer.Ordinal),
            StringComparer.Ordinal)
        {
            ["username"] = login ?? string.Empty,
            ["password"] = password ?? string.Empty,
            ["_eventId"] = "submit"
        };

        foreach (var (name, value) in ReferenceLoginFields)
        {
            if (!form.ContainsKey(name))
                form[name] = value;
        }

        return form;
    }

    /// <summary>Признак классической CAS-формы portal.1c.ru (issue #323, десятая итерация):
    /// есть токен <c>execution</c> И хотя бы одно из полей приглашений/геолокации —
    /// <c>inviteCode</c>/<c>geolocation</c>/<c>rememberMe</c>/<c>submit</c>. Для такой формы
    /// тело POST собирается ТОЧНО как в рабочем коде 1С (см. <see cref="BuildLoginPostBody"/>).</summary>
    private static bool IsClassicCasForm(IReadOnlyDictionary<string, string>? formFields)
    {
        if (formFields is null || !formFields.ContainsKey("execution"))
            return false;
        return formFields.ContainsKey("inviteCode") ||
               formFields.ContainsKey("geolocation") ||
               formFields.ContainsKey("rememberMe") ||
               formFields.ContainsKey("submit");
    }

    /// <summary>Поля POST входа из эталона рабочего кода 1С (issue #323, девятая итерация);
    /// используются для не-классических форм.</summary>
    private static readonly (string Name, string Value)[] ReferenceLoginFields =
    {
        ("inviteCode", string.Empty),
        ("geolocation", string.Empty),
        ("submit", "Войти"),
        ("rememberMe", "on")
    };

    /// <summary>
    /// Выбирает адрес формы входа: полный URL редиректа сервера (<c>login.1c.ru/login?service=…</c>),
    /// если он валиден и ведёт на login.1c.ru, иначе базовый <see cref="PortalLoginUrl"/>.
    /// </summary>
    private static string ResolveLoginFormUrl(string? loginUrl)
    {
        if (!string.IsNullOrWhiteSpace(loginUrl) &&
            Uri.TryCreate(loginUrl, UriKind.Absolute, out var uri) &&
            uri.Host.Contains("login.1c.ru", StringComparison.OrdinalIgnoreCase))
        {
            return uri.AbsoluteUri;
        }

        return PortalLoginUrl;
    }

    /// <summary>
    /// Следует за цепочкой редиректов после POST входа (GET по Location), пока не будет получен
    /// финальный ответ вне страницы входа. Каждый шаг журналируется (единая диагностика issue #323).
    /// Cookie из ответов накапливаются в общем <see cref="CookieContainer"/>.
    /// Возвращает true, если цепочка завершилась успешно (вход подтверждён).
    /// </summary>
    private async Task<bool> FollowLoginRedirectsAsync(Uri location, CancellationToken ct)
    {
        var current = location;
        for (var i = 0; i < MaxRedirects; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            using var response =
                await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            // issue #323 (0.3.9.319): Set-Cookie каждого шага цепочки (SESSION/JSESSIONID для
            // releases.1c.ru — билет принимается именно на ticket-звене) сохраняются в общий
            // контейнер через ApplySetCookieToContainer (важно и для тестов с fake-обработчиками).
            ApplySetCookieToContainer(response, request.RequestUri ?? current);
            var status = (int)response.StatusCode;
            var next = response.Headers.Location;

            if (status is >= 300 and < 400 && next is not null)
            {
                LogRedirectInfo($"[Updates] Редирект входа (шаг {i + 1}): {status} '{next}' для '{current}'");
                current = next.IsAbsoluteUri ? next : new Uri(current, next);
                continue;
            }

            // Конец цепочки: успех — финальный ответ вне страницы входа И тело НЕ содержит
            // форму входа (CAS может вернуть 200 с формой вместо целевого контента —
            // «фантомный успех», issue #330).
            var finalHost = response.RequestMessage?.RequestUri?.Host ?? current.Host;
            if (status < 400 && !finalHost.Contains("login.1c.ru", StringComparison.OrdinalIgnoreCase))
            {
                var body = await ReadBodyQuietlyAsync(response, ct).ConfigureAwait(false);
                if (!LooksLikeLoginForm(body))
                    return true;

                _logger.Warn("[Updates] Цепочка входа завершилась 200 со страницей входа (вход не подтверждён).");
                return false;
            }

            _logger.Warn($"[Updates] Цепочка входа завершилась на странице входа (status={status}, host='{finalHost}').");
            return false;
        }

        _logger.Warn("[Updates] Слишком много перенаправлений после входа на portal.1c.ru.");
        return false;
    }

    /// <summary>Отображаемое имя учётной записи для журнала входа (без пароля):
    /// имя записи справочника, иначе логин, иначе «не задана».</summary>
    private string ResolveAccountName()
    {
        var settings = _repository.LoadSettings();
        var account = _itsAccounts?.Resolve(settings.ItsAccountId);
        if (account is not null)
        {
            return string.IsNullOrWhiteSpace(account.Name)
                ? (account.Login ?? string.Empty)
                : account.Name!;
        }

        return string.IsNullOrWhiteSpace(settings.UpdatesLogin)
            ? "не задана"
            : settings.UpdatesLogin!;
    }

    /// <summary>
    /// Извлекает поля формы входа (<c><input type="hidden"></c> и отмеченные
    /// <c>checkbox</c>) из HTML: имя → значение. Устойчиво к порядку атрибутов и кавычкам
    /// ('…' / "…"). Возвращает все скрытые поля, чтобы POST входа собирался динамически
    /// (execution, lt, CSRF и пр.) — жёсткий список полей отклоняется сервером 401 при
    /// изменении формы (issue #334). Пустой/битый HTML — пустой словарь.
    /// </summary>
    internal static Dictionary<string, string> ExtractFormFields(string html)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(html))
            return fields;

        foreach (Match tag in InputTagRegex.Matches(html))
        {
            var type = GetAttribute(tag.Value, "type") ?? string.Empty;
            var name = GetAttribute(tag.Value, "name");
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var isHidden = string.Equals(type, "hidden", StringComparison.OrdinalIgnoreCase);
            var isCheckedCheckbox = string.Equals(type, "checkbox", StringComparison.OrdinalIgnoreCase)
                                    && Regex.IsMatch(tag.Value, @"\bchecked\b", RegexOptions.IgnoreCase);
            if (!isHidden && !isCheckedCheckbox)
                continue;

            fields[name!] = GetAttribute(tag.Value, "value") ?? string.Empty;
        }

        return fields;
    }

    /// <summary>Регулярное выражение тега <c><input …></c> (включая самозакрывающиеся).</summary>
    private static readonly Regex InputTagRegex =
        new(@"<input\b[^>]*/?>", RegexOptions.IgnoreCase | RegexOptions.Singleline);

    /// <summary>Значение атрибута тега (кавычки '…' / "…" или без кавычек) либо null.</summary>
    private static string? GetAttribute(string tag, string attributeName)
    {
        var pattern = $@"\b{Regex.Escape(attributeName)}\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)'|(?<v>[^\s>]+))";
        var match = Regex.Match(tag, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? match.Groups["v"].Value : null;
    }

    /// <summary>
    /// Извлекает атрибут <c>action</c> формы входа (issue #323/#330/#334, третья итерация):
    /// адрес, на который отправляется POST. Spring Security CAS часто указывает action,
    /// отличный от URL GET-формы (<c>/login/cas?service=…</c>) — POST «на адрес GET» уходил
    /// не туда (401/404). Возвращает null, если action отсутствует/пуст/равен "#".
    /// </summary>
    internal static string? ExtractFormAction(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return null;

        foreach (Match tag in FormTagRegex.Matches(html))
        {
            var action = GetAttribute(tag.Value, "action");
            if (string.IsNullOrWhiteSpace(action) || action == "#")
                continue;
            return action;
        }

        return null;
    }

    /// <summary>Регулярное выражение открывающего тега <c><form …></c>.</summary>
    private static readonly Regex FormTagRegex =
        new(@"<form\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline);

    /// <summary>
    /// Выбирает адрес POST формы входа и причину выбора (issue #323, седьмая итерация, C-2).
    /// Эталон рабочего кода 1С (комментарий 23): POST уходит на ПОЛНЫЙ URL GET-формы с
    /// <c>service=</c>, а не на атрибут action, когда action не несёт собственного пути
    /// («/login», «/login?…») — Spring Security CAS выпускает билет для указанного service
    /// только если service присутствует в POST. Правила:
    /// <list type="bullet">
    /// <item>action отсутствует/пуст/"#" — адрес GET-формы (прежнее поведение);</item>
    /// <item>action ведёт на ТОТ ЖЕ путь, что и GET-форма, БЕЗ собственных параметров, и у
    /// GET-формы есть параметр <c>service</c> — полный URL GET-формы с service=;</item>
    /// <item>иначе (action несёт собственный путь/параметры, например "/login/cas?service=…") —
    /// резолвленный action (прежнее поведение).</item>
    /// </list>
    /// Referer/Origin при этом остаются на адресе GET-формы — часть CAS-развёртываний проверяет
    /// их при POST (см. TryLoginPortalAsync). Возвращает кортеж (адрес, причина выбора) — причина
    /// логируется в журнал, чтобы по нему было видно поведение (критерий приёмки плана 0.3.9.313).
    /// </summary>
    internal static (string Url, string Reason) ResolveFormPostUrl(string formUrl, string? action)
    {
        if (string.IsNullOrWhiteSpace(action))
            return (formUrl, "action отсутствует — адрес GET-формы");

        Uri? target = null;
        if (Uri.TryCreate(action, UriKind.Absolute, out var abs))
            target = abs;
        else if (Uri.TryCreate(new Uri(formUrl), action, out var rel))
            target = rel;
        if (target is null)
            return (formUrl, "action нерезолвен — адрес GET-формы");

        // C-2: action без собственного пути (тот же путь, что и GET-форма) при наличии
        // service у GET-формы — POST на полный URL GET-формы (эталон 1С).
        if (Uri.TryCreate(formUrl, UriKind.Absolute, out var form))
        {
            var hasServiceInForm = form.Query.TrimStart('?')
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Any(pair => pair.Split('=')[0].Equals("service", StringComparison.OrdinalIgnoreCase));
            if (hasServiceInForm &&
                string.Equals(target.AbsolutePath, form.AbsolutePath, StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrEmpty(target.Query))
            {
                return (form.AbsoluteUri,
                    "action без собственного пути, у GET-формы есть service — полный URL GET-формы");
            }
        }

        return (target.AbsoluteUri, "action формы");
    }

    /// <summary>
    /// Признаки того, что форма входа изменилась радикально — OAuth/JS-челлендж вместо
    /// классической CAS-формы с <c>execution</c>/<c>lt</c> (issue #323/#330/#334): маркеры
    /// <c>oauth</c>/<c>client_id</c>/<c>challenge</c>/<c>csrf</c>. Программный POST классической
    /// формы в таком случае невозможен — нужен браузер (или импорт cookie, будущая итерация).
    /// </summary>
    internal static bool LooksLikeOAuthOrChallenge(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return false;
        return ContainsAny(body, "oauth", "client_id", "challenge", "csrf");
    }

    /// <summary>
    /// Логирует анонимизированные признаки неудачной авторизации: размер тела, маркеры
    /// ошибки, имена полей формы. Значения (пароль, логин, execution/lt-токены) НЕ выводятся.
    /// </summary>
    private void LogAnonymizedAuthFailure(string message, string body, IReadOnlyDictionary<string, string> fields)
    {
        var len = string.IsNullOrEmpty(body) ? 0 : body.Length;
        var markers = DetectAuthFailureMarkers(body);
        var title = ExtractPageTitle(body);
        var fieldNames = fields.Count == 0
            ? string.Empty
            : string.Join(", ", fields.Keys.OrderBy(k => k, StringComparer.Ordinal));
        // Признак «капча» сохраняется для выбора ключа локализации Updates.CaptchaRequired
        // (issue #323): автоматический вход временно невозможен — портал запросил подтверждение.
        _lastAuthFailureReason = markers.Contains("капча", StringComparison.Ordinal) ? "капча" : null;
        _logger.Warn($"{message} (body_len={len}" +
                     $"{(title is not null ? $", title='{title}'" : string.Empty)}" +
                     $"{(markers.Length > 0 ? $", признаки: {markers}" : string.Empty)}" +
                     $"{(fieldNames.Length > 0 ? $", поля формы: {fieldNames}" : string.Empty)}).");
    }

    /// <summary>
    /// Логирует расширенную диагностику ветки 2xx POST входа (issue #323/#330/#334):
    /// contentType, длину тела, извлечённый <c><title></c> страницы, превью первых
    /// ~300 символов (БЕЗ секретов — значения полей формы, логин и пароль удаляются) и
    /// Set-Cookie только именами/флагами. По title сразу видно «Личные данные» vs «Вход».
    /// </summary>
    private void LogPost2xxDiagnostics(
        int status,
        HttpResponseMessage response,
        string body,
        IReadOnlyDictionary<string, string> fields,
        string login,
        string password)
    {
        var contentType = response.Content?.Headers.ContentType?.ToString() ?? "<нет>";
        var bodyLength = string.IsNullOrEmpty(body) ? 0 : body.Length;
        var title = ExtractPageTitle(body);
        var preview = SanitizeBodyPreview(body, fields, login, password);
        LogRedirectInfo($"[Updates] Вход: POST 2xx диагностика status={status}, contentType='{contentType}', " +
                        $"bodyLength={bodyLength}, title='{title ?? "<нет>"}', bodyPreview='{preview}'");
        LogRedirectInfo($"[Updates] Вход: POST Set-Cookie: {DescribeSetCookies(response)}");
    }

    /// <summary>Превью тела для журнала (первые ~300 символов) с удалением секретов:
    /// значений полей формы (execution/lt/csrf), логина и пароля, значений атрибутов
    /// <c>value</c> у input-тегов и пар name=значение чувствительных полей. Управляющие
    /// символы заменяются пробелами — превью остаётся одной строкой.</summary>
    private static string SanitizeBodyPreview(
        string body, IReadOnlyDictionary<string, string> fields, string login, string password)
    {
        if (string.IsNullOrEmpty(body))
            return string.Empty;

        var text = body.Length > 300 ? body.Substring(0, 300) : body;

        // Известные секреты: значения полей формы, логин и пароль.
        var secrets = new List<string>();
        foreach (var value in fields.Values)
        {
            if (!string.IsNullOrWhiteSpace(value) && value.Length >= 3)
                secrets.Add(value);
        }

        if (!string.IsNullOrWhiteSpace(login) && login.Length >= 3)
            secrets.Add(login);
        if (!string.IsNullOrWhiteSpace(password) && password.Length >= 3)
            secrets.Add(password);

        foreach (var secret in secrets.Distinct(StringComparer.Ordinal))
            text = text.Replace(secret, "<...>", StringComparison.Ordinal);

        // Значения атрибутов value любых input скрываются целиком (токены в теле POST-ответа
        // могут отличаться от полей GET-формы).
        text = Regex.Replace(text, @"\bvalue\s*=\s*(?:""[^""]*""|'[^']*')",
            "value=\"<...>\"", RegexOptions.IgnoreCase);

        // Пары name=значение в form-urlencoded контексте для чувствительных полей.
        text = Regex.Replace(text,
            @"\b(execution|lt|csrf|_csrf|password|username|j_password)\s*=\s*[^&\s""'<>]+",
            "$1=<...>", RegexOptions.IgnoreCase);

        return Regex.Replace(text, @"[\r\n\t]+", " ");
    }

    /// <summary>Определяет по тексту тела ответа вероятную причину отклонения входа
    /// (без вывода самого текста): неверный логин/пароль, капча, наличие полей lt/execution/csrf.
    /// Сужено (issue #323): «execution»/«lt»/«csrf» учитываются ТОЛЬКО как поля формы
    /// (<c>name="…"</c>), а не любое вхождение слова в HTML — JS-скрипты и подсказки
    /// валидации личного кабинета давали ложные признаки; из фраз отказа убрано слишком
    /// короткое «incorrect», остались точные фразы.</summary>
    private static string DetectAuthFailureMarkers(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return string.Empty;

        var found = new List<string>();
        if (HasAuthFailureTextMarker(body))
            found.Add("неверный логин/пароль");
        if (ContainsAny(body, "captcha", "капч", "recaptcha"))
            found.Add("капча");
        if (HasInputField(body, "execution"))
            found.Add("execution");
        if (HasInputField(body, "lt"))
            found.Add("поле lt");
        if (HasInputField(body, "csrf") || HasInputField(body, "_csrf"))
            found.Add("csrf");
        // Маркеры изменённой формы (OAuth/JS-челлендж, issue #323/#330/#334): по ним
        // распознаётся «протокол изменился» — автоматический вход невозможен.
        if (body.Contains("oauth", StringComparison.OrdinalIgnoreCase))
            found.Add("oauth");
        if (body.Contains("client_id", StringComparison.OrdinalIgnoreCase))
            found.Add("client_id");
        if (body.Contains("challenge", StringComparison.OrdinalIgnoreCase))
            found.Add("challenge");
        return string.Join(", ", found);
    }

    /// <summary>True — в теле есть ТОЧНАЯ фраза отказа авторизации (используется детектором
    /// формы входа, issue #323): наличие поля <c>execution</c> как поля формы НЕ должно
    /// превращать страницу личного кабинета в «форму входа». Капча — ОТДЕЛЬНЫЙ признак
    /// (см. <see cref="DetectAuthFailureMarkers"/>, A-6), в фразы отказа не входит.</summary>
    private static bool HasAuthFailureTextMarker(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return false;
        return ContainsAny(body,
            "Неверный логин", "Неверный логин или пароль", "Неверные учётные данные",
            "неверные учётные данные", "incorrect username or password",
            "invalid username", "invalid credentials", "bad credentials",
            "authentication failed");
    }

    /// <summary>True — текст содержит хотя бы одну из подстрок (без учёта регистра).</summary>
    private static bool ContainsAny(string text, params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (text.Contains(candidate, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Определяет, является ли тело ответа страницей входа на portal.1c.ru (гибридная
    /// авторизация CAS). Критерии ужесточены (issue #323; дополнительно сужены в 0.3.9.313, C-1),
    /// порядок проверки:
    /// <list type="number">
    /// <item>поля ввода логина И пароля (<c>name="username"</c> + <c>name="password"</c>) —
    /// главный признак; фразы отказа (<see cref="HasAuthFailureTextMarker"/>) учитываются ТОЛЬКО
    /// в этом пути (вместе с полями логина/пароля);</item>
    /// <item>ИЛИ токены CAS (<c>execution</c>/<c>lt</c>) КАК ПОЛЯ ФОРМЫ (через
    /// <see cref="ExtractFormFields"/>) И один из СТРУКТУРНЫХ признаков: action формы с «login»
    /// либо id/class-маркер формы входа (<see cref="ContainsLoginFormMarker"/>). Фраза отказа
    /// в этом пути НЕ учитывается — на странице личного кабинета она встречается в JS-скриптах
    /// валидации формы смены аккаунта и давала ложное AuthFailed (лог 7OH).</item>
    /// </list>
    /// Страница с полем <c>execution</c>, но БЕЗ <c>username</c>/<c>password</c> и БЕЗ
    /// формы-входа (action не login, id/class без login-маркера) формой входа НЕ считается —
    /// такие формы приглашений/смены аккаунта есть на странице личного кабинета после успешного
    /// входа. Используется для распознавания «фантомного успеха» при HTTP 200 с формой входа
    /// вместо целевого контента (issue #330/#334) и страницы входа при 200.
    /// </summary>
    internal static bool LooksLikeLoginForm(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return false;

        // Главный признак формы входа CAS: поля ввода логина и пароля. Фраза отказа
        // («Неверный логин или пароль») учитывается ТОЛЬКО вместе с этими полями.
        if (HasInputField(body, "username") && HasInputField(body, "password"))
            return true;

        // Второстепенный признак: токены CAS как ПОЛЯ ФОРМЫ + структурный маркер формы входа
        // (action на login либо id/class с login). Маркеры отказа здесь НЕ участвуют (C-1).
        var fields = ExtractFormFields(body);
        if (fields.ContainsKey("execution") || fields.ContainsKey("lt"))
        {
            var formAction = ExtractFormAction(body) ?? string.Empty;
            if (formAction.Contains("login", StringComparison.OrdinalIgnoreCase) ||
                ContainsLoginFormMarker(body))
                return true;
        }

        return false;
    }

    /// <summary>True — в HTML есть <c><input></c> с заданным атрибутом <c>name</c>
    /// (любой тип; устойчиво к порядку атрибутов и кавычкам '…'/«"…"»).</summary>
    private static bool HasInputField(string html, string name)
    {
        foreach (Match tag in InputTagRegex.Matches(html))
        {
            var tagName = GetAttribute(tag.Value, "name");
            if (string.Equals(tagName, name, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>True — среди <c><form></c> есть форма с id/class-маркером страницы входа
    /// (<c>login</c>, <c>fm1</c>, <c>signin</c>, <c>sign-in</c>, <c>logon</c>, <c>cas</c>,
    /// <c>authentication</c>) — дополнительный признак детектора формы входа (issue #323).</summary>
    private static bool ContainsLoginFormMarker(string html)
    {
        foreach (Match tag in FormTagRegex.Matches(html))
        {
            var marker = string.Concat(GetAttribute(tag.Value, "id"), " ", GetAttribute(tag.Value, "class"));
            if (ContainsAny(marker, "login", "signin", "sign-in", "logon", "cas", "authentication"))
                return true;
        }

        return false;
    }

    /// <summary>
    /// True — тело ответа является страницей ЛИЧНОГО КАБИНЕТА portal.1c.ru ПОСЛЕ успешного
    /// входа (issue #323): маркеры в <c><title></c> и заголовках страницы («Личные данные»,
    /// «Личный кабинет», «личный кабинет», «Главная», «Профиль», «Мои данные»). Используется
    /// в POST-ветке как положительный признак успеха даже при наличии поля <c>execution</c>
    /// (формы приглашений/смены аккаунта на странице кабинета). Логика — статическая
    /// <c>internal</c>, покрывается юнит-тестами.
    /// </summary>
    internal static bool DetectPersonalAreaPage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return false;

        return ContainsAny(body,
            "Личные данные", "Личный кабинет", "личный кабинет", "Главная", "Профиль", "Мои данные");
    }

    /// <summary>Извлекает текст <c><title></c> страницы для журнала (санитизированный:
    /// без вложенных тегов, HTML-декодированный, управляющие символы заменены, до 60 символов);
    /// null — тега нет или тело пустое/битое.</summary>
    internal static string? ExtractPageTitle(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        var match = Regex.Match(body, @"<title\b[^>]*>(?<t>.*?)</title\s*>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!match.Success)
            return null;

        var title = Regex.Replace(match.Groups["t"].Value, @"<[^>]+>", string.Empty);
        title = WebUtility.HtmlDecode(title);
        title = Regex.Replace(title, @"[\r\n\t]+", " ").Trim();
        return title.Length > 60 ? title.Substring(0, 60) : title;
    }

    /// <summary>
    /// Ищет в теле ответа 2xx признак JS/meta-refresh-редиректа и извлекает целевой URL
    /// (issue #323/#330/#334): CAS-цепочка часто доводится до
    /// <c>releases.1c.ru/public/security_check?ticket=…</c> именно JS-редиректом, где
    /// выставляется сессионная cookie. Маркеры: <c><meta http-equiv="refresh"></c>,
    /// <c>window.location</c>, <c>location.href</c>, <c>document.location</c>, <c>top.location</c>.
    /// Возвращает URL (HTML-декодированный) или null.
    /// </summary>
    internal static string? ExtractBodyRedirectUrl(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        // 1) <meta http-equiv="refresh" content="N; url=..."> — порядок атрибутов произвольный.
        foreach (Match tag in MetaRefreshTagRegex.Matches(body))
        {
            var contentMatch = Regex.Match(tag.Value,
                @"content\s*=\s*[""'](?<content>[^""']*)[""']",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!contentMatch.Success)
                continue;

            var urlMatch = Regex.Match(contentMatch.Groups["content"].Value,
                @"url\s*=\s*(?<url>[^;""'\s]+)",
                RegexOptions.IgnoreCase);
            if (urlMatch.Success)
                return WebUtility.HtmlDecode(urlMatch.Groups["url"].Value.Trim());
        }

        // 2) JS-редирект: window.location[.href|.replace|.assign](...) / document.location /
        //    top.location / location.href — присваивание или вызов.
        var js = Regex.Match(body,
            @"(?:\b(?:window|document|top)\s*\.\s*location|\blocation)(?:\s*\.\s*(?:href|replace|assign))?\s*[=(]\s*[""'](?<url>[^""']+)[""']",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (js.Success)
            return WebUtility.HtmlDecode(js.Groups["url"].Value.Trim());

        // 3) <iframe src="..."> — кабинет/промежуточная страница может доводить CAS-цепочку
        //    до ticket-URL iframe-загрузкой (issue #323, десятая итерация).
        var frame = Regex.Match(body,
            @"<iframe\b[^>]*\bsrc\s*=\s*[""'](?<url>[^""']+)[""'][^>]*>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (frame.Success)
            return WebUtility.HtmlDecode(frame.Groups["url"].Value.Trim());

        return null;
    }

    /// <summary>Регулярное выражение тега <c><meta http-equiv="refresh" …></c>.</summary>
    private static readonly Regex MetaRefreshTagRegex =
        new(@"<meta\b[^>]*http-equiv\s*=\s*[""']refresh[""'][^>]*>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

    /// <summary>Резолвит URL из JS/meta-refresh-редиректа относительно адреса POST формы.
    /// Пустая строка НЕ является редиректом: new Uri(base, "") в .NET резолвится в сам
    /// base-URL (пустой относительный путь), что превратило бы «билета нет» в
    /// FollowLoginRedirectsAsync(base) — поэтому для пустой строки возвращается null.</summary>
    private static Uri? ResolveBodyRedirectTarget(string baseUrl, string rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
            return null;
        if (Uri.TryCreate(rawUrl, UriKind.Absolute, out var abs))
            return abs;
        if (Uri.TryCreate(new Uri(baseUrl), rawUrl, out var rel))
            return rel;
        return null;
    }

    /// <summary>
    /// True — в общем хранилище cookie есть сессионная cookie портала 1С
    /// (<c>JSESSIONID</c>/<c>TGC</c>/<c>session_id</c>/<c>SESSION</c>) с доменом <c>.1c.ru</c>
    /// и Path, покрывающим корень. Sticky-session cookie балансировщика <c>SERVERID</c> (если
    /// портал её выставит в <c>Set-Cookie</c>) сознательно НЕ входит в этот набор — она не
    /// является сессией, а лишь привязывает к узлу; видна в инвентаризации
    /// <see cref="DescribeContainerCookies"/> (issue #323, п.2.3). Является вспомогательным
    /// признаком; основной критерий успеха — результат пробной проверки «живой» сессии
    /// (<see cref="IsPortalSessionAliveAsync"/>, A-1). Ужесточено по атрибутам Domain/Path
    /// (issue #323/#330/#334): cookie-заглушки WAF/CDN с «подходящим» именем, но чужим
    /// доменом/путём сессией портала не считаются.
    /// </summary>
    private bool HasPortalSessionCookie()
    {
        foreach (var host in new[] { "login.1c.ru", "releases.1c.ru" })
        {
            try
            {
                foreach (Cookie cookie in _cookieContainer.GetCookies(new Uri($"https://{host}/")))
                {
                    if (!IsPortalDomainCookie(cookie))
                        continue;

                    var name = cookie.Name ?? string.Empty;
                    if (name.Equals("TGC", StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith("JSESSIONID", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("session_id", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("SESSION", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch
            {
                // Некорректный URI/иные ошибки хранилища не должны ронять вход.
            }
        }

        return false;
    }

    /// <summary>True — cookie принадлежит домену портала 1С и покрывает корневой путь:
    /// <c>Domain</c> оканчивается на <c>.1c.ru</c> (или равен <c>login.1c.ru</c>/<c>releases.1c.ru</c>),
    /// <c>Path</c> — «/» (или пуст — значение по умолчанию). Прочие cookie (например, WAF/CDN
    /// с доменом другого сервиса либо путём /login) сессией портала не считаются.
    /// По этому критерию проходит и sticky-session cookie <c>SERVERID</c> (домен .1c.ru, Path=/):
    /// она сохраняется через <see cref="ApplySetCookieToContainer"/> и попадает в инвентаризацию
    /// <see cref="DescribeContainerCookies"/>, но НЕ является сессией — <see cref="HasPortalSessionCookie"/>
    /// по именам её не учитывает (issue #323, п.2.3 — мягкая поддержка).</summary>
    private static bool IsPortalDomainCookie(Cookie cookie)
    {
        var domain = cookie.Domain ?? string.Empty;
        if (domain.Length == 0)
            return false;

        var trimmed = domain.StartsWith(".", StringComparison.Ordinal) ? domain.Substring(1) : domain;
        if (!trimmed.EndsWith(".1c.ru", StringComparison.OrdinalIgnoreCase))
            return false;

        var path = cookie.Path ?? "/";
        return path.Length == 0 || string.Equals(path, "/", StringComparison.Ordinal);
    }

    /// <summary>Удаляет из общего хранилища cookie хостов <c>login.1c.ru</c>/<c>releases.1c.ru</c>
    /// (без влияния на Basic Auth в заголовках) — снимает мусорные cookie-заглушки WAF/CDN перед
    /// повторным входом (issue #323/#330/#334). Реализация через установку просроченного срока:
    /// <see cref="CookieContainer"/> не имеет публичного API удаления отдельных cookie; истёкшие
    /// cookie отбрасываются контейнером при следующем обращении (GetCookies).</summary>
    private void ClearPortalCookies()
    {
        foreach (var host in new[] { "login.1c.ru", "releases.1c.ru" })
        {
            try
            {
                var uri = new Uri($"https://{host}/");
                var cookies = _cookieContainer.GetCookies(uri);
                foreach (Cookie cookie in cookies)
                {
                    if (!IsPortalDomainCookie(cookie))
                        continue;

                    cookie.Expires = DateTime.Now.AddYears(-1);
                    _cookieContainer.Add(uri, cookie);
                }
            }
            catch
            {
                // Ошибки очистки cookie не должны ронять вход.
            }
        }
    }

    /// <summary>Внутренний хелпер для юнит-тестов: добавляет cookie портала в общее хранилище
    /// (симуляция предзаполненного/«заглушечного» контейнера после предыдущего входа).</summary>
    internal void SeedPortalCookieForTesting(string name, string value, string host)
    {
        try
        {
            var cookie = new Cookie(name, value) { Domain = host, Path = "/" };
            _cookieContainer.Add(new Uri($"https://{host}/"), cookie);
        }
        catch
        {
            // Некорректные параметры хелпера не должны ронять тест.
        }
    }

    /// <summary>Логирует перечень cookie общего хранилища для хостов портала 1С (имена и
    /// атрибуты, БЕЗ значений) — диагностика входа (issue #323/#330/#334).</summary>
    private void LogPortalCookieInventory()
        => LogRedirectInfo($"[Updates] Вход: cookie контейнера: {DescribeContainerCookies()}");

    /// <summary>
    /// INFO-диагностика редиректов/входа портала 1С (issue #347, кластер C): пишется ТОЛЬКО
    /// при включённом флаге <c>CM_REDIRECT</c> в конфиге trace.json (env <c>CM_REDIRECT=1</c> —
    /// override включения), иначе INFO-записи входа/редиректов не «капают» в общий журнал.
    /// Итоговые WARN/ERROR (результат входа) пишутся ВСЕГДА — прямые вызовы _logger.Warn
    /// в этой ветке не гейтятся.
    /// </summary>
    private void LogRedirectInfo(string message)
    {
        if (!TraceFlags.IsEnabled(TraceFlags.RedirectFlag))
            return;
        _logger.Info(message);
    }

    /// <summary>Имена и атрибуты (без значений) cookie в общем хранилище для hosts
    /// <c>login.1c.ru</c>/<c>releases.1c.ru</c> — строка для журнала. Выводятся ВСЕ cookie
    /// хостов, включая WAF/CDN-заглушки и sticky-session <c>SERVERID</c> (issue #323, п.2.3) —
    /// по инвентаризации видно, какие именно cookie получены из <c>Set-Cookie</c>.</summary>
    internal string DescribeContainerCookies()
    {
        var entries = new List<string>();
        foreach (var host in new[] { "login.1c.ru", "releases.1c.ru" })
        {
            try
            {
                var cookies = _cookieContainer.GetCookies(new Uri($"https://{host}/"));
                if (cookies.Count == 0)
                {
                    entries.Add($"{host}=<нет>");
                    continue;
                }

                var names = new List<string>();
                foreach (Cookie cookie in cookies)
                {
                    var attrs = new List<string>();
                    if (cookie.Secure) attrs.Add("Secure");
                    if (cookie.HttpOnly) attrs.Add("HttpOnly");
                    if (!string.IsNullOrEmpty(cookie.Path)) attrs.Add($"Path={cookie.Path}");
                    if (!string.IsNullOrEmpty(cookie.Domain)) attrs.Add($"Domain={cookie.Domain}");
                    names.Add(attrs.Count == 0 ? cookie.Name : $"{cookie.Name}{{{string.Join(",", attrs)}}}");
                }

                entries.Add($"{host}={string.Join("|", names)}");
            }
            catch
            {
                entries.Add($"{host}=<ошибка чтения>");
            }
        }

        return string.Join("; ", entries);
    }

    /// <summary>Перечень имён, доменов и путей «сессионных» cookie портала в общем хранилище
    /// (БЕЗ значений) — диагностика POST входа (issue #323): сессия страницы входа
    /// (SESSION Domain=login.1c.ru) НЕ означает авторизованную сессию каталога (там домен
    /// .1c.ru / releases.1c.ru).</summary>
    internal string DescribeSessionCookies()
    {
        var entries = new List<string>();
        foreach (var host in new[] { "login.1c.ru", "releases.1c.ru" })
        {
            try
            {
                foreach (Cookie cookie in _cookieContainer.GetCookies(new Uri($"https://{host}/")))
                {
                    if (!IsPortalDomainCookie(cookie))
                        continue;

                    var name = cookie.Name ?? string.Empty;
                    if (name.Equals("TGC", StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith("JSESSIONID", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("session_id", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("SESSION", StringComparison.OrdinalIgnoreCase))
                        entries.Add($"{name}[Domain={cookie.Domain},Path={cookie.Path}]");
                }
            }
            catch
            {
                // Некорректный URI/иные ошибки хранилища не должны ронять вход.
            }
        }

        return entries.Count == 0 ? "<нет>" : string.Join("|", entries);
    }

    /// <summary>Выбирает ключ локализации ошибки авторизации для результатов проверок:
    /// «лимит попыток исчерпан» — отдельный ключ (issue #334/#330/#323); портал запросил
    /// подтверждение (капча) — «требуется подтверждение» (issue #323); вход предпринимался
    /// и не подтверждён сервером — «вход не подтверждён (401)»; иначе — «требуется вход».</summary>
    private string AuthErrorKey(bool authFailed)
        => IsPortalLoginLimitReached() ? "Updates.LoginLimitReached"
            // Портал запросил подтверждение (капча): автоматический вход временно невозможен
            // (issue #323) — отдельный ключ с понятным текстом и советом открыть login.1c.ru.
            : _lastAuthFailureReason == "капча" ? "Updates.CaptchaRequired"
            // Форма входа изменилась/недоступна (OAuth/JS-челлендж, issue #323/#330/#334) —
            // отдельный ключ с понятным текстом и советом открыть login.1c.ru в браузере.
            : _lastLoginResult == PortalLoginResult.FormUnavailable ? "Updates.FormUnavailable"
            : authFailed ? "Updates.AuthFailed"
            : "Updates.AuthRequired";
}