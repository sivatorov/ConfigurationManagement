namespace Configuration_Management.Models;

/// <summary>
/// Начальная вкладка окна свойств базы при его открытии (issue #355).
/// Двойной клик по колонкам «Конфигурация»/«№ релиза» открывает свойства
/// сразу на вкладке «Платформа»; остальные сценарии используют вкладку по умолчанию.
/// </summary>
public enum InfobasePropertiesTab
{
    /// <summary>Вкладка по умолчанию («База», первая вкладка окна).</summary>
    Default = 0,

    /// <summary>Вкладка «Платформа» (issue #355).</summary>
    Platform = 1
}

/// <summary>
/// Порядок вкладок окна свойств базы (<see cref="Views.ConnectionSettingsWindow"/>).
/// Общий для WPF- и Avalonia-версий окна: обе строят вкладки в одном и том же
/// порядке (База, Подключение, Хранилище конфигурации, Авторизация, Запуск,
/// Разрядность, Платформа, Идентификатор).
/// </summary>
public static class InfobasePropertiesTabs
{
    public const int BaseTabIndex = 0;
    public const int ConnectionTabIndex = 1;
    public const int RepositoryTabIndex = 2;
    public const int AuthTabIndex = 3;
    public const int LaunchTabIndex = 4;
    public const int BitnessTabIndex = 5;
    public const int PlatformTabIndex = 6;
    public const int IdTabIndex = 7;

    /// <summary>Количество вкладок окна свойств базы.</summary>
    public const int TabCount = 8;

    /// <summary>
    /// Возвращает индекс начальной вкладки окна свойств для значения
    /// <paramref name="tab"/>. <see cref="InfobasePropertiesTab.Default"/>
    /// соответствует первой вкладке («База»).
    /// </summary>
    public static int GetTabIndex(InfobasePropertiesTab tab) =>
        tab == InfobasePropertiesTab.Platform ? PlatformTabIndex : BaseTabIndex;

    /// <summary>
    /// Является ли ключ колонки списка баз одной из конфигурационных колонок
    /// («Конфигурация» или «№ релиза»). Двойной клик по такой колонке открывает
    /// свойства базы на вкладке «Платформа» (issue #355). Используется и в WPF
    /// (значение <c>Tag</c> ячейки строки), и в Avalonia (ключ колонки из
    /// построителя колонок), поэтому строки-литералы собраны в одном месте.
    /// </summary>
    public static bool IsConfigurationColumnKey(string? key) =>
        key is "Configuration" or "ConfigurationVersion";
}
