#if LINUX
using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Controls.Primitives;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Configuration_Management.Controls;
using Configuration_Management.Localization;
using Configuration_Management.Models;
using Configuration_Management.Services;
using Configuration_Management.ViewModels;

namespace Configuration_Management
{
    /// <summary>
    /// Диалог настройки подключения к информационной базе. Avalonia/Linux-версия
    /// WPF-окна <see cref="ConnectionSettingsWindow"/>. Привязывается к
    /// <see cref="ConnectionSettingsViewModel"/>.
    /// </summary>
    public class ConnectionSettingsWindow : ModalWindowBase
    {
        private readonly ConnectionSettingsViewModel _viewModel;
        private readonly IDialogService _dialogs;

        private readonly PasswordBox _passwordBox = new();
        private readonly PasswordBox _repositoryPasswordBox = new();
        private readonly PasswordBox _configuratorPasswordBox = new();

        private bool _isSyncingPassword;
        private bool _isSyncingRepositoryPassword;
        private bool _isSyncingConfiguratorPassword;

        /// <summary>
        /// Создаёт диалог настройки подключения.
        /// </summary>
        /// <param name="infobase">База для редактирования. Если null — создаётся новая база.</param>
        /// <param name="groups">Список доступных групп для выбора.</param>
        /// <param name="installedPlatformVersions">Список установленных версий платформы 1С.</param>
        /// <param name="defaultGroupPath">Путь группы по умолчанию для новой базы.</param>
        /// <param name="availableServers">Список серверов 1С из других баз списка.</param>
        /// <param name="availablePorts">Список портов серверов 1С из других баз списка.</param>
        public ConnectionSettingsWindow(Infobase? infobase = null, IEnumerable<Group>? groups = null,
            IEnumerable<string>? installedPlatformVersions = null, string? defaultGroupPath = null,
            IEnumerable<string>? availableServers = null, IEnumerable<int>? availablePorts = null)
        {
            Title = LocalizationManager.T("ConnectionSettings.Title");
            Width = 720;
            Height = 620;
            MinWidth = 620;
            MinHeight = 520;

            _dialogs = AppServices.GetRequiredService<IDialogService>();

            _viewModel = new ConnectionSettingsViewModel(groups);
            _viewModel.SetInstalledPlatformVersions(installedPlatformVersions ?? new List<string>());
            _viewModel.SetAvailableServers(availableServers);
            _viewModel.SetAvailablePorts(availablePorts);
            if (infobase != null)
            {
                _viewModel.LoadFrom(infobase);
                Result.IsFavorite = infobase.IsFavorite;
                Result.IsPinned = infobase.IsPinned;
                Result.Tags = new List<string>(infobase.Tags);
                Result.LastLaunchDate = infobase.LastLaunchDate;
                Result.MetadataRoot = infobase.MetadataRoot;
            }
            else if (!string.IsNullOrWhiteSpace(defaultGroupPath))
            {
                _viewModel.Group = defaultGroupPath;
                _viewModel.SelectedGroup = GroupHierarchyHelper.FindByFullPath(defaultGroupPath, _viewModel.Groups);
            }

            DataContext = _viewModel;
            _viewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ConnectionSettingsViewModel.Password))
                    SyncPasswordBoxFromViewModel();
                else if (e.PropertyName == nameof(ConnectionSettingsViewModel.RepositoryPassword))
                    SyncRepositoryPasswordBoxFromViewModel();
                else if (e.PropertyName == nameof(ConnectionSettingsViewModel.ConfiguratorPassword))
                    SyncConfiguratorPasswordBoxFromViewModel();
            };

            Content = BuildRoot();

            Opened += (_, _) =>
            {
                SyncPasswordBoxFromViewModel();
                SyncRepositoryPasswordBoxFromViewModel();
                SyncConfiguratorPasswordBoxFromViewModel();
            };
        }

        /// <summary>Возвращает отредактированную информационную базу.</summary>
        public Infobase Result { get; private set; } = new();

        // ===================== Вспомогательные построители =====================

        private static TextBox Tb(string path, string? watermark = null, bool multiline = false)
        {
            var tb = new TextBox
            {
                Padding = new Thickness(8, 5),
                Watermark = watermark,
                AcceptsReturn = multiline
            };
            if (multiline)
                tb.MinHeight = 70;
            tb.Bind(TextBox.TextProperty, new Binding(path) { Mode = BindingMode.TwoWay });
            return tb;
        }

        private static ComboBox EditableCombo(string textPath, string itemsPath)
        {
            var combo = new ComboBox { IsEditable = true };
            combo.Bind(ComboBox.TextProperty, new Binding(textPath) { Mode = BindingMode.TwoWay });
            combo.Bind(ComboBox.ItemsSourceProperty, new Binding(itemsPath));
            return combo;
        }

        private static Grid Field(string label, Control control, bool isEnabled = true)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(160)));
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));

            var labelBlock = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(labelBlock, 0);
            grid.Children.Add(labelBlock);

            control.IsEnabled = isEnabled;
            Grid.SetColumn(control, 1);
            grid.Children.Add(control);
            return grid;
        }

        private static StackPanel RadioGroup(params RadioButton[] radios)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, VerticalAlignment = VerticalAlignment.Center };
            foreach (var r in radios)
                panel.Children.Add(r);
            return panel;
        }

        private static RadioButton Radio(string groupName, string path, string content)
        {
            var r = new RadioButton { Content = content, GroupName = groupName };
            r.Bind(Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty,
                new Binding(path) { Mode = BindingMode.TwoWay });
            return r;
        }

        private Control BuildRoot()
        {
            var grid = new Grid { Margin = new Thickness(16) };
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            grid.RowDefinitions.Add(new RowDefinition(new GridLength(1, GridUnitType.Star)));
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            grid.Children.Add(BuildHeader());
            Grid.SetRow(grid.Children[^1], 0);

            var tabs = BuildTabs();
            Grid.SetRow(tabs, 1);
            grid.Children.Add(tabs);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8,
                Margin = new Thickness(0, 12, 0, 0)
            };
            var cancel = new Button { Content = LocalizationManager.T("Common.Cancel"), MinWidth = 100, IsCancel = true };
            cancel.Click += (_, _) => Close();
            buttons.Children.Add(cancel);
            var save = new Button { Content = LocalizationManager.T("Common.Save"), MinWidth = 120, IsDefault = true };
            save.Click += (_, _) => OnSave_Click();
            buttons.Children.Add(save);
            Grid.SetRow(buttons, 2);
            grid.Children.Add(buttons);

            return grid;
        }

        private Control BuildHeader()
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 12), Spacing = 10 };

            // Наименование
            panel.Children.Add(Field(LocalizationManager.T("Connection.NameLabel"), Tb("Name")));

            // Группа
            var groupRow = new Grid();
            groupRow.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(160)));
            groupRow.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            groupRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            var groupLabel = new TextBlock { Text = LocalizationManager.T("Connection.GroupLabel"), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(groupLabel, 0);
            groupRow.Children.Add(groupLabel);
            var groupText = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            groupText.Bind(TextBlock.TextProperty, new Binding("GroupDisplayPath"));
            Grid.SetColumn(groupText, 1);
            groupRow.Children.Add(groupText);
            var selectGroup = new Button { Content = LocalizationManager.T("Connection.ChooseGroup"), MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
            selectGroup.Click += (_, _) => OnSelectGroup_Click();
            Grid.SetColumn(selectGroup, 2);
            groupRow.Children.Add(selectGroup);
            panel.Children.Add(groupRow);

            // Описание
            panel.Children.Add(Field(LocalizationManager.T("Connection.DescriptionLabel"), Tb("Description", null, true)));

            // ID
            var idRow = new Grid();
            idRow.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(160)));
            idRow.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            idRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            idRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            var idLabel = new TextBlock { Text = LocalizationManager.T("ConnectionSettings.IdLabel"), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(idLabel, 0);
            idRow.Children.Add(idLabel);
            var idText = new TextBox { Padding = new Thickness(8, 5), IsReadOnly = true };
            idText.Bind(TextBox.TextProperty, new Binding("Id") { Mode = BindingMode.TwoWay });
            Grid.SetColumn(idText, 1);
            idRow.Children.Add(idText);
            var copyId = new Button { Content = LocalizationManager.T("Connection.CopyId"), MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
            copyId.Click += (_, _) => OnCopyId_Click();
            Grid.SetColumn(copyId, 2);
            idRow.Children.Add(copyId);
            var genId = new Button { Content = LocalizationManager.T("Connection.GenerateId"), MinWidth = 110, Margin = new Thickness(8, 0, 0, 0) };
            genId.Click += (_, _) => _viewModel.Id = Guid.NewGuid().ToString("D");
            Grid.SetColumn(genId, 3);
            idRow.Children.Add(genId);
            panel.Children.Add(idRow);

            // Строка подключения
            var csRow = new Grid();
            csRow.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(160)));
            csRow.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            csRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            var csLabel = new TextBlock { Text = LocalizationManager.T("ConnectionSettings.ConnectionStringLabel"), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(csLabel, 0);
            csRow.Children.Add(csLabel);
            var csText = new TextBox { Padding = new Thickness(8, 5), AcceptsReturn = true, MinHeight = 60 };
            csText.Bind(TextBox.TextProperty, new Binding("ConnectionString") { Mode = BindingMode.TwoWay });
            Grid.SetColumn(csText, 1);
            csRow.Children.Add(csText);
            var pasteCs = new Button { Content = LocalizationManager.T("ConnectionSettings.PasteButton"), MinWidth = 90, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
            pasteCs.Click += (_, _) => OnPasteConnectionString_Click();
            Grid.SetColumn(pasteCs, 2);
            csRow.Children.Add(pasteCs);
            panel.Children.Add(csRow);

            return panel;
        }

        private TabControl BuildTabs()
        {
            var tabs = new TabControl();

            // ===== Подключение =====
            var conn = new StackPanel();

            var connType = RadioGroup(
                Radio("ConnectionType", "IsClientServer", LocalizationManager.T("Connection.TypeServer")),
                Radio("ConnectionType", "IsFile", LocalizationManager.T("Connection.TypeFile")),
                Radio("ConnectionType", "IsWebServer", LocalizationManager.T("Connection.TypeWeb")));
            conn.Children.Add(Field(LocalizationManager.T("ConnectionSettings.ConnectionTypeLabel"), connType));

            conn.Children.Add(Field(LocalizationManager.T("Connection.ServerLabel"), Tb("Server", LocalizationManager.T("ConnectionSettings.ServerWatermark"))));
            conn.Children.Add(Field(LocalizationManager.T("Connection.DatabaseNameLabel"), Tb("DatabaseName", LocalizationManager.T("ConnectionSettings.DatabaseNameWatermark"))));
            conn.Children.Add(Field(LocalizationManager.T("Connection.PortLabel"), EditableCombo("PortText", "AvailablePorts")));

            var filePathRow = new Grid();
            filePathRow.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(160)));
            filePathRow.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            filePathRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            var fileLabel = new TextBlock { Text = LocalizationManager.T("CreateInfobase.DirLabel"), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(fileLabel, 0);
            filePathRow.Children.Add(fileLabel);
            var fileText = Tb("FilePath", LocalizationManager.T("ConnectionSettings.FilePathWatermark"));
            Grid.SetColumn(fileText, 1);
            filePathRow.Children.Add(fileText);
            var browse = new Button { Content = LocalizationManager.T("Common.Browse"), MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
            browse.Click += (_, _) => OnBrowseFilePath_Click();
            Grid.SetColumn(browse, 2);
            filePathRow.Children.Add(browse);
            conn.Children.Add(Field("", filePathRow));

            conn.Children.Add(Field(LocalizationManager.T("Connection.WebUrlLabel"), Tb("WebUrl", "https://…")));

            // Аутентификация
            var auth = RadioGroup(
                Radio("Auth", "IsAuthPrompt", LocalizationManager.T("ConnectionSettings.AuthPromptShort")),
                Radio("Auth", "IsAuthCredentials", LocalizationManager.T("ConnectionSettings.AuthAutoShort")),
                Radio("Auth", "IsAuthWindows", LocalizationManager.T("ConnectionSettings.AuthOsShort")));
            conn.Children.Add(Field(LocalizationManager.T("ConnectionSettings.AuthLabel"), auth));
            conn.Children.Add(Field(LocalizationManager.T("Connection.UserLabel"), Tb("User")));
            _passwordBox.PasswordChanged += (_, _) =>
            {
                if (_isSyncingPassword) return;
                _viewModel.Password = _passwordBox.Password;
            };
            conn.Children.Add(Field(LocalizationManager.T("Connection.PasswordLabel"), _passwordBox));

            tabs.Items.Add(new TabItem { Header = LocalizationManager.T("Connection.Tab.Connection"), Content = new ScrollViewer { Content = conn, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });

            // ===== Платформа и запуск =====
            var platform = new StackPanel();

            var platRow = new Grid();
            platRow.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(160)));
            platRow.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            platRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            var platLabel = new TextBlock { Text = LocalizationManager.T("Connection.VersionLabel"), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(platLabel, 0);
            platform.Children.Add(platRow);
            platRow.Children.Add(platLabel);
            var platCombo = new ComboBox();
            platCombo.Bind(ComboBox.ItemsSourceProperty, new Binding("InstalledPlatformVersions"));
            platCombo.Bind(ComboBox.SelectedItemProperty, new Binding("PlatformVersion") { Mode = BindingMode.TwoWay });
            Grid.SetColumn(platCombo, 1);
            platRow.Children.Add(platCombo);
            var platBtn = new Button { Content = LocalizationManager.T("Connection.ChoosePlatform"), MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
            platBtn.Click += (_, _) => OnPlatformSettings_Click();
            Grid.SetColumn(platBtn, 2);
            platRow.Children.Add(platBtn);

            platform.Children.Add(Field(LocalizationManager.T("ConnectionSettings.ArchLabel"), RadioGroup(
                Radio("Arch", "IsArchitecture32", "32"),
                Radio("Arch", "IsArchitecture64", "64"),
                Radio("Arch", "IsArchitecture32Priority", LocalizationManager.T("ConnectionSettings.Arch32PriorityShort")),
                Radio("Arch", "IsArchitecture64Priority", LocalizationManager.T("ConnectionSettings.Arch64PriorityShort")))));

            platform.Children.Add(Field(LocalizationManager.T("ConnectionSettings.LaunchModeLabel"), RadioGroup(
                Radio("LaunchMode", "IsAutoMode", LocalizationManager.T("Main.SessionClientAuto")),
                Radio("LaunchMode", "IsThinClient", LocalizationManager.T("Main.SessionClientThin")),
                Radio("LaunchMode", "IsThickClient", LocalizationManager.T("ConnectionSettings.LaunchThickShort")),
                Radio("LaunchMode", "IsThickOrdinaryClient", LocalizationManager.T("ConnectionSettings.LaunchThickOrdinaryShort")),
                Radio("LaunchMode", "IsWebClient", LocalizationManager.T("Connection.LaunchWeb")))));

            var launchRow = new Grid();
            launchRow.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(160)));
            launchRow.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            launchRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            var launchLabel = new TextBlock { Text = LocalizationManager.T("ConnectionSettings.LaunchParametersLabel"), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(launchLabel, 0);
            launchRow.Children.Add(launchLabel);
            var launchText = new TextBox { Padding = new Thickness(8, 5), AcceptsReturn = true, MinHeight = 60 };
            launchText.Bind(TextBox.TextProperty, new Binding("LaunchParameters") { Mode = BindingMode.TwoWay });
            Grid.SetColumn(launchText, 1);
            launchRow.Children.Add(launchText);
            var launchBtn = new Button { Content = LocalizationManager.T("ConnectionSettings.ConfigureButton"), MinWidth = 100, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
            launchBtn.Click += (_, _) => OnLaunchParameters_Click();
            Grid.SetColumn(launchBtn, 2);
            launchRow.Children.Add(launchBtn);
            platform.Children.Add(Field("", launchRow));

            platform.Children.Add(Field(LocalizationManager.T("Connection.ConfigurationLabel"), Tb("ConfigurationName")));
            platform.Children.Add(Field(LocalizationManager.T("ConnectionSettings.ConfigurationVersionLabel"), Tb("ConfigurationVersion")));

            tabs.Items.Add(new TabItem { Header = LocalizationManager.T("ConnectionSettings.TabPlatformAndLaunch"), Content = new ScrollViewer { Content = platform, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });

            // ===== Репозиторий =====
            var repo = new StackPanel();
            repo.Children.Add(Field(LocalizationManager.T("Connection.RepositoryServerLabel"), Tb("RepositoryServer")));
            repo.Children.Add(Field(LocalizationManager.T("Connection.RepositoryNameLabel"), Tb("RepositoryName")));
            repo.Children.Add(Field(LocalizationManager.T("Connection.UserLabel"), Tb("RepositoryUser")));
            _repositoryPasswordBox.PasswordChanged += (_, _) =>
            {
                if (_isSyncingRepositoryPassword) return;
                _viewModel.RepositoryPassword = _repositoryPasswordBox.Password;
            };
            repo.Children.Add(Field(LocalizationManager.T("Connection.RepositoryPasswordLabel"), _repositoryPasswordBox));
            tabs.Items.Add(new TabItem { Header = LocalizationManager.T("Connection.Tab.Repository"), Content = new ScrollViewer { Content = repo, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });

            // ===== Конфигуратор =====
            var config = new StackPanel();
            config.Children.Add(Field(LocalizationManager.T("ConnectionSettings.AuthLabel"), RadioGroup(
                Radio("ConfigAuth", "IsConfiguratorAuthPrompt", LocalizationManager.T("ConnectionSettings.AuthPromptShort")),
                Radio("ConfigAuth", "IsConfiguratorAuthCredentials", LocalizationManager.T("ConnectionSettings.AuthAutoShort")),
                Radio("ConfigAuth", "IsConfiguratorAuthWindows", LocalizationManager.T("ConnectionSettings.AuthOsShort")))));
            config.Children.Add(Field(LocalizationManager.T("Connection.UserLabel"), Tb("ConfiguratorUser")));
            _configuratorPasswordBox.PasswordChanged += (_, _) =>
            {
                if (_isSyncingConfiguratorPassword) return;
                _viewModel.ConfiguratorPassword = _configuratorPasswordBox.Password;
            };
            config.Children.Add(Field(LocalizationManager.T("Connection.PasswordLabel"), _configuratorPasswordBox));
            tabs.Items.Add(new TabItem { Header = LocalizationManager.T("ConnectionSettings.TabConfigurator"), Content = new ScrollViewer { Content = config, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });

            return tabs;
        }

        // ===================== Обработчики =====================

        private void OnSelectGroup_Click()
        {
            var dialog = new GroupPickerWindow(
                _viewModel.Groups,
                currentGroupId: _viewModel.SelectedGroup?.Id,
                allowNone: true,
                noneLabel: LocalizationManager.T("Connection.NoGroup"));
            if (dialog.ShowDialogSync(this))
            {
                _viewModel.SelectedGroup = dialog.ResultGroup;
                if (dialog.ResultGroup is null)
                    _viewModel.Group = string.Empty;
            }
        }

        private void OnBrowseFilePath_Click()
        {
            var current = _viewModel.FilePath;
            var path = _dialogs.OpenFolderDialog(LocalizationManager.T("Connection.ChooseFolderTitle"),
                !string.IsNullOrWhiteSpace(current) && Directory.Exists(current) ? current : null);
            if (!string.IsNullOrWhiteSpace(path))
                _viewModel.FilePath = path;
        }

        private void OnCopyId_Click()
        {
            if (!string.IsNullOrWhiteSpace(_viewModel.Id))
            {
                try { Clipboard?.SetTextAsync(_viewModel.Id); } catch { /* ignore */ }
            }
        }

        private void OnPasteConnectionString_Click()
        {
            var dialog = new ConnectionStringInputWindow(_viewModel.ConnectionString);
            if (!dialog.ShowDialogSync(this))
                return;

            _viewModel.ApplyConnectionString(dialog.Result);
            _viewModel.ConnectionString = dialog.Result ?? string.Empty;
            _dialogs.ShowInfo(LocalizationManager.T("Connection.PasteSuccess"), LocalizationManager.T("Connection.PasteSuccessTitle"));
        }

        private void OnLaunchParameters_Click()
        {
            var dialog = new LaunchParametersWindow(_viewModel.LaunchParameters);
            if (dialog.ShowDialogSync(this))
            {
                _viewModel.LaunchParameters = dialog.Result;
            }
        }

        private void OnPlatformSettings_Click()
        {
            var current = _viewModel.PlatformVersion ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(_viewModel.Architecture)
                && _viewModel.Architecture is "32" or "64"
                && !current.Contains('('))
            {
                current = $"{current} ({_viewModel.Architecture})".Trim();
            }

            var dialog = new PlatformVersionPickerWindow(_viewModel.InstalledPlatformVersions, current);
            if (!dialog.ShowDialogSync(this))
                return;

            var result = (dialog.Result ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(result))
                return;

            PlatformVersionService.ParseVariant(result, out var version, out var architecture);
            _viewModel.PlatformVersion = string.IsNullOrWhiteSpace(version) ? result : version;
            if (result.Contains('(') && (architecture == "32" || architecture == "64"))
                _viewModel.Architecture = architecture;
        }

        private void OnSave_Click()
        {
            _viewModel.ApplyTo(Result);

            if (string.IsNullOrWhiteSpace(Result.Id))
            {
                var id = IbasesV8iImporter.FindId(Result.Name, Result.Connection.ToConnectionString());
                if (!string.IsNullOrWhiteSpace(id))
                {
                    Result.Id = id;
                }
                else
                {
                    Result.Id = Guid.NewGuid().ToString("D");
                }
            }

            DialogResult = true;
            Close();
        }

        // ===================== Синхронизация PasswordBox =====================

        private void SyncPasswordBoxFromViewModel()
        {
            _isSyncingPassword = true;
            try
            {
                if (_passwordBox.Password != (_viewModel.Password ?? string.Empty))
                    _passwordBox.Password = _viewModel.Password ?? string.Empty;
            }
            finally
            {
                _isSyncingPassword = false;
            }
        }

        private void SyncRepositoryPasswordBoxFromViewModel()
        {
            _isSyncingRepositoryPassword = true;
            try
            {
                if (_repositoryPasswordBox.Password != (_viewModel.RepositoryPassword ?? string.Empty))
                    _repositoryPasswordBox.Password = _viewModel.RepositoryPassword ?? string.Empty;
            }
            finally
            {
                _isSyncingRepositoryPassword = false;
            }
        }

        private void SyncConfiguratorPasswordBoxFromViewModel()
        {
            _isSyncingConfiguratorPassword = true;
            try
            {
                if (_configuratorPasswordBox.Password != (_viewModel.ConfiguratorPassword ?? string.Empty))
                    _configuratorPasswordBox.Password = _viewModel.ConfiguratorPassword ?? string.Empty;
            }
            finally
            {
                _isSyncingConfiguratorPassword = false;
            }
        }
    }
}
#endif