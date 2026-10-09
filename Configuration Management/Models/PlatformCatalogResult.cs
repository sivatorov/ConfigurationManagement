using System.Collections.Generic;

namespace Configuration_Management.Models;

/// <summary>
/// Статус обращения к каталогу технологической платформы 1С на <c>releases.1c.ru</c>.
/// </summary>
public enum PortalFetchStatus
{
    /// <summary>Каталог получен успешно (или файлы релиза подгружены).</summary>
    Ok,

    /// <summary>Требуется авторизация на портале 1С (редирект на <c>login.1c.ru</c>, 401/403).</summary>
    AuthRequired,

    /// <summary>Программный вход на портал 1С не подтверждён сервером (HTTP 401 после POST
    /// учётных данных либо цепочка редиректов завершилась на странице входа): логин/пароль
    /// не приняты либо изменилась форма входа.</summary>
    AuthFailed,

    /// <summary>Исчерпан лимит попыток входа на portal.1c.ru за сессию службы (анти-брутфорс);
    /// новая попытка разрешается автоматически через несколько минут либо после явного
    /// действия пользователя (смена учётных данных ИТС). Отдельный статус, чтобы показать
    /// понятную ошибку вместо вводящего в заблуждение AuthRequired/AuthFailed
    /// (issue #334/#330/#323).</summary>
    LoginLimitReached,

    /// <summary>Форма входа на portal.1c.ru недоступна для программного входа: не получен HTML,
    /// отсутствуют классические токены CAS (execution/lt) либо форма изменилась радикально
    /// (OAuth/JS-челлендж — маркеры oauth/client_id/challenge/csrf, issue #323/#330/#334).
    /// Автоматический вход невозможен; пользователю нужен браузер (или импорт cookie в будущем).</summary>
    FormUnavailable,

    /// <summary>Запрошенный ресурс не найден (HTTP 404 — страница-маркер «404 Not Found»).</summary>
    NotFound,

    /// <summary>Сетевая ошибка, пустое тело ответа либо неизвестный сбой.</summary>
    NetworkError,

    /// <summary>Операция отменена через <see cref="System.Threading.CancellationToken"/>.</summary>
    Cancelled,
}

/// <summary>
/// Результат обращения к каталогу технологической платформы 1С: статус и список
/// доступных версий (<see cref="Releases"/>) либо файлы выбранного релиза
/// (<see cref="Release"/>). Ошибки сети/авторизации не бросают исключений — итог
/// всегда описывается статусом и ключом локализации.
/// </summary>
public sealed class PlatformCatalogResult
{
    /// <summary>Итоговый статус обращения к порталу.</summary>
    public PortalFetchStatus Status { get; init; } = PortalFetchStatus.Ok;

    /// <summary>Ключ локализации сообщения об ошибке (префикс «PlatformUpdate.Error.*»);
    /// пуст для успешного результата.</summary>
    public string ErrorKey { get; init; } = string.Empty;

    /// <summary>Список доступных версий платформы (заполняется
    /// <c>GetAvailableReleasesAsync</c>), отсортированный по убыванию.</summary>
    public IReadOnlyList<PlatformRelease> Releases { get; init; } = new List<PlatformRelease>();

    /// <summary>Релиз с подгруженными файлами дистрибутива (для <c>LoadReleaseFilesAsync</c>).</summary>
    public PlatformRelease? Release { get; init; }

    /// <summary>Адрес страницы, из которой получен результат (диагностика issue #330:
    /// попадает в журнал окна «Скачивание версии платформы 1С», чтобы пользователь мог
    /// прислать его при пустом списке файлов). null — диагностика не заполнялась.</summary>
    public string? FetchedUrl { get; init; }

    /// <summary>Длина тела ответа в символах (диагностика issue #330).</summary>
    public int BodyLength { get; init; }

    /// <summary>Число распознанных файлов дистрибутива в ответе (диагностика issue #330).</summary>
    public int ParsedFileCount { get; init; }
}

/// <summary>
/// Результат авторизованного GET страницы портала 1С (<c>IOneCUpdatesService.FetchPageAsync</c>):
/// статус обращения и текст ответа при успехе. Ошибки сети/авторизации не бросают исключений —
/// итог описывается статусом <see cref="PortalFetchStatus"/>.
/// </summary>
public sealed class PortalPageResult
{
    /// <summary>Итоговый статус обращения к странице портала.</summary>
    public PortalFetchStatus Status { get; init; } = PortalFetchStatus.NetworkError;

    /// <summary>Текст ответа (при <see cref="Status"/> == <see cref="PortalFetchStatus.Ok"/>).</summary>
    public string? Text { get; init; }
}