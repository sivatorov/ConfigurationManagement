#if WINDOWS
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Win32;
using Configuration_Management.Localization;
using Configuration_Management.Models;
using Configuration_Management.Services;

namespace Configuration_Management.ViewModels;

/// <summary>Main ViewModel (partial class split by feature blocks, see MainViewModel.*.cs).</summary>
public partial class MainViewModel : ViewModelBase
{
    private readonly IInfobaseRepository _repository;
    private readonly IDialogService _dialogs;
    private readonly IAppLogger _logger;
    private readonly IOneCLauncher _launcher;
    private readonly IIbasesSyncService _ibasesSync;
    private Infobase? _selectedInfobase;
    private string _lastSelectedInfobaseId = string.Empty;
    private string _lastSelectedGroupPath = string.Empty;
    private string _searchText = string.Empty;
    private bool _showFavoritesOnly;
    private bool _groupByGroup = true;
    private bool _showEmptyGroups;
    private string _noGroupColor = "#6B7280";
    private string _noGroupIconColor = "#FFFFFF";
    private string _noGroupIcon = string.Empty;
    private string _pinnedColor = "#8B5CF6";
    private string _pinnedIconColor = "#FFFFFF";
    private string _pinnedIcon = string.Empty;
    private string _savedTheme = string.Empty;
    private ColorScheme? _activeColorScheme;
    /// <summary>Пользовательская схема светлой темы (кастомизация хранится независимо от тёмной).</summary>
    private ColorScheme? _lightColorScheme;
    /// <summary>Пользовательская схема тёмной темы (кастомизация хранится независимо от светлой).</summary>
    private ColorScheme? _darkColorScheme;
    /// <summary>Флаг подписки на событие смены языка (предотвращает дублирование подписки).</summary>
    private bool _languageChangedSubscribed;
    private readonly HashSet<string> _collapsedGroups = new(StringComparer.OrdinalIgnoreCase);
    private List<string> _installedPlatformVersions = new();
    private List<string> _additionalPlatformSearchPaths = new();
    private double _nameColumnWidth;
    private double _versionColumnWidth;
    private double _launchModeColumnWidth;
    private double _serverColumnWidth;
    private double _lastLaunchColumnWidth;
    private bool _showFavoritesButton = true;
    private bool _showPinnedButton = true;
    private bool _showTagFilterPanel;
    private bool _allowMultipleInstances;
    private bool _checkForUpdatesOnStartup = true;
    private bool _autoUpdateEnabled = true;
    private readonly ObservableCollection<string> _activeTagFilters = new();
    private ListViewMode _listViewMode = ListViewMode.All;

    private bool _showTags = true;
    private bool _showVersionColumn = true;
    private bool _showConfigurationColumn = true;
    private double _configurationColumnWidth;
    private double _actionsColumnWidth;
    private bool _showRightPanelDetails = true;
    private bool _statusShowConnectionPath = true;
    private bool _statusShowArchitecture = true;
    private bool _statusShowLaunchMode = true;

    /// <summary>Переопределение типа клиента для текущего запуска (не сохраняется в базу).</summary>
    private SessionClientMode _sessionClientMode = SessionClientMode.Auto;
    /// <summary>Переопределение разрядности для текущего запуска (не сохраняется в базу).</summary>
    private SessionArchitectureMode _sessionArchitecture = SessionArchitectureMode.Auto;
    /// <summary>Разрядность по умолчанию, если у базы она не указана (X86 / X64).</summary>
    private string _defaultArchitecture = "X64";
    /// <summary>Показывать блок «Текущая сессия» в правой панели.</summary>
    private bool _showSessionLaunchPanel = true;
    private bool _statusShowPort = true;
    private bool _statusShowPlatformVersion = true;
    private bool _statusShowClientType;
    private bool _statusShowConnectionType;
    private bool _statusShowUser;
    private bool _statusShowId;
    private bool _showLaunchModeColumn = true;
    private bool _showServerColumn = true;
    private bool _showLastLaunchColumn = true;
    private bool _showSizeColumn = true;
    private bool _showActionsColumn = true;
    private double _sizeColumnWidth;
    private List<string> _columnOrder = new();
    private double _windowWidth;
    private double _windowHeight;
    private double _windowLeft;
    private double _windowTop;
    /// <summary>Отмена предыдущего отложенного сохранения (debounce).</summary>
    private CancellationTokenSource? _saveDebounceCts;
    private const int SaveDebounceMs = 400;
    private string _windowState = string.Empty;
    private bool _rememberWindowLayout = true;
    private IbasesSyncMode _ibasesSyncMode = IbasesSyncMode.None;
    private string _ibasesSyncFilePath = string.Empty;
    private IbasesSyncTrigger _ibasesSyncTrigger = IbasesSyncTrigger.OnStartup;
    // ---- Профиль: резервное копирование в произвольный каталог ----
    private string _profileBackupDirectory = string.Empty;
    private bool _profileRestoreOnStartup;
    private int _ibasesSyncIntervalMinutes = 30;
    private string _ibasesSyncScheduleTime = "09:00";
    private bool _ibasesBackupEnabled = true;
    private int _ibasesBackupKeepCount = 5;
    private bool _addTimestampToExportFileName = true;
    private string _exportTimestampFormat = "yyyyMMdd_HHmmss";
    private string _syncMessage = string.Empty;
    private DispatcherTimer? _syncTimer;
    private DateTime? _nextScheduleRun;
    private bool _syncTimerRunning;
    private bool _closeToTray;
    private bool _showTrayIcon = true;
    private bool _compactMode;

    // ---- Быстрый запуск: индикатор загрузки и фоновое построение дерева ----
    private bool _isLoading;
    private string _loadingMessage = string.Empty;
    private bool _startupInitCompleted;
    /// <summary>Кеш размеров файловых ИБ (ускоряет запуск при большом списке баз).</summary>
    private readonly Dictionary<string, Models.FileSizeCacheEntry> _fileSizeCache =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _escapeToTray = true;
    private string _afterLaunchAction = "None";
    private List<string> _templateCatalogPaths = new();
    private string _hotkeyEnterprise = "F3";
    private string _hotkeyConfigurator = "F4";
    private string _hotkeyFavorite = "F8";
    private string _hotkeyEdit = "F2";
    private string _hotkeyDelete = "Delete";
    private string _hotkeyClearCache = "";
    private string _hotkeyAdd = "Insert";
    private string _hotkeyPin = "";
    private string _hotkeyShowAll = "";
    private string _hotkeyShowFavorites = "";
    private string _hotkeyShowRecent = "";
    private string _sortField = "Name";
    private bool _sortAscending = true;
    /// <summary>Направление сортировки подгрупп по имени (true — А→Я, false — Я→А).</summary>
    private bool _groupSortAscending = true;
    private string _fontFamily = Themes.ThemeManager.DefaultFontFamily;
    private double _fontSize = Themes.ThemeManager.DefaultFontSize;
    private string _fontWeight = Themes.ThemeManager.DefaultFontWeight;
    private string _fontStyle = Themes.ThemeManager.DefaultFontStyle;
    private Dictionary<string, Models.ElementFontSettings> _elementFonts = new();
    private readonly List<string> _favoriteHotkeyIds = new();
    private CancellationTokenSource? _searchDebounceCts;
    private HashSet<string> _activeTagFilterSet = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Идёт ли в данный момент выгрузка .dt/.cf (показывает индикатор в верхней панели).</summary>
    private bool _isExporting;
    /// <summary>Текст подсказки индикатора выгрузки (сводка о текущей операции).</summary>
    private string _exportIndicatorTooltip = string.Empty;

    /// <summary>Событие: изменился список избранных с горячими клавишами (нужно перерегистрировать биндинги).</summary>
    public event EventHandler? FavoriteHotkeysChanged;

    /// <summary>
    /// Внутренний набор узлов дерева групп, перестраиваемый при изменении данных.
    /// Заполняется в методе <see cref="RebuildGroupTree"/>.
    /// </summary>
    private List<GroupNodeViewModel> _groupNodes = new();

    public MainViewModel(
        IInfobaseRepository? repository = null,
        IDialogService? dialogs = null,
        IAppLogger? logger = null,
        IOneCLauncher? launcher = null,
        IIbasesSyncService? ibasesSync = null)
    {
        _repository = repository ?? new InfobaseRepository();
        _dialogs = dialogs ?? new WpfDialogService();
        _logger = logger ?? new FileAppLogger();
        _launcher = launcher ?? new OneCLauncherService();
        _ibasesSync = ibasesSync ?? new IbasesSyncService();
        _logger.Info("MainViewModel инициализирован");

        // Отслеживание выгрузок .dt/.cf для анимированного индикатора в верхней панели.
        OneCLauncher.DesignerBatchStarted += OnDesignerBatchStarted;
        OneCLauncher.DesignerBatchCompleted += OnDesignerBatchCompleted;

        // Загружаем настройки интерфейса (состояние кнопок «Избранные» и «Группировать»).
        var settings = _repository.LoadSettings();
        _showFavoritesOnly = settings.ShowFavoritesOnly;
        _groupByGroup = settings.GroupByGroup;
        _showEmptyGroups = settings.ShowEmptyGroups;
        _noGroupColor = string.IsNullOrWhiteSpace(settings.NoGroupColor) ? "#6B7280" : settings.NoGroupColor;
        _noGroupIconColor = string.IsNullOrWhiteSpace(settings.NoGroupIconColor) ? "#FFFFFF" : settings.NoGroupIconColor;
        _noGroupIcon = settings.NoGroupIcon ?? string.Empty;
        _pinnedColor = string.IsNullOrWhiteSpace(settings.PinnedColor) ? "#8B5CF6" : settings.PinnedColor;
        _pinnedIconColor = string.IsNullOrWhiteSpace(settings.PinnedIconColor) ? "#FFFFFF" : settings.PinnedIconColor;
        _pinnedIcon = settings.PinnedIcon ?? string.Empty;
        _savedTheme = settings.Theme;
        _fontFamily = string.IsNullOrWhiteSpace(settings.FontFamily)
            ? Themes.ThemeManager.DefaultFontFamily : settings.FontFamily;
        _fontSize = settings.FontSize > 0 ? settings.FontSize : Themes.ThemeManager.DefaultFontSize;
        _fontWeight = string.Equals(settings.FontWeight, "Bold", StringComparison.OrdinalIgnoreCase)
            ? "Bold" : Themes.ThemeManager.DefaultFontWeight;
        _fontStyle = string.Equals(settings.FontStyle, "Italic", StringComparison.OrdinalIgnoreCase)
            ? "Italic" : Themes.ThemeManager.DefaultFontStyle;
        _elementFonts = settings.ElementFonts is null
            ? new Dictionary<string, Models.ElementFontSettings>()
            : new Dictionary<string, Models.ElementFontSettings>(settings.ElementFonts);
        // Раздельные пользовательские схемы для светлой и тёмной темы: кастомизация
        // каждой базовой темы хранится независимо, поэтому переключение тем не затирает
        // настроенное оформление.
        _lightColorScheme = settings.LightColorScheme;
        _darkColorScheme = settings.DarkColorScheme;
        // Миграция: если задан только старый одиночный ActiveColorScheme, переносим его
        // в слот соответствующей базовой темы.
        if (settings.ActiveColorScheme is { Colors.Count: > 0 })
        {
            if (settings.ActiveColorScheme.IsDark && _darkColorScheme is not { Colors.Count: > 0 })
                _darkColorScheme = settings.ActiveColorScheme;
            else if (!settings.ActiveColorScheme.IsDark && _lightColorScheme is not { Colors.Count: > 0 })
                _lightColorScheme = settings.ActiveColorScheme;
        }
        var baseTheme = string.IsNullOrWhiteSpace(_savedTheme)
            ? ((_darkColorScheme is { Colors.Count: > 0 }) ? Themes.ThemeManager.DarkThemeName : Themes.ThemeManager.LightThemeName)
            : _savedTheme;
        _activeColorScheme = SchemeForTheme(IsDarkTheme(baseTheme));
        _additionalPlatformSearchPaths = new List<string>(settings.AdditionalPlatformSearchPaths ?? new List<string>());
        PlatformVersionService.SetAdditionalSearchPaths(_additionalPlatformSearchPaths);
        // Актуальный список версий платформы с диска (Program Files + доп. пути) собирается
        // в фоне уже после показа окна: рекурсивное сканирование каталогов установки могло бы
        // заметно задержать появление главного окна. Сразу берём сохранённый список из настроек,
        // чтобы выпадающие списки выбора версии работали с самого начала.
        _installedPlatformVersions = new List<string>(settings.InstalledPlatformVersions ?? new List<string>());
        _ = RefreshInstalledPlatformVersionsInBackground();
        _nameColumnWidth = settings.NameColumnWidth;
        _versionColumnWidth = settings.VersionColumnWidth;
        _launchModeColumnWidth = settings.LaunchModeColumnWidth;
        _serverColumnWidth = settings.ServerColumnWidth;
        _lastLaunchColumnWidth = settings.LastLaunchColumnWidth;
        _showFavoritesButton = settings.ShowFavoritesButton;
        _showPinnedButton = settings.ShowPinnedButton;
        _showTags = settings.ShowTags;
        _showTagFilterPanel = settings.ShowTagFilterPanel;
        _allowMultipleInstances = settings.AllowMultipleInstances;
        _checkForUpdatesOnStartup = settings.CheckForUpdatesOnStartup;
        _autoUpdateEnabled = settings.AutoUpdateEnabled;
        _showVersionColumn = settings.ShowVersionColumn;
        _showConfigurationColumn = settings.ShowConfigurationColumn;
        _configurationColumnWidth = settings.ConfigurationColumnWidth;
        _actionsColumnWidth = settings.ActionsColumnWidth;
        _showRightPanelDetails = settings.ShowRightPanelDetails;
        _showSessionLaunchPanel = settings.ShowSessionLaunchPanel;
        if (Enum.TryParse<SessionClientMode>(settings.SessionClientMode, true, out var scm))
            _sessionClientMode = scm;
        if (Enum.TryParse<SessionArchitectureMode>(settings.SessionArchitecture, true, out var sam))
            _sessionArchitecture = sam;
        // Разрядность по умолчанию для запуска, если у базы она не указана.
        _defaultArchitecture = string.Equals(settings.DefaultArchitecture, "X64", StringComparison.OrdinalIgnoreCase)
            ? "X64" : "X86";
        OneCLauncher.DefaultArchitecture = ParseArchitecture(_defaultArchitecture);
        _statusShowConnectionPath = settings.StatusShowConnectionPath;
        _statusShowArchitecture = settings.StatusShowArchitecture;
        _statusShowLaunchMode = settings.StatusShowLaunchMode;
        _statusShowPort = settings.StatusShowPort;
        _statusShowPlatformVersion = settings.StatusShowPlatformVersion;
        _statusShowClientType = settings.StatusShowClientType;
        _statusShowConnectionType = settings.StatusShowConnectionType;
        _statusShowUser = settings.StatusShowUser;
        _statusShowId = settings.StatusShowId;
        _showLaunchModeColumn = settings.ShowLaunchModeColumn;
        _showServerColumn = settings.ShowServerColumn;
        _showLastLaunchColumn = settings.ShowLastLaunchColumn;
        _showSizeColumn = settings.ShowSizeColumn;
        _showActionsColumn = settings.ShowActionsColumn;
        _sizeColumnWidth = settings.SizeColumnWidth;
        _columnOrder = settings.ColumnOrder is { Count: > 0 }
            ? new List<string>(settings.ColumnOrder)
            : new List<string>();
        _windowWidth = settings.WindowWidth;
        _windowHeight = settings.WindowHeight;
        _windowLeft = settings.WindowLeft;
        _windowTop = settings.WindowTop;
        _windowState = settings.WindowState;
        _rememberWindowLayout = settings.RememberWindowLayout;
        _ibasesSyncMode = settings.IbasesSyncMode;
        _ibasesSyncFilePath = settings.IbasesSyncFilePath;
        _ibasesSyncTrigger = settings.IbasesSyncTrigger;
        _ibasesSyncIntervalMinutes = settings.IbasesSyncIntervalMinutes;
        _ibasesSyncScheduleTime = settings.IbasesSyncScheduleTime;
        _ibasesBackupEnabled = settings.IbasesBackupEnabled;
        _ibasesBackupKeepCount = settings.IbasesBackupKeepCount > 0 ? settings.IbasesBackupKeepCount : 5;
        _profileBackupDirectory = settings.ProfileBackupDirectory ?? string.Empty;
        _profileRestoreOnStartup = settings.ProfileRestoreOnStartup;
        _addTimestampToExportFileName = settings.AddTimestampToExportFileName;
        _exportTimestampFormat = string.IsNullOrWhiteSpace(settings.ExportTimestampFormat)
            ? "yyyyMMdd_HHmmss"
            : settings.ExportTimestampFormat;
        _closeToTray = settings.CloseToTray;
        _showTrayIcon = settings.ShowTrayIcon;
        _escapeToTray = settings.EscapeToTray;
        _afterLaunchAction = settings.AfterLaunchAction ?? "None";
        _compactMode = settings.CompactMode;
        _templateCatalogPaths = settings.TemplateCatalogPaths?.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            ?? new List<string>();
        OneCTemplateService.SetUserTemplatePaths(_templateCatalogPaths);
        _hotkeyEnterprise = string.IsNullOrWhiteSpace(settings.HotkeyEnterprise) ? "F3" : settings.HotkeyEnterprise.Trim();
        _hotkeyConfigurator = string.IsNullOrWhiteSpace(settings.HotkeyConfigurator) ? "F4" : settings.HotkeyConfigurator.Trim();
        _hotkeyFavorite = settings.HotkeyFavorite?.Trim() ?? "F8";
        _hotkeyEdit = settings.HotkeyEdit?.Trim() ?? "F2";
        _hotkeyDelete = settings.HotkeyDelete?.Trim() ?? "Delete";
        _hotkeyClearCache = settings.HotkeyClearCache?.Trim() ?? "";
        _hotkeyAdd = settings.HotkeyAdd?.Trim() ?? "Insert";
        _hotkeyPin = settings.HotkeyPin?.Trim() ?? "";
        _hotkeyShowAll = settings.HotkeyShowAll?.Trim() ?? "";
        _hotkeyShowFavorites = settings.HotkeyShowFavorites?.Trim() ?? "";
        _hotkeyShowRecent = settings.HotkeyShowRecent?.Trim() ?? "";
        _sortField = string.IsNullOrWhiteSpace(settings.SortField) ? "Name" : settings.SortField;
        _sortAscending = settings.SortAscending;
        _lastSelectedInfobaseId = settings.LastSelectedInfobaseId ?? string.Empty;
        _lastSelectedGroupPath = settings.LastSelectedGroupPath ?? string.Empty;
        if (settings.FavoriteHotkeyIds != null)
        {
            foreach (var id in settings.FavoriteHotkeyIds.Take(9))
            {
                if (!string.IsNullOrEmpty(id))
                    _favoriteHotkeyIds.Add(id);
            }
        }
        foreach (var groupName in settings.CollapsedGroups)
        {
            _collapsedGroups.Add(groupName);
        }

        // Кеш размеров файловых ИБ из прошлых запусков: позволяет не сканировать диски
        // заново для каждой базы при старте (см. RefreshFileMetadata / CalculateFileBaseSizeCached).
        if (settings.FileSizeCache is { Count: > 0 })
        {
            foreach (var kv in settings.FileSizeCache)
                _fileSizeCache[kv.Key] = kv.Value;
        }

        // Загружаем базы из файла настроек.
        var saved = _repository.Load();
        Infobases = new ObservableCollection<Infobase>(saved);

        // Загружаем группы из файла настроек. Стандартные группы из демо-данных не создаются.
        var loadedGroups = _repository.LoadGroups();
        Groups = new ObservableCollection<Group>(loadedGroups);

        InfobasesView = CollectionViewSource.GetDefaultView(Infobases);
        InfobasesView.Filter = FilterInfobase;
        ApplySortDescriptions();

        // Дерево групп (отображается в виде «группа в группе»).
        GroupNodes = new ObservableCollection<GroupNodeViewModel>();

        // Смена языка интерфейса: локализованные свойства VM и служебные узлы дерева
        // обновляются на лету без перезапуска. XAML-привязки {loc:Loc} обновляются сами
        // через LocalizationManager.Source.NotifyAll(), а здесь обновляем свойства VM,
        // возвращающие LocalizationManager.T(...), и пересобираем дерево групп.
        LocalizationManager.Instance.LanguageChanged += OnLanguageChanged;
        _languageChangedSubscribed = true;

        // Тяжёлая инициализация (построение дерева групп, назначение слотов Alt+1…9,
        // восстановление последнего выделения, подсчёт размеров файловых баз) выполняется
        // ПОСЛЕ показа окна в фоне с индикатором прогресса. Так главное окно появляется
        // мгновенно даже при большом числе баз, а список достраивается без «зависания».
        // См. CompleteStartupInitializationAsync.
        IsLoading = true;
        LoadingMessage = LocalizationManager.T("Main.LoadingInfobases");
        _ = CompleteStartupInitializationAsync();

        SelectInfobaseCommand = new RelayCommand(SelectInfobase);
        RefreshCommand = new RelayCommand(Refresh);
        AddInfobaseCommand = new RelayCommand(AddInfobase);
        // Узел «Закреплённые» сам по себе группы не имеет, но по горячей клавише правки
        // открывает редактор оформления узла (цвет и иконка), как для «Без группы».
        // Исключение — кнопка действия строки конкретной базы внутри узла (параметр — Infobase):
        // такую базу редактируем как обычно.
        EditInfobaseCommand = new RelayCommand(EditInfobase,
            p => (ResolveActionTarget(p) != null || SelectedGroupNode?.Group != null || IsNoGroupNodeSelected() || IsPinnedNodeSelected()));
        DeleteInfobaseCommand = new RelayCommand(DeleteSelected,
            p => ResolveActionTarget(p) != null || SelectedGroupNode?.Group != null);
        // Команды группы: параметр — узел группы или сама группа из строки дерева.
        // Для служебного узла «Закреплённые» (без модели Group) открываем редактор
        // оформления узла (цвет и иконка), как для «Без группы».
        EditGroupCommand = new RelayCommand(p =>
        {
            if (p is GroupNodeViewModel node &&
                node.Group is null &&
                string.Equals(node.Marker, GroupNodeViewModel.PinnedMarker, StringComparison.Ordinal))
            {
                EditPinnedNode();
                return;
            }
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
        ToggleFavoriteCommand = new RelayCommand(ToggleFavorite, _ => SelectedInfobase != null);
        ToggleFavoriteForCommand = new RelayCommand(ToggleFavoriteFor);
        LaunchCommand = new RelayCommand(p => Launch(p), _ => SelectedInfobase != null);
        // Обратная совместимость с XAML: отдельные команды делегируют в единую LaunchCommand.
        LaunchEnterpriseCommand = new RelayCommand(p => Launch(LaunchKind.Enterprise, false, p as Infobase), p => ResolveActionTarget(p) != null);
        LaunchConfiguratorCommand = new RelayCommand(p => Launch(LaunchKind.Configurator, false, p as Infobase), p => ResolveActionTarget(p) != null);
        // Переключение вкладок списка баз (Все / Избранное / Недавние) по горячим клавишам.
        ShowAllCommand = new RelayCommand(_ => IsListModeAll = true);
        ShowFavoritesCommand = new RelayCommand(_ => IsListModeFavorites = true);
        ShowRecentCommand = new RelayCommand(_ => IsListModeRecent = true);
        LaunchEnterpriseThinCommand = new RelayCommand(_ => Launch(LaunchKind.Thin32), _ => SelectedInfobase != null);
        LaunchEnterpriseThickCommand = new RelayCommand(_ => Launch(LaunchKind.Thick32), _ => SelectedInfobase != null);
        LaunchEnterpriseThin64Command = new RelayCommand(_ => Launch(LaunchKind.Thin64), _ => SelectedInfobase != null);
        LaunchEnterpriseThick64Command = new RelayCommand(_ => Launch(LaunchKind.Thick64), _ => SelectedInfobase != null);
        LaunchEnterpriseAsAdminCommand = new RelayCommand(_ => Launch(LaunchKind.Enterprise, runAsAdmin: true), _ => SelectedInfobase != null);
        LaunchConfiguratorAsAdminCommand = new RelayCommand(_ => Launch(LaunchKind.Configurator, runAsAdmin: true), _ => SelectedInfobase != null);
        LaunchEnterpriseWithParamsCommand = new RelayCommand(LaunchEnterpriseWithParams, _ => SelectedInfobase != null);
        LaunchEnterpriseWithAuthCommand = new RelayCommand(LaunchEnterpriseWithAuth, _ => SelectedInfobase != null);
        LaunchConfiguratorWithParamsCommand = new RelayCommand(LaunchConfiguratorWithParams, _ => SelectedInfobase != null);
        LaunchNativeStarterCommand = new RelayCommand(_ => LaunchNativeStarter());
        ImportFromIbasesV8iCommand = new RelayCommand(ImportFromIbasesV8i);
        ExportToIbasesV8iCommand = new RelayCommand(_ => ExportToIbases());
        SynchronizeWithIbasesCommand = new RelayCommand(SynchronizeWithIbasesManual);
        ToggleRightPanelDetailsCommand = new RelayCommand(_ => ShowRightPanelDetails = !ShowRightPanelDetails);
        ToggleSessionLaunchPanelCommand = new RelayCommand(_ => ShowSessionLaunchPanel = !ShowSessionLaunchPanel);
        ExportInfobasesCommand = new RelayCommand(ExportInfobases);
        ImportInfobasesCommand = new RelayCommand(ImportInfobases);
        ClearAllInfobasesCommand = new RelayCommand(ClearAllInfobases);
        TogglePinCommand = new RelayCommand(TogglePin, _ => SelectedInfobase != null);
        TogglePinForCommand = new RelayCommand(TogglePinFor);
        CopyConnectionStringCommand = new RelayCommand(CopyConnectionString, _ => SelectedInfobase != null);
        // Команда очистки кеша верхней панели действует на выбранную базу: если база не
        // выделена — недоступна (CanExecute=false). В колонке «Действия» строка передаёт
        // свою базу параметром, поэтому там кнопка включена независимо от глобального выбора.
        ClearCacheCommand = new RelayCommand(ClearCache,
            p => p is Infobase ? true : SelectedInfobase != null);
        ClearProgramCacheCommand = new RelayCommand(_ => OpenCacheClean(OneCCacheKind.Program));
        ClearUserCacheCommand = new RelayCommand(_ => OpenCacheClean(OneCCacheKind.User));
        ClearCacheBothCommand = new RelayCommand(_ => OpenCacheClean(OneCCacheKind.All));
        OpenInfobaseFolderCommand = new RelayCommand(OpenInfobaseFolder,
            _ => SelectedInfobase?.Connection.Type == ConnectionType.File);
        CreateDesktopShortcutCommand = new RelayCommand(CreateDesktopShortcut, _ => SelectedInfobase != null);
        RemoveMissingFileBasesCommand = new RelayCommand(RemoveMissingFileBases);
        KillOneCProcessesCommand = new RelayCommand(KillOneCProcesses);
        DumpInfobaseDtCommand = new RelayCommand(DumpInfobaseDt, _ => SelectedInfobase != null);
        DumpConfigurationCfCommand = new RelayCommand(DumpConfigurationCf, _ => SelectedInfobase != null);
        TestInfobaseCommand = new RelayCommand(TestInfobase, _ => SelectedInfobase != null);
        ShowLaunchHistoryCommand = new RelayCommand(ShowLaunchHistory, _ => SelectedInfobase != null);
        RefreshFileSizesCommand = new RelayCommand(_ => RefreshFileMetadata());
        AddTagInlineCommand = new RelayCommand(AddTagInline);
        RemoveTagCommand = new RelayCommand(RemoveTag);
        SearchByTagCommand = new RelayCommand(SearchByTag);
        ClearSearchCommand = new RelayCommand(ClearSearch);
        ClearTagFiltersCommand = new RelayCommand(ClearTagFilters, _ => HasActiveTagFilter);
        CollapseAllGroupsCommand = new RelayCommand(CollapseAllGroups);
        ExpandAllGroupsCommand = new RelayCommand(ExpandAllGroups);
        SortGroupsAscendingCommand = new RelayCommand(_ => SortGroups(ascending: true));
        SortGroupsDescendingCommand = new RelayCommand(_ => SortGroups(ascending: false));
        ToggleGroupExpandedCommand = new RelayCommand(ToggleGroupExpanded);
        OpenSettingsCommand = new RelayCommand(OpenSettings);
        OpenInfobaseByLinkCommand = new RelayCommand(OpenInfobaseByLink);
        RefreshConfigurationInfoCommand = new RelayCommand(RefreshConfigurationInfo, _ => SelectedInfobase != null);
        CheckAvailabilityCommand = new RelayCommand(_ => CheckAvailability());
        RegisterComConnectorCommand = new RelayCommand(RegisterComConnector);

        // Если список баз пуст — предлагаем загрузить базы из файла ibases.v8i.
        // Диалог нельзя показывать прямо из конструктора: главное окно ещё не показано,
        // и модальный MessageBox, открытый до появления окна, зависает (в т.ч. при нажатии
        // «Выход»/отмены). Откладываем запрос до завершения раскладки и отрисовки первого
        // кадра, когда окно уже на экране и модальный цикл сообщений работает корректно.
        if (Infobases.Count == 0)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is not null)
            {
                dispatcher.BeginInvoke(
                    new Action(PromptImportFromIbasesV8i),
                    System.Windows.Threading.DispatcherPriority.ContextIdle);
            }
            else
            {
                PromptImportFromIbasesV8i();
            }
        }
    }

    /// <summary>Список информационных баз.</summary>
    public ObservableCollection<Infobase> Infobases { get; }

    /// <summary>Представление списка баз с группировкой и фильтрацией.</summary>
    public ICollectionView InfobasesView { get; }

    /// <summary>Идёт фоновая инициализация главного окна после показа (показывается индикатор загрузки).</summary>
    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    /// <summary>Текст индикатора загрузки на этапе фоновой инициализации.</summary>
    public string LoadingMessage
    {
        get => _loadingMessage;
        set => SetProperty(ref _loadingMessage, value);
    }

    /// <summary>
    /// Событие завершения фоновой инициализации после показа окна. Позволяет интерфейсу
    /// восстановить последнее выделение и пересчитать раскладку, когда дерево уже построено.
    /// </summary>
    public event EventHandler? StartupInitializationCompleted;

    /// <summary>Узлы дерева групп информационных баз для отображения «группа в группе».</summary>
    public ObservableCollection<GroupNodeViewModel> GroupNodes { get; private set; }

    /// <summary>Выбранная информационная база.</summary>
    public Infobase? SelectedInfobase
    {
        get => _selectedInfobase;
        set
        {
            if (SetProperty(ref _selectedInfobase, value))
            {
                // Запоминаем последнюю выбранную базу для восстановления при следующем запуске.
                if (value is not null)
                {
                    _lastSelectedInfobaseId = value.Id ?? string.Empty;
                    _lastSelectedGroupPath = string.Empty;
                    ScheduleSaveSettings();

                    // Размер кеша 1С вычисляется в фоне и отображается в правой панели.
                    value.RefreshCacheSizeAsync();
                }

                CommandManager.InvalidateRequerySuggested();
                OnPropertyChanged(nameof(StatusBarInfo));
            }
        }
    }

    private GroupNodeViewModel? _selectedGroupNode;

    /// <summary>Выбранный узел группы в дереве (null, если выбрана база или ничего).</summary>
    public GroupNodeViewModel? SelectedGroupNode
    {
        get => _selectedGroupNode;
        set
        {
            var previous = _selectedGroupNode;
            if (SetProperty(ref _selectedGroupNode, value))
            {
                // Запоминаем последнюю выбранную группу для восстановления при следующем запуске.
                if (value?.Group is not null)
                {
                    _lastSelectedGroupPath = value.FullPath;
                    _lastSelectedInfobaseId = string.Empty;
                    ScheduleSaveSettings();
                }

                // Сбрасываем подсветку ранее выбранной группы и подсвечиваем новую,
                // чтобы выделение было видно поверх цвета группы в дереве.
                if (previous is not null)
                    previous.IsSelected = false;
                if (_selectedGroupNode is not null)
                    _selectedGroupNode.IsSelected = true;

                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    /// <summary>Идентификатор последней выбранной базы (для восстановления при запуске).</summary>
    public string LastSelectedInfobaseId => _lastSelectedInfobaseId;

    /// <summary>Полный путь последней выбранной группы (для восстановления при запуске).</summary>
    public string LastSelectedGroupPath => _lastSelectedGroupPath;

    /// <summary>
    /// Рекурсивно ищет узел группы по полному пути в текущем дереве групп.
    /// </summary>
    public GroupNodeViewModel? FindGroupNodeByPath(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath))
            return null;
        foreach (var root in GroupNodes)
        {
            var found = FindGroupNodeIn(root, fullPath);
            if (found is not null)
                return found;
        }
        return null;
    }

    private static GroupNodeViewModel? FindGroupNodeIn(GroupNodeViewModel node, string fullPath)
    {
        if (node.Group is not null &&
            string.Equals(node.FullPath, fullPath, StringComparison.OrdinalIgnoreCase))
            return node;
        foreach (var child in node.Children)
        {
            var found = FindGroupNodeIn(child, fullPath);
            if (found is not null)
                return found;
        }
        return null;
    }

    /// <summary>
    /// Раскрывает (в модели) ветку дерева, содержащую последнюю выбранную строку,
    /// чтобы при запуске её контейнер был сгенерирован и выделение можно было восстановить.
    /// </summary>
    private void PrepareLastSelectionExpansion()
    {
        string? groupPath = null;
        if (!string.IsNullOrEmpty(_lastSelectedInfobaseId))
        {
            var ib = Infobases.FirstOrDefault(i => string.Equals(i.Id, _lastSelectedInfobaseId, StringComparison.Ordinal));
            if (ib is not null && !string.IsNullOrEmpty(ib.Group))
                groupPath = ib.Group;
        }
        else if (!string.IsNullOrEmpty(_lastSelectedGroupPath))
        {
            groupPath = _lastSelectedGroupPath;
        }

        if (string.IsNullOrEmpty(groupPath))
            return;

        var node = FindGroupNodeByPath(groupPath);
        if (node is null)
            return;

        var chain = new List<GroupNodeViewModel>();
        for (var n = node; n is not null; n = n.Parent)
            chain.Add(n);
        chain.Reverse();
        foreach (var n in chain)
        {
            n.SetExpandedSilent(true);
            n.NotifyIsExpanded();
        }
    }

    /// <summary>Текст поиска по информационным базам.</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
                ScheduleRebuildGroupTree();
        }
    }

    /// <summary>Показывать только избранные базы.</summary>
    public bool ShowFavoritesOnly
    {
        get => _showFavoritesOnly;
        set
        {
            if (SetProperty(ref _showFavoritesOnly, value))
            {
                // Без InfobasesView.Refresh — дерево строится по EnumerateFilteredInfobases,
                // лишний Refresh на тысячи элементов сильно тормозит UI.
                RebuildGroupTree();
                ScheduleSaveSettings();
            }
        }
    }

    /// <summary>Группировать базы по группам.</summary>
    public bool GroupByGroup
    {
        get => _groupByGroup;
        set
        {
            if (SetProperty(ref _groupByGroup, value))
            {
                RebuildGroupTree();
                ScheduleSaveSettings();
                OnPropertyChanged(nameof(GroupByGroupText));
                OnPropertyChanged(nameof(ShowExpandCollapseButtons));
            }
        }
    }

    /// <summary>Показывать пустые группы в дереве.</summary>
    public bool ShowEmptyGroups
    {
        get => _showEmptyGroups;
        set
        {
            if (_showEmptyGroups == value) return;
            _showEmptyGroups = value;
            OnPropertyChanged();
            RebuildGroupTree();
            ScheduleSaveSettings();
        }
    }

    /// <summary>Цвет фона заголовка узла «Без группы».</summary>
    public string NoGroupColor
    {
        get => _noGroupColor;
        set
        {
            if (_noGroupColor == value) return;
            _noGroupColor = value;
            OnPropertyChanged();
            RebuildGroupTree();
            ScheduleSaveSettings();
        }
    }

    /// <summary>Цвет иконки узла «Без группы».</summary>
    public string NoGroupIconColor
    {
        get => _noGroupIconColor;
        set
        {
            if (_noGroupIconColor == value) return;
            _noGroupIconColor = value;
            OnPropertyChanged();
            RebuildGroupTree();
            ScheduleSaveSettings();
        }
    }

    /// <summary>Ключ иконки узла «Без группы» (пусто — по умолчанию).</summary>
    public string NoGroupIcon
    {
        get => _noGroupIcon;
        set
        {
            if (_noGroupIcon == value) return;
            _noGroupIcon = value;
            OnPropertyChanged();
            RebuildGroupTree();
            ScheduleSaveSettings();
        }
    }

    /// <summary>Цвет фона заголовка узла «Закреплённые».</summary>
    public string PinnedColor
    {
        get => _pinnedColor;
        set
        {
            if (_pinnedColor == value) return;
            _pinnedColor = value;
            OnPropertyChanged();
            RebuildGroupTree();
            ScheduleSaveSettings();
        }
    }

    /// <summary>Цвет иконки узла «Закреплённые».</summary>
    public string PinnedIconColor
    {
        get => _pinnedIconColor;
        set
        {
            if (_pinnedIconColor == value) return;
            _pinnedIconColor = value;
            OnPropertyChanged();
            RebuildGroupTree();
            ScheduleSaveSettings();
        }
    }

    /// <summary>Ключ иконки узла «Закреплённые» (пусто — по умолчанию).</summary>
    public string PinnedIcon
    {
        get => _pinnedIcon;
        set
        {
            if (_pinnedIcon == value) return;
            _pinnedIcon = value;
            OnPropertyChanged();
            RebuildGroupTree();
            ScheduleSaveSettings();
        }
    }

    /// <summary>Текст кнопки переключения отображения групп.</summary>
    public string GroupByGroupText => _groupByGroup ? LocalizationManager.T("Main.HideGroups") : LocalizationManager.T("Main.ShowGroups");


    /// <summary>Список групп информационных баз.</summary>
    public ObservableCollection<Group> Groups { get; }

    /// <summary>Название сохранённой темы оформления (пусто, если тема не сохранялась).</summary>
    public string SavedTheme => _savedTheme;

    /// <summary>Активная цветовая схема (тема оформления).</summary>
    public ColorScheme ActiveColorScheme => _activeColorScheme ?? ColorScheme.CreateLight();

    /// <summary>Семейство шрифта интерфейса.</summary>
    public string FontFamily => _fontFamily;

    /// <summary>Размер шрифта интерфейса.</summary>
    public double FontSize => _fontSize;

    /// <summary>Начертание шрифта интерфейса («Normal»/«Bold»).</summary>
    public string FontWeight => _fontWeight;

    /// <summary>Стиль шрифта интерфейса («Normal»/«Italic»).</summary>
    public string FontStyle => _fontStyle;

    /// <summary>Индивидуальные настройки шрифта отдельных областей интерфейса.</summary>
    public IReadOnlyDictionary<string, Models.ElementFontSettings> ElementFonts => _elementFonts;

    /// <summary>Список установленных версий платформы 1С.</summary>
    public List<string> InstalledPlatformVersions => _installedPlatformVersions;

    /// <summary>
    /// Дополнительные пути к каталогам установки платформы 1С.
    /// </summary>
    public List<string> AdditionalPlatformSearchPaths => _additionalPlatformSearchPaths;

    /// <summary>
    /// Сохраняет список установленных версий платформы 1С.
    /// </summary>
    public void SetInstalledPlatformVersions(IEnumerable<string> versions)
    {
        _installedPlatformVersions = new List<string>(versions);
        SaveSettings();
    }

    /// <summary>
    /// Запускает фоновое сканирование установленных версий платформы 1С (Program Files + доп.
    /// пути) и обновляет <see cref="InstalledPlatformVersions"/>, когда результат готов. Выполняется
    /// в фоне, чтобы рекурсивный обход каталогов не задерживал показ главного окна. При сбое или
    /// пустом результате оставляет текущий (сохранённый из настроек) список.
    /// </summary>
    private async System.Threading.Tasks.Task RefreshInstalledPlatformVersionsInBackground()
    {
        // Снимок дополнительных путей на момент запуска сканирования.
        var additionalPaths = _additionalPlatformSearchPaths.ToList();
        try
        {
            var found = await System.Threading.Tasks.Task.Run(
                () => PlatformVersionService.FindInstalledVersions(additionalPaths));
            if (found.Count > 0)
            {
                _installedPlatformVersions = found;
                OnPropertyChanged(nameof(InstalledPlatformVersions));
            }
        }
        catch
        {
            // Сканирование не должно мешать работе приложения; остаётся сохранённый список.
        }
    }

    /// <summary>
    /// Сохраняет дополнительные пути поиска платформы и применяет их к сервису.
    /// </summary>
    public void SetAdditionalPlatformSearchPaths(IEnumerable<string> paths)
    {
        _additionalPlatformSearchPaths = paths?
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? new List<string>();
        PlatformVersionService.SetAdditionalSearchPaths(_additionalPlatformSearchPaths);
        SaveSettings();
    }

    /// <summary>Режим синхронизации с файлом ibases.v8i.</summary>
    public IbasesSyncMode IbasesSyncMode => _ibasesSyncMode;

    /// <summary>Путь к файлу ibases.v8i для синхронизации.</summary>
    public string IbasesSyncFilePath => _ibasesSyncFilePath;

    /// <summary>Момент запуска автоматической синхронизации с файлом ibases.v8i.</summary>
    public IbasesSyncTrigger IbasesSyncTrigger => _ibasesSyncTrigger;

    /// <summary>Интервал автоматической синхронизации в минутах.</summary>
    public int IbasesSyncIntervalMinutes => _ibasesSyncIntervalMinutes;

    /// <summary>Время автоматической синхронизации по расписанию (HH:mm).</summary>
    public string IbasesSyncScheduleTime => _ibasesSyncScheduleTime;

    /// <summary>Создавать резервные копии ibases.v8i перед записью.</summary>
    public bool IbasesBackupEnabled => _ibasesBackupEnabled;

    /// <summary>Число хранимых резервных копий ibases.v8i.</summary>
    public int IbasesBackupKeepCount => _ibasesBackupKeepCount;

    /// <summary>
    /// Добавлять дату-время к имени файла при выгрузке (экспорт списка баз в JSON,
    /// выгрузка ИБ в .dt, выгрузка конфигурации в .cf).
    /// </summary>
    public bool AddTimestampToExportFileName => _addTimestampToExportFileName;

    /// <summary>
    /// Применяет настройку добавления даты-времени к имени файла при выгрузке.
    /// </summary>
    public void ApplyExportFileNameSettings(bool addTimestamp)
    {
        if (_addTimestampToExportFileName == addTimestamp)
            return;
        _addTimestampToExportFileName = addTimestamp;
        SaveSettings();
    }

    /// <summary>
    /// Шаблон (формат) отметки даты и времени для имени файла при выгрузке.
    /// </summary>
    public string ExportTimestampFormat => _exportTimestampFormat;

    /// <summary>
    /// Применяет шаблон (формат) даты и времени для имени файла при выгрузке.
    /// </summary>
    public void ApplyExportTimestampFormat(string format)
    {
        format = string.IsNullOrWhiteSpace(format) ? "yyyyMMdd_HHmmss" : format.Trim();
        if (string.Equals(_exportTimestampFormat, format, StringComparison.Ordinal))
            return;
        _exportTimestampFormat = format;
        SaveSettings();
    }

    /// <summary>
    /// Текст сообщения о последней выполненной синхронизации с файлом ibases.v8i
    /// (что было обновлено и в какое время). Выводится в строку состояния главного окна.
    /// </summary>
    public string SyncMessage
    {
        get => _syncMessage;
        private set
        {
            if (!SetProperty(ref _syncMessage, value))
                return;
            ScheduleClearSyncMessage();
        }
    }

    private CancellationTokenSource? _syncMessageCts;

    /// <summary>Сообщение о синхронизации скрывается через 10 секунд.</summary>
    private void ScheduleClearSyncMessage()
    {
        _syncMessageCts?.Cancel();
        _syncMessageCts?.Dispose();
        _syncMessageCts = null;

        if (string.IsNullOrEmpty(_syncMessage))
            return;

        var cts = new CancellationTokenSource();
        _syncMessageCts = cts;
        var token = cts.Token;
        _ = ClearSyncMessageAfterDelayAsync(token);
    }

    private async Task ClearSyncMessageAfterDelayAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), token).ConfigureAwait(true);
            if (!token.IsCancellationRequested)
            {
                _syncMessage = string.Empty;
                OnPropertyChanged(nameof(SyncMessage));
            }
        }
        catch (TaskCanceledException)
        {
            /* новая синхронизация */
        }
    }

    /// <summary>Признак того, что автоматическая синхронизация запущена по интервалу/расписанию.</summary>
    public bool IsAutoSyncRunning => _syncTimerRunning;

    /// <summary>
    /// Применяет настройки синхронизации с файлом ibases.v8i, заданные в окне настроек.
    /// </summary>
    public void ApplyIbasesSyncSettings(IbasesSyncMode mode, string filePath,
        IbasesSyncTrigger trigger, int intervalMinutes, string scheduleTime,
        bool backupEnabled = true, int backupKeepCount = 5)
    {
        _ibasesSyncMode = mode;
        _ibasesSyncFilePath = filePath ?? string.Empty;
        _ibasesSyncTrigger = trigger;
        _ibasesSyncIntervalMinutes = intervalMinutes;
        _ibasesSyncScheduleTime = scheduleTime ?? string.Empty;
        _ibasesBackupEnabled = backupEnabled;
        _ibasesBackupKeepCount = backupKeepCount > 0 ? backupKeepCount : 5;
        SaveSettings();
        RestartAutoSync();
    }

    // ---- Профиль: резервное копирование и восстановление ----

    /// <summary>Каталог резервной копии профиля (настройки, базы, пользователи/пароли, ibases.v8i).</summary>
    public string ProfileBackupDirectory => _profileBackupDirectory;

    /// <summary>Восстанавливать профиль из каталога резервной копии при каждом запуске.</summary>
    public bool ProfileRestoreOnStartup => _profileRestoreOnStartup;

    /// <summary>Применяет настройки резервного копирования профиля из окна настроек.</summary>
    public void ApplyProfileBackupSettings(string backupDirectory, bool restoreOnStartup)
    {
        _profileBackupDirectory = backupDirectory?.Trim() ?? string.Empty;
        _profileRestoreOnStartup = restoreOnStartup;
        SaveSettings();
    }

    /// <summary>
    /// Сохраняет текущий профиль (настройки, список баз с пользователями и паролями,
    /// группы, ibases.v8i) в настроенный каталог. Возвращает true при успехе.
    /// </summary>
    public bool BackupProfile()
    {
        if (string.IsNullOrWhiteSpace(_profileBackupDirectory))
        {
            _dialogs.ShowWarning(LocalizationManager.T("Settings.Profile.NoDirectory"));
            return false;
        }
        try
        {
            var count = ProfileBackupService.Backup(_profileBackupDirectory, _ibasesSyncFilePath);
            _logger.Info($"Резервная копия профиля сохранена в {_profileBackupDirectory} ({count} файлов)");
            _dialogs.ShowInfo(string.Format(LocalizationManager.T("Settings.Profile.BackupDone"), count));
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error("Ошибка резервного копирования профиля", ex);
            _dialogs.ShowError(string.Format(LocalizationManager.T("Settings.Profile.BackupFailed"), ex.Message));
            return false;
        }
    }

    /// <summary>
    /// Восстанавливает профиль из настроенного каталога. Файлы копируются на диск;
    /// для полного применения рекомендуется перезапуск приложения (либо включённое
    /// восстановление при запуске). Возвращает true при успехе.
    /// </summary>
    public bool RestoreProfile()
    {
        if (string.IsNullOrWhiteSpace(_profileBackupDirectory))
        {
            _dialogs.ShowWarning(LocalizationManager.T("Settings.Profile.NoDirectory"));
            return false;
        }
        if (!ProfileBackupService.HasBackup(_profileBackupDirectory))
        {
            _dialogs.ShowWarning(LocalizationManager.T("Settings.Profile.NoBackup"));
            return false;
        }
        try
        {
            var count = ProfileBackupService.Restore(_profileBackupDirectory, _ibasesSyncFilePath);
            _logger.Info($"Профиль восстановлен из {_profileBackupDirectory} ({count} файлов)");
            _dialogs.ShowInfo(string.Format(LocalizationManager.T("Settings.Profile.RestoreDone"), count));
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error("Ошибка восстановления профиля", ex);
            _dialogs.ShowError(string.Format(LocalizationManager.T("Settings.Profile.RestoreFailed"), ex.Message));
            return false;
        }
    }

    /// <summary>
    /// После локального изменения базы: выгрузка в ibases.v8i без импорта,
    /// чтобы не перезатереть только что заданные настройки (режим запуска и т.д.).
    /// </summary>
    private void ExportToIbasesAfterLocalChange()
    {
        if (_ibasesSyncMode is not (IbasesSyncMode.Export or IbasesSyncMode.Both))
            return;

        var filePath = ResolveIbasesFilePath();
        if (filePath is null)
            return;

        try
        {
            if (_ibasesBackupEnabled && File.Exists(filePath))
            {
                try { IbasesBackupService.CreateBackup(filePath, _ibasesBackupKeepCount); }
                catch { /* не блокируем сохранение */ }
            }

            var result = _ibasesSync.Export(filePath, Infobases, Groups);
            var text = BuildSyncMessage(LocalizationManager.T("Sync.PrefixExported"), result);
            if (!string.IsNullOrEmpty(text))
                SyncMessage = $"{DateTime.Now:HH:mm:ss} — {text}";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Экспорт ibases после правки: {ex}");
            SyncMessage = string.Format(LocalizationManager.T("Sync.ExportError"), ex.Message);
        }
    }

    /// <summary>
    /// Выполняет синхронизацию с файлом ibases.v8i в соответствии с заданным режимом.
    /// В режимах с импортом загружает новые базы из файла, в режимах с экспортом —
    /// выгружает базы приложения в файл. При наличии изменений формирует сообщение
    /// о том, что было обновлено и в какое время, и выводит его в строку состояния.
    /// </summary>
    /// <returns>True, если была выполнена хотя бы одна операция синхронизации.</returns>
    public bool SynchronizeWithIbases()
    {
        if (_ibasesSyncMode == IbasesSyncMode.None)
            return false;

        var filePath = ResolveIbasesFilePath();
        if (filePath is null)
            return false;

        var importPerformed = _ibasesSyncMode is IbasesSyncMode.Import or IbasesSyncMode.Both;
        var exportPerformed = _ibasesSyncMode is IbasesSyncMode.Export or IbasesSyncMode.Both;

        var message = string.Empty;

        // В двустороннем режиме сначала выгрузка (удаления из приложения попадают в файл),
        // затем загрузка (удаления из стартера 1С убираются из приложения).
        void DoExport()
        {
            try
            {
                if (_ibasesBackupEnabled && File.Exists(filePath))
                {
                    try
                    {
                        var bak = IbasesBackupService.CreateBackup(filePath, _ibasesBackupKeepCount);
                        if (bak is not null)
                            _logger.Info($"Создана резервная копия ibases.v8i: {bak}");
                    }
                    catch (Exception bakEx)
                    {
                        _logger.Error("Не удалось создать резервную копию ibases.v8i", bakEx);
                    }
                }

                var result = _ibasesSync.Export(filePath, Infobases, Groups);
                var exportText = BuildSyncMessage(LocalizationManager.T("Sync.PrefixExported"), result);
                if (!string.IsNullOrEmpty(exportText))
                {
                    message = string.IsNullOrEmpty(message)
                        ? exportText
                        : message + "; " + exportText;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Авто-экспорт ibases.v8i: {ex}");
                var err = string.Format(LocalizationManager.T("Sync.ExportError"), ex.Message);
                SyncMessage = string.IsNullOrEmpty(SyncMessage) ? err : SyncMessage + "; " + err;
            }
        }

        void DoImport()
        {
            if (!File.Exists(filePath))
                return;
            try
            {
                var result = _ibasesSync.Import(filePath, Infobases, Groups);
                InfobasesView.Refresh();
                Save();
                SaveGroups();
                RebuildGroupTree();
                var importText = BuildSyncMessage(LocalizationManager.T("Sync.PrefixImported"), result);
                if (!string.IsNullOrEmpty(importText))
                {
                    message = string.IsNullOrEmpty(message)
                        ? importText
                        : message + "; " + importText;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Авто-импорт ibases.v8i: {ex}");
                SyncMessage = string.Format(LocalizationManager.T("Sync.ImportError"), ex.Message);
            }
        }

        if (_ibasesSyncMode == IbasesSyncMode.Both)
        {
            if (exportPerformed) DoExport();
            if (importPerformed) DoImport();
        }
        else
        {
            if (importPerformed) DoImport();
            if (exportPerformed) DoExport();
        }

        if (!string.IsNullOrEmpty(message))
        {
            SyncMessage = $"{DateTime.Now:HH:mm:ss} — {message}";
        }

        return importPerformed || exportPerformed;
    }

}

/// <summary>Элемент панели тегов с признаком выбора.</summary>
public sealed class TagFilterItem
{
    public TagFilterItem(string name, bool isSelected)
    {
        Name = name;
        IsSelected = isSelected;
    }

    public string Name { get; }
    public bool IsSelected { get; }
}
#endif
