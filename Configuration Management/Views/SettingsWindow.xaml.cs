#if WINDOWS
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Configuration_Management.Localization;
using Configuration_Management.Models;
using Configuration_Management.Services;
using Configuration_Management.ViewModels;
using Microsoft.Win32;

namespace Configuration_Management
{
    /// <summary>
    /// Диалог настроек приложения с горизонтальными вкладками:
    /// «Платформы», «Отображение», «Клавиши», «Настройки», «ibases.v8i», «Базы» и «О программе».
    /// Управление группами — в основном окне (добавление/редактирование через список баз).
    /// </summary>
    public partial class SettingsWindow : Window
    {
        private readonly MainViewModel _viewModel;
        private readonly SettingsViewModel _settings;
        private List<string> _installedPlatformVersions;
        private readonly ObservableCollection<string> _additionalPlatformPaths = new();
        private bool _showFavoritesButton = true;
        private bool _showPinnedButton = true;
        private bool _showTags = true;
        private readonly ObservableCollection<FavoriteHotkeyItem> _favoriteHotkeyItems = new();
        private readonly ObservableCollection<ColumnOrderItem> _columnOrderItems = new();

        // ---- Шрифт интерфейса ----
        // Рабочие копии настроек шрифтов областей хранятся в SettingsViewModel.ElementFonts.
        private string _currentElement = Themes.ThemeManager.FontDefault;

        // ---- Резервное копирование профиля ----
        private System.Windows.Controls.TextBox _profileDirBox = null!;
        private System.Windows.Controls.CheckBox _profileRestoreCheck = null!;

        // ---- Цветовое оформление ----
        private readonly ObservableCollection<ColorItem> _colorItems = new();
        private bool _suppressSchemeEvent;

        /// <summary>
        /// Создаёт диалог настроек приложения.
        /// </summary>
        /// <param name="viewModel">Главная модель представления приложения.</param>
        public SettingsWindow(MainViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel;
            _settings = new SettingsViewModel(viewModel);
            _installedPlatformVersions = new List<string>(viewModel.InstalledPlatformVersions);
            foreach (var path in viewModel.AdditionalPlatformSearchPaths)
                _additionalPlatformPaths.Add(path);
            if (AdditionalPathsList != null)
                AdditionalPathsList.ItemsSource = _additionalPlatformPaths;
            UpdatePlatformsDisplay();
            InitializeDefaultArchitecture();
            InitializeSyncSettings();
            InitializeDisplaySettings();
            InitializeFontSettings();
            InitializeFavoriteHotkeys();
            InitializeExportTimestampSettings();
            InitializeColorSchemes();
            InitializeLanguage();
            InitializeProfileBackupTab();
            InitializeAccountsTab();
        }

        /// <summary>Переключатель компактного режима: применяет изменение сразу и сохраняет.</summary>
        private void OnCompactMode_Toggled(object sender, RoutedEventArgs e)
        {
            if (CompactModeCheck is null)
                return;
            _viewModel.ApplyCompactMode(CompactModeCheck.IsChecked == true);
        }

        /// <summary>
        /// Список установленных версий платформы 1С.
        /// </summary>
        public List<string> Result => _installedPlatformVersions;

        /// <summary>
        /// Живая строка версии для вкладки «О программе»: номер из InformationalVersion
        /// без суффикса «+<sha>», как в Avalonia-версии (SettingsWindow.Avalonia.cs).
        /// </summary>
        public string AboutVersion =>
            string.Format(LocalizationManager.T("Settings.About.Version"), VersionInfo.Display());

        private void OnSave_Click(object sender, RoutedEventArgs e)
        {
            // Сохраняем версии платформы и дополнительные пути поиска.
            _viewModel.SetAdditionalPlatformSearchPaths(_additionalPlatformPaths);
            _viewModel.SetInstalledPlatformVersions(_installedPlatformVersions);

            // Разрядность по умолчанию (Настройки → Платформы).
            _viewModel.ApplyDefaultArchitecture(DefaultArchComboBox.SelectedIndex == 0 ? "X64" : "X86");

            // Сохраняем настройки синхронизации с файлом ibases.v8i.
            var s = _settings.Sync;
            var filePath = SyncFilePathTextBox.Text?.Trim() ?? string.Empty;
            var interval = SettingsViewModel.IbasesSyncSettings.ParseInterval(SyncIntervalTextBox.Text);
            var scheduleTime = SyncScheduleTimePicker.Text?.Trim() ?? string.Empty;
            _viewModel.ApplyIbasesSyncSettings(s.Mode, filePath, s.Trigger, interval, scheduleTime,
                IbasesBackupEnabledCheck.IsChecked ?? true,
                int.TryParse(IbasesBackupKeepCountBox.Text, out var keep) && keep > 0 ? keep : 5);

            // Сохраняем настройки резервного копирования профиля.
            _viewModel.ApplyProfileBackupSettings(_profileDirBox.Text, _profileRestoreCheck.IsChecked == true);

            // Сохраняем настройки отображения списка баз.
            // Видимость колонок читается из тех же элементов списка, где задаётся
            // и порядок: флажок каждой строки и есть её видимость.
            bool VisibleOf(string key) => _columnOrderItems.FirstOrDefault(i => i.Key == key)?.Visible ?? true;

            _viewModel.ApplyDisplaySettings(
                ShowFavoritesButtonCheck.IsChecked ?? false,
                ShowPinnedButtonCheck.IsChecked ?? false,
                ShowTagsCheck.IsChecked ?? false,
                VisibleOf("Version"),
                VisibleOf("LaunchMode"),
                VisibleOf("ServerBase"),
                VisibleOf("LastLaunch"),
                GroupByGroupCheck.IsChecked ?? true,
                ShowFavoritesOnlyCheck.IsChecked ?? false,
                VisibleOf("Size"),
                VisibleOf("Configuration"),
                ShowEmptyGroupsCheck?.IsChecked ?? false,
                _columnOrderItems.Select(i => i.Key).ToList(),
                VisibleOf("Actions"));

            _viewModel.ShowRightPanelDetails = ShowRightPanelDetailsCheck?.IsChecked ?? true;
            _viewModel.ShowSessionLaunchPanel = ShowSessionLaunchPanelCheck?.IsChecked ?? true;
            _viewModel.ApplyStatusBarSettings(
                StatusShowConnectionPathCheck?.IsChecked ?? true,
                StatusShowArchitectureCheck?.IsChecked ?? true,
                StatusShowLaunchModeCheck?.IsChecked ?? true,
                StatusShowPortCheck?.IsChecked ?? true,
                StatusShowPlatformVersionCheck?.IsChecked ?? true,
                StatusShowClientTypeCheck?.IsChecked ?? false,
                StatusShowConnectionTypeCheck?.IsChecked ?? false,
                StatusShowUserCheck?.IsChecked ?? false,
                StatusShowIdCheck?.IsChecked ?? false);
            var hkEnterprise = ReadHotkeyBox(HotkeyEnterpriseBox);
            var hkConfigurator = ReadHotkeyBox(HotkeyConfiguratorBox);
            var hkFavorite = ReadHotkeyBox(HotkeyFavoriteBox);
            var hkEdit = ReadHotkeyBox(HotkeyEditBox);
            var hkDelete = ReadHotkeyBox(HotkeyDeleteBox);
            var hkClearCache = ReadHotkeyBox(HotkeyClearCacheBox);
            var hkAdd = ReadHotkeyBox(HotkeyAddBox);
            var hkPin = ReadHotkeyBox(HotkeyPinBox);
            var hkShowAll = ReadHotkeyBox(HotkeyShowAllBox);
            var hkShowFavorites = ReadHotkeyBox(HotkeyShowFavoritesBox);
            var hkShowRecent = ReadHotkeyBox(HotkeyShowRecentBox);

            // Проверка: одна клавиша — одно действие (пустые «Нет» не учитываются).
            var assigned = new (string Name, string Key)[]
            {
                (LocalizationManager.T("Main.Enterprise"), hkEnterprise),
                (LocalizationManager.T("Main.SectionConfigurator"), hkConfigurator),
                (LocalizationManager.T("Main.Favorites"), hkFavorite),
                (LocalizationManager.T("Main.EditShort"), hkEdit),
                (LocalizationManager.T("Common.Delete"), hkDelete),
                (LocalizationManager.T("Main.ClearCache"), hkClearCache),
                (LocalizationManager.T("Main.AddBase"), hkAdd),
                (LocalizationManager.T("Main.Pin"), hkPin),
                (LocalizationManager.T("Main.AllBasesTooltip"), hkShowAll),
                (LocalizationManager.T("Main.FavoritesTooltip"), hkShowFavorites),
                (LocalizationManager.T("Main.RecentTooltip"), hkShowRecent)
            };
            var duplicates = SettingsViewModel.FindDuplicateHotkeys(assigned).ToList();
            if (duplicates.Count > 0)
            {
                var msg = string.Join("\n", duplicates.Select(g =>
                    string.Format(LocalizationManager.T("Settings.Hotkeys.AssignedTo"), g.Key,
                        string.Join(", ", g.Select(x => x.Name)))));
                MessageBox.Show(
                    string.Format(LocalizationManager.T("Settings.Hotkeys.DuplicateMsg"), msg),
                    LocalizationManager.T("Settings.Hotkeys.DuplicateTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            _viewModel.ApplyAppBehaviorSettings(
                AllowMultipleInstancesCheck.IsChecked ?? false,
                CheckForUpdatesOnStartupCheck?.IsChecked ?? true,
                AutoUpdateEnabledCheck?.IsChecked ?? true,
                ShowTagFilterPanelCheck.IsChecked ?? true,
                CloseToTrayCheck.IsChecked ?? false,
                ShowTrayIconCheck.IsChecked ?? true,
                hkEnterprise,
                hkConfigurator,
                hkFavorite,
                hkEdit,
                hkDelete,
                hkClearCache,
                hkAdd,
                hkPin,
                EscapeToTrayCheck.IsChecked ?? true,
                hkShowAll,
                hkShowFavorites,
                hkShowRecent,
                RememberWindowLayoutCheck.IsChecked ?? true,
                ReadAfterLaunchAction());

            var templatePaths = TemplatePathsList?.Items.Cast<string>().Where(s => !string.IsNullOrWhiteSpace(s)).ToList()
                ?? new System.Collections.Generic.List<string>();
            _viewModel.SetTemplateCatalogPaths(templatePaths);

            // Добавление даты-времени к имени файла при выгрузке (JSON, .dt, .cf).
            _viewModel.ApplyExportFileNameSettings(AddTimestampToExportFileNameCheck?.IsChecked ?? true);

            // Шаблон (формат) отметки даты и времени для имени файла при выгрузке.
            _viewModel.ApplyExportTimestampFormat(ExportTimestampFormatComboBox?.Text ?? "yyyyMMdd_HHmmss");


            // Порядок горячих клавиш избранного.
            _viewModel.SetFavoriteHotkeyOrder(_favoriteHotkeyItems.Select(i => i.Key));

            // Сохраняем все темы, изменённые во вкладке «Цветовое оформление»: каждая тема
            // хранит собственные настройки независимо (встроенные — в своём слоте базовой
            // темы, пользовательские — в своём JSON-файле).
            _settings.PersistEditedSchemes();
            ThemeDebug($"Settings OK: applying '{_settings.CurrentColorScheme.Name}' (isDark={_settings.CurrentColorScheme.IsDark}, colors={_settings.CurrentColorScheme.Colors.Count})");
            _viewModel.ApplyColorScheme(_settings.CurrentColorScheme);

            // Сохраняем настройки шрифта интерфейса (общий и отдельных областей).
            ReadFontSelection();
            _viewModel.SaveElementFonts(_settings.ElementFonts);

            DialogResult = true;
        }

        private void OnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        /// <summary>Элемент списка тем.</summary>
        private sealed class SchemeComboItem
        {
            public string Name { get; set; } = string.Empty;
            public string? DisplayName { get; set; }
            public bool IsBuiltIn { get; set; }
            public override string ToString() => string.IsNullOrEmpty(DisplayName) ? Name : DisplayName;
        }

        /// <summary>Элемент редактора цветов: подпись, ключ, HEX и кисть-образец.</summary>
        private sealed class ColorItem : INotifyPropertyChanged
        {
            public string Key { get; set; } = string.Empty;
            public string Label { get; set; } = string.Empty;

            private string _hex = "#000000";
            public string Hex
            {
                get => _hex;
                set
                {
                    _hex = value;
                    OnPropertyChanged(nameof(Hex));
                    ColorBrush = ParseBrush(value);
                    OnPropertyChanged(nameof(ColorBrush));
                }
            }

            private SolidColorBrush _colorBrush = new(Colors.Black);
            public SolidColorBrush ColorBrush
            {
                get => _colorBrush;
                private set { _colorBrush = value; OnPropertyChanged(nameof(ColorBrush)); }
            }

            public event PropertyChangedEventHandler? PropertyChanged;
            private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

            private static SolidColorBrush ParseBrush(string hex)
            {
                try
                {
                    return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
                }
                catch
                {
                    return new SolidColorBrush(Colors.Transparent);
                }
            }
        }

        /// <summary>Элемент списка колонок: хранит ключ, имя и флаг видимости.
        /// Один элемент объединяет порядок и видимость колонки — оба редактируются
        /// в одном списке на вкладке «Отображение».</summary>
        private sealed class ColumnOrderItem
        {
            public string Key { get; init; } = string.Empty;
            public string Display { get; init; } = string.Empty;
            public bool Visible { get; set; } = true;
            public MaterialDesignThemes.Wpf.PackIconKind IconKind { get; init; }

            public override string ToString() => Display;
        }

        /// <summary>Элемент списка областей интерфейса для выбора шрифта.</summary>
        private sealed class ElementScopeItem
        {
            public string Key { get; }
            public ElementScopeItem(string key) { Key = key; }
            public override string ToString() => Themes.ThemeManager.FontScopeDisplayName(Key);
        }

        /// <summary>Доступные начертания шрифта. Технические Weight/Style не локализуются.</summary>
        private static readonly FontFaceItem[] FontFaces =
        {
            new() { Key = "Settings.Font.StyleNormal", Weight = "Normal", Style = "Normal" },
            new() { Key = "Settings.Font.StyleBold", Weight = "Bold", Style = "Normal" },
            new() { Key = "Settings.Font.StyleItalic", Weight = "Normal", Style = "Italic" },
            new() { Key = "Settings.Font.StyleBoldItalic", Weight = "Bold", Style = "Italic" }
        };

        /// <summary>
        /// Компаратор для сортировки версий по убыванию.
        /// Учитывает суффикс разрядности «(32)» / «(64)»: в пределах одной версии
        /// 64-битный вариант считается более новым.
        /// </summary>
        private sealed class VersionComparer : IComparer<string>
        {
            public int Compare(string? x, string? y)
            {
                if (x == y) return 0;
                if (x is null) return -1;
                if (y is null) return 1;

                var result = CompareCore(x, y);
                if (result != 0)
                    return result;

                // Версии совпадают — сравниваем разрядность (64 > 32).
                return GetArch(x).CompareTo(GetArch(y));
            }

            private static int CompareCore(string x, string y)
            {
                PlatformVersionService.ParseVariant(x, out var xv, out _);
                PlatformVersionService.ParseVariant(y, out var yv, out _);

                var xParts = xv.Split('.').Select(int.Parse).ToArray();
                var yParts = yv.Split('.').Select(int.Parse).ToArray();

                var length = Math.Max(xParts.Length, yParts.Length);
                for (var i = 0; i < length; i++)
                {
                    var xVal = i < xParts.Length ? xParts[i] : 0;
                    var yVal = i < yParts.Length ? yParts[i] : 0;
                    if (xVal != yVal)
                        return xVal.CompareTo(yVal);
                }

                return 0;
            }

            private static int GetArch(string variant)
            {
                PlatformVersionService.ParseVariant(variant, out _, out var architecture);
                return architecture == "64" ? 1 : 0;
            }
        }
    }
}
#endif