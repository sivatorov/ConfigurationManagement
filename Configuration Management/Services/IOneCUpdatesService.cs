using System.Threading;
using System.Threading.Tasks;
using Configuration_Management.Models;

namespace Configuration_Management.Services;

/// <summary>
/// Сервис проверки обновлений типовых конфигураций 1С по web-ресурсу обновлений.
/// Формирует web-адрес по правилу 1С
/// <c>downloads.1c.ru/ipp/.../Configs/<Конфигурация>/<Ред>/<Подред>/</c>,
/// проверяет наличие новых релизов и скачивает дистрибутив с прогрессом.
/// Кроссплатформенный — не зависит от UI-фреймворка.
/// </summary>
public interface IOneCUpdatesService
{
    /// <summary>
    /// Формирует адрес каталога релизов по правилу 1С. Если задан <paramref name="urlOverride"/> —
    /// возвращается он (ручная корректировка), иначе адрес строится из ника конфигурации
    /// (<c>releases.1c.ru/project/<ник></c>). Каждый сегмент экранируется, итог проверяется
    /// на валидность URI.
    /// </summary>
    /// <param name="config">Типовая конфигурация (нужен ник каталога релизов).</param>
    /// <param name="edition">Редакция (может переопределять ссылку целиком). Может быть null.</param>
    /// <param name="urlOverride">Полностью переопределённая ссылка. Пустая строка/null — автоформирование.</param>
    /// <param name="urlSegment">Персональный сегмент (ник) базы. Если задан — используется вместо
    /// ника типовой конфигурации (issue #322). Пустая строка/null — ник конфигурации.</param>
    string BuildUpdateUrl(OneCConfigType? config, OneCConfigEdition? edition, string? urlOverride, string? urlSegment = null);

    /// <summary>
    /// Проверяет наличие обновлений по заданному URL каталога релизов: загружает страницу,
    /// устойчиво ищет ссылки на архивы дистрибутивов и определяет максимальную версию.
    /// Ошибки сети/HTTP не бросают исключение — результат возвращается со статусом
    /// <see cref="ConfigUpdateStatus.Failed"/>.
    /// </summary>
    Task<ConfigUpdateCheckResult> CheckForUpdatesAsync(
        string configName, string currentVersion, string url, CancellationToken ct = default);

    /// <summary>
    /// Скачивает дистрибутив обновления по прямой ссылке в целевой файл с прогрессом.
    /// Возвращает полный путь сохранённого файла или null при ошибке/отмене.
    /// </summary>
    Task<string?> DownloadUpdateAsync(
        string url, string targetPath, IProgress<double>? progress = null, CancellationToken ct = default);

    /// <summary>
    /// Скачивает файл дистрибутива технологической платформы по прямой ссылке:
    /// сначала пытается многопоточная загрузка (HTTP Range, до конца файла), при
    /// неудаче — существующий однопоточный путь с авторизацией портала. Возвращает
    /// полный путь сохранённого файла или null при ошибке/отмене.
    /// </summary>
    /// <param name="url">Прямая ссылка на файл дистрибутива.</param>
    /// <param name="targetPath">Полный путь итогового файла (каталог создаётся).</param>
    /// <param name="progress">Прогресс загрузки 0..1.</param>
    /// <param name="ct">Токен отмены.</param>
    Task<string?> DownloadDistributionAsync(
        string url, string targetPath, IProgress<double>? progress = null, CancellationToken ct = default);

    /// <summary>
    /// Выполняет авторизованный GET (Basic Auth + cookie-сессия портала) по указанному
    /// адресу и возвращает тело ответа как строку. Ошибки сети/HTTP не бросают исключение:
    /// при неуспехе возвращается null.
    /// </summary>
    /// <param name="url">Адрес страницы каталога или ответа version_files.</param>
    /// <param name="ct">Токен отмены.</param>
    Task<string?> GetPageTextAsync(string url, CancellationToken ct = default);

    /// <summary>
    /// Выполняет авторизованный GET по указанному адресу и возвращает текст ответа вместе
    /// со статусом обращения. В отличие от <see cref="GetPageTextAsync"/> позволяет отличить
    /// «требуется вход» (AuthRequired) от «вход не подтверждён сервером» (AuthFailed —
    /// HTTP 401 после POST учётных данных либо цепочка редиректов завершилась на странице
    /// входа, issue #334/#330/#323) и от сетевой ошибки (NetworkError). Ошибки сети/HTTP
    /// не бросают исключение — итог описывается статусом <see cref="PortalFetchStatus"/>
    /// и текстом ответа при успехе.
    /// </summary>
    /// <param name="url">Адрес страницы каталога или ответа version_files.</param>
    /// <param name="ct">Токен отмены.</param>
    Task<PortalPageResult> FetchPageAsync(string url, CancellationToken ct = default);

    /// <summary>
    /// Получает полный каталог версий конфигурации со страницы <c>releases.1c.ru/project/<ник></c>
    /// (issue #352): все строки таблицы #versionsTable, включая колонку «Список версий»
    /// (<see cref="PlatformRelease.Sources"/>), отсортированные по убыванию. Используется для
    /// построения цепочки обновлений, когда последняя версия не обновляется напрямую с текущей.
    /// Ошибки сети/авторизации не бросают исключение — итог описывается статусом
    /// <see cref="ConfigUpdateCatalogResult.Status"/> и ключом локализации «Updates.*».
    /// </summary>
    /// <param name="url">Адрес страницы каталога релизов (project/<ник>).</param>
    /// <param name="ct">Токен отмены.</param>
    Task<ConfigUpdateCatalogResult> GetUpdateCatalogAsync(string url, CancellationToken ct = default);

    /// <summary>Предопределённый набор типовых конфигураций 1С.</summary>
    System.Collections.Generic.IReadOnlyList<OneCConfigType> BuiltInConfigTypes { get; }
}