#if LINUX
using System;
using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Configuration_Management.Localization;
using Configuration_Management.Models;
using Configuration_Management.Services;
using Configuration_Management.Themes;
using Configuration_Management.ViewModels;

namespace Configuration_Management
{
    /// <summary>
    /// Окно «Скачивание версии платформы 1С» (issue #330): дерево версий каталога
    /// releases.1c.ru (линии 8.3/8.5 → группы сборок → полные версии) с поиском и
    /// «свернуть/развернуть все» (комментарий 7OH от 2026-10-08), выбор варианта
    /// дистрибутива для текущей ОС, скачивание файла в выбранную папку с прогрессом.
    /// Кнопки «Запустить установщик» нет — файл скачивается архивом, после скачивания
    /// пользователь сам открывает папку. Авторизация портала — через учётную
    /// запись ИТС из справочника (#333). Вся логика — в чистой
    /// <see cref="PlatformDownloadViewModel"/>; сервисы берутся из <see cref="AppServices"/>.
    /// Avalonia/Linux-версия WPF-окна <see cref="PlatformDownloadWindow"/>.
    /// </summary>
    public sealed class PlatformDownloadWindow : ModalWindowBase
    {
        private readonly Services.IPlatformUpdateService _service = AppServices.GetRequiredService<Services.IPlatformUpdateService>();
        private readonly Services.IOneCUpdatesService _updates = AppServices.GetRequiredService<Services.IOneCUpdatesService>();
        private readonly Services.IItsAccountsStore _accounts = AppServices.GetRequiredService<Services.IItsAccountsStore>();
        private readonly Services.IDialogService _dialogs = AppServices.GetRequiredService<Services.IDialogService>();
        private readonly Services.INotificationService _notifier = AppServices.GetRequiredService<Services.INotificationService>();
        private readonly Services.IAppLogger _logger = AppServices.GetRequiredService<Services.IAppLogger>();
        private readonly Services.IInfobaseRepository _repository = AppServices.GetRequiredService<Services.IInfobaseRepository>();

        private readonly PlatformDownloadViewModel _viewModel;
        private readonly Models.AppSettings _settings;
        private TreeView? _versionsTree;
        private TextBox? _logBox;

        /// <summary>Открывает окно «Скачивание версии платформы 1С».</summary>
        public PlatformDownloadWindow()
        {
            Title = T("PlatformDownload.WindowTitle");
            Width = 980;
            Height = 700;
            MinWidth = 760;
            MinHeight = 560;
            FontSize = 13;
            CanResize = true;

            _settings = _repository.LoadSettings();
            var initialDirectory = ResolveDefaultDirectory(_settings.PlatformDownloadDirectory);

            _viewModel = new PlatformDownloadViewModel(
                _service,
                // Резолв учётной записи ИТС: выбранная в настройках, иначе «Основная» (issue #333).
                () => _accounts.Resolve(_settings.ItsAccountId),
                (url, targetPath, progress, ct) =>
                    _updates.DownloadDistributionAsync(url, targetPath, progress, ct),
                OpenDownloadedFolder,
                chooseDirectory: () => _dialogs.OpenFolderDialog(
                    T("PlatformDownload.ChooseDirectoryTitle"), initialDirectory),
                is64Bit: Environment.Is64BitOperatingSystem,
                defaultDirectory: initialDirectory,
                isWindows: false,
                notify: (title, message, kind, evt) => _notifier.Show(title, message, kind, evt),
                appLogger: _logger,
                // issue #330: заполнение списка версий и связанных свойств — в UI-потоке.
                dispatchToUi: action => Dispatcher.UIThread.Post(action));

            Content = BuildRoot();
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;

            Opened += OnOpened;
        }

        /// <summary>Показывает окно модально (синхронно).</summary>
        public bool ShowSync(Window? owner = null) => ShowDialogSync(owner);

        private static string T(string key) => LocalizationManager.T(key);

        private async void OnOpened(object? sender, EventArgs e)
        {
            if (_viewModel.VersionTree.Count == 0)
                await _viewModel.LoadCatalogAsync();
        }

        private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PlatformDownloadViewModel.LogText))
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (_logBox is not null)
                        _logBox.CaretIndex = _logBox.Text?.Length ?? 0;
                });
            }
        }

        /// <summary>Открывает папку со скачанным файлом (файловый менеджер xdg-open).</summary>
        private static bool OpenDownloadedFolder(string downloadedPath)
        {
            try
            {
                var dir = File.Exists(downloadedPath)
                    ? Path.GetDirectoryName(downloadedPath)
                    : downloadedPath;
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                    return false;

                Process.Start(new ProcessStartInfo
                {
                    FileName = "xdg-open",
                    ArgumentList = { dir },
                    UseShellExecute = false
                });
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Каталог загрузок по умолчанию: сохранённая настройка либо
        /// <c>Загрузки/1CPlatform</c> (создаётся при скачивании).</summary>
        private static string ResolveDefaultDirectory(string? saved)
        {
            if (!string.IsNullOrWhiteSpace(saved))
                return saved.Trim();

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Downloads", "1CPlatform");
        }

        private Control BuildRoot()
        {
            var grid = new Grid { Margin = new Thickness(16) };
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            grid.RowDefinitions.Add(new RowDefinition(new GridLength(1, GridUnitType.Star)));
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            var title = new TextBlock
            {
                Text = T("PlatformDownload.WindowTitle"),
                FontSize = 15,
                FontWeight = FontWeight.SemiBold
            };
            Grid.SetRow(title, 0);
            grid.Children.Add(title);

            var description = new TextBlock
            {
                Text = T("PlatformDownload.Description"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0)
            };
            ThemeBrushes.Bind(description, TextBlock.ForegroundProperty, "TextSecondaryBrush");
            Grid.SetRow(description, 1);
            grid.Children.Add(description);

            // Основная область: дерево версий слева (~25%) + параметры справа (issue #330).
            var body = new Grid { Margin = new Thickness(0, 12, 0, 0) };
            body.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(340)));
            body.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            Grid.SetRow(body, 2);
            grid.Children.Add(body);

            // Дерево версий: линии 8.3/8.5 → группы сборок → полные версии.
            var listBorder = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6)
            };
            ThemeBrushes.Bind(listBorder, Border.BackgroundProperty, "CardBackgroundColorBrush");
            ThemeBrushes.Bind(listBorder, Border.BorderBrushProperty, "BorderColorBrush");

            _versionsTree = new TreeView
            {
                ItemsSource = _viewModel.VersionTree,
                Margin = new Thickness(4)
            };
            _versionsTree.ItemTemplate = new FuncTreeDataTemplate(
                typeof(PlatformCatalogNode),
                (node, _) => new TextBlock
                {
                    Text = node is PlatformCatalogNode catalogNode ? catalogNode.Name : string.Empty,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 1)
                },
                node => node is PlatformCatalogNode catalogNode
                    ? catalogNode.Children
                    : Array.Empty<PlatformCatalogNode>());
            // issue #330: состояние раскрытия в узле (IsExpanded) — им управляют команды
            // «Развернуть/Свернуть все» и пользователь (двусторонняя привязка).
            var itemTheme = new ControlTheme(typeof(TreeViewItem));
            itemTheme.Setters.Add(new Setter(
                TreeViewItem.IsExpandedProperty,
                new Avalonia.Data.Binding(nameof(PlatformCatalogNode.IsExpanded))));
            _versionsTree.ItemContainerTheme = itemTheme;
            // null (сброс выделения при перестроении дерева поиском) игнорируется —
            // выбранные версия и файл не сбрасываются.
            _versionsTree.SelectionChanged += (_, _) =>
            {
                if (_versionsTree.SelectedItem is PlatformCatalogNode node)
                    _viewModel.SelectedVersionNode = node;
            };

            // issue #330 (комментарий 7OH): поиск по дереву + свернуть/развернуть все.
            var treeHeader = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(6, 6, 6, 0) };
            var searchBox = new TextBox
            {
                MinHeight = 28,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                FontSize = 12,
                Watermark = T("PlatformDownload.SearchVersions")
            };
            searchBox.TextChanged += (_, _) => _viewModel.VersionSearchQuery = searchBox.Text ?? string.Empty;
            ToolTip.SetTip(searchBox, T("PlatformDownload.SearchVersionsTooltip"));
            treeHeader.Children.Add(searchBox);
            // issue #330 (комментарий 7OH от 2026-10-09): кнопки свертки дерева —
            // компактные иконки (как в главном окне), поле поиска остаётся видимым.
            // Назначение поясняют подсказки (тултипы).
            treeHeader.Children.Add(MakeIconButton("IconExpandAll", T("PlatformDownload.ExpandAll"), ExecuteExpandAll));
            treeHeader.Children.Add(MakeIconButton("IconCollapseAll", T("PlatformDownload.CollapseAll"), ExecuteCollapseAll));

            var treeGrid = new Grid { RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(new GridLength(1, GridUnitType.Star)) } };
            Grid.SetRow(treeHeader, 0);
            treeGrid.Children.Add(treeHeader);
            Grid.SetRow(_versionsTree, 1);
            treeGrid.Children.Add(_versionsTree);

            listBorder.Child = treeGrid;
            Grid.SetColumn(listBorder, 0);
            body.Children.Add(listBorder);

            // Правая колонка: параметры сверху вниз.
            var right = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var rightPanel = new StackPanel { Margin = new Thickness(14, 0, 0, 0), Spacing = 10 };

            // Разрядность.
            var archPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            archPanel.Children.Add(MakeLabel(T("PlatformDownload.Architecture")));
            var archBox = new ComboBox { Width = 160, Height = 30 };
            archBox.Items.Add("x64");
            archBox.Items.Add("x86");
            archBox.SelectedIndex = _viewModel.Is64Bit ? 0 : 1;
            archBox.SelectionChanged += (_, _) => _viewModel.Is64Bit = archBox.SelectedIndex == 0;
            archPanel.Children.Add(archBox);
            rightPanel.Children.Add(archPanel);

            // Файл дистрибутива: варианты для текущей ОС (issue #330).
            var fileLabel = MakeLabel(T("PlatformDownload.File"), secondary: true);
            rightPanel.Children.Add(fileLabel);
            var fileBox = new ComboBox { Height = 30, ItemsSource = _viewModel.DistributionOptions };
            fileBox.SelectedItem = _viewModel.SelectedDistribution;
            fileBox.SelectionChanged += (_, _) =>
            {
                if (fileBox.SelectedItem is PlatformDistributionOption option)
                    _viewModel.SelectedDistribution = option;
            };
            _viewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PlatformDownloadViewModel.DistributionOptions))
                    fileBox.ItemsSource = _viewModel.DistributionOptions;
                if (e.PropertyName == nameof(PlatformDownloadViewModel.SelectedDistribution) &&
                    !ReferenceEquals(fileBox.SelectedItem, _viewModel.SelectedDistribution))
                    fileBox.SelectedItem = _viewModel.SelectedDistribution;
            };
            rightPanel.Children.Add(fileBox);

            // issue #330: реальное имя файла, который будет скачан (не только подсказка).
            var pickedFileLine = MakeLabel(
                _viewModel.PickedFile?.FileName ?? T("PlatformDownload.NoFile"), secondary: true);
            _viewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PlatformDownloadViewModel.PickedFile))
                    pickedFileLine.Text = _viewModel.PickedFile?.FileName ?? T("PlatformDownload.NoFile");
            };
            rightPanel.Children.Add(pickedFileLine);

            // Учётная запись.
            var accountLine = MakeLabel(
                _viewModel.HasAccount
                    ? string.Format(T("PlatformDownload.AccountValue"), _viewModel.AccountName)
                    : T("PlatformDownload.NoAccount"), secondary: true);
            _viewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PlatformDownloadViewModel.AccountName) ||
                    e.PropertyName == nameof(PlatformDownloadViewModel.HasAccount))
                {
                    accountLine.Text = _viewModel.HasAccount
                        ? string.Format(T("PlatformDownload.AccountValue"), _viewModel.AccountName)
                        : T("PlatformDownload.NoAccount");
                }
            };
            rightPanel.Children.Add(accountLine);

            // Папка загрузки + кнопка выбора.
            var dirPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            dirPanel.Children.Add(MakeLabel(T("PlatformDownload.Directory")));
            var dirBox = new TextBox
            {
                Width = 420,
                Height = 30,
                Text = _viewModel.TargetDirectory,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            dirBox.TextChanged += (_, _) => _viewModel.TargetDirectory = dirBox.Text ?? string.Empty;
            dirPanel.Children.Add(dirBox);
            var chooseDir = MakeButton(T("PlatformDownload.ChooseDirectory"), () => _viewModel.ChooseDirectory(), secondary: true);
            dirPanel.Children.Add(chooseDir);
            rightPanel.Children.Add(dirPanel);

            // Действия после скачивания: только «Открыть папку» — дистрибутив скачивается
            // архивом, запускать установщик из окна не нужно (issue #330, комментарий 7OH).
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, IsVisible = false };
            actions.Children.Add(MakeButton(T("PlatformDownload.OpenFolder"), () => _viewModel.OpenFolder(), primary: true));
            _viewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PlatformDownloadViewModel.HasDownloaded))
                    actions.IsVisible = _viewModel.HasDownloaded;
            };
            rightPanel.Children.Add(actions);

            // Прогресс.
            var progressPanel = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) } };
            var progressBar = new ProgressBar { Minimum = 0, Maximum = 1, Height = 6 };
            progressBar.Bind(ProgressBar.ValueProperty,
                new Avalonia.Data.Binding(nameof(PlatformDownloadViewModel.Progress)) { Source = _viewModel });
            Grid.SetColumn(progressBar, 0);
            progressPanel.Children.Add(progressBar);
            var progressText = new TextBlock
            {
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 11
            };
            progressText.Bind(TextBlock.TextProperty,
                new Avalonia.Data.Binding(nameof(PlatformDownloadViewModel.Progress)) { Source = _viewModel, StringFormat = "{0:P0}" });
            Grid.SetColumn(progressText, 1);
            progressPanel.Children.Add(progressText);
            rightPanel.Children.Add(progressPanel);

            right.Content = rightPanel;
            Grid.SetColumn(right, 1);
            body.Children.Add(right);

            // Нижняя панель: результат, журнал, кнопки.
            var bottom = new Border
            {
                Margin = new Thickness(0, 12, 0, 0),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6)
            };
            ThemeBrushes.Bind(bottom, Border.BackgroundProperty, "CardBackgroundColorBrush");
            ThemeBrushes.Bind(bottom, Border.BorderBrushProperty, "BorderColorBrush");

            var bottomStack = new StackPanel { Margin = new Thickness(12), Spacing = 8 };

            var resultLine = new TextBlock
            {
                Text = _viewModel.ResultText,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap
            };
            ThemeBrushes.Bind(resultLine, TextBlock.ForegroundProperty, "TextSecondaryBrush");
            _viewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PlatformDownloadViewModel.ResultText))
                    resultLine.Text = _viewModel.ResultText;
            };
            bottomStack.Children.Add(resultLine);

            _logBox = new TextBox
            {
                Height = 100,
                FontFamily = new FontFamily("Consolas, Courier New, monospace"),
                FontSize = 11,
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap
            };
            _logBox.Bind(TextBox.TextProperty,
                new Avalonia.Data.Binding(nameof(PlatformDownloadViewModel.LogText)) { Source = _viewModel, Mode = Avalonia.Data.BindingMode.OneWay });
            ThemeBrushes.Bind(_logBox, TextBox.BackgroundProperty, "CardBackgroundColorBrush");
            ThemeBrushes.Bind(_logBox, TextBox.BorderBrushProperty, "BorderColorBrush");
            bottomStack.Children.Add(_logBox);

            var buttons = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            buttons.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            buttons.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            buttons.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

            // Действия при проблемах авторизации (issue #323/#330/#334): открыть login.1c.ru
            // в браузере, справочник учётных данных ИТС.
            var authActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            authActions.Children.Add(MakeButton(T("Updates.OpenLoginPage"), OpenLogin, secondary: true));
            authActions.Children.Add(MakeButton(T("Updates.OpenItsAccounts"), OpenItsAccounts, secondary: true));
            Grid.SetColumn(authActions, 0);
            buttons.Children.Add(authActions);

            var refresh = MakeButton(T("PlatformDownload.RefreshCatalog"), () => _ = _viewModel.LoadCatalogAsync(), secondary: true);
            Grid.SetColumn(refresh, 1);
            buttons.Children.Add(refresh);

            var download = MakeButton(T("PlatformDownload.Download"), () => _ = _viewModel.DownloadAsync(), primary: true);
            Grid.SetColumn(download, 2);
            buttons.Children.Add(download);

            var close = BuildCancelActionButton(120);
            close.Click += (_, _) => Close();
            buttons.Children.Add(close);

            bottomStack.Children.Add(buttons);
            bottom.Child = bottomStack;
            Grid.SetRow(bottom, 2);
            grid.Children.Add(bottom);

            return grid;
        }

        private void ExecuteExpandAll()
        {
            if (_viewModel.ExpandAllCommand.CanExecute(null))
                _viewModel.ExpandAllCommand.Execute(null);
        }

        private void ExecuteCollapseAll()
        {
            if (_viewModel.CollapseAllCommand.CanExecute(null))
                _viewModel.CollapseAllCommand.Execute(null);
        }

        /// <summary>Открывает login.1c.ru в браузере (issue #323/#330/#334): пользователь выполняет
        /// вход вручную, после чего возвращается в окно и повторяет проверку.</summary>
        private void OpenLogin()
        {
            if (!OneCLauncher.OpenUrl("https://login.1c.ru/login"))
            {
                _viewModel.AppendLog(LocalizationManager.T("Settings.About.LinkOpenFailed"));
            }
        }

        /// <summary>Открывает справочник учётных записей ИТС (issue #323/#330/#334): после правки
        /// данных повторный вход использует обновлённую запись.</summary>
        private void OpenItsAccounts()
        {
            var win = new ItsAccountsWindow();
            win.ShowSync(this);
        }

        private static TextBlock MakeLabel(string text, bool secondary = false)
        {
            var block = new TextBlock
            {
                Text = text,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12.5
            };
            if (secondary)
                ThemeBrushes.Bind(block, TextBlock.ForegroundProperty, "TextSecondaryBrush");
            return block;
        }

        private static Button MakeButton(string text, Action onClick, bool primary = false, bool secondary = false)
        {
            var button = new Button
            {
                Content = text,
                Height = 34,
                Padding = new Thickness(14, 0)
            };
            if (primary)
                button.Styled(ControlThemes.DialogConfirmButton);
            else if (secondary)
                button.Styled(ControlThemes.SecondaryButton);
            button.Click += (_, _) => onClick();
            return button;
        }

        /// <summary>Значковая кнопка без текста (issue #330, комментарий 7OH от 2026-10-09):
        /// иконка из Icons.axaml по ключу геометрии + подсказка с назначением. Размер
        /// и стиль совпадают с текстовыми кнопками панели, чтобы строка поиска была видна.</summary>
        private static Button MakeIconButton(string iconKey, string tooltip, Action onClick)
        {
            var button = new Button
            {
                Content = IconHelper.MakeIcon(iconKey, 16),
                Width = 34,
                Height = 28,
                Padding = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            button.Styled(ControlThemes.SecondaryButton);
            ToolTip.SetTip(button, tooltip);
            button.Click += (_, _) => onClick();
            return button;
        }
    }
}
#endif