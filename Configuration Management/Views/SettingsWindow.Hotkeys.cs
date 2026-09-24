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
        /// <summary>Инициализирует поля ввода горячих клавиш (запись комбинаций Ctrl/Shift/Alt).</summary>
        private void InitHotkeyCombos()
        {
            BindHotkeyBox(HotkeyEnterpriseBox, _viewModel.HotkeyEnterprise);
            BindHotkeyBox(HotkeyConfiguratorBox, _viewModel.HotkeyConfigurator);
            BindHotkeyBox(HotkeyFavoriteBox, _viewModel.HotkeyFavorite);
            BindHotkeyBox(HotkeyEditBox, _viewModel.HotkeyEdit);
            BindHotkeyBox(HotkeyDeleteBox, _viewModel.HotkeyDelete);
            BindHotkeyBox(HotkeyClearCacheBox, _viewModel.HotkeyClearCache);
            BindHotkeyBox(HotkeyAddBox, _viewModel.HotkeyAdd);
            BindHotkeyBox(HotkeyPinBox, _viewModel.HotkeyPin);
            BindHotkeyBox(HotkeyShowAllBox, _viewModel.HotkeyShowAll);
            BindHotkeyBox(HotkeyShowFavoritesBox, _viewModel.HotkeyShowFavorites);
            BindHotkeyBox(HotkeyShowRecentBox, _viewModel.HotkeyShowRecent);
            BindHotkeyBox(HotkeyClearSearchBox, _viewModel.HotkeyClearSearch);
            BindHotkeyBox(HotkeyClearTagsBox, _viewModel.HotkeyClearTags);
            BindHotkeyBox(HotkeyRightPanelDetailsBox, _viewModel.HotkeyRightPanelDetails);
            BindHotkeyBox(HotkeyFindInListBox, _viewModel.HotkeyFindInList);
            BindHotkeyBox(HotkeySwitchUserBox, _viewModel.HotkeySwitchUser);
        }

        private static void BindHotkeyBox(Controls.HotkeyBox? box, string current)
        {
            if (box is null) return;
            box.Value = current?.Trim() ?? string.Empty;
        }

        private static string ReadHotkeyBox(Controls.HotkeyBox? box)
        {
            var s = box?.Value;
            // Поле «Нет»/«None» показывает локализованный Common.None при пустом назначении.
            if (string.IsNullOrWhiteSpace(s) ||
                string.Equals(s, LocalizationManager.T("Common.None"), StringComparison.Ordinal))
                return "";
            return s.Trim();
        }

        private void InitializeFavoriteHotkeys()
        {
            _favoriteHotkeyItems.Clear();
            int n = 1;
            foreach (var key in _viewModel.FavoriteHotkeyIds)
            {
                var ib = _viewModel.FindByFavoriteKey(key);
                _favoriteHotkeyItems.Add(new FavoriteHotkeyItem
                {
                    Key = key,
                    Number = n,
                    Name = ib?.Name ?? key
                });
                n++;
            }
            if (FavoriteHotkeysList != null)
                FavoriteHotkeysList.ItemsSource = _favoriteHotkeyItems;
        }

        /// <summary>
        /// Инициализирует выбор шаблона (формата) даты и времени для имени файла при выгрузке:
        /// заполняет список популярных шаблонов и показывает предпросмотр текущего.
        /// </summary>
        private void InitializeExportTimestampSettings()
        {
            if (ExportTimestampFormatComboBox == null)
                return;

            ExportTimestampFormatComboBox.Items.Clear();
            string[] templates =
            {
                "yyyyMMdd_HHmmss",
                "yyyy-MM-dd_HH-mm-ss",
                "yyyy-MM-dd_HHmmss",
                "dd.MM.yyyy HH-mm-ss",
                "yyyyMMdd",
                "HHmmss"
            };
            foreach (var t in templates)
                ExportTimestampFormatComboBox.Items.Add(t);

            var current = _viewModel.ExportTimestampFormat;
            ExportTimestampFormatComboBox.Text = string.IsNullOrWhiteSpace(current) ? "yyyyMMdd_HHmmss" : current;
            UpdateExportTimestampPreview();
        }

        /// <summary>Обновляет текст предпросмотра отметки даты и времени по выбранному шаблону.</summary>
        private void UpdateExportTimestampPreview()
        {
            if (ExportTimestampPreview == null)
                return;

            var format = ExportTimestampFormatComboBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(format))
            {
                ExportTimestampPreview.Text = LocalizationManager.T("Settings.TimestampSpecifyHint");
                return;
            }

            try
            {
                var preview = string.Format(LocalizationManager.T("Settings.TimestampBasePrefix"), DateTime.Now.ToString(format));
                ExportTimestampPreview.Text = string.Format(LocalizationManager.T("Settings.TimestampExample"), preview);
            }
            catch (FormatException)
            {
                ExportTimestampPreview.Text = LocalizationManager.T("Settings.TimestampInvalid");
            }
        }

        private void OnExportTimestampFormat_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateExportTimestampPreview();
        }

        private void RefreshFavoriteHotkeyNumbers()
        {
            var snapshot = _favoriteHotkeyItems.ToList();
            _favoriteHotkeyItems.Clear();
            for (int i = 0; i < snapshot.Count; i++)
            {
                snapshot[i].Number = i + 1;
                _favoriteHotkeyItems.Add(snapshot[i]);
            }
        }

        private void OnFavoriteHotkeyUp_Click(object sender, RoutedEventArgs e)
        {
            var idx = FavoriteHotkeysList.SelectedIndex;
            if (idx <= 0) return;
            var item = _favoriteHotkeyItems[idx];
            _favoriteHotkeyItems.RemoveAt(idx);
            _favoriteHotkeyItems.Insert(idx - 1, item);
            RefreshFavoriteHotkeyNumbers();
            FavoriteHotkeysList.SelectedIndex = idx - 1;
        }

        private void OnFavoriteHotkeyDown_Click(object sender, RoutedEventArgs e)
        {
            var idx = FavoriteHotkeysList.SelectedIndex;
            if (idx < 0 || idx >= _favoriteHotkeyItems.Count - 1) return;
            var item = _favoriteHotkeyItems[idx];
            _favoriteHotkeyItems.RemoveAt(idx);
            _favoriteHotkeyItems.Insert(idx + 1, item);
            RefreshFavoriteHotkeyNumbers();
            FavoriteHotkeysList.SelectedIndex = idx + 1;
        }

        private sealed class FavoriteHotkeyItem
        {
            public string Key { get; set; } = string.Empty;
            public int Number { get; set; }
            public string Name { get; set; } = string.Empty;
            public string Display => $"Alt+{Number}: {Name}";
        }
    }
}
#endif