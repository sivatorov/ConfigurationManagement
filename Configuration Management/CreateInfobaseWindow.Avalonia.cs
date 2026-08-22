#if LINUX
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Configuration_Management.Localization;
using Configuration_Management.Models;
using Configuration_Management.Services;

namespace Configuration_Management
{
    /// <summary>
    /// Диалог создания ИБ через CREATEINFOBASE (пустая или из шаблона .cf/.dt).
    /// Avalonia/Linux-версия WPF-окна <see cref="CreateInfobaseWindow"/>.
    /// </summary>
    public class CreateInfobaseWindow : ModalWindowBase
    {
        private readonly bool _fromTemplate;
        private readonly IReadOnlyList<string> _platformVersions;
        private readonly IReadOnlyList<Group> _groups;
        private string _selectedGroupPath;
        private readonly IDialogService _dialogs;

        private readonly TextBox _nameBox = new() { Padding = new Thickness(8, 5) };
        private readonly TextBlock _groupPathBox = new() { VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBox _platformBox = new() { Padding = new Thickness(8, 5) };
        private readonly RadioButton _fileTypeRadio = new() { Content = LocalizationManager.T("CreateInfobase.FileType"), GroupName = "CreateType" };
        private readonly RadioButton _serverTypeRadio = new() { Content = LocalizationManager.T("CreateInfobase.ServerType"), GroupName = "CreateType", IsChecked = true };
        private readonly TextBox _filePathBox = new() { Padding = new Thickness(8, 5) };
        private readonly TextBox _serverBox = new() { Padding = new Thickness(8, 5) };
        private readonly TextBox _refBox = new() { Padding = new Thickness(8, 5) };
        private readonly TextBox _templateBox = new() { Padding = new Thickness(8, 5) };
        private readonly StackPanel _filePanel = new() { Spacing = 8 };
        private readonly StackPanel _serverPanel = new() { Spacing = 8 };

        public Infobase? Result { get; private set; }

        public CreateInfobaseWindow(
            bool fromTemplate,
            IEnumerable<string> platformVersions,
            string defaultGroupPath = "",
            IEnumerable<Group>? groups = null)
        {
            _fromTemplate = fromTemplate;
            _platformVersions = platformVersions?.ToList() ?? new List<string>();
            _groups = groups?.ToList() ?? new List<Group>();
            _selectedGroupPath = defaultGroupPath ?? string.Empty;
            _dialogs = AppServices.GetRequiredService<IDialogService>();

            Title = fromTemplate
                ? LocalizationManager.T("CreateInfobase.TitleFromTemplate")
                : LocalizationManager.T("CreateInfobase.TitleEmpty");
            Width = 560;
            SizeToContent = SizeToContent.Height;
            CanResize = false;
            SystemDecorations = SystemDecorations.Full;

            _groupPathBox.Text = string.IsNullOrWhiteSpace(_selectedGroupPath)
                ? LocalizationManager.T("Connection.NoGroup")
                : _selectedGroupPath;

            Content = BuildRoot();
            RefreshPlatformList();
            UpdateTypePanels();
        }

        private string HintText => _fromTemplate
            ? LocalizationManager.T("CreateInfobase.HintTemplate")
            : LocalizationManager.T("CreateInfobase.HintEmpty");

        private Control BuildRoot()
        {
            var grid = new Grid { Margin = new Thickness(16) };
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            var title = new TextBlock
            {
                Text = _fromTemplate ? LocalizationManager.T("CreateInfobase.HeaderTemplate") : LocalizationManager.T("CreateInfobase.HeaderEmpty"),
                FontSize = 15,
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, 0, 0, 4)
            };
            Grid.SetRow(title, 0);
            grid.Children.Add(title);

            var hint = new TextBlock
            {
                Text = HintText,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 12)
            };
            Grid.SetRow(hint, 1);
            grid.Children.Add(hint);

            var fields = new StackPanel { Spacing = 10 };

            // Наименование
            fields.Children.Add(Field(LocalizationManager.T("Connection.NameLabel"), _nameBox));

            // Группа
            var groupRow = new Grid();
            groupRow.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(150)));
            groupRow.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            groupRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            var gl = new TextBlock { Text = LocalizationManager.T("Connection.GroupLabel"), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(gl, 0);
            groupRow.Children.Add(gl);
            Grid.SetColumn(_groupPathBox, 1);
            groupRow.Children.Add(_groupPathBox);
            var pickGroup = new Button { Content = LocalizationManager.T("Connection.ChooseGroup"), MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
            pickGroup.Click += (_, _) => OnPickGroup_Click();
            Grid.SetColumn(pickGroup, 2);
            groupRow.Children.Add(pickGroup);
            fields.Children.Add(groupRow);

            // Платформа
            var platRow = new Grid();
            platRow.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(150)));
            platRow.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            platRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            var pl = new TextBlock { Text = LocalizationManager.T("Connection.VersionLabel"), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(pl, 0);
            platRow.Children.Add(pl);
            Grid.SetColumn(_platformBox, 1);
            platRow.Children.Add(_platformBox);
            var pickPlatform = new Button { Content = LocalizationManager.T("CreateInfobase.List"), MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
            pickPlatform.Click += (_, _) => OnPickPlatform_Click();
            Grid.SetColumn(pickPlatform, 2);
            platRow.Children.Add(pickPlatform);
            fields.Children.Add(platRow);

            // Тип базы
            var typePanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
            _fileTypeRadio.IsCheckedChanged += (_, _) => UpdateTypePanels();
            _serverTypeRadio.IsCheckedChanged += (_, _) => UpdateTypePanels();
            typePanel.Children.Add(_fileTypeRadio);
            typePanel.Children.Add(_serverTypeRadio);
            fields.Children.Add(Field(LocalizationManager.T("Connection.TypeLabel"), typePanel));

            // Файловая
            var browseFile = new Button { Content = LocalizationManager.T("Common.Browse"), MinWidth = 90 };
            browseFile.Click += (_, _) => OnBrowseFolder_Click();
            var fileRow = new Grid();
            fileRow.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            fileRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            Grid.SetColumn(_filePathBox, 0);
            fileRow.Children.Add(_filePathBox);
            Grid.SetColumn(browseFile, 1);
            browseFile.Margin = new Thickness(8, 0, 0, 0);
            fileRow.Children.Add(browseFile);
            _filePanel.Children.Add(Field(LocalizationManager.T("CreateInfobase.DirLabel"), fileRow));

            // Серверная
            _serverPanel.Children.Add(Field(LocalizationManager.T("Connection.ServerLabel"), _serverBox));
            _serverPanel.Children.Add(Field(LocalizationManager.T("CreateInfobase.RefLabel"), _refBox));

            fields.Children.Add(_filePanel);
            fields.Children.Add(_serverPanel);

            // Шаблон
            if (_fromTemplate)
            {
                var browseTemplate = new Button { Content = LocalizationManager.T("CreateInfobase.File"), MinWidth = 90 };
                browseTemplate.Click += (_, _) => OnBrowseTemplate_Click();
                var tplRow = new Grid();
                tplRow.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
                tplRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
                Grid.SetColumn(_templateBox, 0);
                tplRow.Children.Add(_templateBox);
                Grid.SetColumn(browseTemplate, 1);
                browseTemplate.Margin = new Thickness(8, 0, 0, 0);
                tplRow.Children.Add(browseTemplate);
                fields.Children.Add(Field(LocalizationManager.T("CreateInfobase.TemplateLabel"), tplRow));
            }

            Grid.SetRow(fields, 2);
            grid.Children.Add(fields);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8,
                Margin = new Thickness(0, 16, 0, 0)
            };
            var cancel = new Button { Content = LocalizationManager.T("Common.Cancel"), MinWidth = 100, IsCancel = true };
            cancel.Click += (_, _) => Close();
            buttons.Children.Add(cancel);
            var create = new Button
            {
                Content = LocalizationManager.T("CreateInfobase.Create"),
                MinWidth = 120,
                IsDefault = true,
                Background = new SolidColorBrush(Color.Parse("#16A34A")),
                Foreground = Brushes.White
            };
            create.Click += (_, _) => OnCreate_Click();
            buttons.Children.Add(create);
            Grid.SetRow(buttons, 5);
            grid.Children.Add(buttons);

            return grid;
        }

        private static Grid Field(string label, Control control)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(150)));
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));

            var labelBlock = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(labelBlock, 0);
            grid.Children.Add(labelBlock);

            control.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(control, 1);
            grid.Children.Add(control);
            return grid;
        }

        private void UpdateTypePanels()
        {
            var isFile = _fileTypeRadio.IsChecked == true;
            _filePanel.IsVisible = isFile;
            _serverPanel.IsVisible = !isFile;
        }

        private void OnPickGroup_Click()
        {
            var dialog = new GroupPickerWindow(
                _groups,
                currentGroupId: null,
                allowNone: true,
                noneLabel: LocalizationManager.T("Connection.NoGroup"));
            if (dialog.ShowDialogSync(this))
            {
                _selectedGroupPath = string.IsNullOrWhiteSpace(dialog.ResultFullPath)
                    ? string.Empty
                    : dialog.ResultFullPath;
                _groupPathBox.Text = string.IsNullOrWhiteSpace(_selectedGroupPath)
                    ? LocalizationManager.T("Connection.NoGroup")
                    : _selectedGroupPath;
            }
        }

        private void RefreshPlatformList()
        {
            var extras = PlatformVersionService.GetAdditionalSearchPaths();
            var platforms = PlatformVersionService.FindInstalledVersions(extras);
            if (platforms.Count == 0)
                platforms = _platformVersions.ToList();

            if (platforms.Count > 0 && string.IsNullOrWhiteSpace(_platformBox.Text))
                _platformBox.Text = platforms[0];
        }

        private void OnPickPlatform_Click()
        {
            RefreshPlatformList();
            var extras = PlatformVersionService.GetAdditionalSearchPaths();
            var platforms = PlatformVersionService.FindInstalledVersions(extras);
            if (platforms.Count == 0)
                platforms = _platformVersions.ToList();

            var dlg = new PlatformVersionPickerWindow(platforms, _platformBox.Text ?? "");
            if (dlg.ShowDialogSync(this) && !string.IsNullOrWhiteSpace(dlg.Result))
                _platformBox.Text = dlg.Result;
        }

        private void OnBrowseFolder_Click()
        {
            var path = _dialogs.OpenFolderDialog(LocalizationManager.T("CreateInfobase.ChooseFolderDescription"));
            if (!string.IsNullOrWhiteSpace(path))
                _filePathBox.Text = path;
        }

        private void OnBrowseTemplate_Click()
        {
            var path = _dialogs.OpenFileDialog(LocalizationManager.T("CreateInfobase.TemplateDialogTitle"),
                $"{LocalizationManager.T("CreateInfobase.FilterTemplates")}|*.cf;*.dt|{LocalizationManager.T("CreateInfobase.FilterConfig")}|*.cf|{LocalizationManager.T("CreateInfobase.FilterDump")}|*.dt|{LocalizationManager.T("Common.AllFiles")}|*.*");
            if (!string.IsNullOrWhiteSpace(path))
                _templateBox.Text = path;
        }

        private void OnCreate_Click()
        {
            var name = _nameBox.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(name))
            {
                _dialogs.ShowWarning(LocalizationManager.T("CreateInfobase.EnterName"), LocalizationManager.T("CreateInfobase.CreateTitle"));
                return;
            }

            var isFile = _fileTypeRadio.IsChecked == true;
            string? filePath = null;
            string? server = null;
            string? refName = null;

            if (isFile)
            {
                filePath = _filePathBox.Text?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(filePath))
                {
                    _dialogs.ShowWarning(LocalizationManager.T("CreateInfobase.EnterFilePath"), LocalizationManager.T("CreateInfobase.CreateTitle"));
                    return;
                }
            }
            else
            {
                server = _serverBox.Text?.Trim() ?? "";
                refName = _refBox.Text?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(refName))
                {
                    _dialogs.ShowWarning(LocalizationManager.T("CreateInfobase.EnterServerAndRef"), LocalizationManager.T("CreateInfobase.CreateTitle"));
                    return;
                }
            }

            string? templatePath = null;
            if (_fromTemplate)
            {
                templatePath = _templateBox.Text?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(templatePath) || !File.Exists(templatePath))
                {
                    _dialogs.ShowWarning(LocalizationManager.T("CreateInfobase.EnterTemplateFile"), LocalizationManager.T("CreateInfobase.CreateTitle"));
                    return;
                }
            }

            var platform = _platformBox.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(platform))
            {
                _dialogs.ShowWarning(
                    LocalizationManager.T("CreateInfobase.NoPlatform"),
                    LocalizationManager.T("CreateInfobase.CreateTitle"));
                return;
            }

            var (ok, error) = OneCLauncher.CreateInfoBase(
                platformVersion: platform,
                isFile: isFile,
                filePath: filePath,
                server: server,
                databaseName: refName,
                templatePath: templatePath);

            if (!ok)
            {
                _dialogs.ShowError(string.Format(LocalizationManager.T("CreateInfobase.CreateFailed"), error ?? ""), LocalizationManager.T("CreateInfobase.CreateTitle"));
                return;
            }

            PlatformVersionService.ParseVariant(platform, out var cleanPlatform, out var platformArch);
            var storedPlatform = string.IsNullOrWhiteSpace(cleanPlatform) ? platform : cleanPlatform;
            var storedArchitecture = platformArch == "32" || platformArch == "64"
                ? platformArch
                : "32-priority";

            Result = new Infobase
            {
                Id = Guid.NewGuid().ToString(),
                Name = name,
                Group = string.IsNullOrWhiteSpace(_selectedGroupPath) ? string.Empty : _selectedGroupPath,
                PlatformVersion = storedPlatform,
                Architecture = storedArchitecture,
                Connection = isFile
                    ? new ConnectionSettings
                    {
                        Type = ConnectionType.File,
                        FilePath = filePath ?? ""
                    }
                    : new ConnectionSettings
                    {
                        Type = ConnectionType.ClientServer,
                        Server = server ?? "",
                        DatabaseName = refName ?? ""
                    }
            };

            DialogResult = true;
            Close();
        }
    }
}
#endif