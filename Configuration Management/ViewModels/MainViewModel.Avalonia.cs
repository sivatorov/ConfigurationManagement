#if LINUX
using System.Collections.ObjectModel;
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

    // ---- Текущая сессия ----

    /// <summary>Показывать блок «Текущая сессия» в правой панели.</summary>
    public bool ShowSessionLaunchPanel => _settings.ShowSessionLaunchPanel;

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
    public ICommand AddTagCommand { get; private set; } = null!;
    public ICommand RemoveTagCommand { get; private set; } = null!;
    public ICommand ClearTagFiltersCommand { get; private set; } = null!;
    public ICommand LaunchEnterpriseCommand { get; private set; } = null!;
    public ICommand LaunchConfiguratorCommand { get; private set; } = null!;
    public ICommand EditInfobaseCommand { get; private set; } = null!;
    public ICommand AddInfobaseCommand { get; private set; } = null!;
    public ICommand DeleteInfobaseCommand { get; private set; } = null!;
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
    public ICommand ExitCommand { get; private set; } = null!;
    public ICommand CopyConnectionStringCommand { get; private set; } = null!;
    public ICommand RefreshAllConfigurationInfoCommand { get; private set; } = null!;
    public ICommand OpenInfobaseFolderCommand { get; private set; } = null!;
    public ICommand CreateDesktopShortcutCommand { get; private set; } = null!;
    public ICommand OpenNativeStarterCommand { get; private set; } = null!;
    public ICommand QuickClearCacheCommand { get; private set; } = null!;
    public ICommand ClearCacheCommand { get; private set; } = null!;
    public ICommand ClearProgramCacheCommand { get; private set; } = null!;
    public ICommand ClearUserCacheCommand { get; private set; } = null!;
    public ICommand ClearCacheBothCommand { get; private set; } = null!;

    private void InitializeCommands()
    {
        ClearSearchCommand = new RelayCommand(() => SearchText = string.Empty);
        SearchByTagCommand = new RelayCommand(SearchByTag);
        AddTagCommand = new RelayCommand(AddTag);
        RemoveTagCommand = new RelayCommand(RemoveTag);
        ClearTagFiltersCommand = new RelayCommand(ClearTagFilters);
        LaunchEnterpriseCommand = new RelayCommand(_ => Launch(_launchVm.LaunchCommand, LaunchKind.Enterprise), _ => SelectedInfobase is not null);
        LaunchConfiguratorCommand = new RelayCommand(_ => Launch(_launchVm.LaunchCommand, LaunchKind.Configurator), _ => SelectedInfobase is not null);
        EditInfobaseCommand = new RelayCommand(_ => EditInfobase(), _ => SelectedInfobase is not null);
        AddInfobaseCommand = new RelayCommand(AddInfobase);
        DeleteInfobaseCommand = new RelayCommand(_ => DeleteInfobase(), _ => SelectedInfobase is not null);
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
        ExitCommand = new RelayCommand(ExitApplication);
        CopyConnectionStringCommand = new RelayCommand(_ => CopyConnectionString(), _ => SelectedInfobase is not null);
        RefreshAllConfigurationInfoCommand = new RelayCommand(RefreshAllConfigurationInfo);
        OpenInfobaseFolderCommand = new RelayCommand(_ => OpenInfobaseFolder(),
            _ => SelectedInfobase?.Connection.Type == ConnectionType.File);
        CreateDesktopShortcutCommand = new RelayCommand(_ => CreateDesktopShortcut(), _ => SelectedInfobase is not null);
        OpenNativeStarterCommand = new RelayCommand(OpenNativeStarter);
        QuickClearCacheCommand = new RelayCommand(QuickClearCache, _ => SelectedInfobase is not null);
        // Команды очистки кеша доступны всегда (в т.ч. при выбранной группе): если база не
        // выделена, окно очистки открывается без выбранной базы (defaultSelected = null).
        ClearCacheCommand = new RelayCommand(_ => OpenCacheClean(OneCCacheKind.All));
        ClearProgramCacheCommand = new RelayCommand(_ => OpenCacheClean(OneCCacheKind.Program));
        ClearUserCacheCommand = new RelayCommand(_ => OpenCacheClean(OneCCacheKind.User));
        ClearCacheBothCommand = new RelayCommand(_ => OpenCacheClean(OneCCacheKind.All));
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
                RebuildTree();
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
            if (SetProperty(ref _selectedInfobase, value))
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
            if (SetProperty(ref _selectedGroupNode, value))
            {
                if (value is not null)
                    SelectedInfobase = null;
                UpdateStatus();
            }
        }
    }

    public bool ShowRightPanelDetails
    {
        get => _showRightPanelDetails;
        set => SetProperty(ref _showRightPanelDetails, value, nameof(RightPanelToggleTooltip));
    }

    public string RightPanelToggleTooltip => _showRightPanelDetails
        ? LocalizationManager.T("Main.CollapseRightPanel")
        : LocalizationManager.T("Main.ExpandRightPanel");

    public string StatusBarInfo
    {
        get => _statusBarInfo;
        set => SetProperty(ref _statusBarInfo, value);
    }

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

    // ---- Горячие клавиши (для подсказок) ----
    public string HotkeyEnterprise => "F3";
    public string HotkeyConfigurator => "F4";
    public string HotkeyEdit => "Ctrl+E";
    public string HotkeyAdd => "Ctrl+N";
    public string HotkeyFavorite => "Alt+F";
    public string HotkeyPin => "Ctrl+P";
    public string HotkeyDelete => "Del";
    public string HotkeyClearCache => "Ctrl+Shift+C";

    // ---- Видимость колонок ----
    public bool ShowExpandCollapseButtons => GroupByGroup;
    public bool ShowFavoritesButton => _settings.ShowFavoritesButton;
    public bool ShowPinnedButton => _settings.ShowPinnedButton;
    public bool ShowVersionColumn => _settings.ShowVersionColumn;
    public bool ShowConfigurationColumn => _settings.ShowConfigurationColumn;
    public bool ShowLaunchModeColumn => _settings.ShowLaunchModeColumn;
    public bool ShowServerColumn => _settings.ShowServerColumn;
    public bool ShowLastLaunchColumn => _settings.ShowLastLaunchColumn;
    public bool ShowSizeColumn => _settings.ShowSizeColumn;
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
    public double LaunchModeColumnWidth => _settings.LaunchModeColumnWidth;
    public double ServerColumnWidth => _settings.ServerColumnWidth;
    public double LastLaunchColumnWidth => _settings.LastLaunchColumnWidth;
    public double SizeColumnWidth => _settings.SizeColumnWidth;

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
            case "LaunchMode": _settings.LaunchModeColumnWidth = width; break;
            case "ServerBase": _settings.ServerColumnWidth = width; break;
            case "LastLaunch": _settings.LastLaunchColumnWidth = width; break;
            case "Size": _settings.SizeColumnWidth = width; break;
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
            if (!SetProperty(ref _sessionClient, value,
                    nameof(IsSessionClientAuto), nameof(IsSessionClientOrdinary), nameof(IsSessionClientThick),
                    nameof(IsSessionClientThickOrdinary), nameof(IsSessionClientThin)))
                return;

            _settings.SessionClientMode = SessionClientMode().ToString();
            SaveSettingsSilently();
        }
    }
    public bool IsSessionClientAuto { get => SessionClient == "Авто"; set { if (value) SessionClient = "Авто"; } }
    public bool IsSessionClientOrdinary { get => SessionClient == "Обычный"; set { if (value) SessionClient = "Обычный"; } }
    public bool IsSessionClientThick { get => SessionClient == "Толстый"; set { if (value) SessionClient = "Толстый"; } }
    public bool IsSessionClientThickOrdinary { get => SessionClient == "ТолстыйОбычные"; set { if (value) SessionClient = "ТолстыйОбычные"; } }
    public bool IsSessionClientThin { get => SessionClient == "Тонкий"; set { if (value) SessionClient = "Тонкий"; } }

    public string SessionArch
    {
        get => _sessionArch;
        set
        {
            if (!SetProperty(ref _sessionArch, value, nameof(IsSessionArchAuto), nameof(IsSessionArch32), nameof(IsSessionArch64)))
                return;

            _settings.SessionArchitecture = SessionArchitectureMode().ToString();
            SaveSettingsSilently();
        }
    }

    /// <summary>Режим клиента текущей сессии в терминах модели.</summary>
    private SessionClientMode SessionClientMode() => _sessionClient switch
    {
        "Обычный" => Models.SessionClientMode.Ordinary,
        "Толстый" => Models.SessionClientMode.Thick,
        "ТолстыйОбычные" => Models.SessionClientMode.ThickOrdinary,
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
                Models.SessionClientMode.ThickOrdinary => "ТолстыйОбычные",
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
            Models.SessionClientMode.ThickOrdinary => OneCClientType.Thick,
            Models.SessionClientMode.Ordinary => OneCClientType.Thick,
            _ => ClientFromInfobase(infobase)
        };

        var architecture = arch switch
        {
            Models.SessionArchitectureMode.X86 => OneCArchitecture.x86,
            Models.SessionArchitectureMode.X64 => OneCArchitecture.x64,
            _ => OneCLauncher.ResolveArchitecture(infobase.Architecture, infobase.PlatformVersion)
        };

        OneCRunMode? runMode = client switch
        {
            Models.SessionClientMode.Thick => OneCRunMode.Managed,
            Models.SessionClientMode.ThickOrdinary => OneCRunMode.Ordinary,
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
            _themeName = _settings.Theme;
            _compactMode = _settings.CompactMode;
            _sortField = string.IsNullOrWhiteSpace(_settings.SortField) ? "Name" : _settings.SortField;
            _sortAscending = _settings.SortAscending;
            _sessionClient = SessionClientFromSetting(_settings.SessionClientMode);
            _sessionArch = SessionArchFromSetting(_settings.SessionArchitecture);

            OnPropertyChanged(nameof(GroupByGroup));
            OnPropertyChanged(nameof(ShowEmptyGroups));
            OnPropertyChanged(nameof(ShowTagFilterPanel));
            NotifyColumnSettings();
            NotifySessionSettings();

            RebuildTree();
            UpdateStatus(string.Format(LocalizationManager.T("Main.LoadedBases"), _allInfobases.Count));

            // Применяем сохранённую тему, если активная схема не задана.
            if (_settings.ActiveColorScheme is { Colors.Count: > 0 })
                ThemeManager.ApplyScheme(_settings.ActiveColorScheme);
            else
                ThemeManager.ApplyTheme(string.IsNullOrWhiteSpace(_themeName) ? ThemeManager.LightThemeName : _themeName);
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
    /// Добавляет тег базе через диалог ввода. Параметр это база строки,
    /// без параметра берётся выбранная.
    /// </summary>
    private void AddTag(object? parameter)
    {
        var infobase = parameter as Infobase ?? SelectedInfobase;
        if (infobase is null)
            return;

        var dialog = new Configuration_Management.TagInputWindow();
        if (!dialog.ShowDialogSync(OwnerWindow()))
            return;

        var tag = dialog.Result?.Trim() ?? string.Empty;
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

    private void OnLaunched()
    {
        if (SelectedInfobase is not null)
        {
            SelectedInfobase.AddLaunchHistory(LocalizationManager.T("Main.LaunchAction"));
            SaveSilently();
        }
    }

    private void EditInfobase()
    {
        var ib = SelectedInfobase;
        if (ib is null)
            return;

        var dialog = new Configuration_Management.ConnectionSettingsWindow(
            ib, _groups, InstalledPlatformVersions(), ib.Group,
            AvailableServers(), AvailablePorts());

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
        ib.Architecture = dialog.Result.Architecture;
        ib.LaunchMode = dialog.Result.LaunchMode;
        ib.LaunchParameters = dialog.Result.LaunchParameters;
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

        SaveSilently();
        RebuildTree();
        ExportToIbasesAfterLocalChange();
        SelectedInfobase = ib;
    }

    private void AddInfobase()
    {
        var chooser = new Configuration_Management.AddEditWindow();
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
        var dialog = new Configuration_Management.CreateInfobaseWindow(
            fromTemplate,
            InstalledPlatformVersions(),
            defaultGroupPath,
            _groups);

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
        var dialog = new Configuration_Management.ConnectionSettingsWindow(
            null, _groups, InstalledPlatformVersions(), defaultGroupPath,
            AvailableServers(), AvailablePorts());

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
    private List<string> InstalledPlatformVersions()
    {
        try { return _platformService.FindInstalledVersions(); }
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

    private void SaveGroupsSilently()
    {
        try { _repository.SaveGroups(_groups); }
        catch (Exception ex) { _logger.Error("Не удалось сохранить группы", ex); }
    }

    /// <summary>Главное окно как владелец модального диалога.</summary>
    private static Avalonia.Controls.Window? OwnerWindow() =>
        (Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    private void DeleteInfobase()
    {
        var ib = SelectedInfobase;
        if (ib is null)
            return;
        if (_dialog.Confirm(string.Format(LocalizationManager.T("Main.ConfirmDeleteBase"), ib.Name)))
        {
            _allInfobases.Remove(ib);
            SaveSilently();
            RebuildTree();
            SelectedInfobase = null;
        }
    }

    private void ToggleFavorite() => ToggleFavoriteFor(SelectedInfobase);

    private void TogglePin() => TogglePinFor(SelectedInfobase);

    private void ToggleFavoriteFor(Infobase? infobase)
    {
        if (infobase is null)
            return;

        infobase.IsFavorite = !infobase.IsFavorite;
        SaveSilently();

        // Состав списка меняется только при активном временном фильтре: там база
        // может выпасть из выборки. Без фильтра строка перекрашивается сама
        // по уведомлению модели, и пересобирать дерево незачем.
        if (IsFilterModeActive())
            ApplyFilter();
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
        var settings = new Configuration_Management.SettingsWindow(this);
        if (Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            var owner = desktop.MainWindow;
            if (owner is not null)
                settings.ShowDialog(owner);
            else
                settings.Show();
        }
        else
        {
            settings.Show();
        }
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

    private void RefreshAllConfigurationInfo()
    {
        _dialog.ShowInfo(LocalizationManager.T("Main.RefreshConfigInfoMsg"));
    }

    // ======================= Этап 6: папки / ярлыки / стартер =======================

    /// <summary>Недавно запускавшиеся базы (для меню трея). До 8 по дате запуска.</summary>
    public List<Infobase> RecentInfobases =>
        _allInfobases
            .Where(ib => ib.LastLaunchDate.HasValue)
            .OrderByDescending(ib => ib.LastLaunchDate)
            .Take(8)
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

    private void SynchronizeWithIbases()
    {
        var path = _dialog.OpenFileDialog(
            LocalizationManager.T("Sync.ChooseIbasesFile"),
            LocalizationManager.T("Sync.IbasesFilter"));
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            var importResult = _sync.Import(path, _allInfobases, _groups);
            SaveSilently();
            RebuildTree();
            SyncMessage = LocalizationManager.T("Sync.Completed");
            StatusBarInfo = string.Format(LocalizationManager.T("Sync.ImportedCount"), _allInfobases.Count, _groups.Count);
            _logger.Info($"Синхронизация с ibases.v8i: {path}");
        }
        catch (Exception ex)
        {
            _logger.Error("Ошибка синхронизации с ibases.v8i", ex);
            _dialog.ShowError(string.Format(LocalizationManager.T("Sync.ErrSyncFailed"), ex.Message));
            SyncMessage = LocalizationManager.T("Sync.Failed");
        }
    }

    private void ToggleTheme()
    {
        ThemeName = ThemeManager.ToggleTheme();
        _settings.Theme = ThemeName;
        SaveSettingsSilently();
    }

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
    }

    private void ExitApplication()
    {
        SaveSettingsSilently();
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }

    // ======================= Сохранение =======================

    private void SaveSilently()
    {
        try { _repository.Save(_allInfobases); }
        catch (Exception ex) { _logger.Error("Не удалось сохранить список баз", ex); }
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
            StatusBarInfo = string.Format(LocalizationManager.T("Main.StatusBase"),
                SelectedInfobase.Name, SelectedInfobase.ServerDatabaseDisplay);
        else if (SelectedGroupNode is not null)
            StatusBarInfo = string.Format(LocalizationManager.T("Main.StatusGroup"), SelectedGroupNode.FullPath);
        else
            StatusBarInfo = LocalizationManager.T("Main.Ready");
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
        OnPropertyChanged(nameof(ShowLaunchModeColumn));
        OnPropertyChanged(nameof(ShowServerColumn));
        OnPropertyChanged(nameof(ShowLastLaunchColumn));
        OnPropertyChanged(nameof(ShowSizeColumn));
        OnPropertyChanged(nameof(ShowTags));
        OnPropertyChanged(nameof(NameColumnWidth));
        OnPropertyChanged(nameof(VersionColumnWidth));
        OnPropertyChanged(nameof(ConfigurationColumnWidth));
        OnPropertyChanged(nameof(LaunchModeColumnWidth));
        OnPropertyChanged(nameof(ServerColumnWidth));
        OnPropertyChanged(nameof(LastLaunchColumnWidth));
        OnPropertyChanged(nameof(SizeColumnWidth));
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
        OnPropertyChanged(nameof(IsSessionClientThickOrdinary));
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
            var removed = OneCCacheCleaner.Clear(ib, OneCCacheKind.All);
            var kindLabel = CacheKindLabel(OneCCacheKind.All);
            var baseLabel = string.Format(LocalizationManager.T("Main.CacheBaseOne"), ib.Name);
            var message = removed > 0
                ? string.Format(LocalizationManager.T("Main.CacheCleaned"), kindLabel, baseLabel, removed)
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
    private void OpenCacheClean(OneCCacheKind kind)
    {
        if (Infobases.Count == 0)
        {
            _dialog.ShowInfo(LocalizationManager.T("Main.CacheEmpty"),
                LocalizationManager.T("Main.ClearCacheDlgTitle"));
            return;
        }

        var dialog = new CacheCleanWindow(Infobases, kind, SelectedInfobase);
        if (!dialog.ShowSync())
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
            var removedBases = OneCCacheCleaner.Clear(infobases, selectedKind);
            var removedOrphans = cleanOrphans ? OneCCacheCleaner.ClearOrphans(selectedKind, Infobases) : 0;

            var resultParts = new List<string>();
            if (infobases.Count > 0)
            {
                var baseLabel = infobases.Count == 1
                    ? string.Format(LocalizationManager.T("Main.CacheBaseOne"), infobases[0].Name)
                    : string.Format(LocalizationManager.T("Main.CacheBaseMany"), infobases.Count);

                if (removedBases > 0)
                    resultParts.Add(string.Format(LocalizationManager.T("Main.CacheCleaned"), kindLabel, baseLabel, removedBases));
                else
                    resultParts.Add(string.Format(LocalizationManager.T("Main.CacheNotFound"), kindLabel, baseLabel));
            }

            if (cleanOrphans)
            {
                if (removedOrphans > 0)
                    resultParts.Add(string.Format(LocalizationManager.T("Main.CacheOrphanRemoved"), removedOrphans));
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