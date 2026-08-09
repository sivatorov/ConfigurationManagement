namespace Configuration_Management.Models;

/// <summary>
/// Настройки интерфейса приложения, сохраняемые между запусками.
/// </summary>
public class AppSettings
{
    /// <summary>Показывать только избранные базы.</summary>
    public bool ShowFavoritesOnly { get; set; }

    /// <summary>Группировать базы по группам.</summary>
    public bool GroupByGroup { get; set; } = true;

    /// <summary>Название выбранной темы оформления.</summary>
    public string Theme { get; set; } = string.Empty;

    /// <summary>Имена групп, свёрнутых в списке баз.</summary>
    public List<string> CollapsedGroups { get; set; } = new();

    /// <summary>Список установленных версий платформы 1С.</summary>
    public List<string> InstalledPlatformVersions { get; set; } = new();

    /// <summary>Ширина колонки «Название» в списке баз (0 — по умолчанию).</summary>
    public double NameColumnWidth { get; set; }

    /// <summary>Ширина колонки «Версия платформы» в списке баз (0 — по умолчанию).</summary>
    public double VersionColumnWidth { get; set; }

    /// <summary>Ширина колонки «Режим запуска» в списке баз (0 — по умолчанию).</summary>
    public double LaunchModeColumnWidth { get; set; }

    /// <summary>Ширина колонки «Сервер/База» в списке баз (0 — по умолчанию).</summary>
    public double ServerColumnWidth { get; set; }
}