#if WINDOWS
using System;
using System.Linq;
using System.Windows.Input;
using Configuration_Management.Localization;
using Configuration_Management.Models;
using Configuration_Management.Services;

namespace Configuration_Management.ViewModels;

/// <summary>
/// Реализация команды «Смена пользователя» (issue #200).
///
/// По кнопке/горячей клавише открывается тот же диалог выбора учётной записи,
/// что и при запуске (см. <see cref="LoginWindow.ShowLogin"/>), но без перезапуска
/// приложения: при успешном входе профиль переключается в работающей программе и
/// данные главного окна перезагружаются; при отмене состояние не меняется.
/// </summary>
public partial class MainViewModel
{
    private ICommand? _switchUserCommand;

    /// <summary>
    /// Горячая клавиша «Смена пользователя». Пусто — не назначена (issue #200).
    /// </summary>
    public string HotkeySwitchUser
    {
        get => _hotkeySwitchUser;
        set
        {
            if (SetProperty(ref _hotkeySwitchUser, NormalizeHotkey(value, "")))
                ScheduleSaveSettings();
        }
    }

    /// <summary>
    /// Команда «Смена пользователя»: открывает диалог входа и при успехе
    /// переключает активный профиль в работающем приложении.
    /// </summary>
    public ICommand SwitchUserCommand =>
        _switchUserCommand ??= new RelayCommand(_ => SwitchUser());

    /// <summary>
    /// true, если учётных записей больше одной — тогда кнопка «Смена пользователя»
    /// показывается на верхней панели (при одной записи переключать нечего).
    /// </summary>
    /// <summary>Реестр учётных записей изменился: пересчитываем видимость кнопки.</summary>
    private void OnProfilesChanged(object? sender, EventArgs e) =>
        OnPropertyChanged(nameof(SwitchUserVisible));

    public bool SwitchUserVisible
    {
        get
        {
            try
            {
                return AppServices.GetRequiredService<IProfileService>().Profiles.Count > 1;
            }
            catch
            {
                // Сервис профилей в тестовом/изолированном контексте может отсутствовать.
                return false;
            }
        }
    }

    /// <summary>
    /// Открывает окно выбора учётной записи и, если пользователь вошёл в другую
    /// запись, переключает активный профиль и перезагружает данные главного окна.
    /// Отмена или выбор той же записи ничего не меняют.
    /// </summary>
    private void SwitchUser()
    {
        try
        {
            var profileService = AppServices.GetRequiredService<IProfileService>();
            if (profileService.Profiles.Count <= 1)
                return;

            var current = profileService.CurrentProfile;
            var selectedId = LoginWindow.ShowLogin(profileService);
            if (selectedId == null)
                return; // Вход отменён — остаёмся как есть.

            if (current != null &&
                string.Equals(current.Id, selectedId, StringComparison.OrdinalIgnoreCase))
                return; // Та же запись — перезагрузка не нужна.

            profileService.SetCurrentProfile(selectedId);
            ReloadAllData();
        }
        catch (Exception ex)
        {
            _logger.Error("Ошибка смены пользователя: " + ex.Message);
            _dialogs.ShowError(string.Format(LocalizationManager.T("Auth.LoginError"), ex.Message));
        }
    }

    /// <summary>
    /// Перезагружает данные главного окна из каталога активного (нового) профиля:
    /// список баз, группы, избранное, состояние дерева, тему/схему, язык и горячие
    /// клавиши. Вызывается после смены пользователя в работающем приложении.
    /// </summary>
    public void ReloadAllData()
    {
        try
        {
            var settings = _repository.LoadSettings();

            // Шаблон имени COM-коннектора у каждого профиля свой, а коннектор — singleton
            // с кэшем шаблона (issue #175). Поле ViewModel тоже перечитываем: иначе окно
            // настроек показало бы шаблон прежнего профиля и записало бы его в новый.
            _comConnectorNameTemplate = settings.ComConnectorNameTemplate ?? string.Empty;
            OnPropertyChanged(nameof(ComConnectorNameTemplate));
            OneCComConnector.ApplyTemplate(_comConnectorNameTemplate);

            // Таймаут определения — вторая настройка COM-чтения, и несвежесть у неё та же:
            // окно настроек показало бы значение прежнего профиля и записало бы его в новый.
            _comDetectTimeoutMs = Math.Max(1000, settings.ComDetectTimeoutMs);
            OnPropertyChanged(nameof(ComDetectTimeoutMs));

            // Основные данные профиля: список баз и групп.
            var saved = _repository.Load();
            Infobases.Clear();
            foreach (var ib in saved)
                Infobases.Add(ib);

            var loadedGroups = _repository.LoadGroups();
            Groups.Clear();
            foreach (var g in loadedGroups)
                Groups.Add(g);
            InfobasesView.Refresh();

            // Состояние дерева и избранного.
            _collapsedGroups.Clear();
            foreach (var key in settings.CollapsedGroups)
                _collapsedGroups.Add(key);

            _favoriteHotkeyIds.Clear();
            if (settings.FavoriteHotkeyIds != null)
            {
                foreach (var id in settings.FavoriteHotkeyIds.Take(9))
                {
                    if (!string.IsNullOrEmpty(id))
                        _favoriteHotkeyIds.Add(id);
                }
            }

            _showFavoritesOnly = settings.ShowFavoritesOnly;
            _groupByGroup = settings.GroupByGroup;
            _showEmptyGroups = settings.ShowEmptyGroups;
            _sortField = string.IsNullOrWhiteSpace(settings.SortField) ? "Name" : settings.SortField;
            _sortAscending = settings.SortAscending;
            _lastSelectedInfobaseId = settings.LastSelectedInfobaseId ?? string.Empty;
            _lastSelectedGroupPath = settings.LastSelectedGroupPath ?? string.Empty;

            // Горячие клавиши нового профиля.
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
            _hotkeyClearSearch = string.IsNullOrWhiteSpace(settings.HotkeyClearSearch) ? "Ctrl+Shift+C" : settings.HotkeyClearSearch.Trim();
            _hotkeyClearTags = string.IsNullOrWhiteSpace(settings.HotkeyClearTags) ? "Ctrl+Shift+T" : settings.HotkeyClearTags.Trim();
            _hotkeyRightPanelDetails = settings.HotkeyRightPanelDetails?.Trim() ?? "";
            _hotkeySwitchUser = settings.HotkeySwitchUser?.Trim() ?? "";

            // Тема и схема нового профиля.
            _savedTheme = settings.Theme;
            _activeColorScheme = Models.ColorScheme.FromLegacy(
                settings.ActiveColorScheme, settings.LightColorScheme, settings.DarkColorScheme);

            // Пересобираем дерево, избранное, сортировку, выделение и тему.
            ApplySortDescriptions();
            RebuildGroupTree();
            SyncFavoriteHotkeys();
            PrepareLastSelectionExpansion();
            ApplyActiveColorSchemeToUi();

            // Локализация выбранного профиля.
            try { LocalizationManager.Instance.Initialize(settings.Language); }
            catch { /* локализация не должна ломать смену пользователя */ }

            // Уведомляем UI о перезагруженных данных.
            OnPropertyChanged(nameof(Infobases));
            OnPropertyChanged(nameof(Groups));
            OnPropertyChanged(nameof(ShowFavoritesOnly));
            OnPropertyChanged(nameof(GroupByGroup));
            OnPropertyChanged(nameof(ShowEmptyGroups));
            OnPropertyChanged(nameof(SwitchUserVisible));
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
            OnPropertyChanged(nameof(HotkeyClearSearch));
            OnPropertyChanged(nameof(HotkeyClearTags));
            OnPropertyChanged(nameof(HotkeyRightPanelDetails));
            OnPropertyChanged(nameof(HotkeySwitchUser));
            FavoriteHotkeysChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.Error("Ошибка перезагрузки данных после смены пользователя", ex);
        }
    }
}
#endif