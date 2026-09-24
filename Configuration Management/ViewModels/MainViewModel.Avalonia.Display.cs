#if LINUX
using System.Collections.Generic;
using Avalonia.Controls.ApplicationLifetimes;
using Configuration_Management.Localization;
using Configuration_Management.Models;
using Configuration_Management.Themes;

namespace Configuration_Management.ViewModels;

/// <summary>Main ViewModel (Avalonia/Linux): отображение, колонки, статус-бар, шрифты, компактный режим (partial).</summary>
public partial class MainViewModel : ViewModelBase
{
    // ---- Компактный режим интерфейса ----

    /// <summary>Компактный режим интерфейса (уменьшенные иконки, отступы, расстояния).</summary>
    public bool CompactMode
    {
        get => _compactMode;
        set
        {
            if (SetProperty(ref _compactMode, value))
            {
                _settings.CompactMode = value;
                SaveSettingsSilently();
                OnCompactModeChanged?.Invoke(value);
            }
        }
    }

    /// <summary>Событие изменения компактного режима (для перестроения главного окна).</summary>
    public event Action<bool>? OnCompactModeChanged;

    // ---- Текущая сессия ----

    /// <summary>Показывать блок «Текущая сессия» в правой панели.</summary>
    public bool ShowSessionLaunchPanel
    {
        get => _settings.ShowSessionLaunchPanel;
        set
        {
            if (_settings.ShowSessionLaunchPanel == value)
                return;
            _settings.ShowSessionLaunchPanel = value;
            SaveSettingsSilently();
            OnPropertyChanged(nameof(ShowSessionLaunchPanel));
        }
    }

    /// <summary>
    /// Применяет настройки вкладки «Отображение» и сохраняет их разом, как это
    /// делает WPF-версия по кнопке в окне настроек: по одному сохранению
    /// на переключатель файл переписывался бы десяток раз.
    /// </summary>
    public void ApplyDisplaySettings(
        bool showFavoritesButton, bool showPinnedButton, bool showTags, bool showTagFilterPanel,
        bool showVersionColumn, bool showConfigurationColumn, bool showConfigurationVersionColumn,
        bool showLaunchModeColumn,
        bool showServerColumn, bool showLastLaunchColumn, bool showSizeColumn,
        bool showModifiedColumn,
        bool showActionsColumn,
        bool showRightPanelDetails, bool showSessionLaunchPanel,
        bool groupByGroup, bool showEmptyGroups,
        List<string>? columnOrder)
    {
        var previousShowFavoritesButton = _settings.ShowFavoritesButton;
        var previousShowPinnedButton = _settings.ShowPinnedButton;
        var previousShowTags = _settings.ShowTags;
        var previousShowVersionColumn = _settings.ShowVersionColumn;
        var previousShowConfigurationColumn = _settings.ShowConfigurationColumn;
        var previousShowConfigurationVersionColumn = _settings.ShowConfigurationVersionColumn;
        var previousShowLaunchModeColumn = _settings.ShowLaunchModeColumn;
        var previousShowServerColumn = _settings.ShowServerColumn;
        var previousShowLastLaunchColumn = _settings.ShowLastLaunchColumn;
        var previousShowSizeColumn = _settings.ShowSizeColumn;
        var previousShowModifiedColumn = _settings.ShowModifiedColumn;
        var previousShowActionsColumn = _settings.ShowActionsColumn;
        var previousColumnOrder = _settings.ColumnOrder ?? new List<string>();
        var previousGroupByGroup = _groupByGroup;
        var previousShowEmptyGroups = _showEmptyGroups;

        _settings.ShowFavoritesButton = showFavoritesButton;
        _settings.ShowPinnedButton = showPinnedButton;
        _settings.ShowTags = showTags;
        _settings.ShowTagFilterPanel = showTagFilterPanel;
        _settings.ShowVersionColumn = showVersionColumn;
        _settings.ShowConfigurationColumn = showConfigurationColumn;
        _settings.ShowConfigurationVersionColumn = showConfigurationVersionColumn;
        _settings.ShowLaunchModeColumn = showLaunchModeColumn;
        _settings.ShowServerColumn = showServerColumn;
        _settings.ShowLastLaunchColumn = showLastLaunchColumn;
        _settings.ShowSizeColumn = showSizeColumn;
        _settings.ShowModifiedColumn = showModifiedColumn;
        _settings.ShowActionsColumn = showActionsColumn;
        _settings.ColumnOrder = columnOrder ?? new List<string>();
        _settings.ShowRightPanelDetails = showRightPanelDetails;
        _settings.ShowSessionLaunchPanel = showSessionLaunchPanel;
        _settings.GroupByGroup = groupByGroup;
        _settings.ShowEmptyGroups = showEmptyGroups;

        _showTagFilterPanel = showTagFilterPanel;
        _showRightPanelDetails = showRightPanelDetails;
        _groupByGroup = groupByGroup;
        _showEmptyGroups = showEmptyGroups;

        // Дерево трогаем, только если изменилось то, что на него влияет:
        // иначе переключатель правой панели сбрасывал бы выделение и прокрутку.
        var treeAffected = showTags != previousShowTags
            || groupByGroup != previousGroupByGroup
            || showEmptyGroups != previousShowEmptyGroups
            || showFavoritesButton != previousShowFavoritesButton
            || showPinnedButton != previousShowPinnedButton
            || showVersionColumn != previousShowVersionColumn
            || showConfigurationColumn != previousShowConfigurationColumn
            || showConfigurationVersionColumn != previousShowConfigurationVersionColumn
            || showLaunchModeColumn != previousShowLaunchModeColumn
            || showServerColumn != previousShowServerColumn
            || showLastLaunchColumn != previousShowLastLaunchColumn
            || showSizeColumn != previousShowSizeColumn
            || showModifiedColumn != previousShowModifiedColumn
            || showActionsColumn != previousShowActionsColumn
            || !previousColumnOrder.SequenceEqual(_settings.ColumnOrder);

        SaveSettingsSilently();

        NotifyColumnSettings();
        NotifySessionSettings();
        OnPropertyChanged(nameof(ShowTagFilterPanel));
        OnPropertyChanged(nameof(ShowActionsColumn));
        OnPropertyChanged(nameof(ShowRightPanelDetails));
        OnPropertyChanged(nameof(ShowConnectionInfo));
        OnPropertyChanged(nameof(ShowRightPanelHint));
        OnPropertyChanged(nameof(GroupByGroup));
        OnPropertyChanged(nameof(ShowEmptyGroups));
        OnPropertyChanged(nameof(ShowExpandCollapseButtons));

        // Состав колонок и группировка меняют и строки, и заголовок.
        if (treeAffected)
            RebuildTree();
    }

    /// <summary>
    /// Меняет видимость одной колонки списка баз по её ключу (issue #173).
    /// Прочие параметры отображения берутся из текущих настроек и применяются через
    /// <see cref="ApplyDisplaySettings"/>, поэтому изменение сохраняется и пересобирает
    /// дерево/заголовок теми же механизмами, что и правка в окне настроек.
    /// </summary>
    public void SetColumnVisible(string key, bool visible)
    {
        ApplyDisplaySettings(
            _settings.ShowFavoritesButton,
            _settings.ShowPinnedButton,
            _settings.ShowTags,
            _showTagFilterPanel,
            key == "Version" ? visible : _settings.ShowVersionColumn,
            key == "Configuration" ? visible : _settings.ShowConfigurationColumn,
            key == "ConfigurationVersion" ? visible : _settings.ShowConfigurationVersionColumn,
            key == "LaunchMode" ? visible : _settings.ShowLaunchModeColumn,
            key == "ServerBase" ? visible : _settings.ShowServerColumn,
            key == "LastLaunch" ? visible : _settings.ShowLastLaunchColumn,
            key == "Size" ? visible : _settings.ShowSizeColumn,
            key == "Modified" ? visible : _settings.ShowModifiedColumn,
            key == "Actions" ? visible : _settings.ShowActionsColumn,
            _showRightPanelDetails,
            _settings.ShowSessionLaunchPanel,
            _groupByGroup,
            _showEmptyGroups,
            _settings.ColumnOrder);
    }

    /// <summary>
    /// Применяет состав нижней панели (строки состояния) и сразу пересобирает
    /// её текст, чтобы изменение было видно без переключения базы.
    /// </summary>
    public void ApplyStatusBarSettings(
        bool connectionPath, bool architecture, bool launchMode, bool port,
        bool platformVersion, bool clientType, bool connectionType, bool user,
        bool showId)
    {
        _settings.StatusShowConnectionPath = connectionPath;
        _settings.StatusShowArchitecture = architecture;
        _settings.StatusShowLaunchMode = launchMode;
        _settings.StatusShowPort = port;
        _settings.StatusShowPlatformVersion = platformVersion;
        _settings.StatusShowClientType = clientType;
        _settings.StatusShowConnectionType = connectionType;
        _settings.StatusShowUser = user;
        _settings.StatusShowId = showId;

        SaveSettingsSilently();

        OnPropertyChanged(nameof(StatusShowConnectionPath));
        OnPropertyChanged(nameof(StatusShowPort));
        OnPropertyChanged(nameof(StatusShowArchitecture));
        OnPropertyChanged(nameof(StatusShowPlatformVersion));
        OnPropertyChanged(nameof(StatusShowLaunchMode));
        OnPropertyChanged(nameof(StatusShowClientType));
        OnPropertyChanged(nameof(StatusShowConnectionType));
        OnPropertyChanged(nameof(StatusShowUser));
        OnPropertyChanged(nameof(StatusShowId));

        UpdateStatus();
    }

    // ======================= Свойства =======================

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
                ApplyFilter();
        }
    }

    public bool ShowTagFilterPanel
    {
        get => _showTagFilterPanel;
        set
        {
            if (!SetProperty(ref _showTagFilterPanel, value))
                return;
            _settings.ShowTagFilterPanel = value;
            SaveSettingsSilently();
        }
    }

    public bool GroupByGroup
    {
        get => _groupByGroup;
        set
        {
            if (SetProperty(ref _groupByGroup, value))
            {
                // Кнопки «развернуть/свернуть/сортировать группы» видны только
                // при группировке, поэтому их видимость идёт следом.
                OnPropertyChanged(nameof(ShowExpandCollapseButtons));
                ApplyFilter();
            }
        }
    }

    public bool ShowEmptyGroups
    {
        get => _showEmptyGroups;
        set
        {
            if (SetProperty(ref _showEmptyGroups, value))
            {
                RebuildTree();
                // Выбор надо сохранять: у автора здесь ScheduleSaveSettings,
                // иначе он теряется при перезапуске, а любое сохранение настроек
                // возвращает старое значение.
                _settings.ShowEmptyGroups = value;
                SaveSettingsSilently();
            }
        }
    }

    public bool IsListModeAll
    {
        get => _listMode == "All";
        set { if (value) SetListMode("All"); }
    }

    public bool IsListModeFavorites
    {
        get => _listMode == "Favorites";
        set { if (value) SetListMode("Favorites"); }
    }

    public bool IsListModeRecent
    {
        get => _listMode == "Recent";
        set { if (value) SetListMode("Recent"); }
    }

    private void SetListMode(string mode)
    {
        if (_listMode == mode)
            return;
        _listMode = mode;
        _settings.ShowFavoritesOnly = mode == "Favorites";
        SaveSettingsSilently();
        OnPropertyChanged(nameof(IsListModeAll));
        OnPropertyChanged(nameof(IsListModeFavorites));
        OnPropertyChanged(nameof(IsListModeRecent));
        ApplyFilter();
    }

    public bool ShowRightPanelDetails
    {
        get => _showRightPanelDetails;
        set
        {
            // Компактный режим правой панели должен переживать перезапуск (issue #149):
            // сохраняем признак в настройки, как делает WPF-версия.
            if (SetPropertyWithRelated(ref _showRightPanelDetails, value, nameof(ShowRightPanelDetails), nameof(RightPanelToggleTooltip), nameof(ShowConnectionInfo), nameof(OpenByLinkCaption), nameof(ShowRightPanelHint)))
            {
                _settings.ShowRightPanelDetails = value;
                SaveSettingsSilently();
            }
        }
    }

    /// <summary>
    /// Подпись кнопки открытия по ссылке: короткая, когда подробности правой
    /// панели скрыты. В разметке WPF это триггер по ShowRightPanelDetails.
    /// </summary>
    public string OpenByLinkCaption => ShowRightPanelDetails
        ? LocalizationManager.T("LinkInput.Title")
        : LocalizationManager.T("Main.OpenByLinkShort");

    /// <summary>
    /// Заголовок правой панели: имя базы, имя группы или «Нет выбора».
    /// Без него при пустом выборе от заголовка оставался один значок.
    /// </summary>
    public string RightPanelTitle =>
        SelectedInfobase?.Name
        ?? SelectedGroupNode?.DisplayName
        ?? LocalizationManager.T("Main.NoSelection");

    /// <summary>
    /// Подзаголовок правой панели: группа выбранной базы или полный путь
    /// выбранной группы, как в WPF-версии.
    /// </summary>
    public string RightPanelSubtitle =>
        SelectedInfobase is { } infobase
            // Как в разметке WPF: «Группа: <имя>», а не голое имя группы.
            ? $"{LocalizationManager.T("Main.GroupLabel")}: {infobase.GroupDisplay}"
            : SelectedGroupNode?.FullPath ?? string.Empty;

    /// <summary>Подсказка «выберите базу» под заголовком, пока база не выбрана.</summary>
    public string RightPanelHint => LocalizationManager.T("Main.NoSelectionHint");

    /// <summary>Значок заголовка: база, значок выбранной группы или ничего.</summary>
    public string? RightPanelIconKey =>
        SelectedInfobase is not null ? "IconDatabase" : SelectedGroupNode?.Icon;

    /// <summary>Показывать значок заголовка: для базы и для группы, но не при пустом выборе.</summary>
    public bool HasRightPanelIcon => RightPanelIconKey is not null;

    /// <summary>Выбрана база, а не группа и не пустота.</summary>
    public bool IsInfobaseSelected => SelectedInfobase is not null;

    /// <summary>
    /// Показывать подсказку «выберите базу» под заголовком правой панели.
    /// Видна, только когда подробности панели включены и база ещё не выбрана:
    /// в компактном режиме она не должна появляться даже при выбранной группе
    /// (issue #149).
    /// </summary>
    public bool ShowRightPanelHint => ShowRightPanelDetails && !IsInfobaseSelected;

    /// <summary>
    /// Показывать таблицу сведений о подключении: только когда выбрана база
    /// и включён показ подробностей.
    /// </summary>
    public bool ShowConnectionInfo => IsInfobaseSelected && ShowRightPanelDetails;

    public string RightPanelToggleTooltip => _showRightPanelDetails
        ? LocalizationManager.T("Main.CollapseRightPanel")
        : LocalizationManager.T("Main.ExpandRightPanel");

    public string StatusBarInfo
    {
        get => _statusBarInfo;
        set => SetProperty(ref _statusBarInfo, value);
    }

    /// <summary>
    /// Идёт длительная фоновая работа: окно закрывается затемняющим индикатором,
    /// как в разметке (MainWindow.xaml:2349).
    /// </summary>
    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    /// <summary>Что именно делается: подпись внутри индикатора.</summary>
    public string LoadingMessage
    {
        get => _loadingMessage;
        set => SetProperty(ref _loadingMessage, value);
    }

    public bool IsExporting
    {
        get => _isExporting;
        set => SetProperty(ref _isExporting, value);
    }

    public string ExportIndicatorTooltip
    {
        get => _exportIndicatorTooltip;
        set => SetProperty(ref _exportIndicatorTooltip, value);
    }

    // ---- Горячие клавиши ----
    // Раньше здесь стояли зашитые сочетания, и настройки пользователя
    // Linux-версия игнорировала: файл настроек общий с WPF-версией, а клавиши
    // в нём другие. Теперь читаются оттуда.
    public string HotkeyEnterprise => _settings.HotkeyEnterprise;
    public string HotkeyConfigurator => _settings.HotkeyConfigurator;
    public string HotkeyEdit => _settings.HotkeyEdit;
    public string HotkeyAdd => _settings.HotkeyAdd;
    public string HotkeyFavorite => _settings.HotkeyFavorite;
    public string HotkeyPin => _settings.HotkeyPin;
    public string HotkeyDelete => _settings.HotkeyDelete;
    public string HotkeyClearCache => _settings.HotkeyClearCache;
    public string HotkeyShowAll => _settings.HotkeyShowAll;
    public string HotkeyShowFavorites => _settings.HotkeyShowFavorites;
    public string HotkeyShowRecent => _settings.HotkeyShowRecent;
    public string HotkeyClearSearch => _settings.HotkeyClearSearch;
    public string HotkeyClearTags => _settings.HotkeyClearTags;
    public string HotkeyRightPanelDetails => _settings.HotkeyRightPanelDetails;
    public string HotkeyFindInList => _settings.HotkeyFindInList;
    public string HotkeySwitchUser => _settings.HotkeySwitchUser;
    public string HotkeyCheckUpdate => _settings.HotkeyCheckUpdate;
    public string HotkeyActualReleases => _settings.HotkeyActualReleases;
    // Сценарии резервирования (функции №16/№18): выполнение сценария (Ctrl+Shift+F5) и «Список выгрузок» (Ctrl+Shift+F7).
    public string HotkeyRunBackup => _settings.HotkeyRunBackup;
    public string HotkeyExportsList => _settings.HotkeyExportsList;

    /// <summary>
    /// Сохраняет назначенные сочетания и сообщает окну, что их надо
    /// перерегистрировать: подписи в меню и сами привязки берутся отсюда.
    /// </summary>
    public void ApplyHotkeys(string enterprise, string configurator, string edit, string add,
        string favorite, string pin, string delete, string clearCache,
        string showAll, string showFavorites, string showRecent,
        string clearSearch, string clearTags, string rightPanelDetails, string switchUser,
        string findInList = "", string sessionLock = "", string lockApp = "",
        string checkIntegrity = "", string serverConsole = "")
    {
        _settings.HotkeyEnterprise = enterprise ?? string.Empty;
        _settings.HotkeyConfigurator = configurator ?? string.Empty;
        _settings.HotkeyEdit = edit ?? string.Empty;
        _settings.HotkeyAdd = add ?? string.Empty;
        _settings.HotkeyFavorite = favorite ?? string.Empty;
        _settings.HotkeyPin = pin ?? string.Empty;
        _settings.HotkeyDelete = delete ?? string.Empty;
        _settings.HotkeyClearCache = clearCache ?? string.Empty;
        _settings.HotkeyShowAll = showAll ?? string.Empty;
        _settings.HotkeyShowFavorites = showFavorites ?? string.Empty;
        _settings.HotkeyShowRecent = showRecent ?? string.Empty;
        _settings.HotkeyClearSearch = clearSearch ?? string.Empty;
        _settings.HotkeyClearTags = clearTags ?? string.Empty;
        _settings.HotkeyRightPanelDetails = rightPanelDetails ?? string.Empty;
        _settings.HotkeyFindInList = findInList ?? string.Empty;
        _settings.HotkeySwitchUser = switchUser ?? string.Empty;
        // Блокировка сеансов ИБ (функция №20, Ctrl+Alt+L) и временная блокировка приложения (функция №19).
        _settings.HotkeySessionLock = sessionLock ?? string.Empty;
        _settings.HotkeyLockApp = lockApp ?? string.Empty;
        // Администрирование ИБ (Этап 6, функция №29 + консоль серверов).
        _settings.HotkeyCheckIntegrity = checkIntegrity ?? string.Empty;
        _settings.HotkeyServerConsole = serverConsole ?? string.Empty;

        SaveSettingsSilently();

        OnPropertyChanged(nameof(HotkeyEnterprise));
        OnPropertyChanged(nameof(HotkeyConfigurator));
        OnPropertyChanged(nameof(HotkeyEdit));
        OnPropertyChanged(nameof(HotkeyAdd));
        OnPropertyChanged(nameof(HotkeyFavorite));
        OnPropertyChanged(nameof(HotkeyPin));
        OnPropertyChanged(nameof(HotkeyDelete));
        OnPropertyChanged(nameof(HotkeyClearCache));
        OnPropertyChanged(nameof(HotkeyShowAll));
        OnPropertyChanged(nameof(HotkeyShowFavorites));
        OnPropertyChanged(nameof(HotkeyShowRecent));
        OnPropertyChanged(nameof(HotkeyClearSearch));
        OnPropertyChanged(nameof(HotkeyClearTags));
        OnPropertyChanged(nameof(HotkeyRightPanelDetails));
        OnPropertyChanged(nameof(HotkeyFindInList));
        OnPropertyChanged(nameof(HotkeySwitchUser));
        OnPropertyChanged(nameof(HotkeySessionLock));
        OnPropertyChanged(nameof(HotkeyLockApp));
        OnPropertyChanged(nameof(HotkeyCheckIntegrity));
        OnPropertyChanged(nameof(HotkeyServerConsole));
        HotkeysChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Сочетания переназначены: окну надо перерегистрировать привязки и меню.</summary>
    public event EventHandler? HotkeysChanged;

    // ---- Видимость колонок ----

    /// <summary>
    /// Порядок колонок списка баз по умолчанию (колонки «Конфигурация» и «№
    /// релиза» в самом конце). Используется, пока пользователь не задал
    /// собственный порядок.
    /// </summary>
    private static readonly string[] DefaultColumnOrder =
        { "Version", "LaunchMode", "Actions", "ServerBase", "LastLaunch", "Size", "Modified", "Configuration", "ConfigurationVersion" };

    /// <summary>
    /// Порядок колонок списка баз слева направо (кроме фиксированной колонки
    /// «Название», которая всегда первая). Если порядок не задан или пуст —
    /// возвращается порядок по умолчанию с колонками «Конфигурация» и «№
    /// релиза» в конце. Колонка «Действия» всегда присутствует в порядке:
    /// старые сохранённые настройки могли не содержать её вовсе, и тогда
    /// колонку нельзя было ни показать, ни отключить в окне настроек
    /// (issue #158). Колонка «№ релиза» (issue #217) вставляется сразу после
    /// «Конфигурации», не меняя сам сохранённый список.
    /// </summary>
    public IReadOnlyList<string> ColumnOrderKeys
    {
        get
        {
            var order = _settings.ColumnOrder;
            if (order is { Count: 0 })
                return DefaultColumnOrder;

            // «Действия», «№ релиза» и «Дата изменений» обязаны присутствовать в
            // списке: старые сохранённые настройки могли не содержать их вовсе, и
            // тогда эти колонки нельзя было ни показать, ни отключить в окне настроек.
            var needsActions = !order!.Contains("Actions", StringComparer.Ordinal);
            var needsConfigurationVersion = !order.Contains("ConfigurationVersion", StringComparer.Ordinal);
            var needsModified = !order.Contains("Modified", StringComparer.Ordinal);
            if (!needsActions && !needsConfigurationVersion && !needsModified)
                return order;

            var result = new List<string>(order.Count + 3);
            foreach (var key in order)
            {
                if (needsConfigurationVersion && key == "Configuration")
                    result.Add("ConfigurationVersion");
                // «Дата изменений» встаёт сразу после «Размера», как в порядке по
                // умолчанию, не меняя сам сохранённый список.
                if (needsModified && key == "Size")
                    result.Add("Modified");
                result.Add(key);
            }
            if (needsActions)
                result.Add("Actions");
            return result;
        }
    }

    public bool ShowExpandCollapseButtons => GroupByGroup;
    public bool ShowFavoritesButton => _settings.ShowFavoritesButton;
    public bool ShowPinnedButton => _settings.ShowPinnedButton;
    public bool ShowVersionColumn => _settings.ShowVersionColumn;
    public bool ShowConfigurationColumn => _settings.ShowConfigurationColumn;
    public bool ShowConfigurationVersionColumn => _settings.ShowConfigurationVersionColumn;
    public bool ShowLaunchModeColumn => _settings.ShowLaunchModeColumn;
    public bool ShowServerColumn => _settings.ShowServerColumn;
    public bool ShowLastLaunchColumn => _settings.ShowLastLaunchColumn;
    public bool ShowSizeColumn => _settings.ShowSizeColumn;
    public bool ShowModifiedColumn => _settings.ShowModifiedColumn;

    /// <summary>Показывать колонку «Действия» (кнопки запуска/конфигуратора/очистки кеша) в списке баз.</summary>
    public bool ShowActionsColumn => _settings.ShowActionsColumn;

    /// <summary>
    /// Состав нижней панели: какие сведения о выбранной базе в неё попадают.
    /// Набор и порядок повторяют версию для Windows.
    /// </summary>
    public bool StatusShowConnectionPath => _settings.StatusShowConnectionPath;
    public bool StatusShowPort => _settings.StatusShowPort;
    public bool StatusShowArchitecture => _settings.StatusShowArchitecture;
    public bool StatusShowPlatformVersion => _settings.StatusShowPlatformVersion;
    public bool StatusShowLaunchMode => _settings.StatusShowLaunchMode;
    public bool StatusShowClientType => _settings.StatusShowClientType;
    public bool StatusShowConnectionType => _settings.StatusShowConnectionType;
    public bool StatusShowUser => _settings.StatusShowUser;
    public bool StatusShowId => _settings.StatusShowId;

    /// <summary>Шрифт интерфейса по умолчанию и настройки отдельных областей.</summary>
    public string FontFamily => _settings.FontFamily;
    public double FontSize => _settings.FontSize;
    public string FontWeight => _settings.FontWeight;
    public string FontStyle => _settings.FontStyle;
    public IReadOnlyDictionary<string, ElementFontSettings> ElementFonts => _settings.ElementFonts;

    /// <summary>
    /// Применяет шрифты областей к главному окну без сохранения: кнопка
    /// «Применить» в настройках показывает результат до нажатия «Сохранить».
    /// </summary>
    public void PreviewElementFonts(Dictionary<string, ElementFontSettings> fonts)
    {
        if (MainWindowOrNull() is { } window)
            ThemeManager.ApplyElementFonts(window, fonts);
    }

    /// <summary>
    /// Сохраняет шрифты областей, применяет их ко всем окнам и пишет в настройки.
    /// Область «По умолчанию» задаёт заодно общий шрифт приложения, как в версии
    /// для Windows: он применяется ко всем окнам и при следующем запуске.
    /// </summary>
    public void SaveElementFonts(Dictionary<string, ElementFontSettings> fonts)
    {
        _settings.ElementFonts = fonts ?? new Dictionary<string, ElementFontSettings>();

        if (_settings.ElementFonts.TryGetValue(ThemeManager.FontDefault, out var def)
            && def is not null && def.FontSize > 0)
        {
            _settings.FontFamily = string.IsNullOrWhiteSpace(def.FontFamily)
                ? ThemeManager.DefaultFontFamily : def.FontFamily;
            _settings.FontSize = def.FontSize;
            _settings.FontWeight = string.Equals(def.FontWeight, "Bold", StringComparison.OrdinalIgnoreCase)
                ? "Bold" : ThemeManager.DefaultFontWeight;
            _settings.FontStyle = string.Equals(def.FontStyle, "Italic", StringComparison.OrdinalIgnoreCase)
                ? "Italic" : ThemeManager.DefaultFontStyle;
        }

        ThemeManager.ApplyFontToAllWindows(_settings.FontFamily, _settings.FontSize,
            _settings.FontWeight, _settings.FontStyle);
        PreviewElementFonts(_settings.ElementFonts);
        SaveSettingsSilently();

        OnPropertyChanged(nameof(FontFamily));
        OnPropertyChanged(nameof(FontSize));
        OnPropertyChanged(nameof(FontWeight));
        OnPropertyChanged(nameof(FontStyle));
    }

    private static MainWindow? MainWindowOrNull()
        => Avalonia.Application.Current?.ApplicationLifetime
            is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow as MainWindow
            : null;

    /// <summary>
    /// Показывать теги в строках списка. Переключатель живёт в панели
    /// инструментов над списком, состояние хранится в настройках.
    /// </summary>
    public bool ShowTags
    {
        get => _settings.ShowTags;
        set
        {
            if (_settings.ShowTags == value)
                return;

            _settings.ShowTags = value;
            SaveSettingsSilently();
            OnPropertyChanged(nameof(ShowTags));
            // Строки строятся с чипами или без них, поэтому пересобираются.
            ApplyFilter();
        }
    }

    public double NameColumnWidth => _settings.NameColumnWidth;
    public double VersionColumnWidth => _settings.VersionColumnWidth;
    public double ConfigurationColumnWidth => _settings.ConfigurationColumnWidth;
    public double ConfigurationVersionColumnWidth => _settings.ConfigurationVersionColumnWidth;
    public double LaunchModeColumnWidth => _settings.LaunchModeColumnWidth;
    public double ServerColumnWidth => _settings.ServerColumnWidth;
    public double LastLaunchColumnWidth => _settings.LastLaunchColumnWidth;
    public double SizeColumnWidth => _settings.SizeColumnWidth;
    public double ModifiedColumnWidth => _settings.ModifiedColumnWidth;
    public double ActionsColumnWidth => _settings.ActionsColumnWidth;

    /// <summary>
    /// Запоминает ширину колонки списка по её ключу. Уведомления намеренно нет:
    /// во время перетаскивания разделителя ширину уже применили и заголовку,
    /// и строкам, а уведомление пересобрало бы заголовок на каждое движение мыши.
    /// </summary>
    public void UpdateColumnWidth(string key, double width, bool save)
    {
        switch (key)
        {
            case "Name": _settings.NameColumnWidth = width; break;
            case "Version": _settings.VersionColumnWidth = width; break;
            case "Configuration": _settings.ConfigurationColumnWidth = width; break;
            case "ConfigurationVersion": _settings.ConfigurationVersionColumnWidth = width; break;
            case "LaunchMode": _settings.LaunchModeColumnWidth = width; break;
            case "ServerBase": _settings.ServerColumnWidth = width; break;
            case "LastLaunch": _settings.LastLaunchColumnWidth = width; break;
            case "Size": _settings.SizeColumnWidth = width; break;
            case "Modified": _settings.ModifiedColumnWidth = width; break;
            // Колонка «Действия» тоже перетаскиваемая и сохраняемая, как в разметке
            // (MainWindow.xaml:528): раньше её ширина была константой.
            case "Actions": _settings.ActionsColumnWidth = width; break;
            default: return;
        }

        if (save)
            SaveSettingsSilently();
    }

    // ---- Сортировка списка ----
    public string SortField => _sortField;
    public bool SortAscending => _sortAscending;

    /// <summary>
    /// Меняет поле сортировки списка баз. Повторный клик по тому же полю
    /// разворачивает направление.
    /// </summary>
    public void SetSortField(string field)
    {
        if (string.IsNullOrWhiteSpace(field))
            return;

        if (string.Equals(_sortField, field, StringComparison.OrdinalIgnoreCase))
            _sortAscending = !_sortAscending;
        else
        {
            _sortField = field;
            _sortAscending = field != "LastLaunchDate"; // дату удобнее сначала по убыванию
        }

        _settings.SortField = _sortField;
        _settings.SortAscending = _sortAscending;
        SaveSettingsSilently();
        OnPropertyChanged(nameof(SortField));
        OnPropertyChanged(nameof(SortAscending));
        RebuildTree();
    }

    /// <summary>
    /// Упорядочивает базы по выбранному полю. Закреплённые всегда идут первыми,
    /// имя служит вторым ключом, чтобы порядок не зависел от порядка в файле.
    /// </summary>
    private IEnumerable<Infobase> ApplyCurrentSort(IEnumerable<Infobase> source)
    {
        var query = source.OrderBy(i => i.GroupSortOrder);
        return _sortField switch
        {
            "LastLaunchDate" when _sortAscending =>
                query.ThenBy(i => i.LastLaunchDate ?? DateTime.MinValue)
                     .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase),
            "LastLaunchDate" =>
                query.ThenByDescending(i => i.LastLaunchDate ?? DateTime.MinValue)
                     .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase),
            "SortOrder" when _sortAscending =>
                query.ThenBy(i => i.SortOrder)
                     .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase),
            "SortOrder" =>
                query.ThenByDescending(i => i.SortOrder)
                     .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase),
            _ when _sortAscending =>
                query.ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase),
            _ =>
                query.ThenByDescending(i => i.Name, StringComparer.OrdinalIgnoreCase)
        };
    }

    /// <summary>
    /// Применяет компактный режим к главному окну (пересобирает UI с уменьшенными
    /// метриками). Вызывается из окна настроек при переключении переключателя.
    /// </summary>
    public void ApplyCompactMode(bool compact)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
            is IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow is Configuration_Management.MainWindow main)
            main.ApplyCompactMode(compact);
    }

    /// <summary>
    /// Применяет настройку «Системный заголовок окна» к главному окну сразу, без
    /// перезапуска (issue #159). Сама настройка уже сохранена через свойство
    /// <see cref="UseSystemTitleBar"/>; здесь только обновляется декор окна и
    /// сбрасывается кэш настройки модальных окон, чтобы новые диалоги тоже
    /// применили свежее значение.
    /// </summary>
    public void ApplySystemTitleBar(bool useSystemTitleBar)
    {
        Configuration_Management.ModalWindowBase.InvalidateSystemTitleBarCache();
        if (Avalonia.Application.Current?.ApplicationLifetime
            is IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow is Configuration_Management.MainWindow main)
            main.ApplySystemTitleBar(useSystemTitleBar);
    }

    private void NotifyColumnSettings()
    {
        OnPropertyChanged(nameof(ShowExpandCollapseButtons));
        OnPropertyChanged(nameof(ShowFavoritesButton));
        OnPropertyChanged(nameof(ShowPinnedButton));
        OnPropertyChanged(nameof(ShowVersionColumn));
        OnPropertyChanged(nameof(ShowConfigurationColumn));
        OnPropertyChanged(nameof(ShowConfigurationVersionColumn));
        OnPropertyChanged(nameof(ShowLaunchModeColumn));
        OnPropertyChanged(nameof(ShowServerColumn));
        OnPropertyChanged(nameof(ShowLastLaunchColumn));
        OnPropertyChanged(nameof(ShowSizeColumn));
        OnPropertyChanged(nameof(ShowModifiedColumn));
        OnPropertyChanged(nameof(ShowTags));
        OnPropertyChanged(nameof(NameColumnWidth));
        OnPropertyChanged(nameof(VersionColumnWidth));
        OnPropertyChanged(nameof(ConfigurationColumnWidth));
        OnPropertyChanged(nameof(ConfigurationVersionColumnWidth));
        OnPropertyChanged(nameof(LaunchModeColumnWidth));
        OnPropertyChanged(nameof(ServerColumnWidth));
        OnPropertyChanged(nameof(LastLaunchColumnWidth));
        OnPropertyChanged(nameof(SizeColumnWidth));
        OnPropertyChanged(nameof(ModifiedColumnWidth));
        OnPropertyChanged(nameof(ActionsColumnWidth));
        OnPropertyChanged(nameof(ColumnOrderKeys));
    }

    private void NotifySessionSettings()
    {
        OnPropertyChanged(nameof(ShowSessionLaunchPanel));
        NotifySessionValues();
    }

    private void NotifySessionValues()
    {
        OnPropertyChanged(nameof(SessionClient));
        OnPropertyChanged(nameof(SessionArch));
        // Переключатели привязаны к производным признакам, а не к самим строкам.
        OnPropertyChanged(nameof(IsSessionClientAuto));
        OnPropertyChanged(nameof(IsSessionClientOrdinary));
        OnPropertyChanged(nameof(IsSessionClientThick));
        OnPropertyChanged(nameof(IsSessionClientThin));
        OnPropertyChanged(nameof(IsSessionArchAuto));
        OnPropertyChanged(nameof(IsSessionArch32));
        OnPropertyChanged(nameof(IsSessionArch64));
    }

    private void UpdateStatus(string? message = null)
    {
        if (message is not null)
            StatusBarInfo = message;
        else if (SelectedInfobase is not null)
            StatusBarInfo = ComposeStatusInfo(SelectedInfobase);
        else if (SelectedGroupNode is not null)
            StatusBarInfo = string.Format(LocalizationManager.T("Main.StatusGroup"), SelectedGroupNode.FullPath);
        else
            StatusBarInfo = LocalizationManager.T("Main.Ready");
    }

    /// <summary>
    /// Собирает строку состояния по выбранной базе из включённых частей.
    /// Состав, порядок и разделитель те же, что в версии для Windows.
    /// Отличие от неё ровно одно и сделано намеренно: когда не включена
    /// ни одна часть, Windows отдаёт пустую строку, а здесь остаётся имя
    /// базы, потому что пустая панель выглядела бы поломкой.
    /// </summary>
    private string ComposeStatusInfo(Infobase ib)
    {
        var parts = new List<string>();
        if (StatusShowConnectionType)
            parts.Add(ib.ConnectionTypeDisplay);
        if (StatusShowConnectionPath)
        {
            var path = ib.Connection.Type == ConnectionType.File
                ? (string.IsNullOrWhiteSpace(ib.Connection.FilePath) ? "—" : ib.Connection.FilePath)
                : ib.ServerDatabaseDisplay;
            if (!string.IsNullOrWhiteSpace(path))
                parts.Add(path);
        }
        if (StatusShowPort && ib.Connection.Type == ConnectionType.ClientServer && ib.Connection.Port > 0)
            parts.Add($"{LocalizationManager.T("Main.StatusPort")} {ib.Connection.Port}");
        if (StatusShowPlatformVersion && !string.IsNullOrWhiteSpace(ib.PlatformVersion))
            parts.Add($"{LocalizationManager.T("Main.StatusPlatform")} {ib.PlatformVersion}");
        if (StatusShowArchitecture)
            parts.Add(ib.ArchitectureDisplay);
        if (StatusShowLaunchMode)
            parts.Add(ib.ParsedLaunchMode);
        if (StatusShowClientType && !string.IsNullOrWhiteSpace(ib.ClientType))
            parts.Add(ib.ClientTypeDisplay);
        if (StatusShowUser && !string.IsNullOrWhiteSpace(ib.Connection.User))
            parts.Add($"{LocalizationManager.T("Main.StatusUser")} {ib.Connection.User}");
        if (StatusShowId && !string.IsNullOrWhiteSpace(ib.Id))
            parts.Add($"ID {ib.Id}");

        return parts.Count > 0 ? string.Join("  ·  ", parts) : ib.Name;
    }
}
#endif