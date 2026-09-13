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
        RefreshFileMetadata();

        // Фоновое дочитывание свойств конфигурации здесь НЕ запускается (issue #174):
        // при импорте/обновлении списка оно было лишним, а на недоступном сервере
        // занимало ~8 с на базу. Только явная команда «Обновить информацию» читает свойства.
    }

    /// <summary>
    /// Открывает мастер добавления (выбор типа: информационная база или группа).
    /// </summary>
    private void AddInfobase(object? parameter)
    {
        var addDialog = new AddEditWindow
        {
            Owner = Application.Current.MainWindow
        };
        if (addDialog.ShowDialog() != true)
            return;

        var defaultGroupPath = SelectedGroupNode?.Group is not null
            ? SelectedGroupNode.FullPath
            : (SelectedInfobase?.Group ?? string.Empty);

        switch (addDialog.SelectedType)
        {
            case "Group":
                AddGroup();
                break;

            case "CreateEmpty":
            case "CreateFromTemplate":
            {
                var createDlg = new CreateInfobaseWindow(
                    fromTemplate: addDialog.SelectedType == "CreateFromTemplate",
                    platformVersions: _installedPlatformVersions,
                    defaultGroupPath: defaultGroupPath,
                    groups: Groups)
                {
                    Owner = Application.Current.MainWindow
                };
                if (createDlg.ShowDialog() == true && createDlg.Result is not null)
                {
                    Infobases.Add(createDlg.Result);
                    SelectedInfobase = createDlg.Result;
                    Save();
                    RebuildGroupTree();
                    ExportToIbasesAfterLocalChange();
                    _dialogs.ShowInfo(
                        string.Format(LocalizationManager.T("Main.DlgBaseCreated"), createDlg.Result.Name),
                        LocalizationManager.T("Main.DlgBaseCreatedTitle"));
                }
                break;
            }

            default:
            {
                // Существующая база — только регистрация в списке.
                var dialog = new ConnectionSettingsWindow(null, Groups, _installedPlatformVersions, defaultGroupPath,
                    availableServers: GetAvailableServers(), availablePorts: GetAvailablePorts(),
                    customLaunchParameters: CustomLaunchParameters, onCustomLaunchParametersChanged: SetCustomLaunchParameters,
                    availableRepositoryServers: GetAvailableRepositoryServers())
                {
                    Owner = Application.Current.MainWindow
                };
                if (dialog.ShowDialog() == true)
                {
                    Infobases.Add(dialog.Result);
                    SelectedInfobase = dialog.Result;
                    Save();
                    RebuildGroupTree();
                    ExportToIbasesAfterLocalChange();
                }
                break;
            }
        }
    }

    /// <summary>
    /// Открывает диалог создания новой группы. Если выбрана группа — создаётся подгруппа внутри неё.
    /// Если выбрана база — родитель = группа этой базы.
    /// </summary>
    private void AddGroup()
    {
        Group? parent = SelectedGroupNode?.Group;
        if (parent is null && !string.IsNullOrWhiteSpace(SelectedInfobase?.Group))
            parent = GroupHierarchyHelper.FindByFullPath(SelectedInfobase!.Group, Groups);

        var dialog = new GroupEditWindow(Groups, parent)
        {
            Owner = Application.Current.MainWindow
        };
        if (dialog.ShowDialog() == true)
        {
            Groups.Add(dialog.Result);
            SaveGroups();
            RebuildGroupTree();
        }
    }

    /// <summary>
    /// Возвращает список серверов 1С из клиент-серверных баз списка (без дублей, по алфавиту).
    /// Используется для выпадающего списка «Сервер» в окне настройки подключения.
    /// </summary>
    private IEnumerable<string> GetAvailableServers()
    {
        return Infobases
            .Where(b => b?.Connection?.Type == ConnectionType.ClientServer)
            .Select(b => b.Connection!.Server?.Trim() ?? string.Empty)
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Возвращает список портов серверов 1С из клиент-серверных баз списка
    /// (без дублей, по возрастанию). Используется для выпадающего списка
    /// «Порт сервера» в окне настройки подключения.
    /// </summary>
    private IEnumerable<int> GetAvailablePorts()
    {
        return Infobases
            .Where(b => b?.Connection?.Type == ConnectionType.ClientServer)
            .Select(b => b.Connection!.Port)
            .Where(p => p > 0)
            .Distinct()
            .OrderBy(p => p);
    }

    /// <summary>
    /// Возвращает список серверов хранилища конфигурации из настроек других баз списка
    /// (без дублей, по алфавиту). Используется для выпадающего списка «Сервер хранилища»
    /// в окне настройки подключения (issue #140).
    /// </summary>
    private IEnumerable<string> GetAvailableRepositoryServers()
    {
        return Infobases
            .Where(b => b?.Repository != null && !string.IsNullOrWhiteSpace(b.Repository.Server))
            .Select(b => b!.Repository!.Server.Trim())
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Возвращает информационную базу для команд действия строки: если команда вызвана с
    /// параметром-Infobase (кнопка в колонке «Действия»), используется она, иначе — выбранная база.
    /// </summary>
    private Infobase? ResolveActionTarget(object? parameter) =>
        parameter as Infobase ?? SelectedInfobase;

    /// <summary>
    /// Возвращает группу из параметра команды (кнопка в колонке «Действия» строки группы):
    /// параметром служит либо сам узел группы, либо модель группы.
    /// </summary>
    private static Group? ResolveGroup(object? parameter) =>
        parameter is Group g ? g : (parameter as GroupNodeViewModel)?.Group;

    /// <summary>
    /// Редактирует выбранный элемент: базу — через окно подключения, группу — через окно группы.
    /// Тип элемента при редактировании изменить нельзя (база не станет группой и наоборот).
    /// </summary>
    private void EditInfobase(object? parameter)
    {
        // Если выбран узел группы — редактируем группу.
        if (SelectedGroupNode?.Group is Group group)
        {
            EditGroup(group);
            return;
        }

        // Служебный узел «Без группы» — меняем его цвет и иконку, как у обычной группы.
        if (IsNoGroupNodeSelected())
        {
            EditNoGroupNode();
            return;
        }

        // Узел «Закреплённые» (без модели группы) — открываем редактор оформления узла
        // (цвет и иконку), как для «Без группы».
        // Если при этом вызвана команда с конкретной базой (кнопка строки базы внутри
        // узла), такую базу редактируем как обычно.
        if (IsPinnedNodeSelected() && parameter is not Infobase)
        {
            EditPinnedNode();
            return;
        }

        var ib = parameter as Infobase ?? SelectedInfobase;
        if (ib is null)
            return;

        var dialog = new ConnectionSettingsWindow(ib, Groups, _installedPlatformVersions,
            availableServers: GetAvailableServers(), availablePorts: GetAvailablePorts(),
            customLaunchParameters: CustomLaunchParameters, onCustomLaunchParametersChanged: SetCustomLaunchParameters,
            availableRepositoryServers: GetAvailableRepositoryServers())
        {
            Owner = Application.Current.MainWindow
        };
        if (dialog.ShowDialog() == true)
        {
            // Применяем изменения к существующему объекту, а не заменяем его новым.
            // Это важно, потому что на объект могут ссылаться и основной список, и представление.
            var target = ib;
            target.Id = dialog.Result.Id;
            target.Name = dialog.Result.Name;
            target.Group = dialog.Result.Group;
            target.Description = dialog.Result.Description;
            target.PlatformVersion = dialog.Result.PlatformVersion;
            // Поля конфигурации переносим явно, иначе введённые вручную имя/версия
            // конфигурации не сохранялись бы (issue #164).
            target.ConfigurationName = dialog.Result.ConfigurationName;
            target.ConfigurationVersion = dialog.Result.ConfigurationVersion;
            target.Architecture = dialog.Result.Architecture;
            target.LaunchMode = dialog.Result.LaunchMode;
            target.LaunchParameters = dialog.Result.LaunchParameters;
            target.DefaultLaunchMode = dialog.Result.DefaultLaunchMode;
            target.ClientType = dialog.Result.ClientType;
            target.IsFavorite = dialog.Result.IsFavorite;
            target.IsPinned = dialog.Result.IsPinned;
            target.LastLaunchDate = dialog.Result.LastLaunchDate;
            target.Tags = dialog.Result.Tags;
            target.MetadataRoot = dialog.Result.MetadataRoot;
            target.Connection = dialog.Result.Connection;
            target.EnterpriseAuth = dialog.Result.EnterpriseAuth;
            target.ConfiguratorAuth = dialog.Result.ConfiguratorAuth;
            target.Repository = dialog.Result.Repository;
            if (!string.IsNullOrWhiteSpace(dialog.Result.LaunchMode))
                target.LaunchMode = dialog.Result.LaunchMode;

            // Правка могла снять или поставить звезду — пересчитываем слоты
            // Alt+1…9, чтобы вкладка, счётчик и список горячих клавиш не
            // разъезжались (issue #194). Как в версии для Avalonia.
            SyncFavoriteHotkeys();

            InfobasesView.Refresh();
            Save();
            RebuildGroupTree();
            // Теги могли измениться (добавлены/удалены/переименованы) —
            // обновляем панель отборов и убираем из фильтра теги, которых больше нет ни на одной базе.
            PruneActiveTagFilters();
            RefreshTagFilterItems();
            // Только выгрузка: импорт сразу после правки затирал режим запуска из ibases.v8i.
            ExportToIbasesAfterLocalChange();

            // Восстанавливаем выделение отредактированной базы ПОСЛЕ пересборки. Во время
            // RebuildGroupTree WPF сбрасывает выбранный элемент дерева, и обработчик
            // SelectedItemChanged(null) очищает SelectedInfobase, поэтому цель, выставленная до
            // пересборки, была бы потеряна. Это нужно и для правки из кнопки «Действия» строки
            // (кнопка строку не выделяет), где SelectedInfobase иначе остался бы пустым.
            SelectedInfobase = target;
        }
    }

    /// <summary>
    /// Открывает диалог редактирования выбранной группы.
    /// </summary>
    private void EditGroup(Group group)
    {
        var dialog = new GroupEditWindow(Groups, group.ParentId, group)
        {
            Owner = Application.Current.MainWindow
        };
        if (dialog.ShowDialog() == true)
        {
            // Старые полные пути группы и её потомков фиксируются ДО применения изменений:
            // по ним пересчитываются пути баз (issue #171). Иначе после переименования базы
            // остаются на старом пути, уезжают в «Без группы», а сама группа становится
            // пустой и скрывается из дерева.
            var oldPathsById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var subtreeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { group.Id };
            CollectGroupDescendants(group.Id, subtreeIds);
            foreach (var id in subtreeIds)
            {
                var g = Groups.FirstOrDefault(x =>
                    string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
                if (g is not null)
                    oldPathsById[id] = GroupHierarchyHelper.GetFullPath(g, Groups);
            }
            var oldRootPath = oldPathsById.TryGetValue(group.Id, out var orp) ? orp : string.Empty;

            // Обновляем поля существующего объекта группы (сохраняем ссылку),
            // чтобы иерархия по ParentId и все привязки остались валидными.
            group.Name = dialog.Result.Name;
            group.Description = dialog.Result.Description;
            group.Color = dialog.Result.Color;
            group.IconColor = dialog.Result.IconColor ?? string.Empty;
            group.Icon = dialog.Result.Icon ?? string.Empty;
            group.ParentId = dialog.Result.ParentId;

            var newRootPath = GroupHierarchyHelper.GetFullPath(group, Groups);

            // Пересчитываем Infobase.Group у всех баз подветки на новый полный путь группы,
            // чтобы группа осталась видимой вместе со своими базами после переименования.
            RemapSubtreeInfobasePaths(oldPathsById, oldRootPath, newRootPath);

            SaveGroups();
            Save();
            RebuildGroupTree();

            // Узлы групп пересоздаются при пересборке, поэтому восстанавливаем выделение
            // отредактированной группы на новом узле (по идентификатору модели). Это нужно и для
            // правки из кнопки «Действия» строки группы, где SelectedGroupNode мог быть не выставлен.
            if (!string.IsNullOrEmpty(group.Id) && FindGroupNodeById(GroupNodes, group.Id) is { } editedNode)
                SelectedGroupNode = editedNode;
        }
    }

    /// <summary>
    /// Признак того, что выбран служебный узел «Без группы» (базы без группы).
    /// </summary>
    private bool IsNoGroupNodeSelected() =>
        SelectedGroupNode is { Group: null } node &&
        string.Equals(node.Marker, GroupNodeViewModel.NoGroupMarker, StringComparison.Ordinal);

    /// <summary>
    /// Признак того, что выбран служебный узел «Закреплённые» (закреплённые базы).
    /// Его настройки не редактируются.
    /// </summary>
    private bool IsPinnedNodeSelected() =>
        SelectedGroupNode is { Group: null } node &&
        string.Equals(node.Marker, GroupNodeViewModel.PinnedMarker, StringComparison.Ordinal);

    /// <summary>
    /// Редактирует оформление служебного узла «Без группы» (цвет и иконку)
    /// по аналогии с обычной группой. Изменения сохраняются в настройках приложения.
    /// </summary>
    private void EditNoGroupNode()
    {
        var dialog = new GroupEditWindow(Groups, _noGroupColor, _noGroupIconColor, _noGroupIcon)
        {
            Owner = Application.Current.MainWindow
        };
        if (dialog.ShowDialog() != true)
            return;

        _noGroupColor = string.IsNullOrWhiteSpace(dialog.Result.Color) ? "#6B7280" : dialog.Result.Color;
        _noGroupIconColor = string.IsNullOrWhiteSpace(dialog.Result.IconColor) ? "#FFFFFF" : dialog.Result.IconColor;
        _noGroupIcon = dialog.Result.Icon ?? string.Empty;

        RebuildGroupTree();
        ScheduleSaveSettings();
    }

    /// <summary>
    /// Редактирует оформление служебного узла «Закреплённые» (цвет и иконку)
    /// по аналогии с «Без группы». Изменения сохраняются в настройках приложения.
    /// </summary>
    private void EditPinnedNode()
    {
        var dialog = new GroupEditWindow(Groups, _pinnedColor, _pinnedIconColor, _pinnedIcon)
        {
            Owner = Application.Current.MainWindow
        };
        if (dialog.ShowDialog() != true)
            return;

        _pinnedColor = string.IsNullOrWhiteSpace(dialog.Result.Color) ? "#8B5CF6" : dialog.Result.Color;
        _pinnedIconColor = string.IsNullOrWhiteSpace(dialog.Result.IconColor) ? "#FFFFFF" : dialog.Result.IconColor;
        _pinnedIcon = dialog.Result.Icon ?? string.Empty;

        RebuildGroupTree();
        ScheduleSaveSettings();
    }

    /// <summary>
    /// Удаляет выбранный элемент: базу или группу (с учётом потомков).
    /// </summary>
    private void DeleteSelected(object? parameter)
    {
        // Удаление группы (если выбран узел группы).
        if (SelectedGroupNode?.Group is Group group)
        {
            DeleteGroup(group);
            return;
        }

        var ib = parameter as Infobase ?? SelectedInfobase;
        if (ib is null)
            return;
        var dlg = new Configuration_Management.DeleteInfobaseWindow(ib)
        {
            Owner = Application.Current?.MainWindow
        };
        if (dlg.ShowDialog() != true || !dlg.Confirmed)
            return;

        if (dlg.DeletePhysically)
        {
            var err = InfobaseMaintenanceService.TryDeleteFileBasePhysically(ib);
            if (err is not null)
            {
                _dialogs.ShowError(err, LocalizationManager.T("DeleteInfobase.PhysicalDeleteTitle"));
                // Даже при ошибке на диске продолжаем удаление из списка по запросу пользователя
                if (!_dialogs.Confirm(
                        LocalizationManager.T("Main.ConfirmDeleteFromList"),
                        LocalizationManager.T("Main.DeleteFromListTitle")))
                    return;
            }
        }

        Infobases.Remove(ib);
        if (ReferenceEquals(SelectedInfobase, ib))
            SelectedInfobase = null;
        Save();
        RebuildGroupTree();
        ExportToIbasesAfterLocalChange();
    }

    /// <summary>
    /// Удаляет группу. Если внутри группы есть дочерние группы или информационные базы,
    /// удаление запрещается: сначала нужно очистить содержимое группы.
    /// </summary>
    private void DeleteGroup(Group group)
    {
        // Проверяем наличие дочерних групп.
        var subgroupCount = Groups.Count(g =>
            string.Equals(g.ParentId, group.Id, StringComparison.OrdinalIgnoreCase));

        // Проверяем наличие баз в группе и всех её подгруппах (по полному пути группы).
        var groupPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectGroupPaths(group.Id, groupPaths);
        var infobaseCount = Infobases.Count(ib =>
            !string.IsNullOrWhiteSpace(ib.Group) &&
            groupPaths.Contains(ib.Group.Trim()));

        // Удаление группы, содержащей подгруппы или базы, запрещено.
        if (subgroupCount > 0 || infobaseCount > 0)
        {
            var reasons = new List<string>();
            if (subgroupCount > 0)
                reasons.Add(string.Format(LocalizationManager.T("Main.SubgroupsCount"), subgroupCount));
            if (infobaseCount > 0)
                reasons.Add(string.Format(LocalizationManager.T("Main.InfobasesCount"), infobaseCount));

            _dialogs.ShowWarning(
                string.Format(LocalizationManager.T("Main.DeleteGroupImpossible"), group.Name) + "\n\n" +
                LocalizationManager.T("Main.DeleteGroupContains") + "\n" +
                string.Join("\n", reasons.Select(r => "• " + r)) + ".\n\n" +
                LocalizationManager.T("Main.DeleteGroupFirstMove"),
                LocalizationManager.T("Main.DeleteGroupImpossibleTitle"));
            return;
        }

        if (!_dialogs.Confirm(
            string.Format(LocalizationManager.T("Main.DeleteGroupConfirm"), group.Name),
            LocalizationManager.T("Main.DeleteGroupConfirmTitle")))
            return;

        Groups.Remove(group);

        SelectedGroupNode = null;
        SaveGroups();
        RebuildGroupTree();
    }

    /// <summary>
    /// Собирает полные пути указанной группы и всех её потомков в иерархии.
    /// Используется для проверки наличия баз внутри группы перед её удалением.
    /// </summary>
    private void CollectGroupPaths(string groupId, ISet<string> result)
    {
        var descendants = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { groupId };
        CollectGroupDescendants(groupId, descendants);

        foreach (var id in descendants)
        {
            var g = Groups.FirstOrDefault(x =>
                string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
            if (g is not null)
            {
                result.Add(GroupHierarchyHelper.GetFullPath(g, Groups));
            }
        }
    }

    /// <summary>
    /// Собирает идентификаторы всех групп-потомков указанной группы.
    /// </summary>
    private void CollectGroupDescendants(string parentId, ISet<string> result)
    {
        var children = Groups
            .Where(g => string.Equals(g.ParentId, parentId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var child in children)
        {
            if (result.Add(child.Id))
            {
                CollectGroupDescendants(child.Id, result);
            }
        }
    }

    private void ToggleFavorite(object? parameter)
    {
        if (SelectedInfobase is null)
            return;
        ApplyFavoriteToggle(SelectedInfobase);
    }

    private void ToggleFavoriteFor(object? parameter)
    {
        if (parameter is not Infobase infobase)
            return;
        ApplyFavoriteToggle(infobase);
    }

    /// <summary>
    /// Переключает избранное без полной перестройки дерева, если фильтр не скрывает базу.
    /// Иконка обновляется через INotifyPropertyChanged; сохранение — отложенное.
    /// При добавлении в избранное назначается свободный слот Alt+1…9.
    /// </summary>
    private void ApplyFavoriteToggle(Infobase infobase)
    {
        infobase.IsFavorite = !infobase.IsFavorite;

        var key = FavoriteKey(infobase);
        if (infobase.IsFavorite)
        {
            if (!_favoriteHotkeyIds.Contains(key) && _favoriteHotkeyIds.Count < 9)
                _favoriteHotkeyIds.Add(key);
        }
        else
        {
            _favoriteHotkeyIds.Remove(key);
        }

        SyncFavoriteHotkeys();

        // Перестройка нужна только если список/дерево должны изменить состав
        // (фильтр «Только избранные», поиск, отбор по тегу).
        if (ShowFavoritesOnly
            || !string.IsNullOrWhiteSpace(SearchText)
            || HasActiveTagFilter)
        {
            InfobasesView.Refresh();
            RebuildGroupTree();
        }

        ScheduleSave();
        ScheduleSaveSettings();
    }

    /// <summary>Запускает избранную базу по номеру горячей клавиши (1–9 → Alt+N).</summary>

    /// <summary>Недавние базы по дате последнего запуска (для меню трея).</summary>
    public System.Collections.Generic.IReadOnlyList<Models.Infobase> GetRecentInfobases(int count = 7)
    {
        return Infobases
            .Where(i => i.LastLaunchDate.HasValue)
            .OrderByDescending(i => i.LastLaunchDate)
            .Take(Math.Max(1, count))
            .ToList();
    }

    /// <summary>Запуск базы по Id из меню трея (без активации окна).</summary>
    public void LaunchInfobaseById(string id, bool isConfigurator)
    {
        var ib = Infobases.FirstOrDefault(i => i.Id == id);
        if (ib is null) return;

        bool ok;
        if (isConfigurator)
            ok = _launcher.Launch(ib, Services.OneCLaunchMode.Configurator);
        else
            ok = LaunchEnterpriseWithSessionOverrides(ib);

        if (ok)
        {
            ib.LastLaunchDate = DateTime.Now;
            ib.AddLaunchHistory(isConfigurator ? "Configurator" : "Enterprise", "tray");
            InfobasesView.Refresh();
            Save();
            _logger.Info($"[tray] Запущена «{ib.Name}» ({(isConfigurator ? "Конфигуратор" : "Предприятие")})");
            NotifyAfterLaunch();
        }
        else
        {
            _logger.Warn($"[tray] Не удалось запустить «{ib.Name}»");
        }
    }

    public void LaunchFavoriteByHotkey(int number)
    {
        if (number < 1 || number > 9 || number > _favoriteHotkeyIds.Count)
            return;
        var key = _favoriteHotkeyIds[number - 1];
        var ib = FindByFavoriteKey(key);
        if (ib is null)
            return;

        SelectedInfobase = ib;
        // Запуск напрямую через лаунчер — не зависит от CanExecute команд UI.
        var ok = _launcher.Launch(ib, OneCLaunchMode.Enterprise);
        if (ok)
        {
            ib.LastLaunchDate = DateTime.Now;
            ScheduleSave();
            _logger.Info($"Запущена избранная база «{ib.Name}» по Alt+{number}");
            NotifyAfterLaunch();
        }
        else
        {
            _logger.Warn($"Не удалось запустить избранную базу «{ib.Name}» по Alt+{number}");
        }
    }

    /// <summary>Возвращает номер горячей клавиши (1–9) для базы или 0, если не назначен.</summary>
    public int GetFavoriteHotkeyNumber(Infobase infobase)
    {
        var key = FavoriteKey(infobase);
        var idx = _favoriteHotkeyIds.IndexOf(key);
        return idx >= 0 ? idx + 1 : 0;
    }

    /// <summary>
    /// Устанавливает поле сортировки. Повторный клик по тому же полю меняет направление.
    /// </summary>
    public void SetSortField(string field)
    {
        if (string.Equals(_sortField, field, StringComparison.OrdinalIgnoreCase))
            _sortAscending = !_sortAscending;
        else
        {
            _sortField = field;
            _sortAscending = field != "LastLaunchDate"; // дату удобнее сначала по убыванию
        }
        ApplySortDescriptions();
        InfobasesView.Refresh();
        RebuildGroupTree();
        OnPropertyChanged(nameof(SortField));
        OnPropertyChanged(nameof(SortAscending));
        ScheduleSaveSettings();
    }

    private void ApplySortDescriptions()
    {
        InfobasesView.SortDescriptions.Clear();
        // Закреплённые всегда сверху.
        InfobasesView.SortDescriptions.Add(new SortDescription(nameof(Infobase.GroupSortOrder), ListSortDirection.Ascending));
        var dir = _sortAscending ? ListSortDirection.Ascending : ListSortDirection.Descending;
        switch (_sortField)
        {
            case "LastLaunchDate":
                InfobasesView.SortDescriptions.Add(new SortDescription(nameof(Infobase.LastLaunchDate), dir));
                InfobasesView.SortDescriptions.Add(new SortDescription(nameof(Infobase.Name), ListSortDirection.Ascending));
                break;
            case "SortOrder":
                InfobasesView.SortDescriptions.Add(new SortDescription(nameof(Infobase.SortOrder), dir));
                InfobasesView.SortDescriptions.Add(new SortDescription(nameof(Infobase.Name), ListSortDirection.Ascending));
                break;
            default:
                InfobasesView.SortDescriptions.Add(new SortDescription(nameof(Infobase.Name), dir));
                break;
        }
    }

    /// <summary>
    /// Сортирует базы согласно текущему полю (_sortField) и направлению.
    /// Закреплённые (GroupSortOrder) всегда идут первыми.
    /// </summary>
    private IEnumerable<Infobase> ApplyCurrentSort(IEnumerable<Infobase> source)
    {
        var query = source.OrderBy(i => i.GroupSortOrder);
        query = _sortField switch
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
        return query;
    }

    /// <summary>Стабильный ключ базы для слотов горячих клавиш.</summary>
    private static string FavoriteKey(Infobase ib) =>
        !string.IsNullOrEmpty(ib.Id) ? ib.Id : "name:" + ib.Name;

    /// <summary>
    /// Назначает слоты Alt+1…9 существующим избранным и обновляет номера на карточках.
    /// </summary>
    private void SyncFavoriteHotkeys()
    {
        try
        {
            if (Infobases is null)
                return;

            // Удаляем ключи, которых больше нет среди избранных: слот должен
            // соответствовать только текущим избранным базам, иначе вкладка,
            // счётчик и список горячих клавиш разъезжаются (issue #194).
            _favoriteHotkeyIds.RemoveAll(key =>
                !Infobases.Any(ib => ib.IsFavorite && FavoriteKey(ib) == key));

            // Добавляем избранные без слота (в порядке имени).
            foreach (var ib in Infobases.Where(i => i.IsFavorite).OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
            {
                var key = FavoriteKey(ib);
                if (!_favoriteHotkeyIds.Contains(key) && _favoriteHotkeyIds.Count < 9)
                    _favoriteHotkeyIds.Add(key);
            }

            // Проставляем номера на объектах Infobase для UI.
            foreach (var ib in Infobases)
            {
                var key = FavoriteKey(ib);
                var idx = _favoriteHotkeyIds.IndexOf(key);
                ib.FavoriteHotkeyNumber = idx >= 0 ? idx + 1 : 0;
            }

            FavoriteHotkeysChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // не роняем приложение из‑за избранного
        }
    }

    /// <summary>Публичный доступ к упорядоченному списку ключей избранного (для настроек).</summary>
    public IReadOnlyList<string> FavoriteHotkeyIds => _favoriteHotkeyIds;

    /// <summary>Возвращает базу по ключу слота избранного.</summary>
    public Infobase? FindByFavoriteKey(string key) =>
        Infobases.FirstOrDefault(ib => FavoriteKey(ib) == key);

    /// <summary>
    /// Заменяет порядок слотов горячих клавиш (из окна настроек).
    /// </summary>
    public void SetFavoriteHotkeyOrder(IEnumerable<string> orderedKeys)
    {
        _favoriteHotkeyIds.Clear();
        foreach (var key in orderedKeys.Where(k => !string.IsNullOrEmpty(k)).Take(9))
            _favoriteHotkeyIds.Add(key);
        SyncFavoriteHotkeys();
        ScheduleSaveSettings();
    }

    public bool CloseToTray
    {
        get => _closeToTray;
        set
        {
            if (SetProperty(ref _closeToTray, value))
                ScheduleSaveSettings();
        }
    }

    public bool ShowTrayIcon
    {
        get => _showTrayIcon;
        set
        {
            if (SetProperty(ref _showTrayIcon, value))
                ScheduleSaveSettings();
        }
    }

    /// <summary>Esc сворачивает окно в трей (если значок в трее включён).</summary>
    public bool EscapeToTray
    {
        get => _escapeToTray;
        set
        {
            if (SetProperty(ref _escapeToTray, value))
                ScheduleSaveSettings();
        }
    }

    /// <summary>
    /// Глобальная настройка «что делать с окном после запуска базы/конфигуратора 1С»:
    /// "None" / "MinimizeToTray" / "Close".
    /// </summary>
    public string AfterLaunchAction
    {
        get => _afterLaunchAction;
        set
        {
            if (SetProperty(ref _afterLaunchAction, value))
                ScheduleSaveSettings();
        }
    }

    /// <summary>
    /// Настраиваемый шаблон имени COM-коннектора 1С (issue #175).
    /// Пустая строка — стандартные ProgID V85/V83/V82/V81.COMConnector; иначе шаблон
    /// разворачивается по версии платформы каждой базы (плейсхолдеры %V12%/%V3%/%V4%)
    /// и пробуется первым в переборе. Действует сразу: новое значение передаётся коннектору
    /// здесь же (issue #175) — иначе настройка применялась бы только после перезапуска.
    /// </summary>
    public string ComConnectorNameTemplate
    {
        get => _comConnectorNameTemplate;
        set
        {
            var normalized = value?.Trim() ?? string.Empty;
            if (!SetProperty(ref _comConnectorNameTemplate, normalized))
                return;

            // Коннектор кэширует шаблон; передаём новое значение сразу, чтобы оно
            // действовало без перезапуска и не зависело от того, когда настройки лягут
            // на диск — запись отложена, а фоновое COM-чтение может случиться раньше
            // (issue #175).
            OneCComConnector.ApplyTemplate(normalized);
            ScheduleSaveSettings();
        }
    }

    /// <summary>
    /// Таймаут определения свойств конфигурации через COM-коннектор (issue #174), миллисекунды.
    /// По умолчанию 30000 мс — первое COM-подключение к клиент-серверной базе (особенно
    /// localhost с холодным стартом сервера и обращением к лицензиям) часто превышает прежние
    /// 8000 мс. Чтение выполняется только по явной команде, поэтому длинный таймаут не мешает
    /// старту. Минимум 1000 мс.
    /// </summary>
    public int ComDetectTimeoutMs
    {
        get => _comDetectTimeoutMs;
        set
        {
            var v = Math.Max(1000, value);
            if (SetProperty(ref _comDetectTimeoutMs, v))
                ScheduleSaveSettings();
        }
    }

    /// <summary>
    /// Запрос к главному окну выполнить действие после успешного запуска базы/конфигуратора
    /// (свернуть или увести в трей согласно глобальной настройке).
    /// </summary>
    public event Action<Models.AfterLaunchAction>? AfterLaunchRequested;

    /// <summary>Оповещает главное окно о необходимости выполнить действие «после запуска».</summary>
    public void NotifyAfterLaunch()
    {
        var action = Models.AfterLaunchActionHelper.Parse(_afterLaunchAction);
        if (action != Models.AfterLaunchAction.None)
            AfterLaunchRequested?.Invoke(action);
    }

    /// <summary>Обновляет список каталогов шаблонов из настроек.</summary>
    public void SetTemplateCatalogPaths(System.Collections.Generic.IEnumerable<string> paths)
    {
        _templateCatalogPaths = paths?
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? new System.Collections.Generic.List<string>();
        Services.OneCTemplateService.SetUserTemplatePaths(_templateCatalogPaths);
        ScheduleSaveSettings();
    }

public string HotkeyEnterprise
    {
        get => _hotkeyEnterprise;
        set
        {
            if (SetProperty(ref _hotkeyEnterprise, NormalizeHotkey(value, "F3")))
                ScheduleSaveSettings();
        }
    }

    public string HotkeyConfigurator
    {
        get => _hotkeyConfigurator;
        set
        {
            if (SetProperty(ref _hotkeyConfigurator, NormalizeHotkey(value, "F4")))
                ScheduleSaveSettings();
        }
    }

    public string HotkeyFavorite
    {
        get => _hotkeyFavorite;
        set
        {
            if (SetProperty(ref _hotkeyFavorite, NormalizeHotkey(value, "")))
                ScheduleSaveSettings();
        }
    }

    public string HotkeyEdit
    {
        get => _hotkeyEdit;
        set
        {
            if (SetProperty(ref _hotkeyEdit, NormalizeHotkey(value, "")))
                ScheduleSaveSettings();
        }
    }

    public string HotkeyDelete
    {
        get => _hotkeyDelete;
        set
        {
            if (SetProperty(ref _hotkeyDelete, NormalizeHotkey(value, "")))
                ScheduleSaveSettings();
        }
    }

    public string HotkeyClearCache
    {
        get => _hotkeyClearCache;
        set
        {
            if (SetProperty(ref _hotkeyClearCache, NormalizeHotkey(value, "")))
                ScheduleSaveSettings();
        }
    }

    public string HotkeyAdd
    {
        get => _hotkeyAdd;
        set
        {
            if (SetProperty(ref _hotkeyAdd, NormalizeHotkey(value, "")))
                ScheduleSaveSettings();
        }
    }

    public string HotkeyPin
    {
        get => _hotkeyPin;
        set
        {
            if (SetProperty(ref _hotkeyPin, NormalizeHotkey(value, "")))
                ScheduleSaveSettings();
        }
    }

    /// <summary>Горячая клавиша показа вкладки «Все базы». Пусто — не назначена.</summary>
    public string HotkeyShowAll
    {
        get => _hotkeyShowAll;
        set
        {
            if (SetProperty(ref _hotkeyShowAll, NormalizeHotkey(value, "")))
                ScheduleSaveSettings();
        }
    }

    /// <summary>Горячая клавиша показа вкладки «Избранное». Пусто — не назначена.</summary>
    public string HotkeyShowFavorites
    {
        get => _hotkeyShowFavorites;
        set
        {
            if (SetProperty(ref _hotkeyShowFavorites, NormalizeHotkey(value, "")))
                ScheduleSaveSettings();
        }
    }

    /// <summary>Горячая клавиша показа вкладки «Недавние». Пусто — не назначена.</summary>
    public string HotkeyShowRecent
    {
        get => _hotkeyShowRecent;
        set
        {
            if (SetProperty(ref _hotkeyShowRecent, NormalizeHotkey(value, "")))
                ScheduleSaveSettings();
        }
    }

    /// <summary>Горячая клавиша очистки строки поиска. Пусто — не назначена.</summary>
    public string HotkeyClearSearch
    {
        get => _hotkeyClearSearch;
        set
        {
            if (SetProperty(ref _hotkeyClearSearch, NormalizeHotkey(value, "Ctrl+Shift+C")))
                ScheduleSaveSettings();
        }
    }

    /// <summary>Горячая клавиша сброса фильтра по тегам. Пусто — не назначена.</summary>
    public string HotkeyClearTags
    {
        get => _hotkeyClearTags;
        set
        {
            if (SetProperty(ref _hotkeyClearTags, NormalizeHotkey(value, "Ctrl+Shift+T")))
                ScheduleSaveSettings();
        }
    }

    /// <summary>Горячая клавиша переключения подробностей правой панели информации. Пусто — не назначена (issue #172).</summary>
    public string HotkeyRightPanelDetails
    {
        get => _hotkeyRightPanelDetails;
        set
        {
            if (SetProperty(ref _hotkeyRightPanelDetails, NormalizeHotkey(value, "")))
                ScheduleSaveSettings();
        }
    }
 
    private static string NormalizeHotkey(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    public string SortField => _sortField;
    public bool SortAscending => _sortAscending;

    private void TogglePin(object? parameter)
    {
        if (SelectedInfobase is null)
            return;
        ApplyPinToggle(SelectedInfobase);
    }

    private void TogglePinFor(object? parameter)
    {
        if (parameter is not Infobase infobase)
            return;
        ApplyPinToggle(infobase);
    }

    /// <summary>
    /// Переключает закрепление: обновляет блок «Закреплённые» точечно, без полной
    /// перестройки дерева групп. Сохранение на диск — отложенное (не блокирует UI).
    /// </summary>
    private void ApplyPinToggle(Infobase infobase)
    {
        infobase.IsPinned = !infobase.IsPinned;
        UpdatePinnedSection(infobase);
        ScheduleSave();
    }

    /// <summary>
    /// Добавляет или убирает базу в узле «Закреплённые» без RebuildGroupTree.
    /// </summary>
    private void UpdatePinnedSection(Infobase infobase)
    {
        // При отключённой группировке или активном временном фильтре
        // (Избранное / Недавние / тег / поиск) закрепление не отображается —
        // влияет только на данные и проявится после выхода из режима фильтра.
        if (!_groupByGroup || IsFilterModeActive())
        {
            return;
        }

        var pinned = GroupNodes.FirstOrDefault(n => n.Group is null &&
            string.Equals(n.Marker, GroupNodeViewModel.PinnedMarker, StringComparison.Ordinal));

        if (infobase.IsPinned)
        {
            if (pinned is null)
            {
                pinned = new GroupNodeViewModel(
                    null,
                    marker: GroupNodeViewModel.PinnedMarker,
                    defaultColor: _pinnedColor,
                    defaultIconColor: _pinnedIconColor,
                    defaultIcon: _pinnedIcon) { IsExpanded = true };
                pinned.Infobases.Add(infobase);
                pinned.PopulateItems(); // NotifyCountChanged внутри
                GroupNodes.Insert(0, pinned);
                _groupNodes.Insert(0, pinned);
                return;
            }

            if (!pinned.Infobases.Contains(infobase))
            {
                pinned.Infobases.Add(infobase);
                pinned.PopulateItems();
            }
            else
            {
                pinned.NotifyCountChanged();
            }
        }
        else if (pinned is not null)
        {
            pinned.Infobases.Remove(infobase);
            if (pinned.Infobases.Count == 0)
            {
                GroupNodes.Remove(pinned);
                _groupNodes.Remove(pinned);
            }
            else
            {
                pinned.PopulateItems();
            }
        }
    }

    /// <summary>
    /// Откладывает сохранение списка баз (debounce), чтобы серия быстрых кликов
    /// по звёздочке/закреплению не писала JSON на каждый клик и не блокировала UI.
    /// </summary>
}
#endif
