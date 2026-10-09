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
        || _listViewMode == ListViewMode.Recent
        || _listViewMode == ListViewMode.Running;

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
    /// Состав списка вот-вот сменится: окну нужно запомнить позицию прокрутки
    /// (issue #252). Поднимается перед заменой коллекции <see cref="GroupNodes"/>,
    /// пока прежнее дерево ещё на месте и смещение читается корректно.
    /// </summary>
    public event Action? TreeRebuilding;

    /// <summary>
    /// Состав списка обновлён: окну нужно вернуть выделение строки и клавиатурный фокус.
    /// Поднимается после полной пересборки дерева, когда прежние контейнеры строк
    /// уничтожены заменой коллекции <see cref="GroupNodes"/>.
    /// </summary>
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

    /// <summary>
    /// Заменяет содержимое GroupNodes с минимумом лишних уведомлений UI.
    /// </summary>
    private void ReplaceGroupNodes(List<GroupNodeViewModel> next)
    {
        // Пока дерево ещё на месте, даём окну запомнить позицию прокрутки до пересборки.
        TreeRebuilding?.Invoke();

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
        if (!OneCLauncher.LaunchByLink(link))
        {
            _dialogs.ShowError(string.Format(LocalizationManager.T("Main.ErrOpenLink"), link));
        }
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
            var importResult = _ibasesSync.Import(filePath, Infobases, Groups, _deletedGroupPaths.Load());

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
            var result = _ibasesSync.Import(filePath, Infobases, Groups, _deletedGroupPaths.Load());

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
            dir = _dialogs.OpenFolderDialog(
                LocalizationManager.T("StartManager.ChooseFolder"), null);
            if (string.IsNullOrWhiteSpace(dir))
                return;
        }

        try
        {
            var candidateInfobases = Infobases.ToList();
            var candidateGroups = Groups.ToList();

            var result = StartManagerImporter.Import(dir, candidateInfobases, candidateGroups, ResolveIbasesFilePath());

            if (result.NoConfigFound)
            {
                _dialogs.ShowInfo(
                    string.Format(LocalizationManager.T("StartManager.NoConfig"), dir),
                    LocalizationManager.T("StartManager.Title"));
                return;
            }

            if (result.NoIbasesFound)
            {
                _dialogs.ShowInfo(
                    LocalizationManager.T("StartManager.NoIbases"),
                    LocalizationManager.T("StartManager.Title"));
                return;
            }

            if (result.Added == 0 && result.Updated == 0)
            {
                _dialogs.ShowInfo(
                    LocalizationManager.T("StartManager.NothingImported"),
                    LocalizationManager.T("StartManager.Title"));
                return;
            }

            Infobases.Clear();
            foreach (var ib in candidateInfobases)
                Infobases.Add(ib);
            SyncFavoriteHotkeys();
            Groups.Clear();
            foreach (var g in candidateGroups)
                Groups.Add(g);

            Save();
            SaveGroups();
            RebuildGroupTree();
            InfobasesView.Refresh();

            // Путь платформы 1С из settings.cnf добавляем в дополнительные пути поиска.
            var platformAdded = false;
            if (result.PlatformSearchPaths.Count > 0)
            {
                var paths = new List<string>(AdditionalPlatformSearchPaths);
                foreach (var p in result.PlatformSearchPaths)
                {
                    if (!paths.Contains(p, StringComparer.OrdinalIgnoreCase))
                    {
                        paths.Add(p);
                        platformAdded = true;
                    }
                }
                if (platformAdded)
                {
                    SetAdditionalPlatformSearchPaths(paths);
                    ApplyDefaultArchitecture(DefaultArchitecture);
                }
            }

            _logger.Info($"Импорт из StartManager: {dir}, добавлено {result.Added}, " +
                         $"обновлено {result.Updated}, пропущено {result.Skipped}");

            var message = string.Format(
                LocalizationManager.T("StartManager.Done"),
                result.Added, result.Updated, result.GroupsCreated, result.Skipped);
            if (result.Skipped > 0)
                message += "\n\n" + LocalizationManager.T("StartManager.SkippedHint");
            if (platformAdded)
                message += "\n" + LocalizationManager.T("StartManager.PlatformPathAdded");
            _dialogs.ShowInfo(message, LocalizationManager.T("StartManager.Title"));
        }
        catch (Exception ex)
        {
            _logger.Error("Ошибка импорта из StartManager", ex);
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
                PropertyNameCaseInsensitive = true,
                // Кириллицу и прочие не-ASCII символы пишем читаемыми UTF-8,
                // а не \uXXXX-последовательностями (issue #170).
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
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
    /// Экспортирует ВСЕ базы, которые пользователь видит сейчас (с учётом фильтра
    /// приватных баз, режима списка, поиска и тегов — тот же набор, что в дереве),
    /// в CSV-файл, открываемый в Excel (0.3.9.91).
    /// </summary>
    private void ExportBasesCsv(object? parameter)
    {
        // Тот же набор, что в RebuildGroupTree: приватные базы заблокированного
        // профиля скрыты, режимы «Избранное»/«Недавние», поиск и теги учитываются.
        var visible = EnumerateFilteredInfobases().ToList();
        if (visible.Count == 0)
        {
            _dialogs.ShowInfo(LocalizationManager.T("Main.ExportEmpty"),
                LocalizationManager.T("ExportCsv.Title"));
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = LocalizationManager.T("ExportCsv.Title"),
            Filter = LocalizationManager.T("ExportCsv.FileFilter"),
            DefaultExt = ".csv",
            FileName = $"Bases_{DateTime.Now:yyyy-MM-dd}.csv",
            AddExtension = true
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            CsvExporter.WriteFile(dialog.FileName, BuildCsvRows(visible));

            _dialogs.ShowInfo(
                string.Format(LocalizationManager.T("ExportCsv.Success"), visible.Count, dialog.FileName),
                LocalizationManager.T("ExportCsv.Title"));
            _logger.Info($"Список баз ({visible.Count}) выгружен в CSV: {dialog.FileName}");
        }
        catch (Exception ex)
        {
            _logger.Error("Ошибка экспорта списка баз в CSV", ex);
            _dialogs.ShowError(
                string.Format(LocalizationManager.T("ExportCsv.Error"), ex.Message),
                LocalizationManager.T("Main.ExportErrorTitle"));
        }
    }

    /// <summary>
    /// Экспортирует видимые базы (тот же набор, что в CSV-экспорте: приватные базы
    /// заблокированного профиля скрыты, режимы «Избранное»/«Недавние», поиск и теги
    /// учтены) в самодостаточный HTML-отчёт (0.3.9.131): шапка с датой/временем и
    /// профилем, сводка по последним известным данным, таблицы по группам с подсветкой
    /// проблемных баз по критериям Центра обслуживания. Стили встроены — файл открывается
    /// в любом браузере и пригоден для печати/рассылки.
    /// </summary>
    private void ExportBasesHtml(object? parameter)
    {
        var visible = EnumerateFilteredInfobases().ToList();
        if (visible.Count == 0)
        {
            _dialogs.ShowInfo(LocalizationManager.T("Main.ExportEmpty"),
                LocalizationManager.T("ExportHtml.Title"));
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = LocalizationManager.T("ExportHtml.Title"),
            Filter = LocalizationManager.T("ExportHtml.FileFilter"),
            DefaultExt = ".html",
            FileName = $"Bases_{DateTime.Now:yyyy-MM-dd}.html",
            AddExtension = true
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            HtmlReportExporter.WriteFile(dialog.FileName, BuildHtmlReportData(visible));

            _dialogs.ShowInfo(
                string.Format(LocalizationManager.T("ExportHtml.Success"), visible.Count, dialog.FileName),
                LocalizationManager.T("ExportHtml.Title"));
            _logger.Info($"HTML-отчёт по базам ({visible.Count}) сформирован: {dialog.FileName}");
        }
        catch (Exception ex)
        {
            _logger.Error("Ошибка формирования HTML-отчёта по базам", ex);
            _dialogs.ShowError(
                string.Format(LocalizationManager.T("ExportHtml.Error"), ex.Message),
                LocalizationManager.T("Main.ExportErrorTitle"));
        }
    }

    /// <summary>
    /// Экспортирует ВСЁ состояние списка баз в JSON (0.3.9.122): базы со всеми полями
    /// (строка подключения, имя, группа, теги, закладка 1–9, закрепление, приватность,
    /// внешняя обработка, скрипты pre/post запуска, раздельные учётные данные,
    /// параметры запуска, порядок сортировки) и иерархию групп целиком. В отличие
    /// от CSV-экспорта переносит и избранное, и закрепление. Приватные базы
    /// включаются только при разблокированном профиле (иначе скрыты, как в CSV).
    /// </summary>
    private void ExportBasesJson(object? parameter)
    {
        // Приватные базы заблокированного профиля в выгрузку не попадают (как в CSV).
        var exportable = Infobases.Where(IsVisibleForPrivateFilter).ToList();
        if (exportable.Count == 0)
        {
            _dialogs.ShowInfo(LocalizationManager.T("Main.ExportEmpty"),
                LocalizationManager.T("ExportJson.Title"));
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = LocalizationManager.T("ExportJson.Title"),
            Filter = LocalizationManager.T("Main.JsonFileFilter"),
            DefaultExt = ".json",
            FileName = BuildExportFileName("infobases_full_export", ".json"),
            AddExtension = true
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var snapshot = InfobaseJsonTransfer.BuildSnapshot(exportable, Groups);
            var json = InfobaseJsonTransfer.Serialize(snapshot);
            File.WriteAllText(dialog.FileName, json);

            _dialogs.ShowInfo(
                string.Format(LocalizationManager.T("ExportJson.Success"),
                    snapshot.Infobases.Count, snapshot.Groups.Count, dialog.FileName),
                LocalizationManager.T("ExportJson.Title"));
            _logger.Info($"Список баз ({snapshot.Infobases.Count}) и групп ({snapshot.Groups.Count}) " +
                         $"выгружен в JSON: {dialog.FileName}");
        }
        catch (Exception ex)
        {
            _logger.Error("Ошибка экспорта списка баз в JSON", ex);
            _dialogs.ShowError(
                string.Format(LocalizationManager.T("ExportJson.Error"), ex.Message),
                LocalizationManager.T("Main.ExportErrorTitle"));
        }
    }

    /// <summary>
    /// Импорт списка баз из JSON с режимом «добавить» (0.3.9.122): дубликаты по строке
    /// подключения пропускаются, новые базы/группы/теги добавляются. Перед импортом
    /// показывается сводка (сколько баз/групп/тегов будет добавлено, сколько пропущено
    /// дубликатов) с подтверждением.
    /// </summary>
    private void ImportBasesJson(object? parameter)
    {
        var dialog = new OpenFileDialog
        {
            Title = LocalizationManager.T("ImportJson.Title"),
            Filter = LocalizationManager.T("Main.JsonFileFilter"),
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog() != true)
            return;

        InfobaseListSnapshot? snapshot;
        try
        {
            var json = File.ReadAllText(dialog.FileName);
            snapshot = InfobaseJsonTransfer.Deserialize(json);
        }
        catch (Exception ex)
        {
            _logger.Error("Ошибка чтения файла импорта списка баз (JSON)", ex);
            _dialogs.ShowError(
                string.Format(LocalizationManager.T("ImportJson.Error"), ex.Message),
                LocalizationManager.T("Main.ImportErrorTitle"));
            return;
        }

        if (snapshot is null || snapshot.Infobases.Count == 0)
        {
            _dialogs.ShowWarning(LocalizationManager.T("ImportJson.NoBases"),
                LocalizationManager.T("ImportJson.Title"));
            return;
        }

        // Сводка перед импортом: сколько добавится, сколько пропустится.
        var plan = InfobaseJsonTransfer.PlanImport(
            Infobases,
            Groups,
            Infobases.SelectMany(i => i.Tags),
            snapshot);

        if (plan.BasesToAdd.Count == 0 && plan.GroupsToAdd.Count == 0)
        {
            _dialogs.ShowInfo(LocalizationManager.T("ImportJson.NothingNew"),
                LocalizationManager.T("ImportJson.Title"));
            return;
        }

        if (!_dialogs.Confirm(
                string.Format(LocalizationManager.T("ImportJson.Confirm"),
                    plan.BasesToAdd.Count, plan.GroupsToAdd.Count, plan.TagsToAdd.Count,
                    plan.DuplicatesSkipped),
                LocalizationManager.T("ImportJson.Title")))
            return;

        foreach (var infobase in plan.BasesToAdd)
            Infobases.Add(infobase);
        SyncFavoriteHotkeys();

        // Группы из файла, которых ещё нет (по имени), добавляются вместе с иерархией
        // ParentId; конфликтующие Id переименовываются без потери связей.
        var mergedGroups = InfobaseJsonTransfer.MergeNewGroups(Groups, plan.GroupsToAdd);
        foreach (var group in mergedGroups)
            Groups.Add(group);

        SelectedInfobase = null;
        InfobasesView.Refresh();
        Save();
        SaveGroups();
        RebuildGroupTree();
        RefreshTagFilterItems();

        _dialogs.ShowInfo(
            string.Format(LocalizationManager.T("ImportJson.Done"),
                plan.BasesToAdd.Count, plan.GroupsToAdd.Count, plan.TagsToAdd.Count,
                plan.DuplicatesSkipped),
            LocalizationManager.T("ImportJson.Title"));
        _logger.Info($"Импорт списка баз из JSON: добавлено {plan.BasesToAdd.Count} баз, " +
                     $"{plan.GroupsToAdd.Count} групп, {plan.TagsToAdd.Count} тегов, " +
                     $"пропущено дубликатов {plan.DuplicatesSkipped}");
    }

    /// <summary>Строки CSV-документа: локализованный заголовок + по строке на видимую базу.</summary>
    private static List<string[]> BuildCsvRows(IEnumerable<Infobase> bases)
    {
        var rows = new List<string[]>
        {
            new[]
            {
                LocalizationManager.T("ExportCsv.ColName"),
                LocalizationManager.T("ExportCsv.ColGroup"),
                LocalizationManager.T("ExportCsv.ColType"),
                LocalizationManager.T("ExportCsv.ColConnection"),
                LocalizationManager.T("ExportCsv.ColTags"),
                LocalizationManager.T("ExportCsv.ColFavorite"),
                LocalizationManager.T("ExportCsv.ColPinned"),
                LocalizationManager.T("ExportCsv.ColModified"),
                LocalizationManager.T("ExportCsv.ColSize")
            }
        };

        foreach (var ib in bases)
        {
            rows.Add(new[]
            {
                ib.Name,
                ib.Group ?? string.Empty,
                ib.ConnectionTypeDisplay,
                ib.ConnectionStringDisplay,
                string.Join(", ", ib.Tags),
                ib.FavoriteHotkeyNumber >= 1 && ib.FavoriteHotkeyNumber <= 9
                    ? ib.FavoriteHotkeyNumber.ToString()
                    : string.Empty,
                ib.IsPinned ? LocalizationManager.T("ExportCsv.Yes") : LocalizationManager.T("ExportCsv.No"),
                FormatCsvModified(ib),
                FormatCsvSize(ib)
            });
        }
        return rows;
    }

    /// <summary>Дата изменений файла ИБ в формате ГГГГ-ММ-ДД ЧЧ:ММ (локальное время) или пусто.</summary>
    private static string FormatCsvModified(Infobase ib) =>
        ib.FileLastWriteTimeUtc.HasValue
            ? ib.FileLastWriteTimeUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : string.Empty;

    /// <summary>Размер ИБ в удобочитаемом виде, если известен; иначе пусто.</summary>
    private static string FormatCsvSize(Infobase ib)
    {
        if (ib.ManualSizeBytes.HasValue)
            return Infobase.FormatSize(ib.ManualSizeBytes.Value);
        return ib.FileSizeBytes.HasValue ? Infobase.FormatSize(ib.FileSizeBytes.Value) : string.Empty;
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

    // ======================= Выборочный экспорт/импорт баз =======================

    private ICommand? _exportSelectedInfobasesCommand;

    /// <summary>Команда «Экспорт выбранных баз…»: окно-чеклист + сохранение в JSON.</summary>
    public ICommand ExportSelectedInfobasesCommand =>
        _exportSelectedInfobasesCommand ??= new RelayCommand(_ => ExecuteExportSelectedInfobases(),
            _ => Infobases.Count > 0);

    private void ExecuteExportSelectedInfobases()
    {
        if (Infobases.Count == 0)
        {
            _dialogs.ShowInfo(LocalizationManager.T("Main.ExportEmpty"),
                LocalizationManager.T("Main.ExportBasesTitle"));
            return;
        }

        var items = Infobases
            .Select(b => new BaseSelectionItem(b.Name, b.GroupDisplay) { Tag = b })
            .ToList();
        var selector = new BaseSelectionWindow(
            LocalizationManager.T("ExportSelect.Title"),
            LocalizationManager.T("ExportSelect.ExportHint"),
            items)
        { Owner = System.Windows.Application.Current.MainWindow };
        if (selector.ShowDialog() != true)
            return;

        var selected = selector.GetSelected().Select(i => (Infobase)i.Tag!).ToList();
        if (selected.Count == 0)
            return;

        var dialog = new SaveFileDialog
        {
            Title = LocalizationManager.T("Main.ExportBasesDialogTitle"),
            Filter = LocalizationManager.T("Main.JsonFileFilter"),
            DefaultExt = ".json",
            FileName = BuildExportFileName("infobases_selected", ".json"),
            AddExtension = true
        };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var exportData = new InfobaseExportData
            {
                Version = 1,
                Infobases = selected,
                Groups = Groups.ToList()
            };

            var json = JsonSerializer.Serialize(exportData, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNameCaseInsensitive = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
            File.WriteAllText(dialog.FileName, json);

            _dialogs.ShowInfo(
                string.Format(LocalizationManager.T("ExportSelect.ExportDone"),
                    selected.Count, dialog.FileName),
                LocalizationManager.T("Main.ExportBasesTitle"));
        }
        catch (Exception ex)
        {
            _dialogs.ShowError(
                string.Format(LocalizationManager.T("Main.ErrExportFailed"), ex.Message),
                LocalizationManager.T("Main.ExportErrorTitle"));
        }
    }

    private ICommand? _importMergeInfobasesCommand;

    /// <summary>Команда «Импорт баз (добавлением)…»: выбор файла, чеклист, слияние по Id.</summary>
    public ICommand ImportMergeInfobasesCommand =>
        _importMergeInfobasesCommand ??= new RelayCommand(_ => ExecuteImportMergeInfobases());

    private void ExecuteImportMergeInfobases()
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

        List<Infobase> loaded;
        List<Group> loadedGroups;
        try
        {
            var json = File.ReadAllText(dialog.FileName);
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
                // Несовместимый формат — пробуем старый список баз ниже.
            }

            if (exportData != null && exportData.Infobases.Count > 0)
            {
                loaded = exportData.Infobases;
                loadedGroups = exportData.Groups;
            }
            else
            {
                loaded = JsonSerializer.Deserialize<List<Infobase>>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                }) ?? new List<Infobase>();
                loadedGroups = new List<Group>();
            }
        }
        catch (Exception ex)
        {
            _dialogs.ShowError(
                string.Format(LocalizationManager.T("Main.ErrLoadFailed"), ex.Message),
                LocalizationManager.T("Main.LoadErrorTitle"));
            return;
        }

        if (loaded.Count == 0)
        {
            _dialogs.ShowWarning(LocalizationManager.T("Main.ImportNoBases"),
                LocalizationManager.T("Main.LoadBasesTitle"));
            return;
        }

        // Уже существующие (по Id/подключению) помечаются снятыми флажками —
        // их импорт пропустит дубликат, но пользователь видит, что в файле.
        var existingKeys = new HashSet<string>(
            Infobases.Select(InfobaseExportMerge.DuplicateKey), StringComparer.OrdinalIgnoreCase);
        var items = loaded
            .Select(b =>
            {
                var duplicate = existingKeys.Contains(InfobaseExportMerge.DuplicateKey(b));
                var subtitle = duplicate
                    ? LocalizationManager.T("ExportSelect.AlreadyExists")
                    : b.GroupDisplay;
                return new BaseSelectionItem(b.Name, subtitle, !duplicate) { Tag = b };
            })
            .ToList();

        var selector = new BaseSelectionWindow(
            LocalizationManager.T("ExportSelect.ImportTitle"),
            LocalizationManager.T("ExportSelect.ImportHint"),
            items)
        { Owner = System.Windows.Application.Current.MainWindow };
        if (selector.ShowDialog() != true)
            return;

        var chosen = selector.GetSelected().Select(i => (Infobase)i.Tag!).ToList();
        if (chosen.Count == 0)
            return;

        var merge = InfobaseExportMerge.SelectNew(Infobases, chosen);

        foreach (var added in merge.Added)
            Infobases.Add(added);

        // Группы из файла, которых ещё нет (по имени), добавляются целиком —
        // вместе с их иерархией ParentId, как в файле экспорта.
        var existingGroupNames = new HashSet<string>(
            Groups.Select(g => (g.Name ?? "").Trim()), StringComparer.OrdinalIgnoreCase);
        foreach (var group in loadedGroups)
        {
            if (!string.IsNullOrWhiteSpace(group.Name) &&
                existingGroupNames.Add(group.Name.Trim()))
            {
                Groups.Add(group);
            }
        }

        InfobasesView.Refresh();
        Save();
        SaveGroups();
        RebuildGroupTree();

        _dialogs.ShowInfo(
            string.Format(LocalizationManager.T("ExportSelect.ImportDone"),
                merge.Added.Count, chosen.Count - merge.Added.Count),
            LocalizationManager.T("Main.LoadBasesTitle"));
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

        // Временная индикация процесса (issue #244): надпись «(обновление информации)»
        // в колонке «Конфигурация», если она видима; иначе в «№ релиза»; иначе в «Название».
        var indicatorColumn = _showConfigurationColumn ? "Configuration"
            : _showConfigurationVersionColumn ? "ConfigurationVersion"
            : "Name";
        ib.SetConfigInfoIndicator(true, indicatorColumn);

        var baseName = ib.Name;
        _ = Task.Run(() =>
        {
            OneCConfigInfo? info = null;
            try
            {
                // Режим чтения сведений — «Конфигуратор» (issue #236): учётные данные берутся
                // из ConfiguratorAuth при её наличии, иначе из авторизации базы.
                info = ConfigurationInfoService.ReadAndApply(ib, overwriteExisting: true,
                    mode: OneCLaunchMode.Configurator);
            }
            catch { }

            Application.Current?.Dispatcher.Invoke(() =>
            {
                // Надпись очищается независимо от результата (успех или ошибка).
                ib.SetConfigInfoIndicator(false, indicatorColumn);

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
    /// Проверяет доступность всех баз 1С и помечает недоступные красной иконкой
    /// типа базы в списке (оригинальная иконка подключения в красном цвете,
    /// issue #289). Для файловых баз — наличие каталога/файла по пути; для
    /// клиент-серверных — реальная попытка подключения через COM-коннектор;
    /// для веб-баз — заполненность адреса.
    /// <para>
    /// Сразу после запуска все базы помечаются как «проверяется» (серый значок
    /// ожидания). Результат каждой базы публикуется в UI по мере готовности, чтобы
    /// пользователь видел ход проверки, а не только финальный итог. Проверки идут
    /// в фоне с ограниченным параллелизмом, чтобы не блокировать интерфейс.
    /// </para>
    /// </summary>
    private void CheckAvailability()
    {
        if (_availabilityCheckRunning)
            return;

        var targets = Infobases.ToList();
        if (targets.Count == 0)
        {
            _dialogs.ShowInfo(LocalizationManager.T("Main.ConfigListEmpty"),
                LocalizationManager.T("Main.AvailabilityTitle"));
            return;
        }

        _availabilityCheckRunning = true;
        foreach (var ib in targets)
            ib.SetChecking(true);
        InfobasesView?.Refresh();
        SyncMessage = string.Format(
            LocalizationManager.T("Main.AvailabilityProgress"), 0, targets.Count);

        _ = RunAvailabilityCheckAsync(targets);
    }

    /// <summary>Ограничение параллельных проверок доступности баз (избегаем лавины COM-запросов).</summary>
    private const int AvailabilityParallelism = 4;

    private bool _availabilityCheckRunning;

    private async Task RunAvailabilityCheckAsync(List<Infobase> targets)
    {
        try
        {
            var results = new List<(Infobase Base, bool Available)>(targets.Count);
            var processed = 0;

            await Task.Run(() => Parallel.ForEach(targets,
                new ParallelOptions { MaxDegreeOfParallelism = AvailabilityParallelism },
                ib =>
                {
                    var available = IsBaseAvailable(ib);
                    var done = Interlocked.Increment(ref processed);
                    lock (results)
                        results.Add((ib, available));

                    // Каждая завершённая база сразу публикуется в UI-потоке:
                    // иконка меняется с серой на фактический статус, в строке
                    // состояния обновляется прогресс «i из N».
                    Application.Current?.Dispatcher.Invoke(() =>
                    {
                        ib.SetCheckedAvailability(available);
                        InfobasesView?.Refresh();
                        SyncMessage = string.Format(
                            LocalizationManager.T("Main.AvailabilityProgress"), done, targets.Count);
                    });
                }));

            Application.Current?.Dispatcher.Invoke(() =>
            {
                InfobasesView?.Refresh();
                var total = results.Count;
                var unavailable = results.Count(r => !r.Available);
                SyncMessage = string.Format(
                    LocalizationManager.T("Main.AvailabilityStatus"), total, total - unavailable, unavailable);
                ScheduleClearSyncMessage();
            });
        }
        finally
        {
            _availabilityCheckRunning = false;
        }
    }

    /// <summary>
    /// Доступность отдельной базы. Файловая — есть ли каталог/файл по пути;
    /// клиент-серверная — удалось ли подключиться; веб-база — заполнен ли адрес.
    /// Для клиент-серверных баз таймаут подключения берётся из настройки
    /// «Таймаут определения свойств конфигурации» (ComDetectTimeoutMs, issue #289).
    /// </summary>
    private bool IsBaseAvailable(Infobase ib)
    {
        try
        {
            switch (ib.Connection?.Type)
            {
                case ConnectionType.File:
                    return InfobaseMaintenanceService.FileBaseExists(ib);

                case ConnectionType.ClientServer:
                {
                    // Быстрая TCP-проверка порта кластера (функция 12, флаг
                    // AvailabilityTcpPrecheckEnabled, по умолчанию выключен): закрытый
                    // порт/таймаут — база точно недоступна, COM не вызываем (ускорение
                    // массовой проверки недоступных серверов); открытый порт или любая
                    // неопределённость — проверку продолжает COM (ложных результатов нет).
                    if (NetworkAvailabilityPrecheck.IsUnreachableFast(
                            AvailabilityTcpPrecheckEnabled,
                            ib.Connection.Server,
                            ib.Connection.Port > 0 ? ib.Connection.Port : OneCPorts.Cluster,
                            NetworkDiagnosticsService.TcpPortCheckAsync))
                        return false;

                    // Проверка доступности — через безопасный путь процесс-агента (ComReadHost).
                    // Прямой Connect у comcntr.dll под CoreCLR обрывает процесс нативным
                    // fast-fail (0xC0000409), поэтому метод помечен [Obsolete] и здесь не используется.
                    // Таймаут берётся из настройки ComDetectTimeoutMs (по умолчанию 30000 мс),
                    // минимум 1000 мс — по аналогии с ConfigurationInfoService.ResolveTimeoutMs
                    // (issue #174/#289).
                    var connector = AppServices.GetRequiredService<IOneCComConnector>();
                    return connector.ReadConfigurationInfo(ib,
                        timeoutMs: Math.Max(1000, ComDetectTimeoutMs)) is not null;
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

            // Фоновое дочитывание свойств конфигурации при старте/импорте намеренно НЕ
            // запускается (issue #174): на недоступном сервере оно занимало ~8 с на базу,
            // «глушило» защёлку COM и было лишним при импорте. Только явная команда
            // «Обновить информацию» (RefreshConfigurationInfo) читает свойства.
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

        var results = await System.Threading.Tasks.Task.Run(() =>
        {
            var map = new Dictionary<Infobase, (long?, DateTime)>();
            foreach (var ib in fileBases)
                map[ib] = CalculateFileBaseMetadataCached(ib);
            return map;
        });

        var changed = false;
        foreach (var kv in results)
        {
            if (kv.Key.FileSizeBytes != kv.Value.Item1)
            {
                kv.Key.FileSizeBytes = kv.Value.Item1;
                changed = true;
            }
            // Дата изменений файла ИБ для колонки «Дата изменений» (Этап 13).
            kv.Key.FileLastWriteTimeUtc = kv.Value.Item2 == default ? (DateTime?)null : kv.Value.Item2;
        }
        if (changed)
            SaveSettings();
        InfobasesView?.Refresh();
    }

    /// <summary>
    /// Возвращает размер и дату последнего изменения файловой ИБ с учётом кеша: при совпадении
    /// времени последней записи пути с сохранённым размер берётся без сканирования диска,
    /// иначе выполняется расчёт и результат помещается в кеш. Дата изменений возвращается
    /// для колонки «Дата изменений» (Этап 13) и вычисляется из того же маркера актуальности.
    /// </summary>
    private (long?, DateTime) CalculateFileBaseMetadataCached(Infobase ib)
    {
        var path = ib.Connection.FilePath?.Trim() ?? "";
        if (string.IsNullOrEmpty(path))
            return (null, default);
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
                return (cached.SizeBytes, lastWrite);

            var size = InfobaseMaintenanceService.CalculateFileBaseSize(ib);
            if (size is not null)
            {
                _fileSizeCache[key] = new Models.FileSizeCacheEntry
                {
                    SizeBytes = size.Value,
                    LastWriteUtc = lastWrite
                };
            }
            return (size, lastWrite);
        }
        catch
        {
            return (null, default);
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
                _dialogs.ShowError(
                    e.ErrorMessage ?? LocalizationManager.T("Main.OperationFailedDefault"),
                    LocalizationManager.T("Main.OperationErrorTitle"));
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
        else
        {
            _dialogs.ShowError(
                LocalizationManager.T("Main.OperationFailedDefault"),
                LocalizationManager.T("Main.OperationErrorTitle"));
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
        else
        {
            _dialogs.ShowError(
                LocalizationManager.T("Main.OperationFailedDefault"),
                LocalizationManager.T("Main.OperationErrorTitle"));
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
        else
        {
            _dialogs.ShowError(
                LocalizationManager.T("Main.OperationFailedDefault"),
                LocalizationManager.T("Main.OperationErrorTitle"));
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
            var g = Groups.FirstOrDefault(x =>
                string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
            if (g is null)
                continue;
            var newPath = GroupHierarchyHelper.GetFullPath(g, Groups);
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

        foreach (var ib in Infobases)
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

                ib.Group = newKey + currentNorm.Substring(oldKeyNorm.Length);
                break;
            }

            // Фолбэк: если путь базы относится к подветке (сама группа или вложенная),
            // но почему-то не попал в pathRemap — пересчитываем его по старому корневому пути.
            // Защищает от потери группы (попадания базы в «Без группы») при любых расхождениях
            // в формате/нормализации пути.
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

        if (_collapsedGroups is { Count: > 0 })
        {
            var updated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in _collapsedGroups)
            {
                if (pathRemap.TryGetValue(key, out var mapped)
                    || pathRemap.TryGetValue(NormalizeGroupPath(key), out mapped))
                    updated.Add(mapped);
                else if (!string.IsNullOrEmpty(oldRootNorm)
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
        bool showSystemNotifications = true,
        bool catchUpMissedTasks = true,
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
        string afterLaunchAction = "None",
        string? hotkeyClearSearch = null,
        string? hotkeyClearTags = null,
        string? hotkeyRightPanelDetails = null,
        string? hotkeyFindInList = null,
        string? hotkeySwitchUser = null,
        string? hotkeySessionLock = null,
        string? hotkeyLockApp = null,
        string? hotkeyCheckIntegrity = null,
        string? hotkeyServerConsole = null,
        string? hotkeyZoomIn = null,
        string? hotkeyZoomOut = null,
        string? hotkeyZoomReset = null,
        string? hotkeyCommandPalette = null,
        // Меню закладок (issue #356): настраиваемая горячая клавиша, по умолчанию Ctrl+B.
        string? hotkeyBookmarksMenu = null,
        // Отбор «Только запущенные» (issue #339): настраиваемая горячая клавиша.
        string? hotkeyShowRunning = null)
    {
        _allowMultipleInstances = allowMultipleInstances;
        _checkForUpdatesOnStartup = checkForUpdatesOnStartup;
        _autoUpdateEnabled = autoUpdateEnabled;
        _showTagFilterPanel = showTagFilterPanel;
        _closeToTray = closeToTray;
        _showTrayIcon = showTrayIcon;
        _showSystemNotifications = showSystemNotifications;
        _catchUpMissedTasks = catchUpMissedTasks;
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
        if (hotkeyShowRunning != null) _hotkeyShowRunning = hotkeyShowRunning.Trim();
        if (hotkeyClearSearch != null) _hotkeyClearSearch = hotkeyClearSearch.Trim();
        if (hotkeyClearTags != null) _hotkeyClearTags = hotkeyClearTags.Trim();
        if (hotkeyRightPanelDetails != null) _hotkeyRightPanelDetails = hotkeyRightPanelDetails.Trim();
        if (hotkeyFindInList != null) _hotkeyFindInList = hotkeyFindInList.Trim();
        if (hotkeySwitchUser != null) _hotkeySwitchUser = hotkeySwitchUser.Trim();
        if (hotkeySessionLock != null) _hotkeySessionLock = hotkeySessionLock.Trim();
        if (hotkeyLockApp != null) _hotkeyLockApp = hotkeyLockApp.Trim();
        if (hotkeyCheckIntegrity != null) _hotkeyCheckIntegrity = hotkeyCheckIntegrity.Trim();
        if (hotkeyServerConsole != null) _hotkeyServerConsole = hotkeyServerConsole.Trim();
        // Масштаб строк списка (issue #303): сочетания Ctrl++ / Ctrl+- / Ctrl+0.
        if (hotkeyZoomIn != null) HotkeyZoomIn = hotkeyZoomIn.Trim();
        if (hotkeyZoomOut != null) HotkeyZoomOut = hotkeyZoomOut.Trim();
        if (hotkeyZoomReset != null) HotkeyZoomReset = hotkeyZoomReset.Trim();
        // Командная палитра (Ctrl+K).
        if (hotkeyCommandPalette != null) HotkeyCommandPalette = hotkeyCommandPalette.Trim();
        // Меню закладок (issue #356).
        if (hotkeyBookmarksMenu != null) HotkeyBookmarksMenu = hotkeyBookmarksMenu.Trim();
        OnPropertyChanged(nameof(AllowMultipleInstances));
        OnPropertyChanged(nameof(CheckForUpdatesOnStartup));
        OnPropertyChanged(nameof(AutoUpdateEnabled));
        OnPropertyChanged(nameof(ShowTagFilterPanel));
        OnPropertyChanged(nameof(CloseToTray));
        OnPropertyChanged(nameof(ShowTrayIcon));
        OnPropertyChanged(nameof(ShowSystemNotifications));
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
        OnPropertyChanged(nameof(HotkeyShowRunning));
        OnPropertyChanged(nameof(HotkeyClearSearch));
        OnPropertyChanged(nameof(HotkeyClearTags));
        OnPropertyChanged(nameof(HotkeyRightPanelDetails));
        OnPropertyChanged(nameof(HotkeyFindInList));
        // Меню закладок (issue #356).
        OnPropertyChanged(nameof(HotkeyBookmarksMenu));
        OnPropertyChanged(nameof(HotkeySwitchUser));
        OnPropertyChanged(nameof(HotkeyCheckIntegrity));
        OnPropertyChanged(nameof(HotkeyServerConsole));
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

    // ======================= Пакетное обновление из хранилищ (0.3.9.88) =======================

    private ICommand? _repositoryBatchUpdateCommand;

    /// <summary>
    /// Команда «Обновление из хранилищ…»: окно-чеклист баз с заполненным хранилищем
    /// конфигурации и последовательный прогон через конфигуратор в пакетном режиме.
    /// Активна, если хотя бы у одной базы заполнен адрес хранилища.
    /// </summary>
    public ICommand RepositoryBatchUpdateCommand =>
        _repositoryBatchUpdateCommand ??= new RelayCommand(_ => ExecuteRepositoryBatchUpdate(),
            _ => Infobases.Any(b => b.Repository.HasServer));

    private void ExecuteRepositoryBatchUpdate()
    {
        var withRepo = Infobases.Where(b => b.Repository.HasServer).ToList();
        if (withRepo.Count == 0)
        {
            _dialogs.ShowInfo(LocalizationManager.T("RepoUpdate.NoBases"),
                LocalizationManager.T("RepoUpdate.Title"));
            return;
        }

        // Безопасность: подтверждение перед запуском конфигуратора по нескольким базам.
        if (!_dialogs.Confirm(
                string.Format(LocalizationManager.T("RepoUpdate.ConfirmFormat"), withRepo.Count),
                LocalizationManager.T("RepoUpdate.Title")))
            return;

        var window = new RepositoryBatchUpdateWindow(withRepo)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        window.ShowDialog();
    }

    // ======================= Обозреватель хранилища конфигурации (0.3.9.128) =======================

    private ICommand? _repositoryBrowserCommand;

    /// <summary>
    /// Команда «Хранилище конфигурации…»: окно обозревателя хранилища конфигурации
    /// выбранной базы (подключение, список версий, состав версии). Активна, только если
    /// у выбранной базы заполнен адрес хранилища (<see cref="CanOpenRepositoryBrowser"/>);
    /// CanExecute пересчитывается автоматически при смене выделения (CommandManager
    /// в сеттере SelectedInfobase).
    /// </summary>
    public ICommand RepositoryBrowserCommand =>
        _repositoryBrowserCommand ??= new RelayCommand(_ => ExecuteRepositoryBrowser(),
            _ => CanOpenRepositoryBrowser(SelectedInfobase));

    private void ExecuteRepositoryBrowser()
    {
        if (SelectedInfobase is not { Repository.HasServer: true } infobase)
            return;

        // persistChanges: выгрузка .cf добавляет запись в историю запусков базы — сохраняем список.
        var window = new RepositoryBrowserWindow(infobase, () => ScheduleSave())
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        window.ShowDialog();
    }

    /// <summary>
    /// Доступен ли обозреватель хранилища для выбранной базы: у базы должен быть
    /// заполнен адрес хранилища конфигурации. Чистый предикат — тестируется отдельно.
    /// </summary>
    public static bool CanOpenRepositoryBrowser(Infobase? infobase) =>
        infobase is { Repository.HasServer: true };

    // ======================= Центр обслуживания (0.3.9.89) =======================

    private ICommand? _maintenanceCenterCommand;

    /// <summary>
    /// Команда «Центр обслуживания…»: окно-дашборд со сводкой состояния всех баз
    /// (доступность, последняя копия, размер, кэш, конфигурация, возраст данных,
    /// проверка обновлений). Работает со всем списком, поэтому активна всегда.
    /// </summary>
    public ICommand MaintenanceCenterCommand =>
        _maintenanceCenterCommand ??= new RelayCommand(_ => ExecuteMaintenanceCenter());

    private void ExecuteMaintenanceCenter()
    {
        var window = new MaintenanceCenterWindow(
            Infobases.ToList(),
            () => CheckAvailabilityCommand.Execute(null),
            ib => FindInListCommand.Execute(ib),
            MaintenanceFreeSpaceWarningGb)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        window.ShowDialog();
    }

    // ======================= Инспектор процессов 1С (0.3.9.93) =======================

    private ICommand? _processInspectorCommand;

    /// <summary>
    /// Команда «Инспектор процессов…»: таблица всех запущенных процессов платформы 1С
    /// (база/режим/пользователь/время старта/PID/строка подключения) с автообновлением
    /// и завершением выбранного процесса. Двойной клик по известной базе — переход
    /// к ней в главном окне. Работает со всем списком, поэтому активна всегда.
    /// </summary>
    public ICommand ProcessInspectorCommand =>
        _processInspectorCommand ??= new RelayCommand(_ => ExecuteProcessInspector());

    private void ExecuteProcessInspector()
    {
        var window = new ProcessInspectorWindow(
            Infobases.ToList(),
            ib => FindInListCommand.Execute(ib))
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        window.ShowDialog();
    }

    // ======================= Монитор серверов 1С (0.3.9.124) =======================

    private ICommand? _serverMonitorCommand;

    /// <summary>
    /// Команда «Серверы 1С…»: встроенный монитор серверов 1С через утилиту rac
    /// (0.3.9.124, цикл 0.3.9.123–0.3.9.126) — подключение к серверу (адрес/порт/
    /// логин/пароль), просмотр кластеров, рабочих процессов, сеансов, соединений,
    /// блокировок и информации о кластере. Пароль администратора кластера не
    /// сохраняется на диск (решение планирования). Активна всегда — сервер 1С
    /// не привязан к конкретной базе списка.
    /// </summary>
    public ICommand ServerMonitorCommand =>
        _serverMonitorCommand ??= new RelayCommand(_ => ExecuteServerMonitor());

    private void ExecuteServerMonitor()
    {
        var window = new ServerMonitorWindow
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        window.ShowDialog();
    }

    // ================== Диагностика сети до сервера 1С (0.3.9.232, функция 12) ==================

    private ICommand? _networkDiagnosticsCommand;

    /// <summary>
    /// Команда «Диагностика подключения…»: окно проверки сетевой доступности сервера
    /// 1С (функция 12, цикл 0.3.9.229–233) — DNS-резолв, ICMP-пинг, TCP-проверка
    /// портов с задержками и выводы по проблемам. Доступна для клиент-серверной и
    /// веб-базы (для файловой скрыта); стартовые хост/порт — из настроек подключения.
    /// </summary>
    public ICommand NetworkDiagnosticsCommand =>
        _networkDiagnosticsCommand ??= new RelayCommand(_ => ExecuteNetworkDiagnostics(),
            _ => NetworkDiagnosticsTargets.FromInfobase(SelectedInfobase) is not null);

    private void ExecuteNetworkDiagnostics()
    {
        var target = NetworkDiagnosticsTargets.FromInfobase(SelectedInfobase);
        if (target is null)
            return;

        var portsStore = AppServices.GetRequiredService<IServerPortsStore>();
        var vm = new NetworkDiagnosticsViewModel(
            AppServices.GetRequiredService<INetworkDiagnosticsService>(),
            target,
            action => System.Windows.Application.Current?.Dispatcher.BeginInvoke(action),
            portsStore: portsStore,
            availableServers: NetworkDiagnosticsServerSources.Merge(
                GetAvailableServers(), portsStore, target.Host));

        new NetworkDiagnosticsWindow(vm)
        {
            Owner = System.Windows.Application.Current.MainWindow
        }.ShowDialog();
    }

    // ======================= Импорт баз из кластера 1С (0.3.9.174) =======================

    private ICommand? _importClusterInfobasesCommand;

    /// <summary>
    /// Команда «Импорт из кластера 1С…»: импорт информационных баз из кластера сервера
    /// 1С через утилиту rac (цикл 0.3.9.172–0.3.9.175) — окно с подключением к ragent/RAS,
    /// чеклистом баз кластера (дубликаты по строке подключения сняты и помечены), сводкой
    /// «будет добавлено» и созданием недостающих групп по имени кластера при группировке.
    /// Активна всегда — кластер не привязан к конкретной базе списка (как ServerMonitorCommand).
    /// </summary>
    public ICommand ImportClusterInfobasesCommand =>
        _importClusterInfobasesCommand ??= new RelayCommand(_ => ExecuteImportClusterInfobases());

    private void ExecuteImportClusterInfobases()
    {
        var window = new ClusterImportWindow(
            AppServices.GetRequiredService<IRacClient>(),
            _repository,
            Infobases.ToList())
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        if (window.ShowDialog() != true || window.SelectedBases.Count == 0)
            return;

        // Страховка от дубликатов: окно уже фильтровало по строкам подключения, но
        // список мог измениться с момента построения чеклиста — лишнее не добавляем.
        var added = window.SelectedBases
            .Where(b => !RacInfobaseMapper.IsDuplicate(Infobases, b))
            .ToList();
        if (added.Count == 0)
            return;

        foreach (var infobase in added)
            Infobases.Add(infobase);

        // Недостающие группы по именам из импортируемых баз (имя кластера при
        // группировке) добавляются как корневые — как при добавляющем импорте JSON.
        var existingGroupNames = new HashSet<string>(
            Groups.Select(g => (g.Name ?? "").Trim()), StringComparer.OrdinalIgnoreCase);
        foreach (var groupName in added
            .Select(b => (b.Group ?? "").Trim())
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (existingGroupNames.Add(groupName))
            {
                Groups.Add(new Group { Name = groupName });
            }
        }

        InfobasesView.Refresh();
        Save();
        SaveGroups();
        RebuildGroupTree();

        _dialogs.ShowInfo(
            string.Format(LocalizationManager.T("ClusterImport.SuccessFormat"), added.Count),
            LocalizationManager.T("ClusterImport.Title"));
        _logger.Info($"Импорт из кластера 1С: добавлено {added.Count} баз, " +
                     $"пропущено дубликатов {window.SelectedBases.Count - added.Count}");
    }

    // ======================= Статистика использования баз (0.3.9.95) =======================

    private ICommand? _usageStatisticsCommand;

    /// <summary>
    /// Команда «Статистика использования…»: аналитика по истории запусков всех баз
    /// (число запусков, первый/последний запуск, дней с последнего запуска, сводка,
    /// распределение по дням недели). Работает со всем списком, поэтому активна всегда.
    /// </summary>
    public ICommand UsageStatisticsCommand =>
        _usageStatisticsCommand ??= new RelayCommand(_ => ExecuteUsageStatistics());

    private void ExecuteUsageStatistics()
    {
        var window = new UsageStatisticsWindow(Infobases.ToList())
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        window.ShowDialog();
    }

    // ======================= Сравнение конфигураций (0.3.9.99) =======================

    private ICommand? _configDiffCommand;

    /// <summary>
    /// Команда «Сравнение конфигураций…»: сравнение конфигурации выбранной базы
    /// с эталонным .cf или двух .cf между собой; результат — отчёт об отличиях
    /// по типам метаданных с экспортом CSV/TXT (0.3.9.99, функция №9).
    /// Работает со всем списком, поэтому активна всегда.
    /// </summary>
    public ICommand ConfigDiffCommand =>
        _configDiffCommand ??= new RelayCommand(_ => ExecuteConfigDiff());

    private void ExecuteConfigDiff()
    {
        List<string> installed;
        try { installed = PlatformVersionService.FindInstalledVersions(); }
        catch { installed = new List<string>(); }

        var window = new ConfigDiffSetupWindow(
            Infobases.ToList(),
            SelectedInfobase,
            installed,
            _repository.LoadSettings().LastFileCreatePlatformVersion ?? string.Empty)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        window.ShowDialog();
    }

    // ======================= Обозреватель метаданных (0.3.9.133) =======================

    private ICommand? _metadataExplorerCommand;

    /// <summary>
    /// Команда «Обозреватель метаданных…»: окно просмотра дерева метаданных конфигурации
    /// (подсистемы → типы → объекты) без интерактивного конфигуратора — источник (база
    /// или файл .cf) выгружается в XML через /DumpConfigToFiles (0.3.9.132–0.3.9.136).
    /// Источник выбирается в окне, поэтому активна всегда.
    /// </summary>
    public ICommand MetadataExplorerCommand =>
        _metadataExplorerCommand ??= new RelayCommand(_ => ExecuteMetadataExplorer());

    private void ExecuteMetadataExplorer()
    {
        var window = new MetadataExplorerWindow(Infobases.ToList(), SelectedInfobase)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        window.ShowDialog();
    }
}
#endif
