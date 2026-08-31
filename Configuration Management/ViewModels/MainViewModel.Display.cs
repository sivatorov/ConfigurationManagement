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

    /// <summary>Ширина колонки «Название» (0 — по умолчанию).</summary>
    public double NameColumnWidth
    {
        get => _nameColumnWidth;
        private set
        {
            if (_nameColumnWidth != value)
            {
                _nameColumnWidth = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Ширина колонки «Версия платформы» (0 — по умолчанию).</summary>
    public double VersionColumnWidth
    {
        get => _versionColumnWidth;
        private set
        {
            if (_versionColumnWidth != value)
            {
                _versionColumnWidth = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Ширина колонки «Режим запуска» (0 — по умолчанию).</summary>
    public double LaunchModeColumnWidth
    {
        get => _launchModeColumnWidth;
        private set
        {
            if (_launchModeColumnWidth != value)
            {
                _launchModeColumnWidth = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Ширина колонки «Сервер/База» (0 — по умолчанию).</summary>
    public double ServerColumnWidth
    {
        get => _serverColumnWidth;
        private set
        {
            if (_serverColumnWidth != value)
            {
                _serverColumnWidth = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Ширина колонки «Последний запуск» (0 — по умолчанию).</summary>
    public double LastLaunchColumnWidth
    {
        get => _lastLaunchColumnWidth;
        private set
        {
            if (_lastLaunchColumnWidth != value)
            {
                _lastLaunchColumnWidth = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Показывать колонку-кнопку «Избранное» (★) в списке баз.</summary>
    public bool ShowFavoritesButton => _showFavoritesButton;

    /// <summary>Показывать колонку-кнопку «Закрепить» (📌) в списке баз.</summary>
    public bool ShowPinnedButton => _showPinnedButton;

    /// <summary>Показывать теги баз в списке (кнопка тегов в заголовке списка баз).</summary>
    public bool ShowTags
    {
        get => _showTags;
        set
        {
            if (SetProperty(ref _showTags, value))
                ScheduleSaveSettings();
            // При отключении показа тегов (верхняя кнопка «Показывать теги»)
            // сбрасываем и активные фильтры по тегам для списка баз,
            // иначе отбор по тегу продолжает скрывать базы без тегов.
            if (!value && _activeTagFilters.Count > 0)
                ClearTagFilters(null);
        }
    }

    /// <summary>Показывать панель быстрого отбора по тегам.</summary>
    public bool ShowTagFilterPanel
    {
        get => _showTagFilterPanel;
        set
        {
            if (SetProperty(ref _showTagFilterPanel, value))
                ScheduleSaveSettings();
        }
    }

    /// <summary>Разрешить несколько экземпляров приложения.</summary>
    public bool AllowMultipleInstances => _allowMultipleInstances;

    /// <summary>Проверять наличие обновлений приложения при запуске (GitHub Releases).</summary>
    public bool CheckForUpdatesOnStartup => _checkForUpdatesOnStartup;

    /// <summary>Автоматически устанавливать новые версии без подтверждения (self-update при запуске).</summary>
    public bool AutoUpdateEnabled => _autoUpdateEnabled;

    /// <summary>Выбранные теги для фильтра (можно несколько одновременно).</summary>
    public ObservableCollection<string> ActiveTagFilters => _activeTagFilters;

    /// <summary>Есть ли активный фильтр по тегам.</summary>
    public bool HasActiveTagFilter => _activeTagFilters.Count > 0;

    /// <summary>Режим списка: Все / Избранное / Недавние.</summary>
    public ListViewMode ListViewMode
    {
        get => _listViewMode;
        set
        {
            if (SetProperty(ref _listViewMode, value))
            {
                // Совместимость с прежним флагом избранного.
                _showFavoritesOnly = value == ListViewMode.Favorites;
                OnPropertyChanged(nameof(ShowFavoritesOnly));
                OnPropertyChanged(nameof(IsListModeAll));
                OnPropertyChanged(nameof(IsListModeFavorites));
                OnPropertyChanged(nameof(IsListModeRecent));
                RebuildGroupTree();
            }
        }
    }

    public bool IsListModeAll
    {
        get => _listViewMode == ListViewMode.All;
        set { if (value) ListViewMode = ListViewMode.All; }
    }

    public bool IsListModeFavorites
    {
        get => _listViewMode == ListViewMode.Favorites;
        set { if (value) ListViewMode = ListViewMode.Favorites; }
    }

    public bool IsListModeRecent
    {
        get => _listViewMode == ListViewMode.Recent;
        set { if (value) ListViewMode = ListViewMode.Recent; }
    }

    /// <summary>Проверяет, выбран ли тег в фильтре.</summary>
    public bool IsTagSelected(string tag) =>
        !string.IsNullOrEmpty(tag) && _activeTagFilters.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Уникальные теги всех баз для панели быстрого отбора.
    /// </summary>
    public IEnumerable<string> AvailableTags =>
        Infobases
            .SelectMany(i => i.Tags)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase);

    private readonly ObservableCollection<TagFilterItem> _tagFilterItems = new();

    /// <summary>Теги с признаком выбора для панели фильтров.</summary>
    public ObservableCollection<TagFilterItem> TagFilterItems => _tagFilterItems;

    /// <summary>Пересобирает облако тегов (панель фильтров).</summary>
    public void RefreshTagFilterItems()
    {
        var selected = new HashSet<string>(_activeTagFilters, StringComparer.OrdinalIgnoreCase);
        var tags = Infobases
            .SelectMany(i => i.Tags)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Не трогаем UI, если набор тегов и выделение не изменились.
        if (_tagFilterItems.Count == tags.Count)
        {
            var same = true;
            for (var i = 0; i < tags.Count; i++)
            {
                if (!string.Equals(_tagFilterItems[i].Name, tags[i], StringComparison.OrdinalIgnoreCase)
                    || _tagFilterItems[i].IsSelected != selected.Contains(tags[i]))
                {
                    same = false;
                    break;
                }
            }
            if (same)
                return;
        }

        _tagFilterItems.Clear();
        foreach (var t in tags)
            _tagFilterItems.Add(new TagFilterItem(t, selected.Contains(t)));
        OnPropertyChanged(nameof(HasActiveTagFilter));
    }

    /// <summary>
    /// Удаляет из активных фильтров теги, которых больше нет ни на одной базе
    /// (например, после удаления или переименования тега). Иначе отбор «зависает»:
    /// фильтр продолжает применяться, но чипа в панели отборов уже нет и снять его нельзя.
    /// </summary>
    private void PruneActiveTagFilters()
    {
        if (_activeTagFilters.Count == 0)
            return;

        var available = new HashSet<string>(
            Infobases.SelectMany(i => i.Tags),
            StringComparer.OrdinalIgnoreCase);

        var changed = false;
        for (var i = _activeTagFilters.Count - 1; i >= 0; i--)
        {
            if (!available.Contains(_activeTagFilters[i]))
            {
                _activeTagFilters.RemoveAt(i);
                changed = true;
            }
        }

        if (!changed)
            return;

        SyncActiveTagFilterSet();
        OnPropertyChanged(nameof(HasActiveTagFilter));
        // Набор активных фильтров изменился — пересобираем список/дерево баз,
        // иначе отбор по «исчезнувшему» тегу продолжал бы скрывать базы.
        RebuildGroupTree();
    }

    private void SyncActiveTagFilterSet()
    {
        _activeTagFilterSet = new HashSet<string>(_activeTagFilters, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Отложенная перестройка дерева (поиск по мере ввода).</summary>
    private void ScheduleRebuildGroupTree()
    {
        _searchDebounceCts?.Cancel();
        _searchDebounceCts?.Dispose();
        var cts = new CancellationTokenSource();
        _searchDebounceCts = cts;
        var token = cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                // Короче debounce — список реагирует быстрее при наборе.
                await Task.Delay(90, token).ConfigureAwait(false);
                if (token.IsCancellationRequested) return;
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher is null) return;
                // Loaded priority: после текущего ввода, до фоновой отрисовки.
                await dispatcher.InvokeAsync(() =>
                {
                    if (!token.IsCancellationRequested)
                        RebuildGroupTree();
                }, System.Windows.Threading.DispatcherPriority.DataBind);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.Error("Ошибка отложенной перестройки дерева", ex);
            }
        });
    }

    /// <summary>Показывать кнопки свернуть/развернуть все (только при группировке).</summary>
    public bool ShowExpandCollapseButtons => _groupByGroup;

    /// <summary>Показывать колонку «Версия платформы» в списке баз.</summary>
    public bool ShowVersionColumn => _showVersionColumn;

    /// <summary>Показывать колонку «Конфигурация».</summary>
    public bool ShowConfigurationColumn => _showConfigurationColumn;

    /// <summary>Ширина колонки «Конфигурация».</summary>
    public double ConfigurationColumnWidth
    {
        get => _configurationColumnWidth;
        set
        {
            if (_configurationColumnWidth != value)
            {
                _configurationColumnWidth = value;
                OnPropertyChanged();
                ScheduleSaveSettings();
            }
        }
    }

    /// <summary>Ширина колонки «Действия» в списке баз (0 — по умолчанию).</summary>
    public double ActionsColumnWidth
    {
        get => _actionsColumnWidth;
        set
        {
            if (_actionsColumnWidth != value)
            {
                _actionsColumnWidth = value;
                OnPropertyChanged();
                ScheduleSaveSettings();
            }
        }
    }

    /// <summary>Показывать подробности в правой панели (иначе — только кнопки).</summary>
    public bool ShowRightPanelDetails
    {
        get => _showRightPanelDetails;
        set
        {
            if (SetProperty(ref _showRightPanelDetails, value))
            {
                OnPropertyChanged(nameof(RightPanelToggleTooltip));
                ScheduleSaveSettings();
            }
        }
    }

    /// <summary>Показывать блок «Текущая сессия» в правой панели (полный и компактный режим).</summary>
    public bool ShowSessionLaunchPanel
    {
        get => _showSessionLaunchPanel;
        set
        {
            if (SetProperty(ref _showSessionLaunchPanel, value))
                ScheduleSaveSettings();
        }
    }

    public string RightPanelToggleTooltip =>
        _showRightPanelDetails ? LocalizationManager.T("Main.RightPanelHideDetails") : LocalizationManager.T("Main.RightPanelShowDetails");

    /// <summary>Тип клиента для текущего запуска (не пишется в настройки базы).</summary>
    public SessionClientMode SessionClientMode
    {
        get => _sessionClientMode;
        set
        {
            if (SetProperty(ref _sessionClientMode, value))
            {
                OnPropertyChanged(nameof(IsSessionClientAuto));
                OnPropertyChanged(nameof(IsSessionClientOrdinary));
                OnPropertyChanged(nameof(IsSessionClientThick));
                OnPropertyChanged(nameof(IsSessionClientThickOrdinary));
                OnPropertyChanged(nameof(IsSessionClientThin));
                ScheduleSaveSettings();
            }
        }
    }

    public bool IsSessionClientAuto
    {
        get => _sessionClientMode == SessionClientMode.Auto;
        set { if (value) SessionClientMode = SessionClientMode.Auto; }
    }

    public bool IsSessionClientOrdinary
    {
        get => _sessionClientMode == SessionClientMode.Ordinary;
        set { if (value) SessionClientMode = SessionClientMode.Ordinary; }
    }

    public bool IsSessionClientThick
    {
        get => _sessionClientMode == SessionClientMode.Thick;
        set { if (value) SessionClientMode = SessionClientMode.Thick; }
    }

    public bool IsSessionClientThin
    {
        get => _sessionClientMode == SessionClientMode.Thin;
        set { if (value) SessionClientMode = SessionClientMode.Thin; }
    }

    /// <summary>Разрядность для текущего запуска (не пишется в настройки базы).</summary>
    public SessionArchitectureMode SessionArchitecture
    {
        get => _sessionArchitecture;
        set
        {
            if (SetProperty(ref _sessionArchitecture, value))
            {
                OnPropertyChanged(nameof(IsSessionArchAuto));
                OnPropertyChanged(nameof(IsSessionArch32));
                OnPropertyChanged(nameof(IsSessionArch64));
                ScheduleSaveSettings();
            }
        }
    }

    public bool IsSessionArchAuto
    {
        get => _sessionArchitecture == SessionArchitectureMode.Auto;
        set { if (value) SessionArchitecture = SessionArchitectureMode.Auto; }
    }

    public bool IsSessionArch32
    {
        get => _sessionArchitecture == SessionArchitectureMode.X86;
        set { if (value) SessionArchitecture = SessionArchitectureMode.X86; }
    }

    public bool IsSessionArch64
    {
        get => _sessionArchitecture == SessionArchitectureMode.X64;
        set { if (value) SessionArchitecture = SessionArchitectureMode.X64; }
    }

    public bool IsSessionClientThickOrdinary
    {
        get => _sessionClientMode == SessionClientMode.ThickOrdinary;
        set { if (value) SessionClientMode = SessionClientMode.ThickOrdinary; }
    }

    /// <summary>Разрядность по умолчанию (X86 / X64), если у базы она не указана.</summary>
    public string DefaultArchitecture => _defaultArchitecture;

    /// <summary>
    /// Задаёт разрядность по умолчанию и немедленно применяет её для последующих запусков.
    /// </summary>
    public void ApplyDefaultArchitecture(string architecture)
    {
        _defaultArchitecture = ParseArchitecture(architecture) == OneCArchitecture.x64 ? "X64" : "X86";
        OneCLauncher.DefaultArchitecture = ParseArchitecture(_defaultArchitecture);
        SaveSettings();
    }

    /// <summary>Преобразует строку разрядности (X86/X64) в enum OneCArchitecture.</summary>
    private static OneCArchitecture ParseArchitecture(string value) =>
        string.Equals(value, "X64", StringComparison.OrdinalIgnoreCase)
            ? OneCArchitecture.x64
            : OneCArchitecture.x86;

    public bool StatusShowConnectionPath => _statusShowConnectionPath;
    public bool StatusShowArchitecture => _statusShowArchitecture;
    public bool StatusShowLaunchMode => _statusShowLaunchMode;
    public bool StatusShowPort => _statusShowPort;
    public bool StatusShowPlatformVersion => _statusShowPlatformVersion;
    public bool StatusShowClientType => _statusShowClientType;
    public bool StatusShowConnectionType => _statusShowConnectionType;
    public bool StatusShowUser => _statusShowUser;
    public bool StatusShowId => _statusShowId;

    /// <summary>Сводка для нижней строки состояния по выбранным в настройках полям.</summary>
    public string StatusBarInfo
    {
        get
        {
            var ib = SelectedInfobase;
            if (ib is null)
                return string.Empty;

            var parts = new List<string>();
            if (_statusShowConnectionType)
                parts.Add(ib.ConnectionTypeDisplay);
            if (_statusShowConnectionPath)
            {
                var path = ib.Connection.Type == ConnectionType.File
                    ? (string.IsNullOrWhiteSpace(ib.Connection.FilePath) ? "—" : ib.Connection.FilePath)
                    : ib.ServerDatabaseDisplay;
                if (!string.IsNullOrWhiteSpace(path))
                    parts.Add(path);
            }
            if (_statusShowPort && ib.Connection.Type == ConnectionType.ClientServer && ib.Connection.Port > 0)
                parts.Add($"{LocalizationManager.T("Main.StatusPort")} {ib.Connection.Port}");
            if (_statusShowPlatformVersion && !string.IsNullOrWhiteSpace(ib.PlatformVersion))
                parts.Add($"{LocalizationManager.T("Main.StatusPlatform")} {ib.PlatformVersion}");
            if (_statusShowArchitecture)
                parts.Add(ib.ArchitectureDisplay);
            if (_statusShowLaunchMode)
                parts.Add(ib.ParsedLaunchMode);
            if (_statusShowClientType && !string.IsNullOrWhiteSpace(ib.ClientType))
                parts.Add(ib.ClientTypeDisplay);
            if (_statusShowUser && !string.IsNullOrWhiteSpace(ib.Connection.User))
                parts.Add($"{LocalizationManager.T("Main.StatusUser")} {ib.Connection.User}");
            if (_statusShowId && !string.IsNullOrWhiteSpace(ib.Id))
                parts.Add($"ID {ib.Id}");

            return string.Join("  ·  ", parts);
        }
    }

    /// <summary>Показывать колонку «Режим запуска» в списке баз.</summary>
    public bool ShowLaunchModeColumn => _showLaunchModeColumn;

    /// <summary>Показывать колонку «Сервер/База» в списке баз.</summary>
    public bool ShowServerColumn => _showServerColumn;

    /// <summary>Показывать колонку «Последний запуск» в списке баз.</summary>
    public bool ShowLastLaunchColumn => _showLastLaunchColumn;

    /// <summary>Показывать колонку «Размер» файловой ИБ.</summary>
    public bool ShowSizeColumn => _showSizeColumn;

    /// <summary>Показывать колонку «Действия» (кнопки запуска/конфигуратора/очистки кеша) в списке баз.</summary>
    public bool ShowActionsColumn => _showActionsColumn;

    public double SizeColumnWidth
    {
        get => _sizeColumnWidth;
        set
        {
            if (SetProperty(ref _sizeColumnWidth, value))
                ScheduleSaveSettings();
        }
    }

    /// <summary>
    /// Порядок колонок списка баз по умолчанию (колонка «Конфигурация» в самом
    /// конце). Используется, пока пользователь не задал собственный порядок.
    /// </summary>
    private static readonly string[] DefaultColumnOrder =
        { "Version", "LaunchMode", "Actions", "ServerBase", "LastLaunch", "Size", "Configuration" };

    /// <summary>
    /// Порядок колонок списка баз слева направо (кроме фиксированной колонки
    /// «Название», которая всегда первая). Если порядок не задан или пуст —
    /// возвращается порядок по умолчанию: «Режим запуска» сразу после названия,
    /// колонка «Действия» — за ним, «Конфигурация» — в конце.
    /// </summary>
    public IReadOnlyList<string> ColumnOrderKeys =>
        _columnOrder is { Count: > 0 } ? _columnOrder : DefaultColumnOrder;

    /// <summary>
    /// Применяет настройки содержимого нижней панели (строки состояния).
    /// </summary>
    public void ApplyStatusBarSettings(
        bool connectionPath, bool architecture, bool launchMode, bool port,
        bool platformVersion, bool clientType, bool connectionType, bool user,
        bool showId = false)
    {
        _statusShowConnectionPath = connectionPath;
        _statusShowArchitecture = architecture;
        _statusShowLaunchMode = launchMode;
        _statusShowPort = port;
        _statusShowPlatformVersion = platformVersion;
        _statusShowClientType = clientType;
        _statusShowConnectionType = connectionType;
        _statusShowUser = user;
        _statusShowId = showId;
        OnPropertyChanged(nameof(StatusShowConnectionPath));
        OnPropertyChanged(nameof(StatusShowArchitecture));
        OnPropertyChanged(nameof(StatusShowLaunchMode));
        OnPropertyChanged(nameof(StatusShowPort));
        OnPropertyChanged(nameof(StatusShowPlatformVersion));
        OnPropertyChanged(nameof(StatusShowClientType));
        OnPropertyChanged(nameof(StatusShowConnectionType));
        OnPropertyChanged(nameof(StatusShowUser));
        OnPropertyChanged(nameof(StatusShowId));
        OnPropertyChanged(nameof(StatusBarInfo));
        SaveSettings();
    }

    /// <summary>
    /// Применяет настройки отображения списка баз, заданные в окне настроек.
    /// Обновляет видимость колонок, кнопок и тегов, а также поведение
    /// группировки и фильтра по избранному.
    /// </summary>
    public void ApplyDisplaySettings(bool showFavoritesButton, bool showPinnedButton, bool showTags,
        bool showVersionColumn, bool showLaunchModeColumn, bool showServerColumn, bool showLastLaunchColumn,
        bool groupByGroup, bool showFavoritesOnly, bool showSizeColumn = true,
        bool showConfigurationColumn = true, bool showEmptyGroups = false,
        List<string>? columnOrder = null, bool showActionsColumn = true)
    {
        _showFavoritesButton = showFavoritesButton;
        _showPinnedButton = showPinnedButton;
        _showTags = showTags;
        _showVersionColumn = showVersionColumn;
        _showConfigurationColumn = showConfigurationColumn;
        _showLaunchModeColumn = showLaunchModeColumn;
        _showServerColumn = showServerColumn;
        _showLastLaunchColumn = showLastLaunchColumn;
        _showSizeColumn = showSizeColumn;
        _showActionsColumn = showActionsColumn;

        OnPropertyChanged(nameof(ShowFavoritesButton));
        OnPropertyChanged(nameof(ShowPinnedButton));
        OnPropertyChanged(nameof(ShowTags));
        OnPropertyChanged(nameof(ShowVersionColumn));
        OnPropertyChanged(nameof(ShowConfigurationColumn));
        OnPropertyChanged(nameof(ShowLaunchModeColumn));
        OnPropertyChanged(nameof(ShowServerColumn));
        OnPropertyChanged(nameof(ShowLastLaunchColumn));
        OnPropertyChanged(nameof(ShowSizeColumn));
        OnPropertyChanged(nameof(ShowActionsColumn));

        // Применяем поведение списка (уже имеющиеся настройки).
        GroupByGroup = groupByGroup;
        ShowFavoritesOnly = showFavoritesOnly;
        ShowEmptyGroups = showEmptyGroups;

        // Пользовательский порядок колонок (пустой — вернуть порядок по умолчанию).
        _columnOrder = columnOrder is { Count: > 0 } ? new List<string>(columnOrder) : new List<string>();
        OnPropertyChanged(nameof(ColumnOrderKeys));

        SaveSettings();
    }

    /// <summary>Сохранённая ширина окна приложения (0 — по умолчанию).</summary>
    public double SavedWindowWidth => _windowWidth;

    /// <summary>Сохранённая высота окна приложения (0 — по умолчанию).</summary>
    public double SavedWindowHeight => _windowHeight;

    /// <summary>Сохранённая позиция окна по горизонтали (0 — по центру).</summary>
    public double SavedWindowLeft => _windowLeft;

    /// <summary>Сохранённая позиция окна по вертикали (0 — по центру).</summary>
    public double SavedWindowTop => _windowTop;

    /// <summary>Сохранённое состояние окна (пусто — обычное).</summary>
    public string SavedWindowState => _windowState;

    /// <summary>Запоминать размер, позицию, состояние окна и монитор при закрытии.</summary>
    public bool RememberWindowLayout => _rememberWindowLayout;

    /// <summary>Компактный режим интерфейса (уменьшенные иконки, отступы, расстояния).</summary>
    public bool CompactMode
    {
        get => _compactMode;
        set { if (SetProperty(ref _compactMode, value)) SaveSettings(); }
    }

    /// <summary>Применяет компактный режим к главному окну (масштабирует отступы/шрифты/высоты).</summary>
    public void ApplyCompactMode(bool compact)
    {
        CompactMode = compact;
        Themes.ThemeManager.ApplyCompact(compact);
    }

    /// <summary>
    /// Сохраняет размер, позицию и состояние окна приложения.
    /// </summary>
    public void SaveWindowLayout(double width, double height, double left, double top, string state)
    {
        _windowWidth = width;
        _windowHeight = height;
        _windowLeft = left;
        _windowTop = top;
        _windowState = state;
        SaveSettings();
    }

    /// <summary>Команда импорта баз из файла ibases.v8i.</summary>
    public ICommand ImportFromIbasesV8iCommand { get; }
    public ICommand ExportToIbasesV8iCommand { get; }
    public ICommand SynchronizeWithIbasesCommand { get; }
    public ICommand ToggleRightPanelDetailsCommand { get; }
    public ICommand ToggleSessionLaunchPanelCommand { get; }

    /// <summary>Команда экспорта списка информационных баз в файл.</summary>
    public ICommand ExportInfobasesCommand { get; }

    /// <summary>Команда загрузки списка информационных баз из файла.</summary>
    public ICommand ImportInfobasesCommand { get; }

    /// <summary>Команда очистки всего списка информационных баз.</summary>
    public ICommand ClearAllInfobasesCommand { get; }

    /// <summary>Команда закрепления/открепления выбранной базы.</summary>
    public ICommand TogglePinCommand { get; }

    /// <summary>Команда закрепления/открепления конкретной базы.</summary>
    public ICommand TogglePinForCommand { get; }

    /// <summary>Команда копирования строки подключения выбранной базы в буфер обмена.</summary>
    public ICommand CopyConnectionStringCommand { get; }

    /// <summary>Команда очистки локального кеша 1С выбранной базы.</summary>
    public ICommand ClearCacheCommand { get; }

    /// <summary>Команда очистки программного кеша 1С.</summary>
    public ICommand ClearProgramCacheCommand { get; }

    /// <summary>Команда очистки пользовательского кеша 1С.</summary>
    public ICommand ClearUserCacheCommand { get; }

    /// <summary>Команда одновременной очистки программного и пользовательского кеша 1С.</summary>
    public ICommand ClearCacheBothCommand { get; }

    /// <summary>Открыть каталог файловой базы в проводнике.</summary>
    public ICommand OpenInfobaseFolderCommand { get; }

    /// <summary>Создать ярлык на рабочем столе для выбранной базы.</summary>
    public ICommand CreateDesktopShortcutCommand { get; }

    /// <summary>Удалить из списка файловые базы без 1Cv8.1CD.</summary>
    public ICommand RemoveMissingFileBasesCommand { get; }

    /// <summary>Завершить зависшие процессы платформы 1С.</summary>
    public ICommand KillOneCProcessesCommand { get; }

    /// <summary>Выгрузка ИБ в .dt (пакетный DESIGNER).</summary>
    public ICommand DumpInfobaseDtCommand { get; }

    /// <summary>Выгрузка конфигурации в .cf.</summary>
    public ICommand DumpConfigurationCfCommand { get; }

    /// <summary>Идёт ли в данный момент выгрузка .dt/.cf (показывает индикатор в верхней панели).</summary>
    public bool IsExporting
    {
        get => _isExporting;
        private set => SetProperty(ref _isExporting, value);
    }

    /// <summary>Текст подсказки индикатора выгрузки (сводка о текущей операции).</summary>
    public string ExportIndicatorTooltip
    {
        get => _exportIndicatorTooltip;
        private set => SetProperty(ref _exportIndicatorTooltip, value);
    }

    /// <summary>Тестирование ИБ (/IBCheckAndRepair -TestOnly).</summary>
    public ICommand TestInfobaseCommand { get; }

    /// <summary>Показать историю запусков выбранной базы.</summary>
    public ICommand ShowLaunchHistoryCommand { get; }

    /// <summary>Пересчитать размеры файловых баз.</summary>
    public ICommand RefreshFileSizesCommand { get; }

    /// <summary>Запросить и заполнить информацию о конфигурации выбранной базы (точечно).</summary>
    public ICommand RefreshConfigurationInfoCommand { get; }

    /// <summary>Проверить доступность всех баз 1С (файловая — наличие по пути; клиент-серверная — подключение).</summary>
    public ICommand CheckAvailabilityCommand { get; }

    /// <summary>Зарегистрировать COM-коннектор 1С (comcntr.dll) в системе.</summary>
    public ICommand RegisterComConnectorCommand { get; }

    /// <summary>Команда добавления тега к базе прямо в строке названия (без отдельного окна).</summary>
    public ICommand AddTagInlineCommand { get; }

    /// <summary>Команда удаления тега из базы.</summary>
    public ICommand RemoveTagCommand { get; }

    /// <summary>Команда поиска баз по тегу.</summary>
    public ICommand SearchByTagCommand { get; }

    /// <summary>Команда очистки поля поиска.</summary>
    public ICommand ClearSearchCommand { get; }

    /// <summary>Сбросить только выбранные теги фильтра.</summary>
    public ICommand ClearTagFiltersCommand { get; }

    /// <summary>Команда сворачивания всех групп.</summary>
    public ICommand CollapseAllGroupsCommand { get; }

    /// <summary>Команда разворачивания всех групп.</summary>
    public ICommand ExpandAllGroupsCommand { get; }

    /// <summary>Команда сортировки групп по возрастанию (А→Я).</summary>
    public ICommand SortGroupsAscendingCommand { get; }

    /// <summary>Команда сортировки групп по убыванию (Я→А).</summary>
    public ICommand SortGroupsDescendingCommand { get; }

    /// <summary>Команда сворачивания/разворачивания отдельной группы с сохранением состояния.</summary>
    public ICommand ToggleGroupExpandedCommand { get; }

    /// <summary>Команда открытия окна настроек приложения.</summary>
    public ICommand OpenSettingsCommand { get; }

    /// <summary>
    /// Команда «Перейти по ссылке» — открывает диалог ввода ссылки на
    /// информационную базу (аналог стандартного загрузчика 1С) и запускает базу.
    /// </summary>
    public ICommand OpenInfobaseByLinkCommand { get; }

    /// <summary>Команда выбора информационной базы.</summary>
    public ICommand SelectInfobaseCommand { get; }

    /// <summary>Команда обновления списка баз.</summary>
    public ICommand RefreshCommand { get; }

    /// <summary>Команда добавления новой базы.</summary>
    public ICommand AddInfobaseCommand { get; }

    /// <summary>Команда редактирования выбранной базы.</summary>
    public ICommand EditInfobaseCommand { get; }

    /// <summary>Команда удаления выбранной базы.</summary>
    public ICommand DeleteInfobaseCommand { get; }

    /// <summary>Команда редактирования конкретной группы (кнопка в колонке «Действия» строки группы).</summary>
    public ICommand EditGroupCommand { get; }

    /// <summary>Команда удаления конкретной группы (кнопка в колонке «Действия» строки группы).</summary>
    public ICommand DeleteGroupCommand { get; }


    /// <summary>Команда добавления/удаления из избранного.</summary>
    public ICommand ToggleFavoriteCommand { get; }

    /// <summary>Команда добавления/удаления из избранного для конкретной базы.</summary>
    public ICommand ToggleFavoriteForCommand { get; }

    /// <summary>Команда запуска 1С:Предприятие.</summary>
    /// <summary>Под-VM запуска баз (композиция MainViewModel).</summary>
    /// <summary>Единая команда запуска (параметр: LaunchKind или строка имени enum).</summary>
    public ICommand LaunchCommand { get; }

    public ICommand LaunchEnterpriseCommand { get; }

    /// <summary>Команда запуска Конфигуратора.</summary>
    public ICommand LaunchConfiguratorCommand { get; }

    /// <summary>Команда переключения вкладки списка на «Все базы» (горячая клавиша).</summary>
    public ICommand ShowAllCommand { get; }

    /// <summary>Команда переключения вкладки списка на «Избранное» (горячая клавиша).</summary>
    public ICommand ShowFavoritesCommand { get; }

    /// <summary>Команда переключения вкладки списка на «Недавние» (горячая клавиша).</summary>
    public ICommand ShowRecentCommand { get; }

    /// <summary>Команда запуска 1С:Предприятие тонким клиентом (32 бита).</summary>
    public ICommand LaunchEnterpriseThinCommand { get; }

    /// <summary>Команда запуска 1С:Предприятие толстым клиентом (32 бита).</summary>
    public ICommand LaunchEnterpriseThickCommand { get; }

    /// <summary>Команда запуска 1С:Предприятие тонким клиентом (64 бита).</summary>
    public ICommand LaunchEnterpriseThin64Command { get; }

    /// <summary>Команда запуска 1С:Предприятие толстым клиентом (64 бита).</summary>
    public ICommand LaunchEnterpriseThick64Command { get; }

    public ICommand LaunchEnterpriseAsAdminCommand { get; }
    public ICommand LaunchConfiguratorAsAdminCommand { get; }
    public ICommand LaunchEnterpriseWithParamsCommand { get; }
    public ICommand LaunchEnterpriseWithAuthCommand { get; }
    public ICommand LaunchConfiguratorWithParamsCommand { get; }
    public ICommand LaunchNativeStarterCommand { get; }

}
#endif
