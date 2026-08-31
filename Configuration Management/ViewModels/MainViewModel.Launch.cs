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
    private void ScheduleSave()
    {
        _saveDebounceCts?.Cancel();
        _saveDebounceCts?.Dispose();
        var cts = new CancellationTokenSource();
        _saveDebounceCts = cts;
        var token = cts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(SaveDebounceMs, token).ConfigureAwait(false);
                if (token.IsCancellationRequested)
                    return;

                // Снимок коллекции на UI-потоке, запись файла — в фоне.
                List<Infobase> snapshot = Application.Current?.Dispatcher is { } dispatcher
                    ? await dispatcher.InvokeAsync(() => Infobases.ToList())
                    : Infobases.ToList();

                if (token.IsCancellationRequested)
                    return;

                await _repository.SaveAsync(snapshot, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Новый клик отменил предыдущее сохранение — нормально.
            }
            catch (Exception ex)
            {
                _logger.Error("Ошибка отложенного сохранения баз", ex);
                try
                {
                    Application.Current?.Dispatcher.Invoke(() =>
                        _dialogs.ShowError(
                            string.Format(LocalizationManager.T("Main.ErrSaveBases"), ex.Message),
                            LocalizationManager.T("Main.ErrSaveBasesTitle")));
                }
                catch
                {
                    // ignore secondary UI failures
                }
            }
        }, token);
    }

    private void LaunchEnterpriseWithParams(object? parameter)
    {
        if (SelectedInfobase is null) return;
        var dlg = new Configuration_Management.LaunchParametersWindow(SelectedInfobase.LaunchParameters ?? "")
        {
            Owner = Application.Current?.MainWindow
        };
        if (dlg.ShowDialog() != true) return;
        var saved = SelectedInfobase.LaunchParameters ?? "";
        try
        {
            SelectedInfobase.LaunchParameters = dlg.Result ?? "";
            var ok = _launcher.Launch(SelectedInfobase, OneCLaunchMode.Enterprise);
            if (ok)
            {
                SelectedInfobase.LastLaunchDate = DateTime.Now;
                Save();
            }
        }
        finally
        {
            SelectedInfobase.LaunchParameters = saved;
        }
    }

    private void LaunchConfiguratorWithParams(object? parameter)
    {
        if (SelectedInfobase is null) return;
        var dlg = new Configuration_Management.LaunchParametersWindow(SelectedInfobase.LaunchParameters ?? "")
        {
            Owner = Application.Current?.MainWindow
        };
        if (dlg.ShowDialog() != true) return;
        var saved = SelectedInfobase.LaunchParameters ?? "";
        try
        {
            SelectedInfobase.LaunchParameters = dlg.Result ?? "";
            var ok = _launcher.Launch(SelectedInfobase, OneCLaunchMode.Configurator);
            if (ok)
            {
                SelectedInfobase.LastLaunchDate = DateTime.Now;
                Save();
            }
        }
        finally
        {
            SelectedInfobase.LaunchParameters = saved;
        }
    }

    private void LaunchEnterpriseWithAuth(object? parameter)
    {
        if (SelectedInfobase is null) return;
        var conn = SelectedInfobase.Connection;
        var savedUser = conn.User;
        var savedPwd = conn.Password;
        var savedAuth = conn.AuthenticationMode;
        try
        {
            conn.User = string.Empty;
            conn.Password = string.Empty;
            conn.AuthenticationMode = AuthenticationMode.Prompt;
            var ok = _launcher.Launch(SelectedInfobase, OneCLaunchMode.Enterprise);
            if (ok)
            {
                SelectedInfobase.LastLaunchDate = DateTime.Now;
                Save();
            }
        }
        finally
        {
            conn.User = savedUser;
            conn.Password = savedPwd;
            conn.AuthenticationMode = savedAuth;
        }
    }

    private void LaunchNativeStarter()
    {
        InfobaseMaintenanceService.OpenNativeStarter();
    }

    /// <summary>
    /// Единая точка запуска 1С. parameter — LaunchKind, строка имени enum или null (Enterprise).
    /// Для Enterprise учитываются переопределения «Текущая сессия» (клиент и разрядность).
    /// </summary>
    private void Launch(object? parameter, bool runAsAdmin = false, Infobase? target = null)
    {
        var ib = target ?? SelectedInfobase;
        if (ib is null)
            return;

        var kind = ResolveLaunchKind(parameter);
        bool ok;
        switch (kind)
        {
            case LaunchKind.Configurator:
                ok = _launcher.Launch(ib, OneCLaunchMode.Configurator, runAsAdmin);
                break;
            case LaunchKind.Thin32:
                ok = _launcher.Launch(ib, OneCLaunchMode.Enterprise, OneCClientType.Thin, OneCArchitecture.x86, runAsAdmin);
                break;
            case LaunchKind.Thick32:
                ok = _launcher.Launch(ib, OneCLaunchMode.Enterprise, OneCClientType.Thick, OneCArchitecture.x86, runAsAdmin);
                break;
            case LaunchKind.Thin64:
                ok = _launcher.Launch(ib, OneCLaunchMode.Enterprise, OneCClientType.Thin, OneCArchitecture.x64, runAsAdmin);
                break;
            case LaunchKind.Thick64:
                ok = _launcher.Launch(ib, OneCLaunchMode.Enterprise, OneCClientType.Thick, OneCArchitecture.x64, runAsAdmin);
                break;
            default:
                ok = LaunchEnterpriseWithSessionOverrides(ib, runAsAdmin);
                break;
        }

        if (ok)
        {
            var sessionDetails = string.Format(
                LocalizationManager.T("Main.LaunchHistorySessionDetails"),
                _sessionClientMode, _sessionArchitecture);
            ib.AddLaunchHistory(kind.ToString(), sessionDetails);
            InfobasesView.Refresh();
            Save();
            _logger.Info($"Запущена база «{ib.Name}» ({kind}, клиент={_sessionClientMode}, арх={_sessionArchitecture})");
            NotifyAfterLaunch();
        }
        else
        {
            _logger.Warn($"Не удалось запустить базу «{ib.Name}» ({kind})");
        }
    }

    /// <summary>
    /// Запуск 1С:Предприятие с учётом переключателей «Текущая сессия».
    /// </summary>
    private bool LaunchEnterpriseWithSessionOverrides(Infobase ib, bool runAsAdmin = false)
    {
        // Полностью «Авто» — стандартная логика по настройкам базы.
        if (_sessionClientMode == SessionClientMode.Auto &&
            _sessionArchitecture == SessionArchitectureMode.Auto)
        {
            return _launcher.Launch(ib, OneCLaunchMode.Enterprise, runAsAdmin);
        }

        OneCClientType? client = _sessionClientMode switch
        {
            SessionClientMode.Thin => OneCClientType.Thin,
            SessionClientMode.Thick => OneCClientType.Thick,
            SessionClientMode.ThickOrdinary => OneCClientType.Thick,
            SessionClientMode.Ordinary => OneCClientType.Thick,
            _ => ResolveClientFromInfobase(ib)
        };

        var arch = _sessionArchitecture switch
        {
            SessionArchitectureMode.X86 => OneCArchitecture.x86,
            SessionArchitectureMode.X64 => OneCArchitecture.x64,
            _ => OneCLauncher.ResolveArchitecture(ib.Architecture, ib.PlatformVersion)
        };

        // Режим форм: «Толстый (управляемые формы)» и «Толстый (обычные формы)» задают
        // его явно; в остальных случаях берём из настройки базы при автоматическом клиенте.
        OneCRunMode? runMode = _sessionClientMode switch
        {
            SessionClientMode.Thick => OneCRunMode.Managed,
            SessionClientMode.ThickOrdinary => OneCRunMode.Ordinary,
            SessionClientMode.Auto => OneCLauncher.GetRunModeFromLaunchMode(ib.LaunchMode),
            _ => null
        };

        return _launcher.Launch(ib, OneCLaunchMode.Enterprise, client, runMode, arch, runAsAdmin);
    }

    /// <summary>Тип клиента из настройки базы (LaunchMode).</summary>
    private static OneCClientType? ResolveClientFromInfobase(Infobase ib)
    {
        if (string.Equals(ib.LaunchMode, "Автоматический", StringComparison.OrdinalIgnoreCase))
            return null;
        if (string.Equals(ib.LaunchMode, "Толстый клиент (обычные формы)", StringComparison.OrdinalIgnoreCase))
            return OneCClientType.Thick;
        if (string.Equals(ib.LaunchMode, "Толстый клиент", StringComparison.OrdinalIgnoreCase))
            return OneCClientType.Thick;
        if (string.Equals(ib.LaunchMode, "Тонкий клиент", StringComparison.OrdinalIgnoreCase))
            return OneCClientType.Thin;
        // Веб и прочее — без принудительного /RunMode
        return null;
    }

    private static LaunchKind ResolveLaunchKind(object? parameter) => parameter switch
    {
        LaunchKind k => k,
        string s when Enum.TryParse<LaunchKind>(s, true, out var parsed) => parsed,
        _ => LaunchKind.Enterprise
    };

    private bool FilterInfobase(object item)
    {
        if (item is not Infobase infobase)
            return false;

        if (_listViewMode == ListViewMode.Favorites && !infobase.IsFavorite)
            return false;
        if (_listViewMode == ListViewMode.Recent && !infobase.LastLaunchDate.HasValue)
            return false;

        if (_activeTagFilterSet.Count > 0
            && !infobase.Tags.Any(t => _activeTagFilterSet.Contains(t)))
            return false;

        var filter = SearchText?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(filter))
            return true;

        return infobase.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
               || (infobase.Description?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
               || (infobase.Group?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
               || (infobase.PlatformVersion?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
               || (infobase.ServerDatabaseDisplay?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
               || (infobase.ConnectionStringDisplay?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
               || infobase.Tags.Any(t => t.Contains(filter, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Сохранение списка баз после точечного изменения (версия платформы и т.п.).</summary>
    public void PersistInfobasesAfterInlineEdit()
    {
        try
        {
            Save();
            InfobasesView?.Refresh();
        }
        catch (Exception ex)
        {
            _logger.Error("Ошибка сохранения после правки базы", ex);
            _dialogs.ShowError(
                string.Format(LocalizationManager.T("Main.ErrSaveChange"), ex.Message),
                LocalizationManager.T("Main.ErrSaveBasesTitle"));
        }
    }

    private void Save()
    {
        try
        {
            _repository.Save(Infobases.ToList());
        }
        catch (Exception ex)
        {
            _logger.Error("Ошибка сохранения баз", ex);
            throw;
        }
    }

    private async Task SaveAsync()
    {
        try
        {
            await _repository.SaveAsync(Infobases.ToList()).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.Error("Ошибка асинхронного сохранения баз", ex);
            _dialogs.ShowError(
                string.Format(LocalizationManager.T("Main.ErrSaveBases"), ex.Message),
                LocalizationManager.T("Main.ErrSaveBasesTitle"));
        }
    }

    private void SaveGroups()
    {
        try
        {
            _repository.SaveGroups(Groups.ToList());
        }
        catch (Exception ex)
        {
            _logger.Error("Ошибка сохранения групп", ex);
            throw;
        }
    }

    private async Task SaveGroupsAsync()
    {
        try
        {
            await _repository.SaveGroupsAsync(Groups.ToList()).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.Error("Ошибка асинхронного сохранения групп", ex);
        }
    }

    /// <summary>
    /// Применяет выбранный язык интерфейса и сохраняет его в настройках.
    /// Язык применяется сразу (окна с привязками Loc обновляются) и
    /// восстанавливается при следующем запуске.
    /// </summary>
    /// <param name="code">Код языка, например "ru", "en" или загруженного внешнего.</param>
    public void ApplyLanguage(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return;

        try
        {
            var settings = _repository.LoadSettings();
            settings.Language = code;
            _repository.SaveSettings(settings);
        }
        catch (Exception ex)
        {
            _logger.Error("Не удалось сохранить язык интерфейса", ex);
        }

        try
        {
            Configuration_Management.Localization.LocalizationManager.Instance.SetLanguage(code);
        }
        catch (Exception ex)
        {
            _logger.Error("Не удалось применить язык интерфейса", ex);
        }
    }

    public void SaveSettings()
    {
        _repository.SaveSettings(new AppSettings
        {
            // Актуальный язык интерфейса сохраняется всегда, чтобы выбор
            // пользователя не затирался при закрытии окна (OnClosing).
            Language = Configuration_Management.Localization.LocalizationManager.Instance.CurrentLanguage,
            ShowFavoritesOnly = _showFavoritesOnly,
            GroupByGroup = _groupByGroup,
            ShowEmptyGroups = _showEmptyGroups,
            Theme = _savedTheme,
            ActiveColorScheme = _activeColorScheme,
            LightColorScheme = _lightColorScheme,
            DarkColorScheme = _darkColorScheme,
            CollapsedGroups = _collapsedGroups.ToList(),
            InstalledPlatformVersions = _installedPlatformVersions,
            AdditionalPlatformSearchPaths = _additionalPlatformSearchPaths,
            NameColumnWidth = _nameColumnWidth,
            VersionColumnWidth = _versionColumnWidth,
            LaunchModeColumnWidth = _launchModeColumnWidth,
            ServerColumnWidth = _serverColumnWidth,
            LastLaunchColumnWidth = _lastLaunchColumnWidth,
            ShowFavoritesButton = _showFavoritesButton,
            ShowPinnedButton = _showPinnedButton,
            ShowTags = _showTags,
            ShowTagFilterPanel = _showTagFilterPanel,
            AllowMultipleInstances = _allowMultipleInstances,
            CheckForUpdatesOnStartup = _checkForUpdatesOnStartup,
            AutoUpdateEnabled = _autoUpdateEnabled,
            ShowVersionColumn = _showVersionColumn,
            ShowConfigurationColumn = _showConfigurationColumn,
            ConfigurationColumnWidth = _configurationColumnWidth,
            ActionsColumnWidth = _actionsColumnWidth,
            ShowRightPanelDetails = _showRightPanelDetails,
            ShowSessionLaunchPanel = _showSessionLaunchPanel,
            SessionClientMode = _sessionClientMode.ToString(),
            SessionArchitecture = _sessionArchitecture.ToString(),
            DefaultArchitecture = _defaultArchitecture,
            StatusShowConnectionPath = _statusShowConnectionPath,
            StatusShowArchitecture = _statusShowArchitecture,
            StatusShowLaunchMode = _statusShowLaunchMode,
            StatusShowPort = _statusShowPort,
            StatusShowPlatformVersion = _statusShowPlatformVersion,
            StatusShowClientType = _statusShowClientType,
            StatusShowConnectionType = _statusShowConnectionType,
            StatusShowUser = _statusShowUser,
            StatusShowId = _statusShowId,
            ShowLaunchModeColumn = _showLaunchModeColumn,
            ShowServerColumn = _showServerColumn,
            ShowLastLaunchColumn = _showLastLaunchColumn,
            ShowSizeColumn = _showSizeColumn,
            ShowActionsColumn = _showActionsColumn,
            SizeColumnWidth = _sizeColumnWidth,
            ColumnOrder = _columnOrder.ToList(),
            WindowWidth = _windowWidth,
            WindowHeight = _windowHeight,
            WindowLeft = _windowLeft,
            WindowTop = _windowTop,
            WindowState = _windowState,
            RememberWindowLayout = _rememberWindowLayout,
            IbasesSyncMode = _ibasesSyncMode,
            IbasesSyncFilePath = _ibasesSyncFilePath,
            IbasesSyncTrigger = _ibasesSyncTrigger,
            IbasesSyncIntervalMinutes = _ibasesSyncIntervalMinutes,
            IbasesSyncScheduleTime = _ibasesSyncScheduleTime,
            IbasesBackupEnabled = _ibasesBackupEnabled,
            IbasesBackupKeepCount = _ibasesBackupKeepCount,
            AddTimestampToExportFileName = _addTimestampToExportFileName,
            ExportTimestampFormat = _exportTimestampFormat,
            CloseToTray = _closeToTray,
            AfterLaunchAction = _afterLaunchAction,
            ShowTrayIcon = _showTrayIcon,
            EscapeToTray = _escapeToTray,
            CompactMode = _compactMode,
            TemplateCatalogPaths = _templateCatalogPaths.ToList(),
            HotkeyEnterprise = _hotkeyEnterprise,
            HotkeyConfigurator = _hotkeyConfigurator,
            HotkeyFavorite = _hotkeyFavorite,
            HotkeyEdit = _hotkeyEdit,
            HotkeyDelete = _hotkeyDelete,
            HotkeyClearCache = _hotkeyClearCache,
            HotkeyAdd = _hotkeyAdd,
            HotkeyPin = _hotkeyPin,
            HotkeyShowAll = _hotkeyShowAll,
            HotkeyShowFavorites = _hotkeyShowFavorites,
            HotkeyShowRecent = _hotkeyShowRecent,
            SortField = _sortField,
            SortAscending = _sortAscending,
            FavoriteHotkeyIds = _favoriteHotkeyIds.ToList(),
            NoGroupColor = _noGroupColor,
            NoGroupIconColor = _noGroupIconColor,
            NoGroupIcon = _noGroupIcon,
            PinnedColor = _pinnedColor,
            PinnedIconColor = _pinnedIconColor,
            PinnedIcon = _pinnedIcon,
            FontFamily = _fontFamily,
            FontSize = _fontSize,
            FontWeight = _fontWeight,
            FontStyle = _fontStyle,
            ElementFonts = _elementFonts,
            LastSelectedInfobaseId = _lastSelectedInfobaseId,
            LastSelectedGroupPath = _lastSelectedGroupPath,
            ProfileBackupDirectory = _profileBackupDirectory,
            ProfileRestoreOnStartup = _profileRestoreOnStartup,
            FileSizeCache = new Dictionary<string, Models.FileSizeCacheEntry>(_fileSizeCache)
        });
    }

    /// <summary>
    /// Сохраняет ширины колонок списка баз в настройках.
    /// </summary>
    public void SaveColumnWidths(double nameWidth, double versionWidth, double configurationWidth, double launchModeWidth, double serverWidth, double lastLaunchWidth, double actionsWidth)
    {
        NameColumnWidth = nameWidth;
        VersionColumnWidth = versionWidth;
        ConfigurationColumnWidth = configurationWidth;
        LaunchModeColumnWidth = launchModeWidth;
        ServerColumnWidth = serverWidth;
        LastLaunchColumnWidth = lastLaunchWidth;
        ActionsColumnWidth = actionsWidth;
        SaveSettings();
    }

    /// <summary>
    /// Обновляет ширины колонок в памяти (без сохранения в файл).
    /// Используется для синхронизации колонок строк во время перетаскивания разделителя.
    /// </summary>
    public void UpdateColumnWidths(double nameWidth, double versionWidth, double configurationWidth, double launchModeWidth, double serverWidth, double lastLaunchWidth, double actionsWidth)
    {
        NameColumnWidth = nameWidth;
        VersionColumnWidth = versionWidth;
        ConfigurationColumnWidth = configurationWidth;
        LaunchModeColumnWidth = launchModeWidth;
        ServerColumnWidth = serverWidth;
        LastLaunchColumnWidth = lastLaunchWidth;
        ActionsColumnWidth = actionsWidth;
    }
}
#endif
