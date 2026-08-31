#if WINDOWS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.IO;
using MaterialDesignThemes.Wpf;
using Configuration_Management.Localization;
using Configuration_Management.Models;
using Configuration_Management.Services;
using Configuration_Management.Themes;
using Configuration_Management.ViewModels;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using Point = System.Windows.Point;

namespace Configuration_Management
{
    public partial class SettingsWindow
    {
        /// <summary>
        /// Инициализирует выпадающий список «Разрядность по умолчанию» во вкладке «Платформы».
        /// </summary>
        private void InitializeDefaultArchitecture()
        {
            DefaultArchComboBox.Items.Clear();
            DefaultArchComboBox.Items.Add(LocalizationManager.T("Settings.Arch64Recommended"));
            DefaultArchComboBox.Items.Add(LocalizationManager.T("Settings.Arch32"));
            DefaultArchComboBox.SelectedIndex =
                string.Equals(_viewModel.DefaultArchitecture, "X64", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
        }

        /// <summary>Локализованное название колонки по её ключу.</summary>
        private static string ColumnOrderLabel(string key) => LocalizationManager.T(key switch
        {
            "Version" => "Column.Version",
            "Configuration" => "Column.Configuration",
            "LaunchMode" => "Column.LaunchMode",
            "ServerBase" => "Column.ServerBase",
            "LastLaunch" => "Column.LastLaunch",
            "Size" => "Column.Size",
            "Actions" => "Column.Actions",
            _ => "Column.Name"
        });

        /// <summary>Иконка колонки по её ключу — та же, что в заголовке списка баз.</summary>
        private static MaterialDesignThemes.Wpf.PackIconKind ColumnOrderIcon(string key) => key switch
        {
            "Version" => MaterialDesignThemes.Wpf.PackIconKind.Information,
            "Configuration" => MaterialDesignThemes.Wpf.PackIconKind.CubeOutline,
            "LaunchMode" => MaterialDesignThemes.Wpf.PackIconKind.Play,
            "ServerBase" => MaterialDesignThemes.Wpf.PackIconKind.Server,
            "LastLaunch" => MaterialDesignThemes.Wpf.PackIconKind.ClockOutline,
            "Size" => MaterialDesignThemes.Wpf.PackIconKind.Database,
            "Actions" => MaterialDesignThemes.Wpf.PackIconKind.Cog,
            _ => MaterialDesignThemes.Wpf.PackIconKind.FormatTitle
        };

        /// <summary>Видимость колонки по её ключу из текущих настроек.</summary>
        private bool ColumnVisible(string key) => key switch
        {
            "Version" => _viewModel.ShowVersionColumn,
            "Configuration" => _viewModel.ShowConfigurationColumn,
            "LaunchMode" => _viewModel.ShowLaunchModeColumn,
            "ServerBase" => _viewModel.ShowServerColumn,
            "LastLaunch" => _viewModel.ShowLastLaunchColumn,
            "Size" => _viewModel.ShowSizeColumn,
            "Actions" => _viewModel.ShowActionsColumn,
            _ => true
        };

        /// <summary>Заполняет список порядка колонок текущим порядком из настроек.</summary>
        private void InitializeColumnOrder()
        {
            _columnOrderItems.Clear();
            foreach (var key in _viewModel.ColumnOrderKeys)
                _columnOrderItems.Add(new ColumnOrderItem
                {
                    Key = key,
                    Display = ColumnOrderLabel(key),
                    Visible = ColumnVisible(key),
                    IconKind = ColumnOrderIcon(key)
                });
            if (ColumnOrderList != null)
                ColumnOrderList.ItemsSource = _columnOrderItems;
            UpdateColumnOrderButtons();
        }

        /// <summary>Обновляет доступность кнопок «Вверх»/«Вниз» по выбранной строке.</summary>
        private void UpdateColumnOrderButtons()
        {
            if (ColumnOrderList == null)
                return;
            var idx = ColumnOrderList.SelectedIndex;
            if (ColumnOrderUpButton != null)
                ColumnOrderUpButton.IsEnabled = idx > 0;
            if (ColumnOrderDownButton != null)
                ColumnOrderDownButton.IsEnabled = idx >= 0 && idx < _columnOrderItems.Count - 1;
        }

        private void OnColumnOrderList_SelectionChanged(object sender, SelectionChangedEventArgs e)
            => UpdateColumnOrderButtons();

        private void OnColumnOrderUp_Click(object sender, RoutedEventArgs e)
        {
            var idx = ColumnOrderList?.SelectedIndex ?? -1;
            if (idx <= 0 || ColumnOrderList == null)
                return;
            var item = _columnOrderItems[idx];
            _columnOrderItems.Move(idx, idx - 1);
            ColumnOrderList.SelectedIndex = idx - 1;
            UpdateColumnOrderButtons();
        }

        private void OnColumnOrderDown_Click(object sender, RoutedEventArgs e)
        {
            var idx = ColumnOrderList?.SelectedIndex ?? -1;
            if (idx < 0 || idx >= _columnOrderItems.Count - 1 || ColumnOrderList == null)
                return;
            var item = _columnOrderItems[idx];
            _columnOrderItems.Move(idx, idx + 1);
            ColumnOrderList.SelectedIndex = idx + 1;
            UpdateColumnOrderButtons();
        }

        /// <summary>
        /// Инициализирует вкладку «Отображение»: заполняет флажки текущими
        /// настройками отображения списка баз.
        /// </summary>
        private void InitializeDisplaySettings()
        {
            _showFavoritesButton = _viewModel.ShowFavoritesButton;
            _showPinnedButton = _viewModel.ShowPinnedButton;
            _showTags = _viewModel.ShowTags;

            // Видимость колонок (кроме закреплённой «Название») задаётся в том же
            // списке, что и порядок: флажки заполняются в InitializeColumnOrder.
            ShowNameColumnCheck.IsChecked = true;

            InitializeColumnOrder();

            ShowFavoritesButtonCheck.IsChecked = _showFavoritesButton;
            ShowPinnedButtonCheck.IsChecked = _showPinnedButton;
            ShowTagsCheck.IsChecked = _showTags;
            if (ShowTagFilterPanelCheck != null)
                ShowTagFilterPanelCheck.IsChecked = _viewModel.ShowTagFilterPanel;
            if (AllowMultipleInstancesCheck != null)
                AllowMultipleInstancesCheck.IsChecked = _viewModel.AllowMultipleInstances;
            if (CheckForUpdatesOnStartupCheck != null)
                CheckForUpdatesOnStartupCheck.IsChecked = _viewModel.CheckForUpdatesOnStartup;
            if (AutoUpdateEnabledCheck != null)
                AutoUpdateEnabledCheck.IsChecked = _viewModel.AutoUpdateEnabled;
            if (ShowTrayIconCheck != null)
                ShowTrayIconCheck.IsChecked = _viewModel.ShowTrayIcon;
            if (CloseToTrayCheck != null)
                CloseToTrayCheck.IsChecked = _viewModel.CloseToTray;
            if (EscapeToTrayCheck != null)
                EscapeToTrayCheck.IsChecked = _viewModel.EscapeToTray;
            if (AfterLaunchActionCombo != null)
            {
                AfterLaunchActionCombo.ItemsSource = new[]
                {
                    LocalizationManager.T("Settings.General.AfterLaunchAction.None"),
                    LocalizationManager.T("Settings.General.AfterLaunchAction.MinimizeToTray"),
                    LocalizationManager.T("Settings.General.AfterLaunchAction.Close")
                };
                AfterLaunchActionCombo.SelectedIndex = (int)Models.AfterLaunchActionHelper.Parse(_viewModel.AfterLaunchAction);
            }
            if (RememberWindowLayoutCheck != null)
                RememberWindowLayoutCheck.IsChecked = _viewModel.RememberWindowLayout;
            if (CompactModeCheck != null)
                CompactModeCheck.IsChecked = _viewModel.CompactMode;

            GroupByGroupCheck.IsChecked = _viewModel.GroupByGroup;
            ShowFavoritesOnlyCheck.IsChecked = _viewModel.ShowFavoritesOnly;
            if (ShowEmptyGroupsCheck != null)
                ShowEmptyGroupsCheck.IsChecked = _viewModel.ShowEmptyGroups;
            if (AddTimestampToExportFileNameCheck != null)
                AddTimestampToExportFileNameCheck.IsChecked = _viewModel.AddTimestampToExportFileName;

            if (ShowRightPanelDetailsCheck != null)
                ShowRightPanelDetailsCheck.IsChecked = _viewModel.ShowRightPanelDetails;
            if (ShowSessionLaunchPanelCheck != null)
                ShowSessionLaunchPanelCheck.IsChecked = _viewModel.ShowSessionLaunchPanel;
            if (StatusShowConnectionPathCheck != null)
                StatusShowConnectionPathCheck.IsChecked = _viewModel.StatusShowConnectionPath;
            if (StatusShowArchitectureCheck != null)
                StatusShowArchitectureCheck.IsChecked = _viewModel.StatusShowArchitecture;
            if (StatusShowLaunchModeCheck != null)
                StatusShowLaunchModeCheck.IsChecked = _viewModel.StatusShowLaunchMode;
            if (StatusShowPortCheck != null)
                StatusShowPortCheck.IsChecked = _viewModel.StatusShowPort;
            if (StatusShowPlatformVersionCheck != null)
                StatusShowPlatformVersionCheck.IsChecked = _viewModel.StatusShowPlatformVersion;
            if (StatusShowClientTypeCheck != null)
                StatusShowClientTypeCheck.IsChecked = _viewModel.StatusShowClientType;
            if (StatusShowConnectionTypeCheck != null)
                StatusShowConnectionTypeCheck.IsChecked = _viewModel.StatusShowConnectionType;
            if (StatusShowUserCheck != null)
                StatusShowUserCheck.IsChecked = _viewModel.StatusShowUser;
            if (StatusShowIdCheck != null)
                StatusShowIdCheck.IsChecked = _viewModel.StatusShowId;

            InitHotkeyCombos();
        }
    }
}
#endif