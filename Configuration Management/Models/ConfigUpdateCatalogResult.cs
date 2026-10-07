using System.Collections.Generic;

namespace Configuration_Management.Models;

/// <summary>
/// Результат получения полного каталога версий конфигурации со страницы
/// <c>releases.1c.ru/project/<ник></c> (issue #352): все строки таблицы
/// #versionsTable с колонкой «Список версий». Ошибки сети/авторизации не бросают
/// исключения — итог описывается статусом <see cref="PortalFetchStatus"/> и ключом
/// локализации «Updates.*».
/// </summary>
public sealed class ConfigUpdateCatalogResult
{
    /// <summary>Итоговый статус обращения к порталу.</summary>
    public PortalFetchStatus Status { get; init; } = PortalFetchStatus.NetworkError;

    /// <summary>Ключ локализации сообщения об ошибке (префикс «Updates.*»);
    /// пуст для успешного результата.</summary>
    public string ErrorKey { get; init; } = string.Empty;

    /// <summary>Все версии каталога, отсортированные по убыванию, с колонкой
    /// «Список версий» (<see cref="PlatformRelease.Sources"/>).</summary>
    public IReadOnlyList<PlatformRelease> Releases { get; init; } = new List<PlatformRelease>();
}