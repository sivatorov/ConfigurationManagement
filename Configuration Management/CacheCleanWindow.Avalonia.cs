#if LINUX
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls.Primitives;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Configuration_Management.Localization;
using Configuration_Management.Models;
using Configuration_Management.Services;

namespace Configuration_Management
{
    /// <summary>
    /// Диалог выбора типа очищаемого кэша 1С и набора информационных баз, для которых
    /// нужно выполнить очистку. Avalonia/Linux-версия WPF-окна <see cref="CacheCleanWindow"/>.
    /// </summary>
    public class CacheCleanWindow : ModalWindowBase
    {
        private readonly List<Infobase> _infobases;
        private readonly Dictionary<CheckBox, Infobase> _baseChecks = new();
        private readonly Dictionary<CheckBox, Grid> _baseRows = new();
        private readonly Dictionary<Infobase, TextBlock> _programSizeTexts = new();
        private readonly Dictionary<Infobase, TextBlock> _userSizeTexts = new();

        private readonly TextBlock _programCacheSizeText = new();
        private readonly TextBlock _userCacheSizeText = new();
        private readonly TextBlock _orphanCacheSizeText = new();
        private readonly CheckBox _programCacheCheck = new();
        private readonly CheckBox _userCacheCheck = new();
        private readonly CheckBox _orphanCacheCheck = new();
        private readonly TextBox _searchBox = new() { Padding = new Thickness(10, 7), Watermark = LocalizationManager.T("CacheClean.SearchBase") };
        private readonly StackPanel _basesPanel = new();
        private readonly TextBlock _basesCountText = new();
        private readonly Button _cleanButton = new() { IsDefault = true };

        // Ширина изменяемых колонок списка.
        private const double DefaultProgramWidth = 130;
        private const double DefaultUserWidth = 130;
        private const double MinColumnWidth = 40;

        private readonly Services.IInfobaseRepository _repository =
            AppServices.GetRequiredService<Services.IInfobaseRepository>();

        private double _nameColumnWidth;             // 0 — колонка «База» растягивается
        private double _programColumnWidth = DefaultProgramWidth;
        private double _userColumnWidth = DefaultUserWidth;
        private Grid? _headerGrid;
        private readonly List<Grid> _rows = new();

        // Состояние перетаскивания разделителя (как в главном окне).
        private bool _isResizing;
        private int _resizeColumn = -1;
        private double _resizeStartWidth;
        private double _resizeStartX;

        /// <param name="infobases">Все доступные информационные базы.</param>
        /// <param name="initialKind">Изначально выбранный тип кэша.</param>
        /// <param name="defaultSelected">База, выбранная по умолчанию (например, выделенная в главном окне).</param>
        public CacheCleanWindow(IEnumerable<Infobase> infobases, OneCCacheKind initialKind, Infobase? defaultSelected = null)
        {
            Title = LocalizationManager.T("CacheClean.Title");
            Width = 580;
            Height = 540;
            MinWidth = 480;
            MinHeight = 500;
            CanResize = true;

            _infobases = infobases.ToList();

            _programCacheCheck.Content = BuildCacheTypeContent(LocalizationManager.T("CacheClean.ProgramCache"), _programCacheSizeText);
            _userCacheCheck.Content = BuildCacheTypeContent(LocalizationManager.T("CacheClean.UserCache"), _userCacheSizeText);
            _orphanCacheCheck.Content = BuildCacheTypeContent(LocalizationManager.T("CacheClean.OrphanCache"), _orphanCacheSizeText);
            ToolTip.SetTip(_orphanCacheCheck, LocalizationManager.T("CacheClean.OrphanCacheTooltip"));

            _programCacheCheck.IsChecked = initialKind.HasFlag(OneCCacheKind.Program);
            _userCacheCheck.IsChecked = initialKind.HasFlag(OneCCacheKind.User);
            _programCacheCheck.Checked += (_, _) => UpdateCleanEnabled();
            _programCacheCheck.Unchecked += (_, _) => UpdateCleanEnabled();
            _userCacheCheck.Checked += (_, _) => UpdateCleanEnabled();
            _userCacheCheck.Unchecked += (_, _) => UpdateCleanEnabled();
            _orphanCacheCheck.Checked += (_, _) => UpdateCleanEnabled();
            _orphanCacheCheck.Unchecked += (_, _) => UpdateCleanEnabled();
            _searchBox.TextChanged += (_, _) => OnSearchTextChanged();

            LoadColumnWidths();

            BuildBasesList(defaultSelected);
            UpdateCount();
            UpdateCleanEnabled();

            // Внешний ScrollViewer гарантирует доступность всех элементов при любой высоте
            // окна: если суммарная высота контента превышает высоту окна, появляется
            // вертикальная прокрутка всего содержимого, а не обрезка нижней панели.
            Content = new ScrollViewer
            {
                Content = BuildRoot(),
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };

            Opened += (_, _) => RefreshCacheSizes();
            Closing += (_, _) => SaveColumnWidths();
        }

        /// <summary>Загружает сохранённые ширины колонок из настроек приложения.</summary>
        private void LoadColumnWidths()
        {
            try
            {
                var settings = _repository.LoadSettings();
                _nameColumnWidth = settings.CacheCleanBaseColumnWidth;
                if (settings.CacheCleanProgramColumnWidth > 0)
                    _programColumnWidth = settings.CacheCleanProgramColumnWidth;
                if (settings.CacheCleanUserColumnWidth > 0)
                    _userColumnWidth = settings.CacheCleanUserColumnWidth;
            }
            catch
            {
                // Игнорируем ошибки загрузки — используем значения по умолчанию.
            }
        }

        /// <summary>Сохраняет текущие ширины колонок в настройки приложения.</summary>
        private void SaveColumnWidths()
        {
            try
            {
                var settings = _repository.LoadSettings();
                settings.CacheCleanBaseColumnWidth = _nameColumnWidth;
                settings.CacheCleanProgramColumnWidth = _programColumnWidth;
                settings.CacheCleanUserColumnWidth = _userColumnWidth;
                _repository.SaveSettings(settings);
            }
            catch
            {
                // Игнорируем ошибки сохранения.
            }
        }

        /// <summary>Тип кэша, выбранный пользователем.</summary>
        public OneCCacheKind SelectedCacheKind { get; private set; } = OneCCacheKind.None;

        /// <summary>Список баз, выбранных для очистки.</summary>
        public IReadOnlyList<Infobase> SelectedInfobases { get; private set; } = Array.Empty<Infobase>();

        /// <summary>Признак того, что нужно дополнительно очистить «остатки» кеша от удалённых баз.</summary>
        public bool CleanOrphans { get; private set; }

        /// <summary>
        /// Показывает окно модально (синхронно) и возвращает результат диалога.
        /// Открытая публичная обёртка над <see cref="ModalWindowBase.ShowDialogSync()"/>,
        /// чтобы диалог можно было вызывать из ViewModel (не наследника окна).
        /// </summary>
        /// <returns>True, если пользователь подтвердил очистку.</returns>
        public bool ShowSync() => ShowDialogSync();

        /// <summary>
        /// Формирует содержимое чекбокса типа кеша: название и поле текущего размера.
        /// </summary>
        private static Control BuildCacheTypeContent(string name, TextBlock sizeText)
        {
            sizeText.VerticalAlignment = VerticalAlignment.Center;
            sizeText.FontSize = 12;
            sizeText.Foreground = Brushes.Gray;
            sizeText.Text = "…";

            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center },
                    sizeText
                }
            };
            return panel;
        }

        /// <summary>
        /// Вычисляет и отображает размеры программного и пользовательского кеша.
        /// Расчёт выполняется в фоновом потоке, чтобы не блокировать интерфейс.
        /// </summary>
        private async void RefreshCacheSizes()
        {
            _programCacheSizeText.Text = "…";
            _userCacheSizeText.Text = "…";
            _orphanCacheSizeText.Text = "…";
            foreach (var t in _programSizeTexts.Values) t.Text = "…";
            foreach (var t in _userSizeTexts.Values) t.Text = "…";

            var program = await Task.Run(() => OneCCacheCleaner.GetSize(OneCCacheKind.Program));
            var user = await Task.Run(() => OneCCacheCleaner.GetSize(OneCCacheKind.User));
            var orphans = await Task.Run(() => OneCCacheCleaner.GetOrphanSize(OneCCacheKind.All, _infobases));

            _programCacheSizeText.Text = FormatSize(program);
            _userCacheSizeText.Text = FormatSize(user);
            _orphanCacheSizeText.Text = FormatSize(orphans);

            foreach (var ib in _infobases)
            {
                var p = await Task.Run(() => OneCCacheCleaner.GetSize(ib, OneCCacheKind.Program));
                var u = await Task.Run(() => OneCCacheCleaner.GetSize(ib, OneCCacheKind.User));
                if (_programSizeTexts.TryGetValue(ib, out var pt)) pt.Text = FormatSize(p);
                if (_userSizeTexts.TryGetValue(ib, out var ut)) ut.Text = FormatSize(u);
            }
        }

        /// <summary>
        /// Форматирует размер в байтах в человекочитаемый вид с локализованными единицами.
        /// </summary>
        private static string FormatSize(long bytes)
        {
            var units = LocalizationManager.T("CacheClean.SizeUnits")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            double value = bytes;
            var index = 0;
            while (value >= 1024 && index < units.Length - 1)
            {
                value /= 1024;
                index++;
            }

            var number = index == 0 ? value.ToString("0") : value.ToString("0.0");
            return $"{number} {units[index]}";
        }

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
                Text = LocalizationManager.T("CacheClean.TitleHeader"),
                FontSize = 15,
                FontWeight = FontWeight.SemiBold
            };
            Grid.SetRow(title, 0);
            grid.Children.Add(title);

            var description = new TextBlock
            {
                Text = LocalizationManager.T("CacheClean.Subtitle"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0)
            };
            Grid.SetRow(description, 1);
            grid.Children.Add(description);

            var typeLabel = new TextBlock
            {
                Text = LocalizationManager.T("CacheClean.CacheTypeLabel"),
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, 16, 0, 6)
            };
            Grid.SetRow(typeLabel, 2);
            grid.Children.Add(typeLabel);

            var typePanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            ToolTip.SetTip(_programCacheCheck, "%LOCALAPPDATA%\\1C\\1cv8…");
            ToolTip.SetTip(_userCacheCheck, "%APPDATA%\\1C\\1cv8…");
            typePanel.Children.Add(_programCacheCheck);
            typePanel.Children.Add(_userCacheCheck);
            Grid.SetRow(typePanel, 3);
            grid.Children.Add(typePanel);

            // Список баз
            var basesBorder = new Border
            {
                Margin = new Thickness(0, 12, 0, 0),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                // Фиксированная высота вместо Star-строки: внутри внешнего ScrollViewer
                // звёздные строки схлопываются в 0, поэтому блоку списка задаём постоянную
                // высоту с MinHeight. Внутренний ScrollViewer базы остаётся рабочим, а при
                // малой высоте окна весь контент прокручивается внешним ScrollViewer —
                // нижняя панель (чекбокс остатков и кнопки) никогда не обрезается.
                Height = 260,
                MinHeight = 220
            };

            var dock = new DockPanel { LastChildFill = true };

            _searchBox.Margin = new Thickness(8, 8, 8, 2);
            DockPanel.SetDock(_searchBox, Dock.Top);
            dock.Children.Add(_searchBox);

            var toolbar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 12,
                Margin = new Thickness(8, 2, 8, 4)
            };
            var selectAll = new Button { Content = IconHelper.IconAndText("IconCheck", LocalizationManager.T("CacheClean.SelectAll")), Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
            selectAll.Click += (_, _) => { foreach (var check in _baseChecks.Keys) check.IsChecked = true; UpdateCount(); UpdateCleanEnabled(); };
            var clearAll = new Button { Content = IconHelper.IconAndText("IconUncheck", LocalizationManager.T("CacheClean.ClearAll")), Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
            clearAll.Click += (_, _) => { foreach (var check in _baseChecks.Keys) check.IsChecked = false; UpdateCount(); UpdateCleanEnabled(); };
            toolbar.Children.Add(selectAll);
            toolbar.Children.Add(clearAll);
            DockPanel.SetDock(toolbar, Dock.Top);
            dock.Children.Add(toolbar);

            // Закреплённая шапка списка (остаётся вверху при прокрутке).
            _headerGrid = BuildHeaderGrid();
            DockPanel.SetDock(_headerGrid, Dock.Top);
            dock.Children.Add(_headerGrid);

            var basesScroll = new ScrollViewer
            {
                Content = _basesPanel,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(8, 2)
            };
            dock.Children.Add(basesScroll);

            basesBorder.Child = dock;
            Grid.SetRow(basesBorder, 4);
            grid.Children.Add(basesBorder);

            // Нижняя панель: счётчик + кнопки
            var bottom = new Grid { Margin = new Thickness(0, 12, 0, 0) };
            bottom.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            bottom.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            bottom.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

            var leftPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 16,
                VerticalAlignment = VerticalAlignment.Center
            };
            leftPanel.Children.Add(_orphanCacheCheck);
            _basesCountText.VerticalAlignment = VerticalAlignment.Center;
            leftPanel.Children.Add(_basesCountText);
            Grid.SetColumn(leftPanel, 0);
            bottom.Children.Add(leftPanel);

            var cancel = new Button { Content = LocalizationManager.T("Common.Cancel"), MinWidth = 100, IsCancel = true };
            cancel.Click += (_, _) => Close();
            Grid.SetColumn(cancel, 1);
            cancel.Margin = new Thickness(0, 0, 8, 0);
            bottom.Children.Add(cancel);

            _cleanButton.Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    IconHelper.MakeIcon("IconDelete", 16),
                    new TextBlock { Text = LocalizationManager.T("CacheClean.Clean"), VerticalAlignment = VerticalAlignment.Center }
                }
            };
            _cleanButton.MinWidth = 130;
            _cleanButton.Click += (_, _) => OnClean_Click();
            Grid.SetColumn(_cleanButton, 2);
            bottom.Children.Add(_cleanButton);

            Grid.SetRow(bottom, 5);
            grid.Children.Add(bottom);

            return grid;
        }

        /// <summary>
        /// Возвращает GridLength для колонки: «База» (индекс 0) при нулевой ширине
        /// растягивается на всё свободное место, остальные — фиксированной ширины.
        /// </summary>
        private static GridLength ColLength(int column, double width)
            => column == 0 && width <= 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(width);

        /// <summary>
        /// Применяет к сетке общую раскладку колонок: 0 — имя базы, 1 — программный, 2 — пользовательский.
        /// </summary>
        private void ApplyColumns(Grid grid)
        {
            grid.ColumnDefinitions.Clear();
            grid.ColumnDefinitions.Add(new ColumnDefinition(ColLength(0, _nameColumnWidth)) { MinWidth = MinColumnWidth });
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(_programColumnWidth)) { MinWidth = MinColumnWidth });
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(_userColumnWidth)) { MinWidth = MinColumnWidth });
        }

        /// <summary>Строит закреплённую шапку списка с зонами захвата для изменения ширины колонок.</summary>
        private Grid BuildHeaderGrid()
        {
            var grid = new Grid { Margin = new Thickness(8, 0, 8, 4) };
            ApplyColumns(grid);

            grid.Children.Add(BuildHeaderText(LocalizationManager.T("CacheClean.ColumnBase"), HorizontalAlignment.Left, 0));
            grid.Children.Add(BuildHeaderText(LocalizationManager.T("CacheClean.ColumnProgramSize"), HorizontalAlignment.Right, 1));
            grid.Children.Add(BuildHeaderText(LocalizationManager.T("CacheClean.ColumnUserSize"), HorizontalAlignment.Right, 2));

            // Зоны захвата для изменения ширины каждой колонки (как в главном окне).
            for (var col = 0; col < 3; col++)
                grid.Children.Add(BuildResizeGrip(col));

            return grid;
        }

        /// <summary>Создаёт зону захвата на правой границе колонки (тонкая полоса + широкий захват).</summary>
        private Border BuildResizeGrip(int column)
        {
            var grip = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Stretch,
                Width = 8,
                ZIndex = 1,
                Background = Brushes.Transparent,
                Cursor = new Cursor(StandardCursorType.SizeWestEast)
            };
            ToolTip.SetTip(grip, LocalizationManager.T("CacheClean.ResizeColumnTooltip"));
            Grid.SetColumn(grip, column);
            grip.PointerPressed += OnResize_PointerPressed;
            grip.PointerMoved += OnResize_PointerMoved;
            grip.PointerReleased += OnResize_PointerReleased;
            return grip;
        }

        private void OnResize_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is not Border grip || _headerGrid is null)
                return;

            var column = Grid.GetColumn(grip);
            if (column < 0 || column >= _headerGrid.ColumnDefinitions.Count)
                return;

            _resizeColumn = column;
            _resizeStartWidth = _headerGrid.ColumnDefinitions[column].ActualWidth;
            _resizeStartX = e.GetPosition(this).X;
            _isResizing = true;
            e.Pointer.Capture(grip);
            e.Handled = true;
        }

        private void OnResize_PointerMoved(object? sender, PointerEventArgs e)
        {
            if (!_isResizing || _resizeColumn < 0 || _headerGrid is null)
                return;

            var newWidth = _resizeStartWidth + (e.GetPosition(this).X - _resizeStartX);
            if (newWidth < MinColumnWidth)
                newWidth = MinColumnWidth;

            SetColumnWidth(_resizeColumn, newWidth);
        }

        private void OnResize_PointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (sender is Border)
                e.Pointer.Capture(null);

            if (_isResizing)
            {
                _isResizing = false;
                _resizeColumn = -1;
                SaveColumnWidths();
            }
        }

        /// <summary>
        /// Применяет новую ширину только к целевой колонке — и в шапке, и во всех строках
        /// (заголовок и данные изменяются синхронно, как в главном окне).
        /// </summary>
        private void SetColumnWidth(int column, double width)
        {
            width = Math.Max(MinColumnWidth, width);

            if (column == 0) _nameColumnWidth = width;
            else if (column == 1) _programColumnWidth = width;
            else if (column == 2) _userColumnWidth = width;

            if (_headerGrid is not null)
                _headerGrid.ColumnDefinitions[column].Width = ColLength(column, width);

            foreach (var row in _rows)
                row.ColumnDefinitions[column].Width = ColLength(column, width);
        }

        private void BuildBasesList(Infobase? defaultSelected)
        {
            _basesPanel.Children.Clear();
            _baseChecks.Clear();
            _baseRows.Clear();
            _programSizeTexts.Clear();
            _userSizeTexts.Clear();
            _rows.Clear();

            foreach (var ib in _infobases)
            {
                var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                ApplyColumns(row);

                var check = new CheckBox
                {
                    Content = ib.Name,
                    IsChecked = ReferenceEquals(ib, defaultSelected),
                    VerticalContentAlignment = VerticalAlignment.Center
                };
                ToolTip.SetTip(check, string.IsNullOrWhiteSpace(ib.ConnectionPathDisplay) ? ib.Name : ib.ConnectionPathDisplay);
                check.Checked += (_, _) => OnBaseChecked();
                check.Unchecked += (_, _) => OnBaseChecked();
                Grid.SetColumn(check, 0);
                row.Children.Add(check);

                var programSize = BuildSizeText();
                Grid.SetColumn(programSize, 1);
                row.Children.Add(programSize);

                var userSize = BuildSizeText();
                Grid.SetColumn(userSize, 2);
                row.Children.Add(userSize);

                _baseChecks[check] = ib;
                _baseRows[check] = row;
                _programSizeTexts[ib] = programSize;
                _userSizeTexts[ib] = userSize;
                _rows.Add(row);
                _basesPanel.Children.Add(row);
            }
        }

        /// <summary>Формирует заголовок колонки списка баз.</summary>
        private static TextBlock BuildHeaderText(string text, HorizontalAlignment align, int column)
        {
            var block = new TextBlock
            {
                Text = text,
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                Foreground = Brushes.Gray,
                HorizontalAlignment = align,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 8, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(block, column);
            return block;
        }

        /// <summary>Формирует поле отображения размера кеша базы.</summary>
        private static TextBlock BuildSizeText()
        {
            return new TextBlock
            {
                Text = "…",
                FontSize = 12,
                Foreground = Brushes.Gray,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 8, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
        }

        private void OnSearchTextChanged()
        {
            var query = _searchBox.Text?.Trim() ?? string.Empty;
            foreach (var kv in _baseChecks)
            {
                var visible = query.Length == 0
                    || kv.Value.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || (kv.Value.ConnectionPathDisplay?.Contains(query, StringComparison.OrdinalIgnoreCase) == true);
                if (_baseRows.TryGetValue(kv.Key, out var row))
                    row.IsVisible = visible;
            }
        }

        private void OnBaseChecked()
        {
            UpdateCount();
            UpdateCleanEnabled();
        }

        private void UpdateCount()
        {
            var selected = _baseChecks.Count(kv => kv.Key.IsChecked == true);
            var total = _baseChecks.Count;
            _basesCountText.Text = string.Format(LocalizationManager.T("CacheClean.CountSelected"), selected, total);
        }

        private void UpdateCleanEnabled()
        {
            var hasType = _programCacheCheck.IsChecked == true || _userCacheCheck.IsChecked == true;
            var hasBases = _baseChecks.Any(kv => kv.Key.IsChecked == true);
            var hasOrphans = _orphanCacheCheck.IsChecked == true;
            _cleanButton.IsEnabled = hasType && (hasBases || hasOrphans);
        }

        private void OnClean_Click()
        {
            var kind = OneCCacheKind.None;
            if (_programCacheCheck.IsChecked == true)
                kind |= OneCCacheKind.Program;
            if (_userCacheCheck.IsChecked == true)
                kind |= OneCCacheKind.User;

            CleanOrphans = _orphanCacheCheck.IsChecked == true;
            SelectedCacheKind = kind;
            SelectedInfobases = _baseChecks
                .Where(kv => kv.Key.IsChecked == true)
                .Select(kv => kv.Value)
                .ToList();

            DialogResult = true;
            Close();
        }
    }
}
#endif