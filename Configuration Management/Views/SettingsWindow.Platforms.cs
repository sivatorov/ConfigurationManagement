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
                _dialogs.ShowInfo(LocalizationManager.T("Settings.PathAlreadyAdded"),
                    LocalizationManager.T("Settings.AdditionalPathsTitle"));
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
                _dialogs.ShowInfo(LocalizationManager.T("Settings.SelectPathToEdit"),
                    LocalizationManager.T("Settings.AdditionalPathsTitle"));
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
                _dialogs.ShowInfo(LocalizationManager.T("Settings.PathAlreadyAdded"),
                    LocalizationManager.T("Settings.AdditionalPathsTitle"));
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
                _dialogs.ShowInfo(LocalizationManager.T("Settings.SelectPathToRemove"),
                    LocalizationManager.T("Settings.AdditionalPathsTitle"));
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

        private void OnExportSelectedInfobases_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.ExportSelectedInfobasesCommand.Execute(null);
        }

        private void OnImportMergeInfobases_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.ImportMergeInfobasesCommand.Execute(null);
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

        // Импорт баз и настроек платформы из программы StartManager (issue #163).
        private void OnImportStartManager_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.ImportFromStartManager();
            RefreshGroupsAfterDataChange();
        }

        /// <summary>
        /// Открывает диалог «Определение конфигураций всех баз» (issue #236). После закрытия
        /// сохраняет список, если хотя бы одна база была изменена (правки вносятся прямо
        /// в объекты <see cref="Infobase"/>).
        /// </summary>
        private void OnDetectAllConfigurations_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new DetectConfigurationsWindow(
                _viewModel.Infobases.ToList(),
                editBase: ib => _viewModel.EditInfobaseCommand.Execute(ib))
            {
                Owner = this
            };
            dialog.ShowDialog();
            if (dialog.DataChanged)
                _viewModel.PersistInfobasesAfterInlineEdit();
        }

        /// <summary>
        /// Открывает диалог «Поиск потерянных и забытых баз 1С на дисках» (issue #247):
        /// сканирование корней в поисках файловых баз 1С и добавление отсутствующих в списке
        /// приложения. После закрытия сохраняет список, если хотя бы одна база была добавлена.
        /// </summary>
        private void OnFindLostBases_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new FindLostBasesWindow(
                _viewModel.Infobases,
                ib => _viewModel.Infobases.Add(ib))
            {
                Owner = this
            };
            dialog.ShowDialog();
            if (dialog.DataChanged)
                _viewModel.PersistInfobasesAfterInlineEdit();
        }

        /// <summary>
        /// Открывает диалог «Очистка истории запусков» (issue #246): таблица баз с флажками,
        /// именем и количеством записей истории. После закрытия сохраняет список, если хотя бы
        /// у одной базы история была очищена (правки вносятся прямо в объекты <see cref="Infobase"/>).
        /// </summary>
        private void OnClearLaunchHistory_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new ClearHistoryWindow(_viewModel.Infobases.ToList())
            {
                Owner = this
            };
            dialog.ShowDialog();
            if (dialog.DataChanged)
                _viewModel.PersistInfobasesAfterInlineEdit();
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
                _dialogs.ShowWarning(LocalizationManager.T("Settings.Ibases.RestoreNoPath"), LocalizationManager.T("Settings.Ibases.RestoreTitle"));
                return;
            }

            var backups = Services.IbasesBackupService.ListBackups(filePath);
            if (backups.Count == 0)
            {
                _dialogs.ShowInfo(string.Format(LocalizationManager.T("Settings.Ibases.RestoreNoBackups"), filePath),
                    LocalizationManager.T("Settings.Ibases.RestoreTitle"));
                return;
            }

            var latest = backups[0];
            if (!_dialogs.Confirm(
                    string.Format(LocalizationManager.T("Settings.Ibases.RestoreConfirm"), System.IO.Path.GetFileName(latest)),
                    LocalizationManager.T("Settings.Ibases.RestoreConfirmTitle")))
                return;

            try
            {
                Services.IbasesBackupService.RestoreBackup(latest, filePath);
                _dialogs.ShowInfo(LocalizationManager.T("Settings.Ibases.RestoreOk"),
                    LocalizationManager.T("Settings.Ibases.RestoreTitle"));
            }
            catch (Exception ex)
            {
                _dialogs.ShowError(string.Format(LocalizationManager.T("Settings.Ibases.RestoreFailed"), ex.Message),
                    LocalizationManager.T("Common.Error"));
            }
        }

        /// <summary>Читает выбранное в окне настроек действие «после запуска базы/конфигуратора».</summary>
        private string ReadAfterLaunchAction()
        {
            if (AfterLaunchActionCombo?.SelectedIndex is int idx && idx >= 0 && idx <= 3)
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
        /// Открывает спонсорскую картинку «О программе» (donat.png) в отдельном окне.
        /// Размер окна равен размеру картинки (ширина окна — по ширине картинки, высота —
        /// по её пропорциям). Если картинка больше доступной рабочей области экрана,
        /// она пропорционально уменьшается, чтобы целиком поместиться без прокрутки.
        /// Рабочая область берётся с учётом разрешения и масштаба (DPI).
        /// </summary>
        private void OnDonatImage_Click(object sender, MouseButtonEventArgs e)
        {
            try
            {
                var bmp = new BitmapImage(new Uri("pack://application:,,,/donat.png"));

                // Размер картинки в DIP (единицах WPF): ImageSource.Width/Height уже
                // учитывают DPI самого изображения.
                var imageWidth = bmp.Width;
                var imageHeight = bmp.Height;

                // Доступная рабочая область экрана: физические пиксели переводим в DIP
                // через DPI-масштаб окна (при 100% — 1 к 1, при 150%/200% — больше),
                // чтобы окно корректно вписывалось независимо от масштабирования Windows.
                var dpi = VisualTreeHelper.GetDpi(this);
                var workArea = System.Windows.Forms.Screen.FromHandle(
                    new System.Windows.Interop.WindowInteropHelper(this).Handle).WorkingArea;
                var maxWidth = workArea.Width / dpi.DpiScaleX * 0.9;
                var maxHeight = workArea.Height / dpi.DpiScaleY * 0.85;

                // Масштаб = 1, если картинка помещается; иначе уменьшаем пропорционально,
                // чтобы она целиком уместилась в рабочей области без прокрутки.
                var scale = Math.Min(1.0, Math.Min(maxWidth / imageWidth, maxHeight / imageHeight));
                if (imageWidth <= 0 || imageHeight <= 0) scale = 1.0;

                var win = new Window
                {
                    Title = "donat.png",
                    Background = Brushes.Black,
                    ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Owner = this,
                    // Ширина и высота окна — по размерам картинки (с учётом масштаба),
                    // поэтому окно точно соответствует изображению.
                    Width = imageWidth * scale,
                    Height = imageHeight * scale,
                    // Uniform страхует от обрезки при неточном размере и сохраняет пропорции.
                    Content = new Image { Source = bmp, Stretch = Stretch.Uniform }
                };
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
                _dialogs.ShowInfo(
                    LocalizationManager.T("Settings.About.TechInfoCopied"),
                    LocalizationManager.T("Common.Information"));
            }
            catch (Exception ex)
            {
                _dialogs.ShowError(
                    LocalizationManager.T("Settings.About.TechInfoCopyFailed") + "\n" + ex.Message,
                    LocalizationManager.T("Common.Error"));
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

        /// <summary>
        /// «Скачать Workspace 1C» из вкладки «О программе» (0.3.12.70): открывает тот же
        /// диалог перехода, который скачивает новую программу с прогрессом в самом окне.
        /// </summary>
        private void OnWorkspace1CDownload_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var migrationService = AppServices.GetRequiredService<Workspace1CMigrationService>();
                var window = new Workspace1CMigrationWindow(migrationService) { Owner = this };
                window.ShowDialog();
            }
            catch
            {
                // Диалог не должен ронять окно настроек.
            }
        }

        /// <summary>
        /// «Открыть страницу» из вкладки «О программе» (0.3.12.70): открывает страницу
        /// проекта Workspace 1C в браузере по умолчанию.
        /// </summary>
        private void OnWorkspace1COpenPage_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = Workspace1CMigrationService.ProjectUrl,
                    UseShellExecute = true
                });
            }
            catch
            {
                // Браузер не открылся — ничего страшного.
            }
        }
    }
}
#endif