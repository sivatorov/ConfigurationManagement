#if LINUX
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Configuration_Management.Localization;
using Configuration_Management.Models;
using Configuration_Management.Services;
using Configuration_Management.Themes;
using Configuration_Management.ViewModels;

namespace Configuration_Management
{
    /// <summary>
    /// Окно «Обновление платформы 1С» (горячая клавиша Ctrl+F9, функция 9): единый список
    /// установленных и доступных версий технологической платформы (Версия/Размер/Статус/
    /// Совместимые базы), проверка каталога портала releases.1c.ru, загрузка дистрибутива
    /// и установка. Перед установкой выполняется проверка готовности (занятые процессы 1С,
    /// права, свободное место, подпись) с диалогом подтверждения, результат операции
    /// сопровождается уведомлением. Вся логика — в чистой <see cref="PlatformUpdateViewModel"/>;
    /// сервисы берутся из <see cref="AppServices"/> (паттерн <see cref="ActualReleasesWindow"/>).
    /// Avalonia/Linux-версия WPF-окна <see cref="PlatformUpdateWindow"/>.
    /// </summary>
    public sealed class PlatformUpdateWindow : ModalWindowBase
    {
        private readonly Services.IPlatformUpdateService _service = AppServices.GetRequiredService<Services.IPlatformUpdateService>();
        private readonly Services.IInfobaseRepository _repository = AppServices.GetRequiredService<Services.IInfobaseRepository>();
        private readonly Services.IOneCUpdatesService _updates = AppServices.GetRequiredService<Services.IOneCUpdatesService>();
        private readonly Services.IDialogService _dialogs = AppServices.GetRequiredService<Services.IDialogService>();
        private readonly Services.IRunningInfobasesService _running = AppServices.GetRequiredService<Services.IRunningInfobasesService>();
        private readonly Services.INotificationService _notifier = AppServices.GetRequiredService<Services.INotificationService>();
        private readonly Services.IAppLogger _logger = AppServices.GetRequiredService<Services.IAppLogger>();

        private readonly PlatformUpdateViewModel _viewModel;

        private readonly StackPanel _rowsPanel = new();
        private readonly Dictionary<PlatformUpdateRowViewModel, StackPanel> _cells = new();
        private readonly Dictionary<PlatformUpdateRowViewModel, ProgressBar> _progressBars = new();
        private readonly Dictionary<PlatformUpdateRowViewModel, TextBlock> _progressTexts = new();

        private TextBox? _logBox;

        /// <summary>Открывает окно «Обновление платформы 1С».</summary>
        public PlatformUpdateWindow()
        {
            Title = LocalizationManager.T("PlatformUpdate.WindowTitle");
            Width = 920;
            Height = 660;
            MinWidth = 740;
            MinHeight = 520;
            FontSize = 13;
            CanResize = true;

            _viewModel = new PlatformUpdateViewModel(
                _service,
                _repository,
                LoadInstalledVersions,
                (url, targetPath, progress, ct) =>
                    _updates.DownloadDistributionAsync(url, targetPath, progress, ct),
                InstallFromZipAsync,
                defaultName => _dialogs.SaveFileDialog(
                    LocalizationManager.T("PlatformUpdate.DownloadOnly"),
                    defaultName ?? "platform.zip",
                    "Архивы (*.zip)|*.zip|Пакеты Linux (*.deb;*.rpm)|*.deb;*.rpm|Все файлы (*.*)|*.*",
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
                loadRunningProcesses: () => _running.GetRunning()
                    .Select(p => p.ProcessName)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                isAdministrator: IsAdministrator,
                getFreeBytes: path => DiskFreeSpaceHelper.TryGetInfo(path, DiskFreeSpaceHelper.DefaultDriveResolver)?.FreeBytes,
                hasValidSignature: _ => false,
                confirmDialog: (title, message) => _dialogs.Confirm(message, title),
                notify: (title, message, kind, evt) => _notifier.Show(title, message, kind, evt),
                appLogger: _logger,
                // Удаление старых версий (этап 0.3.9.215): инфо установленных версий с путями,
                // пути бинарников запущенных процессов; команда удаления sudo + буфер обмена.
                loadInstalledVersionInfos: () => PlatformVersionService.FindInstalledVersionInfos(),
                loadRunningBinPaths: () => _running.GetRunning()
                    .Select(p => OldVersionCleaner.ExtractExecutablePath(p.CommandLine))
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(p => p!)
                    .ToList(),
                buildUninstallCommand: version => PlatformInstaller.BuildSudoUninstallCommand(version),
                copyToClipboard: text =>
                {
                    try
                    {
                        if (this.Clipboard is { } cb)
                            _ = cb.SetTextAsync(text);
                    }
                    catch
                    {
                        // Буфер обмена недоступен — команда остаётся в журнале окна.
                    }
                },
                // issue #334: обновление списка версий и связанных свойств — в UI-потоке.
                dispatchToUi: action => Dispatcher.UIThread.Post(action),
                // issue #334: диалог выбора варианта дистрибутива после «Скачать и установить».
                chooseDistribution: ChooseDistribution,
                // issue #334: диалог выбора удаляемых старых версий (список с флажками).
                chooseVersionsToDelete: ChooseOldVersionsToDelete);

            BuildRows();
            foreach (var row in _viewModel.Rows)
                AddRow(row);

            Content = BuildRoot();
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        /// <summary>Показывает окно модально (синхронно). Открытая публичная обёртка над
        /// <see cref="ModalWindowBase.ShowDialogSync(Window?)"/>, чтобы диалог можно было
        /// вызывать из ViewModel (не наследника окна).</summary>
        public bool ShowSync(Window? owner = null) => ShowDialogSync(owner);

        /// <summary>
        /// Диалог выбора варианта дистрибутива (issue #334): список файлов для текущей ОС,
        /// предвыбран рекомендуемый; null — отмена (остаётся рекомендуемый). Может
        /// вызываться из фонового потока — показ переводится в UI-поток.
        /// </summary>
        private PlatformDistributionOption? ChooseDistribution(
            IReadOnlyList<PlatformDistributionOption> options)
        {
            PlatformDistributionOption? result = null;
            if (Dispatcher.UIThread.CheckAccess())
            {
                var picker = new PlatformDistributionPickerWindow(options);
                if (picker.ShowDialogSync(this))
                    result = picker.Result;
            }
            else
            {
                Dispatcher.UIThread.InvokeAsync(() =>
                {
                    var picker = new PlatformDistributionPickerWindow(options);
                    if (picker.ShowDialogSync(this))
                        result = picker.Result;
                }).Wait();
            }

            return result;
        }

        private static string T(string key) => LocalizationManager.T(key);

        /// <summary>Читает установленные версии платформы через сканер
        /// <see cref="PlatformVersionService.FindInstalledVersionInfos"/> и приводит их
        /// к чистым номерам версий (<see cref="PlatformVersionService.ParseVariant"/> —
        /// суффикс разрядности «(64)» отбрасывается) для сопоставления с каталогом.</summary>
        private static IReadOnlyList<string> LoadInstalledVersions()
        {
            return PlatformVersionService.FindInstalledVersionInfos()
                .Select(info =>
                {
                    PlatformVersionService.ParseVariant(info.Display, out var clean, out _);
                    return clean;
                })
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .ToList();
        }

        /// <summary>На Linux установка из GUI не выполняется (нужны права root): после
        /// загрузки дистрибутива показывается готовая команда sudo и инструкция; результат
        /// ручной установки отразится при следующей проверке/пересканировании.</summary>
        private static async Task<(bool Success, string? ErrorKey, int ExitCode)> InstallFromZipAsync(
            string zipPath, string version, string? installDirectory,
            IProgress<string>? log, CancellationToken ct)
        {
            var file = new PlatformReleaseFile { FileName = Path.GetFileName(zipPath) };
            log?.Report(PlatformInstaller.BuildInstallInstruction(file));
            await Task.CompletedTask.ConfigureAwait(false);
            return (true, null, 0);
        }

        /// <summary>Эвристика прав администратора на Linux: процесс запущен от root.</summary>
        private static bool IsAdministrator()
            => string.Equals(Environment.UserName, "root", StringComparison.OrdinalIgnoreCase);

        /// <summary>Формирует строки: установленные ∪ доступные версии из ViewModel
        /// (единая точка перестроения — <see cref="PlatformUpdateViewModel"/>).</summary>
        private void BuildRows()
        {
            foreach (var row in _viewModel.Rows)
                AddRow(row);
        }

        /// <summary>Автопрокрутка журнала в конец и показ панели прогресса (issue #334):
        /// панель появляется при старте операции и ОСТАЁТСЯ видимой после завершения —
        /// итог («Готово: …», ошибка, путь сохранения) не должен исчезать через
        /// полсекунды (issue #334: после «только скачать» окно с информационными
        /// сообщениями «пропадало»).</summary>
        private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PlatformUpdateViewModel.LogText))
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (_logBox is not null)
                        _logBox.CaretIndex = _logBox.Text?.Length ?? 0;
                });
                return;
            }

            if (e.PropertyName != nameof(PlatformUpdateViewModel.IsBusy))
                return;

            if (_viewModel.IsBusy)
                _progressPanel.IsVisible = true;
        }

        private StackPanel _progressPanel = new();

        /// <summary>Заголовок таблицы: Версия / Размер / Статус / Совместимые базы.</summary>
        private Grid BuildHeaderGrid()
        {
            var grid = new Grid { Margin = new Thickness(8, 0, 8, 2) };
            ApplyColumns(grid);
            grid.Children.Add(MakeHeaderText(T("PlatformUpdate.Column.Version"), 0));
            grid.Children.Add(MakeHeaderText(T("PlatformUpdate.Column.Size"), 1));
            grid.Children.Add(MakeHeaderText(T("PlatformUpdate.Column.Status"), 2));
            grid.Children.Add(MakeHeaderText(T("PlatformUpdate.Column.Bases"), 3));
            return grid;
        }

        private static TextBlock MakeHeaderText(string text, int column)
        {
            var block = new TextBlock
            {
                Text = text,
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 8, 0)
            };
            Themes.ThemeBrushes.Bind(block, TextBlock.ForegroundProperty, "TextSecondaryBrush");
            Grid.SetColumn(block, column);
            return block;
        }

        private static void ApplyColumns(Grid grid)
        {
            grid.ColumnDefinitions.Clear();
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(14, GridUnitType.Star)));
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(10, GridUnitType.Star)));
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(10, GridUnitType.Star)));
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(7, GridUnitType.Star)));
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        }

        /// <summary>Строит визуальную строку для одной версии платформы: версия, размер,
        /// статус, совместимые базы и индикатор загрузки (по образцу
        /// <see cref="ActualReleasesWindow"/>). Клик по строке выбирает её для команд.</summary>
        private void AddRow(PlatformUpdateRowViewModel row)
        {
            var cell = new StackPanel { Margin = new Thickness(0, 2, 0, 2) };

            var rowIndex = _rowsPanel.Children.Count;
            var oddRow = rowIndex % 2 == 0;
            var bandBrush = oddRow ? (TryBrush("ItemHoverBrush") ?? Brushes.Transparent) : Brushes.Transparent;
            var hoverBrush = TryBrush("ItemSelectedBrush") ?? Brushes.Transparent;
            cell.Background = bandBrush;
            cell.PointerEntered += (_, _) => cell.Background = hoverBrush;
            cell.PointerExited += (_, _) =>
                cell.Background = ReferenceEquals(_viewModel.SelectedRow, row) ? hoverBrush : bandBrush;
            cell.PointerPressed += (_, e) =>
            {
                _viewModel.SelectedRow = row;
                foreach (var pair in _cells)
                {
                    var selected = ReferenceEquals(pair.Key, row);
                    var index = _rowsPanel.Children.IndexOf(pair.Value);
                    var odd = index % 2 == 0;
                    pair.Value.Background = selected
                        ? hoverBrush
                        : (odd ? (TryBrush("ItemHoverBrush") ?? Brushes.Transparent) : Brushes.Transparent);
                }

                e.Handled = true;
            };

            var top = new Grid { Margin = new Thickness(8, 0, 8, 0) };
            ApplyColumns(top);

            var version = new TextBlock
            {
                Text = row.Version,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 8, 0)
            };
            if (row.HasUpdate)
            {
                version.FontWeight = FontWeight.SemiBold;
                version.Foreground = TryBrush("AccentBrush") ?? new SolidColorBrush(Color.Parse("#16A34A"));
            }
            else
            {
                Themes.ThemeBrushes.Bind(version, TextBlock.ForegroundProperty, "TextPrimaryBrush");
            }

            Grid.SetColumn(version, 0);
            top.Children.Add(version);

            var size = new TextBlock
            {
                Text = row.SizeText,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 8, 0)
            };
            Grid.SetColumn(size, 1);
            top.Children.Add(size);

            var status = new TextBlock
            {
                Text = row.StatusText,
                FontWeight = FontWeight.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 8, 0)
            };
            ApplyStatusStyle(status, row);
            Grid.SetColumn(status, 2);
            top.Children.Add(status);

            var bases = new TextBlock
            {
                Text = row.CompatibleBases.ToString(),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(8, 0, 8, 0)
            };
            Grid.SetColumn(bases, 3);
            top.Children.Add(bases);

            cell.Children.Add(top);

            // Нижняя строка: индикатор загрузки строки (виден только при активной загрузке).
            var progressBar = new ProgressBar { Minimum = 0, Maximum = 1, Height = 6 };
            var progressText = new TextBlock { FontSize = 12 };
            Themes.ThemeBrushes.Bind(progressText, TextBlock.ForegroundProperty, "TextSecondaryBrush");
            var progressPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                IsVisible = false,
                Margin = new Thickness(16, 2, 8, 0)
            };
            progressPanel.Children.Add(progressBar);
            progressPanel.Children.Add(progressText);
            cell.Children.Add(progressPanel);

            _cells[row] = cell;
            _progressBars[row] = progressBar;
            _progressTexts[row] = progressText;

            row.PropertyChanged += (_, e) => OnRowPropertyChanged(row, e.PropertyName ?? string.Empty);

            _rowsPanel.Children.Add(cell);
        }

        /// <summary>Окрашивает текст статуса: акцент при наличии обновления.</summary>
        private static void ApplyStatusStyle(TextBlock block, PlatformUpdateRowViewModel row)
        {
            if (row.HasUpdate)
            {
                block.Foreground = TryBrush("AccentBrush") ?? new SolidColorBrush(Color.Parse("#16A34A"));
            }
            else
            {
                Themes.ThemeBrushes.Bind(block, TextBlock.ForegroundProperty, "TextPrimaryBrush");
            }
        }

        private static IBrush? TryBrush(string key)
        {
            if (Application.Current is not { } app || !app.TryFindResource(key, out var found))
                return null;
            return found as IBrush;
        }

        private void OnRowPropertyChanged(PlatformUpdateRowViewModel row, string propertyName)
        {
            if (propertyName == nameof(row.Progress))
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (_progressBars.TryGetValue(row, out var bar))
                        bar.Value = Math.Clamp(row.Progress, 0, 1);
                    if (_progressTexts.TryGetValue(row, out var text))
                        text.Text = $"{Math.Round(row.Progress * 100, 0):0} %";
                });
            }
            else if (propertyName == nameof(row.IsDownloading))
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (_progressBars.TryGetValue(row, out var bar))
                        bar.IsVisible = row.IsDownloading;
                    if (_progressTexts.TryGetValue(row, out var text))
                        text.IsVisible = row.IsDownloading;
                });
            }
            else if (propertyName == nameof(row.SizeText) || propertyName == nameof(row.StatusText))
            {
                // Обновляем тексты ячеек: перечитываем свойство из строки (паттерн ActualReleasesWindow).
            }
        }

        private Control BuildRoot()
        {
            var grid = new Grid { Margin = new Thickness(16) };
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            grid.RowDefinitions.Add(new RowDefinition(new GridLength(1, GridUnitType.Star)));
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            var title = new TextBlock
            {
                Text = T("PlatformUpdate.WindowTitle"),
                FontSize = 15,
                FontWeight = FontWeight.SemiBold
            };
            Grid.SetRow(title, 0);
            grid.Children.Add(title);

            // Карточка списка версий.
            var listBorder = new Border
            {
                Margin = new Thickness(0, 12, 0, 0),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6)
            };
            Themes.ThemeBrushes.Bind(listBorder, Border.BackgroundProperty, "CardBackgroundColorBrush");
            Themes.ThemeBrushes.Bind(listBorder, Border.BorderBrushProperty, "BorderColorBrush");

            var dock = new DockPanel { LastChildFill = true };

            var header = BuildHeaderGrid();
            DockPanel.SetDock(header, Dock.Top);
            dock.Children.Add(header);

            _rowsPanel.Margin = new Thickness(4, 2);
            var scroll = new ScrollViewer
            {
                Content = _rowsPanel,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(4)
            };
            dock.Children.Add(scroll);

            listBorder.Child = dock;
            Grid.SetRow(listBorder, 1);
            grid.Children.Add(listBorder);

            // Панель прогресса и журнала (видна при активной операции).
            _progressPanel = new StackPanel
            {
                Margin = new Thickness(0, 12, 0, 0),
                Spacing = 8,
                IsVisible = false
            };
            var progressBar = new ProgressBar { Minimum = 0, Maximum = 1, Height = 6 };
            progressBar.Bind(ProgressBar.ValueProperty, new Avalonia.Data.Binding(nameof(PlatformUpdateViewModel.Progress))
            {
                Source = _viewModel
            });
            _logBox = new TextBox
            {
                Height = 120,
                FontFamily = new FontFamily("Consolas, Courier New, monospace"),
                FontSize = 11,
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap
            };
            _logBox.Bind(TextBox.TextProperty, new Avalonia.Data.Binding(nameof(PlatformUpdateViewModel.LogText))
            {
                Source = _viewModel,
                Mode = Avalonia.Data.BindingMode.OneWay
            });
            Themes.ThemeBrushes.Bind(_logBox, TextBox.BackgroundProperty, "CardBackgroundColorBrush");
            Themes.ThemeBrushes.Bind(_logBox, TextBox.BorderBrushProperty, "BorderColorBrush");
            _progressPanel.Children.Add(progressBar);
            _progressPanel.Children.Add(_logBox);
            Grid.SetRow(_progressPanel, 2);
            grid.Children.Add(_progressPanel);

            // Нижняя панель (issue #334): две строки кнопок. Первая строка — «Проверить
            // обновления», «Учётки», «Открыть в браузере»; вторая — операции с версиями.
            // Кнопка «Закрыть» убрана: есть кнопка закрытия окна и ESC; «Выбрать файл
            // установщика» удалена как лишняя (issue #334).
            var bottom = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };

            var row1 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row1.Children.Add(MakeCommandButton(T("PlatformUpdate.Check"), _viewModel.CheckCommand, primary: true));

            // Действия при проблемах авторизации (issue #323/#330/#334): справочник
            // учётных данных ИТС, login.1c.ru в браузере.
            var itsAccounts = new Button { Content = T("Updates.OpenItsAccounts"), Height = 36 };
            itsAccounts.Styled(ControlThemes.SecondaryButton);
            itsAccounts.Click += (_, _) => OpenItsAccounts();
            row1.Children.Add(itsAccounts);

            var openLogin = new Button { Content = T("Updates.OpenLoginPage"), Height = 36 };
            openLogin.Styled(ControlThemes.SecondaryButton);
            openLogin.Click += (_, _) => OpenLogin();
            row1.Children.Add(openLogin);

            var row2 = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Margin = new Thickness(0, 8, 0, 0)
            };
            row2.Children.Add(MakeCommandButton(T("PlatformUpdate.DownloadInstall"), _viewModel.DownloadAndInstallCommand));
            row2.Children.Add(MakeCommandButton(T("PlatformUpdate.DownloadOnly"), _viewModel.DownloadOnlyCommand));
            row2.Children.Add(MakeCommandButton(T("PlatformUpdate.RemoveOld"), _viewModel.RemoveOldVersionsCommand));

            bottom.Children.Add(row1);
            bottom.Children.Add(row2);
            Grid.SetRow(bottom, 3);
            grid.Children.Add(bottom);

            return grid;
        }

        /// <summary>
        /// Диалог выбора удаляемых старых версий (issue #334): список версий с флажками;
        /// возвращает выбранные пользователем версии или null при отмене. Может
        /// вызываться из фонового потока — показ переводится в UI-поток.
        /// </summary>
        private IReadOnlyList<PlatformVersionInfo>? ChooseOldVersionsToDelete(
            IReadOnlyList<PlatformVersionInfo> candidates)
        {
            IReadOnlyList<PlatformVersionInfo>? result = null;
            if (Dispatcher.UIThread.CheckAccess())
            {
                var picker = new PlatformOldVersionsWindow(candidates);
                if (picker.ShowDialogSync(this))
                    result = picker.Result;
            }
            else
            {
                Dispatcher.UIThread.InvokeAsync(() =>
                {
                    var picker = new PlatformOldVersionsWindow(candidates);
                    if (picker.ShowDialogSync(this))
                        result = picker.Result;
                }).Wait();
            }

            return result;
        }

        /// <summary>Кнопка команды нижней панели: тема подтверждения для главной, вторичная — для остальных.</summary>
        private static Button MakeCommandButton(string text, System.Windows.Input.ICommand command, bool primary = false)
        {
            var button = new Button
            {
                Content = text,
                Height = 36,
                Command = command
            };
            button.Styled(primary ? ControlThemes.DialogConfirmButton : ControlThemes.SecondaryButton);
            return button;
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
    }
}
#endif