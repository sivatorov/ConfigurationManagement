using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Win32;
using Configuration_Management.Models;
using Configuration_Management.Services;
using MessageBox = System.Windows.MessageBox;

namespace Configuration_Management.ViewModels;

/// <summary>
/// Главная ViewModel приложения «Управление конфигурациями 1С».
/// </summary>
public class MainViewModel : ViewModelBase
{
    private readonly InfobaseRepository _repository;
    private Infobase? _selectedInfobase;
    private string _searchText = string.Empty;
    private bool _showFavoritesOnly;
    private bool _groupByGroup = true;
    private string _savedTheme = string.Empty;
    private readonly HashSet<string> _collapsedGroups = new(StringComparer.OrdinalIgnoreCase);
    private List<string> _installedPlatformVersions = new();
    private double _nameColumnWidth;
    private double _versionColumnWidth;
    private double _launchModeColumnWidth;
    private double _serverColumnWidth;
    private double _lastLaunchColumnWidth;
    private double _windowWidth;
    private double _windowHeight;
    private double _windowLeft;
    private double _windowTop;
    private string _windowState = string.Empty;

    /// <summary>
    /// Внутренний набор узлов дерева групп, перестраиваемый при изменении данных.
    /// Заполняется в методе <see cref="RebuildGroupTree"/>.
    /// </summary>
    private List<GroupNodeViewModel> _groupNodes = new();

    public MainViewModel()
    {
        _repository = new InfobaseRepository();

        // Загружаем настройки интерфейса (состояние кнопок «Избранные» и «Группировать»).
        var settings = _repository.LoadSettings();
        _showFavoritesOnly = settings.ShowFavoritesOnly;
        _groupByGroup = settings.GroupByGroup;
        _savedTheme = settings.Theme;
        _installedPlatformVersions = new List<string>(settings.InstalledPlatformVersions);
        _nameColumnWidth = settings.NameColumnWidth;
        _versionColumnWidth = settings.VersionColumnWidth;
        _launchModeColumnWidth = settings.LaunchModeColumnWidth;
        _serverColumnWidth = settings.ServerColumnWidth;
        _lastLaunchColumnWidth = settings.LastLaunchColumnWidth;
        _windowWidth = settings.WindowWidth;
        _windowHeight = settings.WindowHeight;
        _windowLeft = settings.WindowLeft;
        _windowTop = settings.WindowTop;
        _windowState = settings.WindowState;
        foreach (var groupName in settings.CollapsedGroups)
        {
            _collapsedGroups.Add(groupName);
        }

        // Загружаем базы из файла настроек.
        var saved = _repository.Load();
        Infobases = new ObservableCollection<Infobase>(saved);

        // Загружаем группы из файла настроек. Стандартные группы из демо-данных не создаются.
        var loadedGroups = _repository.LoadGroups();
        Groups = new ObservableCollection<Group>(loadedGroups);

        InfobasesView = CollectionViewSource.GetDefaultView(Infobases);
        InfobasesView.Filter = FilterInfobase;
        // Сортировка: закреплённые базы идут первыми, затем по названию базы.
        InfobasesView.SortDescriptions.Add(new SortDescription(nameof(Infobase.GroupSortOrder), ListSortDirection.Ascending));
        InfobasesView.SortDescriptions.Add(new SortDescription(nameof(Infobase.Name), ListSortDirection.Ascending));

        // Дерево групп (отображается в виде «группа в группе»).
        GroupNodes = new ObservableCollection<GroupNodeViewModel>();
        RebuildGroupTree();

        SelectInfobaseCommand = new RelayCommand(SelectInfobase);
        RefreshCommand = new RelayCommand(Refresh);
        AddInfobaseCommand = new RelayCommand(AddInfobase);
        EditInfobaseCommand = new RelayCommand(EditInfobase, _ => SelectedInfobase != null);
        DeleteInfobaseCommand = new RelayCommand(DeleteInfobase, _ => SelectedInfobase != null);
        ToggleFavoriteCommand = new RelayCommand(ToggleFavorite, _ => SelectedInfobase != null);
        ToggleFavoriteForCommand = new RelayCommand(ToggleFavoriteFor);
        LaunchEnterpriseCommand = new RelayCommand(LaunchEnterprise, _ => SelectedInfobase != null);
        LaunchConfiguratorCommand = new RelayCommand(LaunchConfigurator, _ => SelectedInfobase != null);
        LaunchEnterpriseThinCommand = new RelayCommand(LaunchEnterpriseThin, _ => SelectedInfobase != null);
        LaunchEnterpriseThickCommand = new RelayCommand(LaunchEnterpriseThick, _ => SelectedInfobase != null);
        LaunchEnterpriseThin64Command = new RelayCommand(LaunchEnterpriseThin64, _ => SelectedInfobase != null);
        LaunchEnterpriseThick64Command = new RelayCommand(LaunchEnterpriseThick64, _ => SelectedInfobase != null);
        ManageGroupsCommand = new RelayCommand(ManageGroups);
        ImportFromIbasesV8iCommand = new RelayCommand(ImportFromIbasesV8i);
        ExportInfobasesCommand = new RelayCommand(ExportInfobases);
        ImportInfobasesCommand = new RelayCommand(ImportInfobases);
        ClearAllInfobasesCommand = new RelayCommand(ClearAllInfobases);
        TogglePinCommand = new RelayCommand(TogglePin, _ => SelectedInfobase != null);
        TogglePinForCommand = new RelayCommand(TogglePinFor);
        CopyConnectionStringCommand = new RelayCommand(CopyConnectionString, _ => SelectedInfobase != null);
        ClearCacheCommand = new RelayCommand(ClearCache, _ => SelectedInfobase != null);
        AddTagCommand = new RelayCommand(AddTag);
        AddTagInlineCommand = new RelayCommand(AddTagInline);
        RemoveTagCommand = new RelayCommand(RemoveTag);
        SearchByTagCommand = new RelayCommand(SearchByTag);
        ClearSearchCommand = new RelayCommand(ClearSearch);
        CollapseAllGroupsCommand = new RelayCommand(CollapseAllGroups);
        ExpandAllGroupsCommand = new RelayCommand(ExpandAllGroups);
        OpenSettingsCommand = new RelayCommand(OpenSettings);

        // Если список баз пуст — предлагаем загрузить базы из файла ibases.v8i.
        if (Infobases.Count == 0)
        {
            PromptImportFromIbasesV8i();
        }
    }

    /// <summary>Список информационных баз.</summary>
    public ObservableCollection<Infobase> Infobases { get; }

    /// <summary>Представление списка баз с группировкой и фильтрацией.</summary>
    public ICollectionView InfobasesView { get; }

    /// <summary>Узлы дерева групп информационных баз для отображения «группа в группе».</summary>
    public ObservableCollection<GroupNodeViewModel> GroupNodes { get; }

    /// <summary>Выбранная информационная база.</summary>
    public Infobase? SelectedInfobase
    {
        get => _selectedInfobase;
        set
        {
            if (SetProperty(ref _selectedInfobase, value))
            {
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    /// <summary>Текст поиска по информационным базам.</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                InfobasesView.Refresh();
                RebuildGroupTree();
            }
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
                InfobasesView.Refresh();
                RebuildGroupTree();
                SaveSettings();
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
                InfobasesView.Refresh();
                RebuildGroupTree();
                SaveSettings();
                OnPropertyChanged(nameof(GroupByGroupText));
            }
        }
    }

    /// <summary>Текст кнопки переключения отображения групп.</summary>
    public string GroupByGroupText => _groupByGroup ? "📁 Скрыть группы" : "📁 Показывать группы";

    /// <summary>Список групп информационных баз.</summary>
    public ObservableCollection<Group> Groups { get; }

    /// <summary>Название сохранённой темы оформления (пусто, если тема не сохранялась).</summary>
    public string SavedTheme => _savedTheme;

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

    /// <summary>Команда управления группами.</summary>
    public ICommand ManageGroupsCommand { get; }

    /// <summary>Команда импорта баз из файла ibases.v8i.</summary>
    public ICommand ImportFromIbasesV8iCommand { get; }

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

    /// <summary>Команда добавления тега к базе.</summary>
    public ICommand AddTagCommand { get; }

    /// <summary>Команда добавления тега к базе прямо в строке названия (без отдельного окна).</summary>
    public ICommand AddTagInlineCommand { get; }

    /// <summary>Команда удаления тега из базы.</summary>
    public ICommand RemoveTagCommand { get; }

    /// <summary>Команда поиска баз по тегу.</summary>
    public ICommand SearchByTagCommand { get; }

    /// <summary>Команда очистки поля поиска.</summary>
    public ICommand ClearSearchCommand { get; }

    /// <summary>Команда сворачивания всех групп.</summary>
    public ICommand CollapseAllGroupsCommand { get; }

    /// <summary>Команда разворачивания всех групп.</summary>
    public ICommand ExpandAllGroupsCommand { get; }

    /// <summary>Команда открытия окна настроек приложения.</summary>
    public ICommand OpenSettingsCommand { get; }

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

    /// <summary>Команда добавления/удаления из избранного.</summary>
    public ICommand ToggleFavoriteCommand { get; }

    /// <summary>Команда добавления/удаления из избранного для конкретной базы.</summary>
    public ICommand ToggleFavoriteForCommand { get; }

    /// <summary>Команда запуска 1С:Предприятие.</summary>
    public ICommand LaunchEnterpriseCommand { get; }

    /// <summary>Команда запуска Конфигуратора.</summary>
    public ICommand LaunchConfiguratorCommand { get; }

    /// <summary>Команда запуска 1С:Предприятие тонким клиентом (32 бита).</summary>
    public ICommand LaunchEnterpriseThinCommand { get; }

    /// <summary>Команда запуска 1С:Предприятие толстым клиентом (32 бита).</summary>
    public ICommand LaunchEnterpriseThickCommand { get; }

    /// <summary>Команда запуска 1С:Предприятие тонким клиентом (64 бита).</summary>
    public ICommand LaunchEnterpriseThin64Command { get; }

    /// <summary>Команда запуска 1С:Предприятие толстым клиентом (64 бита).</summary>
    public ICommand LaunchEnterpriseThick64Command { get; }

    private void SelectInfobase(object? parameter)
    {
        if (parameter is Infobase infobase)
        {
            SelectedInfobase = infobase;
        }
    }

    private void Refresh(object? parameter)
    {
        Infobases.Clear();
        foreach (var infobase in _repository.Load())
        {
            Infobases.Add(infobase);
        }
        SelectedInfobase = null;
        Save();
        RebuildGroupTree();
    }

    private void AddInfobase(object? parameter)
    {
        var dialog = new ConnectionSettingsWindow(null, Groups, _installedPlatformVersions)
        {
            Owner = Application.Current.MainWindow
        };
        if (dialog.ShowDialog() == true)
        {
            Infobases.Add(dialog.Result);
            SelectedInfobase = dialog.Result;
            Save();
            RebuildGroupTree();
        }
    }

    private void EditInfobase(object? parameter)
    {
        if (SelectedInfobase is null)
            return;

        var dialog = new ConnectionSettingsWindow(SelectedInfobase, Groups, _installedPlatformVersions)
        {
            Owner = Application.Current.MainWindow
        };
        if (dialog.ShowDialog() == true)
        {
            // Применяем изменения к существующему объекту, а не заменяем его новым.
            // Это важно, потому что на объект могут ссылаться и основной список, и представление.
            var target = SelectedInfobase;
            target.Id = dialog.Result.Id;
            target.Name = dialog.Result.Name;
            target.Group = dialog.Result.Group;
            target.Description = dialog.Result.Description;
            target.PlatformVersion = dialog.Result.PlatformVersion;
            target.LaunchMode = dialog.Result.LaunchMode;
            target.LaunchParameters = dialog.Result.LaunchParameters;
            target.ClientType = dialog.Result.ClientType;
            target.IsFavorite = dialog.Result.IsFavorite;
            target.IsPinned = dialog.Result.IsPinned;
            target.LastLaunchDate = dialog.Result.LastLaunchDate;
            target.Tags = dialog.Result.Tags;
            target.MetadataRoot = dialog.Result.MetadataRoot;
            target.Connection = dialog.Result.Connection;

            // Обновляем отображаемые данные (перегруппировка с учётом изменения закрепления).
            InfobasesView.Refresh();
            Save();
            RebuildGroupTree();
        }
    }

    private void DeleteInfobase(object? parameter)
    {
        if (SelectedInfobase is null)
            return;

        var result = MessageBox.Show(
            $"Удалить информационную базу «{SelectedInfobase.Name}»?",
            "Подтверждение удаления",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            Infobases.Remove(SelectedInfobase);
            SelectedInfobase = null;
            Save();
            RebuildGroupTree();
        }
    }

    private void ToggleFavorite(object? parameter)
    {
        if (SelectedInfobase is null)
            return;

        SelectedInfobase.IsFavorite = !SelectedInfobase.IsFavorite;
        // Полный Refresh нужен только тогда, когда активен фильтр «Только избранные»
        // или поиск по тегу — иначе база должна исчезнуть из списка.
        if (ShowFavoritesOnly || !string.IsNullOrWhiteSpace(SearchText))
        {
            InfobasesView.Refresh();
        }
        // Перестраиваем дерево групп, чтобы пустые группы скрывались/показывались
        // в зависимости от состояния избранного.
        RebuildGroupTree();
        Save();
    }

    private void ToggleFavoriteFor(object? parameter)
    {
        if (parameter is not Infobase infobase)
            return;

        infobase.IsFavorite = !infobase.IsFavorite;
        // Полный Refresh нужен только тогда, когда активен фильтр «Только избранные»
        // или поиск по тегу — иначе база должна исчезнуть из списка.
        if (ShowFavoritesOnly || !string.IsNullOrWhiteSpace(SearchText))
        {
            InfobasesView.Refresh();
        }
        // Перестраиваем дерево групп, чтобы пустые группы скрывались/показывались
        // в зависимости от состояния избранного.
        RebuildGroupTree();
        Save();
    }

    private void TogglePin(object? parameter)
    {
        if (SelectedInfobase is null)
            return;

        SelectedInfobase.IsPinned = !SelectedInfobase.IsPinned;
        InfobasesView.Refresh();
        Save();
        RebuildGroupTree();
    }

    private void TogglePinFor(object? parameter)
    {
        if (parameter is not Infobase infobase)
            return;

        infobase.IsPinned = !infobase.IsPinned;
        InfobasesView.Refresh();
        Save();
        RebuildGroupTree();
    }

    private void LaunchEnterprise(object? parameter)
    {
        if (SelectedInfobase is null)
            return;
        if (OneCLauncher.Launch(SelectedInfobase, OneCLaunchMode.Enterprise))
        {
            InfobasesView.Refresh();
            Save();
        }
    }

    private void LaunchConfigurator(object? parameter)
    {
        if (SelectedInfobase is null)
            return;
        if (OneCLauncher.Launch(SelectedInfobase, OneCLaunchMode.Configurator))
        {
            InfobasesView.Refresh();
            Save();
        }
    }

    private void LaunchEnterpriseThin(object? parameter)
        => LaunchEnterpriseWith(OneCClientType.Thin, OneCArchitecture.x86);

    private void LaunchEnterpriseThick(object? parameter)
        => LaunchEnterpriseWith(OneCClientType.Thick, OneCArchitecture.x86);

    private void LaunchEnterpriseThin64(object? parameter)
        => LaunchEnterpriseWith(OneCClientType.Thin, OneCArchitecture.x64);

    private void LaunchEnterpriseThick64(object? parameter)
        => LaunchEnterpriseWith(OneCClientType.Thick, OneCArchitecture.x64);

    private void LaunchEnterpriseWith(OneCClientType clientType, OneCArchitecture architecture)
    {
        if (SelectedInfobase is null)
            return;
        if (OneCLauncher.Launch(SelectedInfobase, OneCLaunchMode.Enterprise, clientType, architecture))
        {
            InfobasesView.Refresh();
            Save();
        }
    }

    private bool FilterInfobase(object item)
    {
        if (item is not Infobase infobase)
            return false;

        // Фильтр по избранным.
        if (ShowFavoritesOnly && !infobase.IsFavorite)
            return false;

        // Фильтр по тексту поиска.
        var filter = SearchText?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(filter))
            return true;

        return infobase.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
               || infobase.Description.Contains(filter, StringComparison.OrdinalIgnoreCase)
               || infobase.Group.Contains(filter, StringComparison.OrdinalIgnoreCase)
               || infobase.PlatformVersion.Contains(filter, StringComparison.OrdinalIgnoreCase)
               || infobase.Tags.Any(t => t.Contains(filter, StringComparison.OrdinalIgnoreCase));
    }

    private void Save()
    {
        _repository.Save(Infobases.ToList());
    }

    private void SaveGroups()
    {
        _repository.SaveGroups(Groups.ToList());
    }

    private void SaveSettings()
    {
        _repository.SaveSettings(new AppSettings
        {
            ShowFavoritesOnly = _showFavoritesOnly,
            GroupByGroup = _groupByGroup,
            Theme = _savedTheme,
            CollapsedGroups = _collapsedGroups.ToList(),
            InstalledPlatformVersions = _installedPlatformVersions,
            NameColumnWidth = _nameColumnWidth,
            VersionColumnWidth = _versionColumnWidth,
            LaunchModeColumnWidth = _launchModeColumnWidth,
            ServerColumnWidth = _serverColumnWidth,
            LastLaunchColumnWidth = _lastLaunchColumnWidth,
            WindowWidth = _windowWidth,
            WindowHeight = _windowHeight,
            WindowLeft = _windowLeft,
            WindowTop = _windowTop,
            WindowState = _windowState
        });
    }

    /// <summary>
    /// Сохраняет ширины колонок списка баз в настройках.
    /// </summary>
    public void SaveColumnWidths(double nameWidth, double versionWidth, double launchModeWidth, double serverWidth, double lastLaunchWidth)
    {
        NameColumnWidth = nameWidth;
        VersionColumnWidth = versionWidth;
        LaunchModeColumnWidth = launchModeWidth;
        ServerColumnWidth = serverWidth;
        LastLaunchColumnWidth = lastLaunchWidth;
        SaveSettings();
    }

    /// <summary>
    /// Обновляет ширины колонок в памяти (без сохранения в файл).
    /// Используется для синхронизации колонок строк во время перетаскивания разделителя.
    /// </summary>
    public void UpdateColumnWidths(double nameWidth, double versionWidth, double launchModeWidth, double serverWidth, double lastLaunchWidth)
    {
        NameColumnWidth = nameWidth;
        VersionColumnWidth = versionWidth;
        LaunchModeColumnWidth = launchModeWidth;
        ServerColumnWidth = serverWidth;
        LastLaunchColumnWidth = lastLaunchWidth;
    }

    /// <summary>
    /// Сохраняет выбранную тему оформления в настройках.
    /// </summary>
    public void SaveTheme(string theme)
    {
        _savedTheme = theme;
        SaveSettings();
    }

    /// <summary>
    /// Возвращает true, если указанная группа свёрнута в списке баз.
    /// </summary>
    public bool IsGroupCollapsed(string groupName)
    {
        return _collapsedGroups.Contains(groupName);
    }

    /// <summary>
    /// Устанавливает состояние свёрнутости группы и сохраняет его в настройках.
    /// </summary>
    public void SetGroupCollapsed(string groupName, bool collapsed)
    {
        if (collapsed)
        {
            _collapsedGroups.Add(groupName);
        }
        else
        {
            _collapsedGroups.Remove(groupName);
        }
        SaveSettings();
    }

    /// <summary>
    /// Сворачивает все группы в дереве баз.
    /// </summary>
    private void CollapseAllGroups(object? parameter)
    {
        CollapseAllNodes(_groupNodes, collapse: true);
        SaveSettings();
    }

    /// <summary>
    /// Разворачивает все группы в дереве баз.
    /// </summary>
    private void ExpandAllGroups(object? parameter)
    {
        CollapseAllNodes(_groupNodes, collapse: false);
        _collapsedGroups.Clear();
        SaveSettings();
    }

    /// <summary>
    /// Рекурсивно устанавливает состояние развёрнутости всех узлов дерева групп.
    /// </summary>
    private static void CollapseAllNodes(IEnumerable<GroupNodeViewModel> nodes, bool collapse)
    {
        foreach (var node in nodes)
        {
            node.IsExpanded = !collapse;
            CollapseAllNodes(node.Children, collapse);
        }
    }

    /// <summary>
    /// Перестраивает дерево групп на основе текущего списка групп и баз.
    /// Закреплённые базы попадают в корневой узел «Закреплённые», базы без группы — в «Без группы».
    /// Группы и подгруппы, не содержащие баз (в том числе при активном фильтре «Только избранные»),
    /// в дерево не попадают.
    /// </summary>
    public void RebuildGroupTree()
    {
        // Когда группировка отключена — показываем плоский список всех баз в одном узле.
        if (!_groupByGroup)
        {
            var flatNode = new GroupNodeViewModel(null, displayName: "Все базы");
            foreach (var infobase in InfobasesView.Cast<Infobase>())
            {
                flatNode.Infobases.Add(infobase);
            }

            flatNode.PopulateItems();
            _groupNodes = new List<GroupNodeViewModel> { flatNode };
            GroupNodes.Clear();
            GroupNodes.Add(flatNode);
            ApplyExpandedState(_groupNodes);
            OnPropertyChanged(nameof(GroupNodes));
            return;
        }

        var roots = GroupNodeViewModel.BuildTree(Groups);

        // Корневой узел «Закреплённые» для закреплённых баз.
        var pinnedNode = new GroupNodeViewModel(null, displayName: "Закреплённые");

        // Определяем, какая группа реально соответствует каждой базе (по полному пути).
        var pathToNode = new Dictionary<string, GroupNodeViewModel>(StringComparer.OrdinalIgnoreCase);
        void IndexNode(GroupNodeViewModel node)
        {
            if (node.Group is not null)
            {
                pathToNode[GroupHierarchyHelper.GetFullPath(node.Group, Groups)] = node;
            }
            foreach (var child in node.Children)
            {
                IndexNode(child);
            }
        }
        foreach (var root in roots)
        {
            IndexNode(root);
        }

        // Распределяем базы по узлам и в «Закреплённые».
        // Используем уже отфильтрованное представление (по избранным и тексту поиска).
        foreach (var infobase in InfobasesView.Cast<Infobase>())
        {
            if (infobase.IsPinned)
            {
                pinnedNode.Infobases.Add(infobase);
                continue;
            }

            var groupPath = infobase.Group?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(groupPath))
            {
                continue; // База без группы — не показываем в дереве (или добавить отдельный узел при необходимости).
            }

            if (pathToNode.TryGetValue(groupPath, out var node))
            {
                node.Infobases.Add(infobase);
            }
        }

        // Заполняем единые коллекции Items (подгруппы + базы) для вложенного отображения.
        foreach (var root in roots)
        {
            root.PopulateItems();
        }
        pinnedNode.PopulateItems();

        // Блок «Закреплённые» показываем в начале дерева, только если в нём есть базы.
        _groupNodes = roots;
        GroupNodes.Clear();
        if (pinnedNode.ContainsInfobases)
        {
            GroupNodes.Add(pinnedNode);
        }
        foreach (var root in roots)
        {
            // Пустые группы (не содержащие баз в текущем фильтре, например при активном
            // режиме «Только избранные») в дерево не добавляются.
            if (root.ContainsInfobases)
            {
                GroupNodes.Add(root);
            }
        }

        // Применяем сохранённое состояние развёрнутости.
        ApplyExpandedState(_groupNodes);

        OnPropertyChanged(nameof(GroupNodes));
    }

    /// <summary>
    /// Применяет сохранённое состояние развёрнутости к узлам дерева.
    /// </summary>
    private void ApplyExpandedState(IEnumerable<GroupNodeViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            if (node.Group is not null)
            {
                node.IsExpanded = !IsGroupCollapsed(node.FullPath);
            }
            ApplyExpandedState(node.Children);
        }
    }

    /// <summary>
    /// Рекурсивно ищет узел дерева групп по идентификатору группы.
    /// </summary>
    private GroupNodeViewModel? FindGroupNode(GroupNodeViewModel node, string groupId)
    {
        if (node.Group is not null && string.Equals(node.Group.Id, groupId, StringComparison.OrdinalIgnoreCase))
        {
            return node;
        }
        foreach (var child in node.Children)
        {
            var found = FindGroupNode(child, groupId);
            if (found is not null)
            {
                return found;
            }
        }
        return null;
    }

    /// <summary>
    /// Перемещает группу на позицию другой группы, переупорядочивая элементы в списке баз.
    /// </summary>
    public void MoveGroup(string sourceGroup, string targetGroup)
    {
        if (string.IsNullOrEmpty(sourceGroup) || string.IsNullOrEmpty(targetGroup))
            return;
        if (string.Equals(sourceGroup, targetGroup, StringComparison.OrdinalIgnoreCase))
            return;

        // Собираем элементы перетаскиваемой группы.
        var sourceItems = Infobases
            .Where(i => string.Equals(i.GroupDisplay, sourceGroup, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (sourceItems.Count == 0)
            return;

        // Удаляем элементы перетаскиваемой группы из коллекции.
        foreach (var item in sourceItems)
        {
            Infobases.Remove(item);
        }

        // Находим индекс первого элемента целевой группы в обновлённой коллекции.
        var targetIndex = Infobases
            .ToList()
            .FindIndex(i => string.Equals(i.GroupDisplay, targetGroup, StringComparison.OrdinalIgnoreCase));
        if (targetIndex < 0)
        {
            targetIndex = Infobases.Count;
        }

        // Вставляем элементы перетаскиваемой группы на позицию целевой группы.
        for (var i = 0; i < sourceItems.Count; i++)
        {
            Infobases.Insert(targetIndex + i, sourceItems[i]);
        }

        InfobasesView.Refresh();
        Save();
        RebuildGroupTree();
    }

    private void ManageGroups(object? parameter)
    {
        var dialog = new GroupSettingsWindow(Groups)
        {
            Owner = Application.Current.MainWindow
        };
        if (dialog.ShowDialog() == true)
        {
            // Обновляем список групп из результата диалога.
            Groups.Clear();
            foreach (var group in dialog.Result)
            {
                Groups.Add(group);
            }
            SaveGroups();
            InfobasesView.Refresh();
            RebuildGroupTree();
        }
    }

    /// <summary>
    /// Открывает окно настроек приложения (установленные версии платформы 1С).
    /// </summary>
    private void OpenSettings(object? parameter)
    {
        var dialog = new SettingsWindow(_installedPlatformVersions)
        {
            Owner = Application.Current.MainWindow
        };
        if (dialog.ShowDialog() == true)
        {
            _installedPlatformVersions = new List<string>(dialog.Result);
            SaveSettings();
        }
    }

    /// <summary>
    /// Показывает окно с предложением загрузить базы из файла ibases.v8i,
    /// если список информационных баз пуст. При согласии выполняет импорт.
    /// </summary>
    private void PromptImportFromIbasesV8i()
    {
        var result = MessageBox.Show(
            "Список информационных баз пуст.\n\n" +
            "Хотите загрузить базы из стандартного файла 1С (ibases.v8i)?",
            "Загрузка баз",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
            return;

        // Сначала пытаемся найти файл ibases.v8i автоматически в стандартном месте.
        var filePath = IbasesV8iImporter.FindDefaultPath();

        // Если файл не найден — предлагаем выбрать его вручную.
        if (filePath is null)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Выберите файл списка баз 1С (ibases.v8i)",
                Filter = "Файл списка баз 1С (*.v8i)|*.v8i|Все файлы (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };

            if (dialog.ShowDialog() != true)
                return;

            filePath = dialog.FileName;
        }

        try
        {
            var importResult = IbasesV8iImporter.Import(filePath, Infobases, Groups);

            InfobasesView.Refresh();
            Save();
            SaveGroups();
            RebuildGroupTree();

            MessageBox.Show(
                $"Импорт завершён.\n\n" +
                $"Добавлено новых баз: {importResult.Added}\n" +
                $"Обновлено баз: {importResult.Updated}\n" +
                $"Пропущено (отключено): {importResult.Skipped}\n" +
                $"Создано новых групп: {importResult.GroupsCreated}",
                "Импорт из ibases.v8i",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Не удалось выполнить импорт.\n{ex.Message}",
                "Ошибка импорта",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void ImportFromIbasesV8i(object? parameter)
    {
        // Сначала пытаемся найти файл ibases.v8i автоматически в стандартном месте.
        var filePath = IbasesV8iImporter.FindDefaultPath();

        // Если файл не найден — предлагаем выбрать его вручную.
        if (filePath is null)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Выберите файл списка баз 1С (ibases.v8i)",
                Filter = "Файл списка баз 1С (*.v8i)|*.v8i|Все файлы (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };

            if (dialog.ShowDialog() != true)
                return;

            filePath = dialog.FileName;
        }

        try
        {
            var result = IbasesV8iImporter.Import(filePath, Infobases, Groups);

            InfobasesView.Refresh();
            Save();
            SaveGroups();
            RebuildGroupTree();

            MessageBox.Show(
                $"Импорт завершён.\n\n" +
                $"Добавлено новых баз: {result.Added}\n" +
                $"Обновлено баз: {result.Updated}\n" +
                $"Пропущено (отключено): {result.Skipped}\n" +
                $"Создано новых групп: {result.GroupsCreated}",
                "Импорт из ibases.v8i",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Не удалось выполнить импорт.\n{ex.Message}",
                "Ошибка импорта",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Экспортирует список информационных баз в выбранный JSON-файл.
    /// </summary>
    private void ExportInfobases(object? parameter)
    {
        if (Infobases.Count == 0)
        {
            MessageBox.Show(
                "Список информационных баз пуст. Экспортировать нечего.",
                "Экспорт списка баз",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Экспорт списка информационных баз",
            Filter = "JSON-файл (*.json)|*.json|Все файлы (*.*)|*.*",
            DefaultExt = ".json",
            FileName = "infobases_export.json",
            AddExtension = true
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var exportData = new InfobaseExportData
            {
                Infobases = Infobases.ToList(),
                Groups = Groups.ToList()
            };

            var json = JsonSerializer.Serialize(exportData, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNameCaseInsensitive = true
            });
            File.WriteAllText(dialog.FileName, json);

            MessageBox.Show(
                $"Список информационных баз успешно экспортирован.\n\n" +
                $"Количество баз: {Infobases.Count}\n" +
                $"Количество групп: {Groups.Count}\n" +
                $"Файл: {dialog.FileName}",
                "Экспорт списка баз",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Не удалось выполнить экспорт.\n{ex.Message}",
                "Ошибка экспорта",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Загружает список информационных баз из выбранного JSON-файла,
    /// заменяя текущий список.
    /// </summary>
    private void ImportInfobases(object? parameter)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Загрузка списка информационных баз",
            Filter = "JSON-файл (*.json)|*.json|Все файлы (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var json = File.ReadAllText(dialog.FileName);

            // Пытаемся загрузить новый формат (базы + группы).
            InfobaseExportData? exportData = null;
            try
            {
                exportData = JsonSerializer.Deserialize<InfobaseExportData>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            }
            catch (JsonException)
            {
                // Несовместимый формат — обрабатываем ниже.
            }

            List<Infobase> loaded;
            List<Group> loadedGroups;

            if (exportData != null && exportData.Infobases.Count > 0)
            {
                loaded = exportData.Infobases;
                loadedGroups = exportData.Groups;
            }
            else
            {
                // Старый формат: файл содержит только список баз.
                loaded = JsonSerializer.Deserialize<List<Infobase>>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                }) ?? new List<Infobase>();
                loadedGroups = new List<Group>();
            }

            if (loaded.Count == 0)
            {
                MessageBox.Show(
                    "В выбранном файле не найдено ни одной информационной базы.",
                    "Загрузка списка баз",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            var result = MessageBox.Show(
                $"Загрузить {loaded.Count} информационных баз и {loadedGroups.Count} групп из файла?\n\n" +
                "Текущий список баз и групп будет заменён.",
                "Загрузка списка баз",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
                return;

            Infobases.Clear();
            foreach (var infobase in loaded)
            {
                Infobases.Add(infobase);
            }

            Groups.Clear();
            foreach (var group in loadedGroups)
            {
                Groups.Add(group);
            }

            SelectedInfobase = null;
            InfobasesView.Refresh();
            Save();
            SaveGroups();
            RebuildGroupTree();

            MessageBox.Show(
                $"Список информационных баз успешно загружен.\n\n" +
                $"Количество баз: {loaded.Count}\n" +
                $"Количество групп: {loadedGroups.Count}",
                "Загрузка списка баз",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Не удалось выполнить загрузку.\n{ex.Message}",
                "Ошибка загрузки",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Очищает весь список информационных баз и групп.
    /// </summary>
    private void ClearAllInfobases(object? parameter)
    {
        if (Infobases.Count == 0 && Groups.Count == 0)
        {
            MessageBox.Show(
                "Список информационных баз уже пуст.",
                "Очистка списка баз",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show(
            $"Очистить весь список информационных баз?\n\n" +
            $"Будет удалено баз: {Infobases.Count}\n" +
            $"Будет удалено групп: {Groups.Count}\n\n" +
            "Это действие необратимо.",
            "Очистка списка баз",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
            return;

        Infobases.Clear();
        Groups.Clear();
        SelectedInfobase = null;
        InfobasesView.Refresh();
        Save();
        SaveGroups();
        RebuildGroupTree();

        MessageBox.Show(
            "Список информационных баз очищен.",
            "Очистка списка баз",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void CopyConnectionString(object? parameter)
    {
        if (SelectedInfobase is null)
            return;

        try
        {
            // Для файловой базы копируем путь в кавычках без префикса File=,
            // для клиент-серверной — строку подключения.
            Clipboard.SetText(SelectedInfobase.ConnectionPathDisplay);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Не удалось скопировать строку подключения.\n{ex.Message}",
                "Ошибка копирования",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Очищает локальный кеш 1С выбранной базы.
    /// </summary>
    private void ClearCache(object? parameter)
    {
        if (SelectedInfobase is null)
            return;

        var result = MessageBox.Show(
            $"Очистить локальный кеш 1С для базы «{SelectedInfobase.Name}»?\n\n" +
            "Кеш будет удалён из каталогов %LOCALAPPDATA%\\1C\\1cv8 и %APPDATA%\\1C\\1cv8.\n" +
            "Рекомендуется закрыть все сеансы 1С для этой базы перед очисткой.",
            "Очистка кеша 1С",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
            return;

        try
        {
            var removed = OneCCacheCleaner.Clear(SelectedInfobase);

            if (removed > 0)
            {
                MessageBox.Show(
                    $"Кеш базы «{SelectedInfobase.Name}» очищен.\nУдалено каталогов: {removed}.",
                    "Очистка кеша 1С",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show(
                    $"Каталоги кеша для базы «{SelectedInfobase.Name}» не найдены.",
                    "Очистка кеша 1С",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Не удалось очистить кеш.\n{ex.Message}",
                "Ошибка очистки кеша",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Добавляет тег к выбранной базе.
    /// </summary>
    private void AddTag(object? parameter)
    {
        var infobase = parameter as Infobase ?? SelectedInfobase;
        if (infobase is null)
            return;

        var dialog = new TagInputWindow
        {
            Owner = Application.Current.MainWindow
        };
        if (dialog.ShowDialog() != true)
            return;

        var tag = dialog.Result?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(tag))
            return;

        if (!infobase.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
        {
            infobase.Tags.Add(tag);
            Save();
            RebuildGroupTree();
        }
    }

    /// <summary>
    /// Добавляет тег к базе прямо в строке названия (без отдельного окна).
    /// Параметр приходит как object[] от MultiBinding: [0] = Infobase, [1] = текст тега.
    /// </summary>
    private void AddTagInline(object? parameter)
    {
        if (parameter is not object[] values || values.Length < 2)
            return;

        if (values[0] is not Infobase infobase || values[1] is not string rawTag)
            return;

        var tag = rawTag.Trim();
        if (string.IsNullOrEmpty(tag))
            return;

        if (!infobase.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
        {
            infobase.Tags.Add(tag);
            Save();
            RebuildGroupTree();
        }
    }

    /// <summary>
    /// Удаляет тег из базы.
    /// </summary>
    private void RemoveTag(object? parameter)
    {
        // Параметр приходит как object[] от MultiBinding: [0] = Infobase, [1] = тег.
        if (parameter is not object[] values || values.Length < 2)
            return;

        if (values[0] is not Infobase infobase || values[1] is not string tag)
            return;

        infobase.Tags.RemoveAll(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));
        Save();
        RebuildGroupTree();
    }

    /// <summary>
    /// Копирует тег в поле поиска и выполняет отбор баз с этим тегом.
    /// </summary>
    private void SearchByTag(object? parameter)
    {
        if (parameter is not string tag)
            return;

        SearchText = tag;
    }

    /// <summary>
    /// Очищает поле поиска.
    /// </summary>
    private void ClearSearch(object? parameter)
    {
        SearchText = string.Empty;
    }

}