#if LINUX
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows.Input;
using Avalonia.Threading;
using Configuration_Management.Localization;
using Configuration_Management.Models;
using Configuration_Management.Services;
using Configuration_Management.Themes;

namespace Configuration_Management.ViewModels;

/// <summary>
/// Главная ViewModel для Avalonia (Linux). Упрощённая, но функциональная версия
/// WPF-<c>MainViewModel</c>: загрузка и сохранение списка баз, группы, поиск и теги,
/// избранное, запуск 1С, переключение темы, синхронизация с ibases.v8i.
/// Коллекции на <see cref="ObservableCollection{T}"/> (без ICollectionView) с ручной
/// фильтрацией/сортировкой.
/// </summary>
public class MainViewModel : ViewModelBase
{
    private readonly IInfobaseRepository _repository;
    private readonly IAppLogger _logger;
    private readonly IDialogService _dialog;
    private readonly IOneCLauncher _launcher;
    private readonly IIbasesSyncService _sync;
    private readonly IPlatformVersionService _platformService;

    private List<Infobase> _allInfobases = new();
    private List<Group> _groups = new();
    private bool _groupSortAscending = true;
    private string _sortField = "Name";
    private Avalonia.Threading.DispatcherTimer? _syncTimer;
    private DateTime? _nextScheduleRun;
    private bool _sortAscending = true;
    private readonly HashSet<string> _collapsedGroups = new(StringComparer.OrdinalIgnoreCase);
    private bool _deferCollapsedSave;

    private AppSettings _settings = new();

    // ---- Поиск / теги ----
    private string _searchText = string.Empty;
    private bool _showTagFilterPanel = true;

    // ---- Вид списка ----
    private string _listMode = "All"; // All / Favorites / Recent
    private bool _groupByGroup = true;
    private bool _showEmptyGroups;

    // ---- Правая панель ----
    private Infobase? _selectedInfobase;
    private GroupNodeViewModel? _selectedGroupNode;
    private bool _showRightPanelDetails = true;

    // ---- Строка состояния ----
    private string _statusBarInfo = LocalizationManager.T("Main.Ready");
    private string _syncMessage = string.Empty;
    private System.Threading.CancellationTokenSource? _statusMessageCts;

    // ---- Тема ----
    private string _themeName = ThemeManager.LightThemeName;

    // ---- Компактный режим интерфейса ----
    private bool _compactMode;
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

    // ---- Действие после запуска базы или конфигуратора ----
    private string _afterLaunchAction = "None";

    /// <summary>
    /// Что делать с окном после успешного запуска: "None", "MinimizeToTray" или "Close".
    /// Хранится строкой, как в WPF-версии и в файле настроек.
    /// </summary>
    public string AfterLaunchAction
    {
        get => _afterLaunchAction;
        set
        {
            if (SetProperty(ref _afterLaunchAction, value))
            {
                _settings.AfterLaunchAction = value;
                SaveSettingsSilently();
            }
        }
    }

    /// <summary>
    /// Настраиваемый шаблон имени COM-коннектора 1С (issue #175).
    /// Пустая строка — стандартные ProgID V85/V83/V82/V81.COMConnector; иначе шаблон
    /// разворачивается по версии платформы каждой базы (плейсхолдеры %V12%/%V3%/%V4%)
    /// и пробуется первым в переборе. Применяется сразу: новое значение передаётся коннектору
    /// (issue #175). На Linux COM отсутствует, но значение сохраняется в общий файл настроек,
    /// чтобы не теряться при переходе между платформами.
    /// </summary>
    public string ComConnectorNameTemplate
    {
        get => _settings.ComConnectorNameTemplate ?? "";
        set
        {
            var normalized = value?.Trim() ?? string.Empty;
            if (string.Equals(_settings.ComConnectorNameTemplate, normalized, StringComparison.Ordinal))
                return;
            _settings.ComConnectorNameTemplate = normalized;
            // Симметрично Windows-сборке (issue #175). На Linux вызов — no-op: COM
            // отсутствует, кэшировать нечего, но ветки держим одинаковыми.
            OneCComConnector.ApplyTemplate(normalized);
            SaveSettingsSilently();
        }
    }

    /// <summary>
    /// Таймаут определения свойств конфигурации через COM-коннектор (issue #174), мс.
    /// На Linux COM отсутствует, но значение сохраняется в общий файл настроек,
    /// чтобы не теряться при переходе между платформами. Минимум 1000 мс.
    /// </summary>
    public int ComDetectTimeoutMs
    {
        get => Math.Max(1000, _settings.ComDetectTimeoutMs);
        set
        {
            var v = Math.Max(1000, value);
            if (_settings.ComDetectTimeoutMs == v)
                return;
            _settings.ComDetectTimeoutMs = v;
            SaveSettingsSilently();
        }
    }

    /// <summary>
    /// Разрешено ли несколько экземпляров: от этого зависит, вернётся ли
    /// спрятанное окно повторным запуском приложения.
    /// </summary>
    public bool AllowMultipleInstances => _settings.AllowMultipleInstances;

    /// <summary>
    /// Запоминать ли размер, положение и состояние главного окна между запусками.
    /// </summary>
    public bool RememberWindowLayout => _settings.RememberWindowLayout;

    /// <summary>Проверять наличие обновлений приложения при запуске (GitHub Releases).</summary>
    public bool CheckForUpdatesOnStartup => _settings.CheckForUpdatesOnStartup;

    /// <summary>Автоматически устанавливать новые версии без подтверждения.</summary>
    public bool AutoUpdateEnabled => _settings.AutoUpdateEnabled;

    /// <summary>Сохранённая ширина главного окна; ноль означает «не сохранялась».</summary>
    public double SavedWindowWidth => _settings.WindowWidth;

    /// <summary>Сохранённая высота главного окна; ноль означает «не сохранялась».</summary>
    public double SavedWindowHeight => _settings.WindowHeight;

    /// <summary>Сохранённая позиция главного окна по горизонтали.</summary>
    public double SavedWindowLeft => _settings.WindowLeft;

    /// <summary>Сохранённая позиция главного окна по вертикали.</summary>
    public double SavedWindowTop => _settings.WindowTop;

    /// <summary>Сохранённое состояние главного окна (Normal, Maximized).</summary>
    public string SavedWindowState => _settings.WindowState;

    /// <summary>Сохраняет размер, положение и состояние главного окна.</summary>
    public void SaveWindowLayout(double width, double height, double left, double top, string state)
    {
        _settings.WindowWidth = width;
        _settings.WindowHeight = height;
        _settings.WindowLeft = left;
        _settings.WindowTop = top;
        _settings.WindowState = state ?? string.Empty;
        SaveSettingsSilently();
    }

    /// <summary>Показывать ли значок в области уведомлений.</summary>
    public bool ShowTrayIcon => _settings.ShowTrayIcon;

    /// <summary>Уводить ли окно в трей вместо выхода при закрытии.</summary>
    public bool CloseToTray => _settings.CloseToTray;

    /// <summary>
    /// Показывать стандартный системный заголовок окна вместо собственного
    /// безрамкового (issue #152). Изменение вступает в силу после перезапуска,
    /// так как декор окна задаётся при построении главного окна.
    /// </summary>
    public bool UseSystemTitleBar
    {
        get => _settings.UseSystemTitleBar;
        set
        {
            if (_settings.UseSystemTitleBar == value)
                return;
            _settings.UseSystemTitleBar = value;
            SaveSettingsSilently();
            OnPropertyChanged();
        }
    }

    /// <summary>Уводить ли окно в трей по клавише Esc.</summary>
    public bool EscapeToTray => _settings.EscapeToTray;

    /// <summary>Настройки трея изменились: окну нужно обновить значок.</summary>
    public event Action? TraySettingsChanged;

    /// <summary>
    /// Применяет настройки поведения приложения. Обе лежали в общем с версией
    /// для Windows файле настроек, но в Linux-сборке их нечем было изменить.
    /// </summary>
    public void ApplyBehaviorSettings(
        bool allowMultipleInstances,
        bool rememberWindowLayout,
        bool checkForUpdatesOnStartup,
        bool autoUpdateEnabled)
    {
        _settings.AllowMultipleInstances = allowMultipleInstances;
        _settings.RememberWindowLayout = rememberWindowLayout;
        _settings.CheckForUpdatesOnStartup = checkForUpdatesOnStartup;
        _settings.AutoUpdateEnabled = autoUpdateEnabled;
        if (!SaveSettingsSafe())
            _dialog.ShowError(LocalizationManager.T("Main.SaveFailedHint"),
                LocalizationManager.T("Settings.Title"));
        OnPropertyChanged(nameof(AllowMultipleInstances));
        OnPropertyChanged(nameof(RememberWindowLayout));
        OnPropertyChanged(nameof(CheckForUpdatesOnStartup));
        OnPropertyChanged(nameof(AutoUpdateEnabled));
    }

    /// <summary>Применяет настройки поведения трея из окна настроек.</summary>
    public void ApplyTraySettings(bool showTrayIcon, bool closeToTray, bool escapeToTray)
    {
        _settings.ShowTrayIcon = showTrayIcon;
        _settings.CloseToTray = closeToTray;
        _settings.EscapeToTray = escapeToTray;
        if (!SaveSettingsSafe())
            _dialog.ShowError(LocalizationManager.T("Main.SaveFailedHint"),
                LocalizationManager.T("Settings.Title"));
        // Значок показывается и прячется сразу, как в версии для Windows,
        // иначе настройка действовала бы только после перезапуска.
        TraySettingsChanged?.Invoke();
    }

    /// <summary>Запрос к главному окну выполнить действие после успешного запуска.</summary>
    public event Action<Models.AfterLaunchAction>? AfterLaunchRequested;

    /// <summary>Оповещает главное окно, если настройка требует действия.</summary>
    public void NotifyAfterLaunch()
    {
        var action = Models.AfterLaunchActionHelper.Parse(_afterLaunchAction);
        if (action != Models.AfterLaunchAction.None)
            AfterLaunchRequested?.Invoke(action);
    }

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

    private string _sessionClient = "Авто";
    private string _sessionArch = "Авто";

    private bool _isExporting;
    private string _exportIndicatorTooltip = string.Empty;

    private readonly LaunchViewModel _launchVm;

    /// <summary>Создаёт главную ViewModel и подключает сервисы.</summary>
    public MainViewModel(
        IInfobaseRepository repository,
        IAppLogger logger,
        IDialogService dialog,
        IOneCLauncher launcher,
        IIbasesSyncService sync,
        IPlatformVersionService platformService)
    {
        _repository = repository;
        _logger = logger;
        _dialog = dialog;
        _launcher = launcher;
        _sync = sync;
        _platformService = platformService;

        GroupNodes = new ObservableCollection<GroupNodeViewModel>();
        AllGroupNodes = new ObservableCollection<GroupNodeViewModel>();
        FlatItems = new ObservableCollection<object>();
        TagFilterItems = new ObservableCollection<TagFilterItem>();

        // Блок «Текущая сессия» действует на очередной запуск Предприятия.
        _launchVm = new LaunchViewModel(
            () => SelectedInfobase,
            launcher,
            logger,
            OnLaunched)
        {
            EnterpriseOverrides = ResolveSessionOverrides
        };

        InitializeCommands();

        // При изменении реестра учётных записей (создание/переименование/удаление в окне
        // настроек) обновляем видимость кнопки «Смена пользователя» (issue #200).
        try
        {
            AppServices.GetRequiredService<IProfileService>().ProfilesChanged += (_, _) =>
                OnPropertyChanged(nameof(SwitchUserVisible));
        }
        catch { /* сервис профилей может отсутствовать в изолированном контексте */ }
    }

    // ======================= Коллекции =======================

    /// <summary>Корневые узлы дерева групп для отображения.</summary>
    public ObservableCollection<GroupNodeViewModel> GroupNodes { get; }

    /// <summary>Полный список корневых узлов (до фильтрации по виду/поиску).</summary>
    public ObservableCollection<GroupNodeViewModel> AllGroupNodes { get; }

    /// <summary>Плоский список элементов (для режима «Избранное»/«Недавние»/поиска).</summary>
    public ObservableCollection<object> FlatItems { get; }

    /// <summary>Чипы тегов на панели быстрого отбора.</summary>
    public ObservableCollection<TagFilterItem> TagFilterItems { get; }

    // ======================= Команды =======================

    public ICommand ClearSearchCommand { get; private set; } = null!;
    public ICommand SearchByTagCommand { get; private set; } = null!;
    public ICommand AddTagInlineCommand { get; private set; } = null!;
    public ICommand RemoveTagCommand { get; private set; } = null!;
    public ICommand ClearTagFiltersCommand { get; private set; } = null!;
    public ICommand ShowAllCommand { get; private set; } = null!;
    public ICommand ShowFavoritesCommand { get; private set; } = null!;
    public ICommand ShowRecentCommand { get; private set; } = null!;
    public ICommand LaunchEnterpriseCommand { get; private set; } = null!;
    public ICommand LaunchConfiguratorCommand { get; private set; } = null!;

    /// <summary>Запуск Предприятия с разовыми параметрами командной строки.</summary>
    public ICommand LaunchEnterpriseWithParamsCommand { get; private set; } = null!;

    /// <summary>Запуск Предприятия с запросом имени и пароля вместо сохранённых.</summary>
    public ICommand LaunchEnterpriseWithAuthCommand { get; private set; } = null!;

    /// <summary>Запуск Конфигуратора с разовыми параметрами командной строки.</summary>
    public ICommand LaunchConfiguratorWithParamsCommand { get; private set; } = null!;
    public ICommand EditInfobaseCommand { get; private set; } = null!;
    public ICommand AddInfobaseCommand { get; private set; } = null!;
    public ICommand DeleteInfobaseCommand { get; private set; } = null!;
    public ICommand EditGroupCommand { get; private set; } = null!;
    public ICommand DeleteGroupCommand { get; private set; } = null!;
    public ICommand OpenInfobaseByLinkCommand { get; private set; } = null!;
    public ICommand ToggleFavoriteCommand { get; private set; } = null!;
    public ICommand TogglePinCommand { get; private set; } = null!;
    public ICommand ToggleFavoriteForCommand { get; private set; } = null!;
    public ICommand TogglePinForCommand { get; private set; } = null!;
    public ICommand OpenSettingsCommand { get; private set; } = null!;
    public ICommand ExpandAllGroupsCommand { get; private set; } = null!;
    public ICommand CollapseAllGroupsCommand { get; private set; } = null!;
    public ICommand SortGroupsAscendingCommand { get; private set; } = null!;
    public ICommand SortGroupsDescendingCommand { get; private set; } = null!;
    public ICommand SynchronizeWithIbasesCommand { get; private set; } = null!;
    public ICommand ToggleThemeCommand { get; private set; } = null!;
    public ICommand ToggleRightPanelDetailsCommand { get; private set; } = null!;
    public ICommand ToggleSessionLaunchPanelCommand { get; private set; } = null!;
    public ICommand ExitCommand { get; private set; } = null!;
    public ICommand CopyConnectionStringCommand { get; private set; } = null!;
    public ICommand CheckAvailabilityCommand { get; private set; } = null!;
    public ICommand OpenInfobaseFolderCommand { get; private set; } = null!;
    public ICommand CreateDesktopShortcutCommand { get; private set; } = null!;
    public ICommand OpenNativeStarterCommand { get; private set; } = null!;
    public ICommand QuickClearCacheCommand { get; private set; } = null!;
    public ICommand ClearCacheCommand { get; private set; } = null!;
    public ICommand ClearProgramCacheCommand { get; private set; } = null!;
    public ICommand DumpInfobaseDtCommand { get; private set; } = null!;
    public ICommand DumpConfigurationCfCommand { get; private set; } = null!;
    public ICommand RefreshConfigurationInfoCommand { get; private set; } = null!;
    public ICommand ClearUserCacheCommand { get; private set; } = null!;
    public ICommand ClearCacheBothCommand { get; private set; } = null!;
    public ICommand SwitchUserCommand { get; private set; } = null!;

    private void InitializeCommands()
    {
        ClearSearchCommand = new RelayCommand(() => SearchText = string.Empty);
        SearchByTagCommand = new RelayCommand(SearchByTag);
        AddTagInlineCommand = new RelayCommand(AddTagInline);
        RemoveTagCommand = new RelayCommand(RemoveTag);
        ClearTagFiltersCommand = new RelayCommand(ClearTagFilters);
        // Режимы списка вынесены в команды, чтобы их можно было повесить
        // на горячую клавишу: привязка принимает команду, а не свойство.
        ShowAllCommand = new RelayCommand(() => IsListModeAll = true);
        ShowFavoritesCommand = new RelayCommand(() => IsListModeFavorites = true);
        ShowRecentCommand = new RelayCommand(() => IsListModeRecent = true);
        LaunchEnterpriseCommand = new RelayCommand(_ => Launch(_launchVm.LaunchCommand, LaunchKind.Enterprise), _ => SelectedInfobase is not null);
        LaunchConfiguratorCommand = new RelayCommand(_ => Launch(_launchVm.LaunchCommand, LaunchKind.Configurator), _ => SelectedInfobase is not null);
        LaunchEnterpriseWithParamsCommand = new RelayCommand(_ => LaunchWithParams(LaunchKind.Enterprise), _ => SelectedInfobase is not null);
        LaunchEnterpriseWithAuthCommand = new RelayCommand(_ => LaunchWithAuth(), _ => SelectedInfobase is not null);
        LaunchConfiguratorWithParamsCommand = new RelayCommand(_ => LaunchWithParams(LaunchKind.Configurator), _ => SelectedInfobase is not null);
        EditInfobaseCommand = new RelayCommand(_ => EditInfobase(), _ => SelectedInfobase is not null);
        AddInfobaseCommand = new RelayCommand(AddInfobase);
        DeleteInfobaseCommand = new RelayCommand(_ => DeleteInfobase(),
            _ => SelectedInfobase is not null || SelectedGroupNode?.Group is not null);
        // Команды группы: параметр — узел группы или сама группа из строки дерева.
        EditGroupCommand = new RelayCommand(p =>
        {
            var group = ResolveGroup(p);
            if (group is not null)
                EditGroup(group);
        });
        DeleteGroupCommand = new RelayCommand(p =>
        {
            var group = ResolveGroup(p);
            if (group is not null)
                DeleteGroup(group);
        }, p => ResolveGroup(p) != null);
        OpenInfobaseByLinkCommand = new RelayCommand(OpenInfobaseByLink);
        ToggleFavoriteCommand = new RelayCommand(_ => ToggleFavorite(), _ => SelectedInfobase is not null);
        TogglePinCommand = new RelayCommand(_ => TogglePin(), _ => SelectedInfobase is not null);
        ToggleFavoriteForCommand = new RelayCommand(p => ToggleFavoriteFor(p as Infobase), p => p is Infobase);
        TogglePinForCommand = new RelayCommand(p => TogglePinFor(p as Infobase), p => p is Infobase);
        OpenSettingsCommand = new RelayCommand(OpenSettings);
        ExpandAllGroupsCommand = new RelayCommand(ExpandAllGroups);
        CollapseAllGroupsCommand = new RelayCommand(CollapseAllGroups);
        SortGroupsAscendingCommand = new RelayCommand(() => SortGroups(true));
        SortGroupsDescendingCommand = new RelayCommand(() => SortGroups(false));
        SynchronizeWithIbasesCommand = new RelayCommand(SynchronizeWithIbases);
        ToggleThemeCommand = new RelayCommand(ToggleTheme);
        ToggleRightPanelDetailsCommand = new RelayCommand(() => ShowRightPanelDetails = !ShowRightPanelDetails);
        ToggleSessionLaunchPanelCommand = new RelayCommand(() => ShowSessionLaunchPanel = !ShowSessionLaunchPanel);
        ExitCommand = new RelayCommand(ExitApplication);
        CopyConnectionStringCommand = new RelayCommand(_ => CopyConnectionString(), _ => SelectedInfobase is not null);
        CheckAvailabilityCommand = new RelayCommand(CheckAvailability);
        OpenInfobaseFolderCommand = new RelayCommand(_ => OpenInfobaseFolder(),
            _ => SelectedInfobase?.Connection.Type == ConnectionType.File);
        CreateDesktopShortcutCommand = new RelayCommand(_ => CreateDesktopShortcut(), _ => SelectedInfobase is not null);
        OpenNativeStarterCommand = new RelayCommand(OpenNativeStarter);
        QuickClearCacheCommand = new RelayCommand(QuickClearCache, _ => SelectedInfobase is not null);
        // Кнопка «Очистить кеш» верхней панели действует на выбранную базу: если база не
        // выделена (например, под курсором папка) — окно открывается без предзаполненных
        // галок, и пользователь сам отмечает нужные базы (issue #196). В колонке
        // «Действия» строка передаёт свою базу параметром, поэтому там кнопка включена
        // независимо от глобального выбора и стартовая галка ставится на базу строки.
        ClearCacheCommand = new RelayCommand(p => OpenCacheClean(OneCCacheKind.All, p as Infobase),
            p => p is Infobase ? true : Infobases.Count > 0);
        ClearProgramCacheCommand = new RelayCommand(_ => OpenCacheClean(OneCCacheKind.Program));
        DumpInfobaseDtCommand = new RelayCommand(DumpInfobaseDt);
        DumpConfigurationCfCommand = new RelayCommand(DumpConfigurationCf);
        RefreshConfigurationInfoCommand = new RelayCommand(RefreshConfigurationInfo);
        ClearUserCacheCommand = new RelayCommand(_ => OpenCacheClean(OneCCacheKind.User));
        ClearCacheBothCommand = new RelayCommand(_ => OpenCacheClean(OneCCacheKind.All));
        // Смена пользователя (issue #200): диалог входа без перезапуска приложения.
        SwitchUserCommand = new RelayCommand(SwitchUser);
    }

    private void Launch(ICommand launchVmCommand, LaunchKind kind) => launchVmCommand.Execute(kind);

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

    public Infobase? SelectedInfobase
    {
        get => _selectedInfobase;
        set
        {
            if (SetPropertyWithRelated(ref _selectedInfobase, value, nameof(SelectedInfobase), nameof(RightPanelTitle), nameof(RightPanelSubtitle),
                    nameof(IsInfobaseSelected), nameof(ShowConnectionInfo), nameof(ShowRightPanelHint),
                    nameof(RightPanelIconKey), nameof(HasRightPanelIcon)))
            {
                if (value is not null)
                    SelectedGroupNode = null;
                RaiseCommandCanExecuteChanged();
                UpdateStatus();
            }
        }
    }

    public GroupNodeViewModel? SelectedGroupNode
    {
        get => _selectedGroupNode;
        set
        {
            if (SetPropertyWithRelated(ref _selectedGroupNode, value, nameof(SelectedGroupNode), nameof(RightPanelTitle), nameof(RightPanelSubtitle),
                    nameof(IsInfobaseSelected), nameof(ShowConnectionInfo), nameof(ShowRightPanelHint),
                    nameof(RightPanelIconKey), nameof(HasRightPanelIcon)))
            {
                if (value is not null)
                    SelectedInfobase = null;
                UpdateStatus();
                // Удаление доступно и при выбранной группе, а без этого
                // события кнопка и клавиша остались бы неактивными.
                RaiseCommandCanExecuteChanged();
            }
        }
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
    private bool _isLoading;

    /// <summary>Что именно делается: подпись внутри индикатора.</summary>
    public string LoadingMessage
    {
        get => _loadingMessage;
        set => SetProperty(ref _loadingMessage, value);
    }
    private string _loadingMessage = string.Empty;

    public string SyncMessage
    {
        get => _syncMessage;
        set => SetProperty(ref _syncMessage, value);
    }

    public string ThemeName
    {
        get => _themeName;
        set => SetProperty(ref _themeName, value);
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
    public string HotkeySwitchUser => _settings.HotkeySwitchUser;

    /// <summary>
    /// Сохраняет назначенные сочетания и сообщает окну, что их надо
    /// перерегистрировать: подписи в меню и сами привязки берутся отсюда.
    /// </summary>
    public void ApplyHotkeys(string enterprise, string configurator, string edit, string add,
        string favorite, string pin, string delete, string clearCache,
        string showAll, string showFavorites, string showRecent,
        string clearSearch, string clearTags, string rightPanelDetails, string switchUser)
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
        _settings.HotkeySwitchUser = switchUser ?? string.Empty;

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
        OnPropertyChanged(nameof(HotkeySwitchUser));
        HotkeysChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Сочетания переназначены: окну надо перерегистрировать привязки и меню.</summary>
    public event EventHandler? HotkeysChanged;

    /// <summary>
    /// true, если учётных записей больше одной — тогда кнопка «Смена пользователя»
    /// показывается на верхней панели (при одной записи переключать нечего).
    /// </summary>
    public bool SwitchUserVisible
    {
        get
        {
            try
            {
                return AppServices.GetRequiredService<IProfileService>().Profiles.Count > 1;
            }
            catch
            {
                // Сервис профилей в изолированном/тестовом контексте может отсутствовать.
                return false;
            }
        }
    }

    /// <summary>
    /// Открывает окно выбора учётной записи и, если пользователь вошёл в другую
    /// запись, переключает активный профиль и перезагружает данные главного окна.
    /// Отмена или выбор той же записи ничего не меняют.
    /// </summary>
    private void SwitchUser()
    {
        try
        {
            var profileService = AppServices.GetRequiredService<IProfileService>();
            if (profileService.Profiles.Count <= 1)
                return;

            var current = profileService.CurrentProfile;
            var selectedId = LoginWindow.ShowLogin(profileService);
            if (selectedId == null)
                return; // Вход отменён — остаёмся как есть.

            if (current != null &&
                string.Equals(current.Id, selectedId, StringComparison.OrdinalIgnoreCase))
                return; // Та же запись — перезагрузка не нужна.

            profileService.SetCurrentProfile(selectedId);
            ReloadAfterProfileSwitch();
        }
        catch (Exception ex)
        {
            _logger.Error("Ошибка смены пользователя: " + ex.Message);
            _dialog.ShowError(string.Format(LocalizationManager.T("Auth.LoginError"), ex.Message));
        }
    }

    /// <summary>
    /// Перезагружает данные главного окна после смены активного профиля в работающем
    /// приложении: повторно выполняет <see cref="Initialize"/> (список баз, группы,
    /// тема, избранное, горячие клавиши), обновляет язык интерфейса профиля и
    /// сообщает окну о переназначении сочетаний.
    /// </summary>
    private void ReloadAfterProfileSwitch()
    {
        try
        {
            Initialize();

            // Локализация выбранного профиля.
            try
            {
                var s = _repository.LoadSettings();
                LocalizationManager.Instance.Initialize(s.Language);
            }
            catch
            {
                // Локализация не должна ломать смену пользователя.
            }

            OnPropertyChanged(nameof(SwitchUserVisible));
            HotkeysChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.Error("Ошибка перезагрузки данных после смены пользователя", ex);
        }
    }

    // ---- Видимость колонок ----

    /// <summary>
    /// Порядок колонок списка баз по умолчанию (колонки «Конфигурация» и «№
    /// релиза» в самом конце). Используется, пока пользователь не задал
    /// собственный порядок.
    /// </summary>
    private static readonly string[] DefaultColumnOrder =
        { "Version", "LaunchMode", "Actions", "ServerBase", "LastLaunch", "Size", "Configuration", "ConfigurationVersion" };

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

            // «Действия» и «№ релиза» обязаны присутствовать в списке: старые
            // сохранённые настройки могли не содержать их вовсе, и тогда эти
            // колонки нельзя было ни показать, ни отключить в окне настроек.
            var needsActions = !order!.Contains("Actions", StringComparer.Ordinal);
            var needsConfigurationVersion = !order.Contains("ConfigurationVersion", StringComparer.Ordinal);
            if (!needsActions && !needsConfigurationVersion)
                return order;

            var result = new List<string>(order.Count + 2);
            foreach (var key in order)
            {
                if (needsConfigurationVersion && key == "Configuration")
                    result.Add("ConfigurationVersion");
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
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
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

    // ---- Текущая сессия ----
    public string SessionClient
    {
        get => _sessionClient;
        set
        {
            if (!SetPropertyWithRelated(ref _sessionClient, value, nameof(SessionClient),
                    nameof(IsSessionClientAuto), nameof(IsSessionClientOrdinary), nameof(IsSessionClientThick),
                    nameof(IsSessionClientThin)))
                return;

            _settings.SessionClientMode = SessionClientMode().ToString();
            SaveSettingsSilently();
        }
    }
    public bool IsSessionClientAuto { get => SessionClient == "Авто"; set { if (value) SessionClient = "Авто"; } }
    public bool IsSessionClientOrdinary { get => SessionClient == "Обычный"; set { if (value) SessionClient = "Обычный"; } }
    public bool IsSessionClientThick { get => SessionClient == "Толстый"; set { if (value) SessionClient = "Толстый"; } }
    public bool IsSessionClientThin { get => SessionClient == "Тонкий"; set { if (value) SessionClient = "Тонкий"; } }

    public string SessionArch
    {
        get => _sessionArch;
        set
        {
            if (!SetPropertyWithRelated(ref _sessionArch, value, nameof(SessionArch), nameof(IsSessionArchAuto), nameof(IsSessionArch32), nameof(IsSessionArch64)))
                return;

            // «Текущая сессия» учитывается лаунчером первым шагом приоритета (issue #146).
            OneCLauncher.SessionArchitecture = SessionArchitectureMode();
            _settings.SessionArchitecture = SessionArchitectureMode().ToString();
            SaveSettingsSilently();
        }
    }

    /// <summary>Режим клиента текущей сессии в терминах модели.</summary>
    private SessionClientMode SessionClientMode() => _sessionClient switch
    {
        "Обычный" => Models.SessionClientMode.Ordinary,
        "Толстый" => Models.SessionClientMode.Thick,
        "Тонкий" => Models.SessionClientMode.Thin,
        _ => Models.SessionClientMode.Auto
    };

    /// <summary>Разрядность текущей сессии в терминах модели.</summary>
    private SessionArchitectureMode SessionArchitectureMode() => _sessionArch switch
    {
        "32" => Models.SessionArchitectureMode.X86,
        "64" => Models.SessionArchitectureMode.X64,
        _ => Models.SessionArchitectureMode.Auto
    };

    private static string SessionClientFromSetting(string? saved) =>
        Enum.TryParse<SessionClientMode>(saved, true, out var parsed)
            ? parsed switch
            {
                Models.SessionClientMode.Ordinary => "Обычный",
                Models.SessionClientMode.Thick => "Толстый",
                Models.SessionClientMode.Thin => "Тонкий",
                _ => "Авто"
            }
            : "Авто";

    private static string SessionArchFromSetting(string? saved) =>
        Enum.TryParse<SessionArchitectureMode>(saved, true, out var parsed)
            ? parsed switch
            {
                Models.SessionArchitectureMode.X86 => "32",
                Models.SessionArchitectureMode.X64 => "64",
                _ => "Авто"
            }
            : "Авто";

    /// <summary>
    /// Переопределения очередного запуска Предприятия по блоку «Текущая сессия».
    /// Возвращает null, когда оба переключателя в «Авто»: тогда запуск идёт
    /// по настройкам самой базы, как в WPF-версии.
    /// </summary>
    private LaunchOverrides? ResolveSessionOverrides(Infobase infobase)
    {
        var client = SessionClientMode();
        var arch = SessionArchitectureMode();
        if (client == Models.SessionClientMode.Auto && arch == Models.SessionArchitectureMode.Auto)
            return null;

        OneCClientType? clientType = client switch
        {
            Models.SessionClientMode.Thin => OneCClientType.Thin,
            Models.SessionClientMode.Thick => OneCClientType.Thick,
            // «Обычный режим» и «Толстый (обычные формы)» объединены (issue #144).
            Models.SessionClientMode.Ordinary => OneCClientType.Thick,
            _ => ClientFromInfobase(infobase)
        };

        // Разрядность полностью определяет лаунчер по приоритету (issue #146):
        // 1) «Текущая сессия» (передана через OneCLauncher.SessionArchitecture),
        // 2) суффикс версии, 3) глобальная настройка, 4) настройка базы / priority.
        var architecture = OneCLauncher.ResolveArchitecture(infobase.Architecture, infobase.PlatformVersion);

        OneCRunMode? runMode = client switch
        {
            Models.SessionClientMode.Thick => OneCRunMode.Managed,
            // «Обычный режим» соответствует бывшему «Толстый (обычные формы)» (issue #144).
            Models.SessionClientMode.Ordinary => OneCRunMode.Ordinary,
            Models.SessionClientMode.Auto => OneCLauncher.GetRunModeFromLaunchMode(infobase.LaunchMode),
            _ => null
        };

        return new LaunchOverrides(clientType, runMode, architecture);
    }

    /// <summary>Тип клиента из настройки базы, как в WPF-версии.</summary>
    private static OneCClientType? ClientFromInfobase(Infobase infobase)
    {
        if (string.Equals(infobase.LaunchMode, "Автоматический", StringComparison.OrdinalIgnoreCase))
            return null;
        if (string.Equals(infobase.LaunchMode, "Толстый клиент (обычные формы)", StringComparison.OrdinalIgnoreCase))
            return OneCClientType.Thick;
        if (string.Equals(infobase.LaunchMode, "Толстый клиент", StringComparison.OrdinalIgnoreCase))
            return OneCClientType.Thick;
        if (string.Equals(infobase.LaunchMode, "Тонкий клиент", StringComparison.OrdinalIgnoreCase))
            return OneCClientType.Thin;
        return null;
    }
    public bool IsSessionArchAuto { get => SessionArch == "Авто"; set { if (value) SessionArch = "Авто"; } }
    public bool IsSessionArch32 { get => SessionArch == "32"; set { if (value) SessionArch = "32"; } }
    public bool IsSessionArch64 { get => SessionArch == "64"; set { if (value) SessionArch = "64"; } }

    // ======================= Загрузка данных =======================

    /// <summary>Загружает настройки, список баз и групп, строит дерево.</summary>
    public void Initialize()
    {
        try
        {
            _settings = _repository.LoadSettings();
            _allInfobases = _repository.Load();
            _groups = _repository.LoadGroups();

            _collapsedGroups.Clear();
            foreach (var key in _settings.CollapsedGroups)
                _collapsedGroups.Add(key);

            _groupByGroup = _settings.GroupByGroup;
            _showEmptyGroups = _settings.ShowEmptyGroups;
            _showTagFilterPanel = _settings.ShowTagFilterPanel;
            // Компактный режим правой панели восстанавливается из настроек (issue #149).
            _showRightPanelDetails = _settings.ShowRightPanelDetails;
            // Уведомляем производные признаки, чтобы уже построенное главное окно
            // (панель могла быть собрана с дефолтным значением до загрузки настроек)
            // сразу применило компактный режим и скрыло лишние блоки информации.
            OnPropertyChanged(nameof(ShowRightPanelDetails));
            OnPropertyChanged(nameof(ShowRightPanelHint));
            OnPropertyChanged(nameof(ShowConnectionInfo));
            OnPropertyChanged(nameof(OpenByLinkCaption));
            OnPropertyChanged(nameof(RightPanelToggleTooltip));
            _themeName = _settings.Theme;
            _compactMode = _settings.CompactMode;
            _afterLaunchAction = _settings.AfterLaunchAction ?? "None";
            _sortField = string.IsNullOrWhiteSpace(_settings.SortField) ? "Name" : _settings.SortField;
            _sortAscending = _settings.SortAscending;
            // Вид списка хранится тем же признаком, что и в WPF: «только избранные».
            _listMode = _settings.ShowFavoritesOnly ? "Favorites" : "All";
            _sessionClient = SessionClientFromSetting(_settings.SessionClientMode);
            _sessionArch = SessionArchFromSetting(_settings.SessionArchitecture);
            // «Текущая сессия» учитывается лаунчером первым шагом приоритета
            // выбора разрядности (issue #146), даже для запусков напрямую.
            OneCLauncher.SessionArchitecture = SessionArchitectureMode();
            ApplyDefaultArchitecture();
            ApplyAdditionalSearchPaths();
            ApplyTemplateCatalogPaths();

            OnPropertyChanged(nameof(GroupByGroup));
            OnPropertyChanged(nameof(ShowEmptyGroups));
            OnPropertyChanged(nameof(ShowTagFilterPanel));
            // Сегменты «Все / Избранное / Недавние» привязаны к производным
            // признакам, а вид списка восстановлен полем, поэтому уведомляем.
            OnPropertyChanged(nameof(IsListModeAll));
            OnPropertyChanged(nameof(IsListModeFavorites));
            OnPropertyChanged(nameof(IsListModeRecent));
            NotifyColumnSettings();
            NotifySessionSettings();

            RebuildTree();
            UpdateStatus(string.Format(LocalizationManager.T("Main.LoadedBases"), _allInfobases.Count));

            // Слоты Alt+1…9 читаются из общего с версией для Windows файла
            // настроек, затем раздаются избранным без слота.
            _favoriteHotkeyIds.Clear();
            if (_settings.FavoriteHotkeyIds is { } savedSlots)
                _favoriteHotkeyIds.AddRange(savedSlots.Where(k => !string.IsNullOrWhiteSpace(k)).Take(9));
            SyncFavoriteHotkeys();

            // При запуске синхронизируемся при любом триггере, как в WPF:
            // «при старте» это сразу и только, интервал и расписание это сразу
            // и дальше по таймеру.
            if (_settings.IbasesSyncMode != IbasesSyncMode.None)
                SynchronizeSilently();
            RestartAutoSync();

            // Миграция старой модели схем (активная + раздельные слоты) в единую схему
            // с двумя палитрами; устаревшие поля обнуляем, чтобы сохранялся новый формат.
            _settings.ActiveColorScheme = Models.ColorScheme.FromLegacy(
                _settings.ActiveColorScheme, _settings.LightColorScheme, _settings.DarkColorScheme);
            _settings.LightColorScheme = null;
            _settings.DarkColorScheme = null;

            if (string.IsNullOrWhiteSpace(_themeName))
                _themeName = ThemeManager.LightThemeName;
            _settings.Theme = _themeName;

            // Применяем схему и вариант темы: палитра выбирается по варианту.
            ThemeManager.ApplyScheme(_settings.ActiveColorScheme);
            ThemeManager.ApplyTheme(_themeName == ThemeManager.DarkThemeName);
        }
        catch (Exception ex)
        {
            _logger.Error("Ошибка загрузки данных главного окна", ex);
            _dialog.ShowError(string.Format(LocalizationManager.T("Main.ErrLoadBases"), ex.Message));
        }
    }

    /// <summary>Перестраивает дерево групп из моделей.</summary>
    public void RebuildTree()
    {
        // Узлы пересоздаются, поэтому прежний выбранный узел больше не тот,
        // что показан в дереве: правая панель иначе показывала бы старую группу.
        // Ключ запоминается, чтобы выбор вернулся на равнозначный новый узел.
        // Для настоящей группы главным остаётся её идентификатор: перенос
        // подветки меняет путь, а вместе с ним и ключ, но группа та же.
        var selectedKey = SelectedGroupNode?.NodeKey;
        var selectedGroupId = SelectedGroupNode?.Group?.Id;
        SelectedGroupNode = null;
        AllGroupNodes.Clear();
        GroupNodes.Clear();
        FlatItems.Clear();

        var roots = GroupNodeViewModel.BuildTree(_groups);
        DistributeInfobases(roots);

        foreach (var root in roots)
            AllGroupNodes.Add(root);

        // Определяем, какие корневые группы реально показывать (содержат базы).
        foreach (var root in AllGroupNodes)
        {
            if (_showEmptyGroups || root.ContainsInfobases)
                GroupNodes.Add(root);
        }

        RebuildTagFilters();
        ApplyFilter();

        // Пустой идентификатор группы штатно возможен (значение по умолчанию
        // в модели), и сравнение по нему совпало бы с первой попавшейся группой.
        var hasGroupId = !string.IsNullOrEmpty(selectedGroupId);
        if (hasGroupId || selectedKey is not null)
            SelectedGroupNode = FindNode(node =>
                hasGroupId
                && string.Equals(node.Group?.Id, selectedGroupId, StringComparison.OrdinalIgnoreCase))
                ?? (selectedKey is null ? null : FindNode(node =>
                    string.Equals(node.NodeKey, selectedKey, StringComparison.OrdinalIgnoreCase)));

        // Состав списка мог смениться импортом, удалением или очисткой,
        // а его показывает меню трея.
        OnPropertyChanged(nameof(RecentInfobases));
    }

    /// <summary>Состав списка вот-вот сменится: окну нужно запомнить прокрутку.</summary>
    public event Action? TreeRebuilding;

    /// <summary>Состав списка обновлён: окну нужно вернуть выделение строки и прокрутку.</summary>
    public event Action? TreeRebuilt;

    /// <summary>Ищет узел дерева по признаку, включая служебные узлы и подгруппы.</summary>
    private GroupNodeViewModel? FindNode(Func<GroupNodeViewModel, bool> match)
    {
        GroupNodeViewModel? Search(GroupNodeViewModel node)
        {
            if (match(node))
                return node;
            foreach (var child in node.Children)
            {
                var found = Search(child);
                if (found is not null)
                    return found;
            }
            return null;
        }

        foreach (var root in GroupNodes)
        {
            var found = Search(root);
            if (found is not null)
                return found;
        }
        return null;
    }

    /// <summary>
    /// Раскладывает базы по узлам дерева: по полному пути группы, закреплённые
    /// дополнительно в отдельный узел, остальные в узел «без группы». Узлы
    /// «закреплённые» и «без группы» добавляются к корням, если непусты.
    /// </summary>
    private void DistributeInfobases(List<GroupNodeViewModel> roots)
    {
        var pinnedNode = new GroupNodeViewModel(null, marker: GroupNodeViewModel.PinnedMarker);
        var noGroupNode = new GroupNodeViewModel(null, marker: GroupNodeViewModel.NoGroupMarker);

        // Индексация по полному пути узла: база хранит путь группы строкой.
        var pathToNode = new Dictionary<string, GroupNodeViewModel>(StringComparer.OrdinalIgnoreCase);
        void Index(GroupNodeViewModel node)
        {
            if (node.Group is not null && !string.IsNullOrEmpty(node.FullPath))
            {
                pathToNode[node.FullPath] = node;
                var normalized = NormalizeGroupPath(node.FullPath);
                if (!string.IsNullOrEmpty(normalized))
                    pathToNode[normalized] = node;
            }
            foreach (var child in node.Children)
                Index(child);
        }
        foreach (var root in roots)
            Index(root);

        foreach (var node in pathToNode.Values)
            node.SetNotificationsSuppressed(true);
        pinnedNode.SetNotificationsSuppressed(true);
        noGroupNode.SetNotificationsSuppressed(true);

        foreach (var infobase in ApplyCurrentSort(_allInfobases))
        {
            if (infobase.IsPinned)
                pinnedNode.Infobases.Add(infobase);

            var groupPath = infobase.Group?.Trim() ?? string.Empty;
            if (!string.IsNullOrEmpty(groupPath)
                && (pathToNode.TryGetValue(groupPath, out var node)
                    || pathToNode.TryGetValue(NormalizeGroupPath(groupPath), out node)))
                node.Infobases.Add(infobase);
            else
                noGroupNode.Infobases.Add(infobase);
        }

        foreach (var node in pathToNode.Values)
            node.SetNotificationsSuppressed(false);
        pinnedNode.SetNotificationsSuppressed(false);
        noGroupNode.SetNotificationsSuppressed(false);

        // Порядок как в WPF-версии: закреплённые, без группы, затем группы
        // по алфавиту в выбранном направлении.
        var comparer = StringComparer.OrdinalIgnoreCase;
        roots.Sort(_groupSortAscending
            ? (a, b) => comparer.Compare(a.DisplayName, b.DisplayName)
            : (a, b) => comparer.Compare(b.DisplayName, a.DisplayName));
        foreach (var root in roots)
            root.SortChildrenRecursive(_groupSortAscending);

        if (noGroupNode.Infobases.Count > 0)
            roots.Insert(0, noGroupNode);
        if (pinnedNode.Infobases.Count > 0)
            roots.Insert(0, pinnedNode);

        foreach (var root in roots)
        {
            root.PopulateItems(_showEmptyGroups);
            ApplyExpandedState(root);
            SubscribeExpandedTracking(root);
        }
    }

    /// <summary>
    /// Временный фильтр: поиск, отбор по тегам или вид списка кроме «Все».
    /// В этом режиме дерево показывает плоский найденный список, а не группы.
    /// </summary>
    private bool IsFilterModeActive() =>
        !string.IsNullOrWhiteSpace(SearchText) || HasActiveTagFilter || _listMode != "All";

    /// <summary>Применяет фильтр по виду списка и поиску.</summary>
    private void ApplyFilter()
    {
        // События подняты здесь, а не в RebuildTree: список меняет состав
        // и мимо полной пересборки, через поиск, вкладки, отбор по тегам,
        // переключатель тегов и группировки. Все эти пути идут сюда.
        TreeRebuilding?.Invoke();

        var filterActive = IsFilterModeActive();

        // Плоский список нужен в двух случаях: активен фильтр (поиск, теги,
        // «Избранное», «Недавние») либо пользователь отключил группировку.
        // Дерево привязано только к GroupNodes, поэтому результат кладётся
        // одним узлом туда же, иначе список остался бы пустым.
        if (filterActive || !_groupByGroup)
        {
            // В режиме «Недавние» порядок задаёт дата запуска, в остальных —
            // выбранное поле сортировки.
            var matched = filterActive ? _allInfobases.Where(MatchesFilter) : _allInfobases;
            var visible = (_listMode == "Recent"
                ? matched.OrderByDescending(i => i.LastLaunchDate ?? DateTime.MinValue)
                         .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                : ApplyCurrentSort(matched)).ToList();

            FlatItems.Clear();
            foreach (var ib in visible)
                FlatItems.Add(ib);

            var flatNode = new GroupNodeViewModel(
                null,
                displayName: filterActive ? LocalizationManager.T("Main.FlatFound") : null,
                marker: filterActive ? null : GroupNodeViewModel.AllBasesMarker);

            flatNode.SetNotificationsSuppressed(true);
            try
            {
                foreach (var ib in visible)
                    flatNode.Infobases.Add(ib);
            }
            finally
            {
                flatNode.SetNotificationsSuppressed(false);
            }

            flatNode.PopulateItems();
            flatNode.SetExpandedSilent(true);

            GroupNodes.Clear();
            GroupNodes.Add(flatNode);
        }
        else
        {
            FlatItems.Clear();
            GroupNodes.Clear();
            foreach (var root in AllGroupNodes)
            {
                if (_showEmptyGroups || root.ContainsInfobases)
                    GroupNodes.Add(root);
            }
        }

        // Выбранный узел мог исчезнуть из дерева: поиск и отключение группировки
        // подменяют его плоским списком. Иначе правая панель продолжила бы
        // показывать группу, которой в дереве уже нет.
        if (SelectedGroupNode is { } selected && FindNode(node => ReferenceEquals(node, selected)) is null)
            SelectedGroupNode = null;

        TreeRebuilt?.Invoke();
    }

    private bool MatchesFilter(Infobase ib)
    {
        if (_listMode == "Favorites" && !ib.IsFavorite)
            return false;
        if (_listMode == "Recent" && ib.LastLaunchDate is null)
            return false;

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var q = SearchText.Trim();
            if (!ContainsIgnoreCase(ib.Name, q)
                && !ContainsIgnoreCase(ib.ServerDatabaseDisplay, q)
                && !ContainsIgnoreCase(ib.ConfigurationName, q)
                && !ContainsIgnoreCase(ib.PlatformVersion, q))
                return false;
        }

        foreach (var tag in TagFilterItems.Where(t => t.IsSelected))
        {
            if (!ib.Tags.Any(t => string.Equals(t, tag.Name, StringComparison.OrdinalIgnoreCase)))
                return false;
        }
        return true;
    }

    private static bool ContainsIgnoreCase(string? source, string value) =>
        !string.IsNullOrWhiteSpace(source) && source.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;

    // ======================= Теги =======================

    private void RebuildTagFilters()
    {
        // Выбор сохраняется: пересборка идёт при каждом перестроении дерева,
        // и без этого действующий отбор сбрасывался бы при любой правке базы.
        var selected = TagFilterItems.Where(t => t.IsSelected)
            .Select(t => t.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        TagFilterItems.Clear();
        foreach (var tag in _allInfobases
                     .SelectMany(ib => ib.Tags)
                     .Where(t => !string.IsNullOrWhiteSpace(t))
                     .Select(t => t.Trim())
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(t => t, StringComparer.OrdinalIgnoreCase))
        {
            TagFilterItems.Add(new TagFilterItem(tag) { IsSelected = selected.Contains(tag) });
        }

        // HasActiveTagFilter отдельно не поднимается: интерфейс слушает и его,
        // и это событие, и панель пересобиралась бы дважды на одно перестроение.
        TagFiltersRebuilt?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Набор тегов пересобран целиком. Отдельное событие нужно, чтобы
    /// интерфейс перестраивал панель один раз, а не на каждый добавленный
    /// элемент коллекции.
    /// </summary>
    public event EventHandler? TagFiltersRebuilt;

    /// <summary>
    /// Добавляет тег к базе прямо в строке названия (без отдельного окна).
    /// Параметр приходит как object[]: [0] = Infobase, [1] = текст тега.
    /// </summary>
    private void AddTagInline(object? parameter)
    {
        if (parameter is not object[] values || values.Length < 2)
            return;
        if (values[0] is not Infobase infobase || values[1] is not string rawTag)
            return;

        var tag = rawTag.Trim();
        if (tag.Length == 0 || infobase.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            return;

        var wasFiltering = IsFilterModeActive();
        infobase.Tags.Add(tag);
        infobase.NotifyTagsChanged();
        SaveSilently();
        RebuildTagFilters();

        // Пересобираем список, только если состав видимых баз мог измениться:
        // без фильтра строка сама показывает новый чип по уведомлению модели.
        if (wasFiltering || IsFilterModeActive())
            ApplyFilter();
    }

    /// <summary>
    /// Убирает тег у базы. Параметр той же формы, что и в WPF-версии:
    /// массив из базы и тега.
    /// </summary>
    private void RemoveTag(object? parameter)
    {
        if (parameter is not object[] values || values.Length < 2)
            return;
        if (values[0] is not Infobase infobase || values[1] is not string tag)
            return;

        // Признак снимается до пересборки отбора: она убирает из панели тег,
        // которого больше нет ни у одной базы, и проверка после неё уже
        // не увидела бы, что список показан отобранным.
        var wasFiltering = IsFilterModeActive();
        infobase.Tags.RemoveAll(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));
        infobase.NotifyTagsChanged();
        SaveSilently();
        RebuildTagFilters();

        if (wasFiltering || IsFilterModeActive())
            ApplyFilter();
    }

    private void SearchByTag(object? parameter)
    {
        var name = parameter as string;
        if (string.IsNullOrWhiteSpace(name))
            return;
        var item = TagFilterItems.FirstOrDefault(t => t.Name == name);
        if (item is null)
            return;
        item.IsSelected = !item.IsSelected;
        OnPropertyChanged(nameof(HasActiveTagFilter));
        ApplyFilter();
    }

    public bool HasActiveTagFilter => TagFilterItems.Any(t => t.IsSelected);

    private void ClearTagFilters()
    {
        foreach (var item in TagFilterItems)
            item.IsSelected = false;
        OnPropertyChanged(nameof(HasActiveTagFilter));
        ApplyFilter();
    }

    // ======================= Группы =======================

    /// <summary>Возвращает true, если группа свёрнута (используется конвертерами).</summary>
    public bool IsGroupCollapsed(string groupName)
    {
        if (string.IsNullOrWhiteSpace(groupName))
            return false;
        foreach (var node in AllGroupNodes)
        {
            var found = FindNode(node, groupName);
            if (found is not null)
                return !found.IsExpanded;
        }
        return false;
    }

    private static GroupNodeViewModel? FindNode(GroupNodeViewModel node, string fullPathOrName)
    {
        if (string.Equals(node.FullPath, fullPathOrName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(node.DisplayName, fullPathOrName, StringComparison.OrdinalIgnoreCase))
            return node;
        foreach (var child in node.Children)
        {
            var found = FindNode(child, fullPathOrName);
            if (found is not null)
                return found;
        }
        return null;
    }

    private void ExpandAllGroups() => SetExpandedForAll(true);

    private void CollapseAllGroups() => SetExpandedForAll(false);

    /// <summary>
    /// Массово меняет раскрытие всех узлов и сохраняет настройки один раз.
    /// Поузловое сохранение записывало бы весь файл настроек столько раз,
    /// сколько узлов в дереве, и всё это на потоке интерфейса.
    /// </summary>
    private void SetExpandedForAll(bool expanded)
    {
        var before = _collapsedGroups.Count;
        var snapshot = _collapsedGroups.ToHashSet(StringComparer.OrdinalIgnoreCase);

        _deferCollapsedSave = true;
        try
        {
            foreach (var root in AllGroupNodes)
                SetExpandedRecursive(root, expanded);
        }
        finally
        {
            _deferCollapsedSave = false;
        }

        // Файл настроек пишется только если набор действительно изменился:
        // «развернуть все» на уже развёрнутом дереве не должно трогать диск.
        if (_collapsedGroups.Count != before || !_collapsedGroups.SetEquals(snapshot))
            PersistCollapsedGroups();
    }

    private static void SetExpandedRecursive(GroupNodeViewModel node, bool expanded)
    {
        node.SetExpandedSilent(expanded);
        node.NotifyIsExpanded();
        foreach (var child in node.Children)
            SetExpandedRecursive(child, expanded);
    }

    private void SortGroups(bool ascending)
    {
        // Направление запоминается: RebuildTree пересобирает дерево из _groups
        // в исходном порядке, поэтому сортировать сами узлы бесполезно.
        _groupSortAscending = ascending;
        RebuildTree();
    }

    // ======================= Запуск / действия =======================

    /// <summary>
    /// Запуск с разовыми параметрами: диалог правит параметры только на один
    /// запуск, сохранённое значение базы возвращается в любом случае.
    /// </summary>
    private void LaunchWithParams(LaunchKind kind)
    {
        var infobase = SelectedInfobase;
        if (infobase is null)
            return;

        var dialog = new Configuration_Management.LaunchParametersWindow(infobase.LaunchParameters ?? string.Empty);
        if (!dialog.ShowDialogSync(OwnerWindow()))
            return;

        var saved = infobase.LaunchParameters ?? string.Empty;
        try
        {
            infobase.LaunchParameters = dialog.Result;
            Launch(_launchVm.LaunchCommand, kind);
        }
        finally
        {
            infobase.LaunchParameters = saved;
            // Успешный запуск сохраняет список баз изнутри, то есть подменённое
            // значение уже успело уйти на диск. Возвращаем файл к прежнему виду,
            // иначе разовые параметры остались бы у базы навсегда.
            SaveSilently();
        }
    }

    /// <summary>
    /// Запуск с авторизацией: сохранённые имя и пароль на один раз убираются,
    /// чтобы платформа спросила их сама. Прежние значения возвращаются всегда.
    /// </summary>
    private void LaunchWithAuth()
    {
        var infobase = SelectedInfobase;
        if (infobase?.Connection is not { } connection)
            return;

        var savedUser = connection.User;
        var savedPassword = connection.Password;
        var savedMode = connection.AuthenticationMode;

        // У базы может быть отдельная авторизация Предприятия, и лаунчер
        // предпочитает именно её: без этого пункт молча запускал бы клиент
        // с сохранёнными учётными данными.
        var enterpriseAuth = infobase.EnterpriseAuth;
        var savedAuthUser = enterpriseAuth?.User;
        var savedAuthPassword = enterpriseAuth?.Password;
        var savedAuthMode = enterpriseAuth?.AuthenticationMode;

        try
        {
            connection.User = string.Empty;
            connection.Password = string.Empty;
            connection.AuthenticationMode = AuthenticationMode.Prompt;

            if (enterpriseAuth is not null)
            {
                enterpriseAuth.User = string.Empty;
                enterpriseAuth.Password = string.Empty;
                enterpriseAuth.AuthenticationMode = AuthenticationMode.Prompt;
            }

            Launch(_launchVm.LaunchCommand, LaunchKind.Enterprise);
        }
        finally
        {
            connection.User = savedUser;
            connection.Password = savedPassword;
            connection.AuthenticationMode = savedMode;

            if (enterpriseAuth is not null)
            {
                enterpriseAuth.User = savedAuthUser ?? string.Empty;
                enterpriseAuth.Password = savedAuthPassword ?? string.Empty;
                enterpriseAuth.AuthenticationMode = savedAuthMode ?? AuthenticationMode.Prompt;
            }

            // Причина та же, что и у запуска с параметрами: успешный запуск
            // сохраняет базы изнутри, и пустые учётные данные уже на диске.
            SaveSilently();
        }
    }

    /// <summary>
    /// Запуск базы из меню трея: прямо по ссылке, не трогая выделение в списке.
    /// В Windows-версии это делает LaunchInfobaseById (MainViewModel.Commands.cs:544),
    /// и она тоже не меняет SelectedInfobase: иначе запуск из трея переставлял бы
    /// выделение, правую панель и строку состояния. Источник записи в истории
    /// тот же, что у автора.
    /// </summary>
    public void LaunchFromTray(Infobase ib, bool configurator)
    {
        if (!_allInfobases.Contains(ib))
            return;

        var ok = configurator
            ? _launcher.Launch(ib, Services.OneCLaunchMode.Configurator)
            : _launcher.Launch(ib, Services.OneCLaunchMode.Enterprise);

        if (ok)
        {
            ib.AddLaunchHistory(configurator ? "Configurator" : "Enterprise", "tray");
            SaveSilently();
            OnPropertyChanged(nameof(RecentInfobases));
            _logger.Info($"[tray] Запущена «{ib.Name}» ({(configurator ? "Конфигуратор" : "Предприятие")})");
            NotifyAfterLaunch();
        }
        else
        {
            _logger.Warn($"[tray] Не удалось запустить «{ib.Name}»");
        }
    }

    private void OnLaunched()
    {
        if (SelectedInfobase is not null)
        {
            SelectedInfobase.AddLaunchHistory(LocalizationManager.T("Main.LaunchAction"));
            SaveSilently();
        }

        // Список недавних изменился, и его показывает меню трея.
        OnPropertyChanged(nameof(RecentInfobases));

        // Одна точка на все пути запуска: команды окна, контекстное меню и трей
        // приходят сюда же, в отличие от WPF, где уведомление расставлено трижды.
        NotifyAfterLaunch();
    }

    private void EditInfobase()
    {
        var ib = SelectedInfobase;
        if (ib is null)
            return;

        // Построение окна может упасть (битые ресурсы, иконки, темы, сбой локализации):
        // на Linux без обработчика это роняло приложение abort-ом (issue #168). Ловим
        // здесь, логируем и не даём сбою одного окна остановить работу программы.
        Configuration_Management.ConnectionSettingsWindow dialog;
        try
        {
            dialog = new Configuration_Management.ConnectionSettingsWindow(
                ib, _groups, InstalledPlatformVersions(), ib.Group,
                AvailableServers(), AvailablePorts());
        }
        catch (Exception ex)
        {
            _logger.Error($"Не удалось открыть окно свойств базы: {ex.Message}", ex);
            return;
        }

        if (!dialog.ShowDialogSync(OwnerWindow()))
            return;

        // Применяем изменения к существующему объекту, а не заменяем его новым.
        // Диалог возвращает свежий Infobase, в который переносятся только
        // редактируемые поля, поэтому замена стёрла бы историю запусков,
        // порядок сортировки и номер горячей клавиши избранного.
        ib.Id = dialog.Result.Id;
        ib.Name = dialog.Result.Name;
        ib.Group = dialog.Result.Group;
        ib.Description = dialog.Result.Description;
        ib.PlatformVersion = dialog.Result.PlatformVersion;
        // Поля конфигурации переносим явно, иначе введённые вручную имя/версия
        // конфигурации не сохранялись бы (issue #164).
        ib.ConfigurationName = dialog.Result.ConfigurationName;
        ib.ConfigurationVersion = dialog.Result.ConfigurationVersion;
        ib.Architecture = dialog.Result.Architecture;
        ib.LaunchMode = dialog.Result.LaunchMode;
        ib.LaunchParameters = dialog.Result.LaunchParameters;
        ib.DefaultLaunchMode = dialog.Result.DefaultLaunchMode;
        ib.ClientType = dialog.Result.ClientType;
        ib.IsFavorite = dialog.Result.IsFavorite;
        ib.IsPinned = dialog.Result.IsPinned;
        ib.LastLaunchDate = dialog.Result.LastLaunchDate;
        ib.Tags = dialog.Result.Tags;
        ib.MetadataRoot = dialog.Result.MetadataRoot;
        ib.Connection = dialog.Result.Connection;
        ib.EnterpriseAuth = dialog.Result.EnterpriseAuth;
        ib.ConfiguratorAuth = dialog.Result.ConfiguratorAuth;
        ib.Repository = dialog.Result.Repository;

        // Правка могла снять или поставить звезду, а у базы без идентификатора
        // сменить и ключ слота: он строится из имени. Версия для Windows этого
        // не делает, и там слот теряется молча до следующего пересчёта.
        if (!ib.IsFavorite)
            _favoriteHotkeyIds.Remove(FavoriteKey(ib));
        SyncFavoriteHotkeys();

        SaveSilently();
        RebuildTree();
        ExportToIbasesAfterLocalChange();
        SelectedInfobase = ib;
        // Ссылка та же, поэтому сеттер молчит, а в строке состояния остаются
        // прежние порт, версия, пользователь и путь. Пересобираем явно.
        UpdateStatus();
    }

    private void AddInfobase()
    {
        Configuration_Management.AddEditWindow chooser;
        try { chooser = new Configuration_Management.AddEditWindow(); }
        catch (Exception ex)
        {
            _logger.Error($"Не удалось открыть окно выбора типа элемента: {ex.Message}", ex);
            return;
        }
        if (!chooser.ShowDialogSync(OwnerWindow()))
            return;

        var defaultGroupPath = SelectedGroupNode?.Group is not null
            ? SelectedGroupNode.FullPath
            : (SelectedInfobase?.Group ?? string.Empty);

        switch (chooser.SelectedType)
        {
            case "Group":
                AddGroup();
                break;

            case "CreateEmpty":
            case "CreateFromTemplate":
                CreateInfobase(chooser.SelectedType == "CreateFromTemplate", defaultGroupPath);
                break;

            default:
                RegisterExistingInfobase(defaultGroupPath);
                break;
        }
    }

    /// <summary>Создание новой базы: пустой или из шаблона.</summary>
    private void CreateInfobase(bool fromTemplate, string defaultGroupPath)
    {
        Configuration_Management.CreateInfobaseWindow dialog;
        try
        {
            dialog = new Configuration_Management.CreateInfobaseWindow(
                fromTemplate,
                InstalledPlatformVersions(),
                defaultGroupPath,
                _groups);
        }
        catch (Exception ex)
        {
            _logger.Error($"Не удалось открыть окно создания базы: {ex.Message}", ex);
            return;
        }

        if (!dialog.ShowDialogSync(OwnerWindow()) || dialog.Result is null)
            return;

        _allInfobases.Add(dialog.Result);
        SaveSilently();
        RebuildTree();
        ExportToIbasesAfterLocalChange();
        SelectedInfobase = dialog.Result;
        _dialog.ShowInfo(
            string.Format(LocalizationManager.T("Main.DlgBaseCreated"), dialog.Result.Name),
            LocalizationManager.T("Main.DlgBaseCreatedTitle"));
    }

    /// <summary>Регистрация уже существующей базы в списке.</summary>
    private void RegisterExistingInfobase(string defaultGroupPath)
    {
        Configuration_Management.ConnectionSettingsWindow dialog;
        try
        {
            dialog = new Configuration_Management.ConnectionSettingsWindow(
                null, _groups, InstalledPlatformVersions(), defaultGroupPath,
                AvailableServers(), AvailablePorts());
        }
        catch (Exception ex)
        {
            _logger.Error($"Не удалось открыть окно регистрации базы: {ex.Message}", ex);
            return;
        }

        if (!dialog.ShowDialogSync(OwnerWindow()))
            return;

        _allInfobases.Add(dialog.Result);
        SaveSilently();
        RebuildTree();
        ExportToIbasesAfterLocalChange();
        SelectedInfobase = dialog.Result;
    }

    /// <summary>Добавление группы: родитель берётся из выделения в дереве.</summary>
    private void AddGroup()
    {
        var parent = SelectedGroupNode?.Group;
        if (parent is null && !string.IsNullOrWhiteSpace(SelectedInfobase?.Group))
            parent = FindGroupByFullPath(SelectedInfobase!.Group);
        var dialog = new Configuration_Management.GroupEditWindow(_groups, parent);
        if (!dialog.ShowDialogSync(OwnerWindow()))
            return;

        _groups.Add(dialog.Result);
        SaveGroupsSilently();
        RebuildTree();
    }

    /// <summary>Список установленных версий платформы для диалогов.</summary>
    /// <summary>
    /// Версии платформы, найденные штатными путями и дополнительными путями
    /// из настроек. Дополнительные пути в Linux-ветке до сих пор никуда
    /// не передавались, и настройка была бесполезной.
    /// </summary>
    public List<string> FindPlatformVersions(IEnumerable<string>? additionalPaths = null)
    {
        try { return _platformService.FindInstalledVersions(additionalPaths ?? _settings.AdditionalPlatformSearchPaths); }
        catch (Exception ex)
        {
            _logger.Warn($"Не удалось получить список версий платформы: {ex.Message}");
            return new List<string>();
        }
    }

    /// <summary>Дополнительные пути поиска платформы из настроек.</summary>
    public IReadOnlyList<string> AdditionalPlatformSearchPaths => _settings.AdditionalPlatformSearchPaths;

    /// <summary>Режим «Разрядности по умолчанию»: «X64», «X86» или «Priority».</summary>
    public string DefaultArchitecture => _settings.DefaultArchitecture;

    // ---- Синхронизация с ibases.v8i ----
    public IbasesSyncMode IbasesSyncMode => _settings.IbasesSyncMode;
    public string IbasesSyncFilePath => _settings.IbasesSyncFilePath;
    public IbasesSyncTrigger IbasesSyncTrigger => _settings.IbasesSyncTrigger;
    public int IbasesSyncIntervalMinutes => _settings.IbasesSyncIntervalMinutes;
    public string IbasesSyncScheduleTime => _settings.IbasesSyncScheduleTime;
    public bool IbasesBackupEnabled => _settings.IbasesBackupEnabled;
    public int IbasesBackupKeepCount => _settings.IbasesBackupKeepCount;

    /// <summary>Применяет настройки синхронизации с файлом списка баз платформы.</summary>
    public void ApplyIbasesSyncSettings(IbasesSyncMode mode, string filePath, IbasesSyncTrigger trigger,
        int intervalMinutes, string scheduleTime, bool backupEnabled, int backupKeepCount)
    {
        _settings.IbasesSyncMode = mode;
        _settings.IbasesSyncFilePath = filePath ?? string.Empty;
        _settings.IbasesSyncTrigger = trigger;
        _settings.IbasesSyncIntervalMinutes = intervalMinutes > 0 ? intervalMinutes : 30;
        _settings.IbasesSyncScheduleTime = scheduleTime ?? string.Empty;
        _settings.IbasesBackupEnabled = backupEnabled;
        _settings.IbasesBackupKeepCount = backupKeepCount > 0 ? backupKeepCount : 5;

        SaveSettingsSilently();
        RestartAutoSync();
    }

    // ---- Профиль: резервное копирование и восстановление ----

    /// <summary>Каталог резервной копии профиля (настройки, базы, пользователи/пароли, ibases.v8i).</summary>
    public string ProfileBackupDirectory => _settings.ProfileBackupDirectory;

    /// <summary>Восстанавливать профиль из каталога резервной копии при каждом запуске.</summary>
    public bool ProfileRestoreOnStartup => _settings.ProfileRestoreOnStartup;

    /// <summary>Применяет настройки резервного копирования профиля из окна настроек.</summary>
    public void ApplyProfileBackupSettings(string backupDirectory, bool restoreOnStartup)
    {
        _settings.ProfileBackupDirectory = backupDirectory?.Trim() ?? string.Empty;
        _settings.ProfileRestoreOnStartup = restoreOnStartup;
        SaveSettingsSilently();
    }

    /// <summary>
    /// Сохраняет текущий профиль (настройки, список баз с пользователями и паролями,
    /// группы, ibases.v8i) в настроенный каталог. Возвращает true при успехе.
    /// </summary>
    public bool BackupProfile()
    {
        var dir = _settings.ProfileBackupDirectory;
        if (string.IsNullOrWhiteSpace(dir))
        {
            _dialog.ShowWarning(LocalizationManager.T("Settings.Profile.NoDirectory"));
            return false;
        }
        try
        {
            var count = ProfileBackupService.Backup(dir, _settings.IbasesSyncFilePath);
            _logger.Info($"Резервная копия профиля сохранена в {dir} ({count} файлов)");
            _dialog.ShowInfo(string.Format(LocalizationManager.T("Settings.Profile.BackupDone"), count));
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error("Ошибка резервного копирования профиля", ex);
            _dialog.ShowError(string.Format(LocalizationManager.T("Settings.Profile.BackupFailed"), ex.Message));
            return false;
        }
    }

    /// <summary>
    /// Восстанавливает профиль из настроенного каталога и перезагружает данные,
    /// чтобы они применились без перезапуска. Возвращает true при успехе.
    /// </summary>
    public bool RestoreProfile()
    {
        var dir = _settings.ProfileBackupDirectory;
        if (string.IsNullOrWhiteSpace(dir))
        {
            _dialog.ShowWarning(LocalizationManager.T("Settings.Profile.NoDirectory"));
            return false;
        }
        if (!ProfileBackupService.HasBackup(dir))
        {
            _dialog.ShowWarning(LocalizationManager.T("Settings.Profile.NoBackup"));
            return false;
        }
        try
        {
            var count = ProfileBackupService.Restore(dir, _settings.IbasesSyncFilePath);
            _logger.Info($"Профиль восстановлен из {dir} ({count} файлов)");
            // Перезагружаем настройки, список баз и группы, чтобы они применились сразу.
            Initialize();
            _dialog.ShowInfo(string.Format(LocalizationManager.T("Settings.Profile.RestoreDone"), count));
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error("Ошибка восстановления профиля", ex);
            _dialog.ShowError(string.Format(LocalizationManager.T("Settings.Profile.RestoreFailed"), ex.Message));
            return false;
        }
    }

    /// <summary>
    /// Запоминает выбранную цветовую схему как активную и сохраняет её в слот
    /// соответствующей базовой темы (светлой/тёмной), чтобы переключение тем
    /// не сбрасывало настроенное оформление.
    /// </summary>
    public void ApplyColorScheme(Models.ColorScheme scheme)
    {
        if (scheme is null)
            return;
        var clone = scheme.Clone();
        clone.Normalize();
        _settings.ActiveColorScheme = clone;
        // Устаревшие раздельные слоты больше не ведутся: схема едина и несёт обе палитры.
        _settings.LightColorScheme = null;
        _settings.DarkColorScheme = null;
        // Применяем палитру по текущему варианту темы; сам вариант не меняем.
        ThemeManager.ApplyScheme(clone);
        // Общий цвет папок хранится в схеме и применяется при построении дерева —
        // пересобираем его, чтобы папки сразу перекрасились.
        RebuildTree();
        SaveSettingsSilently();
        OnPropertyChanged(nameof(ThemeName));
    }

    /// <summary>true, если имя соответствует встроенной теме («Светлая»/«Тёмная»).</summary>
    private static bool IsBuiltInSchemeName(string? name)
        => string.Equals(name, "Светлая", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, "Тёмная", StringComparison.OrdinalIgnoreCase);

    /// <summary>Сохранённая цветовая схема (две палитры): окно настроек открывает редактор
    /// с неё, а не с той, что применена предпросмотром.</summary>
    public Models.ColorScheme ActiveColorScheme
    {
        get
        {
            var saved = _settings.ActiveColorScheme;
            if (saved is not null && (saved.LightColors.Count > 0 || saved.DarkColors.Count > 0))
            {
                saved.Normalize();
                return saved;
            }
            return ThemeManager.GetBuiltInScheme(_settings.Theme) ?? Models.ColorScheme.CreateLight();
        }
    }

    /// <summary>Предупреждение из окна настроек: диалоги живут в сервисе вьюмодели.</summary>
    public void ShowWarning(string message) => _dialog.ShowWarning(message);

    /// <summary>Сообщение из окна настроек.</summary>
    public void ShowInfo(string message) => _dialog.ShowInfo(message);

    /// <summary>Сообщение со своим заголовком окна.</summary>
    public void ShowInfo(string message, string title) => _dialog.ShowInfo(message, title);

    /// <summary>Сообщение об ошибке из окна настроек.</summary>
    public void ShowError(string message) => _dialog.ShowError(message);

    /// <summary>Запрос подтверждения из окна настроек.</summary>
    public bool Confirm(string message) => _dialog.Confirm(message);

    /// <summary>Диалог выбора файла для окна настроек.</summary>
    public string? PickFile(string title, string filter) => _dialog.OpenFileDialog(title, filter);

    /// <summary>Диалог сохранения файла для окна настроек.</summary>
    public string? PickSaveFile(string title, string defaultFileName) =>
        _dialog.SaveFileDialog(title, defaultFileName);

    /// <summary>Диалог выбора каталога для окна настроек.</summary>
    public string? PickFolder(string title, string? initialDirectory = null)
        => _dialog.OpenFolderDialog(title, initialDirectory);

    /// <summary>
    /// Применяет настройки вкладки «Платформы»: дополнительные пути поиска
    /// и разрядность по умолчанию.
    /// </summary>
    public void ApplyPlatformSettings(IEnumerable<string> additionalPaths, string architecture)
    {
        _settings.AdditionalPlatformSearchPaths = additionalPaths
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _settings.DefaultArchitecture = NormalizeDefaultArchitecture(architecture);

        ApplyDefaultArchitecture();
        ApplyAdditionalSearchPaths();
        SaveSettingsSilently();
    }

    /// <summary>
    /// Отдаёт дополнительные пути поиска самой платформе: вкладка настроек
    /// передаёт их аргументом, а запуск читает статический список сервиса,
    /// и без этой передачи платформа из нестандартного каталога была видна
    /// в списке, но не находилась при запуске.
    /// </summary>
    private void ApplyAdditionalSearchPaths() =>
        PlatformVersionService.SetAdditionalSearchPaths(_settings.AdditionalPlatformSearchPaths);

    /// <summary>
    /// Убирает из списка файловые базы, у которых нет каталога или файла базы.
    /// Перед удалением показывается список того, что будет убрано.
    /// </summary>
    public void RemoveMissingFileBases()
    {
        var states = _allInfobases
            .Select(ib => (Infobase: ib, State: InfobaseMaintenanceService.GetFileBaseState(ib)))
            .ToList();

        var missing = states
            .Where(x => x.State == InfobaseMaintenanceService.FileBaseState.Missing)
            .Select(x => x.Infobase)
            .ToList();

        // Базы с недоступного диска в удаление не идут: «проверить не удалось»
        // это не «нет». О них сказано отдельно.
        var unknown = states.Count(x => x.State == InfobaseMaintenanceService.FileBaseState.Unknown);

        if (missing.Count == 0)
        {
            _dialog.ShowInfo(
                unknown == 0
                    ? LocalizationManager.T("Main.MissingNone")
                    : string.Format(LocalizationManager.T("Main.MissingOnlyUnchecked"), unknown),
                LocalizationManager.T("Main.CheckFileBasesTitle"));
            return;
        }

        var preview = string.Join("\n", missing.Take(15).Select(ib => "• " + ib.Name));
        if (missing.Count > 15)
            preview += string.Format(LocalizationManager.T("Main.MissingMore"), missing.Count - 15);
        if (unknown > 0)
            preview += "\n\n" + string.Format(LocalizationManager.T("Main.MissingUnchecked"), unknown);

        if (!_dialog.Confirm(
                string.Format(LocalizationManager.T("Main.MissingConfirm"), missing.Count, preview),
                LocalizationManager.T("Main.RemoveMissingTitle")))
            return;

        // Сначала запись, потом замена списка в памяти: при ошибке диска
        // пользователь остался бы с урезанным списком в окне и полным на диске,
        // а следующее сохранение записало бы урезанный поверх.
        var removing = new HashSet<Infobase>(missing, ReferenceEqualityComparer.Instance as IEqualityComparer<Infobase>);
        var remaining = _allInfobases.Where(ib => !removing.Contains(ib)).ToList();
        if (!SaveList(remaining))
        {
            _dialog.ShowError(LocalizationManager.T("Main.SaveFailedHint"),
                LocalizationManager.T("Main.RemoveMissingTitle"));
            return;
        }

        _allInfobases.Clear();
        _allInfobases.AddRange(remaining);
        // Состав списка изменился: слоты избранного пересчитываются, иначе
        // номер остаётся у удалённой базы, а её слот занят навсегда.
        SyncFavoriteHotkeys();

        if (SelectedInfobase is { } selected && !_allInfobases.Contains(selected))
            SelectedInfobase = null;

        RebuildTree();
        _logger.Info($"Удалено отсутствующих файловых баз: {missing.Count}");

        _dialog.ShowInfo(
            string.Format(LocalizationManager.T("Main.MissingRemoved"), missing.Count),
            LocalizationManager.T("Main.RemoveMissingTitle"));
    }

    /// <summary>Завершает запущенные процессы платформы 1С.</summary>
    public void KillOneCProcesses()
    {
        // Один снимок на вопрос и на действие: между ними процессы приходят
        // и уходят, и завершать пришлось бы не то, что показано.
        var snapshot = InfobaseMaintenanceService.SnapshotOneCProcesses();
        if (snapshot.Count == 0)
        {
            _dialog.ShowInfo(LocalizationManager.T("Main.NoProcesses"),
                LocalizationManager.T("Main.OneCProcessesTitle"));
            return;
        }

        // Разбивка и предупреждение идут перед вопросом: ключ подтверждения
        // общий с версией для Windows и заканчивается словом «Продолжить?»,
        // поэтому дописывать предупреждение после него нельзя.
        var breakdown = InfobaseMaintenanceService.DescribeProcesses(snapshot);
        var details = string.Format(LocalizationManager.T("Main.KillProcessesBreakdown"),
            string.Join(", ", breakdown.Select(p => $"{p.Name}: {p.Count}")));

        // Про остановку кластера предупреждаем только когда серверные процессы
        // действительно в списке.
        if (breakdown.Any(p => ServerProcessNames.Contains(p.Name, StringComparer.OrdinalIgnoreCase)))
            details += " " + LocalizationManager.T("Main.KillProcessesServerNote");

        var question = details + "\n\n"
            + string.Format(LocalizationManager.T("Main.KillProcessesConfirm"), snapshot.Count);

        if (!_dialog.Confirm(question, LocalizationManager.T("Main.KillProcessesTitle")))
            return;

        var (killed, failed) = InfobaseMaintenanceService.KillOneCProcesses(snapshot);
        _logger.Info($"Завершено процессов 1С: {killed}, не удалось: {failed}");

        var message = string.Format(LocalizationManager.T("Main.ProcessesKilled"), killed);
        if (failed > 0)
            message += "\n" + string.Format(LocalizationManager.T("Main.ProcessesKillFailed"), failed);

        _dialog.ShowInfo(message, LocalizationManager.T("Main.OneCProcessesTitle"));
    }

    /// <summary>Очищает список баз и групп целиком. Сами базы на диске не трогает.</summary>
    public void ClearAllInfobases()
    {
        if (_allInfobases.Count == 0 && _groups.Count == 0)
        {
            _dialog.ShowInfo(LocalizationManager.T("Main.ClearAllAlreadyEmpty"),
                LocalizationManager.T("Main.ClearAllTitle"));
            return;
        }

        if (!_dialog.Confirm(
                string.Format(LocalizationManager.T("Main.ClearAllConfirm"), _allInfobases.Count, _groups.Count),
                LocalizationManager.T("Main.ClearAllTitle")))
            return;

        // Пустые списки пишутся на диск до того, как очищается память:
        // иначе при отказе записи в окне пусто, а на диске прежнее, и первое
        // же следующее сохранение затирает уцелевшее. Файла два, поэтому при
        // отказе на втором первый возвращается обратно: иначе на диске
        // оставался бы пустой список баз при живых группах.
        var previousInfobases = _allInfobases.ToList();
        if (!SaveList(new List<Infobase>()))
        {
            _dialog.ShowError(LocalizationManager.T("Main.SaveFailedHint"),
                LocalizationManager.T("Main.ClearAllTitle"));
            return;
        }

        if (!SaveGroupList(new List<Group>()))
        {
            // Список баз уже записан пустым, возвращаем прежний. Если и это
            // не удалось, на диске пусто, а в памяти нет: чтобы состояния
            // сошлись, память тоже очищается, и об этом сказано отдельно.
            if (!SaveList(previousInfobases))
            {
                _allInfobases.Clear();
                // Состав списка изменился: слоты избранного пересчитываются, иначе
                // номер остаётся у удалённой базы, а её слот занят навсегда.
                SyncFavoriteHotkeys();
                _groups.Clear();
                SelectedInfobase = null;
                RebuildTree();
                _dialog.ShowError(LocalizationManager.T("Main.ClearAllPartial"),
                    LocalizationManager.T("Main.ClearAllTitle"));
                return;
            }

            _dialog.ShowError(LocalizationManager.T("Main.SaveFailedHint"),
                LocalizationManager.T("Main.ClearAllTitle"));
            return;
        }

        _allInfobases.Clear();
        // Состав списка изменился: слоты избранного пересчитываются, иначе
        // номер остаётся у удалённой базы, а её слот занят навсегда.
        SyncFavoriteHotkeys();
        _groups.Clear();
        SelectedInfobase = null;

        // Выгрузка в ibases.v8i намеренно не вызывается, как и в версии
        // для Windows: экспорт убирает из файла записи, которых нет
        // в приложении, и очистка списка вынесла бы пусковой список платформы.
        RebuildTree();
        _logger.Info("Список баз и групп очищен");

        _dialog.ShowInfo(LocalizationManager.T("Main.ClearAllDone"),
            LocalizationManager.T("Main.ClearAllTitle"));
    }

    /// <summary>Добавлять ли метку времени к имени файла выгрузки.</summary>
    public bool AddTimestampToExportFileName => _settings.AddTimestampToExportFileName;

    /// <summary>Формат метки времени в имени файла выгрузки.</summary>
    public string ExportTimestampFormat => _settings.ExportTimestampFormat;

    /// <summary>Применяет настройки имени файла выгрузки со вкладки «Базы».</summary>
    public void ApplyExportFileNameSettings(bool addTimestamp, string timestampFormat)
    {
        _settings.AddTimestampToExportFileName = addTimestamp;
        _settings.ExportTimestampFormat = string.IsNullOrWhiteSpace(timestampFormat)
            ? "yyyyMMdd_HHmmss"
            : timestampFormat.Trim();
        SaveSettingsSilently();
    }

    /// <summary>Выгружает список баз и групп в JSON-файл.</summary>
    public void ExportInfobases(bool addTimestamp, string timestampFormat)
    {
        if (_allInfobases.Count == 0)
        {
            _dialog.ShowInfo(LocalizationManager.T("Main.ExportEmpty"),
                LocalizationManager.T("Main.ExportBasesTitle"));
            return;
        }

        var path = _dialog.SaveFileDialog(
            LocalizationManager.T("Main.ExportBasesDialogTitle"),
            BuildExportFileName("infobases_export", ".json", addTimestamp, timestampFormat),
            LocalizationManager.T("Main.JsonFileFilter"));
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            var json = JsonSerializer.Serialize(
                new InfobaseExportData
                {
                    Infobases = _allInfobases.ToList(),
                    Groups = _groups.ToList()
                },
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                    // Кириллицу и прочие не-ASCII символы пишем читаемыми UTF-8,
                    // а не \uXXXX-последовательностями (issue #170).
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                });
            File.WriteAllText(path, json);

            _dialog.ShowInfo(
                string.Format(LocalizationManager.T("Main.ExportDone"), _allInfobases.Count, _groups.Count, path),
                LocalizationManager.T("Main.ExportBasesTitle"));
            _logger.Info($"Список баз выгружен в {path}");
        }
        catch (Exception ex)
        {
            _logger.Error("Ошибка выгрузки списка баз", ex);
            _dialog.ShowError(
                string.Format(LocalizationManager.T("Main.ErrExportFailed"), ex.Message),
                LocalizationManager.T("Main.ExportErrorTitle"));
        }
    }

    /// <summary>
    /// Загружает список баз и групп из JSON-файла, заменяя текущий.
    /// Понимает и старый формат, где в файле лежит только список баз.
    /// </summary>
    public void ImportInfobases()
    {
        var path = _dialog.OpenFileDialog(
            LocalizationManager.T("Main.ImportBasesDialogTitle"),
            LocalizationManager.T("Main.JsonFileFilter"));
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            var json = File.ReadAllText(path);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

            InfobaseExportData? exportData = null;
            try { exportData = JsonSerializer.Deserialize<InfobaseExportData>(json, options); }
            catch (JsonException) { }

            List<Infobase> loaded;
            List<Group> loadedGroups;
            if (exportData is not null && exportData.Infobases.Count > 0)
            {
                loaded = exportData.Infobases;
                loadedGroups = exportData.Groups;
            }
            else
            {
                loaded = JsonSerializer.Deserialize<List<Infobase>>(json, options) ?? new List<Infobase>();
                loadedGroups = new List<Group>();
            }

            if (loaded.Count == 0)
            {
                _dialog.ShowWarning(LocalizationManager.T("Main.ImportNoBases"),
                    LocalizationManager.T("Main.LoadBasesTitle"));
                return;
            }

            if (!_dialog.Confirm(
                    string.Format(LocalizationManager.T("Main.ImportConfirm"), loaded.Count, loadedGroups.Count),
                    LocalizationManager.T("Main.LoadBasesTitle")))
                return;

            _allInfobases.Clear();
            _allInfobases.AddRange(loaded);
            // Состав списка изменился: слоты избранного пересчитываются, иначе
            // номер остаётся у удалённой базы, а её слот занят навсегда.
            SyncFavoriteHotkeys();
            _groups.Clear();
            _groups.AddRange(loadedGroups);
            SelectedInfobase = null;

            var saved = SaveSilently();
            saved &= SaveGroupsSilently();
            RebuildTree();

            if (!saved)
            {
                // Список в памяти уже заменён, а на диске осталось прежнее
                // состояние: сообщать об успехе нельзя.
                _dialog.ShowError(
                    string.Format(LocalizationManager.T("Main.ErrLoadFailed"),
                        LocalizationManager.T("Main.SaveFailedHint")),
                    LocalizationManager.T("Main.LoadErrorTitle"));
                return;
            }

            _dialog.ShowInfo(
                string.Format(LocalizationManager.T("Main.ImportDoneMsg"), loaded.Count, loadedGroups.Count),
                LocalizationManager.T("Main.LoadBasesTitle"));
            _logger.Info($"Список баз загружен из {path}");
        }
        catch (Exception ex)
        {
            _logger.Error("Ошибка загрузки списка баз", ex);
            _dialog.ShowError(
                string.Format(LocalizationManager.T("Main.ErrLoadFailed"), ex.Message),
                LocalizationManager.T("Main.LoadErrorTitle"));
        }
    }

    /// <summary>
    /// Читает список баз из ibases.v8i и добавляет или обновляет базы.
    /// Путь ищется сам, и только если файла нет, спрашивается у пользователя.
    /// </summary>
    public void ImportFromIbasesV8i()
    {
        var path = IbasesV8iImporter.FindDefaultPath();
        if (string.IsNullOrWhiteSpace(path))
        {
            path = _dialog.OpenFileDialog(
                LocalizationManager.T("Settings.Ibases.FileDialogTitle"),
                LocalizationManager.T("Main.IbasesFileFilter"));
            if (string.IsNullOrWhiteSpace(path))
                return;
        }

        ImportFromIbasesFileInteractive(path);
    }

    /// <summary>
    /// Путь для ручной операции с ibases.v8i: заданный в окне настроек, а если
    /// поле пустое, стандартный. Берётся набранное в поле, а не сохранённое:
    /// в версии для Windows так делает только восстановление из копии,
    /// а загрузка и выгрузка проверяют один путь, а читают другой.
    /// </summary>
    private static string? ResolveIbasesPathForCommand(string? filePath)
        => string.IsNullOrWhiteSpace(filePath)
            ? IbasesV8iImporter.FindDefaultPath()
            : filePath.Trim();

    /// <summary>
    /// Ручная загрузка списка баз из ibases.v8i. О результате сообщает сам
    /// импорт, включая подтверждение, если файл требует удалить базы.
    /// </summary>
    public void ImportFromIbasesFile(string? filePath)
    {
        var path = ResolveIbasesPathForCommand(filePath);
        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
        {
            _dialog.ShowInfo(LocalizationManager.T("Settings.Ibases.ImportFileNotFound"),
                LocalizationManager.T("Settings.Ibases.ImportTitle"));
            return;
        }

        ImportFromIbasesFileInteractive(path);
    }

    /// <summary>Ручная выгрузка списка баз в ibases.v8i.</summary>
    public void ExportToIbasesFile(string? filePath)
    {
        var path = ResolveIbasesPathForCommand(filePath);
        if (string.IsNullOrWhiteSpace(path))
        {
            _dialog.ShowInfo(LocalizationManager.T("Settings.Ibases.ExportNoPath"),
                LocalizationManager.T("Settings.Ibases.ExportTitle"));
            return;
        }

        if (TryExportToIbases(path, backup: true, "Ручная выгрузка", out var error))
        {
            _dialog.ShowInfo(LocalizationManager.T("Settings.Ibases.ExportOk"),
                LocalizationManager.T("Settings.Ibases.ExportTitle"));
            return;
        }

        _dialog.ShowError(
            string.Format(LocalizationManager.T("Settings.Ibases.ExportFailed"), error),
            LocalizationManager.T("Settings.Ibases.ExportErrorTitle"));
    }

    /// <summary>
    /// Восстанавливает ibases.v8i из последней резервной копии рядом с файлом.
    /// Копии создаёт сама выгрузка, если включён соответствующий флажок.
    /// </summary>
    public void RestoreIbasesBackup(string? filePath)
    {
        var path = ResolveIbasesPathForCommand(filePath);
        if (string.IsNullOrWhiteSpace(path))
        {
            _dialog.ShowWarning(LocalizationManager.T("Settings.Ibases.RestoreNoPath"),
                LocalizationManager.T("Settings.Ibases.RestoreTitle"));
            return;
        }

        var backups = IbasesBackupService.ListBackups(path);
        if (backups.Count == 0)
        {
            _dialog.ShowInfo(
                string.Format(LocalizationManager.T("Settings.Ibases.RestoreNoBackups"), path),
                LocalizationManager.T("Settings.Ibases.RestoreTitle"));
            return;
        }

        var latest = backups[0];
        if (!_dialog.Confirm(
                string.Format(LocalizationManager.T("Settings.Ibases.RestoreConfirm"),
                    System.IO.Path.GetFileName(latest)),
                LocalizationManager.T("Settings.Ibases.RestoreConfirmTitle")))
            return;

        try
        {
            IbasesBackupService.RestoreBackup(latest, path);
            _logger.Info($"Файл {path} восстановлен из копии {latest}");
            _dialog.ShowInfo(LocalizationManager.T("Settings.Ibases.RestoreOk"),
                LocalizationManager.T("Settings.Ibases.RestoreTitle"));
        }
        catch (Exception ex)
        {
            _logger.Error("Не удалось восстановить ibases.v8i из копии", ex);
            _dialog.ShowError(
                string.Format(LocalizationManager.T("Settings.Ibases.RestoreFailed"), ex.Message),
                LocalizationManager.T("Common.Error"));
        }
    }

    /// <summary>
    /// Разовый импорт из ibases.v8i по требованию пользователя. Импортёр не
    /// только добавляет и обновляет базы, но и удаляет те, которых в файле нет,
    /// поэтому пустой или испорченный файл вычистил бы список. Ровно та же
    /// защита стоит в автоматической синхронизации.
    /// </summary>
    private void ImportFromIbasesFileInteractive(string path)
    {
        var before = _allInfobases.Count;

        try
        {
            // Импорт идёт в копии списков: он удаляет базы, которых нет в файле,
            // и до подтверждения пользователем рабочие списки трогать нельзя.
            // Проверено исполнением: без этого разовый импорт молча заменил
            // тридцать баз восемью из файла.
            var candidateInfobases = _allInfobases.ToList();
            var candidateGroups = _groups.ToList();
            var result = _sync.Import(path, candidateInfobases, candidateGroups);

            if (before > 0 && candidateInfobases.Count == 0)
            {
                _logger.Warn($"Импорт из {path} не дал ни одной базы, список приложения не меняем");
                _dialog.ShowWarning(LocalizationManager.T("Main.ImportNoBases"),
                    LocalizationManager.T("Main.ImportIbasesTitle"));
                return;
            }

            if (result.Removed > 0 && !_dialog.Confirm(
                    string.Format(LocalizationManager.T("Main.ImportRemovesConfirm"),
                        result.Removed, result.Added, result.Updated),
                    LocalizationManager.T("Main.ImportIbasesTitle")))
                return;

            _allInfobases.Clear();
            _allInfobases.AddRange(candidateInfobases);
            // Состав списка изменился: слоты избранного пересчитываются, иначе
            // номер остаётся у удалённой базы, а её слот занят навсегда.
            SyncFavoriteHotkeys();
            _groups.Clear();
            _groups.AddRange(candidateGroups);

            var saved = SaveSilently();
            // Импорт создаёт недостающие группы, и без их записи они пропадают
            // при следующем запуске.
            saved &= SaveGroupsSilently();
            RebuildTree();

            // Выбранную базу мог удалить сам импорт.
            if (SelectedInfobase is { } selected && !_allInfobases.Contains(selected))
                SelectedInfobase = null;

            StatusBarInfo = string.Format(LocalizationManager.T("Sync.ImportedCount"), _allInfobases.Count, _groups.Count);
            _logger.Info($"Импорт из ibases.v8i: {path}, добавлено {result.Added}, обновлено {result.Updated}, удалено {result.Removed}");

            if (!saved)
            {
                _dialog.ShowError(
                    string.Format(LocalizationManager.T("Main.ErrImportFailed"),
                        LocalizationManager.T("Main.SaveFailedHint")),
                    LocalizationManager.T("Main.ImportErrorTitle"));
                return;
            }

            _dialog.ShowInfo(
                string.Format(LocalizationManager.T("Main.ImportDone"),
                    result.Added, result.Updated, result.Skipped, result.GroupsCreated),
                LocalizationManager.T("Main.ImportIbasesTitle"));
        }
        catch (Exception ex)
        {
            _logger.Error("Ошибка импорта из ibases.v8i", ex);
            _dialog.ShowError(
                string.Format(LocalizationManager.T("Main.ErrImportFailed"), ex.Message),
                LocalizationManager.T("Main.ImportErrorTitle"));
        }
    }

    /// <summary>
    /// Импорт баз и настроек платформы из программы StartManager (issue #163).
    /// Читает каталог настроек StartManager (v8config.smc и settings.cnf), добавляет
    /// и обновляет базы с авторизацией (расшифровывая пароли по алгоритму Виженера),
    /// а также добавляет найденный путь платформы 1С (V8AppPath) в дополнительные
    /// пути поиска платформы приложения.
    /// </summary>
    public void ImportFromStartManager()
    {
        var dir = StartManagerImporter.FindDefaultSettingsDir();
        if (string.IsNullOrWhiteSpace(dir) || !System.IO.Directory.Exists(dir))
        {
            dir = _dialog.OpenFolderDialog(
                LocalizationManager.T("StartManager.ChooseFolder"), null);
            if (string.IsNullOrWhiteSpace(dir))
                return;
        }

        try
        {
            var candidateInfobases = _allInfobases.ToList();
            var candidateGroups = _groups.ToList();

            var result = StartManagerImporter.Import(dir, candidateInfobases, candidateGroups, ResolveIbasesFilePath());

            if (result.NoConfigFound)
            {
                _dialog.ShowInfo(
                    string.Format(LocalizationManager.T("StartManager.NoConfig"), dir),
                    LocalizationManager.T("StartManager.Title"));
                return;
            }

            if (result.NoIbasesFound)
            {
                _dialog.ShowInfo(
                    LocalizationManager.T("StartManager.NoIbases"),
                    LocalizationManager.T("StartManager.Title"));
                return;
            }

            if (result.Added == 0 && result.Updated == 0)
            {
                _dialog.ShowInfo(
                    LocalizationManager.T("StartManager.NothingImported"),
                    LocalizationManager.T("StartManager.Title"));
                return;
            }

            _allInfobases.Clear();
            _allInfobases.AddRange(candidateInfobases);
            SyncFavoriteHotkeys();
            _groups.Clear();
            _groups.AddRange(candidateGroups);

            var saved = SaveSilently();
            saved &= SaveGroupsSilently();
            RebuildTree();

            // Путь платформы 1С из settings.cnf добавляем в дополнительные пути поиска.
            var platformAdded = false;
            if (result.PlatformSearchPaths.Count > 0)
            {
                var paths = new List<string>(_settings.AdditionalPlatformSearchPaths);
                foreach (var p in result.PlatformSearchPaths)
                {
                    if (!paths.Contains(p, StringComparer.OrdinalIgnoreCase))
                    {
                        paths.Add(p);
                        platformAdded = true;
                    }
                }
                if (platformAdded)
                    ApplyPlatformSettings(paths, _settings.DefaultArchitecture);
            }

            StatusBarInfo = string.Format(
                LocalizationManager.T("Sync.ImportedCount"), _allInfobases.Count, _groups.Count);
            _logger.Info($"Импорт из StartManager: {dir}, добавлено {result.Added}, " +
                         $"обновлено {result.Updated}, пропущено {result.Skipped}");

            if (!saved)
            {
                _dialog.ShowError(
                    string.Format(LocalizationManager.T("Main.ErrImportFailed"),
                        LocalizationManager.T("Main.SaveFailedHint")),
                    LocalizationManager.T("Main.ImportErrorTitle"));
                return;
            }

            var message = string.Format(
                LocalizationManager.T("StartManager.Done"),
                result.Added, result.Updated, result.GroupsCreated, result.Skipped);
            if (result.Skipped > 0)
                message += "\n\n" + LocalizationManager.T("StartManager.SkippedHint");
            if (platformAdded)
                message += "\n" + LocalizationManager.T("StartManager.PlatformPathAdded");
            _dialog.ShowInfo(message, LocalizationManager.T("StartManager.Title"));
        }
        catch (Exception ex)
        {
            _logger.Error("Ошибка импорта из StartManager", ex);
            _dialog.ShowError(
                string.Format(LocalizationManager.T("Main.ErrImportFailed"), ex.Message),
                LocalizationManager.T("Main.ImportErrorTitle"));
        }
    }

    /// <summary>Имя файла выгрузки с меткой времени, если она включена.</summary>
    private static string BuildExportFileName(string baseName, string extension, bool addTimestamp, string timestampFormat)
    {
        if (!addTimestamp)
            return $"{baseName}{extension}";

        var format = string.IsNullOrWhiteSpace(timestampFormat) ? "yyyyMMdd_HHmmss" : timestampFormat;
        try
        {
            return $"{baseName}_{DateTime.Now.ToString(format)}{extension}";
        }
        catch (FormatException)
        {
            // Шаблон мог прийти из файла настроек, правленного руками.
            return $"{baseName}_{DateTime.Now:yyyyMMdd_HHmmss}{extension}";
        }
    }

    /// <summary>
    /// Процессы, завершение которых бьёт не по клиентскому сеансу: кластер
    /// серверов, сервер отладки, сервер данных и утилиты, держащие базу.
    /// </summary>
    private static readonly string[] ServerProcessNames =
    {
        "ragent", "rmngr", "rphost", "ras", "rac", "dbgs", "dbda", "ibsrv", "ibcmd", "crserver"
    };

    private bool SaveList(List<Infobase> infobases)
    {
        try { _repository.Save(infobases); return true; }
        catch (Exception ex) { _logger.Error("Не удалось сохранить список баз", ex); return false; }
    }

    /// <summary>
    /// Сохраняет список баз и перестраивает дерево после точечного изменения отдельных баз
    /// (например, определения конфигураций всех баз, issue #236). Аналог
    /// <see cref="MainViewModel.PersistInfobasesAfterInlineEdit"/> для Windows.
    /// </summary>
    public void PersistInfobasesAfterInlineEdit()
    {
        SaveSilently();
        RebuildTree();
    }

    private bool SaveGroupList(List<Group> groups)
    {
        try { _repository.SaveGroups(groups); return true; }
        catch (Exception ex) { _logger.Error("Не удалось сохранить группы", ex); return false; }
    }

    /// <summary>Каталоги шаблонов конфигураций, заданные пользователем.</summary>
    public IReadOnlyList<string> TemplateCatalogPaths => _settings.TemplateCatalogPaths;

    /// <summary>
    /// Применяет настройки каталогов шаблонов со вкладки «Базы». Поиск шаблонов
    /// читает статический список сервиса, поэтому без этой передачи заданные
    /// каталоги оставались только в файле настроек.
    /// </summary>
    public void ApplyTemplateCatalogPaths(IEnumerable<string> paths)
    {
        _settings.TemplateCatalogPaths = (paths ?? Enumerable.Empty<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        ApplyTemplateCatalogPaths();
        SaveSettingsSilently();
    }

    private void ApplyTemplateCatalogPaths() =>
        OneCTemplateService.SetUserTemplatePaths(_settings.TemplateCatalogPaths);

    /// <summary>
    /// Каталоги шаблонов, известные самой платформе: из её настроек и
    /// стандартный tmplts. Кнопка «Из 1С» на вкладке «Базы» заполняет
    /// список ими.
    /// </summary>
    public List<string> DiscoverTemplateCatalogPaths()
    {
        var found = new List<string>();
        try
        {
            var configured = OneCTemplateService.GetConfiguredOrDefaultTemplatePath();
            if (!string.IsNullOrWhiteSpace(configured))
                found.Add(configured);
            found.AddRange(OneCTemplateService.GetTemplateRootFolders());
        }
        catch (Exception ex)
        {
            _logger.Warn($"Не удалось прочитать каталоги шаблонов: {ex.Message}");
        }

        return found
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Передаёт режим «Разрядности по умолчанию» лаунчеру платформы. Без этого
    /// настройка в Linux-ветке не действовала: запуск всегда считал её x64.
    /// Строка "Priority" («Использовать приоритет базы») передаётся как есть —
    /// разрешение разрядности происходит в лаунчере.
    /// </summary>
    private void ApplyDefaultArchitecture() =>
        OneCLauncher.DefaultArchitectureMode = NormalizeDefaultArchitecture(_settings.DefaultArchitecture);

    /// <summary>Нормализует строку режима «Разрядности по умолчанию» (X86 / X64 / Priority).</summary>
    private static string NormalizeDefaultArchitecture(string? value)
    {
        if (string.Equals(value, "X86", StringComparison.OrdinalIgnoreCase))
            return "X86";
        if (string.Equals(value, "Priority", StringComparison.OrdinalIgnoreCase))
            return "Priority";
        return "X64";
    }

    /// <summary>
    /// Смена версии платформы у базы двойным щелчком по колонке версии.
    /// Разбирает вариант вида «8.3.27.1234 (64)»: разрядность уходит в своё
    /// поле, а в версии остаётся чистый номер, как в версии для Windows
    /// (MainWindow.Events.cs, OpenPlatformVersionPicker).
    /// </summary>
    public void PickPlatformVersionFor(Infobase? infobase)
    {
        if (infobase is null)
            return;

        SelectedInfobase = infobase;
        var dialog = new PlatformVersionPickerWindow(InstalledPlatformVersions(), infobase.PlatformVersion);
        if (!dialog.ShowDialogSync(OwnerWindow()))
            return;

        var selected = dialog.Result?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(selected))
            return;

        PlatformVersionService.ParseVariant(selected, out var cleanVersion, out var arch);
        var newVersion = string.IsNullOrWhiteSpace(cleanVersion) ? selected : cleanVersion;
        var versionChanged = !string.Equals(infobase.PlatformVersion, newVersion, StringComparison.Ordinal);
        // Раньше здесь был ранний return при совпадении версии — из-за этого
        // нельзя было сменить разрядность (х86 → х64) одной и той же версии (issue #146).
        if (versionChanged)
            infobase.PlatformVersion = newVersion;
        if (arch is "32" or "64")
            infobase.Architecture = arch;
        if (versionChanged || arch is "32" or "64")
        {
            SaveSilently();
            RebuildTree();
        }
    }

    private List<string> InstalledPlatformVersions()
    {
        try { return _platformService.FindInstalledVersions(_settings.AdditionalPlatformSearchPaths); }
        catch (Exception ex)
        {
            _logger.Warn($"Не удалось получить список версий платформы: {ex.Message}");
            return new List<string>();
        }
    }

    /// <summary>Приводит путь группы к каноническому виду: и «/», и «\\» как разделители.</summary>
    private static string NormalizeGroupPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;
        var parts = path
            .Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0);
        return string.Join(GroupHierarchyHelper.PathSeparator, parts);
    }

    /// <summary>Находит группу по полному пути с учётом нормализации разделителей.</summary>
    private Group? FindGroupByFullPath(string? fullPath)
    {
        var target = NormalizeGroupPath(fullPath);
        if (string.IsNullOrEmpty(target))
            return null;
        return _groups.FirstOrDefault(g =>
            string.Equals(NormalizeGroupPath(GroupHierarchyHelper.GetFullPath(g, _groups)), target,
                StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Серверы из уже зарегистрированных клиент-серверных баз (для автодополнения).</summary>
    private IEnumerable<string> AvailableServers() => _allInfobases
        .Where(b => b?.Connection?.Type == ConnectionType.ClientServer)
        .Select(b => b.Connection!.Server?.Trim() ?? string.Empty)
        .Where(s => !string.IsNullOrEmpty(s))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(s => s, StringComparer.OrdinalIgnoreCase);

    /// <summary>Порты из уже зарегистрированных клиент-серверных баз.</summary>
    private IEnumerable<int> AvailablePorts() => _allInfobases
        .Where(b => b?.Connection?.Type == ConnectionType.ClientServer)
        .Select(b => b.Connection!.Port)
        .Where(p => p > 0)
        .Distinct()
        .OrderBy(p => p);

    /// <summary>
    /// Выгружает список баз в ibases.v8i после локального изменения.
    /// Работает только если пользователь включил режим экспорта: по умолчанию
    /// IbasesSyncMode.None, то есть файл платформы не трогается вовсе.
    /// Перед записью снимается резервная копия, если она включена в настройках.
    /// </summary>
    private void ExportToIbasesAfterLocalChange()
    {
        if (_settings.IbasesSyncMode is not (IbasesSyncMode.Export or IbasesSyncMode.Both))
            return;

        var filePath = string.IsNullOrWhiteSpace(_settings.IbasesSyncFilePath)
            ? IbasesV8iImporter.FindDefaultPath()
            : _settings.IbasesSyncFilePath;
        if (string.IsNullOrWhiteSpace(filePath))
            return;

        try
        {
            if (_settings.IbasesBackupEnabled && System.IO.File.Exists(filePath))
            {
                try { IbasesBackupService.CreateBackup(filePath, _settings.IbasesBackupKeepCount); }
                catch (Exception ex) { _logger.Warn($"Не удалось создать резервную копию ibases.v8i: {ex.Message}"); }
            }

            _sync.Export(filePath, _allInfobases, _groups);
            _logger.Info($"Список баз выгружен в {filePath}");
        }
        catch (Exception ex)
        {
            _logger.Error("Ошибка выгрузки списка баз в ibases.v8i", ex);
            SyncMessage = string.Format(LocalizationManager.T("Sync.ExportError"), ex.Message);
        }
    }

    /// <summary>
    /// Ключ узла, пригодный для хранения в настройках, либо null.
    /// Годятся только полный путь группы и внутренний маркер служебного узла:
    /// они не зависят от языка интерфейса. У узла без группы и без маркера
    /// NodeKey это отображаемое имя, то есть локализованная строка, которая
    /// после смены языка перестанет совпадать, а с реальной группой такого же
    /// имени ещё и столкнётся.
    /// </summary>
    private static string? PersistableNodeKey(GroupNodeViewModel node)
    {
        if (node.Group is not null && !string.IsNullOrEmpty(node.FullPath))
            return node.FullPath;
        return string.IsNullOrEmpty(node.Marker) ? null : node.Marker;
    }

    /// <summary>
    /// Восстанавливает свёрнутость узла и его потомков из сохранённого набора.
    /// </summary>
    private void ApplyExpandedState(GroupNodeViewModel node)
    {
        var key = PersistableNodeKey(node);
        if (key is not null)
            node.SetExpandedSilent(!_collapsedGroups.Contains(key));
        foreach (var child in node.Children)
            ApplyExpandedState(child);
    }

    /// <summary>
    /// Подписывает узел на запоминание свёрнутости. Раскрытие меняется и мышью
    /// через привязку контейнера, и командами «развернуть все» / «свернуть все»,
    /// поэтому отслеживается само свойство, а не места его изменения.
    /// </summary>
    private void SubscribeExpandedTracking(GroupNodeViewModel node)
    {
        node.PropertyChanged -= OnNodeExpandedChanged;
        node.PropertyChanged += OnNodeExpandedChanged;
        foreach (var child in node.Children)
            SubscribeExpandedTracking(child);
    }

    private void OnNodeExpandedChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(GroupNodeViewModel.IsExpanded) || sender is not GroupNodeViewModel node)
            return;

        var key = PersistableNodeKey(node);
        if (key is null)
            return;

        var changed = node.IsExpanded ? _collapsedGroups.Remove(key) : _collapsedGroups.Add(key);
        if (!changed || _deferCollapsedSave)
            return;

        PersistCollapsedGroups();
    }

    private void PersistCollapsedGroups()
    {
        _settings.CollapsedGroups = _collapsedGroups.ToList();
        SaveSettingsSilently();
    }

    /// <summary>Сохраняет группы, возвращая признак успеха: ошибка идёт в журнал.</summary>
    private bool SaveGroupsSilently() => SaveGroupList(_groups);

    /// <summary>
    /// Окно-владелец для модальных окон. Спрятанное в трей окно владельцем
    /// быть не может: показ поверх невидимого окна роняет приложение.
    /// </summary>
    private static Avalonia.Controls.Window? OwnerWindow()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
            is not Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            return null;

        foreach (var window in desktop.Windows)
        {
            if (window.IsActive && window.IsVisible)
                return window;
        }

        return desktop.MainWindow is { IsVisible: true } main ? main : null;
    }

    private void DeleteInfobase()
    {
        // Выбран узел группы: удаляется группа, как в WPF-версии.
        if (SelectedGroupNode?.Group is Group group)
        {
            DeleteGroup(group);
            return;
        }

        var ib = SelectedInfobase;
        if (ib is null)
            return;

        var dialog = new Configuration_Management.DeleteInfobaseWindow(ib);
        if (!dialog.ShowDialogSync(OwnerWindow()) || !dialog.Confirmed)
            return;

        if (dialog.DeletePhysically)
        {
            var error = InfobaseMaintenanceService.TryDeleteFileBasePhysically(ib);
            if (error is not null)
            {
                _dialog.ShowError(error);
                // Даже при ошибке на диске из списка удаляем, если пользователь подтвердит.
                if (!_dialog.Confirm(LocalizationManager.T("Main.ConfirmDeleteFromList")))
                    return;
            }
        }

        _allInfobases.Remove(ib);
        // Состав списка изменился: слоты избранного пересчитываются, иначе
        // номер остаётся у удалённой базы, а её слот занят навсегда.
        SyncFavoriteHotkeys();
        SaveSilently();
        RebuildTree();
        SelectedInfobase = null;
        ExportToIbasesAfterLocalChange();
    }

    /// <summary>
    /// Открывает диалог редактирования конкретной группы (кнопка в колонке
    /// «Действия» строки группы). Сохраняет изменения в существующем объекте,
    /// чтобы иерархия по ParentId и все привязки остались валидными.
    /// </summary>
    private void EditGroup(Group group)
    {
        var dialog = new Configuration_Management.GroupEditWindow(_groups, group.ParentId, group);
        if (!dialog.ShowDialogSync(OwnerWindow()))
            return;

        // Старые полные пути группы и её потомков фиксируются ДО применения изменений:
        // по ним пересчитываются пути баз (issue #171). Иначе после переименования базы
        // остаются на старом пути, уезжают в «Без группы», а сама группа становится
        // пустой и скрывается из дерева.
        var oldPathsById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var subtreeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { group.Id };
        CollectGroupDescendants(group.Id, subtreeIds);
        foreach (var id in subtreeIds)
        {
            var g = _groups.FirstOrDefault(x =>
                string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
            if (g is not null)
                oldPathsById[id] = GroupHierarchyHelper.GetFullPath(g, _groups);
        }
        var oldRootPath = oldPathsById.TryGetValue(group.Id, out var orp) ? orp : string.Empty;

        group.Name = dialog.Result.Name;
        group.Description = dialog.Result.Description;
        group.Color = dialog.Result.Color;
        group.IconColor = dialog.Result.IconColor ?? string.Empty;
        group.Icon = dialog.Result.Icon ?? string.Empty;
        group.ParentId = dialog.Result.ParentId;

        var newRootPath = GroupHierarchyHelper.GetFullPath(group, _groups);

        // Пересчитываем Infobase.Group у всех баз подветки на новый полный путь группы,
        // чтобы группа осталась видимой вместе со своими базами после переименования.
        RemapSubtreeInfobasePaths(oldPathsById, oldRootPath, newRootPath);

        // Записываются оба файла; экспорт идёт только когда удались оба: иначе
        // пути баз и дерево групп разъедутся, и базы уедут в «Без группы».
        var saved = SaveSilently();
        saved &= SaveGroupsSilently();
        if (saved)
            ExportToIbasesAfterLocalChange();
        RebuildTree();

        // Узлы групп пересоздаются при пересборке; восстанавливаем выделение отредактированной
        // группы на новом узле (по идентификатору). Нужно и для правки из кнопки «Действия»
        // строки группы, где SelectedGroupNode мог быть не выставлен до открытия диалога.
        if (!string.IsNullOrEmpty(group.Id)
            && FindNode(n => string.Equals(n.Group?.Id, group.Id, StringComparison.OrdinalIgnoreCase)) is { } editedNode)
            SelectedGroupNode = editedNode;
    }

    /// <summary>
    /// Возвращает группу из параметра команды (кнопка в колонке «Действия» строки группы):
    /// параметром служит либо сам узел группы, либо модель группы.
    /// </summary>
    private static Group? ResolveGroup(object? parameter) =>
        parameter is Group g ? g : (parameter as GroupNodeViewModel)?.Group;

    /// <summary>
    /// Удаляет группу. Группа с подгруппами или базами не удаляется: сначала
    /// её надо освободить, как и в WPF-версии.
    /// </summary>
    private void DeleteGroup(Group group)
    {
        var subgroupCount = _groups.Count(g =>
            string.Equals(g.ParentId, group.Id, StringComparison.OrdinalIgnoreCase));

        var groupPaths = CollectGroupPaths(group.Id);
        var infobaseCount = _allInfobases.Count(ib =>
            !string.IsNullOrWhiteSpace(ib.Group) && groupPaths.Contains(ib.Group.Trim()));

        if (subgroupCount > 0 || infobaseCount > 0)
        {
            var reasons = new List<string>();
            if (subgroupCount > 0)
                reasons.Add(string.Format(LocalizationManager.T("Main.SubgroupsCount"), subgroupCount));
            if (infobaseCount > 0)
                reasons.Add(string.Format(LocalizationManager.T("Main.InfobasesCount"), infobaseCount));

            _dialog.ShowWarning(
                string.Format(LocalizationManager.T("Main.DeleteGroupImpossible"), group.Name) + "\n\n" +
                LocalizationManager.T("Main.DeleteGroupContains") + "\n" +
                string.Join("\n", reasons.Select(r => "• " + r)) + ".\n\n" +
                LocalizationManager.T("Main.DeleteGroupFirstMove"));
            return;
        }

        if (!_dialog.Confirm(string.Format(LocalizationManager.T("Main.DeleteGroupConfirm"), group.Name)))
            return;

        _groups.Remove(group);
        SelectedGroupNode = null;
        SaveGroupsSilently();
        RebuildTree();
    }

    /// <summary>Предупреждение в журнал из окна: журнал живёт во вьюмодели.</summary>
    public void LogWarning(string message) => _logger.Warn(message);

    /// <summary>Группы для окна: путь узла считается по этому же списку.</summary>
    public IReadOnlyList<Group> Groups => _groups;

    /// <summary>
    /// Перемещает базу в указанную группу (полный путь).
    /// <paramref name="insertBefore"/> — база, перед которой вставить (null = в конец группы).
    /// </summary>
    public void MoveInfobaseToGroup(Infobase infobase, string groupFullPath, Infobase? insertBefore = null)
    {
        var targetPath = groupFullPath ?? string.Empty;
        var targetNorm = NormalizeGroupPath(targetPath);
        infobase.Group = string.IsNullOrEmpty(targetNorm) ? targetPath : targetNorm;

        var siblings = _allInfobases
            .Where(i => !ReferenceEquals(i, infobase)
                        && string.Equals(NormalizeGroupPath(i.Group), targetNorm,
                            StringComparison.OrdinalIgnoreCase))
            .OrderBy(i => i.SortOrder)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (insertBefore is not null
            && siblings.Any(s => ReferenceEquals(s, insertBefore)
                                 || string.Equals(s.Id, insertBefore.Id, StringComparison.OrdinalIgnoreCase)
                                    && !string.IsNullOrEmpty(insertBefore.Id)))
        {
            var index = siblings.FindIndex(s =>
                ReferenceEquals(s, insertBefore)
                || (string.Equals(s.Id, insertBefore.Id, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrEmpty(insertBefore.Id)));
            siblings.Insert(Math.Max(0, index), infobase);
        }
        else
        {
            siblings.Add(infobase);
        }

        for (var i = 0; i < siblings.Count; i++)
            siblings[i].SortOrder = (i + 1) * 10;

        // Экспорт только после удачной записи: иначе ibases.v8i получит новую
        // группу, а свой файл останется со старой, и перезапуск их разведёт.
        if (SaveSilently())
            ExportToIbasesAfterLocalChange();
        RebuildTree();

        // Выбор не менялся, а группа базы изменилась: подзаголовок правой панели
        // считается от неё и сам об этом не узнает.
        OnPropertyChanged(nameof(RightPanelSubtitle));
    }

    /// <summary>
    /// Перемещает группу под другую группу (или в корень при пустом newParentId)
    /// вместе со всеми вложенными подгруппами и информационными базами.
    /// Обновляет ParentId и полные пути Infobase.Group у всей подветки.
    /// </summary>
    public void MoveGroupUnder(Group group, string newParentId)
    {
        newParentId ??= string.Empty;
        if (string.Equals(group.Id, newParentId, StringComparison.OrdinalIgnoreCase))
            return;

        // Родитель тот же: сброс группы на её текущего родителя не должен
        // переписывать три файла ради нулевого изменения.
        if (string.Equals(group.ParentId, newParentId, StringComparison.OrdinalIgnoreCase))
            return;

        // Нельзя сделать родителем потомка этой группы (иначе цикл в иерархии).
        if (!string.IsNullOrEmpty(newParentId)
            && GroupHierarchyHelper.IsAncestorOrSelf(newParentId, group.Id, _groups))
            return;

        // Старые полные пути: сама группа + все потомки (до смены ParentId).
        var oldPathsById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var subtreeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { group.Id };
        CollectGroupDescendants(group.Id, subtreeIds);
        foreach (var id in subtreeIds)
        {
            var g = _groups.FirstOrDefault(x =>
                string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
            if (g is not null)
                oldPathsById[id] = GroupHierarchyHelper.GetFullPath(g, _groups);
        }

        var oldRootPath = oldPathsById.TryGetValue(group.Id, out var orp) ? orp : string.Empty;
        var oldRootNorm = NormalizeGroupPath(oldRootPath);

        // Меняем родителя только у перемещаемой группы; вложенные группы
        // остаются её потомками через свои ParentId и переезжают вместе с ней.
        group.ParentId = newParentId;

        var newRootPath = GroupHierarchyHelper.GetFullPath(group, _groups);

        // pathRemap: старый путь (и нормализованный) → новый канонический.
        var pathRemap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(oldRootPath) && !string.IsNullOrEmpty(newRootPath))
        {
            pathRemap[oldRootPath] = newRootPath;
            pathRemap[oldRootNorm] = newRootPath;
        }

        foreach (var id in subtreeIds)
        {
            var g = _groups.FirstOrDefault(x =>
                string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
            if (g is null || !oldPathsById.TryGetValue(id, out var oldPath))
                continue;
            var newPath = GroupHierarchyHelper.GetFullPath(g, _groups);
            if (string.IsNullOrEmpty(oldPath) || string.IsNullOrEmpty(newPath))
                continue;
            pathRemap[oldPath] = newPath;
            pathRemap[NormalizeGroupPath(oldPath)] = newPath;
        }

        if (pathRemap.Count > 0)
        {
            // Длинные пути первыми — чтобы «A / B» не переписывался как префикс «A».
            var remapByLength = pathRemap
                .OrderByDescending(kv => kv.Key.Length)
                .ToList();

            foreach (var ib in _allInfobases)
            {
                var current = ib.Group?.Trim() ?? string.Empty;
                if (string.IsNullOrEmpty(current))
                    continue;

                var currentNorm = NormalizeGroupPath(current);
                if (pathRemap.TryGetValue(current, out var mapped)
                    || pathRemap.TryGetValue(currentNorm, out mapped))
                {
                    ib.Group = mapped;
                    continue;
                }

                // Префикс: база во вложенном пути, которого не было в pathRemap.
                // Путь считается по нормализованному ключу, иначе база не найдёт
                // свой узел при перестройке дерева и уедет в «Без группы».
                foreach (var (oldKey, newKey) in remapByLength)
                {
                    var oldKeyNorm = NormalizeGroupPath(oldKey);
                    if (string.IsNullOrEmpty(oldKeyNorm))
                        continue;
                    var prefix = oldKeyNorm + GroupHierarchyHelper.PathSeparator;
                    if (!currentNorm.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        continue;

                    ib.Group = newKey + currentNorm.Substring(oldKeyNorm.Length);
                    break;
                }

                // Фолбэк на случай расхождений в формате пути: путь пересчитывается
                // по старому корневому пути подветки.
                if (!string.IsNullOrEmpty(oldRootNorm)
                    && !string.IsNullOrEmpty(newRootPath)
                    && (string.Equals(currentNorm, oldRootNorm, StringComparison.OrdinalIgnoreCase)
                        || currentNorm.StartsWith(oldRootNorm + GroupHierarchyHelper.PathSeparator,
                            StringComparison.OrdinalIgnoreCase)))
                {
                    var suffix = currentNorm.Length > oldRootNorm.Length
                        ? currentNorm.Substring(oldRootNorm.Length)
                        : string.Empty;
                    ib.Group = newRootPath + suffix;
                }
            }

            if (_collapsedGroups.Count > 0)
            {
                var updated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var key in _collapsedGroups)
                {
                    if (pathRemap.TryGetValue(key, out var mapped)
                        || pathRemap.TryGetValue(NormalizeGroupPath(key), out mapped))
                        updated.Add(mapped);
                    else if (!string.IsNullOrEmpty(oldRootNorm)
                             && NormalizeGroupPath(key).StartsWith(oldRootNorm + GroupHierarchyHelper.PathSeparator,
                                 StringComparison.OrdinalIgnoreCase)
                             && pathRemap.TryGetValue(oldRootPath, out var newRoot))
                        // Совпадение ищется по нормализованному пути, значит и суффикс
                        // режется от него же: иначе длины разойдутся и ключ станет битым.
                        updated.Add(newRoot + NormalizeGroupPath(key).Substring(oldRootNorm.Length));
                    else
                        updated.Add(key);
                }
                _collapsedGroups.Clear();
                foreach (var k in updated)
                    _collapsedGroups.Add(k);

                // В WPF свёрнутость после переноса остаётся только в памяти:
                // ключи переложены, а настройки не сохраняются.
                PersistCollapsedGroups();
            }
        }

        // Записываются оба файла, экспорт идёт только когда удались оба: иначе
        // пути баз и дерево групп разъедутся, и базы уедут в «Без группы».
        var saved = SaveSilently();
        saved &= SaveGroupsSilently();
        if (saved)
            ExportToIbasesAfterLocalChange();
        RebuildTree();

        // Группа выбранной базы могла измениться вместе с путём подветки,
        // а подзаголовок правой панели считается от базы и об этом не узнает.
        OnPropertyChanged(nameof(RightPanelSubtitle));
    }

    /// <summary>Полные пути группы и всех её потомков: по ним ищутся базы внутри.</summary>
    private HashSet<string> CollectGroupPaths(string groupId)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { groupId };
        CollectGroupDescendants(groupId, ids);

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids)
        {
            var group = _groups.FirstOrDefault(g => string.Equals(g.Id, id, StringComparison.OrdinalIgnoreCase));
            if (group is not null)
                paths.Add(GroupHierarchyHelper.GetFullPath(group, _groups));
        }
        return paths;
    }

    private void CollectGroupDescendants(string parentId, ISet<string> result)
    {
        foreach (var child in _groups.Where(g => string.Equals(g.ParentId, parentId, StringComparison.OrdinalIgnoreCase)))
        {
            if (result.Add(child.Id))
                CollectGroupDescendants(child.Id, result);
        }
    }

    /// <summary>
    /// Пересчитывает полные пути <see cref="Infobase.Group"/> у всех баз подветки группы
    /// после её переименования или перемещения. Старые пути собраны в
    /// <paramref name="oldPathsById"/> ДО применения изменений, а <paramref name="oldRootPath"/>
    /// и <paramref name="newRootPath"/> — пути самой группы до и после.
    /// Предотвращает «исчезновение» группы (issue #171): иначе базы остаются со старым путём,
    /// попадают в «Без группы», а сама группа становится пустой и скрывается из дерева.
    /// </summary>
    private void RemapSubtreeInfobasePaths(
        IReadOnlyDictionary<string, string> oldPathsById,
        string oldRootPath,
        string newRootPath)
    {
        var oldRootNorm = NormalizeGroupPath(oldRootPath);
        var newRootPathNorm = NormalizeGroupPath(newRootPath);

        // pathRemap: старый путь (и нормализованный) → новый канонический.
        var pathRemap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(oldRootPath) && !string.IsNullOrEmpty(newRootPath))
        {
            pathRemap[oldRootPath] = newRootPath;
            pathRemap[oldRootNorm] = newRootPath;
        }

        foreach (var (id, oldPath) in oldPathsById)
        {
            if (string.IsNullOrEmpty(oldPath))
                continue;
            var g = _groups.FirstOrDefault(x =>
                string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
            if (g is null)
                continue;
            var newPath = GroupHierarchyHelper.GetFullPath(g, _groups);
            if (string.IsNullOrEmpty(newPath))
                continue;
            pathRemap[oldPath] = newPath;
            var norm = NormalizeGroupPath(oldPath);
            if (!string.IsNullOrEmpty(norm))
                pathRemap[norm] = newPath;
        }

        if (pathRemap.Count == 0)
            return;

        // Длинные пути первыми — чтобы «A / B» не переписывался как префикс «A».
        var remapByLength = pathRemap
            .OrderByDescending(kv => kv.Key.Length)
            .ToList();

        foreach (var ib in _allInfobases)
        {
            var current = ib.Group?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(current))
                continue;

            var currentNorm = NormalizeGroupPath(current);
            if (pathRemap.TryGetValue(current, out var mapped)
                || pathRemap.TryGetValue(currentNorm, out mapped))
            {
                ib.Group = mapped;
                continue;
            }

            // Префикс: база во вложенном пути, которого не было в pathRemap.
            // Путь считается по нормализованному ключу, иначе база не найдёт
            // свой узел при перестройке дерева и уедет в «Без группы».
            foreach (var (oldKey, newKey) in remapByLength)
            {
                var oldKeyNorm = NormalizeGroupPath(oldKey);
                if (string.IsNullOrEmpty(oldKeyNorm))
                    continue;
                var prefix = oldKeyNorm + GroupHierarchyHelper.PathSeparator;
                if (!currentNorm.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                ib.Group = newKey + currentNorm.Substring(oldKeyNorm.Length);
                break;
            }

            // Фолбэк на случай расхождений в формате пути: путь пересчитывается
            // по старому корневому пути подветки.
            if (!string.IsNullOrEmpty(oldRootNorm)
                && !string.IsNullOrEmpty(newRootPathNorm)
                && (string.Equals(currentNorm, oldRootNorm, StringComparison.OrdinalIgnoreCase)
                    || currentNorm.StartsWith(oldRootNorm + GroupHierarchyHelper.PathSeparator,
                        StringComparison.OrdinalIgnoreCase)))
            {
                var suffix = currentNorm.Length > oldRootNorm.Length
                    ? currentNorm.Substring(oldRootNorm.Length)
                    : string.Empty;
                ib.Group = newRootPathNorm + suffix;
            }
        }

        if (_collapsedGroups.Count > 0)
        {
            var updated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in _collapsedGroups)
            {
                if (pathRemap.TryGetValue(key, out var mapped)
                    || pathRemap.TryGetValue(NormalizeGroupPath(key), out mapped))
                    updated.Add(mapped);
                else if (!string.IsNullOrEmpty(oldRootNorm)
                         && NormalizeGroupPath(key).StartsWith(oldRootNorm + GroupHierarchyHelper.PathSeparator,
                             StringComparison.OrdinalIgnoreCase)
                         && pathRemap.TryGetValue(oldRootPath, out var newRoot))
                    updated.Add(newRoot + NormalizeGroupPath(key).Substring(oldRootNorm.Length));
                else
                    updated.Add(key);
            }
            _collapsedGroups.Clear();
            foreach (var k in updated)
                _collapsedGroups.Add(k);
            PersistCollapsedGroups();
        }
    }

    /// <summary>
    /// Открывает диалог ввода ссылки на информационную базу и запускает её,
    /// как «Перейти по ссылке» в стандартном загрузчике 1С.
    /// </summary>
    private void OpenInfobaseByLink()
    {
        var dialog = new Configuration_Management.LinkInputWindow();
        if (!dialog.ShowDialogSync(OwnerWindow()) || string.IsNullOrWhiteSpace(dialog.Result))
            return;

        var link = dialog.Result;
        _logger.Info($"Запуск 1С по ссылке: {link}");
        if (!OneCLauncher.LaunchByLink(link))
        {
            _dialog.ShowError(string.Format(LocalizationManager.T("Main.ErrOpenLink"), link));
            return;
        }

        NotifyAfterLaunch();
    }

    private void ToggleFavorite() => ToggleFavoriteFor(SelectedInfobase);

    private void TogglePin() => TogglePinFor(SelectedInfobase);

    private void DumpInfobaseDt(object? parameter) =>
        DumpViaDesigner(parameter, OneCLauncher.DesignerBatchOperation.DumpIB, ".dt",
            "Main.DumpDtDialogTitle", "Main.DtFileFilter", "Main.DumpDtStarted", "Main.DumpDtTitle");

    private void DumpConfigurationCf(object? parameter) =>
        DumpViaDesigner(parameter, OneCLauncher.DesignerBatchOperation.DumpCfg, ".cf",
            "Main.DumpCfDialogTitle", "Main.CfFileFilter", "Main.DumpCfStarted", "Main.DumpCfTitle");

    /// <summary>
    /// Выгрузка базы или конфигурации пакетным запуском конфигуратора.
    /// Общая часть обеих команд: в версии для Windows это два почти одинаковых метода.
    /// </summary>
    private void DumpViaDesigner(
        object? parameter,
        OneCLauncher.DesignerBatchOperation operation,
        string extension,
        string dialogTitleKey,
        string filterKey,
        string startedKey,
        string titleKey)
    {
        var ib = parameter as Infobase ?? SelectedInfobase;
        if (ib is null)
            return;

        var path = _dialog.SaveFileDialog(
            LocalizationManager.T(dialogTitleKey),
            BuildExportFileName(SanitizeFileName(ib.Name), extension,
                _settings.AddTimestampToExportFileName, _settings.ExportTimestampFormat),
            LocalizationManager.T(filterKey));
        if (string.IsNullOrWhiteSpace(path))
            return;

        if (!OneCLauncher.RunDesignerBatch(ib, operation, path))
            return;

        ib.AddLaunchHistory(operation == OneCLauncher.DesignerBatchOperation.DumpIB ? "DumpDT" : "DumpCF", path);
        SaveSilently();
        _dialog.ShowInfo(LocalizationManager.T(startedKey), LocalizationManager.T(titleKey));
    }

    private void RefreshConfigurationInfo(object? parameter)
    {
        var ib = parameter as Infobase ?? SelectedInfobase;
        if (ib is null)
            return;

        // Сброса вердиктов о недоступности COM здесь нет: он живёт
        // в OneCComConnector.cs, а тот в Linux-сборку не входит.
        var baseName = ib.Name;
        _ = Task.Run(() =>
        {
            OneCConfigInfo? info = null;
            try
            {
                info = ConfigurationInfoService.ReadAndApply(ib, overwriteExisting: true);
            }
            catch
            {
                // причина уходит в LastComError и показывается ниже
            }

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (info is null)
                {
                    var comError = ConfigurationInfoService.LastComError;
                    var detail = string.IsNullOrWhiteSpace(comError)
                        ? LocalizationManager.T("Main.ConfigInfoCheckHint")
                        : string.Format(LocalizationManager.T("Main.ConfigInfoReason"), comError);
                    _logger.Warn($"Не удалось получить информацию о конфигурации базы «{baseName}». {detail}");
                    _dialog.ShowWarning(
                        string.Format(LocalizationManager.T("Main.ErrConfigInfo"), baseName, detail),
                        LocalizationManager.T("Main.ConfigInfoTitle"));
                    return;
                }

                RebuildTree();
                SaveSilently();

                var name = info.Value.Name.Trim();
                var version = info.Value.Version.Trim();
                _logger.Info($"Обновлена информация о конфигурации базы «{baseName}»: {name} ({version})");

                var sb = new System.Text.StringBuilder();
                sb.AppendLine(string.Format(LocalizationManager.T("Main.ConfigInfoBase"), baseName));
                if (name.Length > 0)
                    sb.AppendLine(string.Format(LocalizationManager.T("Main.ConfigInfoName"), name));
                if (version.Length > 0)
                    sb.AppendLine(string.Format(LocalizationManager.T("Main.ConfigInfoVersion"), version));
                _dialog.ShowInfo(sb.ToString().TrimEnd(), LocalizationManager.T("Main.ConfigInfoTitle"));
            });
        });
    }

    private static string SanitizeFileName(string? name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var s = new string((name ?? "base").Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(s) ? "base" : s;
    }

    private void ToggleFavoriteFor(Infobase? infobase)
    {
        if (infobase is null)
            return;

        infobase.IsFavorite = !infobase.IsFavorite;

        // Снятая звезда освобождает слот явно: пересчёт сам этого не сделает,
        // он выбрасывает только ключи баз, которых больше нет в списке.
        // Тот же порядок в версии для Windows (MainViewModel.Commands.cs:460).
        if (!infobase.IsFavorite)
            _favoriteHotkeyIds.Remove(FavoriteKey(infobase));

        SyncFavoriteHotkeys();
        SaveSilently();
        // Слоты живут в настройках, а не в списке баз, поэтому сохраняются
        // отдельно: SaveSilently пишет только список.
        SaveSettingsSilently();

        // Состав списка меняется только при активном временном фильтре: там база
        // может выпасть из выборки. Без фильтра строка перекрашивается сама
        // по уведомлению модели, и пересобирать дерево незачем.
        if (IsFilterModeActive())
            ApplyFilter();
    }


    // ======================= Горячие клавиши избранного =======================

    /// <summary>Слоты Alt+1…Alt+9 в порядке назначения: ключи баз.</summary>
    private readonly List<string> _favoriteHotkeyIds = new();

    /// <summary>
    /// Ключ базы для слота. Идентификатор, а при его отсутствии имя: тот же
    /// ключ, что и в версии для Windows, поэтому список слотов переносится
    /// между платформами через общий файл настроек.
    /// </summary>
    private static string FavoriteKey(Infobase ib) =>
        !string.IsNullOrEmpty(ib.Id) ? ib.Id : "name:" + ib.Name;

    /// <summary>Упорядоченный список ключей слотов, для окна настроек.</summary>
    public IReadOnlyList<string> FavoriteHotkeyIds => _favoriteHotkeyIds;

    /// <summary>Возвращает базу по ключу слота.</summary>
    public Infobase? FindByFavoriteKey(string key) =>
        _allInfobases.FirstOrDefault(ib => FavoriteKey(ib) == key);

    /// <summary>
    /// Раздаёт слоты избранным базам и проставляет номера на самих базах.
    /// Список хранится в настройках, поэтому здесь же переписывается в них:
    /// любое последующее сохранение унесёт его на диск.
    /// </summary>
    private void SyncFavoriteHotkeys()
    {
        try
        {
            // Слот должен соответствовать только текущим избранным базам, иначе
            // вкладка, счётчик и список горячих клавиш разъезжаются (issue #194).
            _favoriteHotkeyIds.RemoveAll(key =>
                !_allInfobases.Any(ib => ib.IsFavorite && FavoriteKey(ib) == key));

            // Избранные без слота получают его в порядке имени, как в версии
            // для Windows. Слотов девять, лишние остаются без номера.
            foreach (var ib in _allInfobases
                         .Where(i => i.IsFavorite)
                         .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
            {
                var key = FavoriteKey(ib);
                if (!_favoriteHotkeyIds.Contains(key) && _favoriteHotkeyIds.Count < 9)
                    _favoriteHotkeyIds.Add(key);
            }

            foreach (var ib in _allInfobases)
            {
                var idx = _favoriteHotkeyIds.IndexOf(FavoriteKey(ib));
                ib.FavoriteHotkeyNumber = idx >= 0 ? idx + 1 : 0;
            }

            _settings.FavoriteHotkeyIds = _favoriteHotkeyIds.ToList();
        }
        catch (Exception ex)
        {
            // Избранное не повод ронять окно: без слотов список просто
            // остаётся без номеров.
            _logger.Error("Не удалось разложить слоты избранного", ex);
        }
    }

    /// <summary>Заменяет порядок слотов, заданный в окне настроек.</summary>
    public void SetFavoriteHotkeyOrder(IEnumerable<string> orderedKeys)
    {
        _favoriteHotkeyIds.Clear();
        foreach (var key in orderedKeys.Where(k => !string.IsNullOrWhiteSpace(k)).Take(9))
            _favoriteHotkeyIds.Add(key);
        SyncFavoriteHotkeys();
        SaveSettingsSilently();
    }

    /// <summary>
    /// Запускает Предприятие для базы из слота. Запуск идёт обычным путём окна,
    /// а не прямым вызовом лаунчера, как в версии для Windows: так работают
    /// и разовые параметры, и действие после запуска.
    /// </summary>
    public void LaunchFavoriteByHotkey(int number)
    {
        if (number < 1 || number > _favoriteHotkeyIds.Count)
            return;

        var ib = FindByFavoriteKey(_favoriteHotkeyIds[number - 1]);
        if (ib is null)
            return;

        SelectedInfobase = ib;
        _logger.Info($"Запуск избранной базы «{ib.Name}» по Alt+{number}");
        Launch(_launchVm.LaunchCommand, LaunchKind.Enterprise);
    }

    private void TogglePinFor(Infobase? infobase)
    {
        if (infobase is null)
            return;

        infobase.IsPinned = !infobase.IsPinned;
        SaveSilently();
        UpdatePinnedSection(infobase);
    }

    /// <summary>
    /// Точечно обновляет узел «Закреплённые», как это делает WPF-версия
    /// (ApplyPinToggle / UpdatePinnedSection): полная пересборка дерева здесь
    /// стоила бы пересоздания всех строк и потери выделения. Узел правится
    /// всегда, а в видимый список попадает только когда он там уместен:
    /// AllGroupNodes переживает и фильтр, и отключение группировки, и после
    /// возврата к группам список берётся именно оттуда.
    /// </summary>
    private void UpdatePinnedSection(Infobase infobase)
    {
        var pinnedVisible = _groupByGroup && !IsFilterModeActive();
        var pinned = AllGroupNodes.FirstOrDefault(node => node.Group is null
            && string.Equals(node.Marker, GroupNodeViewModel.PinnedMarker, StringComparison.Ordinal));

        // Закрепление меняет и порядок внутри своей группы: закреплённые идут
        // первыми (GroupSortOrder). Без этого строка осталась бы на прежнем
        // месте до следующей полной пересборки.
        SortOwningNode(infobase);

        if (infobase.IsPinned)
        {
            if (pinned is null)
            {
                pinned = new GroupNodeViewModel(null, marker: GroupNodeViewModel.PinnedMarker);
                pinned.Infobases.Add(infobase);
                pinned.PopulateItems(_showEmptyGroups);
                ApplyExpandedState(pinned);
                SubscribeExpandedTracking(pinned);
                AllGroupNodes.Insert(0, pinned);
                if (pinnedVisible)
                    GroupNodes.Insert(0, pinned);
                return;
            }

            if (!pinned.Infobases.Contains(infobase))
            {
                pinned.Infobases.Add(infobase);
                SortNodeInfobases(pinned);
                pinned.PopulateItems(_showEmptyGroups);
            }
            else
            {
                pinned.NotifyCountChanged();
            }

            return;
        }

        if (pinned is null)
            return;

        pinned.Infobases.Remove(infobase);
        if (pinned.Infobases.Count > 0)
        {
            pinned.PopulateItems(_showEmptyGroups);
            return;
        }

        AllGroupNodes.Remove(pinned);
        GroupNodes.Remove(pinned);
    }

    /// <summary>
    /// Переупорядочивает узел, в котором лежит база, кроме служебного узла
    /// «Закреплённые»: его состав меняется отдельно.
    /// </summary>
    private void SortOwningNode(Infobase infobase)
    {
        foreach (var root in AllGroupNodes)
        {
            var owner = FindNodeWith(root, infobase);
            if (owner is null
                || string.Equals(owner.Marker, GroupNodeViewModel.PinnedMarker, StringComparison.Ordinal))
                continue;

            SortNodeInfobases(owner);
            owner.PopulateItems(_showEmptyGroups);
            return;
        }
    }

    /// <summary>Ищет узел дерева, в списке баз которого лежит указанная база.</summary>
    private static GroupNodeViewModel? FindNodeWith(GroupNodeViewModel node, Infobase infobase)
    {
        if (node.Infobases.Contains(infobase))
            return node;

        foreach (var child in node.Children)
        {
            var found = FindNodeWith(child, infobase);
            if (found is not null)
                return found;
        }

        return null;
    }

    /// <summary>Переупорядочивает базы узла по текущему полю сортировки.</summary>
    private void SortNodeInfobases(GroupNodeViewModel node)
    {
        var sorted = ApplyCurrentSort(node.Infobases).ToList();
        node.SetNotificationsSuppressed(true);
        try
        {
            node.Infobases.Clear();
            foreach (var infobase in sorted)
                node.Infobases.Add(infobase);
        }
        finally
        {
            node.SetNotificationsSuppressed(false);
        }
    }

    private void CopyConnectionString()
    {
        if (SelectedInfobase is not Infobase ib)
            return;

        var text = ib.ConnectionStringDisplay;
        try
        {
            var lifetime = Avalonia.Application.Current?.ApplicationLifetime
                as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
            var clipboard = lifetime?.MainWindow?.Clipboard;
            if (clipboard is null)
            {
                _logger.Warn("Буфер обмена недоступен (нет активного окна).");
                return;
            }
            _ = clipboard.SetTextAsync(text);
            _logger.Info($"Скопирована строка подключения базы «{ib.Name}»");
        }
        catch (Exception ex)
        {
            _logger.Warn($"Не удалось скопировать строку подключения: {ex.Message}");
        }
    }

    private void OpenSettings()
    {
        // Построение окна настроек — большая процедура (восемь вкладок, иконки, темы).
        // На Linux сбой в ней завершал процесс abort-ом (issue #168); ловим и логируем,
        // чтобы приложение продолжало работать, а ошибка попала в журнал.
        Configuration_Management.SettingsWindow settings;
        try
        {
            settings = new Configuration_Management.SettingsWindow(this);
        }
        catch (Exception ex)
        {
            _logger.Error($"Не удалось открыть окно настроек: {ex.Message}", ex);
            return;
        }

        // Владелец берётся только видимый: окно, спрятанное в трей, остаётся
        // в списке окон приложения, и показ поверх него ничего не показывает.
        // Настройки открываются из меню трея именно в таком состоянии.
        if (OwnerWindow() is { } owner)
            settings.ShowDialog(owner);
        else
            settings.Show();
    }

    /// <summary>
    /// Применяет компактный режим к главному окну (пересобирает UI с уменьшенными
    /// метриками). Вызывается из окна настроек при переключении переключателя.
    /// </summary>
    public void ApplyCompactMode(bool compact)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
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
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow is Configuration_Management.MainWindow main)
            main.ApplySystemTitleBar(useSystemTitleBar);
    }

    /// <summary>
    /// Проверяет доступность всех баз 1С и помечает недоступные красным крестиком
    /// в списке баз. Ручная команда верхней панели команд вместо автопроверки при
    /// запуске: старт не блокируется запросами ко всем базам. Доступность
    /// определяется по факту: для файловых баз — наличие каталога/файла базы по
    /// пути, для клиент-серверных — реальная попытка подключения, для веб-баз —
    /// заполненность адреса. Проверки выполняются в фоне, чтобы не блокировать UI.
    /// </summary>
    private void CheckAvailability()
    {
        var targets = Infobases.ToList();
        if (targets.Count == 0)
        {
            ShowTemporaryStatusMessage(LocalizationManager.T("Main.ConfigListEmpty"));
            return;
        }

        IsLoading = true;
        LoadingMessage = LocalizationManager.T("Main.CheckAvailabilityLabel");
        _ = Task.Run(() =>
        {
            // Результаты собираем заранее, чтобы не трогать модель из фонового потока.
            var results = new List<(Infobase Base, bool Available)>(targets.Count);
            foreach (var ib in targets)
                results.Add((ib, IsBaseAvailable(ib)));

            Dispatcher.UIThread.Post(() =>
            {
                IsLoading = false;
                foreach (var (ib, available) in results)
                    ib.SetCheckedAvailability(available);
                RebuildTree();

                var total = results.Count;
                var unavailable = results.Count(r => !r.Available);
                ShowTemporaryStatusMessage(string.Format(
                    LocalizationManager.T("Main.AvailabilityStatus"), total, unavailable));
            });
        });
    }

    /// <summary>
    /// Показывает сообщение в нижней строке состояния на 10 секунд, после чего
    /// возвращает обычный текст. Повторный вызов сбрасывает предыдущий таймер.
    /// </summary>
    private void ShowTemporaryStatusMessage(string message)
    {
        _statusMessageCts?.Cancel();
        _statusMessageCts?.Dispose();
        _statusMessageCts = null;

        StatusBarInfo = message;

        var cts = new System.Threading.CancellationTokenSource();
        _statusMessageCts = cts;
        var token = cts.Token;
        _ = ClearStatusMessageAfterDelayAsync(token);
    }

    private async Task ClearStatusMessageAfterDelayAsync(System.Threading.CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), token).ConfigureAwait(true);
            if (!token.IsCancellationRequested)
                UpdateStatus();
        }
        catch (TaskCanceledException)
        {
            // новое сообщение заменило предыдущее
        }
    }

    /// <summary>
    /// Доступность отдельной базы. Файловая — есть ли каталог/файл по пути;
    /// клиент-серверная — удалось ли подключиться; веб-база — заполнен ли адрес.
    /// Для клиент-серверных баз выполняется реальная попытка подключения через
    /// COM-коннектор (на Linux недоступна, поэтому такие базы считаются недоступными).
    /// </summary>
    private static bool IsBaseAvailable(Infobase ib)
    {
        try
        {
            switch (ib.Connection?.Type)
            {
                case ConnectionType.File:
                    // Наличие каталога или файла базы по пути.
                    return InfobaseMaintenanceService.FileBaseExists(ib);

                case ConnectionType.ClientServer:
                {
                    // На Linux COM-коннектор отсутствует (Connect возвращает null), поэтому
                    // проверить доступность клиент-серверной базы по сети нельзя. Считать её
                    // полным DumpCfg конфигуратора для каждой базы слишком дорого — не пробуем,
                    // база считается недоступной (как и документировано выше).
                    return false;
                }

                case ConnectionType.WebServer:
                    return !string.IsNullOrWhiteSpace(ib.Connection.WebUrl);

                default:
                    return false;
            }
        }
        catch
        {
            return false;
        }
    }

    // ======================= Этап 6: папки / ярлыки / стартер =======================

    /// <summary>
    /// Недавно запускавшиеся базы (для меню трея). До семи по дате запуска,
    /// как в Windows-версии (MainWindow.Tray.cs:216 запрашивает семь).
    /// </summary>
    public List<Infobase> RecentInfobases =>
        _allInfobases
            .Where(ib => ib.LastLaunchDate.HasValue)
            .OrderByDescending(ib => ib.LastLaunchDate)
            .Take(7)
            .ToList();

    /// <summary>Все информационные базы (для диалога выбора при очистке кеша).</summary>
    public IReadOnlyList<Infobase> Infobases => _allInfobases;

    /// <summary>Открыть каталог файловой базы в файловом менеджере рабочего стола.</summary>
    private void OpenInfobaseFolder()
    {
        var ib = SelectedInfobase;
        if (ib is null)
            return;
        if (!InfobaseMaintenanceService.OpenInfobaseFolder(ib))
            _dialog.ShowError(LocalizationManager.T("Main.ErrOpenBaseFolder"));
    }

    /// <summary>Создать ярлык .desktop на рабочем столе для запуска базы.</summary>
    private void CreateDesktopShortcut()
    {
        var ib = SelectedInfobase;
        if (ib is null)
            return;
        if (InfobaseMaintenanceService.CreateDesktopShortcut(ib))
            _dialog.ShowInfo(string.Format(LocalizationManager.T("Main.ShortcutCreated"), ib.Name));
        else
            _dialog.ShowError(string.Format(LocalizationManager.T("Main.ErrShortcutCreate"), ib.Name));
    }

    /// <summary>Запустить родной стартер 1С (1cestart).</summary>
    private void OpenNativeStarter()
    {
        if (!InfobaseMaintenanceService.OpenNativeStarter())
            _dialog.ShowError(LocalizationManager.T("Main.ErrStartStarter"));
    }

    /// <summary>
    /// Перезапускает автоматическую синхронизацию по настройкам. При выключенной
    /// синхронизации и при режиме «при старте» таймер не нужен.
    /// </summary>
    private void RestartAutoSync()
    {
        StopAutoSync();

        if (_settings.IbasesSyncMode == IbasesSyncMode.None
            || _settings.IbasesSyncTrigger == IbasesSyncTrigger.OnStartup)
            return;

        if (!ComputeNextRunTime(out var nextRun))
            return;

        _nextScheduleRun = nextRun;
        _syncTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _syncTimer.Tick += OnSyncTimerTick;
        _syncTimer.Start();
        _logger.Info($"Автосинхронизация с ibases.v8i включена, следующий запуск {nextRun:HH:mm}");
    }

    private void StopAutoSync()
    {
        if (_syncTimer is null)
            return;

        _syncTimer.Stop();
        _syncTimer.Tick -= OnSyncTimerTick;
        _syncTimer = null;
        _nextScheduleRun = null;
    }

    private void OnSyncTimerTick(object? sender, EventArgs e)
    {
        if (_settings.IbasesSyncMode == IbasesSyncMode.None)
        {
            RestartAutoSync();
            return;
        }

        if (_nextScheduleRun is null)
        {
            if (ComputeNextRunTime(out var next))
                _nextScheduleRun = next;
            return;
        }

        if (DateTime.Now < _nextScheduleRun.Value)
            return;

        SynchronizeSilently();
        if (ComputeNextRunTime(out var following))
            _nextScheduleRun = following;
    }

    /// <summary>
    /// Время следующего запуска: для интервала это «сейчас плюс интервал»,
    /// для расписания ближайшее заданное время, завтра если сегодня прошло.
    /// </summary>
    private bool ComputeNextRunTime(out DateTime nextRun)
    {
        nextRun = default;

        if (_settings.IbasesSyncTrigger == IbasesSyncTrigger.Interval)
        {
            nextRun = DateTime.Now.AddMinutes(Math.Max(1, _settings.IbasesSyncIntervalMinutes));
            return true;
        }

        if (_settings.IbasesSyncTrigger == IbasesSyncTrigger.Schedule)
        {
            // Строгий разбор времени суток: обычный TryParse принимает и локальные
            // форматы, и длительности вроде 25:00, и «9» как девять суток.
            // Час допускается с ведущим нулём и без: поле свободного ввода,
            // и «9:00» пользователь набирает не реже, чем «09:00», а раньше
            // такое расписание молча не запускалось вовсе.
            if (!TimeSpan.TryParseExact(_settings.IbasesSyncScheduleTime?.Trim(),
                    new[] { @"hh\:mm", @"h\:mm" },
                    System.Globalization.CultureInfo.InvariantCulture, out var time)
                || time < TimeSpan.Zero || time >= TimeSpan.FromDays(1))
            {
                _logger.Warn($"Автосинхронизация выключена: время расписания «{_settings.IbasesSyncScheduleTime}» не распознано, ожидается ЧЧ:ММ");
                return false;
            }

            var now = DateTime.Now;
            var run = now.Date + time;
            if (run <= now)
                run = run.AddDays(1);

            nextRun = run;
            return true;
        }

        return false;
    }

    /// <summary>Путь к файлу списка баз: заданный в настройках или стандартный.</summary>
    private string? ResolveIbasesFilePath() =>
        string.IsNullOrWhiteSpace(_settings.IbasesSyncFilePath)
            ? IbasesV8iImporter.FindDefaultPath()
            : _settings.IbasesSyncFilePath;

    /// <summary>
    /// Синхронизация без диалогов: для запуска по расписанию и при старте.
    /// В двустороннем режиме сначала выгрузка, затем загрузка, как в WPF-версии.
    /// </summary>
    private void SynchronizeSilently()
    {
        if (_settings.IbasesSyncMode == IbasesSyncMode.None)
            return;

        var filePath = ResolveIbasesFilePath();
        if (filePath is null)
        {
            _logger.Warn("Автосинхронизация: файл ibases.v8i не найден");
            return;
        }

        var done = false;

        // Выгрузка и загрузка разделены: отказ одной не должен отменять другую,
        // как это устроено в WPF-версии.
        if (_settings.IbasesSyncMode is IbasesSyncMode.Export or IbasesSyncMode.Both)
            done |= ExportToIbases(filePath);

        if (_settings.IbasesSyncMode is IbasesSyncMode.Import or IbasesSyncMode.Both)
            done |= ImportFromIbases(filePath);

        SyncMessage = done
            ? LocalizationManager.T("Sync.Completed")
            : LocalizationManager.T("Sync.Failed");
    }

    /// <summary>Выгрузка списка баз в файл платформы с резервной копией.</summary>
    private bool ExportToIbases(string filePath)
        => TryExportToIbases(filePath, backup: true, "Автосинхронизация: выгрузка", out _);

    /// <summary>
    /// Выгрузка с текстом ошибки и своей записью в журнал: по ней отличают
    /// автоматическую синхронизацию от нажатой кнопки, а текст ошибки нужен
    /// только ручной операции.
    /// </summary>
    /// <param name="backup">
    /// Создавать ли резервную копию файла. Создают обе выгрузки, и ручная тоже,
    /// хотя в версии для Windows ручная копию не делает. Причина в том, что
    /// выгрузка переписывает файл целиком: при пустом списке баз приложения
    /// она обнуляет ibases.v8i, и без копии это уже не отменить. Копия хранит
    /// состояние до выгрузки, то есть соседняя кнопка восстановления вернёт
    /// именно то, что было до ошибочного нажатия.
    /// </param>
    private bool TryExportToIbases(string filePath, bool backup, string logPrefix, out string error)
    {
        error = string.Empty;
        try
        {
            if (backup && _settings.IbasesBackupEnabled && System.IO.File.Exists(filePath))
            {
                try { IbasesBackupService.CreateBackup(filePath, _settings.IbasesBackupKeepCount); }
                catch (Exception ex) { _logger.Error("Не удалось создать резервную копию ibases.v8i", ex); }
            }

            _sync.Export(filePath, _allInfobases, _groups);
            _logger.Info($"{logPrefix} в {filePath}");
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _logger.Error("Ошибка выгрузки в ibases.v8i", ex);
            return false;
        }
    }

    /// <summary>
    /// Загрузка списка баз из файла платформы. Импорт удаляет из приложения базы,
    /// которых нет в файле, поэтому отсутствующий и подозрительно пустой файл
    /// пропускаются: иначе повреждённый файл вычистил бы список без спроса.
    /// </summary>
    private bool ImportFromIbases(string filePath)
    {
        if (!System.IO.File.Exists(filePath))
        {
            _logger.Warn($"Автосинхронизация: файла {filePath} нет, загрузка пропущена");
            return false;
        }

        var before = _allInfobases.Count;

        try
        {
            var result = _sync.Import(filePath, _allInfobases, _groups);

            if (before > 0 && _allInfobases.Count == 0)
            {
                _logger.Warn($"Автосинхронизация: файл {filePath} не дал ни одной базы, список приложения не меняем");
                _allInfobases = _repository.Load();
                // Состав списка изменился: слоты избранного пересчитываются, иначе
                // номер остаётся у удалённой базы, а её слот занят навсегда.
                SyncFavoriteHotkeys();
                RebuildTree();
                return false;
            }

            SaveSilently();
            // Импорт создаёт недостающие группы, и без этого они пропадали
            // при следующем запуске: сохраняется только список баз.
            SaveGroupsSilently();
            RebuildTree();

            // Выбранная база могла быть удалена импортом.
            if (SelectedInfobase is { } selected && !_allInfobases.Contains(selected))
                SelectedInfobase = null;

            _logger.Info($"Автосинхронизация: загрузка из {filePath}, добавлено {result.Added}, обновлено {result.Updated}, удалено {result.Removed}");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error("Ошибка загрузки из ibases.v8i", ex);
            return false;
        }
    }

    private void SynchronizeWithIbases()
    {
        var path = _dialog.OpenFileDialog(
            LocalizationManager.T("Sync.ChooseIbasesFile"),
            LocalizationManager.T("Sync.IbasesFilter"));
        if (string.IsNullOrWhiteSpace(path))
            return;

        // Тот же путь, что и у кнопки на вкладке «Базы»: раньше здесь терялись
        // созданные импортом группы и оставался выбор на удалённой базе.
        ImportFromIbasesFileInteractive(path);
        SyncMessage = LocalizationManager.T("Sync.Completed");
    }

    private void ToggleTheme()
    {
        // Схема одна (несёт обе палитры): переключение темы лишь выбирает палитру.
        ApplySchemeForTheme(ThemeManager.CurrentTheme != ThemeManager.DarkThemeName);
    }

    /// <summary>Применяет выбранный вариант темы (светлую/тёмную), не меняя схему.</summary>
    public void ApplyTheme(string theme)
    {
        ApplySchemeForTheme(theme == ThemeManager.DarkThemeName);
    }

    /// <summary>
    /// Возвращает активную схему (с двумя палитрами). Вариант темы определяет,
    /// какая палитра показывается; сама схема от него не зависит.
    /// </summary>
    public Models.ColorScheme GetSchemeForTheme(string theme)
        => ActiveColorScheme.Clone();

    /// <summary>
    /// Задаёт вариант темы (светлую/тёмную) и применяет активную схему с палитрой
    /// этого варианта.
    /// </summary>
    private void ApplySchemeForTheme(bool dark)
    {
        ThemeManager.ApplyTheme(dark);
        // Общий цвет папок берётся из активной палитры схемы при построении дерева.
        RebuildTree();
        _settings.Theme = dark ? ThemeManager.DarkThemeName : ThemeManager.LightThemeName;
        _themeName = _settings.Theme;
        SaveSettingsSilently();
        OnPropertyChanged(nameof(ThemeName));
    }

    private static bool IsDarkTheme(string? theme)
        => string.Equals(theme, ThemeManager.DarkThemeName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Применяет выбранный язык интерфейса и сохраняет его в настройках.
    /// Локализация применяется сразу (обновляются окна с привязками Loc) и
    /// восстанавливается при следующем запуске.
    /// </summary>
    /// <param name="code">Код языка, например "ru", "en" или загруженного внешнего.</param>
    public void ApplyLanguage(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return;

        Console.Error.WriteLine("[l10n-debug] MainViewModel.ApplyLanguage(" + code + ")");
        _settings.Language = code;
        SaveSettingsSilently();

        try
        {
            Configuration_Management.Localization.LocalizationManager.Instance.SetLanguage(code);
        }
        catch (Exception ex)
        {
            _logger.Error("Не удалось применить язык интерфейса", ex);
        }

        // Строка состояния собрана из локализованных частей и хранится готовой,
        // поэтому её надо пересобрать: сама она на смену языка не откликается.
        UpdateStatus();
    }

    /// <summary>
    /// Сохраняет текущие настройки (включая язык интерфейса) на диск.
    /// Вызывается при закрытии окна в трей и при полном выходе, чтобы
    /// выбранный язык не терялся между запусками.
    /// </summary>
    public void PersistSettings()
    {
        _settings.Language = Configuration_Management.Localization.LocalizationManager.Instance.CurrentLanguage;
        Console.Error.WriteLine("[l10n-debug] PersistSettings language=" + _settings.Language);
        SaveSettingsSilently();
    }

    private void ExitApplication()
    {
        // Гарантируем сохранение выбранного языка при завершении программы:
        // если язык определился автоматически (по системе) и не сохранялся через
        // ApplyLanguage, записываем текущий код, чтобы он не потерялся между запусками.
        PersistSettings();
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }

    // ======================= Сохранение =======================

    /// <summary>Сохраняет список баз, возвращая признак успеха: ошибка идёт в журнал.</summary>
    private bool SaveSilently() => SaveList(_allInfobases);

    /// <summary>Сохраняет настройки и сообщает, удалось ли.</summary>
    private bool SaveSettingsSafe()
    {
        try { _repository.SaveSettings(_settings); return true; }
        catch (Exception ex) { _logger.Error("Не удалось сохранить настройки", ex); return false; }
    }

    private void SaveSettingsSilently()
    {
        try { _repository.SaveSettings(_settings); }
        catch (Exception ex) { _logger.Error("Не удалось сохранить настройки", ex); }
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

    private void RaiseCommandCanExecuteChanged()
    {
        (LaunchEnterpriseCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (LaunchConfiguratorCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (EditInfobaseCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (DeleteInfobaseCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ToggleFavoriteCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (TogglePinCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (CopyConnectionStringCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (OpenInfobaseFolderCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (QuickClearCacheCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ClearCacheCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ClearProgramCacheCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ClearUserCacheCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ClearCacheBothCommand as RelayCommand)?.RaiseCanExecuteChanged();
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
        OnPropertyChanged(nameof(ShowTags));
        OnPropertyChanged(nameof(NameColumnWidth));
        OnPropertyChanged(nameof(VersionColumnWidth));
        OnPropertyChanged(nameof(ConfigurationColumnWidth));
        OnPropertyChanged(nameof(ConfigurationVersionColumnWidth));
        OnPropertyChanged(nameof(LaunchModeColumnWidth));
        OnPropertyChanged(nameof(ServerColumnWidth));
        OnPropertyChanged(nameof(LastLaunchColumnWidth));
        OnPropertyChanged(nameof(SizeColumnWidth));
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

    // ======================= Очистка кеша 1С =======================

    /// <summary>
    /// Быстрая очистка всего кеша (программного и пользовательского) выбранной базы.
    /// Перед очисткой запрашивает подтверждение у пользователя.
    /// </summary>
    /// <param name="parameter">Информационная база (или null — используется выбранная).</param>
    private void QuickClearCache(object? parameter)
    {
        var ib = parameter as Infobase ?? SelectedInfobase;
        if (ib is null)
            return;

        if (!_dialog.Confirm(
            string.Format(LocalizationManager.T("Main.CacheClearAllConfirm"), ib.Name),
            LocalizationManager.T("Main.ClearCacheDlgTitle")))
            return;

        try
        {
            // Объём кэша базы до очистки — для отчёта (issue #178).
            var size = OneCCacheCleaner.GetSize(ib, OneCCacheKind.All);
            var removed = OneCCacheCleaner.Clear(ib, OneCCacheKind.All);
            var kindLabel = CacheKindLabel(OneCCacheKind.All);
            var baseLabel = string.Format(LocalizationManager.T("Main.CacheBaseOne"), ib.Name);
            var message = removed > 0
                ? string.Format(LocalizationManager.T("Main.CacheCleaned"), kindLabel, baseLabel, removed, Infobase.FormatSize(size))
                : string.Format(LocalizationManager.T("Main.CacheNotFound"), kindLabel, baseLabel);
            _dialog.ShowInfo(message, LocalizationManager.T("Main.ClearCacheDlgTitle"));
        }
        catch (Exception ex)
        {
            _dialog.ShowError(
                string.Format(LocalizationManager.T("Main.ErrCacheClear"), ex.Message),
                LocalizationManager.T("Main.CacheErrorTitle"));
        }
    }

    /// <summary>
    /// Открывает окно выбора типа кеша и информационных баз, после подтверждения выполняет очистку.
    /// </summary>
    /// <param name="kind">Тип кеша, выбранный по умолчанию.</param>
    /// <param name="defaultInfobase">База, отмеченная по умолчанию (например, выделенная в главном окне).</param>
    private void OpenCacheClean(OneCCacheKind kind, Infobase? defaultInfobase = null)
    {
        if (Infobases.Count == 0)
        {
            _dialog.ShowInfo(LocalizationManager.T("Main.CacheEmpty"),
                LocalizationManager.T("Main.ClearCacheDlgTitle"));
            return;
        }

        // Список отдаётся копией и окно открывается модально: иначе пока оно
        // открыто, список баз можно очистить из главного окна, и очистка
        // остатков посчитает остатками уже весь кеш.
        var dialog = new CacheCleanWindow(Infobases.ToList(), kind, defaultInfobase ?? SelectedInfobase);
        if (!dialog.ShowSync(OwnerWindow()))
            return;

        var infobases = dialog.SelectedInfobases;
        var selectedKind = dialog.SelectedCacheKind;
        var cleanOrphans = dialog.CleanOrphans;
        if (selectedKind == OneCCacheKind.None)
            return;
        if (infobases.Count == 0 && !cleanOrphans)
            return;

        var kindLabel = CacheKindLabel(selectedKind);

        // Описание подтверждения: выбранные базы и/или остатки кеша от удалённых баз.
        var confirmParts = new List<string>();
        if (infobases.Count > 0)
            confirmParts.Add(string.Join(", ", infobases.Select(ib => ib.Name)));
        if (cleanOrphans)
            confirmParts.Add(LocalizationManager.T("Main.CacheOrphanNote"));

        if (!_dialog.Confirm(
            string.Format(LocalizationManager.T("Main.CacheConfirm"), kindLabel, string.Join("\n", confirmParts)),
            LocalizationManager.T("Main.ClearCacheDlgTitle")))
            return;

        try
        {
            // Объём «остатков» до очистки — для отчёта (issue #178).
            var orphanSize = cleanOrphans ? OneCCacheCleaner.GetOrphanSize(selectedKind, Infobases) : 0L;
            // Объём кэша баз до очистки — для отчёта (issue #178).
            var basesSize = infobases.Count > 0 ? OneCCacheCleaner.GetSize(selectedKind, infobases) : 0L;
            var removedBases = OneCCacheCleaner.Clear(infobases, selectedKind);
            var removedOrphans = cleanOrphans ? OneCCacheCleaner.ClearOrphans(selectedKind, Infobases) : 0;

            var resultParts = new List<string>();
            if (infobases.Count > 0)
            {
                var baseLabel = infobases.Count == 1
                    ? string.Format(LocalizationManager.T("Main.CacheBaseOne"), infobases[0].Name)
                    : string.Format(LocalizationManager.T("Main.CacheBaseMany"), infobases.Count);

                if (removedBases > 0)
                    resultParts.Add(string.Format(LocalizationManager.T("Main.CacheCleaned"), kindLabel, baseLabel, removedBases, Infobase.FormatSize(basesSize)));
                else
                    resultParts.Add(string.Format(LocalizationManager.T("Main.CacheNotFound"), kindLabel, baseLabel));
            }

            if (cleanOrphans)
            {
                if (removedOrphans > 0)
                    resultParts.Add(string.Format(LocalizationManager.T("Main.CacheOrphanRemoved"), removedOrphans, Infobase.FormatSize(orphanSize)));
                else
                    resultParts.Add(LocalizationManager.T("Main.CacheOrphanNone"));
            }

            _dialog.ShowInfo(string.Join("\n\n", resultParts), LocalizationManager.T("Main.ClearCacheDlgTitle"));
        }
        catch (Exception ex)
        {
            _dialog.ShowError(
                string.Format(LocalizationManager.T("Main.ErrCacheClear"), ex.Message),
                LocalizationManager.T("Main.CacheErrorTitle"));
        }
    }

    /// <summary>Возвращает читаемое описание типа кеша.</summary>
    private static string CacheKindLabel(OneCCacheKind kind)
    {
        return kind switch
        {
            OneCCacheKind.Program => LocalizationManager.T("Main.CacheKindProgram"),
            OneCCacheKind.User => LocalizationManager.T("Main.CacheKindUser"),
            _ => LocalizationManager.T("Main.CacheKindAll")
        };
    }
}

/// <summary>
/// Чип тега на панели быстрого отбора. Уведомляет контейнер при смене выбора.
/// </summary>
public class TagFilterItem : ViewModelBase
{
    private bool _isSelected;

    public TagFilterItem(string name)
    {
        Name = name;
    }

    public string Name { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
#endif