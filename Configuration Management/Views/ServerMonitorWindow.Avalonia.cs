#if LINUX
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Configuration_Management.Controls;
using Configuration_Management.Localization;
using Configuration_Management.Services;
using Configuration_Management.Themes;
using Configuration_Management.ViewModels;

namespace Configuration_Management
{
    /// <summary>
    /// Окно «Серверы 1С» (0.3.9.124, Avalonia/Linux): встроенный монитор серверов 1С
    /// через утилиту rac — панель подключения (адрес/порт/логин/пароль), выбор
    /// кластера, вкладки «Рабочие процессы / Сеансы / Соединения / Блокировки /
    /// Информация о кластере» и статус-строка. Вся логика — в чистой ViewModel
    /// <see cref="ServerMonitorViewModel"/>. Пароль не сохраняется на диск
    /// (решение планирования): PasswordBox не биндится, значение передаётся
    /// в VM при подключении.
    /// </summary>
    public sealed class ServerMonitorWindow : ModalWindowBase
    {
        private readonly ServerMonitorViewModel _vm;
        private readonly ComboBox _clusterCombo;
        private TextBlock? _jobsEmptyHint;

        public ServerMonitorWindow()
        {
            Title = LocalizationManager.T("ServerMonitor.Title");
            Width = 1280;
            Height = 720;
            MinWidth = 980;
            MinHeight = 520;
            FontSize = 13;
            CanResize = true;

            var rac = AppServices.GetRequiredService<IRacClient>();
            var dialogs = AppServices.GetRequiredService<IDialogService>();

            _vm = new ServerMonitorViewModel(
                rac,
                dialogs,
                action => Dispatcher.UIThread.Post(action));

            // Начальные адрес/порт/логин — из настроек приложения (пароль НЕ сохраняется).
            LoadSavedConnectionSettings();

            DataContext = _vm;
            _vm.PropertyChanged += OnVmPropertyChanged;
            Closed += (_, _) => _vm.Dispose();

            // ---- Панель подключения: адрес, порт, логин, пароль, кнопки. ----
            var addressBox = Tb("ServerAddress", 150);
            var portBox = Tb("ServerPort", 60);

            // Поля префиллятся из настроек последнего успешного подключения (issue #324):
            // подсказка объясняет источник значения, чтобы оно не выглядело «подменой ввода».
            ToolTip.SetTip(addressBox, LocalizationManager.T("ServerMonitor.SavedConnectionTooltip"));
            var userBox = Tb("UserName", 120);
            var passwordBox = new PasswordBox().Styled(ControlThemes.ModernPasswordBox);

            // Подсказка про порт (issue #324): rac подключается к АГЕНТУ сервера (1540),
            // порт кластера (1541) указывать не нужно — он виден в списке кластеров.
            ToolTip.SetTip(portBox, LocalizationManager.T("ServerMonitor.PortTooltip"));

            var connectButton = BuildActionButton(LocalizationManager.T("ServerMonitor.Connect"), "🔌", () =>
            {
                _vm.Password = passwordBox.Password ?? string.Empty;
                _ = ConnectAndSaveAsync();
            });
            var refreshButton = BuildActionButton(LocalizationManager.T("ServerMonitor.Refresh"), "⟳", _vm.Refresh);
            var closeButton = BuildCloseButton();

            var fields = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    Labeled("ServerMonitor.Address", addressBox),
                    Labeled("ServerMonitor.Port", portBox),
                    Labeled("ServerMonitor.User", userBox),
                    Labeled("ServerMonitor.Password", passwordBox)
                }
            };

            // «Диагностика сети…» (0.3.9.232, функция 12): DNS/ICMP/TCP-проверка
            // адреса:порт монитора и стандартных портов 1С. Работает до подключения rac.
            var diagnosticsButton = BuildActionButton(
                LocalizationManager.T("ServerMonitor.NetworkDiagnostics"), "🛜", OpenNetworkDiagnostics);

            // Автообновление (issue #324, комментарий 17/18): переключатель вкл/выкл + интервал.
            var autoRefreshCheck = new CheckBox
            {
                Content = LocalizationManager.T("ServerMonitor.AutoRefreshToggle"),
                VerticalAlignment = VerticalAlignment.Center,
                IsChecked = _vm.IsAutoRefreshEnabled
            };
            autoRefreshCheck.IsCheckedChanged += (_, _) => _vm.SetAutoRefreshEnabled(autoRefreshCheck.IsChecked == true);
            var intervalLabel = new TextBlock
            {
                Text = LocalizationManager.T("ServerMonitor.AutoRefreshInterval"),
                VerticalAlignment = VerticalAlignment.Center
            };
            var intervalBox = new ComboBox { Width = 72, VerticalAlignment = VerticalAlignment.Center };
            intervalBox.ItemsSource = new[] { 5, 10, 15, 30, 60 };
            intervalBox.SelectedItem = _vm.AutoRefreshIntervalSeconds;
            intervalBox.SelectionChanged += (_, _) =>
            {
                if (intervalBox.SelectedItem is int seconds)
                    _vm.AutoRefreshIntervalSeconds = seconds;
            };

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8,
                Margin = new Thickness(0, 10, 0, 0),
                Children = { connectButton, refreshButton, autoRefreshCheck, intervalLabel, intervalBox, diagnosticsButton, closeButton }
            };

            // ---- Выбор кластера + статус. ----
            _clusterCombo = new ComboBox { Width = 380, VerticalContentAlignment = VerticalAlignment.Center };
            _clusterCombo.Styled(ControlThemes.ModernComboBox);
            _clusterCombo.ItemsSource = _vm.ClusterRows;
            _clusterCombo.ItemTemplate = new FuncDataTemplate<RacClusterRow>((row, _) => new TextBlock { Text = row.DisplayText });
            _clusterCombo.SelectionChanged += (_, _) =>
            {
                if (_clusterCombo.SelectedItem is RacClusterRow row)
                    _vm.SelectedClusterId = row.Id;
            };

            var status = new TextBlock { FontSize = 12, Opacity = 0.65, VerticalAlignment = VerticalAlignment.Center };
            status.Bind(TextBlock.TextProperty, new Binding("StatusText"));
            var error = new TextBlock { FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#DC2626")), TextWrapping = TextWrapping.Wrap };
            error.Bind(TextBlock.TextProperty, new Binding("ErrorMessage"));

            var clusterPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Margin = new Thickness(0, 10, 0, 0),
                Children =
                {
                    new TextBlock { Text = LocalizationManager.T("ServerMonitor.Cluster"), VerticalAlignment = VerticalAlignment.Center },
                    _clusterCombo,
                    status
                }
            };

            // ---- Вкладки данных кластера. ----
            var tabs = new TabControl { Margin = new Thickness(0, 12, 0, 0) };
            tabs.Items.Add(new TabItem
            {
                Header = LocalizationManager.T("ServerMonitor.Tabs.Processes"),
                Content = BuildList("Processes", BuildProcessRow)
            });
            tabs.Items.Add(new TabItem
            {
                Header = LocalizationManager.T("ServerMonitor.Tabs.Sessions"),
                Content = BuildTabWithAction(
                    BuildList("Sessions", BuildSessionRow, "SelectedSession"),
                    BuildActionButton(LocalizationManager.T("ServerMonitor.TerminateSession"), "✕",
                        () => _vm.TerminateSessionCommand.Execute(null)))
            });
            tabs.Items.Add(new TabItem
            {
                Header = LocalizationManager.T("ServerMonitor.Tabs.Connections"),
                Content = BuildTabWithAction(
                    BuildList("Connections", BuildConnectionRow, "SelectedConnection"),
                    BuildActionButton(LocalizationManager.T("ServerMonitor.DisconnectConnection"), "⛓",
                        () => _vm.DisconnectConnectionCommand.Execute(null)))
            });
            tabs.Items.Add(new TabItem
            {
                Header = LocalizationManager.T("ServerMonitor.Tabs.Locks"),
                Content = BuildList("Locks", BuildLockRow)
            });
            // Регламентные задания кластера (0.3.9.178): фильтр по базе, кнопки
            // «Приостановить/Возобновить» (подтверждение — в VM) и «Детали».
            tabs.Items.Add(new TabItem
            {
                Header = LocalizationManager.T("ServerMonitor.Tabs.Jobs"),
                Content = BuildJobsTab()
            });
            tabs.Items.Add(new TabItem
            {
                Header = LocalizationManager.T("ServerMonitor.Tabs.Info"),
                Content = BuildInfoPane()
            });

            var autoRefreshHint = new TextBlock { FontSize = 11, Opacity = 0.65, TextWrapping = TextWrapping.Wrap };
            autoRefreshHint.Bind(TextBlock.TextProperty, new Binding("AutoRefreshText"));

            var root = new Grid
            {
                Margin = new Thickness(16),
                RowDefinitions =
                {
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Star),
                    new RowDefinition(GridLength.Auto)
                }
            };
            Place(root, fields, 0);
            Place(root, buttons, 1);
            Place(root, clusterPanel, 2);
            Place(root, tabs, 3);
            Place(root, new StackPanel { Children = { status, error, autoRefreshHint }, Spacing = 2 }, 4);
            Content = root;
        }

        private async System.Threading.Tasks.Task ConnectAndSaveAsync()
        {
            await _vm.ConnectAsync();
            if (_vm.HasConnected)
                SaveRacSettings();
        }

        /// <summary>
        /// «Диагностика сети…» (0.3.9.232, функция 12): открывает окно диагностики
        /// с текущими адресом/портом монитора. Не требует успешного подключения rac —
        /// диагностика нужна именно до подключения.
        /// </summary>
        private void OpenNetworkDiagnostics()
        {
            var target = NetworkDiagnosticsTargets.FromServerMonitor(_vm.ServerAddress, _vm.ServerPort);
            var vm = new NetworkDiagnosticsViewModel(
                AppServices.GetRequiredService<INetworkDiagnosticsService>(),
                target,
                action => Dispatcher.UIThread.Post(action));
            new NetworkDiagnosticsWindow(vm).ShowDialog(this);
        }

        // ===================== Построители =====================

        private static void Place(Grid grid, Control control, int row)
        {
            Grid.SetRow(control, row);
            grid.Children.Add(control);
        }

        private static Control Labeled(string textKey, Control input)
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = LocalizationManager.T(textKey), VerticalAlignment = VerticalAlignment.Center },
                    input
                }
            };
            return panel;
        }

        private static TextBox Tb(string property, double width)
        {
            var tb = new TextBox
            {
                Width = width,
                Padding = new Thickness(6, 3),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            tb.Styled(ControlThemes.ModernTextBox);
            tb.Bind(TextBox.TextProperty, new Binding(property, BindingMode.TwoWay));
            return tb;
        }

        private static ListBox BuildList(string binding, Func<object, Control> rowFactory, string? selectedBinding = null)
        {
            var list = new ListBox
            {
                ItemTemplate = new FuncDataTemplate<object>((item, _) => rowFactory(item))
            };
            // Тексты строк по вертикали по центру (issue #324): контейнер ListBoxItem
            // центрирует содержимое, MinHeight задаёт единую комфортную высоту строки
            // (аналог RowHeight=34 у DataGrid в WPF-версии окна).
            list.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
            {
                Setters =
                {
                    new Setter(ListBoxItem.MinHeightProperty, 36d),
                    new Setter(ListBoxItem.VerticalContentAlignmentProperty, VerticalAlignment.Center)
                }
            });
            list.Bind(ListBox.ItemsSourceProperty, new Binding(binding));
            if (selectedBinding is not null)
                list.Bind(ListBox.SelectedItemProperty, new Binding(selectedBinding, BindingMode.TwoWay));
            return list;
        }

        /// <summary>Вкладка с действием: кнопка снизу (завершение сеанса / разрыв соединения), список заполняет остальное.</summary>
        private static Control BuildTabWithAction(Control content, Button button)
        {
            var dock = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(button, Dock.Bottom);
            button.HorizontalAlignment = HorizontalAlignment.Left;
            button.Margin = new Thickness(0, 8, 0, 0);
            dock.Children.Add(button);
            dock.Children.Add(content);
            return dock;
        }

        private static Button BuildActionButton(string text, string icon, System.Action onClick)
        {
            var button = new Button
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Children =
                    {
                        new TextBlock { Text = icon, VerticalAlignment = VerticalAlignment.Center },
                        new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center }
                    }
                },
                Padding = new Thickness(12, 5)
            };
            button.Styled(ControlThemes.ModernButton);
            ThemeBrushes.Bind(button, Button.BackgroundProperty, "ItemHoverBrush");
            button.Click += (_, _) => onClick();
            return button;
        }

        private Button BuildCloseButton()
        {
            var button = new Button
            {
                Content = IconHelper.IconAndText("IconClose", LocalizationManager.T("Common.Close"), 14, "TextPrimaryBrush"),
                IsCancel = true
            };
            button.Styled(ControlThemes.ModernButton);
            ThemeBrushes.Bind(button, Button.BackgroundProperty, "ItemHoverBrush");
            ThemeBrushes.Bind(button, Button.ForegroundProperty, "TextPrimaryBrush");
            button.Click += (_, _) => Close();
            return button;
        }

        private Control BuildInfoPane()
        {
            var text = new TextBlock
            {
                FontFamily = new FontFamily("Consolas, monospace"),
                FontSize = 12,
                Margin = new Thickness(10),
                TextWrapping = TextWrapping.Wrap
            };
            text.Bind(TextBlock.TextProperty, new Binding("ClusterInfoText"));
            return new ScrollViewer { Content = text, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }

        // ===================== Строки вкладок =====================

        private static Control CellText(string property, bool bold = false, string? colorHex = null)
        {
            var text = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
                Foreground = colorHex is null ? null : new SolidColorBrush(Color.Parse(colorHex))
            };
            text.Bind(TextBlock.TextProperty, new Binding(property));
            return text;
        }

        /// <summary>
        /// Вкладка «Регламентные задания»: фильтр по базе, кнопки действий и таблица.
        /// Подсказка пустого списка обновляется в <see cref="OnVmPropertyChanged"/>.
        /// </summary>
        private Control BuildJobsTab()
        {
            var filterCombo = new ComboBox { Width = 260, VerticalContentAlignment = VerticalAlignment.Center };
            filterCombo.Styled(ControlThemes.ModernComboBox);
            filterCombo.ItemsSource = _vm.JobInfobaseFilterRows;
            filterCombo.ItemTemplate = new FuncDataTemplate<RacJobFilterRow>((row, _) =>
                new TextBlock { Text = row.DisplayText });
            filterCombo.SelectionChanged += (_, _) =>
            {
                if (filterCombo.SelectedItem is RacJobFilterRow row)
                    _vm.SelectedJobInfobaseId = row.Id;
            };

            var pauseButton = BuildActionButton(LocalizationManager.T("ServerMonitor.PauseJob"), "⏸",
                () => _vm.PauseJobCommand.Execute(null));
            var resumeButton = BuildActionButton(LocalizationManager.T("ServerMonitor.ResumeJob"), "▶",
                () => _vm.ResumeJobCommand.Execute(null));
            var detailsButton = BuildActionButton(LocalizationManager.T("ServerMonitor.JobDetails"), "ℹ",
                () =>
                {
                    if (_vm.SelectedJob is not null)
                        new JobDetailsWindow(_vm.SelectedJob.DetailsText).ShowDialog(this);
                });

            // Кнопки действий зависят от состояния выбранного задания (VM уведомляет).
            pauseButton.Bind(Button.IsEnabledProperty, new Binding("CanPauseSelectedJob"));
            resumeButton.Bind(Button.IsEnabledProperty, new Binding("CanResumeSelectedJob"));

            _jobsEmptyHint = new TextBlock
            {
                Text = LocalizationManager.T("ServerMonitor.Empty.Jobs"),
                FontSize = 12,
                Opacity = 0.65,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6),
                IsVisible = !_vm.HasJobs
            };

            var toolbar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Margin = new Thickness(0, 0, 0, 8),
                Children =
                {
                    new TextBlock
                    {
                        Text = LocalizationManager.T("ServerMonitor.Columns.Job.Infobase"),
                        VerticalAlignment = VerticalAlignment.Center
                    },
                    filterCombo,
                    pauseButton,
                    resumeButton,
                    detailsButton
                }
            };

            var dock = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(toolbar, Dock.Top);
            DockPanel.SetDock(_jobsEmptyHint, Dock.Top);
            dock.Children.Add(toolbar);
            dock.Children.Add(_jobsEmptyHint);
            dock.Children.Add(BuildList("FilteredJobs", BuildJobRow, "SelectedJob"));
            return dock;
        }

        private static Control BuildProcessRow(object item)
        {
            var row = (RacProcessRow)item;
            var runningDot = new TextBlock
            {
                Text = row.Running ? "●" : "○",
                FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.Parse(row.StateColorHex))
            };

            var grid = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(new GridLength(90)),
                    new ColumnDefinition(new GridLength(1.2, GridUnitType.Star)),
                    new ColumnDefinition(new GridLength(70)),
                    new ColumnDefinition(new GridLength(60)),
                    new ColumnDefinition(new GridLength(130)),
                    new ColumnDefinition(new GridLength(90)),
                    new ColumnDefinition(new GridLength(80)),
                    new ColumnDefinition(new GridLength(80)),
                    new ColumnDefinition(new GridLength(80)),
                    new ColumnDefinition(new GridLength(60)),
                    new ColumnDefinition(new GridLength(30))
                }
            };
            AddCell(grid, CellText("Type"), 0);
            AddCell(grid, CellText("Host"), 1);
            AddCell(grid, CellText("Pid", bold: true), 2);
            AddCell(grid, CellText("Port"), 3);
            AddCell(grid, CellText("StartedAtText"), 4);
            AddCell(grid, CellText("MemorySizeText"), 5);
            AddCell(grid, CellText("Threads"), 6);
            AddCell(grid, CellText("CpuText"), 7);
            AddCell(grid, CellText("RunningText"), 8);
            AddCell(grid, CellText("Infobases"), 9);
            AddCell(grid, runningDot, 10);
            return grid;
        }

        private static Control BuildSessionRow(object item)
        {
            var row = (RacSessionRow)item;
            var blockedDot = new TextBlock
            {
                Text = row.BlockedSymbol,
                FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.Parse("#D97706"))
            };

            var grid = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(new GridLength(1.2, GridUnitType.Star)),
                    new ColumnDefinition(new GridLength(1.1, GridUnitType.Star)),
                    new ColumnDefinition(new GridLength(90)),
                    new ColumnDefinition(new GridLength(130)),
                    new ColumnDefinition(new GridLength(130)),
                    new ColumnDefinition(new GridLength(100)),
                    new ColumnDefinition(new GridLength(40)),
                    new ColumnDefinition(new GridLength(90)),
                    new ColumnDefinition(new GridLength(90)),
                    new ColumnDefinition(new GridLength(90)),
                    new ColumnDefinition(new GridLength(80))
                }
            };
            AddCell(grid, CellText("User", bold: true), 0);
            AddCell(grid, CellText("Host"), 1);
            AddCell(grid, CellText("AppId"), 2);
            AddCell(grid, CellText("StartedAtText"), 3);
            AddCell(grid, CellText("LastActiveAtText"), 4);
            AddCell(grid, CellText("StateText", colorHex: row.StateColorHex), 5);
            AddCell(grid, blockedDot, 6);
            AddCell(grid, CellText("MemoryText"), 7);
            AddCell(grid, CellText("DurationAllText"), 8);
            AddCell(grid, CellText("DurationCurrentText"), 9);
            AddCell(grid, CellText("HibernateText"), 10);
            return grid;
        }

        private static Control BuildConnectionRow(object item)
        {
            var row = (RacConnectionRow)item;
            var grid = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(new GridLength(1.2, GridUnitType.Star)),
                    new ColumnDefinition(new GridLength(70)),
                    new ColumnDefinition(new GridLength(100)),
                    new ColumnDefinition(new GridLength(130)),
                    new ColumnDefinition(new GridLength(130)),
                    new ColumnDefinition(new GridLength(100)),
                    new ColumnDefinition(new GridLength(1.3, GridUnitType.Star)),
                    new ColumnDefinition(new GridLength(110))
                }
            };
            AddCell(grid, CellText("Host"), 0);
            AddCell(grid, CellText("Port"), 1);
            AddCell(grid, CellText("Connector"), 2);
            AddCell(grid, CellText("EstablishedAtText"), 3);
            AddCell(grid, CellText("LastConnectionTimeText"), 4);
            AddCell(grid, CellText("DurationText"), 5);
            AddCell(grid, CellText("Descr"), 6);
            AddCell(grid, CellText("BlockedText", colorHex: row.BlockedColorHex), 7);
            return grid;
        }

        private static Control BuildJobRow(object item)
        {
            var row = (RacJobRow)item;
            var grid = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(new GridLength(1.4, GridUnitType.Star)),
                    new ColumnDefinition(new GridLength(1.1, GridUnitType.Star)),
                    new ColumnDefinition(new GridLength(1.1, GridUnitType.Star)),
                    new ColumnDefinition(new GridLength(1.1, GridUnitType.Star)),
                    new ColumnDefinition(new GridLength(110)),
                    new ColumnDefinition(new GridLength(130)),
                    new ColumnDefinition(new GridLength(130)),
                    new ColumnDefinition(new GridLength(80)),
                    new ColumnDefinition(new GridLength(1.2, GridUnitType.Star)),
                    new ColumnDefinition(new GridLength(90))
                }
            };
            AddCell(grid, CellText("Name", bold: true), 0);
            AddCell(grid, CellText("InfobaseName"), 1);
            AddCell(grid, CellText("MethodName"), 2);
            AddCell(grid, CellText("Schedule"), 3);
            AddCell(grid, CellText("StateText", colorHex: row.StateColorHex), 4);
            AddCell(grid, CellText("NextStartText"), 5);
            AddCell(grid, CellText("LastStartText"), 6);
            AddCell(grid, CellText("LastSuccessText"), 7);
            AddCell(grid, CellText("ResultText"), 8);
            AddCell(grid, CellText("PredefinedText"), 9);
            return grid;
        }

        private static Control BuildLockRow(object item)
        {
            var row = (RacLockRow)item;
            var grid = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(new GridLength(2, GridUnitType.Star)),
                    new ColumnDefinition(new GridLength(90)),
                    new ColumnDefinition(new GridLength(90)),
                    new ColumnDefinition(new GridLength(1.1, GridUnitType.Star)),
                    new ColumnDefinition(new GridLength(1.1, GridUnitType.Star)),
                    new ColumnDefinition(new GridLength(1.1, GridUnitType.Star))
                }
            };
            AddCell(grid, CellText("Object", bold: true), 0);
            AddCell(grid, CellText("WaitingSymbol", colorHex: "#D97706"), 1);
            AddCell(grid, CellText("BlockingSymbol", colorHex: "#DC2626"), 2);
            AddCell(grid, CellText("SessionId"), 3);
            AddCell(grid, CellText("ConnectionId"), 4);
            AddCell(grid, CellText("TransactionId"), 5);
            return grid;
        }

        private static void AddCell(Grid grid, Control control, int column)
        {
            Grid.SetColumn(control, column);
            grid.Children.Add(control);
        }

        // ===================== Синхронизация =====================

        private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ServerMonitorViewModel.HasJobs) && _jobsEmptyHint is not null)
            {
                // Подсказка «заданий нет / нет прав администратора» видна только при пустом списке.
                _jobsEmptyHint.IsVisible = !_vm.HasJobs;
                return;
            }

            // issue #324 (0.3.9.330): ItemsSource комбо кластеров задаётся в конструкторе,
            // когда ClusterRows ещё пуст, — после подключения список надо обновить,
            // иначе в выпадающем списке нечего выбирать.
            if (e.PropertyName == nameof(ServerMonitorViewModel.ClusterRows))
            {
                _clusterCombo.ItemsSource = _vm.ClusterRows;
                return;
            }

            if (e.PropertyName != nameof(ServerMonitorViewModel.SelectedClusterId))
                return;
            if (_vm.SelectedClusterId is Guid id)
            {
                var row = _vm.ClusterRows.FirstOrDefault(r => r.Id == id);
                if (row is not null && !ReferenceEquals(_clusterCombo.SelectedItem, row))
                    _clusterCombo.SelectedItem = row;
            }
        }

        // ===================== Настройки =====================

        /// <summary>Сохраняет адрес/порт/логин после успешного подключения (без пароля).</summary>
        private void SaveRacSettings()
        {
            try
            {
                var repository = AppServices.GetRequiredService<IInfobaseRepository>();
                var settings = repository.LoadSettings();
                settings.RacServerAddress = _vm.ServerAddress;
                settings.RacServerPort = _vm.ServerPort;
                settings.RacUserName = _vm.UserName;
                repository.SaveSettings(settings);
            }
            catch
            {
                // Сохранение настроек не критично для работы окна.
            }
        }

        private void LoadSavedConnectionSettings()
        {
            try
            {
                var settings = AppServices.GetRequiredService<IInfobaseRepository>().LoadSettings();
                _vm.ServerAddress = string.IsNullOrWhiteSpace(settings.RacServerAddress)
                    ? "localhost"
                    : settings.RacServerAddress;
                if (settings.RacServerPort > 0)
                    _vm.ServerPort = settings.RacServerPort;
                _vm.UserName = settings.RacUserName ?? string.Empty;
            }
            catch
            {
                // Без сохранённых настроек остаются дефолты VM.
            }
        }
    }
}
#endif