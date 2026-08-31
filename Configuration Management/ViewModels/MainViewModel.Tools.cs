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
    /// <summary>
    /// Нужно ли автоматически разворачивать группы с видимыми базами:
    /// при поиске, фильтре по тегам, режиме «Избранное» или «Недавние».
    /// </summary>
    private bool ShouldAutoExpandGroups() =>
        !string.IsNullOrWhiteSpace(SearchText)
        || HasActiveTagFilter
        || _listViewMode == ListViewMode.Favorites
        || _listViewMode == ListViewMode.Recent;

    /// <summary>
    /// Активен ли временный режим фильтра (Избранное / Недавние / отбор по тегу / поиск),
    /// при котором группы и закрепления временно скрываются, чтобы не было дублей.
    /// </summary>
    private bool IsFilterModeActive() =>
        _listViewMode != ListViewMode.All
        || HasActiveTagFilter
        || !string.IsNullOrWhiteSpace(SearchText);

    /// <summary>
    /// Разворачивает узлы дерева, в которых есть базы (или вложенные с базами).
    /// Используется при поиске, фильтре по тегу, избранном и недавних.
    /// </summary>
    private static void ExpandAllNodesWithContent(IEnumerable<GroupNodeViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            if (node.ContainsInfobases)
                node.SetExpandedSilent(true);
            ExpandAllNodesWithContent(node.Children);
        }
    }


    /// <summary>
    /// Состав списка обновлён: окну нужно вернуть выделение строки и клавиатурный фокус.
    /// Поднимается после полной пересборки дерева, когда прежние контейнеры строк
    /// уничтожены заменой коллекции <see cref="GroupNodes"/>.
    /// </summary>
    public event Action? TreeRebuilt;

    /// <summary>
    /// Заменяет содержимое GroupNodes с минимумом лишних уведомлений UI.
    /// </summary>
    private void ReplaceGroupNodes(List<GroupNodeViewModel> next)
    {
        // Новая коллекция вместо Clear/Add: один сброс ItemsSource у TreeView,
        // без промежуточных CollectionChanged на каждый корневой узел.
        GroupNodes = new ObservableCollection<GroupNodeViewModel>(next);
        OnPropertyChanged(nameof(GroupNodes));
        TreeRebuilt?.Invoke();
    }

    /// <summary>
    /// Отложенное сохранение настроек (фильтр избранного, группировка и т.п.) без блокировки UI.
    /// </summary>
    private void ScheduleSaveSettings()
    {
        _ = Task.Run(() =>
        {
            try
            {
                // Небольшой debounce, если пользователь быстро щёлкает фильтры.
                Thread.Sleep(150);
                Application.Current?.Dispatcher.Invoke(SaveSettings);
            }
            catch (Exception ex)
            {
                _logger.Error("Ошибка отложенного сохранения настроек", ex);
            }
        });
    }

    /// <summary>
    /// Применяет сохранённое состояние развёрнутости к узлам дерева.
    /// </summary>
    private void ApplyExpandedState(IEnumerable<GroupNodeViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            // Для реальных групп ключом служит полный путь, для служебных узлов —
            // внутренний маркер (не зависит от языка; единый формат).
            var key = node.NodeKey;
            node.SetExpandedSilent(!IsGroupCollapsed(key));
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

    /// <summary>
    /// Открывает окно настроек приложения (платформы, группы, дополнительные функции).
    /// </summary>
    private void OpenSettings(object? parameter)
    {
        var dialog = new SettingsWindow(this)
        {
            Owner = Application.Current.MainWindow
        };
        dialog.ShowDialog();
    }

    /// <summary>
    /// Открывает диалог ввода ссылки на информационную базу (аналог «Перейти по ссылке»
    /// в стандартном загрузчике 1С) и запускает указанную базу в 1С:Предприятие.
    /// </summary>
    private void OpenInfobaseByLink(object? parameter)
    {
        var dialog = new LinkInputWindow
        {
            Owner = Application.Current.MainWindow
        };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.Result))
            return;

        var link = dialog.Result;
        _logger.Info($"Запуск 1С по ссылке: {link}");
        OneCLauncher.LaunchByLink(link);
    }

    /// <summary>
    /// Показывает окно с предложением загрузить базы из файла ibases.v8i,
    /// если список информационных баз пуст. При согласии выполняет импорт.
    /// </summary>
    private void PromptImportFromIbasesV8i()
    {
        if (!_dialogs.Confirm(LocalizationManager.T("Main.PromptImportEmpty"),
            LocalizationManager.T("Main.LoadBasesTitle")))
            return;

        // Сначала пытаемся найти файл ibases.v8i автоматически в стандартном месте.
        var filePath = IbasesV8iImporter.FindDefaultPath();

        // Если файл не найден — предлагаем выбрать его вручную.
        if (filePath is null)
        {
            var dialog = new OpenFileDialog
            {
                Title = LocalizationManager.T("Settings.Ibases.FileDialogTitle"),
                Filter = LocalizationManager.T("Main.IbasesFileFilter"),
                CheckFileExists = true,
                Multiselect = false
            };

            if (dialog.ShowDialog() != true)
                return;

            filePath = dialog.FileName;
        }

        try
        {
            var importResult = _ibasesSync.Import(filePath, Infobases, Groups);

            InfobasesView.Refresh();
            Save();
            SaveGroups();
            RebuildGroupTree();

            _dialogs.ShowInfo(
                string.Format(LocalizationManager.T("Main.ImportDone"),
                    importResult.Added, importResult.Updated, importResult.Skipped, importResult.GroupsCreated),
                LocalizationManager.T("Main.ImportIbasesTitle"));
        }
        catch (Exception ex)
        {
            _dialogs.ShowError(
                string.Format(LocalizationManager.T("Main.ErrImportFailed"), ex.Message),
                LocalizationManager.T("Main.ImportErrorTitle"));
        }
    }

    /// <summary>
    /// Ручная синхронизация с ibases.v8i по режиму из настроек приложения.
    /// Если синхронизация отключена — сообщает об этом и предлагает открыть настройки.
    /// </summary>
    private void SynchronizeWithIbasesManual(object? parameter)
    {
        if (_ibasesSyncMode == IbasesSyncMode.None)
        {
            if (_dialogs.Confirm(
                    LocalizationManager.T("Main.SyncDisabledConfirm"),
                    LocalizationManager.T("Main.SyncIbasesTitle")))
            {
                OpenSettings(null);
            }
            return;
        }

        var filePath = ResolveIbasesFilePath();
        if (filePath is null)
        {
            _dialogs.ShowInfo(
                LocalizationManager.T("Main.ErrSyncNoPath"),
                LocalizationManager.T("Main.SyncIbasesTitle"));
            return;
        }

        var modeText = _ibasesSyncMode switch
        {
            IbasesSyncMode.Import => LocalizationManager.T("Main.SyncModeImport"),
            IbasesSyncMode.Export => LocalizationManager.T("Main.SyncModeExport"),
            IbasesSyncMode.Both => LocalizationManager.T("Main.SyncModeBoth"),
            _ => LocalizationManager.T("Main.SyncModeUnknown")
        };

        try
        {
            // Сбрасываем предыдущее сообщение, чтобы увидеть актуальный результат.
            SyncMessage = string.Empty;
            var ok = SynchronizeWithIbases();

            var status = string.IsNullOrWhiteSpace(SyncMessage)
                ? (ok ? LocalizationManager.T("Main.SyncDoneNoChanges") : LocalizationManager.T("Main.SyncNotPerformed"))
                : SyncMessage;

            _dialogs.ShowInfo(
                string.Format(LocalizationManager.T("Main.SyncResultFormat"), modeText, filePath, status),
                LocalizationManager.T("Main.SyncIbasesTitle"));
        }
        catch (Exception ex)
        {
            _dialogs.ShowError(
                string.Format(LocalizationManager.T("Main.ErrSyncFailed"), ex.Message),
                LocalizationManager.T("Sync.Failed"));
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
                Title = LocalizationManager.T("Settings.Ibases.FileDialogTitle"),
                Filter = LocalizationManager.T("Main.IbasesFileFilter"),
                CheckFileExists = true,
                Multiselect = false
            };

            if (dialog.ShowDialog() != true)
                return;

            filePath = dialog.FileName;
        }

        try
        {
            var result = _ibasesSync.Import(filePath, Infobases, Groups);

            InfobasesView.Refresh();
            Save();
            SaveGroups();
            RebuildGroupTree();

            _dialogs.ShowInfo(
                string.Format(LocalizationManager.T("Main.ImportDone"),
                    result.Added, result.Updated, result.Skipped, result.GroupsCreated),
                LocalizationManager.T("Main.ImportIbasesTitle"));
        }
        catch (Exception ex)
        {
            _dialogs.ShowError(
                string.Format(LocalizationManager.T("Main.ErrImportFailed"), ex.Message),
                LocalizationManager.T("Main.ImportErrorTitle"));
        }
    }

    /// <summary>
    /// Экспортирует список информационных баз в выбранный JSON-файл.
    /// </summary>
    private void ExportInfobases(object? parameter)
    {
        if (Infobases.Count == 0)
        {
            _dialogs.ShowInfo(LocalizationManager.T("Main.ExportEmpty"),
                LocalizationManager.T("Main.ExportBasesTitle"));
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = LocalizationManager.T("Main.ExportBasesDialogTitle"),
            Filter = LocalizationManager.T("Main.JsonFileFilter"),
            DefaultExt = ".json",
            FileName = BuildExportFileName("infobases_export", ".json"),
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

            _dialogs.ShowInfo(
                string.Format(LocalizationManager.T("Main.ExportDone"),
                    Infobases.Count, Groups.Count, dialog.FileName),
                LocalizationManager.T("Main.ExportBasesTitle"));
        }
        catch (Exception ex)
        {
            _dialogs.ShowError(
                string.Format(LocalizationManager.T("Main.ErrExportFailed"), ex.Message),
                LocalizationManager.T("Main.ExportErrorTitle"));
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
            Title = LocalizationManager.T("Main.ImportBasesDialogTitle"),
            Filter = LocalizationManager.T("Main.JsonFileFilter"),
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
                _dialogs.ShowWarning(LocalizationManager.T("Main.ImportNoBases"),
                    LocalizationManager.T("Main.LoadBasesTitle"));
                return;
            }

            if (!_dialogs.Confirm(
                string.Format(LocalizationManager.T("Main.ImportConfirm"), loaded.Count, loadedGroups.Count),
                LocalizationManager.T("Main.LoadBasesTitle")))
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

            _dialogs.ShowInfo(
                string.Format(LocalizationManager.T("Main.ImportDoneMsg"), loaded.Count, loadedGroups.Count),
                LocalizationManager.T("Main.LoadBasesTitle"));
        }
        catch (Exception ex)
        {
            _dialogs.ShowError(
                string.Format(LocalizationManager.T("Main.ErrLoadFailed"), ex.Message),
                LocalizationManager.T("Main.LoadErrorTitle"));
        }
    }

    /// <summary>
    /// Очищает весь список информационных баз и групп.
    /// </summary>
    private void ClearAllInfobases(object? parameter)
    {
        if (Infobases.Count == 0 && Groups.Count == 0)
        {
            _dialogs.ShowInfo(LocalizationManager.T("Main.ClearAllAlreadyEmpty"),
                LocalizationManager.T("Main.ClearAllTitle"));
            return;
        }

        if (!_dialogs.Confirm(
            string.Format(LocalizationManager.T("Main.ClearAllConfirm"), Infobases.Count, Groups.Count),
            LocalizationManager.T("Main.ClearAllTitle")))
            return;

        Infobases.Clear();
        Groups.Clear();
        SelectedInfobase = null;
        InfobasesView.Refresh();
        Save();
        SaveGroups();
        RebuildGroupTree();

        _dialogs.ShowInfo(LocalizationManager.T("Main.ClearAllDone"),
            LocalizationManager.T("Main.ClearAllTitle"));
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
            _dialogs.ShowError(
                string.Format(LocalizationManager.T("Main.ErrCopyConnection"), ex.Message),
                LocalizationManager.T("Main.CopyErrorTitle"));
        }
    }

    /// <summary>
    /// Очищает локальный кеш 1С выбранной базы (программный и пользовательский).
    /// </summary>
    private void ClearCache(object? parameter)
    {
        OpenCacheClean(OneCCacheKind.All, parameter as Infobase);
    }

    /// <summary>
    /// Открывает диалог выбора типа кеша и баз 1С, после подтверждения выполняет очистку.
    /// </summary>
    /// <param name="kind">Тип кеша, выбранный по умолчанию.</param>
    /// <param name="defaultInfobase">База, выделенная по умолчанию (если указана).</param>
    private void OpenCacheClean(OneCCacheKind kind, Infobase? defaultInfobase = null)
    {
        if (Infobases.Count == 0)
        {
            _dialogs.ShowInfo(LocalizationManager.T("Main.CacheEmpty"),
                LocalizationManager.T("Main.ClearCacheDlgTitle"));
            return;
        }

        var dialog = new CacheCleanWindow(Infobases, kind, defaultInfobase ?? SelectedInfobase)
        {
            Owner = Application.Current.MainWindow
        };

        if (dialog.ShowDialog() != true)
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

        if (!_dialogs.Confirm(
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

            _dialogs.ShowInfo(string.Join("\n\n", resultParts), LocalizationManager.T("Main.ClearCacheDlgTitle"));
        }
        catch (Exception ex)
        {
            _dialogs.ShowError(
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

    /// <summary>Открывает каталог файловой ИБ в проводнике Windows.</summary>
    private void OpenInfobaseFolder(object? parameter)
    {
        var ib = parameter as Infobase ?? SelectedInfobase;
        if (ib is null) return;

        if (ib.Connection.Type != ConnectionType.File)
        {
            _dialogs.ShowInfo(LocalizationManager.T("Main.OpenFolderOnlyFile"),
                LocalizationManager.T("Main.OpenCatalogTitle"));
            return;
        }

        if (!InfobaseMaintenanceService.OpenInfobaseFolder(ib))
        {
            _dialogs.ShowError(
                string.Format(LocalizationManager.T("Main.ErrOpenFolder"), ib.Connection.FilePath),
                LocalizationManager.T("Main.OpenCatalogTitle"));
        }
    }

    /// <summary>Создаёт ярлык .lnk на рабочем столе для запуска базы.</summary>
    private void CreateDesktopShortcut(object? parameter)
    {
        var ib = parameter as Infobase ?? SelectedInfobase;
        if (ib is null) return;

        if (InfobaseMaintenanceService.CreateDesktopShortcut(ib))
        {
            _dialogs.ShowInfo(
                string.Format(LocalizationManager.T("Main.ShortcutCreatedFull"), ib.Name),
                LocalizationManager.T("Main.ShortcutTitle"));
            _logger.Info($"Создан ярлык 1С на рабочем столе для базы «{ib.Name}»");
        }
        else
        {
            _dialogs.ShowError(
                LocalizationManager.T("Main.ErrShortcutCreateFull"),
                LocalizationManager.T("Main.ShortcutTitle"));
        }
    }

    /// <summary>Удаляет из списка файловые базы, у которых нет 1Cv8.1CD / каталога.</summary>
    private void RemoveMissingFileBases(object? parameter)
    {
        var missing = Infobases.Where(ib => !InfobaseMaintenanceService.FileBaseExists(ib)).ToList();
        if (missing.Count == 0)
        {
            _dialogs.ShowInfo(LocalizationManager.T("Main.MissingNone"),
                LocalizationManager.T("Main.CheckFileBasesTitle"));
            return;
        }

        var preview = string.Join("\n", missing.Take(15).Select(ib => "• " + ib.Name));
        if (missing.Count > 15)
            preview += string.Format(LocalizationManager.T("Main.MissingMore"), missing.Count - 15);

        if (!_dialogs.Confirm(
                string.Format(LocalizationManager.T("Main.MissingConfirm"), missing.Count, preview),
                LocalizationManager.T("Main.RemoveMissingTitle")))
            return;

        foreach (var ib in missing)
            Infobases.Remove(ib);

        RebuildGroupTree();
        InfobasesView.Refresh();
        Save();
        _logger.Info($"Удалено отсутствующих файловых баз: {missing.Count}");
        _dialogs.ShowInfo(
            string.Format(LocalizationManager.T("Main.MissingRemoved"), missing.Count),
            LocalizationManager.T("Main.RemoveMissingTitle"));
    }

    /// <summary>Завершает процессы 1cv8 / 1cv8c и связанные.</summary>
    private void KillOneCProcesses(object? parameter)
    {
        var count = InfobaseMaintenanceService.CountOneCProcesses();
        if (count == 0)
        {
            _dialogs.ShowInfo(LocalizationManager.T("Main.NoProcesses"),
                LocalizationManager.T("Main.OneCProcessesTitle"));
            return;
        }

        if (!_dialogs.Confirm(
                string.Format(LocalizationManager.T("Main.KillProcessesConfirm"), count),
                LocalizationManager.T("Main.KillProcessesTitle")))
            return;

        var killed = InfobaseMaintenanceService.KillOneCProcesses();
        _logger.Info($"Завершено процессов 1С: {killed}");
        _dialogs.ShowInfo(
            string.Format(LocalizationManager.T("Main.ProcessesKilled"), killed),
            LocalizationManager.T("Main.OneCProcessesTitle"));
    }


    /// <summary>
    /// Фоново считывает имя и версию конфигурации для баз, где они ещё не заполнены.
    /// </summary>
    private void RefreshConfigurationInfoAsync()
    {
        var targets = Infobases
            .Where(ib => string.IsNullOrWhiteSpace(ib.ConfigurationName)
                         || string.IsNullOrWhiteSpace(ib.ConfigurationVersion))
            .ToList();
        if (targets.Count == 0) return;

        _ = Task.Run(() =>
        {
            var any = false;
            foreach (var ib in targets)
            {
                try
                {
                    if (ConfigurationInfoService.TryApply(ib, overwriteExisting: false))
                        any = true;
                }
                catch { }
            }

            if (!any) return;

            try
            {
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    InfobasesView?.Refresh();
                    Save();
                });
            }
            catch { }
        });
    }

    /// <summary>
    /// Точечно запрашивает и заполняет информацию о конфигурации выбранной базы
    /// (из контекстного меню). Выполняется в фоне, чтобы не блокировать UI.
    /// </summary>
    private void RefreshConfigurationInfo(object? parameter)
    {
        var ib = parameter as Infobase ?? SelectedInfobase;
        if (ib is null) return;

        // Пользователь попросил явно — снимаем оба вердикта о недоступности COM: кэш
        // реестра и сессионную защёлку агента. Причина сбоя могла быть разовой (антивирус,
        // нехватка памяти) или уже устранённой (платформу поставили после запуска),
        // а иначе до перезапуска приложения команда молча отвечала бы отказом.
        OneCComConnector.ResetComVerdicts();

        var baseName = ib.Name;
        _ = Task.Run(() =>
        {
            OneCConfigInfo? info = null;
            try
            {
                info = ConfigurationInfoService.ReadAndApply(ib, overwriteExisting: true);
            }
            catch { }

            Application.Current?.Dispatcher.Invoke(() =>
            {
                if (info is null)
                {
                    var comError = ConfigurationInfoService.LastComError;
                    var detail = string.IsNullOrWhiteSpace(comError)
                        ? LocalizationManager.T("Main.ConfigInfoCheckHint")
                        : string.Format(LocalizationManager.T("Main.ConfigInfoReason"), comError);
                    _logger.Warn($"Не удалось получить информацию о конфигурации базы «{baseName}». {detail}");
                    _dialogs.ShowWarning(
                        string.Format(LocalizationManager.T("Main.ErrConfigInfo"), baseName, detail),
                        LocalizationManager.T("Main.ConfigInfoTitle"));
                    return;
                }

                InfobasesView?.Refresh();
                Save();

                var name = info.Value.Name.Trim();
                var version = info.Value.Version.Trim();
                _logger.Info($"Обновлена информация о конфигурации базы «{baseName}»: {name} ({version})");

                var sb = new System.Text.StringBuilder();
                sb.AppendLine(string.Format(LocalizationManager.T("Main.ConfigInfoBase"), baseName));
                if (name.Length > 0) sb.AppendLine(string.Format(LocalizationManager.T("Main.ConfigInfoName"), name));
                if (version.Length > 0) sb.AppendLine(string.Format(LocalizationManager.T("Main.ConfigInfoVersion"), version));
                _dialogs.ShowInfo(sb.ToString().TrimEnd(), LocalizationManager.T("Main.ConfigInfoTitle"));
            });
        });
    }

    /// <summary>
    /// Проверяет доступность всех баз 1С и помечает недоступные красным крестиком
    /// в списке баз. Для файловых баз — наличие каталога/файла по пути; для
    /// клиент-серверных — реальная попытка подключения через COM-коннектор;
    /// для веб-баз — заполненность адреса. Проверки выполняются в фоне, чтобы
    /// не блокировать UI.
    /// </summary>
    private void CheckAvailability()
    {
        var targets = Infobases.ToList();
        if (targets.Count == 0)
        {
            _dialogs.ShowInfo(LocalizationManager.T("Main.ConfigListEmpty"),
                LocalizationManager.T("Main.AvailabilityTitle"));
            return;
        }

        _ = Task.Run(() =>
        {
            // Результаты собираем заранее, чтобы не трогать модель из фонового потока.
            var results = new List<(Infobase Base, bool Available)>(targets.Count);
            foreach (var ib in targets)
                results.Add((ib, IsBaseAvailable(ib)));

            Application.Current?.Dispatcher.Invoke(() =>
            {
                foreach (var (ib, available) in results)
                    ib.SetCheckedAvailability(available);
                InfobasesView?.Refresh();

                var total = results.Count;
                var unavailable = results.Count(r => !r.Available);
                SyncMessage = string.Format(
                    LocalizationManager.T("Main.AvailabilityStatus"), total, unavailable);
                ScheduleClearSyncMessage();
            });
        });
    }

    /// <summary>
    /// Доступность отдельной базы. Файловая — есть ли каталог/файл по пути;
    /// клиент-серверная — удалось ли подключиться; веб-база — заполнен ли адрес.
    /// </summary>
    private static bool IsBaseAvailable(Infobase ib)
    {
        try
        {
            switch (ib.Connection?.Type)
            {
                case ConnectionType.File:
                    return InfobaseMaintenanceService.FileBaseExists(ib);

                case ConnectionType.ClientServer:
                {
                    // Проверка доступности — через безопасный путь процесс-агента (ComReadHost).
                    // Прямой Connect у comcntr.dll под CoreCLR обрывает процесс нативным
                    // fast-fail (0xC0000409), поэтому метод помечен [Obsolete] и здесь не используется.
                    var connector = AppServices.GetRequiredService<IOneCComConnector>();
                    return connector.ReadConfigurationInfo(ib, timeoutMs: 8000) is not null;
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

    /// <summary>
    /// Регистрирует COM-коннектор 1С в системе (comcntr.dll / comcntr64.dll).
    /// Использует версию и разрядность выбранной базы, либо новейшую установленную платформу.
    /// Требует прав администратора (запрос UAC).
    /// </summary>
    private void RegisterComConnector(object? parameter)
    {
        var ib = parameter as Infobase ?? SelectedInfobase;
        var version = ib?.PlatformVersion ?? string.Empty;
        var architecture = ib is not null && (ib.Architecture == "64" || ib.Architecture == "x64") ? "64" : "32";

        var versionLabel = string.IsNullOrWhiteSpace(version)
            ? LocalizationManager.T("Main.ComRegLatestVersion")
            : version;

        if (!_dialogs.Confirm(
                string.Format(LocalizationManager.T("Main.ComRegConfirm"), versionLabel, architecture),
                LocalizationManager.T("Main.ComRegTitle")))
            return;

        var registrar = AppServices.GetRequiredService<IOneCComConnectorRegistrar>();

        _ = Task.Run(() =>
        {
            var result = registrar.Register(version, architecture);

            Application.Current?.Dispatcher.Invoke(() =>
            {
                if (result.BinDirectory is null)
                {
                    _dialogs.ShowError(
                        string.Format(LocalizationManager.T("Main.ComRegNotFound"),
                            !string.IsNullOrWhiteSpace(result.VerificationNote)
                                ? result.VerificationNote
                                : LocalizationManager.T("Main.ComRegInstallHint")),
                        LocalizationManager.T("Main.ComRegTitle"));
                    return;
                }

                var sb = new System.Text.StringBuilder();
                sb.AppendLine(string.Format(LocalizationManager.T("Main.ComRegPlatform"), result.PlatformVersion));
                sb.AppendLine(string.Format(LocalizationManager.T("Main.ComRegBinDir"), result.BinDirectory));
                sb.AppendLine();

                if (result.Items.Count == 0)
                {
                    sb.AppendLine(LocalizationManager.T("Main.ComRegNoDll"));
                }
                else
                {
                    foreach (var item in result.Items)
                    {
                        var fileName = Path.GetFileName(item.DllPath);
                        var suffix = item.Success
                            ? LocalizationManager.T("Main.ComRegRegistered")
                            : string.Format(LocalizationManager.T("Main.ComRegErrorSuffix"), item.Error);
                        sb.AppendLine($"{(item.Success ? "✓" : "✗")} {fileName}{suffix}");
                    }
                }

                sb.AppendLine();
                sb.AppendLine(result.ProgIdVisible
                    ? LocalizationManager.T("Main.ComRegProgIdOk")
                    : LocalizationManager.T("Main.ComRegProgIdFail"));
                if (!string.IsNullOrWhiteSpace(result.VerificationNote))
                    sb.AppendLine(result.VerificationNote);

                if (result.Success && result.ProgIdVisible)
                {
                    // После успешной регистрации сбрасываем оба вердикта о недоступности:
                    // кэш реестра и сессионную защёлку процесса-агента. Вердиктов два, и
                    // снимать надо оба, иначе чтение по-прежнему откажет по устаревшему кэшу.
                    OneCComConnector.ResetComVerdicts();
                    _logger.Info("COM-коннектор 1С успешно зарегистрирован.");
                    _dialogs.ShowInfo(sb.ToString().TrimEnd(),
                        LocalizationManager.T("Main.ComRegTitle"));
                }
                else
                {
                    _logger.Warn("Регистрация COM-коннектора 1С завершилась неудачно.");
                    _dialogs.ShowWarning(sb.ToString().TrimEnd(),
                        LocalizationManager.T("Main.ComRegTitle"));
                }
            });
        });
    }

    /// <summary>
    /// Пересчёт размеров файловых баз. Выполняется в фоне: для каталогов это рекурсивный обход
    /// всей папки базы (включая 1Cv8.1CD и логи), который на UI-потоке заметно задерживал показ
    /// окна при старте. Сами объекты баз обновляются уже после возврата на UI-поток (после await).
    /// </summary>
    /// <summary>
    /// Фоновая инициализация после показа окна: строит дерево групп, назначает слоты
    /// Alt+1…9, восстанавливает последнее выделение и пересчитывает размеры файловых баз.
    /// Выполняется с индикатором прогресса и с отдачей управления диспетчеру между этапами,
    /// чтобы окно отрисовалось как можно раньше и интерфейс не «завис» при большом числе баз.
    /// </summary>
    private async System.Threading.Tasks.Task CompleteStartupInitializationAsync()
    {
        if (_startupInitCompleted)
            return;
        _startupInitCompleted = true;
        try
        {
            // Даём диспетчеру отрисовать окно и индикатор загрузки.
            await System.Threading.Tasks.Task.Delay(30);

            // Назначаем слоты Alt+1…9 уже существующим избранным и проставляем номера в UI.
            LoadingMessage = LocalizationManager.T("Main.LoadingFavorites");
            SyncFavoriteHotkeys();
            await System.Threading.Tasks.Task.Delay(1);

            // Восстанавливаем ветку, содержащую последнюю выбранную строку.
            PrepareLastSelectionExpansion();
            await System.Threading.Tasks.Task.Delay(1);

            // Строим дерево групп — самая затратная операция при большом числе баз.
            LoadingMessage = LocalizationManager.T("Main.LoadingTree");
            RebuildGroupTree();
            await System.Threading.Tasks.Task.Delay(1);

            // Размеры файловых ИБ считаются в фоне с учётом кеша (не блокирует UI).
            RefreshFileMetadata();

            // Фоново читаем имя и версию конфигурации для баз, где они ещё не заполнены.
            RefreshConfigurationInfoAsync();
        }
        catch (Exception ex)
        {
            _logger.Error("Ошибка фоновой инициализации главного окна: " + ex.Message);
        }
        finally
        {
            StartupInitializationCompleted?.Invoke(this, EventArgs.Empty);
            IsLoading = false;
            LoadingMessage = string.Empty;
        }
    }

    /// <summary>
    /// Пересчитывает размеры файловых ИБ в фоне, используя кеш предыдущих вычислений:
    /// если время последней записи пути не изменилось — диск повторно не сканируется.
    /// </summary>
    private async void RefreshFileMetadata()
    {
        // Снимок файловых баз на момент вызова: коллекция может меняться, пока считаются размеры.
        var fileBases = Infobases.Where(ib => ib.Connection.Type == ConnectionType.File).ToList();
        if (fileBases.Count == 0)
            return;

        var sizes = await System.Threading.Tasks.Task.Run(() =>
        {
            var map = new Dictionary<Infobase, long?>();
            foreach (var ib in fileBases)
                map[ib] = CalculateFileBaseSizeCached(ib);
            return map;
        });

        var changed = false;
        foreach (var kv in sizes)
        {
            if (kv.Key.FileSizeBytes != kv.Value)
            {
                kv.Key.FileSizeBytes = kv.Value;
                changed = true;
            }
        }
        if (changed)
            SaveSettings();
        InfobasesView?.Refresh();
    }

    /// <summary>
    /// Возвращает размер файловой ИБ с учётом кеша: при совпадении времени последней записи
    /// пути с сохранённым размер берётся без сканирования диска, иначе выполняется расчёт
    /// и результат помещается в кеш.
    /// </summary>
    private long? CalculateFileBaseSizeCached(Infobase ib)
    {
        var path = ib.Connection.FilePath?.Trim() ?? "";
        if (string.IsNullOrEmpty(path))
            return null;
        var key = NormalizeCachePath(path);
        try
        {
            // Маркер актуальности: для каталога берём время записи самого файла базы
            // 1Cv8.1CD (оно меняется при изменении данных базы), иначе — файла/каталога.
            string marker;
            if (File.Exists(path))
                marker = path;
            else
            {
                var dbFile = System.IO.Path.Combine(path, "1Cv8.1CD");
                marker = File.Exists(dbFile) ? dbFile : path;
            }
            DateTime lastWrite = File.Exists(marker)
                ? File.GetLastWriteTimeUtc(marker)
                : Directory.GetLastWriteTimeUtc(path);
            if (_fileSizeCache.TryGetValue(key, out var cached) && cached.LastWriteUtc == lastWrite)
                return cached.SizeBytes;

            var size = InfobaseMaintenanceService.CalculateFileBaseSize(ib);
            if (size is not null)
            {
                _fileSizeCache[key] = new Models.FileSizeCacheEntry
                {
                    SizeBytes = size.Value,
                    LastWriteUtc = lastWrite
                };
            }
            return size;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Нормализует путь для ключа кеша размеров (убирает хвостовые разделители, верхний регистр).</summary>
    private static string NormalizeCachePath(string path) =>
        path.TrimEnd('\\', '/').ToUpperInvariant();

    /// <summary>Обработчик запуска пакетной операции DESIGNER (показывает индикатор выгрузки).</summary>
    private void OnDesignerBatchStarted(object? sender, OneCLauncher.DesignerBatchInfo e)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _logger.Info($"Пакетная операция запущена: {e.OperationLabel}, база «{e.InfobaseName}»");
            ExportIndicatorTooltip =
                string.Format(LocalizationManager.T("Main.ExportTooltipData"), e.OperationLabel, e.InfobaseName) +
                (string.IsNullOrWhiteSpace(e.OutputPath) ? "" : string.Format(LocalizationManager.T("Main.ExportTooltipFile"), e.OutputPath));
            IsExporting = true;
        });
    }

    /// <summary>Обработчик завершения пакетной операции DESIGNER (скрывает индикатор выгрузки).</summary>
    private void OnDesignerBatchCompleted(object? sender, OneCLauncher.DesignerBatchInfo e)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _logger.Info($"Пакетная операция завершена: {e.OperationLabel}, база «{e.InfobaseName}»" +
                         (e.Success ? "" : $" (код {e.ExitCode}, ошибка)"));
            IsExporting = false;
            ExportIndicatorTooltip = string.Empty;

            // При неуспехе показываем реальную причину (лог 1С из /Out и код возврата).
            if (!e.Success)
            {
                _logger.Error($"Ошибка пакетной операции: {e.ErrorMessage}");
                System.Windows.MessageBox.Show(
                    e.ErrorMessage ?? LocalizationManager.T("Main.OperationFailedDefault"),
                    LocalizationManager.T("Main.OperationErrorTitle"),
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
        });
    }

    private void DumpInfobaseDt(object? parameter)
    {
        var ib = parameter as Infobase ?? SelectedInfobase;
        if (ib is null) return;

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = LocalizationManager.T("Main.DumpDtDialogTitle"),
            Filter = LocalizationManager.T("Main.DtFileFilter"),
            FileName = BuildExportFileName(SanitizeFileName(ib.Name), ".dt")
        };
        if (dlg.ShowDialog() != true) return;

        if (OneCLauncher.RunDesignerBatch(ib, OneCLauncher.DesignerBatchOperation.DumpIB, dlg.FileName))
        {
            ib.AddLaunchHistory("DumpDT", dlg.FileName);
            Save();
            _dialogs.ShowInfo(
                LocalizationManager.T("Main.DumpDtStarted"),
                LocalizationManager.T("Main.DumpDtTitle"));
        }
    }

    private void DumpConfigurationCf(object? parameter)
    {
        var ib = parameter as Infobase ?? SelectedInfobase;
        if (ib is null) return;

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = LocalizationManager.T("Main.DumpCfDialogTitle"),
            Filter = LocalizationManager.T("Main.CfFileFilter"),
            FileName = BuildExportFileName(SanitizeFileName(ib.Name), ".cf")
        };
        if (dlg.ShowDialog() != true) return;

        if (OneCLauncher.RunDesignerBatch(ib, OneCLauncher.DesignerBatchOperation.DumpCfg, dlg.FileName))
        {
            ib.AddLaunchHistory("DumpCF", dlg.FileName);
            Save();
            _dialogs.ShowInfo(
                LocalizationManager.T("Main.DumpCfStarted"),
                LocalizationManager.T("Main.DumpCfTitle"));
        }
    }

    private void TestInfobase(object? parameter)
    {
        var ib = parameter as Infobase ?? SelectedInfobase;
        if (ib is null) return;

        if (!_dialogs.Confirm(
                string.Format(LocalizationManager.T("Main.TestInfobaseConfirm"), ib.Name),
                LocalizationManager.T("Main.TestInfobaseTitle")))
            return;

        if (OneCLauncher.RunDesignerBatch(ib, OneCLauncher.DesignerBatchOperation.TestAndRepair))
        {
            ib.AddLaunchHistory("Test", "");
            Save();
            _dialogs.ShowInfo(
                LocalizationManager.T("Main.TestInfobaseStarted"),
                LocalizationManager.T("Main.TestInfobaseTitle"));
        }
    }

    private void ShowLaunchHistory(object? parameter)
    {
        var ib = parameter as Infobase ?? SelectedInfobase;
        if (ib is null) return;

        if (ib.LaunchHistory == null || ib.LaunchHistory.Count == 0)
        {
            _dialogs.ShowInfo(
                string.Format(LocalizationManager.T("Main.LaunchHistoryEmpty"), ib.Name),
                LocalizationManager.T("Main.LaunchHistoryTitle"));
            return;
        }

        var text = string.Join("\n", ib.LaunchHistory.Select(h => h.Display));
        _dialogs.ShowInfo(
            string.Format(LocalizationManager.T("Main.LaunchHistoryFormat"), ib.Name, text),
            LocalizationManager.T("Main.LaunchHistoryTitle"));
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var s = new string((name ?? "base").Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(s) ? "base" : s;
    }

    /// <summary>
    /// Формирует имя файла выгрузки. Если включена настройка добавления даты-времени
    /// (<see cref="AddTimestampToExportFileName"/>), к базовому имени добавляется суффикс
    /// «_yyyyMMdd_HHmmss» (например «База_20260819_074312.dt»).
    /// </summary>
    private string BuildExportFileName(string baseName, string extension)
    {
        if (_addTimestampToExportFileName)
        {
            var format = string.IsNullOrWhiteSpace(_exportTimestampFormat) ? "yyyyMMdd_HHmmss" : _exportTimestampFormat;
            var ts = DateTime.Now.ToString(format);
            return $"{baseName}_{ts}{extension}";
        }
        return $"{baseName}{extension}";
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
            infobase.NotifyTagsChanged();
            ScheduleSave();
            PruneActiveTagFilters();
            RefreshTagFilterItems();
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
        infobase.NotifyTagsChanged();
        ScheduleSave();
        // Если удалённый тег был выбран в фильтре и его больше нет ни на одной базе,
        // убираем его из активных отборов, иначе отбор «зависает»: чипа в панели нет,
        // а фильтр продолжает применяться и скрывать базы.
        PruneActiveTagFilters();
        RefreshTagFilterItems();
    }

    /// <summary>
    /// Переключает тег в мультифильтре (можно выбрать несколько).
    /// </summary>
    private void SearchByTag(object? parameter)
    {
        if (parameter is not string tag || string.IsNullOrWhiteSpace(tag))
            return;

        var existing = _activeTagFilters.FirstOrDefault(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
            _activeTagFilters.Remove(existing);
        else
            _activeTagFilters.Add(tag);

        SyncActiveTagFilterSet();
        OnPropertyChanged(nameof(HasActiveTagFilter));
        // Обновляем подсветку чипов тегов — иначе визуально фильтр «остаётся».
        RefreshTagFilterItems();
        RebuildGroupTree();
    }

    /// <summary>
    /// Очищает поле поиска (теги не трогает).
    /// </summary>
    private void ClearSearch(object? parameter)
    {
        // Отменяем отложенную перестройку от набора текста, чтобы не «вернуть» старый фильтр.
        _searchDebounceCts?.Cancel();
        _searchDebounceCts?.Dispose();
        _searchDebounceCts = null;

        if (!string.IsNullOrEmpty(_searchText))
            _searchText = string.Empty;
        OnPropertyChanged(nameof(SearchText));
        RebuildGroupTree();
    }

    /// <summary>
    /// Сбрасывает выбранные теги фильтра.
    /// </summary>
    private void ClearTagFilters(object? parameter)
    {
        if (_activeTagFilters.Count == 0)
            return;
        _activeTagFilters.Clear();
        SyncActiveTagFilterSet();
        OnPropertyChanged(nameof(HasActiveTagFilter));
        // Важно: пересоздать TagFilterItems с IsSelected=false, иначе чипы остаются «включёнными».
        RefreshTagFilterItems();
        RebuildGroupTree();
    }

    /// <summary>
    /// Нормализует путь группы: единый разделитель « / », обрезка пробелов.
    /// </summary>
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

    /// <summary>
    /// Перемещает базу в указанную группу (полный путь).
    /// <paramref name="insertBefore"/> — база, перед которой вставить (null = в конец группы).
    /// </summary>
    public void MoveInfobaseToGroup(Infobase infobase, string groupFullPath, Infobase? insertBefore = null)
    {
        var targetPath = groupFullPath ?? string.Empty;
        var targetNorm = NormalizeGroupPath(targetPath);
        infobase.Group = string.IsNullOrEmpty(targetNorm) ? targetPath : targetNorm;

        // Соседи в целевой группе (кроме переносимой).
        var siblings = Infobases
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

        Save();
        RebuildGroupTree();
        OnPropertyChanged(nameof(AvailableTags));
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

        // Нельзя сделать родителем потомка этой группы (иначе цикл в иерархии).
        if (!string.IsNullOrEmpty(newParentId)
            && GroupHierarchyHelper.IsAncestorOrSelf(newParentId, group.Id, Groups))
            return;

        // Старые полные пути: сама группа + все потомки (до смены ParentId).
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
        var oldRootNorm = NormalizeGroupPath(oldRootPath);

        // Меняем родителя только у перемещаемой группы; вложенные группы
        // остаются её потомками через свои ParentId и переезжают вместе с ней.
        group.ParentId = newParentId;

        // Новый полный путь самой перемещаемой группы (после смены родителя).
        var newRootPath = GroupHierarchyHelper.GetFullPath(group, Groups);
        var newRootNorm = NormalizeGroupPath(newRootPath);

        // pathRemap: старый путь (и нормализованный) → новый канонический.
        var pathRemap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Гарантированно добавляем маппинг для самой перемещаемой группы, чтобы базы,
        // находящиеся непосредственно в ней, всегда получили новый путь.
        if (!string.IsNullOrEmpty(oldRootPath)
            && !string.IsNullOrEmpty(newRootPath))
        {
            pathRemap[oldRootPath] = newRootPath;
            pathRemap[oldRootNorm] = newRootPath;
        }

        foreach (var id in subtreeIds)
        {
            var g = Groups.FirstOrDefault(x =>
                string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
            if (g is null || !oldPathsById.TryGetValue(id, out var oldPath))
                continue;
            var newPath = GroupHierarchyHelper.GetFullPath(g, Groups);
            if (string.IsNullOrEmpty(oldPath) || string.IsNullOrEmpty(newPath))
                continue;
            pathRemap[oldPath] = newPath;
            pathRemap[NormalizeGroupPath(oldPath)] = newPath;
        }

        // Обновляем Infobase.Group у всех баз подветки.
        if (pathRemap.Count > 0)
        {
            // Длинные пути первыми — чтобы «A / B» не переписывался как префикс «A».
            var remapByLength = pathRemap
                .OrderByDescending(kv => kv.Key.Length)
                .ToList();

            foreach (var ib in Infobases)
            {
                var current = ib.Group?.Trim() ?? string.Empty;
                if (string.IsNullOrEmpty(current))
                    continue;

                var currentNorm = NormalizeGroupPath(current);
                string? mapped = null;

                if (pathRemap.TryGetValue(current, out mapped)
                    || pathRemap.TryGetValue(currentNorm, out mapped))
                {
                    ib.Group = mapped;
                    continue;
                }

                // Префикс: база во вложенном пути, которого не было в pathRemap.
                // Всегда работаем через нормализованный путь и нормализованный ключ, чтобы
                // суффикс и итоговый путь получались каноническими и совпадали с FullPath узла.
                // Иначе база не найдёт группу при перестройке дерева и «уедет» в «Без группы».
                foreach (var (oldKey, newKey) in remapByLength)
                {
                    var oldKeyNorm = NormalizeGroupPath(oldKey);
                    if (string.IsNullOrEmpty(oldKeyNorm))
                        continue;
                    var prefix = oldKeyNorm + GroupHierarchyHelper.PathSeparator;
                    if (!currentNorm.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var suffix = currentNorm.Substring(oldKeyNorm.Length);
                    ib.Group = newKey + suffix;
                    break;
                }

                // Фолбэк: если путь базы относится к подветке (сама группа или вложенная),
                // но почему-то не попал в pathRemap — пересчитываем его по старому корневому пути.
                // Защищает от потери группы (попадания базы в «Без группы») при любых расхождениях
                // в формате/нормализации пути.
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

            if (_collapsedGroups is { Count: > 0 })
            {
                var updated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var key in _collapsedGroups)
                {
                    if (pathRemap.TryGetValue(key, out var mapped)
                        || pathRemap.TryGetValue(NormalizeGroupPath(key), out mapped))
                        updated.Add(mapped);
                    else if (!string.IsNullOrEmpty(oldRootPath)
                             && (key.StartsWith(oldRootPath + GroupHierarchyHelper.PathSeparator,
                                     StringComparison.OrdinalIgnoreCase)
                                 || NormalizeGroupPath(key).StartsWith(oldRootNorm + GroupHierarchyHelper.PathSeparator,
                                     StringComparison.OrdinalIgnoreCase))
                             && pathRemap.TryGetValue(oldRootPath, out var newRoot))
                        updated.Add(newRoot + key.Substring(Math.Min(key.Length, oldRootPath.Length)));
                    else
                        updated.Add(key);
                }
                _collapsedGroups.Clear();
                foreach (var k in updated)
                    _collapsedGroups.Add(k);
            }
        }

        // Всегда сохраняем базы и группы, затем UI — как после перезапуска.
        Save();
        SaveGroups();
        RebuildGroupTree();
    }

    /// <summary>
    /// Применяет настройки приложения (экземпляры, панель тегов).
    /// </summary>
    public void ApplyAppBehaviorSettings(
        bool allowMultipleInstances,
        bool checkForUpdatesOnStartup,
        bool autoUpdateEnabled,
        bool showTagFilterPanel,
        bool closeToTray = false,
        bool showTrayIcon = true,
        string? hotkeyEnterprise = null,
        string? hotkeyConfigurator = null,
        string? hotkeyFavorite = null,
        string? hotkeyEdit = null,
        string? hotkeyDelete = null,
        string? hotkeyClearCache = null,
        string? hotkeyAdd = null,
        string? hotkeyPin = null,
        bool escapeToTray = true,
        string? hotkeyShowAll = null,
        string? hotkeyShowFavorites = null,
        string? hotkeyShowRecent = null,
        bool rememberWindowLayout = true,
        string afterLaunchAction = "None")
    {
        _allowMultipleInstances = allowMultipleInstances;
        _checkForUpdatesOnStartup = checkForUpdatesOnStartup;
        _autoUpdateEnabled = autoUpdateEnabled;
        _showTagFilterPanel = showTagFilterPanel;
        _closeToTray = closeToTray;
        _showTrayIcon = showTrayIcon;
        _escapeToTray = escapeToTray;
        _rememberWindowLayout = rememberWindowLayout;
        _afterLaunchAction = afterLaunchAction;
        if (hotkeyEnterprise != null) _hotkeyEnterprise = hotkeyEnterprise.Trim();
        if (hotkeyConfigurator != null) _hotkeyConfigurator = hotkeyConfigurator.Trim();
        if (hotkeyFavorite != null) _hotkeyFavorite = hotkeyFavorite.Trim();
        if (hotkeyEdit != null) _hotkeyEdit = hotkeyEdit.Trim();
        if (hotkeyDelete != null) _hotkeyDelete = hotkeyDelete.Trim();
        if (hotkeyClearCache != null) _hotkeyClearCache = hotkeyClearCache.Trim();
        if (hotkeyAdd != null) _hotkeyAdd = hotkeyAdd.Trim();
        if (hotkeyPin != null) _hotkeyPin = hotkeyPin.Trim();
        if (hotkeyShowAll != null) _hotkeyShowAll = hotkeyShowAll.Trim();
        if (hotkeyShowFavorites != null) _hotkeyShowFavorites = hotkeyShowFavorites.Trim();
        if (hotkeyShowRecent != null) _hotkeyShowRecent = hotkeyShowRecent.Trim();
        OnPropertyChanged(nameof(AllowMultipleInstances));
        OnPropertyChanged(nameof(CheckForUpdatesOnStartup));
        OnPropertyChanged(nameof(AutoUpdateEnabled));
        OnPropertyChanged(nameof(ShowTagFilterPanel));
        OnPropertyChanged(nameof(CloseToTray));
        OnPropertyChanged(nameof(ShowTrayIcon));
        OnPropertyChanged(nameof(EscapeToTray));
        OnPropertyChanged(nameof(AfterLaunchAction));
        OnPropertyChanged(nameof(HotkeyEnterprise));
        OnPropertyChanged(nameof(HotkeyConfigurator));
        OnPropertyChanged(nameof(HotkeyFavorite));
        OnPropertyChanged(nameof(HotkeyEdit));
        OnPropertyChanged(nameof(HotkeyDelete));
        OnPropertyChanged(nameof(HotkeyClearCache));
        OnPropertyChanged(nameof(HotkeyAdd));
        OnPropertyChanged(nameof(HotkeyPin));
        OnPropertyChanged(nameof(HotkeyShowAll));
        OnPropertyChanged(nameof(HotkeyShowFavorites));
        OnPropertyChanged(nameof(HotkeyShowRecent));
        OnPropertyChanged(nameof(RememberWindowLayout));
        SaveSettings();
    }

    /// <summary>
    /// Уведомляет UI об изменении списка доступных тегов.
    /// </summary>
    public void RefreshAvailableTags()
    {
        RefreshTagFilterItems();
    }

}
#endif
