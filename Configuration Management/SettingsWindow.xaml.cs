using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Configuration_Management.Models;
using Configuration_Management.Services;
using Configuration_Management.ViewModels;
using Microsoft.Win32;

namespace Configuration_Management
{
    /// <summary>
    /// Диалог настроек приложения с горизонтальными вкладками:
    /// «Платформы», «Группы» и «Дополнительные функции».
    /// </summary>
    public partial class SettingsWindow : Window
    {
        private readonly MainViewModel _viewModel;
        private readonly ObservableCollection<Group> _groups;
        private List<string> _installedPlatformVersions;
        private IbasesSyncMode _syncMode;
        private string _syncFilePath = string.Empty;
        private IbasesSyncTrigger _syncTrigger = IbasesSyncTrigger.OnStartup;
        private int _syncIntervalMinutes = 30;
        private string _syncScheduleTime = "09:00";

        /// <summary>
        /// Создаёт диалог настроек приложения.
        /// </summary>
        /// <param name="viewModel">Главная модель представления приложения.</param>
        public SettingsWindow(MainViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel;
            _installedPlatformVersions = new List<string>(viewModel.InstalledPlatformVersions);
            _groups = new ObservableCollection<Group>(viewModel.Groups);
            RebuildTree();
            UpdatePlatformsDisplay();
            InitializeSyncSettings();
        }

        /// <summary>
        /// Инициализирует блок синхронизации с файлом ibases.v8i: заполняет список
        /// режимов, подставляет сохранённый путь и обновляет состояние элементов управления.
        /// </summary>
        private void InitializeSyncSettings()
        {
            _syncMode = _viewModel.IbasesSyncMode;
            _syncFilePath = _viewModel.IbasesSyncFilePath;
            _syncTrigger = _viewModel.IbasesSyncTrigger;
            _syncIntervalMinutes = _viewModel.IbasesSyncIntervalMinutes;
            _syncScheduleTime = _viewModel.IbasesSyncScheduleTime;

            SyncModeComboBox.Items.Add("Отключена");
            SyncModeComboBox.Items.Add("Только загрузка (из файла в приложение)");
            SyncModeComboBox.Items.Add("Только выгрузка (из приложения в файл)");
            SyncModeComboBox.Items.Add("Двусторонняя (загрузка и выгрузка)");
            SyncModeComboBox.SelectedIndex = (int)_syncMode;

            SyncTriggerComboBox.Items.Add("Только при запуске приложения");
            SyncTriggerComboBox.Items.Add("Через заданный интервал");
            SyncTriggerComboBox.Items.Add("По расписанию");
            SyncTriggerComboBox.SelectedIndex = (int)_syncTrigger;

            SyncFilePathTextBox.Text = _syncFilePath;
            SyncIntervalTextBox.Text = _syncIntervalMinutes.ToString();
            SyncScheduleTimePicker.Text = _syncScheduleTime;

            UpdateSyncControls();
        }

        /// <summary>
        /// Обновляет видимость/доступность элементов управления блока синхронизации
        /// в зависимости от выбранного режима и пути к файлу.
        /// </summary>
        private void UpdateSyncControls()
        {
            var enabled = _syncMode != IbasesSyncMode.None;
            SyncFilePathTextBox.IsEnabled = enabled;
            BrowseSyncFileButton.IsEnabled = enabled;

            // Элементы настройки момента автоматической синхронизации.
            SyncTriggerComboBox.IsEnabled = enabled;
            var trigger = SyncTriggerComboBox.SelectedIndex;
            var isInterval = enabled && trigger == (int)IbasesSyncTrigger.Interval;
            var isSchedule = enabled && trigger == (int)IbasesSyncTrigger.Schedule;
            SyncIntervalTextBox.IsEnabled = isInterval;
            SyncIntervalLabel.IsEnabled = isInterval;
            SyncScheduleTimePicker.IsEnabled = isSchedule;
            SyncScheduleLabel.IsEnabled = isSchedule;

            // Кнопка «Загрузить» доступна в режимах с импортом, «Выгрузить» — с экспортом.
            SyncImportButton.IsEnabled = enabled &&
                (_syncMode == IbasesSyncMode.Import || _syncMode == IbasesSyncMode.Both);
            SyncExportButton.IsEnabled = enabled &&
                (_syncMode == IbasesSyncMode.Export || _syncMode == IbasesSyncMode.Both);

            // Текстовый статус.
            var path = ResolveDisplayPath();
            if (!enabled)
            {
                SyncStatusText.Text = "Синхронизация отключена.";
            }
            else if (string.IsNullOrWhiteSpace(path))
            {
                SyncStatusText.Text = "Файл ibases.v8i не найден. Укажите путь вручную.";
            }
            else
            {
                var modeText = _syncMode switch
                {
                    IbasesSyncMode.Import => "только загрузка",
                    IbasesSyncMode.Export => "только выгрузка",
                    _ => "двусторонняя"
                };
                var triggerText = _syncTrigger switch
                {
                    IbasesSyncTrigger.Interval => $"автоматически каждые {_syncIntervalMinutes} мин.",
                    IbasesSyncTrigger.Schedule => $"автоматически по расписанию в {_syncScheduleTime}.",
                    _ => "автоматически при запуске."
                };
                SyncStatusText.Text = $"Файл: {path}\nРежим: {modeText}. Запуск: {triggerText}";
            }
        }

        /// <summary>
        /// Возвращает путь к файлу ibases.v8i для отображения: пользовательский путь
        /// или стандартный путь 1С, если пользовательский не задан.
        /// </summary>
        private string? ResolveDisplayPath()
        {
            if (!string.IsNullOrWhiteSpace(_syncFilePath))
                return _syncFilePath;

            return IbasesV8iImporter.FindDefaultPath();
        }

        private void OnSyncMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SyncModeComboBox.SelectedIndex < 0)
                return;

            _syncMode = (IbasesSyncMode)SyncModeComboBox.SelectedIndex;
            UpdateSyncControls();
        }

        private void OnSyncTrigger_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SyncTriggerComboBox.SelectedIndex < 0)
                return;

            _syncTrigger = (IbasesSyncTrigger)SyncTriggerComboBox.SelectedIndex;
            UpdateSyncControls();
        }

        private void OnSyncInterval_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (int.TryParse(SyncIntervalTextBox.Text, out var minutes) && minutes > 0)
            {
                _syncIntervalMinutes = minutes;
                UpdateSyncControls();
            }
        }

        private void OnSyncScheduleTime_TextChanged(object sender, TextChangedEventArgs e)
        {
            var value = SyncScheduleTimePicker.Text?.Trim() ?? string.Empty;
            if (TimeSpan.TryParse(value, out _))
            {
                _syncScheduleTime = value;
                UpdateSyncControls();
            }
        }

        private void OnBrowseSyncFile_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Выберите файл списка баз 1С (ibases.v8i)",
                Filter = "Файл списка баз 1С (*.v8i)|*.v8i|Все файлы (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };

            if (dialog.ShowDialog() == true)
            {
                _syncFilePath = dialog.FileName;
                SyncFilePathTextBox.Text = _syncFilePath;
                UpdateSyncControls();
            }
        }

        private void OnSyncImport_Click(object sender, RoutedEventArgs e)
        {
            var filePath = ResolveDisplayPath();
            if (filePath is null || !System.IO.File.Exists(filePath))
            {
                MessageBox.Show(
                    "Файл ibases.v8i не найден. Укажите путь к файлу вручную.",
                    "Импорт из ibases.v8i",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            // Используем готовый метод ViewModel, который выполняет импорт,
            // обновляет представление и сохраняет данные.
            var ok = _viewModel.ImportFromIbases();
            if (ok)
            {
                MessageBox.Show("Импорт из файла ibases.v8i выполнен успешно.",
                    "Импорт из ibases.v8i", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show("Не удалось выполнить импорт. Проверьте, что файл ibases.v8i существует и доступен.",
                    "Ошибка импорта", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            RefreshGroupsAfterDataChange();
        }

        private void OnSyncExport_Click(object sender, RoutedEventArgs e)
        {
            var filePath = ResolveDisplayPath();
            if (filePath is null)
            {
                MessageBox.Show(
                    "Не удалось определить путь к файлу ibases.v8i. Укажите путь вручную.",
                    "Экспорт в ibases.v8i",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            try
            {
                IbasesV8iExporter.Export(filePath, _viewModel.Infobases, _viewModel.Groups);
                MessageBox.Show("Экспорт в файл ibases.v8i выполнен успешно.",
                    "Экспорт в ibases.v8i", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Не удалось выполнить экспорт.\n{ex.Message}",
                    "Ошибка экспорта", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Список установленных версий платформы 1С.
        /// </summary>
        public List<string> Result => _installedPlatformVersions;

        /// <summary>
        /// Обновляет список установленных версий платформы, сканируя каталоги 1С.
        /// </summary>
        private void OnRefreshPlatforms_Click(object sender, RoutedEventArgs e)
        {
            _installedPlatformVersions = PlatformVersionService.FindInstalledVersions();
            _viewModel.SetInstalledPlatformVersions(_installedPlatformVersions);
            UpdatePlatformsDisplay();
        }

        /// <summary>
        /// Обновляет отображение списка установленных версий платформы,
        /// группируя их по мажорной версии (например, «8.3.27»).
        /// </summary>
        private void UpdatePlatformsDisplay()
        {
            PlatformsTree.Items.Clear();

            if (_installedPlatformVersions.Count == 0)
            {
                StatusText.Text = "Версии платформы 1С не найдены. Нажмите «Обновить список».";
                return;
            }

            // Группируем версии по первым трём компонентам (мажорная версия).
            var groups = _installedPlatformVersions
                .GroupBy(GetMajorVersion)
                .OrderByDescending(g => g.Key, new VersionComparer())
                .Select(g => new PlatformVersionGroup
                {
                    Name = g.Key,
                    Versions = g.OrderByDescending(v => v, new VersionComparer()).ToList()
                })
                .ToList();

            foreach (var group in groups)
            {
                PlatformsTree.Items.Add(group);
            }

            StatusText.Text = $"Найдено версий: {_installedPlatformVersions.Count}";
        }

        /// <summary>
        /// Возвращает мажорную версию (первые три компонента) из варианта платформы.
        /// Например, для «8.3.27.1234 (64)» вернёт «8.3.27».
        /// </summary>
        private static string GetMajorVersion(string variant)
        {
            PlatformVersionService.ParseVariant(variant, out var version, out _);
            var parts = version.Split('.');
            return parts.Length >= 3
                ? string.Join(".", parts.Take(3))
                : version;
        }

        /// <summary>
        /// Перестраивает дерево групп из плоского списка.
        /// </summary>
        private void RebuildTree()
        {
            GroupsTree.ItemsSource = GroupNodeViewModel.BuildTree(_groups);
        }

        /// <summary>
        /// Возвращает выбранный узел дерева групп.
        /// </summary>
        private GroupNodeViewModel? SelectedNode =>
            GroupsTree.SelectedItem as GroupNodeViewModel;

        private void OnAddRoot_Click(object sender, RoutedEventArgs e)
        {
            // Новая корневая группа.
            var dialog = new GroupEditWindow(_groups, parent: null)
            {
                Owner = this
            };
            if (dialog.ShowDialog() == true)
            {
                _groups.Add(dialog.Result);
                RebuildTree();
                SelectGroup(dialog.Result);
            }
        }

        private void OnAddSubgroup_Click(object sender, RoutedEventArgs e)
        {
            // Новая подгруппа внутри выбранной группы.
            var parent = SelectedNode?.Group;
            if (parent is null)
            {
                MessageBox.Show(
                    "Выберите группу, внутри которой нужно создать подгруппу.",
                    "Внимание",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var dialog = new GroupEditWindow(_groups, parent)
            {
                Owner = this
            };
            if (dialog.ShowDialog() == true)
            {
                _groups.Add(dialog.Result);
                RebuildTree();
                SelectGroup(dialog.Result);
            }
        }

        private void OnEditGroup_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedNode?.Group is not Group group)
                return;

            var dialog = new GroupEditWindow(_groups, group.ParentId, group)
            {
                Owner = this
            };
            if (dialog.ShowDialog() == true)
            {
                // Обновляем группу в коллекции, сохраняя ссылку на объект,
                // чтобы не потерять ParentId у дочерних групп.
                var index = _groups.IndexOf(group);
                if (index >= 0)
                {
                    _groups[index] = dialog.Result;
                }

                RebuildTree();
                SelectGroup(dialog.Result);
            }
        }

        private void OnDeleteGroup_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedNode?.Group is not Group group)
                return;

            var subgroupCount = _groups.Count(g =>
                string.Equals(g.ParentId, group.Id, StringComparison.OrdinalIgnoreCase));

            var message = $"Удалить группу «{group.Name}»?";
            if (subgroupCount > 0)
            {
                message += $"\n\nВнутри группы находится подгрупп: {subgroupCount}.\n" +
                           "Они также будут удалены.";
            }

            var result = MessageBox.Show(
                message,
                "Подтверждение удаления",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
                return;

            // Собираем все удаляемые группы (саму группу и всех потомков).
            var toRemove = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { group.Id };
            CollectDescendants(group.Id, toRemove);

            for (var i = _groups.Count - 1; i >= 0; i--)
            {
                if (toRemove.Contains(_groups[i].Id))
                {
                    _groups.RemoveAt(i);
                }
            }
            RebuildTree();
        }

        /// <summary>
        /// Собирает идентификаторы всех групп-потомков указанной группы.
        /// </summary>
        private void CollectDescendants(string parentId, ISet<string> result)
        {
            var children = _groups
                .Where(g => string.Equals(g.ParentId, parentId, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var child in children)
            {
                if (result.Add(child.Id))
                {
                    CollectDescendants(child.Id, result);
                }
            }
        }

        private void OnCollapseAll_Click(object sender, RoutedEventArgs e)
        {
            SetAllExpanded(collapse: true);
        }

        private void OnExpandAll_Click(object sender, RoutedEventArgs e)
        {
            SetAllExpanded(collapse: false);
        }

        /// <summary>
        /// Сворачивает или разворачивает все узлы дерева групп.
        /// </summary>
        private void SetAllExpanded(bool collapse)
        {
            SetExpandedRecursive(GroupsTree.Items.OfType<GroupNodeViewModel>(), collapse);
        }

        private static void SetExpandedRecursive(IEnumerable<GroupNodeViewModel> nodes, bool collapse)
        {
            foreach (var node in nodes)
            {
                node.IsExpanded = !collapse;
                SetExpandedRecursive(node.Children, collapse);
            }
        }

        /// <summary>
        /// Выбирает узел с указанной группой в дереве (разворачивая предков).
        /// </summary>
        private void SelectGroup(Group group)
        {
            foreach (var root in GroupsTree.Items.OfType<GroupNodeViewModel>())
            {
                if (SelectInContainer(GroupsTree, root, group))
                    return;
            }
        }

        /// <summary>
        /// Рекурсивно находит и выбирает узел с указанной группой.
        /// </summary>
        private static bool SelectInContainer(ItemsControl container, GroupNodeViewModel node, Group target)
        {
            // Разворачиваем узел, чтобы его потомки стали доступны.
            node.IsExpanded = true;

            if (node.Group is not null &&
                string.Equals(node.Group.Id, target.Id, StringComparison.OrdinalIgnoreCase))
            {
                var item = container.ItemContainerGenerator.ContainerFromItem(node) as TreeViewItem;
                if (item is not null)
                {
                    item.IsSelected = true;
                    item.BringIntoView();
                }
                return true;
            }

            foreach (var child in node.Children)
            {
                // Получаем контейнер текущего узла для доступа к его ItemsControl.
                var childContainer = container.ItemContainerGenerator.ContainerFromItem(node) as ItemsControl;
                if (childContainer is not null && SelectInContainer(childContainer, child, target))
                {
                    return true;
                }
            }

            return false;
        }

        private void OnExportInfobases_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.ExportInfobasesCommand.Execute(null);
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
        private void RefreshGroupsAfterDataChange()
        {
            _groups.Clear();
            foreach (var group in _viewModel.Groups)
            {
                _groups.Add(group);
            }
            RebuildTree();
        }

        private void OnSave_Click(object sender, RoutedEventArgs e)
        {
            // Сохраняем изменения групп и версий платформы в модели представления.
            _viewModel.ApplyGroupChanges(_groups);
            _viewModel.SetInstalledPlatformVersions(_installedPlatformVersions);

            // Сохраняем настройки синхронизации с файлом ibases.v8i.
            var filePath = SyncFilePathTextBox.Text?.Trim() ?? string.Empty;
            if (!int.TryParse(SyncIntervalTextBox.Text, out var interval) || interval <= 0)
            {
                interval = 30;
            }
            var scheduleTime = SyncScheduleTimePicker.Text?.Trim() ?? string.Empty;
            _viewModel.ApplyIbasesSyncSettings(_syncMode, filePath, _syncTrigger, interval, scheduleTime);

            DialogResult = true;
        }

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