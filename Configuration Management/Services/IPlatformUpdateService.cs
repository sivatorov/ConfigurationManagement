using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Configuration_Management.Models;

namespace Configuration_Management.Services;

/// <summary>
/// Сервис автообновления технологической платформы 1С: получение списка доступных
/// версий с портала <c>releases.1c.ru</c>, ленивая подгрузка файлов дистрибутива
/// выбранного релиза и выбор файла под текущую ОС/разрядность. Ошибки сети,
/// авторизации и отмены не бросают исключений наружу — результат несёт статус
/// <see cref="PortalFetchStatus"/> и ключ локализации.
/// </summary>
public interface IPlatformUpdateService
{
    /// <summary>
    /// Получает список доступных версий платформы со страницы
    /// <c>releases.1c.ru/project/Platform83</c> (через
    /// <see cref="IOneCUpdatesService.FetchPageAsync"/> и
    /// <see cref="OneCPlatformCatalogParser.ParseVersions"/>).
    /// </summary>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Результат со статусом и отсортированным по убыванию списком версий.</returns>
    Task<PlatformCatalogResult> GetAvailableReleasesAsync(CancellationToken ct = default);

    /// <summary>
    /// Получает объединённый список доступных версий платформы со ВСЕХ поддерживаемых
    /// каталогов (<c>Platform83</c> и <c>Platform85</c>, issue #330/#334): каждый каталог
    /// запрашивается с <c>allUpdates=true</c> (полный список версий, а не только последние
    /// релизы), результаты дедуплицируются по версии и сортируются по убыванию. Сбой
    /// одного каталога не роняет общий результат — возвращаются версии полученных каталогов.
    /// </summary>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Результат со статусом Ok и отсортированным по убыванию списком версий,
    /// либо ошибкой (если ни один каталог не получен).</returns>
    Task<PlatformCatalogResult> GetAllAvailableReleasesAsync(CancellationToken ct = default);

    /// <summary>
    /// Получает список доступных версий платформы с указанного каталога
    /// <c>releases.1c.ru/project/<nick></c> (например, <c>Platform85</c>, issue #334).
    /// Каталог запрашивается с <c>allUpdates=true</c> (полный список версий, issue #330).
    /// Поведение идентично <see cref="GetAvailableReleasesAsync"/>, отличается только ник каталога.
    /// </summary>
    /// <param name="nick">Ник каталога платформы (например, <c>Platform83</c>/<c>Platform85</c>).</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Результат со статусом и отсортированным по убыванию списком версий.</returns>
    Task<PlatformCatalogResult> GetAvailableReleasesForNickAsync(string nick, CancellationToken ct = default);

    /// <summary>
    /// Лениво подгружает файлы дистрибутива выбранной версии из ответа
    /// <c>version_files?nick=Platform83&ver=…</c> (через
    /// <see cref="OneCPlatformCatalogParser.ParseDistributionFiles"/>). Заполняет
    /// <see cref="PlatformRelease.Files"/> переданного релиза, приводит
    /// <see cref="PlatformRelease.VersionFilesUrl"/> к абсолютному адресу и возвращает
    /// тот же экземпляр в <see cref="PlatformCatalogResult.Release"/>.
    /// </summary>
    /// <param name="release">Релиз, для которого подгружаются файлы (мутируется).</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Результат с заполненным <see cref="PlatformCatalogResult.Release"/>.</returns>
    /// <summary>
    /// Лениво подгружает файлы дистрибутива выбранной версии из ответа
    /// <c>version_files?nick=<ник релиза>&ver=…</c> (ник берётся из
    /// <see cref="PlatformRelease.Nick"/>, иначе — <c>Platform83</c>; issue #334:
    /// версии 8.5 живут в каталоге Platform85). Заполняет
    /// <see cref="PlatformRelease.Files"/> переданного релиза, приводит
    /// <see cref="PlatformRelease.VersionFilesUrl"/> к абсолютному адресу и возвращает
    /// тот же экземпляр в <see cref="PlatformCatalogResult.Release"/>.
    /// </summary>
    /// <param name="release">Релиз, для которого подгружаются файлы (мутируется).</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Результат с заполненным <see cref="PlatformCatalogResult.Release"/>.</returns>
    Task<PlatformCatalogResult> LoadReleaseFilesAsync(PlatformRelease release, CancellationToken ct = default);

    /// <summary>
    /// Лениво подгружает файлы дистрибутива выбранной версии с указанного каталога
    /// <c>version_files?nick=<nick>&ver=…</c> (например, <c>Platform85</c>, issue #334).
    /// Поведение идентично <see cref="LoadReleaseFilesAsync(PlatformRelease, CancellationToken)"/>,
    /// отличается только ник каталога (используется при отсутствии ссылки у релиза).
    /// </summary>
    /// <param name="release">Релиз, для которого подгружаются файлы (мутируется).</param>
    /// <param name="nick">Ник каталога платформы (например, <c>Platform83</c>/<c>Platform85</c>).</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Результат с заполненным <see cref="PlatformCatalogResult.Release"/>.</returns>
    Task<PlatformCatalogResult> LoadReleaseFilesForNickAsync(PlatformRelease release, string nick, CancellationToken ct = default);

    /// <summary>
    /// Выбирает файл дистрибутива под текущую ОС и разрядность (чистый метод):
    /// на Windows приоритет zip-архива с setup.exe (x64 предпочтительнее x86),
    /// на Linux — пакет .deb/.rpm (x64), при отсутствии — универсальный .tar.gz.
    /// Пустой список возвращает null.
    /// </summary>
    /// <param name="files">Файлы дистрибутива релиза.</param>
    /// <returns>Выбранный файл или null, если подходящего нет.</returns>
    PlatformReleaseFile? PickDistribution(IReadOnlyList<PlatformReleaseFile> files);
}