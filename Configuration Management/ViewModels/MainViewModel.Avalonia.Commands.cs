#if LINUX
using System.Windows.Input;
using Configuration_Management.Localization;
using Configuration_Management.Models;
using Configuration_Management.Services;

namespace Configuration_Management.ViewModels;

/// <summary>Main ViewModel (Avalonia/Linux): команды (partial).</summary>
public partial class MainViewModel : ViewModelBase
{
    // ======================= Команды =======================

    public ICommand ClearSearchCommand { get; private set; } = null!;
    public ICommand SearchByTagCommand { get; private set; } = null!;
    public ICommand AddTagInlineCommand { get; private set; } = null!;
    public ICommand RemoveTagCommand { get; private set; } = null!;
    public ICommand ClearTagFiltersCommand { get; private set; } = null!;
    public ICommand FindInListCommand { get; private set; } = null!;
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
        // «Найти в списке»: переход к базе в общем списке «Все базы» с раскрытием группы (issue #285).
        FindInListCommand = new RelayCommand(p => ExecuteFindInList(p as Infobase),
            p => SelectedInfobase is not null || p is Infobase);
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
        EditInfobaseCommand = new RelayCommand(p => EditInfobase(p as Infobase ?? SelectedInfobase), _ => SelectedInfobase is not null);
        AddInfobaseCommand = new RelayCommand(AddInfobase);
        DeleteInfobaseCommand = new RelayCommand(_ => DeleteInfobase(),
            _ => SelectedInfobase is not null || SelectedGroupNode?.Group is not null);
        // Команды группы: параметр — узел группы или сама группа из строки дерева.
        EditGroupCommand = new RelayCommand(p =>
        {
            var group = ResolveGroup(p);
            if (group is not null)
            {
                EditGroup(group);
                return;
            }

            // Служебные узлы «Без группы» / «Закреплённые» (без модели Group)
            // редактируются тем же окном, но только по оформлению (цвет и иконка),
            // как в Windows-версии (issue #240).
            if (p is GroupNodeViewModel node && node.Marker is { } marker)
            {
                if (string.Equals(marker, GroupNodeViewModel.NoGroupMarker, StringComparison.Ordinal))
                    EditNoGroupNode();
                else if (string.Equals(marker, GroupNodeViewModel.PinnedMarker, StringComparison.Ordinal))
                    EditPinnedNode();
            }
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
        // Копия экрана по хоткею (функция №30, Этап 8).
        TakeScreenshotCommand = new RelayCommand(TakeScreenshot);
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

    /// <summary>
    /// «Найти в списке» (issue #285): переходит к базе в общем списке «Все базы».
    /// Сбрасывает фильтры (вкладка/поиск/теги), принудительно раскрывает цепочку групп
    /// от корня до группы базы и выделяет базу; UI после пересборки прокрутит список к строке.
    /// </summary>
    private void ExecuteFindInList(Infobase? target)
    {
        var ib = target ?? SelectedInfobase;
        if (ib is null)
            return;

        // Переход на вкладку «Все базы» и сброс фильтров, скрывающих базу из списка.
        IsListModeAll = true;
        if (!string.IsNullOrWhiteSpace(SearchText))
            SearchText = string.Empty;
        ClearTagFilters();

        ExpandPathTo(ib);

        SelectedInfobase = ib;
        RebuildTree();
    }

    /// <summary>Раскрывает цепочку групп от корня до родителя базы (принудительно).</summary>
    private void ExpandPathTo(Infobase infobase)
    {
        foreach (var root in AllGroupNodes)
        {
            var owner = FindNodeWith(root, infobase);
            if (owner is null)
                continue;

            var chain = new List<GroupNodeViewModel>();
            for (var n = owner; n is not null; n = n.Parent)
                chain.Add(n);
            chain.Reverse();
            foreach (var n in chain)
            {
                n.SetExpandedSilent(true);
                n.NotifyIsExpanded();
            }
            return;
        }
    }
}
#endif