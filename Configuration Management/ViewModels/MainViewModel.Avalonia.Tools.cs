#if LINUX
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Avalonia.Threading;
using Configuration_Management.Localization;
using Configuration_Management.Models;
using Configuration_Management.Services;

namespace Configuration_Management.ViewModels;

/// <summary>Main ViewModel (Avalonia/Linux): экспорт/импорт, обнаружение платформ/шаблонов, утилиты (partial).</summary>
public partial class MainViewModel : ViewModelBase
{
    /// <summary>Версии платформы, найденные штатными путями и дополнительными путями
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

    /// <summary>
    /// Добавляет найденную на диске базу в список приложения (issue #247, диалог
    /// «Поиск потерянных и забытых баз 1С»). Только добавляет в коллекцию; сохранение
    /// и перестроение дерева выполняет вызывающий код через
    /// <see cref="PersistInfobasesAfterInlineEdit"/> после закрытия диалога, если были
    /// добавления.
    /// </summary>
    public void AddFoundInfobase(Infobase infobase)
    {
        _allInfobases.Add(infobase);
        OnPropertyChanged(nameof(Infobases));
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
        // Разрядность записываем, только если она задана в выбранном варианте ЯВНО суффиксом
        // «(32)/(64)». ParseVariant по умолчанию возвращает разрядность даже без суффикса,
        // поэтому выбор папки/частичной версии («8.3», «8.3.27») не должен подставлять x86 —
        // разрешение разрядности остаётся за лаунчером (issue #251).
        if ((arch is "32" or "64") && PlatformVersionService.HasExplicitArchitecture(selected))
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
    /// Проверяет доступность всех баз 1С и помечает недоступные красным крестиком
    /// в списке баз. Ручная команда верхней панели команд вместо автопроверки при
    /// запуске: старт не блокируется запросами ко всем базам. Доступность
    /// определяется по факту: для файловых баз — наличие каталога/файла базы по
    /// пути, для клиент-серверных — реальная попытка подключения, для веб-баз —
    /// заполненность адреса.
    /// <para>
    /// Сразу после запуска все базы помечаются как «проверяется» (серый значок
    /// ожидания). Результат каждой базы публикуется в строку состояния по мере
    /// готовности, а значок обновляется через подписку строки на свойства базы.
    /// Проверки идут в фоне с ограниченным параллелизмом, чтобы не блокировать UI.
    /// </para>
    /// </summary>
    private void CheckAvailability()
    {
        if (_availabilityCheckRunning)
            return;

        var targets = Infobases.ToList();
        if (targets.Count == 0)
        {
            ShowTemporaryStatusMessage(LocalizationManager.T("Main.ConfigListEmpty"));
            return;
        }

        _availabilityCheckRunning = true;
        IsLoading = true;
        LoadingMessage = LocalizationManager.T("Main.CheckAvailabilityLabel");
        foreach (var ib in targets)
            ib.SetChecking(true);
        RebuildTree();
        StatusBarInfo = string.Format(
            LocalizationManager.T("Main.AvailabilityProgress"), 0, targets.Count);

        _ = RunAvailabilityCheckAsync(targets);
    }

    /// <summary>Ограничение параллельных проверок доступности баз.</summary>
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

                    // Каждая завершённая база сразу публикуется в UI-потоке: иконка
                    // строки меняется с серой на фактический статус (по PropertyChanged),
                    // в строке состояния обновляется прогресс «i из N».
                    Dispatcher.UIThread.Post(() =>
                    {
                        ib.SetCheckedAvailability(available);
                        StatusBarInfo = string.Format(
                            LocalizationManager.T("Main.AvailabilityProgress"), done, targets.Count);
                    });
                }));

            Dispatcher.UIThread.Post(() =>
            {
                IsLoading = false;
                RebuildTree();
                var total = results.Count;
                var unavailable = results.Count(r => !r.Available);
                ShowTemporaryStatusMessage(string.Format(
                    LocalizationManager.T("Main.AvailabilityStatus"),
                    total, total - unavailable, unavailable));
            });
        }
        finally
        {
            _availabilityCheckRunning = false;
        }
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
    private bool IsBaseAvailable(Infobase ib)
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

    /// <summary>Предупреждение в журнал из окна: журнал живёт во вьюмодели.</summary>
    public void LogWarning(string message) => _logger.Warn(message);
}
#endif