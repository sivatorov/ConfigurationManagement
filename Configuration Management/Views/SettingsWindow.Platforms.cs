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
        /// Обновляет список установленных версий платформы, сканируя стандартные
        /// и дополнительные каталоги 1С.
        /// </summary>
        private void OnRefreshPlatforms_Click(object sender, RoutedEventArgs e)
        {
            PlatformVersionService.SetAdditionalSearchPaths(_additionalPlatformPaths);
            UpdatePlatformsDisplay();
            _viewModel.SetInstalledPlatformVersions(_installedPlatformVersions);
        }

        private void OnAddPlatformPath_Click(object sender, RoutedEventArgs e)
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = LocalizationManager.T("Settings.ChoosePlatformFolder"),
                UseDescriptionForTitle = true,
                ShowNewFolderButton = false
            };

            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                return;

            var path = dialog.SelectedPath?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(path))
                return;

            if (_additionalPlatformPaths.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show(LocalizationManager.T("Settings.PathAlreadyAdded"),
                    LocalizationManager.T("Settings.AdditionalPathsTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _additionalPlatformPaths.Add(path);
            PlatformVersionService.SetAdditionalSearchPaths(_additionalPlatformPaths);
            UpdatePlatformsDisplay();
        }

        private void OnEditPlatformPath_Click(object sender, RoutedEventArgs e)
        {
            var selected = AdditionalPathsList?.SelectedItem as string;
            if (string.IsNullOrEmpty(selected))
            {
                MessageBox.Show(LocalizationManager.T("Settings.SelectPathToEdit"),
                    LocalizationManager.T("Settings.AdditionalPathsTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = LocalizationManager.T("Settings.ChooseNewPlatformFolder"),
                UseDescriptionForTitle = true,
                ShowNewFolderButton = false,
                SelectedPath = selected
            };

            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                return;

            var path = dialog.SelectedPath?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(path))
                return;

            if (_additionalPlatformPaths.Any(p =>
                    !string.Equals(p, selected, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(p, path, StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show(LocalizationManager.T("Settings.PathAlreadyAdded"),
                    LocalizationManager.T("Settings.AdditionalPathsTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var index = _additionalPlatformPaths.IndexOf(selected);
            if (index < 0) return;
            _additionalPlatformPaths[index] = path;
            if (AdditionalPathsList != null)
                AdditionalPathsList.SelectedItem = path;
            PlatformVersionService.SetAdditionalSearchPaths(_additionalPlatformPaths);
            UpdatePlatformsDisplay();
        }

        private void OnRemovePlatformPath_Click(object sender, RoutedEventArgs e)
        {
            var selected = AdditionalPathsList?.SelectedItem as string;
            if (string.IsNullOrEmpty(selected))
            {
                MessageBox.Show(LocalizationManager.T("Settings.SelectPathToRemove"),
                    LocalizationManager.T("Settings.AdditionalPathsTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _additionalPlatformPaths.Remove(selected);
            PlatformVersionService.SetAdditionalSearchPaths(_additionalPlatformPaths);
            UpdatePlatformsDisplay();
        }

        /// <summary>
        /// Обновляет список платформ: линия (8.3) → разрядность → сборка с путём
        /// (тот же принцип, что в диалоге выбора версии).
        /// </summary>
        private void UpdatePlatformsDisplay()
        {
            PlatformsTree.Items.Clear();

            var infos = PlatformVersionService.FindInstalledVersionInfos(_additionalPlatformPaths);
            _installedPlatformVersions = infos.Select(i => i.Display).ToList();

            if (infos.Count == 0)
            {
                StatusText.Text = LocalizationManager.T("Settings.PlatformsNotFound");
                return;
            }

            var tree = PlatformVersionService.BuildGroupedTree(infos);
            foreach (var node in tree)
                PlatformsTree.Items.Add(node);

            StatusText.Text = string.Format(LocalizationManager.T("Settings.PlatformsFound"), infos.Count);

            // Разворачиваем линии 8.x, чтобы группировка была видна сразу
            Dispatcher.BeginInvoke(new Action(() => ExpandPlatformTreeGroups(PlatformsTree)),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private static void ExpandPlatformTreeGroups(ItemsControl parent)
        {
            parent.UpdateLayout();
            foreach (var item in parent.Items)
            {
                if (item is not Models.PlatformVersionGroup node || node.IsLeaf)
                    continue;
                if (parent.ItemContainerGenerator.ContainerFromItem(item) is not TreeViewItem container)
                    continue;
                container.IsExpanded = true;
                container.UpdateLayout();
                ExpandPlatformTreeGroups(container);
            }
        }

        private void OnExportInfobases_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.ExportInfobasesCommand.Execute(null);
        }

        private void OnRemoveMissingFileBases_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.RemoveMissingFileBasesCommand.Execute(null);
            RefreshGroupsAfterDataChange();
        }

        private void OnKillOneCProcesses_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.KillOneCProcessesCommand.Execute(null);
        }

        private void OnImportInfobases_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.ImportInfobasesCommand.Execute(null);
            RefreshGroupsAfterDataChange();
        }

        private void OnImportIbasesV8i_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.ImportFromIbasesV8iCommand.Execute(null);
            RefreshGroupsAfterDataChange();
        }

        private void OnClearAllInfobases_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.ClearAllInfobasesCommand.Execute(null);
            RefreshGroupsAfterDataChange();
        }

        /// <summary>
        /// Обновляет локальную копию списка групп после изменения данных
        /// командами дополнительных функций.
        /// </summary>
        /// <summary>
        /// После импорта/очистки данные уже в MainViewModel; локальный список групп в настройках не ведётся.
        /// </summary>
        private void RefreshGroupsAfterDataChange()
        {
            // Группы управляются из главного окна.
        }
        private void OnRestoreIbasesBackup_Click(object sender, RoutedEventArgs e)
        {
            var filePath = SyncFilePathTextBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(filePath))
                filePath = Services.IbasesV8iImporter.FindDefaultPath() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(filePath))
            {
                MessageBox.Show(LocalizationManager.T("Settings.Ibases.RestoreNoPath"), LocalizationManager.T("Settings.Ibases.RestoreTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var backups = Services.IbasesBackupService.ListBackups(filePath);
            if (backups.Count == 0)
            {
                MessageBox.Show(string.Format(LocalizationManager.T("Settings.Ibases.RestoreNoBackups"), filePath),
                    LocalizationManager.T("Settings.Ibases.RestoreTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var latest = backups[0];
            var result = MessageBox.Show(
                string.Format(LocalizationManager.T("Settings.Ibases.RestoreConfirm"), System.IO.Path.GetFileName(latest)),
                LocalizationManager.T("Settings.Ibases.RestoreConfirmTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
                return;

            try
            {
                Services.IbasesBackupService.RestoreBackup(latest, filePath);
                MessageBox.Show(LocalizationManager.T("Settings.Ibases.RestoreOk"),
                    LocalizationManager.T("Settings.Ibases.RestoreTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(string.Format(LocalizationManager.T("Settings.Ibases.RestoreFailed"), ex.Message),
                    LocalizationManager.T("Common.Error"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>Читает выбранное в окне настроек действие «после запуска базы/конфигуратора».</summary>
        private string ReadAfterLaunchAction()
        {
            if (AfterLaunchActionCombo?.SelectedIndex is int idx && idx >= 0 && idx <= 2)
                return ((Models.AfterLaunchAction)idx).ToSettingString();
            return _viewModel.AfterLaunchAction;
        }


        private void OnAddTemplatePath_Click(object sender, RoutedEventArgs e)
        {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = LocalizationManager.T("Settings.Bases.AddTemplateFolderDesc"),
                UseDescriptionForTitle = true
            };
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            var path = dlg.SelectedPath;
            if (TemplatePathsList.Items.Cast<string>().Any(x => string.Equals(x, path, StringComparison.OrdinalIgnoreCase)))
                return;
            TemplatePathsList.Items.Add(path);
        }

        private void OnRemoveTemplatePath_Click(object sender, RoutedEventArgs e)
        {
            if (TemplatePathsList.SelectedItem is string path)
                TemplatePathsList.Items.Remove(path);
        }

        private void OnEditTemplatePath_Click(object sender, RoutedEventArgs e)
        {
            if (TemplatePathsList.SelectedItem is not string currentPath)
                return;

            using var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = LocalizationManager.T("Settings.Bases.EditTemplateFolderDesc"),
                UseDescriptionForTitle = true,
                SelectedPath = currentPath
            };
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            var path = dlg.SelectedPath;
            if (string.IsNullOrWhiteSpace(path)) return;
            if (TemplatePathsList.Items.Cast<string>().Any(x =>
                    !string.Equals(x, currentPath, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(x, path, StringComparison.OrdinalIgnoreCase)))
                return;

            var index = TemplatePathsList.Items.IndexOf(currentPath);
            if (index < 0) return;
            TemplatePathsList.Items[index] = path;
            TemplatePathsList.SelectedItem = path;
        }

        private void OnLoadDefaultTemplatePaths_Click(object sender, RoutedEventArgs e)
        {
            TemplatePathsList.Items.Clear();
            foreach (var p in Configuration_Management.Services.OneCTemplateService.GetTemplateRootFolders())
                TemplatePathsList.Items.Add(p);
            var def = Configuration_Management.Services.OneCTemplateService.GetConfiguredOrDefaultTemplatePath();
            if (!string.IsNullOrEmpty(def) && !TemplatePathsList.Items.Cast<string>().Any(x => string.Equals(x, def, StringComparison.OrdinalIgnoreCase)))
                TemplatePathsList.Items.Insert(0, def);
        }

        private void OnAboutLink_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement { Tag: string url } && !string.IsNullOrWhiteSpace(url))
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = url,
                        UseShellExecute = true
                    });
                }
                catch { /* ignore */ }
            }
        }

        /// <summary>
        /// Открывает спонсорскую картинку «О программе» (donat.png) в полном размере
        /// в отдельном окне с прокруткой, если картинка больше окна.
        /// </summary>
        private void OnDonatImage_Click(object sender, MouseButtonEventArgs e)
        {
            try
            {
                var bmp = new BitmapImage(new Uri("pack://application:,,,/donat.png"));
                var scroll = new ScrollViewer
                {
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Background = Brushes.Black,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Content = new Image { Source = bmp, Stretch = Stretch.None, SnapsToDevicePixels = true }
                };

                var wa = SystemParameters.WorkArea;
                var win = new Window
                {
                    Title = "donat.png",
                    Background = Brushes.Black,
                    ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Owner = this,
                    Content = scroll
                };
                // Окно ограничено рабочей областью; при большем размере картинки появляется прокрутка.
                win.Width = Math.Min(bmp.Width, wa.Width * 0.9);
                win.Height = Math.Min(bmp.Height, wa.Height * 0.9);
                if (win.Width < 300) win.Width = 300;
                if (win.Height < 200) win.Height = 200;
                win.ShowDialog();
            }
            catch
            {
                // Изображение не загрузилось — просто ничего не показываем.
            }
        }

        /// <summary>
        /// Копирует обезличенную техническую информацию о системе и приложении в буфер обмена
        /// (для диагностики проблемы разработчику). Работает в Windows и Linux.
        /// </summary>
        private void OnCopyTechInfo_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Windows.Clipboard.SetText(TechnicalInfoService.Collect());
                MessageBox.Show(
                    LocalizationManager.T("Settings.About.TechInfoCopied"),
                    LocalizationManager.T("Common.Information"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    LocalizationManager.T("Settings.About.TechInfoCopyFailed") + "\n" + ex.Message,
                    LocalizationManager.T("Common.Error"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Ручная проверка обновлений из вкладки «О программе». Сообщает явный результат
        /// (актуальная версия / ошибка / доступно обновление) через UpdateService.
        /// </summary>
        private async void OnCheckForUpdates_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var updateService = AppServices.GetRequiredService<UpdateService>();
                await updateService.CheckForUpdatesManualAsync();
            }
            catch
            {
                // Внутренние ошибки уже показаны в UpdateService; здесь только страхуемся.
            }
        }
    }
}
#endif