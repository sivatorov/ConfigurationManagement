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
/// Разбит на partial-файлы по ответственности, зеркально WPF-версии:
/// <see cref="MainViewModel.Avalonia.Commands"/> (.Commands.cs), .Display.cs,
/// .Launch.cs, .SwitchUser.cs, .Sync.cs, .Theme.cs, .Tools.cs.
/// </summary>
public partial class MainViewModel : ViewModelBase
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
    private Avalonia.Threading.DispatcherTimer? _listStateAutoSaveTimer;
    private bool _listStateDirty;

    private AppSettings _settings = new();

    /// <summary>Глобальное действие по двойному щелчку на базе (функция №28 StartManager).</summary>
    private string _defaultDoubleClickAction = DoubleClickAction.GlobalDefault;

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

    private string _sessionClient = "Авто";
    private string _sessionArch = "Авто";

    private bool _isExporting;
    private string _exportIndicatorTooltip = string.Empty;

    private readonly LaunchViewModel _launchVm;

    private bool _isLoading;
    private string _loadingMessage = string.Empty;

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

    /// <summary>Логин учётной записи сайта 1С для авторизации (HTTP Basic Auth) при проверке обновлений конфигураций.</summary>
    public string UpdatesLogin
    {
        get => _settings.UpdatesLogin ?? "";
        set
        {
            var v = value ?? string.Empty;
            if (string.Equals(_settings.UpdatesLogin, v, StringComparison.Ordinal))
                return;
            _settings.UpdatesLogin = v;
            SaveSettingsSilently();
        }
    }

    /// <summary>Пароль учётной записи сайта 1С для авторизации (HTTP Basic Auth) при проверке обновлений конфигураций.</summary>
    public string UpdatesPassword
    {
        get => _settings.UpdatesPassword ?? "";
        set
        {
            var v = value ?? string.Empty;
            if (string.Equals(_settings.UpdatesPassword, v, StringComparison.Ordinal))
                return;
            _settings.UpdatesPassword = v;
            SaveSettingsSilently();
        }
    }

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
        // Служба обновления живёт до перезапуска, а кнопка «Проверить обновления» стоит
        // в том же окне настроек: без этого снятая галка вступала бы в силу только
        // со следующего запуска.
        AppServices.GetRequiredService<UpdateService>().ApplyAutoUpdatePolicy(autoUpdateEnabled);
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

    /// <summary>Текущее глобальное действие по двойному щелчку на базе (функция №28 StartManager).</summary>
    public string DefaultDoubleClickAction => _defaultDoubleClickAction;

    /// <summary>
    /// Устанавливает глобальное действие по двойному щелчку на базе и сохраняет настройки.
    /// </summary>
    public void SetDefaultDoubleClickAction(string value)
    {
        var normalized = DoubleClickAction.Normalize(value);
        if (string.Equals(_defaultDoubleClickAction, normalized, StringComparison.Ordinal))
            return;
        _defaultDoubleClickAction = normalized;
        _settings.DefaultDoubleClickAction = normalized;
        SaveSettingsSilently();
    }

    /// <summary>
    /// Определяет действие по двойному щелчку для конкретной базы (функция №28 StartManager):
    /// индивидуальное значение ИБ имеет приоритет; если оно пусто — глобальная настройка.
    /// </summary>
    public string ResolveDoubleClickAction(Infobase? infobase)
    {
        var perBase = (infobase?.DoubleClickAction ?? string.Empty).Trim();
        return string.IsNullOrEmpty(perBase)
            ? _defaultDoubleClickAction
            : DoubleClickAction.Normalize(perBase);
    }

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

            // Режим функциональности и параметры 1CLaunch.cfg (Этап 10 StartManager).
            LoadFunctionalSettings();

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
            // Глобальное действие по двойному щелчку на базе (функция №28 StartManager).
            _defaultDoubleClickAction = DoubleClickAction.Normalize(_settings.DefaultDoubleClickAction);
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

            // Дата изменений файла ИБ для колонки «Дата изменений» (Этап 13) считается
            // в фоне после показа дерева: дисковые обращения не должны задерживать старт.
            RefreshFileModifiedTimesInBackground();

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
            StartListStateAutoSave();

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

    /// <summary>
    /// Считает дату последнего изменения файла ИБ для колонки «Дата изменений» (Этап 13)
    /// в фоне после показа дерева: дисковые обращения не должны задерживать старт.
    /// Для каждой файловой базы определяется время записи файла базы (1Cv8.1CD) и
    /// помещается в <see cref="Infobase.FileLastWriteTimeUtc"/>, что обновляет колонку.
    /// </summary>
    private async void RefreshFileModifiedTimesInBackground()
    {
        var fileBases = _allInfobases.Where(ib => ib.Connection.Type == ConnectionType.File).ToList();
        if (fileBases.Count == 0)
            return;

        var results = await System.Threading.Tasks.Task.Run(() =>
        {
            var map = new Dictionary<Infobase, DateTime?>(fileBases.Count);
            foreach (var ib in fileBases)
                map[ib] = CalculateFileLastWriteTimeUtc(ib);
            return map;
        });

        foreach (var kv in results)
            kv.Key.FileLastWriteTimeUtc = kv.Value;
    }

    /// <summary>Время последней записи файловой ИБ (UTC) или null, если определить не удалось.</summary>
    private static DateTime? CalculateFileLastWriteTimeUtc(Infobase ib)
    {
        try
        {
            var path = ib.Connection.FilePath?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(path))
                return null;
            // Маркер актуальности: для каталога берём время записи самого файла базы
            // 1Cv8.1CD (оно меняется при изменении данных базы), иначе — файла/каталога.
            string marker;
            if (System.IO.File.Exists(path))
                marker = path;
            else
            {
                var dbFile = System.IO.Path.Combine(path, "1Cv8.1CD");
                marker = System.IO.File.Exists(dbFile) ? dbFile : path;
            }
            return System.IO.File.Exists(marker)
                ? System.IO.File.GetLastWriteTimeUtc(marker)
                : System.IO.Directory.GetLastWriteTimeUtc(path);
        }
        catch
        {
            return null;
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

    /// <summary>
    /// «Найти в списке» (issue #285) завершил переход: цель выставлена и дерево пересобрано.
    /// Окну нужно показать строку цели, а не восстанавливать прежнюю позицию прокрутки.
    /// </summary>
    public event Action? RevealFindInListRequested;

    /// <summary>Состав списка обновлён: окну нужно вернуть выделение строки и прокрутку.</summary>
    public event Action? TreeRebuilt;

    /// <summary>
    /// Модальное окно свойств базы вот-вот откроется: окну нужно запомнить позицию
    /// прокрутки, чтобы вернуть её даже если пользователь закроет окно без сохранения
    /// («Нет») и пересборки не будет (issue #252).
    /// </summary>
    public event Action? TreeModalOpening;

    /// <summary>
    /// Модальное окно свойств базы закрылось без сохранения («Нет»): пересборки дерева
    /// не было, событий <see cref="TreeRebuilding"/>/<see cref="TreeRebuilt"/> не случилось,
    /// а закрытие модального окна само подтягивает выбранную строку в видимую область.
    /// Окну нужно вернуть прежнюю позицию прокрутки явно (issue #252).
    /// </summary>
    public event Action? TreeModalClosed;

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
        // Служебным узлам передаём их собственные цвета по умолчанию (issue #240),
        // иначе заданное оформление не применяется к узлу на Linux/Avalonia.
        var pinnedNode = new GroupNodeViewModel(
            null,
            marker: GroupNodeViewModel.PinnedMarker,
            defaultColor: _settings.PinnedColor,
            defaultIconColor: _settings.PinnedIconColor,
            defaultIcon: _settings.PinnedIcon ?? string.Empty);
        var noGroupNode = new GroupNodeViewModel(
            null,
            marker: GroupNodeViewModel.NoGroupMarker,
            defaultColor: _settings.NoGroupColor,
            defaultIconColor: _settings.NoGroupIconColor,
            defaultIcon: _settings.NoGroupIcon ?? string.Empty);

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
        {
            MarkListStateDirty();
            PersistCollapsedGroups();
        }
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

    private void EditInfobase(Infobase? target = null)
    {
        var ib = target ?? SelectedInfobase;
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
                AvailableServers(), AvailablePorts(),
                // Существующие теги всех баз — для автодополнения при добавлении (issue #283).
                _allInfobases.SelectMany(i => i.Tags)
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Distinct(StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            _logger.Error($"Не удалось открыть окно свойств базы: {ex.Message}", ex);
            return;
        }

        // Перед показом модального окна запоминаем позицию прокрутки списка: даже если
        // пользователь закроет окно без сохранения («Нет») и пересборки не будет, мы сможем
        // вернуть прежнюю позицию — иначе при закрытии модального окна Avalonia сама
        // подтягивает выбранную строку в видимую область и список «уезжает» (issue #252).
        TreeModalOpening?.Invoke();

        if (!dialog.ShowDialogSync(OwnerWindow()))
        {
            // Окно закрыто без сохранения («Нет») — пересборки дерева не было, поэтому
            // события TreeRebuilding/TreeRebuilt не сработали и позиция не восстановилась.
            // Возвращаем прежнюю позицию прокрутки явно (issue #252).
            TreeModalClosed?.Invoke();
            return;
        }

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
        // Внешняя обработка при запуске и действие по двойному щелчку (Этап 7).
        ib.DoubleClickAction = dialog.Result.DoubleClickAction;
        ib.ExternalProcessingPath = dialog.Result.ExternalProcessingPath;
        ib.ExternalProcessingData = dialog.Result.ExternalProcessingData;
        ib.ClientType = dialog.Result.ClientType;
        ib.IsFavorite = dialog.Result.IsFavorite;
        ib.IsPinned = dialog.Result.IsPinned;
        ib.LastLaunchDate = dialog.Result.LastLaunchDate;
        ib.Tags = dialog.Result.Tags;
        ib.MetadataRoot = dialog.Result.MetadataRoot;
        // Ручной размер базы переносим явно, иначе введённый вручную размер терялся
        // при сохранении (поле редактируется в окне подключения, issue #243).
        ib.ManualSizeBytes = dialog.Result.ManualSizeBytes;
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
                AvailableServers(), AvailablePorts(),
                // Существующие теги всех баз — для автодополнения при добавлении (issue #283).
                _allInfobases.SelectMany(i => i.Tags)
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Distinct(StringComparer.OrdinalIgnoreCase));
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

    private bool SaveList(List<Infobase> infobases)
    {
        try { _repository.Save(infobases); return true; }
        catch (Exception ex) { _logger.Error("Не удалось сохранить список баз", ex); return false; }
    }

    private bool SaveGroupList(List<Group> groups)
    {
        try { _repository.SaveGroups(groups); return true; }
        catch (Exception ex) { _logger.Error("Не удалось сохранить группы", ex); return false; }
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

        MarkListStateDirty();
        PersistCollapsedGroups();
    }

    private void PersistCollapsedGroups()
    {
        _settings.CollapsedGroups = _collapsedGroups.ToList();
        SaveSettingsSilently();
    }

    /// <summary>
    /// Запускает периодическое автосохранение состояния списка (раскрытых и
    /// свёрнутых групп). Таймер работает, только когда автосохранение включено
    /// в настройках; по тику сохраняет на диск группы, если они менялись с
    /// прошлого сохранения. При выключенной настройке таймер останавливается.
    /// </summary>
    private void StartListStateAutoSave()
    {
        if (_settings.AutoSaveListState)
        {
            if (_listStateAutoSaveTimer is not null)
                return;
            _listStateAutoSaveTimer = new Avalonia.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(Math.Max(1, _settings.ListStateAutoSaveIntervalSeconds))
            };
            _listStateAutoSaveTimer.Tick += OnListStateAutoSaveTick;
            _listStateAutoSaveTimer.Start();
        }
        else if (_listStateAutoSaveTimer is not null)
        {
            _listStateAutoSaveTimer.Stop();
            _listStateAutoSaveTimer.Tick -= OnListStateAutoSaveTick;
            _listStateAutoSaveTimer = null;
        }
    }

    private void OnListStateAutoSaveTick(object? sender, System.EventArgs e)
        => SaveListStateIfDirty();

    /// <summary>Сохраняет свёрнутые группы, только если они менялись с последнего сохранения.</summary>
    private void SaveListStateIfDirty()
    {
        if (!_listStateDirty)
            return;
        _listStateDirty = false;
        PersistCollapsedGroups();
    }

    /// <summary>Помечает состояние списка изменившимся: его сохранит следующий тик таймера автосохранения.</summary>
    private void MarkListStateDirty()
    {
        _listStateDirty = true;
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
    /// Редактирует оформление служебного узла «Без группы» (цвет и иконку) по аналогии
    /// с обычной группой (issue #240). Изменения сохраняются в настройках приложения
    /// и применяются к узлу при пересборке дерева.
    /// </summary>
    private void EditNoGroupNode()
    {
        Configuration_Management.GroupEditWindow dialog;
        try
        {
            dialog = new Configuration_Management.GroupEditWindow(
                _groups,
                _settings.NoGroupColor,
                _settings.NoGroupIconColor,
                _settings.NoGroupIcon ?? string.Empty);
        }
        catch (Exception ex)
        {
            _logger.Error($"Не удалось открыть окно оформления узла «Без группы»: {ex.Message}", ex);
            return;
        }

        if (!dialog.ShowDialogSync(OwnerWindow()))
            return;

        _settings.NoGroupColor = string.IsNullOrWhiteSpace(dialog.Result.Color) ? "#6B7280" : dialog.Result.Color;
        _settings.NoGroupIconColor = string.IsNullOrWhiteSpace(dialog.Result.IconColor) ? "#FFFFFF" : dialog.Result.IconColor;
        _settings.NoGroupIcon = dialog.Result.Icon ?? string.Empty;

        RebuildTree();
        SaveSettingsSilently();
    }

    /// <summary>
    /// Редактирует оформление служебного узла «Закреплённые» (цвет и иконку) по аналогии
    /// с узлом «Без группы». Изменения сохраняются в настройках приложения.
    /// </summary>
    private void EditPinnedNode()
    {
        Configuration_Management.GroupEditWindow dialog;
        try
        {
            dialog = new Configuration_Management.GroupEditWindow(
                _groups,
                _settings.PinnedColor,
                _settings.PinnedIconColor,
                _settings.PinnedIcon ?? string.Empty);
        }
        catch (Exception ex)
        {
            _logger.Error($"Не удалось открыть окно оформления узла «Закреплённые»: {ex.Message}", ex);
            return;
        }

        if (!dialog.ShowDialogSync(OwnerWindow()))
            return;

        _settings.PinnedColor = string.IsNullOrWhiteSpace(dialog.Result.Color) ? "#8B5CF6" : dialog.Result.Color;
        _settings.PinnedIconColor = string.IsNullOrWhiteSpace(dialog.Result.IconColor) ? "#FFFFFF" : dialog.Result.IconColor;
        _settings.PinnedIcon = dialog.Result.Icon ?? string.Empty;

        RebuildTree();
        SaveSettingsSilently();
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
                MarkListStateDirty();
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
            MarkListStateDirty();
            PersistCollapsedGroups();
        }
    }

    // ======================= Сохранение =======================

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
}
#endif