#if LINUX
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using Avalonia.Controls.Presenters;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Controls.Shapes;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Utilities;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Configuration_Management.Controls;
using Configuration_Management.Localization;
using Configuration_Management.Models;
using Configuration_Management.Themes;
using Configuration_Management.ViewModels;

namespace Configuration_Management
{
    /// <summary>
    /// Avalonia-версия главного окна (Linux). Собирается в коде (без XAML-компилятора),
    /// чтобы гарантировать компиляцию без Linux-SDK. Реализует: верхнюю панель (группы,
    /// поиск, вкладки Все/Избранное/Недавние, синхронизация, тема, настройки), дерево
    /// списка баз, правую панель (карточка базы + действия), нижнюю панель статуса и трей.
    /// </summary>
    public class MainWindow : Window
    {
        private MainViewModel? _vm;
        private TextBox _searchBox = null!;
        private TextBlock _statusInfo = null!;
        private TextBlock _syncMessage = null!;
        private LeveledTreeView _tree = null!;

        // Поля empty-state (заглушка пустого списка / «ничего не найдено»).
        private Border _emptyState = null!;
        private Avalonia.Controls.Shapes.Path _emptyIcon = null!;
        private Control _emptyIconHost = null!;
        private TextBlock _emptyTitle = null!;
        private SegmentButton? _tagsToggle;
        private SegmentButton? _emptyGroupsToggle;
        private SegmentButton? _groupByToggle;
        private SegmentButton? _compactToggle;
        private Border? _commandPanel;
        private Border? _columnHeader;
        private Grid? _columnHeaderRow;
        private ColumnDefinition? _headerOffsetColumn;
        private Grid? _listContent;
        /// <summary>Шаг прокрутки колесом, как у штатного ScrollContentPresenter.</summary>
        private const double WheelScrollStep = 50;

        private ScrollBar? _listVerticalBar;
        private ScrollViewer? _listScroll;
        private ScrollViewer? _boundTreeScroll;
        private ScrollBar? _boundScrollBar;
        private readonly List<IDisposable> _scrollBarLinks = new();
        private bool _syncingScrollBar;
        private bool _columnHeaderRefreshQueued;
        private bool _headerAlignQueued;
        private readonly Dictionary<string, int> _headerColumnIndex = new(StringComparer.Ordinal);
        private object? _dragPayload;
        private Point _dragStartPoint;
        private bool _isDragging;
        private string? _resizeKey;
        private int _resizePointerId;
        private readonly List<Grid> _resizeRowGrids = new();
        private double _resizeStartWidth;
        private double _resizeStartX;
        private Border? _tagPanel;
        private WrapPanel? _tagPanelItems;
        private Button? _tagClearButton;
        private TextBlock _emptyHint = null!;

        /// <summary>
        /// Если true — закрытие окна уводит приложение в трей (а не завершает).
        /// Сбрасывается командой «Выход» из трея перед Shutdown.
        /// </summary>
        private bool _allowCloseToTray = true;

        // Текущий режим системного заголовка (issue #159). Держится полем, чтобы при
        // пересборке содержимого (ApplySystemTitleBar) знать, рисовать ли собственную
        // шапку с кнопками окна и зоны изменения размера, или их даёт системная рамка.
        private bool _useSystemTitleBar;

        // Непрозрачный режим окна (issue #153): прозрачность на X11 с программным
        // рендером/в виртуализации заставляет WM непрерывно перерисовывать фон.
        // В этом режиме «стеклянная» подложка рисуется полностью непрозрачной.
        private readonly bool _opaqueWindow;

        public MainWindow(MainViewModel viewModel)
        {
            _vm = viewModel;

            Title = ComposeWindowTitle();
            // Значок в заголовке окна — тот же app.ico, что и у приложения и трея.
            Icon = Services.AppIconLoader.LoadAppIcon();
            Width = 1200;
            Height = 760;
            MinWidth = 900;
            MinHeight = 600;

            // По настройке можно вернуть стандартный системный заголовок, как в Windows
            // (issue #152). По умолчанию — собственный безрамковый: отказываемся от системных
            // кнопок и рамки в пользу собственных (свернуть/развернуть/закрыть), рисуемых
            // в коде. Перетаскивание реализовано за фон верхней панели (BeginMoveDrag),
            // изменение размера — угловыми и краевыми зонами (BeginResizeDrag).
            _useSystemTitleBar = viewModel.UseSystemTitleBar;
            // На X11 с программным рендером или в виртуализации прозрачность окна заставляет
            // оконный менеджер непрерывно перерисовывать фон, что при простаивающем окне даёт
            // высокую нагрузку CPU и «зависание» реакции на мышь (issue #153). Окно рисуется
            // непрозрачным во всех таких случаях (X11 без композитинга, виртуализация, любой
            // программный рендер); прозрачное «стекло» оставляется только на Wayland, где
            // композитор обязателен и постоянной перерисовки фона нет. Определение собрано
            // в одном месте — Services.LinuxRendering (учитывает и ручную переменную
            // CM_DISABLE_TRANSPARENCY=1, issue #153).
            _opaqueWindow = _useSystemTitleBar || Services.LinuxRendering.OpaqueWindow;
            ApplySystemDecorations();

            ApplySavedWindowLayout();

            DataContext = viewModel;

            Content = BuildRoot();
            Loaded += OnWindowLoaded;
            KeyDown += OnWindowKeyDown;

            // Шапка окна реагирует на активность: акцентная заливка у активного окна,
            // цвет карточки у неактивного (MainWindow.xaml.cs:78-79).
            Activated += (_, _) => ApplyTitleBarAppearance(true);
            Deactivated += (_, _) => ApplyTitleBarAppearance(false);

            // Геометрия обычного состояния запоминается на ходу: у Avalonia нет
            // аналога RestoreBounds, а развёрнутое окно надо сохранять размером,
            // к которому оно вернётся.
            PositionChanged += (_, _) => RememberNormalBounds();
            SizeChanged += (_, _) => RememberNormalBounds();

            // Действие после запуска базы или конфигуратора по глобальной настройке.
            _vm.AfterLaunchRequested += OnAfterLaunchRequested;

            // Подписка здесь, а не в построении содержимого: компактный режим
            // пересобирает содержимое, и обработчики копились бы на каждый показ.
            _vm.TraySettingsChanged += ApplyTrayVisibility;
            _vm.TreeRebuilding += RememberTreeScroll;
            _vm.TreeRebuilt += RestoreTreeSelection;

            // Смена языка интерфейса: названия колонок, кнопки правой панели и подсказки
            // создаются в коде через LocalizationManager.T(...), поэтому окно пересобирается,
            // чтобы переведённый текст появился сразу, а не после перезапуска.
            LocalizationManager.Instance.LanguageChanged += OnLanguageChanged;
        }

        /// <summary>Шапка окна: полоса, подпись и кнопки, перекрашиваемые по активности.</summary>
        private Border? _titleBarBorder;
        private TextBlock? _appTitleText;
        private IDisposable? _appTitleSub;
        private IDisposable? _glassCornerSub;
        private IDisposable? _titleBackgroundSub;
        private IDisposable? _titleForegroundSub;
        private readonly List<WindowControlButton> _windowControlButtons = new();

        /// <summary>Значок трея создан без ошибки: значение проверяется перед тем, как прятать окно.</summary>
        private bool _trayIconCreated;

        /// <summary>Ссылка на значок трея, чтобы обновлять меню при смене языка.</summary>
        private TrayIcon? _trayIcon;
        private NativeMenu? _trayMenu;
        private string? _traySignature;
        private bool _trayRefreshQueued;
        private NotifyCollectionChangedEventHandler? _groupNodesChanged;
        private NotifyCollectionChangedEventHandler? _flatItemsChanged;
        private EventHandler? _tagFiltersRebuilt;
        private PropertyChangedEventHandler? _vmPropertyChanged;

        /// <summary>
        /// Можно ли вернуть спрятанное окно. Значок трея это первый путь, но
        /// на GNOME Shell без AppIndicator он не появится, а ошибки при этом не
        /// будет. Второй путь, повторный запуск приложения через файл-сигнал,
        /// работает только пока включён режим единственного экземпляра.
        /// Берётся состояние текущего процесса, а не настройка: блокировка
        /// и слушатель сигнала заводятся один раз при старте, и снятый на ходу
        /// флажок «несколько экземпляров» пути возврата не создаёт.
        /// </summary>
        private bool CanRestoreHiddenWindow =>
            (_trayIconCreated && TrayIconWanted && !Services.LinuxDesktopEnvironment.TrayMayBeUnavailable)
            || App.SingleInstanceActive;

        /// <summary>Нужен ли значок по настройкам: сам значок либо закрытие в трей.</summary>
        private bool TrayIconWanted => _vm is null || _vm.ShowTrayIcon || _vm.CloseToTray;

        /// <summary>
        /// Уводит окно в трей после успешного запуска, как это делает WPF-версия
        /// для обоих значений настройки. Выполняется через диспетчер: запрос
        /// приходит из обработчика команды, а окно к этому моменту ещё показывает
        /// нажатую кнопку.
        /// </summary>
        private void OnAfterLaunchRequested(Models.AfterLaunchAction action)
        {
            if (action == Models.AfterLaunchAction.None)
                return;

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                // «Свернуть» — просто свернуть окно в панель задач, не уводя его в трей (issue #201).
                if (action == Models.AfterLaunchAction.Minimize)
                {
                    WindowState = WindowState.Minimized;
                    return;
                }

                // Спрятанное окно живёт только в трее, поэтому без пути возврата
                // оно сворачивается: иначе пользователь остался бы с работающим
                // процессом, который нечем показать.
                if (!CanRestoreHiddenWindow)
                {
                    WindowState = WindowState.Minimized;
                    _vm?.LogWarning(LocalizationManager.T("Main.AfterLaunchTrayUnavailable"));
                    return;
                }

                SaveWindowLayout();
                Hide();
                if (action == Models.AfterLaunchAction.MinimizeToTray
                    && WindowState == WindowState.Minimized)
                    WindowState = WindowState.Normal;
            });
        }

        // ======================= Построение UI =======================

        private Control BuildRoot()
        {
            // Панель фильтра тегов живёт внутри левой колонки, а не отдельной
            // строкой окна (MainWindow.xaml:341-372): иначе при её показе вниз
            // уезжала и правая панель, чего в версии для Windows не происходит.
            var grid = new Grid();
            // Строка заголовка окна, содержимое, строка состояния. Верхняя панель
            // поиска не выделяется отдельной полноширинной строкой: она живёт только
            // над левой колонкой внутри основной области (см. BuildMainArea), как
            // в WPF (MainWindow.xaml:264). Раньше она тянулась на всю ширину окна
            // и опускала правую панель вниз лишним отступом сверху, которого нет
            // в Windows-версии (issue #221).
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Star });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Собственная безрамковая шапка с кнопками окна нужна только в безрамковом
            // режиме: при системной рамке она не строится, чтобы не было двух заголовков
            // (issue #159). Соответственно, строка 0 остаётся пустой нулевой высоты.
            if (!_useSystemTitleBar)
                grid.Children.Add(BuildTitleBar());
            var mainArea = BuildMainArea();
            var statusBar = BuildStatusBar();

            Grid.SetRow(mainArea, 1);
            Grid.SetRow(statusBar, 2);

            grid.Children.Add(mainArea);
            grid.Children.Add(statusBar);

            // Затемняющий индикатор фоновой работы поверх всего окна
            // (MainWindow.xaml:2349): карточка с подписью и полосой прогресса.
            var overlay = BuildLoadingOverlay();
            Grid.SetRow(overlay, 0);
            Grid.SetRowSpan(overlay, grid.RowDefinitions.Count);
            overlay.ZIndex = 1000;
            grid.Children.Add(overlay);

            // Без системной рамки изменение размера рисуем сами: невидимые зоны
            // по краям и углам окна перехватывают нажатие и вызывают BeginResizeDrag.
            // При системной рамке размер меняет сама система, зоны не нужны.
            if (!_useSystemTitleBar)
                AddResizeZones(grid);

            // «Стеклянный» контейнер: скруглённые углы в стиле glass и полупрозрачный
            // фон, адаптивно получаемый из цвета темы (светлая/тёмная и любые схемы).
            // Если WM не дал Acrylic/Blur (вернулся Transparent) — остаётся просто
            // полупрозрачный фон без размытия, окно остаётся рабочим и красивым.
            var glass = new Border
            {
                CornerRadius = new CornerRadius(UiMetrics.RadiusLg),
                ClipToBounds = true
            };
            ApplyGlassBackground(glass);
            grid.ClipToBounds = true;
            glass.Child = grid;

            // В развёрнутом виде скругление убираем: в углах окна не должно
            // просвечивать содержимое рабочего стола под рамкой соседних окон.
            // WindowStateObserver берёт Action без параметра и читает состояние сам.
            // Подписка живёт на окне, а содержимое пересобирается при смене языка
            // и компактного режима: без освобождения каждая пересборка укореняла бы
            // прежнее дерево целиком вместе с шапкой и кнопками.
            _glassCornerSub?.Dispose();
            _glassCornerSub = this.GetObservable(WindowStateProperty)
                .Subscribe(new WindowStateObserver(() =>
                    glass.CornerRadius = WindowState == WindowState.Maximized
                        ? new CornerRadius(0)
                        : new CornerRadius(UiMetrics.RadiusLg)));

            return glass;
        }

        /// <summary>
        /// Альфа полупрозрачной «стеклянной» подложки: ~91% непрозрачности сохраняет
        /// контраст текста, но при этом сквозь неё проступает acrylic/размытие фона.
        /// </summary>
        private const byte GlassBackgroundAlpha = 0xE8;

        /// <summary>
        /// Подписка стеклянного контейнера на цвет фона темы: берём текущий
        /// <c>ContentBackgroundColorBrush</c> и делаем из него полупрозрачную версию,
        /// чтобы обе темы и все цветовые схемы выглядели как «стекло» своего цвета.
        /// </summary>
        private void ApplyGlassBackground(Border glass)
        {
            // В непрозрачном режиме (программный рендер/виртуализация, issue #153) фон
            // рисуем полностью непрозрачным: полупрозрачная подложка поверх непрозрачного
            // окна в этих окружениях тоже способна включать лишнюю компоновку кадра.
            var alpha = _opaqueWindow ? (byte)0xFF : GlassBackgroundAlpha;
            ThemeBrushes.Observe(glass, "ContentBackgroundColorBrush",
                brush => glass.Background = ThemeBrushes.WithAlpha(brush, alpha));
        }

        private Control BuildTopBar()
        {
            // Отступ у внешней рамки, а не здесь: задавать его в обоих местах
            // значило удвоить его против разметки (MainWindow.xaml:157).
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 200 });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Слева: сегментные переключатели групп и тегов (с иконками и состояниями).
            var left = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };

            _groupByToggle = MakeSegmentToggle("IconFolder", LocalizationManager.T("Main.ToggleGroups"));
            _groupByToggle.IsChecked = _vm?.GroupByGroup ?? true;
            _groupByToggle.Click += (_, _) => { if (_vm is not null) _vm.GroupByGroup = _groupByToggle.IsChecked == true; };
            left.Children.Add(_groupByToggle);

            // Показывать пустые группы: у автора этот переключатель виден только
            // при включённой группировке (Visibility по GroupByGroup), иначе он
            // висел бы в негруппированном списке без дела.
            _emptyGroupsToggle = MakeSegmentToggle("IconFolderOutline",
                LocalizationManager.T("Settings.Panels.ShowEmptyGroups"));
            _emptyGroupsToggle.IsChecked = _vm?.ShowEmptyGroups ?? false;
            _emptyGroupsToggle.Click += (_, _) =>
            {
                if (_vm is not null)
                    _vm.ShowEmptyGroups = _emptyGroupsToggle.IsChecked == true;
            };
            _emptyGroupsToggle.Bind(Control.IsVisibleProperty, new Binding("GroupByGroup"));
            left.Children.Add(_emptyGroupsToggle);

            // Подсказка подробная, как в разметке WPF (MainWindow.xaml:194): этот
            // переключатель управляет и панелью тегов сверху, и тегами в списке,
            // в отличие от переключателя в шапке списка.
            _tagsToggle = MakeSegmentToggle("IconTag", LocalizationManager.T("Main.ToggleTagsFull"));
            _tagsToggle.IsChecked = _vm?.ShowTagFilterPanel ?? true;
            _tagsToggle.Click += (_, _) => { if (_vm is not null) _vm.ShowTagFilterPanel = _tagsToggle.IsChecked == true; };
            left.Children.Add(_tagsToggle);

            grid.Children.Add(left);
            Grid.SetColumn(left, 0);

            // Поиск: скруглённое поле с иконкой слева, кнопкой очистки справа и hover-подсветкой.
            var search = BuildSearchBox();
            grid.Children.Add(search);
            Grid.SetColumn(search, 1);

            // Сегментированный контроль «Все / Избранное / Недавние» в общем контейнере.
            // Рамка у группы фильтров своя, её строит BuildListModeSegments.
            // Второй обёртки не нужно: у автора рамка ровно одна.
            var tabs = BuildListModeSegments();
            // Имя нужно настройке шрифта области «Вкладки»: ThemeManager ищет
            // область по этому имени, как и Windows-версия.
            tabs.Name = "TabsPanel";
            grid.Children.Add(tabs);
            Grid.SetColumn(tabs, 2);


            var topBarBorder = new Border
            {
                Child = grid,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(UiMetrics.TopBarH, UiMetrics.TopBarV)
            };
            // Нижняя граница TopBar из темы.
            ThemeBrushes.Bind(topBarBorder, Border.BorderBrushProperty, "BorderColorBrush");
            // Заливка полосы, как в разметке WPF: без неё фон групп команд
            // и фильтров совпадает с фоном под ними и рамки выглядят пустыми.
            ThemeBrushes.Bind(topBarBorder, Border.BackgroundProperty, "CardBackgroundBrush");

            // Перетаскивание окна за фон верхней панели (системной рамки больше нет).
            // Интерактивные элементы (кнопки, поля) движение не начинают; пустое
            // место полосы тянет окно за собой.
            topBarBorder.PointerPressed += OnTopBarPointerPressed;

            return topBarBorder;
        }

        /// <summary>
        /// Панель команд над заголовками колонок (MainWindow.xaml:488-612).
        /// Слева блок управления группами, дальше правка списка, обслуживание
        /// баз и настройки, разделённые вертикальными чертами. В версии для
        /// Windows все эти команды живут здесь, а не в верхней полосе окна.
        /// </summary>
        private Control BuildCommandPanel()
        {
            var border = new Border
            {
                Child = BuildCommandPanelContent(),
                Margin = new Thickness(4, 0, 4, 0),
                Padding = new Thickness(8, 6),
                BorderThickness = new Thickness(0, 0, 0, 1)
            };
            ThemeBrushes.Bind(border, Border.BackgroundProperty, "CardBackgroundBrush");
            ThemeBrushes.Bind(border, Border.BorderBrushProperty, "BorderColorBrush");
            _commandPanel = border;
            return border;
        }

        /// <summary>
        /// Пересобирает содержимое панели команд: состав кнопок групп зависит
        /// от настроек, а они меняются на живом окне из окна настроек.
        /// </summary>
        private void RefreshCommandPanel()
        {
            if (_commandPanel is not null)
                _commandPanel.Child = BuildCommandPanelContent();
        }

        /// <summary>Кнопки панели команд одной строкой, слева направо.</summary>
        private Control BuildCommandPanelContent()
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                // В разметке зазор задан отступом каждой кнопки (Margin="0,0,2,0",
                // MainWindow.xaml:496 и далее), здесь он общий для всей строки.
                Spacing = 2
            };

            // Развернуть и свернуть группы, сортировка групп, показ тегов в строках.
            panel.Children.Add(BuildGroupToolbar());
            panel.Children.Add(CommandPanelSeparator());

            // «Правка»: добавить, изменить и удалить выбранную базу.
            var addBtn = TopBarIconButton("IconAdd", LocalizationManager.T("Main.AddBase"), "#22C55E");
            addBtn.Bind(Button.CommandProperty, new Binding("AddInfobaseCommand"));
            panel.Children.Add(addBtn);

            var editBtn = TopBarIconButton("IconEdit", LocalizationManager.T("Main.EditBaseTooltip"),
                themeBrushKey: "TextSecondaryColorBrush");
            editBtn.Bind(Button.CommandProperty, new Binding("EditInfobaseCommand"));
            panel.Children.Add(editBtn);

            var deleteBtn = TopBarIconButton("IconDelete", LocalizationManager.T("Main.DeleteTooltip"), "#DC2626");
            deleteBtn.Bind(Button.CommandProperty, new Binding("DeleteInfobaseCommand"));
            panel.Children.Add(deleteBtn);

            panel.Children.Add(CommandPanelSeparator());

            // «Управление списком»: очистка кеша, индикатор выгрузки, синхронизация
            // с ibases.v8i и проверка доступности баз.
            var clearCacheBtn = TopBarIconButton("IconBroom", LocalizationManager.T("Main.ClearCacheTooltip"), "#F59E0B");
            clearCacheBtn.Bind(Button.CommandProperty, new Binding("ClearCacheCommand"));
            panel.Children.Add(clearCacheBtn);

            // Индикатор выгрузки .dt и .cf: виден только во время пакетной
            // операции, подсказка сводкой (MainWindow.xaml:563).
            var exportBtn = TopBarIconButton("IconUpload", string.Empty, "#F59E0B");
            exportBtn.Bind(ToolTip.TipProperty, new Binding("ExportIndicatorTooltip"));
            exportBtn.Bind(Control.IsVisibleProperty, new Binding("IsExporting"));
            exportBtn.Focusable = false;
            panel.Children.Add(exportBtn);

            var syncBtn = TopBarIconButton("IconSync", LocalizationManager.T("Main.SyncDetailedTooltip"), "#14B8A6");
            syncBtn.Bind(Button.CommandProperty, new Binding("SynchronizeWithIbasesCommand"));
            panel.Children.Add(syncBtn);

            // Проверить доступность всех баз 1С: ручная команда вместо автопроверки при запуске.
            // Иконка — зелёный гидролокатор (сонар), как экран на подводных лодках.
            var checkAvailBtn = new Button
            {
                Content = IconHelper.MakeIcon("IconSonar", UiMetrics.Scaled(18),
                    new SolidColorBrush(Color.Parse("#14B8A6"))),
                Padding = new Thickness(UiMetrics.Scaled(8)),
                HorizontalContentAlignment = HorizontalAlignment.Center
            };
            checkAvailBtn.Styled(Themes.ControlThemes.IconButton);
            ToolTip.SetTip(checkAvailBtn, LocalizationManager.T("Main.CheckAvailabilityTooltip"));
            checkAvailBtn.Bind(Button.CommandProperty, new Binding("CheckAvailabilityCommand"));
            panel.Children.Add(checkAvailBtn);

            panel.Children.Add(CommandPanelSeparator());

            // «Настройки»: тема, компактный режим, окно настроек и справка.
            // Значок темы меняется вместе со схемой, как в версии для Windows
            // (MainWindow.Language.cs:41): в тёмной солнце, в светлой луна.
            var themeIconKey = ThemeManager.CurrentTheme == ThemeManager.DarkThemeName ? "IconSun" : "IconMoon";
            var themeBtn = TopBarIconButton(themeIconKey, LocalizationManager.T("Main.Theme"), "#8B5CF6");
            themeBtn.Bind(Button.CommandProperty, new Binding("ToggleThemeCommand"));
            panel.Children.Add(themeBtn);

            // Это ToggleButton (MainWindow.xaml:596): включённый компактный режим
            // виден акцентной заливкой, а не только по плотности списка.
            _compactToggle = MakeSegmentToggle("IconCompress", LocalizationManager.T("Main.CompactModeTooltip"));
            _compactToggle.IsChecked = _vm?.CompactMode ?? false;
            _compactToggle.Click += (_, _) =>
            {
                if (_vm is null)
                    return;
                var next = _compactToggle.IsChecked == true;
                _vm.CompactMode = next;
                ApplyCompactMode(next);
            };
            panel.Children.Add(_compactToggle);

            // «Смена пользователя» (issue #200): видна только при нескольких учётных записях.
            var switchUserBtn = TopBarIconButton("IconAccountMultiple", LocalizationManager.T("Main.SwitchUserTooltip"),
                themeBrushKey: "TextSecondaryColorBrush");
            switchUserBtn.Bind(Button.CommandProperty, new Binding("SwitchUserCommand"));
            switchUserBtn.Bind(Control.IsVisibleProperty, new Binding("SwitchUserVisible"));
            panel.Children.Add(switchUserBtn);

            var settingsBtn = TopBarIconButton("IconSettings", LocalizationManager.T("Main.SettingsTooltip"),
                themeBrushKey: "TextSecondaryColorBrush");
            settingsBtn.Bind(Button.CommandProperty, new Binding("OpenSettingsCommand"));
            panel.Children.Add(settingsBtn);

            panel.Children.Add(new HelpLink
            {
                HelpText = LocalizationManager.T("Main.BaseListHelp"),
                Margin = new Thickness(4, 0, 0, 0)
            });

            return panel;
        }

        /// <summary>
        /// Вертикальная черта между группами команд панели: одна точка ширины
        /// с прозрачностью 0.55 и полями 4,5 (MainWindow.xaml:533).
        /// </summary>
        private static Control CommandPanelSeparator()
        {
            var line = new Border
            {
                Width = 1,
                Margin = new Thickness(4, 5),
                Opacity = 0.55
            };
            ThemeBrushes.Bind(line, Border.BackgroundProperty, "BorderColorBrush");
            return line;
        }

        /// <summary>
        /// Заголовок программы: локализованное имя и суффикс версии. Суффикс
        /// не задваивается при повторном вызове, как в версии для Windows
        /// (MainWindow.Language.cs:107-122). Пользователь видит эту строку в шапке
        /// окна, потому что системной строки заголовка на Linux у нас нет.
        /// </summary>
        private static string ComposeWindowTitle()
        {
            var baseTitle = LocalizationManager.T("App.Title");
            var version = VersionInfo.Display();
            if (string.IsNullOrWhiteSpace(version))
                return baseTitle;

            var suffix = $" v{version}";
            return baseTitle.EndsWith(suffix, StringComparison.Ordinal) ? baseTitle : baseTitle + suffix;
        }

        /// <summary>
        /// Строка заголовка окна вместо системной: слева значок приложения и название
        /// с версией, справа кнопки управления окном (MainWindow.xaml:182-235).
        /// Шапка активного окна заливается акцентом, неактивного — цветом карточки,
        /// перетаскивание идёт за пустое место полосы.
        /// </summary>
        private Control BuildTitleBar()
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            if (Services.AppIconLoader.TitleBarBitmap() is { } appBitmap)
            {
                left.Children.Add(new Image
                {
                    Source = appBitmap,
                    Width = UiMetrics.Scaled(18),
                    Height = UiMetrics.Scaled(18),
                    Margin = new Thickness(0, 0, UiMetrics.Scaled(8), 0),
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            // Подпись берётся из свойства окна: там уже собран заголовок с версией
            // (App.axaml.cs:213-215), как в UpdateWindowTitle версии для Windows.
            _appTitleText = new TextBlock
            {
                FontSize = UiMetrics.ScaledFont(13),
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            _appTitleSub?.Dispose();
            _titleBackgroundSub?.Dispose();
            _titleForegroundSub?.Dispose();
            _titleBackgroundSub = null;
            _titleForegroundSub = null;
            _appTitleSub = _appTitleText.Bind(TextBlock.TextProperty, this.GetObservable(TitleProperty));
            left.Children.Add(_appTitleText);

            var buttons = BuildWindowControls();

            Grid.SetColumn(left, 0);
            Grid.SetColumn(buttons, 1);
            grid.Children.Add(left);
            grid.Children.Add(buttons);

            _titleBarBorder = new Border
            {
                Child = grid,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(UiMetrics.Scaled(12), UiMetrics.Scaled(6))
            };
            ThemeBrushes.Bind(_titleBarBorder, Border.BorderBrushProperty, "BorderColorBrush");

            // Перетаскивание и разворот двойным щелчком — как у автора
            // (MainWindow.xaml.cs:711-731), обработчик общий с панелью команд.
            _titleBarBorder.PointerPressed += OnTopBarPointerPressed;

            // Цвета шапки зависят от активности окна и от темы, поэтому применяются
            // и сразу после пересборки содержимого, а не только по событию.
            ApplyTitleBarAppearance(IsActive);
            return _titleBarBorder;
        }

        /// <summary>
        /// Шапка активного окна заливается акцентным цветом темы, неактивного — цветом
        /// карточки; вместе с фоном меняются цвет названия и значков кнопок окна, иначе
        /// на акценте они нечитаемы (MainWindow.xaml.cs:588-615).
        /// </summary>
        private void ApplyTitleBarAppearance(bool active)
        {
            if (_titleBarBorder is null || _appTitleText is null)
                return;

            // Прежняя привязка снимается явно. Avalonia заменяет привязку того же
            // приоритета и сама (ValueStore.AddBinding зовёт
            // DisposeExistingLocalValueBinding), накопления не было бы и без этого,
            // но активность окна переключается за сессию сотни раз, и владение
            // привязкой здесь лучше держать явным.
            _titleBackgroundSub?.Dispose();
            _titleForegroundSub?.Dispose();
            _titleBackgroundSub = _titleBarBorder.Bind(Border.BackgroundProperty,
                new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(
                    active ? "AccentBrush" : "CardBackgroundBrush"));
            _titleForegroundSub = _appTitleText.Bind(TextBlock.ForegroundProperty,
                new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(
                    active ? "ButtonTextBrush" : "TextPrimaryColorBrush"));

            foreach (var button in _windowControlButtons)
                button.SetOnAccent(active);
        }

        /// <summary>
        /// Собственные кнопки управления окном вместо системных: свернуть (минус),
        /// развернуть/восстановить (квадрат / два квадрата) и закрыть (крест).
        /// Панель прижата к правому краю верхней панели.
        /// </summary>
        private Control BuildWindowControls()
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            // Список пересобирается вместе с шапкой: старые кнопки уже не в дереве.
            _windowControlButtons.Clear();

            var minimize = new WindowControlButton(this, WindowControlKind.Minimize);
            ToolTip.SetTip(minimize, LocalizationManager.T("Window.Minimize"));
            minimize.Click += (_, _) => WindowState = WindowState.Minimized;
            panel.Children.Add(minimize);
            _windowControlButtons.Add(minimize);

            var maximize = new WindowControlButton(this, WindowControlKind.Maximize);
            ToolTip.SetTip(maximize, LocalizationManager.T("Window.Maximize"));
            maximize.Click += (_, _) =>
            {
                WindowState = WindowState == WindowState.Maximized
                    ? WindowState.Normal
                    : WindowState.Maximized;
            };
            panel.Children.Add(maximize);
            _windowControlButtons.Add(maximize);

            // Закрытие уходит через штатный Close(): OnClosing сам решает,
            // прятать ли окно в трей (CloseToTray) или завершать приложение.
            var close = new WindowControlButton(this, WindowControlKind.Close);
            ToolTip.SetTip(close, LocalizationManager.T("Common.Close"));
            close.Click += (_, _) => Close();
            panel.Children.Add(close);
            _windowControlButtons.Add(close);

            return panel;
        }

        /// <summary>
        /// Перетаскивание окна за фон верхней панели. Кнопки, поля и прочие
        /// интерактивные элементы движение не начинают — только пустое место полосы.
        /// </summary>
        private void OnTopBarPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                return;
            if (IsInteractiveSource(e.Source))
                return;

            // Двойной щелчок по полосе разворачивает и возвращает окно, как у автора
            // (MainWindow.xaml.cs:715-719). Проверяется раньше перетаскивания: у
            // развёрнутого окна перетаскивание отключено, а разворот работать должен.
            if (e.ClickCount == 2)
            {
                WindowState = WindowState == WindowState.Maximized
                    ? WindowState.Normal
                    : WindowState.Maximized;
                e.Handled = true;
                return;
            }

            // Развёрнутое окно не таскаем: возврат к «плавающему» виду делается
            // кнопкой разворота, а BeginMoveDrag по развёрнутому окну на части
            // оконных менеджеров ведёт себя непредсказуемо.
            if (WindowState == WindowState.Maximized)
                return;
            BeginMoveDrag(e);
        }

        /// <summary>true, если источник нажатия — интерактивный элемент внутри верхней панели.</summary>
        private static bool IsInteractiveSource(object? source)
        {
            var node = source as Visual;
            while (node is not null)
            {
                if (node is Button or ToggleButton or TextBox or HelpLink)
                    return true;
                node = node.GetVisualParent();
            }
            return false;
        }

        /// <summary>
        /// Невидимые зоны изменения размера по краям и углам окна (системной рамки
        /// больше нет): нажатие в такой зоне вызывает BeginResizeDrag нужного края.
        /// </summary>
        private void AddResizeZones(Grid root)
        {
            const double edgeThickness = 6;
            const double cornerSize = 12;

            var overlay = new Grid();
            Grid.SetRowSpan(overlay, root.RowDefinitions.Count);
            overlay.ZIndex = 2000;

            // Углы — поверх рёбер, чтобы нажимались первыми.
            AddResizeZone(overlay, WindowEdge.NorthWest, HorizontalAlignment.Left, VerticalAlignment.Top,
                cornerSize, cornerSize, StandardCursorType.TopLeftCorner);
            AddResizeZone(overlay, WindowEdge.NorthEast, HorizontalAlignment.Right, VerticalAlignment.Top,
                cornerSize, cornerSize, StandardCursorType.TopRightCorner);
            AddResizeZone(overlay, WindowEdge.SouthWest, HorizontalAlignment.Left, VerticalAlignment.Bottom,
                cornerSize, cornerSize, StandardCursorType.BottomLeftCorner);
            AddResizeZone(overlay, WindowEdge.SouthEast, HorizontalAlignment.Right, VerticalAlignment.Bottom,
                cornerSize, cornerSize, StandardCursorType.BottomRightCorner);
            // Рёбра.
            AddResizeZone(overlay, WindowEdge.North, HorizontalAlignment.Stretch, VerticalAlignment.Top,
                0, edgeThickness, StandardCursorType.SizeNorthSouth);
            AddResizeZone(overlay, WindowEdge.South, HorizontalAlignment.Stretch, VerticalAlignment.Bottom,
                0, edgeThickness, StandardCursorType.SizeNorthSouth);
            AddResizeZone(overlay, WindowEdge.West, HorizontalAlignment.Left, VerticalAlignment.Stretch,
                edgeThickness, 0, StandardCursorType.SizeWestEast);
            AddResizeZone(overlay, WindowEdge.East, HorizontalAlignment.Right, VerticalAlignment.Stretch,
                edgeThickness, 0, StandardCursorType.SizeWestEast);

            root.Children.Add(overlay);
        }

        private void AddResizeZone(Grid host, WindowEdge edge, HorizontalAlignment ha, VerticalAlignment va,
            double width, double height, StandardCursorType cursor)
        {
            var zone = new Border
            {
                HorizontalAlignment = ha,
                VerticalAlignment = va,
                Width = width > 0 ? width : double.NaN,
                Height = height > 0 ? height : double.NaN,
                // Прозрачная, но не null кисть: по ней всё равно идёт hit-test.
                Background = Brushes.Transparent,
                Cursor = new Cursor(cursor),
                IsHitTestVisible = true
            };
            zone.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                    BeginResizeDrag(edge, e);
            };
            host.Children.Add(zone);
        }

        private ScrollViewer? _rightPanelHost;
        private StackPanel? _rightPanelContent;
        private Border? _sessionCard;
        private TextBlock? _sessionTitleBlock;

        /// <summary>
        /// Ширина правой панели как в разметке WPF: при показанных подробностях
        /// 320 с минимумом 280, при скрытых панель сжимается по содержимому
        /// и не шире 200. У автора это триггер по ShowRightPanelDetails.
        /// </summary>
        private void UpdateRightPanelWidth()
        {
            if (_rightPanelHost is null)
                return;
            var details = _vm?.ShowRightPanelDetails != false;
            _rightPanelHost.Width = details ? 320 : double.NaN;
            _rightPanelHost.MinWidth = details ? 280 : 0;
            _rightPanelHost.MaxWidth = details ? double.PositiveInfinity : 200;
            _rightPanelHost.HorizontalAlignment = details
                ? HorizontalAlignment.Stretch
                : HorizontalAlignment.Left;
            if (_rightPanelContent is not null)
            {
                // Верхний отступ правой панели приведён к стандартному (12), как у левой
                // колонки и как в Windows-версии (issue #167). Прежний большой зазор 56
                // «отодвигал» блок запуска вниз и выглядел лишним отступом перед кнопками
                // справа (issue #221). Правая панель и так лежит ниже строки заголовка,
                // поэтому значение не зависит от режима системной рамки окна.
                _rightPanelContent.Margin = details
                    ? new Thickness(12, 12)
                    : new Thickness(2, 12, 4, 6);
                _rightPanelContent.HorizontalAlignment = details
                    ? HorizontalAlignment.Stretch
                    : HorizontalAlignment.Left;
            }
            if (_sessionCard is not null)
            {
                _sessionCard.Margin = details
                    ? new Thickness(8, 0, 8, 10)
                    : new Thickness(4, 0, 4, 6);
                _sessionCard.Padding = details
                    ? new Thickness(10, 8)
                    : new Thickness(6);
            }
            if (_sessionTitleBlock is not null)
            {
                _sessionTitleBlock.FontSize = UiMetrics.ScaledFont(details ? 12 : 11);
                _sessionTitleBlock.Margin = details
                    ? new Thickness(0, 0, 0, 6)
                    : new Thickness(0, 0, 0, 4);
            }
        }

        /// <summary>Сегментный переключатель (например «группы»/«теги») с иконкой и состояниями.</summary>
        private SegmentButton MakeSegmentToggle(string iconKey, string tooltip, double iconSize = 18)
        {
            // Размеры из разметки (MainWindow.xaml:171-179): значок 18, отступ 6,
            // зазор между сегментами 2. Прежние 15, 12 на 5 и общий Spacing
            // разносили кнопки заметно шире, чем в версии для Windows.
            // У переключателя тегов в панели команд значок свой, 14
            // (MainWindow.xaml:528), поэтому размер параметром.
            var segment = new SegmentButton(iconKey, string.Empty, "ItemHoverBrush", "ItemSelectedBrush", lockOn: false,
                iconSize: UiMetrics.Scaled(iconSize))
            {
                IsChecked = true,
                // У автора рамки нет, отступ 6. У нас рамка в 2 держит фокусное
                // кольцо, поэтому отступ уменьшен на её толщину: внешний размер
                // кнопки совпадает, а фокус остаётся видимым.
                Padding = new Thickness(UiMetrics.Scaled(4)),
                Margin = new Thickness(0, 0, 2, 0),
                MinHeight = 0
            };
            ToolTip.SetTip(segment, tooltip);
            return segment;
        }

        /// <summary>Сегментированный контроль фильтра списка: Все / Избранное / Недавние.</summary>
        private Control BuildListModeSegments()
        {
            // Размеры и кисть как у группы команд в разметке WPF: скругление 12,
            // отступ 4, фон ContentBackgroundBrush.
            var container = new Border
            {
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(4),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            };
            ThemeBrushes.Bind(container, Border.BackgroundProperty, "ContentBackgroundColorBrush");
            ThemeBrushes.Bind(container, Border.BorderBrushProperty, "BorderColorBrush");
            UiMetrics.AddBrushTransition(container);

            // Отступ 14 на 7, кегль 13, значок 14, зазор до подписи 5 и поле 4
            // между сегментами (MainWindow.xaml:234-241, LightTheme.xaml:537).
            var panel = new StackPanel { Orientation = Orientation.Horizontal };

            var allSeg = new SegmentButton("IconDatabase", LocalizationManager.T("Main.AllBases"), "ItemHoverBrush", "ItemSelectedBrush",
                iconSize: UiMetrics.Scaled(14), cornerRadius: 8)
            {
                Padding = new Thickness(12, 5),
                Margin = new Thickness(0, 0, 4, 0),
                MinHeight = 0
            };
            ToolTip.SetTip(allSeg, LocalizationManager.T("Main.AllBasesTooltip"));
            allSeg.Bind(ToggleButton.IsCheckedProperty, new Binding("IsListModeAll") { Mode = BindingMode.TwoWay });
            panel.Children.Add(allSeg);

            var favSeg = new SegmentButton("IconStar", LocalizationManager.T("Main.Favorites"), "ItemHoverBrush", "ItemSelectedBrush",
                iconSize: UiMetrics.Scaled(14), cornerRadius: 8)
            {
                Padding = new Thickness(12, 5),
                Margin = new Thickness(0, 0, 4, 0),
                MinHeight = 0
            };
            ToolTip.SetTip(favSeg, LocalizationManager.T("Main.FavoritesTooltip"));
            favSeg.Bind(ToggleButton.IsCheckedProperty, new Binding("IsListModeFavorites") { Mode = BindingMode.TwoWay });
            panel.Children.Add(favSeg);

            var recSeg = new SegmentButton("IconHistory", LocalizationManager.T("Main.Recent"), "ItemHoverBrush", "ItemSelectedBrush",
                iconSize: UiMetrics.Scaled(14), cornerRadius: 8)
            {
                Padding = new Thickness(12, 5),
                Margin = new Thickness(0),
                MinHeight = 0
            };
            ToolTip.SetTip(recSeg, LocalizationManager.T("Main.RecentTooltip"));
            recSeg.Bind(ToggleButton.IsCheckedProperty, new Binding("IsListModeRecent") { Mode = BindingMode.TwoWay });
            panel.Children.Add(recSeg);

            container.Child = panel;
            return container;
        }

        /// <summary>Поле поиска: скруглённая рамка, иконка слева, кнопка очистки справа, hover-подсветка.</summary>
        private Border BuildSearchBox()
        {
            var border = new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(8, 4),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0),
                BorderThickness = new Thickness(1)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var searchIcon = IconHelper.MakeIcon("IconSearch", 16, "TextSecondaryBrush");
            searchIcon.Margin = new Thickness(2, 0, 6, 0);
            grid.Children.Add(searchIcon);
            Grid.SetColumn(searchIcon, 0);

            _searchBox = new TextBox
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(2, 6),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            // Строка-подсказка в разметке служит только всплывающей подсказкой,
            // внутри пустого поля текста нет (MainWindow.xaml:202-225).
            ToolTip.SetTip(_searchBox, LocalizationManager.T("Main.SearchPlaceholder"));
            _searchBox.Bind(TextBox.TextProperty, new Binding("SearchText") { Mode = BindingMode.TwoWay });
            grid.Children.Add(_searchBox);
            Grid.SetColumn(_searchBox, 1);

            var clearBtn = new Button
            {
                // Крестик очистки поиска в разметке 12 (MainWindow.xaml:220).
                Content = IconHelper.MakeIcon("IconClose", 12, "TextSecondaryBrush"),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(6, 0),
                Cursor = new Cursor(StandardCursorType.Hand)
            };
            ToolTip.SetTip(clearBtn, LocalizationManager.T("Main.ClearSearch"));
            clearBtn.Bind(Button.CommandProperty, new Binding("ClearSearchCommand"));
            grid.Children.Add(clearBtn);
            Grid.SetColumn(clearBtn, 2);

            border.Child = grid;

            // Ни наведение, ни фокус вида поля в разметке не меняют: фон и рамка
            // там постоянные, толщина рамки всегда 1 (MainWindow.xaml:202-225).
            // Прежние подсветка и утолщение рамки были нашей добавкой.
            border.BorderThickness = new Thickness(1);
            // Подписки привязаны к жизни рамки: содержимое окна пересобирается
            // при переключении компактного режима, и наблюдатель, живущий
            // у приложения, удерживал бы прежнее дерево целиком.
            ThemeBrushes.Observe(border, "CardBackgroundColorBrush", b => border.Background = b);
            ThemeBrushes.Observe(border, "BorderColorBrush", b => border.BorderBrush = b);
            UiMetrics.AddBrushTransition(border);
            return border;
        }

        /// <summary>Primary-кнопка топ-бара: акцентный фон, иконка + подпись цветом «на акценте».</summary>
        private static PanelButton TopBarPrimaryButton(string iconKey, string text, string tooltip)
        {
            var button = new PanelButton("AccentBrush", "AccentHoverBrush", "AccentPressedBrush", "AccentBrush")
            {
                Content = ThemedIconAndText(iconKey, text, "TextOnAccentBrush", UiMetrics.ScaledFont(15), centered: false),
                Padding = new Thickness(UiMetrics.ButtonPadH, UiMetrics.ButtonPadV),
                HorizontalContentAlignment = HorizontalAlignment.Center
            };
            ToolTip.SetTip(button, tooltip);
            return button;
        }

        /// <summary>Secondary-кнопка топ-бара: приглушённый фон, иконка + подпись, hover/pressed.</summary>

        /// <summary>Компактная иконко-кнопка топ-бара (например тема) с состояниями из темы.</summary>
        /// <param name="colorHex">
        /// Явный цвет значка, как в разметке WPF: там часть команд верхней панели
        /// покрашена вручную, а часть берёт цвет из темы. Без него берётся тема.
        /// </param>
        private static Button TopBarIconButton(string iconKey, string tooltip, string? colorHex = null,
            string themeBrushKey = "ButtonTextBrush")
        {
            Control icon;
            if (colorHex is null)
            {
                icon = IconHelper.MakeIcon(iconKey, UiMetrics.Scaled(18), themeBrushKey);
            }
            else
            {
                icon = IconHelper.MakeIcon(iconKey, UiMetrics.Scaled(18),
                    new SolidColorBrush(Color.Parse(colorHex)));
            }

            // Оформление берёт тема IconButton разметки (LightTheme.xaml:561
            // и DarkTheme.xaml:1105): прозрачный фон, скругление 8, отступ 8,
            // подсветка только при наведении. Своя реализация красила наведение
            // кистью ItemHover в обеих темах, тогда как в светлой у автора это
            // серый #F1F5F9, и гасила недоступную кнопку прозрачностью, которой
            // у этого стиля нет вовсе.
            var button = new Button
            {
                Content = icon,
                Padding = new Thickness(UiMetrics.Scaled(8)),
                HorizontalContentAlignment = HorizontalAlignment.Center
            };
            button.Styled(Themes.ControlThemes.IconButton);
            ToolTip.SetTip(button, tooltip);
            return button;
        }

        private Control BuildMainArea()
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _tree = new LeveledTreeView
            {
                BorderThickness = new Thickness(0)
            };
            // Фон списка баз — «стеклянная» версия фона рабочей области из темы:
            // полупрозрачный, чтобы acrylic/размытие проступали и в области списка,
            // а не только за верхней панелью (иначе стекло выглядело бы пятнами).
            ThemeBrushes.Observe(_tree, "ContentBackgroundColorBrush",
                brush => _tree.Background = ThemeBrushes.WithAlpha(brush, GlassBackgroundAlpha));
            // Горизонтальная прокрутка отключена: иначе строка растягивается
            // по сумме ширин колонок и уезжает за правый край, а заголовки,
            // живущие вне области прокрутки, перестают совпадать со значениями.
            ScrollViewer.SetHorizontalScrollBarVisibility(_tree, ScrollBarVisibility.Disabled);
            // Внутренняя прокрутка появляется только вместе с шаблоном, а он
            // применяется заново при каждой пересборке окна компактным режимом.
            _tree.TemplateApplied += (_, _) => AttachVerticalScrollBar();
            _tree.Bind(TreeView.ItemsSourceProperty, new Binding("GroupNodes"));
            _tree.SelectionMode = SelectionMode.Single;

            // Строки списка идут по своему шаблону, а не по Fluent: тот сдвигает
            // на уровень вложенности всю строку, и колонки значений вложенных
            // строк уезжают от заголовков. Подсветки в этом шаблоне нет вовсе,
            // фон рисует карточка строки из ресурсов темы.
            _tree.ItemContainerTheme = LeveledTreeViewItem.RowTheme();

            // Раскрытие узла связывает с моделью сам LeveledTreeView, при подготовке
            // контейнера на любом уровне вложенности. Здесь остаётся только
            // выравнивание заголовка: раскрытие группы добавляет строки, а с ними
            // может измениться и самый левый отступ, по которому выровнен заголовок.
            _tree.ContainerPrepared += (_, _) => QueueHeaderAlign();

            // Ширина шапки приравнивается ширине содержимого списка, а колонка
            // «Название» звёздная: любая разница общей ширины уходит в неё и
            // сдвигает все колонки значений. Поэтому пересчёт нужен на каждое
            // изменение размеров списка, а не только на пересборку строк.
            _tree.GetObservable(Visual.BoundsProperty)
                .Subscribe(new PropertyObserver<Rect>(_ => QueueHeaderAlign()));

            // Меню висит на дереве, как в WPF: над группой и над пустым местом
            // оно тоже открывается, а недоступные пункты гасит CanExecute.
            // Строку под курсором дерево выделяет само, по правому нажатию.
            _tree.ContextMenu = BuildRowContextMenu();

            _tree.ItemTemplate = new FuncTreeDataTemplate(
                typeof(object),
                (item, _) => BuildTreeRow(item),
                item => item is GroupNodeViewModel g ? g.Items : Array.Empty<object>());
            _tree.SelectionChanged += OnTreeSelectionChanged;

            // Перетаскивание баз и групп. Нажатие ловится по туннелю: TreeView
            // помечает PointerPressed обработанным, обновляя выделение, и
            // обычная подписка не сработала бы. Это прямой аналог
            // PreviewMouseLeftButtonDown в WPF-версии.
            _tree.AddHandler(InputElement.PointerPressedEvent, OnTreeDragPointerPressed, RoutingStrategies.Tunnel);
            _tree.PointerMoved += OnTreeDragPointerMoved;
            DragDrop.SetAllowDrop(_tree, true);
            _tree.AddHandler(DragDrop.DragOverEvent, OnTreeDragOver);
            _tree.AddHandler(DragDrop.DropEvent, OnTreeDrop);

            // Горизонтальную прокрутку списка ведёт внешний ScrollViewer, общий
            // с заголовком колонок, а вертикальную сам TreeView.
            // Прежнее опасение про бесконечную высоту и потерю виртуализации здесь
            // неприменимо: у TreeView в Avalonia 11.3.20 виртуализации нет вовсе,
            // панель элементов по умолчанию обычный StackPanel, и ни тема Fluent,
            // ни сам контрол её не переопределяют.
            _emptyState = BuildEmptyState();
            var leftInner = new Grid();
            leftInner.Children.Add(_tree);
            leftInner.Children.Add(_emptyState);

            // Заголовки колонок и строки живут в одной области горизонтальной
            // прокрутки: колонок может не хватить по ширине, и если прокручивать
            // только список, заголовки перестанут совпадать со значениями.
            _listContent = new Grid();
            _listContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _listContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Star });
            var columnHeader = BuildColumnHeader();
            _listContent.Children.Add(columnHeader);
            Grid.SetRow(columnHeader, 0);
            _listContent.Children.Add(leftInner);
            Grid.SetRow(leftInner, 1);

            var listArea = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = _listContent
            };
            _listScroll = listArea;

            // Вертикальная полоса вынесена из области горизонтальной прокрутки
            // и стоит отдельным столбцом справа. Собственная полоса дерева
            // рисуется у правого края его содержимого, поэтому уезжала за границу,
            // как только колонки переставали помещаться по ширине.
            ScrollViewer.SetVerticalScrollBarVisibility(_tree, ScrollBarVisibility.Hidden);
            // Ширина и авто-скрытие не задаются: полоса должна выглядеть так же,
            // как горизонтальная полоса списка и полоса правой панели, то есть
            // по правилам темы. Видимость тоже ведёт сам контрол: при Auto он
            // показывает полосу ровно когда есть что прокручивать. Присваивать
            // IsVisible руками нельзя, при значении Visible полоса включает себя
            // обратно на каждое изменение Maximum и ViewportSize.
            _listVerticalBar = new ScrollBar
            {
                Orientation = Orientation.Vertical,
                Visibility = ScrollBarVisibility.Auto,
                Minimum = 0,
                Maximum = 0,
                ViewportSize = 0
            };
            // Локальная ссылка на созданную полосу: поле к этому моменту
            // указывает на неё, но при следующей пересборке окна начнёт
            // указывать на другую, а подписки живут вместе с этой.
            var verticalBar = _listVerticalBar;
            // Полоса не входит в шаблон ScrollViewer, поэтому колесо над ней
            // некому переадресовать. Шаг и разбор осей взяты из
            // ScrollContentPresenter платформы: с Shift вертикальная дельта
            // становится горизонтальной, и в версии для Windows сделано так же.
            // Вертикаль ведёт прокрутка дерева, горизонталь общая с шапкой.
            _listVerticalBar.PointerWheelChanged += (_, e) =>
            {
                if (!ReferenceEquals(_listVerticalBar, verticalBar))
                    return;
                var delta = e.Delta;
                if (e.KeyModifiers == KeyModifiers.Shift && MathUtilities.IsZero(delta.X))
                    delta = new Vector(delta.Y, delta.X);

                // Событие считается разобранным только если список сдвинулся:
                // на краю платформа отдаёт прокрутку выше по дереву, и глушить
                // её здесь значит ломать это правило.
                var moved = false;
                if (delta.Y != 0 && TreeScroll is { } vertical)
                {
                    var hidden = Math.Max(0, vertical.Extent.Height - vertical.Viewport.Height);
                    var next = vertical.Offset.WithY(
                        Math.Clamp(vertical.Offset.Y - delta.Y * WheelScrollStep, 0, hidden));
                    if (next != vertical.Offset)
                    {
                        vertical.Offset = next;
                        moved = true;
                    }
                }

                if (delta.X != 0 && _listScroll is { } horizontal)
                {
                    var hidden = Math.Max(0, horizontal.Extent.Width - horizontal.Viewport.Width);
                    var next = horizontal.Offset.WithX(
                        Math.Clamp(horizontal.Offset.X - delta.X * WheelScrollStep, 0, hidden));
                    if (next != horizontal.Offset)
                    {
                        horizontal.Offset = next;
                        moved = true;
                    }
                }

                e.Handled = moved;
            };
            // Прокручиваются только строки, поэтому полоса начинается под шапкой
            // колонок. Высота шапки меняется вместе с компактным режимом и темой.
            columnHeader.GetObservable(Visual.BoundsProperty).Subscribe(new PropertyObserver<Rect>(bounds =>
                verticalBar.Margin = new Thickness(0, bounds.Height, 0, 0)));
            _listVerticalBar.GetObservable(RangeBase.ValueProperty).Subscribe(new PropertyObserver<double>(value =>
            {
                // Флаг разводит два направления: пользователь тянет полосу,
                // и наоборот, прокрутка списка двигает полосу.
                if (_syncingScrollBar || !ReferenceEquals(_listVerticalBar, verticalBar)
                    || TreeScroll is not { } scroll)
                    return;
                _syncingScrollBar = true;
                try { scroll.Offset = scroll.Offset.WithY(value); }
                finally { _syncingScrollBar = false; }
            }));

            // Полоса занимает свой столбец, как в версии для Windows. Ширина
            // ей задана темой и при наведении не меняется, поэтому список от неё
            // не дёргается, а пока прокручивать нечего, полоса скрыта и столбец
            // пуст. Поверх списка её класть нельзя: она забирала бы клики,
            // контекстное меню и перетаскивание по правой кромке строк.
            var listWithBar = new Grid();
            listWithBar.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            listWithBar.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            Grid.SetColumn(_listVerticalBar, 1);
            listWithBar.Children.Add(listArea);
            listWithBar.Children.Add(_listVerticalBar);

            // Левая колонка: свой фон и правая граница, внутреннее поле 12,0,0,12.
            // Верхнего отступа нет: панель поиска (BuildTopBar) лежит внутри левой
            // колонки сразу над панелью тегов, и лишний зазор между ней и панелью тегов
            // выглядел «большим непонятным отступом» (issue #167). В WPF-версии панель
            // поиска тоже лежит внутри левой колонки (MainWindow.xaml:264), а панель
            // тегов прижата к ней без промежутка — здесь так же. Панель поиска не тянется
            // на правую панель, чтобы не опускать её вниз лишним отступом (issue #221).
            // В WPF (MainWindow.xaml:347-350) отступ справа 8 был нужен полосе дерева,
            // которая жила внутри области прокрутки. Здесь вертикальная полоса вынесена
            // отдельным столбцом (listWithBar), и правый отступ оставлял бы между ней и
            // границей панели пустоту ~8px. Убираем его, чтобы полоса была прижата к
            // правому краю панели. Панель тегов сверху имеет собственные отступы
            // (4,0,4,8 и поле 8), поэтому её вид не меняется.
            var leftContent = new Grid { Margin = new Thickness(12, 0, 0, 12) };
            leftContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            leftContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            leftContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Star });
            var tagPanel = BuildTagFilterPanel();
            // Панель команд стоит между фильтром тегов и списком, как в разметке
            // (MainWindow.xaml:488, Grid.Row=2 левой колонки).
            var commandPanel = BuildCommandPanel();
            Grid.SetRow(tagPanel, 0);
            Grid.SetRow(commandPanel, 1);
            Grid.SetRow(listWithBar, 2);
            leftContent.Children.Add(tagPanel);
            leftContent.Children.Add(commandPanel);
            leftContent.Children.Add(listWithBar);

            // Верхняя панель поиска живёт только над левой колонкой, как в WPF
            // (MainWindow.xaml:264), и не тянется на правую панель. Раньше она была
            // полноширинной строкой окна, и из-за неё правая панель начиналась ниже
            // и у неё оставался лишний верхний отступ, которого нет в Windows-версии
            // (issue #221). Левая колонка выглядит так же, как раньше: панель стоит
            // ровно там, где была полноширинная строка, а правая панель теперь
            // поднимается вверх и встаёт вровень с верхней панелью поиска.
            //
            // Верхний отступ 12 у левой колонки повторяет внутреннее поле сетки
            // WPF-версии (MainWindow.xaml:248, Margin="12,12,8,12"): там и панель
            // поиска, и правая панель (её ScrollViewer Padding="12,12") начинаются
            // с одной высоты 12, поэтому край запуска справа стоит вровень со строкой
            // поиска. Без этого отступа левая панель начиналась с y=0, а содержимое
            // правой панели (Margin сверху 12) оказывалось на 12px ниже строки поиска
            // и создавало «остаточный верхний отступ» в правой панели (issue #221).
            var leftStack = new Grid
            {
                Margin = new Thickness(0, 12, 0, 0)
            };
            leftStack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            leftStack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Star });
            var topBar = BuildTopBar();
            Grid.SetRow(topBar, 0);
            Grid.SetRow(leftContent, 1);
            leftStack.Children.Add(topBar);
            leftStack.Children.Add(leftContent);

            var leftPanel = new Border
            {
                Child = leftStack,
                BorderThickness = new Thickness(0, 0, 1, 0)
            };
            ThemeBrushes.Bind(leftPanel, Border.BackgroundProperty, "CardBackgroundBrush");
            ThemeBrushes.Bind(leftPanel, Border.BorderBrushProperty, "BorderColorBrush");

            grid.Children.Add(leftPanel);
            Grid.SetColumn(leftPanel, 0);

            // Показываем/скрываем заглушку при любых изменениях списка и поиска.
            if (_vm is not null)
            {
                // Содержимое окна пересобирается при смене языка и компактного
                // режима, а вьюмодель живёт дальше: без снятия прежние
                // обработчики накапливались бы и делали ту же работу заново.
                DetachViewModelHandlers();

                // Строки пересобираются вместе с деревом, поэтому заголовок
                // выравнивается по ним заново: отступ уровня мог измениться.
                _groupNodesChanged = (_, _) => { UpdateEmptyState(); QueueHeaderAlign(); };
                _flatItemsChanged = (_, _) => UpdateEmptyState();
                _tagFiltersRebuilt = (_, _) => RefreshTagFilterPanel();
                _vmPropertyChanged = (_, e) =>
                {
                    if (e.PropertyName == nameof(MainViewModel.SearchText))
                    {
                        // Клик в поле поиска и очистка крестиком меняют фильтр и пересобирают
                        // дерево: глубина первой видимой базы может измениться, а с ней и нужный
                        // компенсатор заголовка. Ставим выравнивание в очередь, чтобы оно
                        // выполнилось после материализации новых строк (issue #214).
                        UpdateEmptyState();
                        QueueHeaderAlign();
                    }
                    // Меню трея показывает выбранную базу и недавние: без этого
                    // оно осталось бы таким, каким было собрано при запуске.
                    if (e.PropertyName == nameof(MainViewModel.SelectedInfobase)
                        || e.PropertyName == nameof(MainViewModel.RecentInfobases))
                        QueueTrayMenuRefresh();
                    // Заголовок строится до загрузки настроек, поэтому обновляется
                    // при уведомлении о колонках: иначе сохранённые ширина и состав
                    // применились бы к строкам, но не к уже собранному заголовку.
                    if (e.PropertyName is not null && e.PropertyName.Contains("Column", StringComparison.Ordinal))
                        QueueColumnHeaderRefresh();
                    // Кнопки групп живут в заголовке и видны только при группировке.
                    if (e.PropertyName == nameof(MainViewModel.ShowExpandCollapseButtons))
                    {
                        QueueColumnHeaderRefresh();
                        // Кнопки групп живут в панели команд, а их видимость
                        // меняется из окна настроек на живом окне.
                        RefreshCommandPanel();
                    }
                    // Звезда и булавка задают ширину ведущих колонок, а строки
                    // при смене настройки пересобираются: без пересборки шапки
                    // её колонки остались бы прежней ширины и разошлись со строками.
                    if (e.PropertyName == nameof(MainViewModel.ShowPinnedButton)
                        || e.PropertyName == nameof(MainViewModel.ShowFavoritesButton))
                        QueueColumnHeaderRefresh();
                    // Переключатель тегов в списке живёт в панели команд,
                    // а его настройка меняется и из окна настроек.
                    if (e.PropertyName == nameof(MainViewModel.ShowTags))
                        RefreshCommandPanel();
                    // Группировку меняют и верхняя панель, и окно настроек,
                    // поэтому переключатель подтягивает состояние вьюмодели.
                    if (e.PropertyName == nameof(MainViewModel.GroupByGroup) && _groupByToggle is not null)
                        _groupByToggle.IsChecked = _vm.GroupByGroup;
                    // Пустые группы переключаются ещё и из окна настроек, поэтому
                    // кнопка подтягивает состояние, иначе первый клик уходит вхолостую.
                    if (e.PropertyName == nameof(MainViewModel.ShowEmptyGroups) && _emptyGroupsToggle is not null)
                        _emptyGroupsToggle.IsChecked = _vm.ShowEmptyGroups;

                    if (e.PropertyName == nameof(MainViewModel.CompactMode) && _compactToggle is not null)
                        _compactToggle.IsChecked = _vm.CompactMode;
                    // Ширина правой панели задана числами по этому свойству,
                    // а переключатель подробностей живёт в строке состояния
                    // и меняет его на живом окне. Без пересчёта панель застывала
                    // в ширине, снятой при построении.
                    if (e.PropertyName == nameof(MainViewModel.ShowRightPanelDetails))
                    {
                        // Смена ширины правой панели меняет ширину области списка, а значит и
                        // общую ширину сеток заголовка/строк, от равенства которой зависит
                        // совпадение колонок — пересчитываем выравнивание (issue #214).
                        UpdateRightPanelWidth();
                        QueueHeaderAlign();
                    }
                    if (e.PropertyName == nameof(MainViewModel.ShowTagFilterPanel)
                        || e.PropertyName == nameof(MainViewModel.HasActiveTagFilter))
                    {
                        // Кнопка «теги» строится до загрузки настроек, поэтому
                        // её состояние подтягивается отсюда, иначе после перезапуска
                        // она разошлась бы с реальной видимостью панели.
                        if (_tagsToggle is not null)
                            _tagsToggle.IsChecked = _vm.ShowTagFilterPanel;
                        RefreshTagFilterPanel();
                    }
                };

                _vm.GroupNodes.CollectionChanged += _groupNodesChanged;
                _vm.FlatItems.CollectionChanged += _flatItemsChanged;
                _vm.TagFiltersRebuilt += _tagFiltersRebuilt;
                _vm.PropertyChanged += _vmPropertyChanged;
            }
            UpdateEmptyState();
            RefreshTagFilterPanel();
            RefreshColumnHeader();

            var rightPanel = new ScrollViewer
            {
                Name = "RightPanelBorder",
                Content = BuildRightPanel(),
                // Более плотные отступы — кнопки и карточки занимают меньше места.
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MinWidth = 280
            };

            // Панель светлее карточки, как в разметке: без своего фона карточка
            // сведений совпадала с фоном окна и от неё оставалась одна рамка.
            ThemeBrushes.Bind(rightPanel, TemplatedControl.BackgroundProperty, "CardBackgroundBrush");
            _rightPanelHost = rightPanel;
            UpdateRightPanelWidth();

            grid.Children.Add(rightPanel);
            Grid.SetColumn(rightPanel, 1);

            return grid;
        }

        /// <summary>
        /// Строит карточку-заглушку пустого списка: иконка, заголовок, подсказка и кнопка
        /// «Добавить базу». Иконка/тексты меняются в <see cref="UpdateEmptyState"/> в зависимости
        /// от того, пуст ли список баз вообще или фильтр ничего не нашёл.
        /// </summary>
        private Border BuildEmptyState()
        {
            var card = new Border
            {
                CornerRadius = new CornerRadius(UiMetrics.RadiusXl),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(30, 34),
                MaxWidth = 380,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsVisible = false
            };
            ThemeBrushes.Bind(card, Border.BackgroundProperty, "CardBackgroundBrush");
            ThemeBrushes.Bind(card, Border.BorderBrushProperty, "BorderColorBrush");
            UiMetrics.AddSoftShadow(card);
            UiMetrics.AddBrushTransition(card);
            UiMetrics.AddOpacityTransition(card);

            var stack = new StackPanel
            {
                Spacing = 6,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            _emptyIconHost = IconHelper.MakeIcon("IconDatabase", 44, out _emptyIcon);
            ThemeBrushes.Bind(_emptyIcon, Avalonia.Controls.Shapes.Path.FillProperty, "TextSecondaryBrush");
            _emptyIconHost.HorizontalAlignment = HorizontalAlignment.Center;
            _emptyIconHost.Margin = new Thickness(0, 0, 0, 6);
            stack.Children.Add(_emptyIconHost);

            _emptyTitle = new TextBlock
            {
                FontSize = 15,
                FontWeight = FontWeight.SemiBold,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            ThemeBrushes.Bind(_emptyTitle, TextBlock.ForegroundProperty, "TextPrimaryBrush");
            stack.Children.Add(_emptyTitle);

            _emptyHint = new TextBlock
            {
                FontSize = 12,
                Opacity = 0.85,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                MaxWidth = 320,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            ThemeBrushes.Bind(_emptyHint, TextBlock.ForegroundProperty, "TextSecondaryBrush");
            stack.Children.Add(_emptyHint);

            var addBtn = TopBarPrimaryButton("IconAdd", LocalizationManager.T("Main.AddBase"), LocalizationManager.T("Main.AddTooltip"));
            addBtn.Bind(Button.CommandProperty, new Binding("AddInfobaseCommand"));
            addBtn.HorizontalAlignment = HorizontalAlignment.Center;
            addBtn.Margin = new Thickness(0, 10, 0, 0);
            stack.Children.Add(addBtn);

            card.Child = stack;
            return card;
        }

        /// <summary>
        /// Обновляет заглушку пустого списка: показывает её, когда нет ни одного элемента
        /// (GroupNodes и FlatItems пусты), и подбирает иконку/текст под контекст (нет баз вообще
        /// либо фильтр/поиск не дал результатов).
        /// </summary>
        private void UpdateEmptyState()
        {
            if (_vm is null)
                return;

            var hasItems = _vm.GroupNodes.Count > 0 || _vm.FlatItems.Count > 0;
            if (hasItems)
            {
                _emptyState.IsVisible = false;
                return;
            }

            var searching = !string.IsNullOrWhiteSpace(_vm.SearchText)
                            || _vm.HasActiveTagFilter
                            || !_vm.IsListModeAll;

            if (searching)
            {
                _emptyIcon.Data = IconHelper.Geometry("IconSearch");
                _emptyTitle.Text = LocalizationManager.T("Main.EmptyNoResults");
                _emptyHint.Text = LocalizationManager.T("Main.EmptyNoResultsHint");
            }
            else
            {
                _emptyIcon.Data = IconHelper.Geometry("IconDatabase");
                _emptyTitle.Text = LocalizationManager.T("Main.EmptyNoBases");
                _emptyHint.Text = LocalizationManager.T("Main.EmptyNoBasesHint");
            }

            // Плавное появление заглушки.
            _emptyState.Opacity = 0;
            _emptyState.IsVisible = true;
            _emptyState.Opacity = 1;
        }

        /// <summary>Строит строку дерева: заголовок группы или карточку базы.</summary>
        private Control BuildTreeRow(object? item)
        {
            if (item is GroupNodeViewModel group)
                return BuildGroupRow(group);
            if (item is Infobase ib)
                return BuildInfobaseRow(ib);
            return new TextBlock { Text = item?.ToString() ?? string.Empty };
        }

        private Control BuildGroupRow(GroupNodeViewModel group)
        {
            // Высота оформления группы и расстояние между группами берутся из метрик:
            // в компактном режиме вертикальный padding заголовка и внешний отступ
            // уменьшаются, чтобы группы занимали меньше места.
            // Отступ 8,3 в обычном режиме и 6,1 в компактном, рамка толщиной 2:
            // прозрачная в покое и акцентная у выбранной группы
            // (MainWindow.xaml:872-882). Рамки у нас не было вовсе.
            var header = new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(UiMetrics.Compact ? 6 : 8, UiMetrics.GroupHeaderPadV),
                Margin = new Thickness(0, UiMetrics.GroupHeaderMarginV, 0, UiMetrics.GroupHeaderMarginV),
                BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent
            };
            // Пустая группа сдвигается вправо на отступ своего уровня и ширину
            // кнопки разворота: этой кнопки у неё нет, и без сдвига её заголовок
            // начинался бы левее соседних (Converters/GroupOffsetConverter.cs:37).
            if (group.Items.Count == 0)
            {
                var marginV = UiMetrics.GroupHeaderMarginV;
                header[!Border.MarginProperty] = new Binding(nameof(TreeViewItem.Level))
                {
                    RelativeSource = new RelativeSource
                    {
                        Mode = RelativeSourceMode.FindAncestor,
                        AncestorType = typeof(TreeViewItem)
                    },
                    Converter = new FuncValueConverter<int, Thickness>(level =>
                        new Thickness(EmptyGroupOffsetFor(level), marginV, 0, marginV))
                };
            }

            header.Bind(Border.BackgroundProperty, new Binding("HeaderBrush") { Source = group });

            // Кисть берётся наблюдателем темы, а подписка на узел живёт ровно
            // столько, сколько строка находится в дереве: строки пересобираются
            // часто, а узел группы переживает их все.
            IBrush accent = Brushes.Transparent;
            void ApplyGroupSelection()
                => header.BorderBrush = group.IsSelected ? accent : Brushes.Transparent;
            ThemeBrushes.Observe(header, "AccentBrush", brush => { accent = brush; ApplyGroupSelection(); });

            void OnGroupChanged(object? _, PropertyChangedEventArgs e)
            {
                if (e.PropertyName == nameof(GroupNodeViewModel.IsSelected))
                    ApplyGroupSelection();
            }
            header.AttachedToVisualTree += (_, _) => { group.PropertyChanged += OnGroupChanged; ApplyGroupSelection(); };
            header.DetachedFromVisualTree += (_, _) => group.PropertyChanged -= OnGroupChanged;
            ApplyGroupSelection();

            // Имя и счётчик привязаны к узлу, а не подставлены строкой: состав узла
            // меняется и без пересборки дерева (закрепление базы), и тогда готовый
            // текст остался бы со старым числом.
            var caption = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center
            };

            // Имя группы наследует применяемый к интерфейсу шрифт; в компактном режиме
            // размер задаётся явно и уменьшается, чтобы строки групп были плотнее.
            // Значок группы перед именем, как в разметке (MainWindow.xaml:964).
            var groupIcon = IconHelper.MakeIcon(group.Icon, UiMetrics.Compact ? 14 : 18, out var groupIconPath);
            groupIconPath.Fill = group.IconBrush;
            groupIcon.Margin = new Thickness(0, 0, 8, 0);
            caption.Children.Add(groupIcon);

            var text = new TextBlock
            {
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = UiMetrics.ScaledFont(UiMetrics.Compact ? 12 : 15)
            };
            if (UiMetrics.Compact)
                text.FontSize = UiMetrics.GroupNameFont;
            text.Bind(TextBlock.TextProperty, new Binding("DisplayName") { Source = group });
            text.Bind(TextBlock.ForegroundProperty, new Binding("HeaderTextBrush") { Source = group });
            caption.Children.Add(text);

            var count = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = UiMetrics.ScaledFont(UiMetrics.Compact ? 11 : 13)
            };
            if (UiMetrics.Compact)
                count.FontSize = UiMetrics.GroupNameFont;
            count.Bind(TextBlock.TextProperty,
                new Binding("TotalInfobaseCount") { Source = group, StringFormat = "({0})" });
            count.Bind(TextBlock.ForegroundProperty, new Binding("HeaderTextBrush") { Source = group });
            caption.Children.Add(count);

            // Колонки те же, что у строки базы: команды группы стоят в колонке
            // «Действия» и попадают ровно под кнопки строк, а имя со счётчиком
            // занимает всё, что левее (MainWindow.xaml:961 и 1015).
            var row = new Grid { Name = GroupRowGridName };
            var actionsIndex = AddListColumns(row,
                _vm?.ShowFavoritesButton ?? true, _vm?.ShowPinnedButton ?? true);

            // Колонка «Действия» всегда есть в сетке (нулевой ширины при выключенной
            // настройке, AddListColumns), но панель кнопок, как и в строке базы
            // и в заголовке, строится только когда колонка видима: иначе в нулевую
            // колонку попадали бы невидимые кнопки с обработчиками (issue #191).
            if (_vm?.ShowActionsColumn != false)
            {
                var actions = new ActionsPanel();
                actions.Children.Add(GroupRowActionButton(group, "IconEdit", "EditGroupCommand",
                    LocalizationManager.T("Main.EditGroupTooltip"), "TextSecondaryBrush"));
                // «Удалить» у служебных узлов скрыта: у них нет модели группы.
                var deleteBtn = GroupRowActionButton(group, "IconDelete", "DeleteGroupCommand",
                    LocalizationManager.T("Main.DeleteGroupTooltip"), colorHex: "#DC2626");
                deleteBtn.IsVisible = group.Marker != GroupNodeViewModel.PinnedMarker
                                      && group.Marker != GroupNodeViewModel.NoGroupMarker;
                actions.Children.Add(deleteBtn);
                Grid.SetColumn(actions, actionsIndex);
                row.Children.Add(actions);
            }

            Grid.SetColumn(caption, 0);
            Grid.SetColumnSpan(caption, actionsIndex);
            row.Children.Add(caption);

            header.Child = row;
            return header;
        }

        /// <summary>
        /// Панель кнопок колонки «Действия»: раскладывает по горизонтали столько
        /// кнопок, сколько помещается в колонку, остальные не показывает вовсе.
        /// Обычная панель с обрезкой оставляла бы половину значка у самой границы
        /// колонки «Сервер/База», а в версии для Windows лишние значки пропадают.
        /// </summary>
        private sealed class ActionsPanel : Panel
        {
            /// <summary>Зазор между кнопками.</summary>
            public double Spacing { get; init; }

            protected override Size MeasureOverride(Size availableSize)
            {
                double width = 0, height = 0;
                foreach (var child in Children)
                {
                    child.Measure(Size.Infinity);
                    width += child.DesiredSize.Width + Spacing;
                    height = Math.Max(height, child.DesiredSize.Height);
                }
                return new Size(Math.Min(width, availableSize.Width), height);
            }

            protected override Size ArrangeOverride(Size finalSize)
            {
                double total = 0;
                var fit = 0;
                foreach (var child in Children)
                {
                    var next = total + child.DesiredSize.Width + (fit > 0 ? Spacing : 0);
                    if (next > finalSize.Width)
                        break;
                    total = next;
                    fit++;
                }

                var x = Math.Max(0, (finalSize.Width - total) / 2);
                for (var i = 0; i < Children.Count; i++)
                {
                    var child = Children[i];
                    if (i >= fit)
                    {
                        child.Arrange(new Rect(0, 0, 0, 0));
                        continue;
                    }
                    if (i > 0)
                        x += Spacing;
                    var size = child.DesiredSize;
                    child.Arrange(new Rect(x, Math.Max(0, (finalSize.Height - size.Height) / 2), size.Width, size.Height));
                    x += size.Width;
                }
                return finalSize;
            }
        }

        /// <summary>
        /// Кнопка действия в колонке «Действия» строки группы: иконка, команда из вьюмодели,
        /// параметром служит узел группы строки.
        /// </summary>
        private Button GroupRowActionButton(GroupNodeViewModel group, string iconKey, string commandPath, string tooltip,
            string? brushKey = null, string? colorHex = null)
        {
            var button = new Button
            {
                Content = colorHex is not null
                    ? IconHelper.MakeIcon(iconKey, UiMetrics.Scaled(15), new SolidColorBrush(Color.Parse(colorHex)))
                    : IconHelper.MakeIcon(iconKey, UiMetrics.Scaled(15), brushKey ?? "TextSecondaryBrush"),
                Margin = new Thickness(1, 0),
                MinWidth = 0,
                MinHeight = 0,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                CommandParameter = group
            };
            button.Styled(Themes.ControlThemes.IconButton);
            ToolTip.SetTip(button, tooltip);
            // Команда живёт во вьюмодели, а контекстом строки служит узел группы.
            button.Bind(Button.CommandProperty, new Binding(commandPath) { Source = _vm });
            return button;
        }

        /// <summary>
        /// Колонки строки списка. Ведущих ровно пять и они одинаковы у заголовка,
        /// строки группы и строки базы: место под кнопки групп, компенсатор отступа
        /// дерева, звезда, булавка, название (MainWindow.xaml:495-512, 893-905
        /// и 1077-1088). Одинаковый набор ведущих колонок и есть причина, по которой
        /// значения строк стоят ровно под своими заголовками: дальше идут колонки
        /// значений с «Действиями» на своём месте.
        /// </summary>
        /// <param name="compensator">
        /// Колонка-компенсатор заголовка: её ширину подбирает <see cref="AlignHeaderToRows"/>.
        /// У строк компенсатор всегда нулевой, поэтому там передаётся null.
        /// </param>
        /// <returns>Индекс колонки «Действия».</returns>
        private int AddListColumns(Grid grid, bool showFavorite, bool showPin,
            ColumnDefinition? compensator = null)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(GroupButtonsColumnWidth) });
            grid.ColumnDefinitions.Add(compensator ?? new ColumnDefinition { Width = new GridLength(0) });
            // Резерв под переключатель тегов снят вместе с переносом кнопок
            // в панель команд: колонка пустая и лишнего отступа слева от
            // «Названия» давать не должна (MainWindow.xaml:656 и 1030).
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(showFavorite ? FavoriteColumnWidth : 0)
            });
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(showPin ? PinColumnWidth : 0)
            });
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = NameColumnLength(),
                MinWidth = MinColumnWidth
            });

            var columns = ListColumns();
            var actionsOffset = ActionsOffsetInColumns(columns);
            // Скрытая колонка «Действия» остаётся в сетке нулевой ширины (issue #158):
            // индексы и выравнивание остальных колонок и заголовка не меняются, а на
            // экране колонка просто не занимает места.
            var actionsWidth = _vm?.ShowActionsColumn != false ? ActionsColumnWidth : 0;
            var actionsIndex = -1;
            for (var i = 0; i < columns.Count; i++)
            {
                if (i == actionsOffset)
                {
                    actionsIndex = grid.ColumnDefinitions.Count;
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(actionsWidth) });
                }
                grid.ColumnDefinitions.Add(
                    new ColumnDefinition { Width = new GridLength(columns[i].Width), MinWidth = MinColumnWidth });
            }
            if (actionsOffset >= columns.Count)
            {
                actionsIndex = grid.ColumnDefinitions.Count;
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(actionsWidth) });
            }
            return actionsIndex;
        }

        private Control BuildInfobaseRow(Infobase ib)
        {
            // Карточка с фоном/границей из темы; hover и выделение отслеживает сама
            // (см. InfobaseRowCard): обычное → CardBackgroundBrush, hover → ItemHoverBrush,
            // выделено → ItemSelectedBrush + AccentBrush-граница.
            var card = new InfobaseRowCard();

            var grid = new Grid();
            // Слева направо: звезда, булавка, иконка типа подключения, имя базы,
            // дальше колонки значений. Звезда и булавка повторяют колонки заголовка
            // теми же ширинами и подчиняются тем же настройкам.
            var showFavorite = _vm?.ShowFavoritesButton ?? true;
            var showPin = _vm?.ShowPinnedButton ?? true;
            AddListColumns(grid, showFavorite, showPin);
            var columns = ListColumns();
            var actionsOffset = ActionsOffsetInColumns(columns);

            // Звезда, булавка, значок подключения и имя лежат в одной горизонтальной
            // панели, которая занимает все пять ведущих колонок (MainWindow.xaml:1152).
            // Так их собственная ширина не двигает колонки значений: те начинаются
            // после ведущих и стоят под своими заголовками. Отступ вложенности
            // ставит контейнер строки, панель его получает от него же.
            var lead = new StackPanel
            {
                Name = LeadBlockName,
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                // Обрезка по границе колонки «Название»: у автора имя тоже лежит
                // в горизонтальной панели и ширины не знает, но там оно упирается
                // в край окна, а у нас налезало бы на колонки значений.
                ClipToBounds = true
            };
            // Отступ вложенности: сдвигается только ведущий блок, сама строка
            // остаётся у левого края (MainWindow.xaml:1155).
            lead[!StackPanel.MarginProperty] = new Binding(nameof(TreeViewItem.Level))
            {
                RelativeSource = new RelativeSource
                {
                    Mode = RelativeSourceMode.FindAncestor,
                    AncestorType = typeof(TreeViewItem)
                },
                Converter = LeveledTreeViewItem.LeadIndent
            };

            if (showFavorite)
            {
                // Номер слота Alt+N идёт плашкой сразу за звездой и внутри той же
                // кнопки, как в разметке (MainWindow.xaml:1180). Прежде он был
                // наложен на угол колонки мелкой цифрой без подложки.
                var favorite = RowMarkButton(card, ib, "IconFavorite", "FavoriteBrush",
                    nameof(Infobase.IsFavorite), () => ib.IsFavorite,
                    LocalizationManager.T("Main.ToggleFavoriteTooltip"), "ToggleFavoriteForCommand",
                    FavoriteSlotBadge(card, ib));
                lead.Children.Add(favorite);
            }

            if (showPin)
            {
                var pin = RowMarkButton(card, ib, "IconPin", "AccentBrush",
                    nameof(Infobase.IsPinned), () => ib.IsPinned,
                    LocalizationManager.T("Main.TogglePinTooltip"), "TogglePinForCommand");
                lead.Children.Add(pin);
            }

            // Иконка статуса базы слева: тип подключения (папка / глобус / сеть)
            // или «недоступна». Цвет зависит от статуса: янтарный — файловая,
            // синий — веб, фиолетовый — клиент-сервер, красный — недоступна.
            var connectionIconKey = ib.StatusIconKey;

            // Значок идёт без подложки и рамки: в разметке это голый Path 14 на 14
            // с отступом 6 справа (MainWindow.xaml:1234). Коробка вокруг него была
            // нашей отсебятиной.
            var iconBox = IconHelper.MakeIcon(connectionIconKey, UiMetrics.RowIcon,
                new SolidColorBrush(Color.Parse(ib.StatusColorHex)));
            iconBox.HorizontalAlignment = HorizontalAlignment.Center;
            iconBox.VerticalAlignment = VerticalAlignment.Center;
            iconBox.Margin = new Thickness(0, 0, 6, 0);
            ToolTip.SetTip(iconBox, ib.StatusDisplay);

            lead.Children.Add(iconBox);

            // Правая колонка: имя (крупно) + строки вторичной информации.
            // В компактном режиме уменьшаем и межстрочный промежуток, чтобы строки с
            // полным набором метаданных тоже «сжимались», а не оставались прежней высоты.
            var content = new StackPanel { Spacing = UiMetrics.Scaled(2), VerticalAlignment = VerticalAlignment.Center };

            // Имя лежит в горизонтальной панели, как в разметке (MainWindow.xaml:1244),
            // и своей ширины не знает: обрезку по краю колонки «Название» даёт
            // ClipToBounds ведущего блока. Многоточия при этом не будет, у автора
            // имя тоже не подрезается.
            var name = new TextBlock
            {
                Text = ib.Name,
                FontSize = UiMetrics.RowNameFont,
                FontWeight = FontWeight.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            ThemeBrushes.Bind(name, TextBlock.ForegroundProperty, "TextPrimaryBrush");
            content.Children.Add(name);

            // Второй подписи под именем в разметке нет: первая строка это значок
            // статуса и имя, а вторая отдана тегам (MainWindow.xaml:1230-1247).
            // Расположение живёт в своей колонке и в сведениях правой панели.
            lead.Children.Add(content);
            grid.Children.Add(lead);
            Grid.SetColumn(lead, 0);
            Grid.SetColumnSpan(lead, NameRowColumn + 1);

            var dataColumn = NameRowColumn + 1;
            for (var i = 0; i < columns.Count; i++)
            {
                if (i == actionsOffset)
                    dataColumn++;
                var value = ColumnValue(ib, columns[i].Key);
                var cell = SecondaryText(string.IsNullOrWhiteSpace(value) ? string.Empty : value, card);
                cell.VerticalAlignment = VerticalAlignment.Center;
                // Отступ ячейки значения как в разметке (MainWindow.xaml:1249):
                // без него значения стояли на 6 пикселей левее своих заголовков,
                // у которых такой отступ есть.
                cell.HorizontalAlignment = HorizontalAlignment.Left;
                cell.Margin = new Thickness(6, 0, 6, 0);
                if (columns[i].Key == "Version")
                {
                    // Двойной щелчок открывает выбор версии платформы, как
                    // в разметке WPF (MainWindow.xaml:1230). До этого окно
                    // PlatformVersionPickerWindow собиралось, но из интерфейса
                    // Linux-версии было недостижимо.
                    ToolTip.SetTip(cell, LocalizationManager.T("Main.PlatformVersionTooltip"));
                    cell.DoubleTapped += (_, e) =>
                    {
                        e.Handled = true;
                        _vm?.PickPlatformVersionFor(ib);
                    };
                }
                grid.Children.Add(cell);
                Grid.SetColumn(cell, dataColumn);
                dataColumn++;
            }

            // Кнопки действий в колонке «Действия» (после колонки «Режим запуска»):
            // запуск, конфигуратор, изменить настройки, очистить кеш, удалить.
            // Колонка при этом нулевой ширины, поэтому панель не строится вовсе
            // и не остаётся невидимых обработчиков на каждую строку (issue #158).
            var actionsCol = NameRowColumn + 1 + actionsOffset;
            ActionsPanel? actions = null;
            if (_vm?.ShowActionsColumn != false)
            {
                // Три действия, как в разметке WPF (MainWindow.xaml:1497-1517):
                // запуск, конфигуратор, очистка кеша. Правка настроек и удаление
                // в строке не показываются, они остаются в контекстном меню.
                actions = new ActionsPanel { Spacing = 1 };
                actions.Children.Add(RowActionButton(ib, "IconPlay", "LaunchEnterpriseCommand", LocalizationManager.T("Main.LaunchEnterpriseTooltip")));
                actions.Children.Add(RowActionButton(ib, "IconWrench", "LaunchConfiguratorCommand", LocalizationManager.T("Main.LaunchConfiguratorSectionTooltip")));
                actions.Children.Add(RowActionButton(ib, "IconBroom", "ClearCacheCommand", LocalizationManager.T("Main.ClearCacheTooltip")));
                // Кнопки живут внутри панели, обрезанной по своей колонке: в узкой
                // колонке «Действия» лишние значки у автора пропадают, а у нас
                // рисовались поверх колонки «Сервер/База».
                grid.Children.Add(actions);
                Grid.SetColumn(actions, actionsCol);
            }

            if (_vm?.ShowTags == true)
            {
                // Теги идут второй строкой от левого края и до колонки действий,
                // а сами действия охватывают обе строки (MainWindow.xaml:1278
                // и 1318). У нас теги начинались с колонки имени и проходили под
                // действиями и остальными значениями.
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                if (actions is not null)
                    Grid.SetRowSpan(actions, 2);

                var tags = BuildRowTags(card, ib);
                // Тот же отступ вложенности, что и у ведущего блока
                // (MainWindow.xaml:1316): теги стоят под именем, а не левее его.
                tags[!Control.MarginProperty] = new Binding(nameof(TreeViewItem.Level))
                {
                    RelativeSource = new RelativeSource
                    {
                        Mode = RelativeSourceMode.FindAncestor,
                        AncestorType = typeof(TreeViewItem)
                    },
                    // Верхний отступ 2 сохраняется: привязка задаёт Margin целиком.
                    Converter = new FuncValueConverter<int, Thickness>(level =>
                        new Thickness(LeveledTreeViewItem.LeadIndentFor(level), 2, 0, 0))
                };
                grid.Children.Add(tags);
                Grid.SetRow(tags, 1);
                Grid.SetColumn(tags, 0);
                Grid.SetColumnSpan(tags, actionsCol);
            }

            card.Child = grid;

            // Двойной клик по строке базы запускает её в режиме по умолчанию
            // (issue #201): «1С:Предприятие» или «Конфигуратор» согласно DefaultLaunchMode.
            card.DoubleTapped += (_, _) =>
            {
                if (_vm is not { } vm)
                    return;
                vm.SelectedInfobase = ib;
                if (string.Equals(ib.DefaultLaunchMode, "Configurator", StringComparison.Ordinal))
                    vm.LaunchConfiguratorCommand.Execute(null);
                else
                    vm.LaunchEnterpriseCommand.Execute(null);
            };

            return card;
        }

        /// <summary>
        /// Теги базы под её именем: чип с крестиком на каждый тег и кнопка
        /// «+ тег». Панель перестраивается по уведомлению самой базы, поэтому
        /// после правки тегов строку пересобирать не нужно.
        /// </summary>
        private Control BuildRowTags(InfobaseRowCard card, Infobase infobase)
        {
            var panel = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
            var chipSubscriptions = new List<IDisposable>();

            void Fill()
            {
                foreach (var subscription in chipSubscriptions)
                    subscription.Dispose();
                chipSubscriptions.Clear();
                panel.Children.Clear();

                foreach (var tag in infobase.Tags)
                    panel.Children.Add(BuildTagChip(infobase, tag, chipSubscriptions));

                panel.Children.Add(BuildAddTagButton(infobase, chipSubscriptions));
            }

            void OnInfobaseChanged(object? _, PropertyChangedEventArgs e)
            {
                if (e.PropertyName == nameof(Infobase.Tags))
                    Fill();
            }

            card.AddSubscription(() =>
            {
                infobase.PropertyChanged += OnInfobaseChanged;
                Fill();
                return new ActionDisposable(() =>
                {
                    infobase.PropertyChanged -= OnInfobaseChanged;
                    foreach (var subscription in chipSubscriptions)
                        subscription.Dispose();
                    chipSubscriptions.Clear();
                });
            });

            return panel;
        }

        /// <summary>Чип тега: клик отбирает базы по тегу, крестик убирает тег у базы.</summary>
        private Control BuildTagChip(Infobase infobase, string tag, ICollection<IDisposable> subscriptions)
        {
            var text = new TextBlock
            {
                Text = tag,
                FontSize = UiMetrics.ScaledFont(11),
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = UiMetrics.Scaled(180),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            ToolTip.SetTip(text, tag);
            ThemeBrushes.Bind(text, TextBlock.ForegroundProperty, "AccentBrush");

            var name = new Button
            {
                Content = text,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                MinWidth = 0,
                MinHeight = 0,
                Cursor = new Cursor(StandardCursorType.Hand),
                CommandParameter = tag
            };
            ToolTip.SetTip(name, LocalizationManager.T("Main.ShowTagBases"));
            name.Bind(Button.CommandProperty, new Binding("SearchByTagCommand") { Source = _vm });

            // Крестик в разметке это знак × кеглем 11 в коробке 12 на 12
            // (MainWindow.xaml:1357), а не контур.
            var removeGlyph = new TextBlock
            {
                Text = "\u00D7",
                FontSize = UiMetrics.ScaledFont(11),
                LineHeight = UiMetrics.Scaled(12),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            ThemeBrushes.Bind(removeGlyph, TextBlock.ForegroundProperty, "TextSecondaryBrush");

            var remove = new Button
            {
                Content = removeGlyph,
                Width = UiMetrics.Scaled(12),
                Height = UiMetrics.Scaled(12),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                Margin = new Thickness(2, 0, 0, 0),
                MinWidth = 0,
                MinHeight = 0,
                Cursor = new Cursor(StandardCursorType.Hand),
                // Форма параметра та же, что в WPF-версии: база и тег.
                CommandParameter = new object[] { infobase, tag }
            };
            ToolTip.SetTip(remove, LocalizationManager.T("Main.RemoveTag"));
            remove.Bind(Button.CommandProperty, new Binding("RemoveTagCommand") { Source = _vm });

            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(name);
            row.Children.Add(remove);

            // Рамки у чипа в строке нет, подложка ItemHover и скругление 3:
            // рамка была нашей отсебятиной (MainWindow.xaml:1335-1338).
            var chip = new Border
            {
                CornerRadius = new CornerRadius(3),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(4, 0),
                Margin = new Thickness(0, 0, 3, 2),
                Height = UiMetrics.Scaled(16),
                VerticalAlignment = VerticalAlignment.Center,
                Child = row
            };
            ThemeBrushes.Bind(chip, Border.BackgroundProperty, "ItemHoverBrush");
            return chip;
        }

        /// <summary>Складывает подписку в приёмник, пропуская пустую (Application ещё не поднят).</summary>
        private static void Track(ICollection<IDisposable> sink, IDisposable? subscription)
        {
            if (subscription is not null)
                sink.Add(subscription);
        }

        /// <summary>
        /// Кнопка «+ тег» в конце списка тегов строки. По клику раскрывается поле ввода
        /// прямо в строке: Enter добавляет тег, Esc отменяет, потеря фокуса сохраняет введённое.
        /// </summary>
        private Control BuildAddTagButton(Infobase infobase, ICollection<IDisposable> subscriptions)
        {
            var text = new TextBlock
            {
                Text = LocalizationManager.T("Main.AddTagShort"),
                FontSize = UiMetrics.ScaledFont(11),
                VerticalAlignment = VerticalAlignment.Center
            };
            ThemeBrushes.Bind(text, TextBlock.ForegroundProperty, "TextSecondaryBrush");

            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
            content.Children.Add(IconHelper.MakeIcon("IconTag", UiMetrics.Scaled(11), "TextSecondaryBrush"));
            content.Children.Add(text);

            var button = new Button
            {
                Content = content,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                // Отступ 2,0, высота 16 и поле 2 слева (MainWindow.xaml:1377).
                Padding = new Thickness(2, 0),
                Height = UiMetrics.Scaled(16),
                Margin = new Thickness(2, 0, 0, 0),
                MinWidth = 0,
                MinHeight = 0,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = new Cursor(StandardCursorType.Hand)
            };
            ToolTip.SetTip(button, LocalizationManager.T("Main.AddTag"));

            // Поле ввода тега показывается на месте кнопки во время редактирования.
            var input = new TextBox
            {
                // Числа и кисти из разметки (MainWindow.xaml:1390): ширина 120,
                // кегль 12, отступ 6,3, высота не меньше 24, поле 4 слева,
                // акцентная рамка толщиной 1.
                Watermark = LocalizationManager.T("Main.AddTag"),
                Width = UiMetrics.Scaled(120),
                FontSize = UiMetrics.ScaledFont(12),
                Padding = new Thickness(6, 3),
                MinHeight = UiMetrics.Scaled(24),
                Margin = new Thickness(4, 0, 0, 0),
                BorderThickness = new Thickness(1),
                VerticalContentAlignment = VerticalAlignment.Center,
                IsVisible = false
            };
            ThemeBrushes.Bind(input, TemplatedControl.BackgroundProperty, "CardBackgroundBrush");
            ThemeBrushes.Bind(input, TemplatedControl.ForegroundProperty, "TextPrimaryColorBrush");
            ThemeBrushes.Bind(input, TemplatedControl.BorderBrushProperty, "AccentBrush");
            ThemeBrushes.Bind(input, TextBox.CaretBrushProperty, "TextPrimaryColorBrush");
            ToolTip.SetTip(input, LocalizationManager.T("Main.EnterTagHint"));

            void ShowEditor()
            {
                button.IsVisible = false;
                input.Text = string.Empty;
                input.IsVisible = true;
                input.Focus();
                input.SelectAll();
            }

            void HideEditor()
            {
                input.IsVisible = false;
                button.IsVisible = true;
            }

            void Commit()
            {
                if (!input.IsVisible)
                    return;

                var tag = input.Text?.Trim() ?? string.Empty;
                HideEditor();
                input.Text = string.Empty;

                if (tag.Length == 0)
                    return;

                if (_vm?.AddTagInlineCommand.CanExecute(null) == true)
                    _vm.AddTagInlineCommand.Execute(new object[] { infobase, tag });
            }

            void Cancel()
            {
                if (!input.IsVisible)
                    return;

                input.Text = string.Empty;
                HideEditor();
            }

            button.Click += (_, _) => ShowEditor();

            input.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    Cancel();
                    e.Handled = true;
                }
                else if (e.Key == Key.Enter)
                {
                    Commit();
                    e.Handled = true;
                }
            };

            // Потеря фокуса сохраняет введённый тег, как в WPF-версии. Откладываем
            // обработку: клик вне поля сначала переводит фокус, затем фиксируем ввод.
            input.LostFocus += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (input.IsVisible)
                    Commit();
            });

            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(button);
            row.Children.Add(input);
            return row;
        }

        /// <summary>
        /// Кнопка-маркер в строке базы: звезда «избранное» или булавка «закреплено».
        /// Цвет иконки следит за состоянием самой базы, поэтому после переключения
        /// строку не нужно пересобирать, и за кистями темы он тоже следует.
        /// </summary>
        /// <summary>
        /// Номер слота Alt+N у избранной базы. Пусто, если слот не назначен:
        /// их девять, а избранных может быть больше.
        /// </summary>
        private Control FavoriteSlotBadge(InfobaseRowCard card, Infobase infobase)
        {
            var text = new TextBlock
            {
                FontSize = UiMetrics.ScaledFont(10),
                FontWeight = FontWeight.Bold,
                // Цвет подписи в разметке задан числом и одинаков в обеих темах.
                Foreground = new SolidColorBrush(Color.Parse("#1C1917")),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };

            // Отступы и минимум плашки ужаты против разметки: там колонка звезды
            // шириной 28 и содержимое кнопки выходит за её край, а WPF ничего
            // не обрезает. Здесь оно обрезалось, поэтому звезда с плашкой
            // укладываются в 30 целиком.
            var host = new Border
            {
                Child = text,
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(3, 0, 3, 1),
                Margin = new Thickness(2, 0, 0, 0),
                MinWidth = UiMetrics.Scaled(13),
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            ThemeBrushes.Bind(host, Border.BackgroundProperty, "FavoriteBrush");

            void Apply()
            {
                text.Text = infobase.FavoriteHotkeyDisplay;
                // Разметка WPF гасит плашку и по номеру, и по самой звезде
                // (MainWindow.xaml:1156-1183): без второго условия номер
                // остаётся висеть у базы, которую убрали из избранного.
                host.IsVisible = infobase.IsFavorite && !string.IsNullOrEmpty(text.Text);
            }

            void OnChanged(object? _, PropertyChangedEventArgs e)
            {
                if (e.PropertyName == nameof(Infobase.FavoriteHotkeyDisplay)
                    || e.PropertyName == nameof(Infobase.FavoriteHotkeyNumber)
                    || e.PropertyName == nameof(Infobase.IsFavorite))
                    Apply();
            }

            card.AddSubscription(() =>
            {
                infobase.PropertyChanged += OnChanged;
                Apply();
                return new ActionDisposable(() => infobase.PropertyChanged -= OnChanged);
            });

            return host;
        }

        private Button RowMarkButton(InfobaseRowCard card, Infobase infobase, string iconKey,
            string activeBrushKey, string stateProperty, Func<bool> isActive,
            string tooltip, string commandPath, Control? trailing = null)
        {
            var iconHost = IconHelper.MakeIcon(iconKey, UiMetrics.Scaled(14), out var icon);
            Control content = iconHost;
            if (trailing is not null)
            {
                var pair = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                pair.Children.Add(iconHost);
                pair.Children.Add(trailing);
                content = pair;
            }

            IBrush? active = null;
            IBrush? idle = null;
            void ApplyState()
            {
                var brush = isActive() ? active : idle;
                if (brush is not null)
                    icon.Fill = brush;
            }

            card.AddSubscription(() => Application.Current?.GetResourceObservable(activeBrushKey)
                .Subscribe(new BrushObserver(brush => active = brush, ApplyState)));
            card.AddSubscription(() => Application.Current?.GetResourceObservable("TextSecondaryBrush")
                .Subscribe(new BrushObserver(brush => idle = brush, ApplyState)));

            void OnInfobaseChanged(object? _, PropertyChangedEventArgs e)
            {
                if (e.PropertyName == stateProperty)
                    ApplyState();
            }

            card.AddSubscription(() =>
            {
                infobase.PropertyChanged += OnInfobaseChanged;
                // Состояние могло измениться, пока строка была отсоединена.
                ApplyState();
                return new ActionDisposable(() => infobase.PropertyChanged -= OnInfobaseChanged);
            });

            var button = new Button
            {
                Content = content,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                MinWidth = 0,
                MinHeight = 0,
                // Своей ширины у кнопки нет: она лежит в ведущем блоке строки
                // и пакуется вплотную к соседям, как в разметке.
                HorizontalAlignment = HorizontalAlignment.Left,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = new Cursor(StandardCursorType.Hand),
                CommandParameter = infobase
            };
            ToolTip.SetTip(button, tooltip);
            // Команда живёт во вьюмодели, а контекстом строки служит сама база,
            // поэтому источник привязки указывается явно.
            button.Bind(Button.CommandProperty, new Binding(commandPath) { Source = _vm });
            return button;
        }

        /// <summary>
        /// Кнопка действия в колонке «Действия» строки базы: иконка, команда из вьюмодели,
        /// параметром служит сама информационная база строки.
        /// </summary>
        /// <param name="colorHex">
        /// Явный цвет значка. У автора кнопки строки вторичного цвета, кроме
        /// удаления: оно красное.
        /// </param>
        private Control RowActionButton(Infobase ib, string iconKey, string commandPath, string tooltip,
            string? colorHex = null)
        {
            Control glyph;
            if (colorHex is null)
            {
                glyph = IconHelper.MakeIcon(iconKey, UiMetrics.Scaled(15), "TextSecondaryBrush");
            }
            else
            {
                glyph = IconHelper.MakeIcon(iconKey, UiMetrics.Scaled(15),
                    new SolidColorBrush(Color.Parse(colorHex)));
            }

            // Оформление берёт тема IconButton разметки: у автора все пять команд
            // строки идут этим стилем с полем 1,0 (MainWindow.xaml:1282).
            // Своя кнопка была нужна, пока темы не было: штатная тема Fluent красит
            // не саму кнопку, а её внутренний ContentPresenter, и локальный
            // прозрачный фон её не перебивал.
            var button = new Button
            {
                Content = glyph,
                Margin = new Thickness(1, 0),
                MinWidth = 0,
                MinHeight = 0,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                CommandParameter = ib
            };
            button.Styled(Themes.ControlThemes.IconButton);
            ToolTip.SetTip(button, tooltip);
            // Команда живёт во вьюмодели, а контекстом строки служит сама база.
            button.Bind(Button.CommandProperty, new Binding(commandPath) { Source = _vm });
            return button;
        }

        /// <summary>Освобождение по вызову действия: снятие подписки на событие модели.</summary>
        private sealed class ActionDisposable : IDisposable
        {
            private Action? _dispose;

            public ActionDisposable(Action dispose) => _dispose = dispose;

            public void Dispose()
            {
                var action = _dispose;
                _dispose = null;
                action?.Invoke();
            }
        }

        /// <summary>Объединяет непустые фрагменты в одну строку с разделителем «•».</summary>
        private static string JoinSegments(params string?[] parts)
        {
            var nonEmpty = parts
                .Select(p => (p ?? string.Empty).Trim())
                .Where(p => p.Length > 0 && p != "—")
                .ToList();
            return nonEmpty.Count == 0 ? string.Empty : string.Join("  •  ", nonEmpty);
        }

        /// <summary>Строка вторичной информации: приглушённый текст из темы с подсказкой по полному значению.</summary>
        private static TextBlock SecondaryText(string text, InfobaseRowCard? owner = null)
        {
            var block = new TextBlock
            {
                Text = text,
                FontSize = UiMetrics.RowSecondaryFont,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            ToolTip.SetTip(block, text);
            if (owner is null)
                ThemeBrushes.Bind(block, TextBlock.ForegroundProperty, "TextSecondaryBrush");
            else
                ThemeBrushes.Bind(block, TextBlock.ForegroundProperty, "TextSecondaryBrush");
            return block;
        }

        private Control BuildRightPanel()
        {
            // Компактная правая панель: primary-запуски на всю ширину, вторичные
            // действия — списком в один столбец, секции без тяжёлых карточек.
            // Отступы держит само содержимое, а не Padding у ScrollViewer:
            // его отступ не входит в прокручиваемую высоту, и нижняя кнопка
            // становилась недостижимой, от неё была видна одна рамка.
            // Верхний отступ приведён к стандартному (12), как в WPF-версии и левой
            // колонке: прежний зазор 56 «отодвигал» блок запуска вниз и выглядел
            // «конским отступом перед кнопками справа» (issue #167). Теперь кнопка
            // запуска поднята и стоит вровень с верхними сегментами левой колонки.
            var panel = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Spacing = UiMetrics.ActionGridGap,
                Margin = new Thickness(12, 12)
            };
            _rightPanelContent = panel;

            // Заголовок базы
            var nameBlock = new TextBlock
            {
                FontSize = UiMetrics.ScaledFont(16),
                FontWeight = FontWeight.SemiBold,
                TextWrapping = TextWrapping.Wrap
            };
            nameBlock.Bind(TextBlock.TextProperty, new Binding("RightPanelTitle"));

            var groupBlock = new TextBlock
            {
                FontSize = UiMetrics.ScaledFont(12),
                Margin = new Thickness(0, 2, 0, 0),
                TextWrapping = TextWrapping.Wrap
            };
            ThemeBrushes.Bind(groupBlock, TextBlock.ForegroundProperty, "TextSecondaryColorBrush");
            groupBlock.Bind(TextBlock.TextProperty, new Binding("RightPanelSubtitle"));

            // Заголовок сеткой, а не горизонтальной панелью: в панели подпись
            // получала бы бесконечную ширину и не переносилась бы по словам.
            var header = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            // Значок базы показывается только когда база выбрана: при выбранной
            // группе и при пустом выборе он висел бы один без подписи.
            // Значок 28 акцентной кистью и правым полем 10, как в разметке
            // (MainWindow.xaml:1709). Контур привязывается к самому Path внутри
            // холста: у Viewbox такого свойства нет, и привязка молча не работала.
            var headerIcon = IconHelper.MakeIcon("IconDatabase", UiMetrics.Scaled(28), out var headerIconPath);
            headerIconPath.Bind(Avalonia.Controls.Shapes.Path.DataProperty,
                new Binding("RightPanelIconKey") { Converter = IconKeyConverter });
            ThemeBrushes.Bind(headerIconPath, Avalonia.Controls.Shapes.Path.FillProperty, "AccentBrush");
            headerIcon.Margin = new Thickness(0, 0, 10, 0);
            headerIcon.Bind(Control.IsVisibleProperty, new Binding("HasRightPanelIcon"));
            header.Children.Add(headerIcon);
            var headerText = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
            headerText.Children.Add(nameBlock);
            headerText.Children.Add(groupBlock);
            header.Children.Add(headerText);
            Grid.SetColumn(headerText, 1);

            // Подсказка «выберите базу» отдельной строкой под заголовком,
            // как в WPF: там она видна, пока база не выбрана.
            var hintBlock = new TextBlock
            {
                FontSize = UiMetrics.ScaledFont(11.5),
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.7,
                Margin = new Thickness(0, 0, 0, 4)
            };
            hintBlock.Bind(TextBlock.TextProperty, new Binding("RightPanelHint"));
            // Подсказка «выберите базу» скрыта и в компактном режиме правой панели
            // (issue #149): иначе при выделении группы сверху появлялась лишняя строка
            // информации. Видимость — производное от «подробности включены» и «база
            // не выбрана», поэтому вынесено в свойство модели ShowRightPanelHint.
            hintBlock.Bind(Control.IsVisibleProperty, new Binding("ShowRightPanelHint"));
            // Заголовок выбранной базы скрыт вместе с подробностями и при пустом
            // выборе, как в разметке (MainWindow.xaml:1694-1706): раньше он висел
            // в узкой панели всегда.
            header.Bind(Control.IsVisibleProperty, new Binding("ShowRightPanelDetails"));
            panel.Children.Add(header);
            panel.Children.Add(hintBlock);

            // Запуск 1С:Предприятие (primary) — акцентная кнопка запуска с меню
            // дополнительных вариантов.
            var launchEnterpriseBlock = BuildLaunchSplitButton(
                "IconPlay",
                LocalizationManager.T("Main.Enterprise"),
                "LaunchEnterpriseCommand",
                LocalizationManager.T("Main.LaunchEnterpriseTooltip"),
                primary: true,
                new (string, string, string?, string?)[]
                {
                    (LocalizationManager.T("Main.LaunchWithParams"), "LaunchEnterpriseWithParamsCommand", "IconTune", "#0EA5E9"),
                    (LocalizationManager.T("Main.LaunchWithAuth"), "LaunchEnterpriseWithAuthCommand", "IconAccountKey", "#8B5CF6")
                });

            // Конфигуратор — secondary full-width (без отдельной тяжёлой карточки).
            var launchConfiguratorBlock = BuildLaunchSplitButton(
                "IconWrench",
                LocalizationManager.T("Main.SectionConfigurator"),
                "LaunchConfiguratorCommand",
                LocalizationManager.T("Main.LaunchConfiguratorSectionTooltip"),
                primary: false,
                new (string, string, string?, string?)[]
                {
                    (LocalizationManager.T("Main.LaunchWithParams"), "LaunchConfiguratorWithParamsCommand", "IconTune", "#0EA5E9")
                });

            // Остальные действия («Очистить кеш», «Изменить настройки», «Удалить»,
            // «Добавить») перенесены в колонку «Действия» строк базы и верхнюю панель
            // команд. Здесь остаются вторичные действия списком.
            // Под «Действиями» у автора ровно три кнопки: Предприятие, Конфигуратор
            // и штатный стартер. «Открыть каталог» и «Ярлык на рабочем столе»
            // у него живут в контекстном меню строки, там они есть и у нас.
            var starterBlock = BuildActionList(
                CompactActionButton("IconApplication", LocalizationManager.T("Main.NativeStarter"), "OpenNativeStarterCommand", LocalizationManager.T("Main.NativeStarterTooltipLinux"), "#F59E0B", colorTextToo: false,
                    iconSize: UiMetrics.Scaled(15), widePadding: new Thickness(8, 6), narrowPadding: new Thickness(6, 8))
            );

            // Переход по ссылке идёт после карточки сессии, как в разметке.
            var byLinkBlock = BuildActionList(
                CompactActionButtonBound("IconLink", "OpenByLinkCaption", "OpenInfobaseByLinkCommand", LocalizationManager.T("Main.OpenLinkTooltip"), "#0EA5E9", "IconArrowRight", colorTextToo: false,
                    iconSize: UiMetrics.Scaled(16), widePadding: new Thickness(14, 11), narrowPadding: new Thickness(6, 8))
            );

            // Бейджи «Избранное» и «Закреплено», как в разметке WPF: цвета там
            // заданы явно и одинаковы в обеих темах, поэтому берутся числом.
            var badges = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };

            var favoriteBadge = Badge("#FEF3C7");
            favoriteBadge.Child = BadgeContent("IconStar", "#F59E0B", textBinding:
                new MultiBinding
                {
                    StringFormat = "{0} {1}",
                    Bindings =
                    {
                        new Binding { Source = LocalizationManager.T("Main.Favorites") },
                        new Binding("SelectedInfobase.FavoriteHotkeyDisplay")
                    }
                }, themeBrushKey: "FavoriteBrush");
            favoriteBadge.Bind(Control.IsVisibleProperty, new Binding("SelectedInfobase.IsFavorite"));
            badges.Children.Add(favoriteBadge);

            var pinnedBadge = Badge("#EDE9FE");
            pinnedBadge.Child = BadgeContent("IconPin", "#8B5CF6", "#5B21B6",
                new Binding { Source = LocalizationManager.T("Main.PinnedLabel") });
            pinnedBadge.Bind(Control.IsVisibleProperty, new Binding("SelectedInfobase.IsPinned"));
            badges.Children.Add(pinnedBadge);

            var tagsHeader = ThemedIconAndText("IconTag", LocalizationManager.T("Main.Tags"),
                "TextSecondaryColorBrush", 14, centered: false);
            var tagsList = new ItemsControl
            {
                Margin = new Thickness(0, 0, 0, 4),
                ItemsPanel = new FuncTemplate<Panel?>(() => new WrapPanel()),
                ItemTemplate = new FuncDataTemplate<string>((tag, _) =>
                {
                    var chip = new Border
                    {
                        CornerRadius = new CornerRadius(8),
                        Padding = new Thickness(8, 3),
                        Margin = new Thickness(0, 0, 4, 4),
                        BorderThickness = new Thickness(1),
                        Child = ThemedIconAndText("IconTag", tag ?? "", "AccentColorBrush", 10, centered: false)
                    };
                    ThemeBrushes.Bind(chip, Border.BackgroundProperty, "ItemHoverBrush");
                    ThemeBrushes.Bind(chip, Border.BorderBrushProperty, "BorderColorBrush");
                    return chip;
                })
            };
            tagsList.Bind(ItemsControl.ItemsSourceProperty, new Binding("SelectedInfobase.Tags"));
            var tagsBlock = new StackPanel { Spacing = 4 };
            tagsBlock.Children.Add(tagsHeader);
            tagsBlock.Children.Add(tagsList);

            // Информация о подключении.
            // Блок сведений подчинён переключателю подробностей правой панели,
            // как в WPF: там по нему прячется та же таблица.
            var connectionLabel = SectionLabel(LocalizationManager.T("Main.SectionConnInfo"));
            var connectionCard = PlainCard(
                DetailRow(LocalizationManager.T("Main.Type"), new Binding("SelectedInfobase.ConnectionTypeDisplay")),
                DetailRow(LocalizationManager.T("Main.ServerPath"), new Binding("SelectedInfobase.ConnectionPathDisplay")),
                DetailRow(LocalizationManager.T("Column.ServerBase"), new Binding("SelectedInfobase.ServerDatabaseDisplay")),
                DetailRow(LocalizationManager.T("Main.ConnectionString"), new Binding("SelectedInfobase.ConnectionStringDisplay")),
                DetailRow(LocalizationManager.T("Main.Platform"), new Binding("SelectedInfobase.PlatformVersion")),
                DetailRow(LocalizationManager.T("Main.LaunchMode"), new Binding("SelectedInfobase.ParsedLaunchMode")),
                DetailRow(LocalizationManager.T("Main.Client"), new Binding("SelectedInfobase.ClientTypeDisplay")),
                DetailRow(LocalizationManager.T("Main.Bitness"), new Binding("SelectedInfobase.ArchitectureDisplay")),
                DetailRow(LocalizationManager.T("Main.Parameters"), new Binding("SelectedInfobase.LaunchParameters")),
                DetailRow(LocalizationManager.T("Main.LastLaunch"), new Binding("SelectedInfobase.LastLaunchDisplay")),
                DetailRow(LocalizationManager.T("Main.CacheSize"), new Binding("SelectedInfobase.CacheSizeDisplay")),
                DetailRow(LocalizationManager.T("Column.Configuration"), new Binding("SelectedInfobase.ConfigurationDisplay")));
            connectionCard.Bind(Control.IsVisibleProperty, new Binding("ShowConnectionInfo"));

            // Блок «Текущая сессия»: значения действуют только на следующий запуск.
            var sessionCard = BuildSessionCard();

            // Описание (стиль из ConfigurationManagement): значение TextPrimaryBrush, нижний отступ.
            var desc = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = UiMetrics.ScaledFont(12),
                Margin = new Thickness(0, 0, 0, 10)
            };
            ThemeBrushes.Bind(desc, TextBlock.ForegroundProperty, "TextPrimaryBrush");
            desc.Bind(TextBlock.TextProperty, new Binding("SelectedInfobase.Description"));
            var descriptionLabel = SectionLabel(LocalizationManager.T("Main.Description"), smallCaps: false);

            // Порядок блоков взят из разметки WPF: сведения о подключении, описание,
            // теги, затем действия и текущая сессия. Раньше действия стояли первыми.
            // Подписи секций подчинены тем же условиям, что и их содержимое:
            // иначе в компактной панели и без выбранной базы висели заголовки
            // без содержимого.
            badges.Bind(Control.IsVisibleProperty, new Binding("IsInfobaseSelected"));
            connectionLabel.Bind(Control.IsVisibleProperty, new Binding("ShowConnectionInfo"));
            descriptionLabel.Bind(Control.IsVisibleProperty, new Binding("ShowConnectionInfo"));
            desc.Bind(Control.IsVisibleProperty, new Binding("ShowConnectionInfo"));
            tagsBlock.Bind(Control.IsVisibleProperty, new Binding("ShowConnectionInfo"));

            panel.Children.Add(badges);
            panel.Children.Add(connectionLabel);
            panel.Children.Add(connectionCard);
            panel.Children.Add(descriptionLabel);
            panel.Children.Add(desc);
            panel.Children.Add(tagsBlock);

            // Линия и заголовок «Действия» перед кнопками запуска, как в разметке.
            // Обе видны только при показанных подробностях правой панели.
            var actionsSeparator = new Border
            {
                Height = 1,
                Margin = new Thickness(0, 4, 0, 12)
            };
            ThemeBrushes.Bind(actionsSeparator, Border.BackgroundProperty, "BorderColorBrush");
            actionsSeparator.Bind(Control.IsVisibleProperty, new Binding("ShowRightPanelDetails"));
            panel.Children.Add(actionsSeparator);

            var actionsLabel = new TextBlock
            {
                Text = LocalizationManager.T("Main.Actions"),
                FontSize = UiMetrics.ScaledFont(12),
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, 0, 0, 10)
            };
            ThemeBrushes.Bind(actionsLabel, TextBlock.ForegroundProperty, "TextSecondaryColorBrush");
            actionsLabel.Bind(Control.IsVisibleProperty, new Binding("ShowRightPanelDetails"));
            panel.Children.Add(actionsLabel);

            panel.Children.Add(launchEnterpriseBlock);
            panel.Children.Add(launchConfiguratorBlock);
            panel.Children.Add(starterBlock);

            // Линия между запуском и остальными действиями (MainWindow.xaml:2097).
            var afterStarterSeparator = new Border
            {
                Height = 1,
                Margin = new Thickness(0, 10, 0, 10),
                Opacity = 0.7
            };
            ThemeBrushes.Bind(afterStarterSeparator, Border.BackgroundProperty, "BorderColorBrush");
            panel.Children.Add(afterStarterSeparator);

            panel.Children.Add(sessionCard);

            // Линия между текущей сессией и переходом по ссылке видна вместе
            // с самой карточкой сессии (MainWindow.xaml:2191).
            var beforeByLinkSeparator = new Border { Height = 1, Margin = new Thickness(0, 6, 0, 10) };
            ThemeBrushes.Bind(beforeByLinkSeparator, Border.BackgroundProperty, "BorderColorBrush");
            beforeByLinkSeparator.Bind(Control.IsVisibleProperty, new Binding("ShowSessionLaunchPanel"));
            panel.Children.Add(beforeByLinkSeparator);

            panel.Children.Add(byLinkBlock);

            // Линия между переходом по ссылке и выходом, как в разметке.
            var exitSeparator = new Border { Height = 1, Margin = new Thickness(0, 10) };
            ThemeBrushes.Bind(exitSeparator, Border.BackgroundProperty, "BorderColorBrush");
            panel.Children.Add(exitSeparator);

            // Выход — компактная кнопка внизу, без лишней «карточки».
            // «Выход» у автора красный, #DC2626 в обеих темах.
            var exitBtn = CompactActionButton("IconExitToApp", LocalizationManager.T("Main.Exit"), "ExitCommand",
                LocalizationManager.T("Main.ExitTooltip"), "#DC2626", "IconClose",
                iconSize: UiMetrics.Scaled(16), widePadding: new Thickness(14, 11), narrowPadding: new Thickness(6, 8));
            // Нижний отступ обязателен: без него последний элемент не попадает
            // в прокручиваемую высоту целиком и снизу остаётся видна только рамка.
            exitBtn.Margin = new Thickness(0, UiMetrics.ActionGridGap, 0, UiMetrics.ActionGridGap);
            panel.Children.Add(exitBtn);

            return panel;
        }

        /// <summary>
        /// Двухколоночная сетка компактных кнопок действий правой панели.
        /// Равномерно заполняет ширину и заметно экономит вертикальное место
        /// по сравнению со стеком полноширинных secondary-кнопок.
        /// </summary>
        private static Control BuildActionList(params Control[] buttons)
        {
            // Кнопки идут в один столбец, как в версии для Windows. Двухколоночная
            // раскладка экономила высоту, но подписи в неё не помещались ни при
            // какой ширине панели: при её пределе 340 на ячейку остаётся около 160
            // пикселей, а «Ярлык на рабочем столе» требует почти 190, и подпись
            // обрывалась на середине слова.
            var stack = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Spacing = UiMetrics.ActionGridGap,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            foreach (var btn in buttons)
                stack.Children.Add(btn);

            return stack;
        }

        /// <summary>
        /// Компактная кнопка действия правой панели: иконка + текст, низкая высота,
        /// растягивается на всю ширину панели.
        /// </summary>
        /// <param name="colorHex">
        /// Явный цвет значка и подписи. У автора так покрашен «Выход»: #DC2626
        /// одинаково в обеих темах.
        /// </param>
        private static Control CompactActionButton(string iconKey, string text, string commandPath, string tooltip,
            string? colorHex = null, string? trailingIconKey = null, bool colorTextToo = true,
            double? iconSize = null, Thickness? widePadding = null, Thickness? narrowPadding = null)
        {
            var btn = new PanelButton(
                "SecondaryButtonBackgroundBrush",
                "SecondaryButtonHoverBrush",
                "SecondaryButtonPressedBrush",
                "BorderColorBrush",
                new CornerRadius(UiMetrics.RadiusMd))
            {
                Content = CompactIconAndText(iconKey, text, "ButtonTextBrush", colorHex: colorHex, trailingIconKey: trailingIconKey, colorTextToo: colorTextToo, iconSize: iconSize),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                MinHeight = UiMetrics.ActionButtonMinHeight,
                Padding = new Thickness(UiMetrics.ActionButtonPadH, UiMetrics.ActionButtonPadV),
                Margin = new Thickness(0)
            };
            BindActionPadding(btn, widePadding, narrowPadding);
            ToolTip.SetTip(btn, tooltip);
            btn.Bind(Button.CommandProperty, new Binding(commandPath));
            return btn;
        }

        /// <summary>
        /// Отступ кнопки действия меняется вместе с шириной правой панели, как
        /// в разметке: у перехода по ссылке и выхода 14 на 11 в полной панели
        /// и 6 на 8 в узкой (MainWindow.xaml:2205 и 2264).
        /// </summary>
        private static void BindActionPadding(Control button, Thickness? wide, Thickness? narrow)
        {
            if (wide is not { } w || narrow is not { } n)
                return;
            button.Bind(TemplatedControl.PaddingProperty, new Binding("ShowRightPanelDetails")
            {
                Converter = new Avalonia.Data.Converters.FuncValueConverter<bool, Thickness>(v => v ? w : n)
            });
        }

        /// <summary>Компактное содержимое кнопки: иконка + подпись меньшего размера.</summary>
        /// <summary>
        /// Вариант кнопки действия с подписью из привязки: нужен там, где текст
        /// меняется по состоянию, как короткая подпись открытия по ссылке.
        /// </summary>
        private static Control CompactActionButtonBound(string iconKey, string textPath, string commandPath, string tooltip,
            string? colorHex = null, string? trailingIconKey = null, bool colorTextToo = true,
            double? iconSize = null, Thickness? widePadding = null, Thickness? narrowPadding = null)
        {
            var btn = new PanelButton(
                "SecondaryButtonBackgroundBrush",
                "SecondaryButtonHoverBrush",
                "SecondaryButtonPressedBrush",
                "BorderColorBrush",
                new CornerRadius(UiMetrics.RadiusMd))
            {
                Content = CompactIconAndText(iconKey, "", "ButtonTextBrush", textPath, colorHex, trailingIconKey, colorTextToo, iconSize),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                MinHeight = UiMetrics.ActionButtonMinHeight,
                Padding = new Thickness(UiMetrics.ActionButtonPadH, UiMetrics.ActionButtonPadV),
                Margin = new Thickness(0)
            };
            BindActionPadding(btn, widePadding, narrowPadding);
            ToolTip.SetTip(btn, tooltip);
            btn.Bind(Button.CommandProperty, new Binding(commandPath));
            return btn;
        }

        private static Control CompactIconAndText(string iconKey, string text, string brushKey, string? textPath = null,
            string? colorHex = null, string? trailingIconKey = null, bool colorTextToo = true, double? iconSize = null)
        {
            // Сеткой, а не горизонтальной панелью: панель меряет подпись
            // бесконечной шириной, поэтому обрезка многоточием не срабатывает
            // и длинный текст вылезает за кнопку вместо того, чтобы сократиться.
            var sp = new Grid
            {
                ColumnSpacing = 6,
                VerticalAlignment = VerticalAlignment.Center,
                // Растягиваем на ширину кнопки, иначе хвостовой значок липнет
                // к подписи, а у автора он прижат к правому краю.
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            sp.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            sp.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (trailingIconKey is not null)
                sp.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (colorHex is null)
            {
                sp.Children.Add(IconHelper.MakeIcon(iconKey, iconSize ?? UiMetrics.ActionIconSize, brushKey));
            }
            else
            {
                sp.Children.Add(IconHelper.MakeIcon(iconKey, iconSize ?? UiMetrics.ActionIconSize,
                    new SolidColorBrush(Color.Parse(colorHex))));
            }
            var tb = new TextBlock
            {
                Text = text,
                FontSize = UiMetrics.ActionFontSize,
                FontWeight = FontWeight.Medium,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            // У автора цвет значка и подписи совпадает не всегда: у выхода
            // красное и то и другое, у перехода по ссылке значок голубой,
            // а подпись обычная.
            if (colorHex is null || !colorTextToo)
                ThemeBrushes.Bind(tb, TextBlock.ForegroundProperty, brushKey);
            else
                tb.Foreground = new SolidColorBrush(Color.Parse(colorHex));
            if (textPath is not null)
                tb.Bind(TextBlock.TextProperty, new Binding(textPath));
            Grid.SetColumn(tb, 1);
            sp.Children.Add(tb);
            if (trailingIconKey is not null)
            {
                // Хвостовой значок справа, как в разметке: стрелка у перехода
                // по ссылке и крестик у выхода.
                // Цвет и прозрачность как в разметке, и прячется вместе
                // с подробностями правой панели, а не висит всегда.
                var trailingHost = IconHelper.MakeIcon(trailingIconKey, 12, out var trailing);
                trailingHost.Opacity = 0.75;
                if (colorTextToo && colorHex is not null)
                    trailing.Fill = new SolidColorBrush(Color.Parse(colorHex));
                else
                    ThemeBrushes.Bind(trailing, Avalonia.Controls.Shapes.Path.FillProperty, "ButtonTextBrush");
                trailingHost.Bind(Control.IsVisibleProperty, new Binding("ShowRightPanelDetails"));
                Grid.SetColumn(trailingHost, 2);
                sp.Children.Add(trailingHost);
            }
            return sp;
        }

        /// <summary>
        /// Блок «Текущая сессия»: режим клиента и разрядность только для
        /// следующего запуска, сохранённые настройки базы он не меняет.
        /// Видимостью управляет настройка, как и в WPF-версии.
        /// </summary>
        private Control BuildSessionCard()
        {
            var hint = new TextBlock
            {
                Text = LocalizationManager.T("Main.SessionOnceHint"),
                FontSize = UiMetrics.ScaledFont(11),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            };
            ThemeBrushes.Bind(hint, TextBlock.ForegroundProperty, "TextSecondaryBrush");
            ToolTip.SetTip(hint, LocalizationManager.T("Main.CurrentSessionHelp"));
            // Подсказка и подписи групп скрыты в узкой панели, как в разметке
            // (MainWindow.xaml:2137 и 2170): без этого она выходила заметно выше.
            hint.Bind(Control.IsVisibleProperty, new Binding("ShowRightPanelDetails"));

            // Переключатели идут вплотную, как у автора: карточка добавляет свой
            // интервал между каждым дочерним элементом, и от этого строки
            // расходились. Внутри контейнера интервала нет, работают только
            // собственные отступы переключателей.
            var options = new StackPanel { Spacing = 0 };
            // Кружки мельче штатных Fluent, как у автора. Части шаблона
            // адресуются по именам: общий OfType<Ellipse> сделал бы одинаковыми
            // все три окружности и раздул бы внутреннюю точку.
            foreach (var (part, size) in new[] { ("OuterEllipse", 14d), ("CheckOuterEllipse", 14d), ("CheckGlyph", 6d) })
            {
                options.Styles.Add(new Style(x => x.OfType<RadioButton>().Class("compactRadio")
                    .Template().Name(part))
                {
                    Setters =
                    {
                        new Setter(Layoutable.WidthProperty, size),
                        new Setter(Layoutable.HeightProperty, size)
                    }
                });
            }
            // Внутри шаблона Fluent обойма кружков задана фиксированной высотой,
            // и одного MinHeight на самой кнопке мало: строка оставалась 32
            // пикселя против плотных строк версии для Windows.
            options.Styles.Add(new Style(x => x.OfType<RadioButton>().Class("compactRadio")
                .Template().OfType<Grid>())
            {
                Setters = { new Setter(Layoutable.HeightProperty, UiMetrics.Scaled(22)) }
            });

            void AddOption(params Control[] items)
            {
                foreach (var item in items)
                    options.Children.Add(item);
            }

            var clientOptions = new StackPanel
            {
                Margin = new Thickness(0, 0, 0, 8)
            };
            clientOptions.Children.Add(
                SessionOption(LocalizationManager.T("Main.SessionClientAuto"), "SessionClient", "IsSessionClientAuto"));
            clientOptions.Children.Add(
                SessionOption(LocalizationManager.T("Main.SessionClientOrdinary"), "SessionClient", "IsSessionClientOrdinary"));
            clientOptions.Children.Add(
                SessionOption(LocalizationManager.T("Main.SessionClientThickManaged"), "SessionClient", "IsSessionClientThick",
                    LocalizationManager.T("Main.SessionThickManagedTooltip")));
            clientOptions.Children.Add(
                SessionOption(LocalizationManager.T("Main.SessionClientThin"), "SessionClient", "IsSessionClientThin"));

            var archOptions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    SessionOption(LocalizationManager.T("Main.SessionClientAuto"), "SessionArch", "IsSessionArchAuto",
                        margin: new Thickness(0, 2, 10, 2)),
                    SessionOption("32", "SessionArch", "IsSessionArch32",
                        margin: new Thickness(0, 2, 10, 2)),
                    SessionOption("64", "SessionArch", "IsSessionArch64")
                }
            };

            AddOption(
                SessionGroupLabel(LocalizationManager.T("Main.ClientMode")),
                clientOptions,
                SessionGroupLabel(LocalizationManager.T("Main.Bitness")),
                archOptions);

            var card = SectionCard(LocalizationManager.T("Main.CurrentSession"), "Main.CurrentSessionHelp",
                hint,
                options);

            card.Bind(Control.IsVisibleProperty, new Binding("ShowSessionLaunchPanel"));
            return card;
        }

        /// <summary>Подпись группы переключателей в блоке текущей сессии.</summary>
        private Control SessionGroupLabel(string text)
        {
            var block = new TextBlock
            {
                Text = text,
                FontSize = UiMetrics.ScaledFont(11),
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, 0, 0, 4)
            };
            ThemeBrushes.Bind(block, TextBlock.ForegroundProperty, "TextSecondaryBrush");
            block.Bind(Control.IsVisibleProperty, new Binding("ShowRightPanelDetails"));
            return block;
        }

        /// <summary>Переключатель в блоке текущей сессии: одна из взаимоисключающих опций.</summary>
        private static Control SessionOption(string text, string group, string propertyPath, string? tooltip = null,
            Thickness? margin = null)
        {
            var option = new RadioButton
            {
                Content = text,
                GroupName = group,
                FontSize = UiMetrics.ScaledFont(12),
                // Штатная строка Fluent высотой 32 растягивала список вдвое
                // против версии для Windows: там строки идут вплотную.
                MinHeight = UiMetrics.Scaled(22),
                Padding = new Thickness(6, 0, 0, 0),
                Margin = margin ?? new Thickness(0, 2)
            };
            // Класс нужен не для оформления, а чтобы поднять приоритет сеттеров:
            // Fluent задаёт размеры частей шаблона приоритетом Template, который
            // старше безусловного стиля. Условный селектор становится
            // StyleTrigger и Template перебивает.
            option.Classes.Add("compactRadio");
            option.Bind(RadioButton.IsCheckedProperty, new Binding(propertyPath) { Mode = BindingMode.TwoWay });
            if (tooltip is not null)
                ToolTip.SetTip(option, tooltip);
            return option;
        }

        /// <summary>Ключ значка в геометрию из Icons.axaml для привязок заголовка.</summary>
        private static readonly Avalonia.Data.Converters.FuncValueConverter<string?, Geometry?> IconKeyConverter =
            new(key => string.IsNullOrEmpty(key) ? null : IconHelper.Geometry(key));

        /// <summary>
        /// Подпись секции правой панели: у автора она стоит снаружи рамки,
        /// малыми капителями и вторичным цветом, без значка.
        /// </summary>
        /// <summary>Рамка бейджа правой панели с явным фоном, как в разметке WPF.</summary>
        private static Border Badge(string backHex) =>
            new()
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 3),
                Margin = new Thickness(0, 0, 6, 4),
                Background = new SolidColorBrush(Color.Parse(backHex))
            };

        /// <summary>Содержимое бейджа: значок и подпись из привязки.</summary>
        private static Control BadgeContent(string iconKey, string iconHex, string? textHex = null,
            IBinding? textBinding = null, string? themeBrushKey = null)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            row.Children.Add(IconHelper.MakeIcon(iconKey, 12, new SolidColorBrush(Color.Parse(iconHex))));
            var text = new TextBlock
            {
                FontSize = 11,
                // У автора подпись бейджа закрепления обычного начертания.
                FontWeight = themeBrushKey is null ? FontWeight.Normal : FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            if (themeBrushKey is not null)
                ThemeBrushes.Bind(text, TextBlock.ForegroundProperty, themeBrushKey);
            else
                text.Foreground = new SolidColorBrush(Color.Parse(textHex ?? "#000000"));
            if (textBinding is not null)
                text.Bind(TextBlock.TextProperty, textBinding);
            row.Children.Add(text);
            return row;
        }

        private static Control SectionLabel(string text, bool smallCaps = true)
        {
            // У автора подпись набрана малыми капителями (Typography.Capitals).
            // В Avalonia 11.3 такого свойства у TextBlock нет, поэтому приближаем:
            // прописные буквы кеглем помельче дают тот же рисунок строки.
            var block = new TextBlock
            {
                Text = smallCaps ? text.ToUpperInvariant() : text,
                FontSize = UiMetrics.ScaledFont(smallCaps ? 10.5 : 12),
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, UiMetrics.ActionGridGap, 0, smallCaps ? 8 : 4)
            };
            ThemeBrushes.Bind(block, TextBlock.ForegroundProperty, "TextSecondaryColorBrush");
            return block;
        }

        /// <summary>Рамка секции без собственного заголовка: подпись живёт снаружи.</summary>
        private static Control PlainCard(params Control[] children)
        {
            // Числа из разметки (MainWindow.xaml:1756): скругление 12,
            // отступ 12 на 10, нижнее поле 14.
            var card = new Border
            {
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 10),
                Margin = new Thickness(0, 0, 0, 14),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            // У автора панель светлее карточки, а не наоборот: панель
            // CardBackgroundBrush, карточка ContentBackgroundBrush.
            ThemeBrushes.Bind(card, Border.BackgroundProperty, "ContentBackgroundColorBrush");
            ThemeBrushes.Bind(card, Border.BorderBrushProperty, "BorderColorBrush");
            UiMetrics.AddBrushTransition(card);
            var content = new StackPanel { Spacing = UiMetrics.Gap };
            foreach (var child in children)
                content.Children.Add(child);
            card.Child = content;
            return card;
        }

        /// <summary>
        /// Карточка-секция с заголовком и значком внутри рамки. Осталась только
        /// у карточки текущей сессии: у остальных секций подпись вынесена наружу.
        /// </summary>
        private Control SectionCard(string title, string helpKey, params Control[] children)
        {
            // Числа из разметки (MainWindow.xaml:2101-2113): скругление 8, фон
            // ItemHover, отступ 10 на 8 и 6 на 6 в узкой панели, поле 8,0,8,10.
            var card = new Border
            {
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 8),
                Margin = new Thickness(8, 0, 8, 10),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            _sessionCard = card;
            ThemeBrushes.Bind(card, Border.BackgroundProperty, "ItemHoverBrush");
            ThemeBrushes.Bind(card, Border.BorderBrushProperty, "BorderColorBrush");
            UiMetrics.AddBrushTransition(card);

            var content = new StackPanel { Spacing = 0 };

            var header = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6
            };
            // Значка у заголовка в разметке нет, зато есть кнопка справки
            // рядом с подписью (MainWindow.xaml:2118-2136). Подпись основным
            // цветом, а не вторичным.
            var titleBlock = new TextBlock
            {
                Text = title,
                FontSize = UiMetrics.ScaledFont(12),
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, 0, 0, 6),
                VerticalAlignment = VerticalAlignment.Center
            };
            _sessionTitleBlock = titleBlock;
            ThemeBrushes.Bind(titleBlock, TextBlock.ForegroundProperty, "TextPrimaryColorBrush");
            header.Children.Add(titleBlock);
            if (!string.IsNullOrEmpty(helpKey))
                header.Children.Add(new Controls.HelpLink
                {
                    Margin = new Thickness(6, 0, 0, 4),
                    HelpText = LocalizationManager.T(helpKey)
                });
            content.Children.Add(header);

            foreach (var child in children)
                content.Children.Add(child);

            card.Child = content;
            return card;
        }

        /// <summary>Крупная primary-кнопка на акцентном фоне с контрастным текстом/иконкой.</summary>
        private static Control PrimaryActionButton(string iconKey, string text, string commandPath, string tooltip)
        {
            var btn = new PanelButton("AccentBrush", "AccentHoverBrush", "AccentPressedBrush", "AccentBrush")
            {
                Content = ThemedIconAndText(iconKey, text, "TextOnAccentBrush", UiMetrics.ScaledFont(18), centered: true),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 0, 0, UiMetrics.SectionMarginBottom),
                Padding = new Thickness(UiMetrics.ButtonPadH, UiMetrics.ButtonPadV)
            };
            // В Avalonia подсказка это присоединённое свойство, а не свойство контрола.
            ToolTip.SetTip(btn, tooltip);
            btn.Bind(Button.CommandProperty, new Binding(commandPath));
            return btn;
        }

        /// <summary>Secondary-кнопка с приглушённым фоном и hover/pressed из ресурсов темы.</summary>
        private static Control SecondaryActionButton(string iconKey, string text, string commandPath, string tooltip)
        {
            var btn = new PanelButton(
                "SecondaryButtonBackgroundBrush",
                "SecondaryButtonHoverBrush",
                "SecondaryButtonPressedBrush",
                "BorderColorBrush",
                new CornerRadius(UiMetrics.RadiusMd))
            {
                Content = ThemedIconAndText(iconKey, text, "ButtonTextBrush", UiMetrics.ActionIconSize, centered: false),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                MinHeight = UiMetrics.ActionButtonMinHeight,
                Padding = new Thickness(UiMetrics.ActionButtonPadH, UiMetrics.ActionButtonPadV),
                Margin = new Thickness(0, 0, 0, UiMetrics.ActionGridGap / 2)
            };
            ToolTip.SetTip(btn, tooltip);
            btn.Bind(Button.CommandProperty, new Binding(commandPath));
            return btn;
        }

        /// <summary>
        /// Кнопка запуска со стрелкой и меню дополнительных вариантов, как
        /// в WPF-версии. Пункт «от имени администратора» не переносится:
        /// на Linux нет повышения прав через оболочку, параметр runAsAdmin
        /// в лаунчере не используется, а запуск клиента 1С от root оставил бы
        /// в домашнем каталоге пользователя файлы, которые ему не принадлежат.
        /// </summary>
        private static Control BuildLaunchSplitButton(
            string iconKey,
            string text,
            string commandPath,
            string tooltip,
            bool primary,
            IReadOnlyList<(string Header, string Command, string? IconKey, string? IconColor)> menuItems)
        {
            var radius = UiMetrics.RadiusLg;
            var mainCorner = new CornerRadius(radius, 0, 0, radius);

            // Вторичная кнопка запуска у автора прозрачная с рамкой, а не залитая:
            // залит только первичный запуск, а кремовым остаётся штатный стартер.
            var main = primary
                ? new PanelButton("AccentBrush", "AccentHoverBrush", "AccentPressedBrush", "AccentBrush", mainCorner)
                : new PanelButton("", "ItemHoverBrush",
                    "SecondaryButtonPressedBrush", "BorderColorBrush", mainCorner);

            // У вторичной кнопки подпись и значок берут основной цвет текста,
            // как в разметке: ButtonTextBrush чёрный в обеих темах и на прозрачной
            // кнопке тёмной темы не читается.
            var contentBrush = primary ? "TextOnAccentBrush" : "TextPrimaryColorBrush";
            // Числа из разметки (MainWindow.xaml:1904): содержимое прижато влево,
            // отступ 8,8,4,8, минимальная высота 34, значок 16, подпись 12.
            main.Content = ThemedIconAndText(iconKey, text, contentBrush, UiMetrics.Scaled(16), centered: false);
            main.HorizontalContentAlignment = HorizontalAlignment.Left;
            main.HorizontalAlignment = HorizontalAlignment.Stretch;
            main.MinHeight = UiMetrics.Scaled(34);
            main.Padding = new Thickness(UiMetrics.Scaled(8), UiMetrics.Scaled(8), UiMetrics.Scaled(4), UiMetrics.Scaled(8));
            main.Margin = new Thickness(0);
            ToolTip.SetTip(main, tooltip);
            main.Bind(Button.CommandProperty, new Binding(commandPath));

            var menu = new ContextMenu().Styled(Themes.ControlThemes.ModernContextMenu);
            foreach (var (header, command, itemIcon, itemColor) in menuItems)
            {
                if (header.Length == 0)
                {
                    menu.Items.Add(new Separator());
                    continue;
                }

                // Значки пунктов заданы в разметке явным цветом
                // (MainWindow.xaml:1954 и 1962): настройка параметров голубая,
                // запуск с авторизацией фиолетовый.
                var item = new MenuItem { Header = header };
                item.Styled(Themes.ControlThemes.ModernMenuItem);
                if (itemIcon is not null)
                    item.Icon = IconHelper.MakeIcon(itemIcon, 18,
                        itemColor is not null ? new SolidColorBrush(Color.Parse(itemColor)) : Brushes.Gray);
                item.Bind(MenuItem.CommandProperty, new Binding(command));
                menu.Items.Add(item);
            }

            var arrowCorner = new CornerRadius(0, radius, radius, 0);
            var arrow = primary
                ? new PanelButton("AccentBrush", "AccentHoverBrush", "AccentPressedBrush", "AccentBrush", arrowCorner)
                // Стрелка вторичной кнопки прозрачная, как и её основная часть.
                : new PanelButton("", "ItemHoverBrush",
                    "SecondaryButtonPressedBrush", "BorderColorBrush", arrowCorner);
            arrow.Width = UiMetrics.Scaled(28);
            arrow.MinHeight = main.MinHeight;
            arrow.Padding = new Thickness(0);
            arrow.Margin = new Thickness(0);
            // Стрелка в разметке это контур ChevronDown 16, а не текстовый знак.
            arrow.Content = IconHelper.MakeIcon("IconChevronDown", UiMetrics.Scaled(16), contentBrush);
            ToolTip.SetTip(arrow, LocalizationManager.T("Main.MoreLaunchOptions"));
            arrow.ContextMenu = menu;
            arrow.Click += (_, _) => menu.Open(arrow);

            // Между частями в разметке стоит линия шириной 1 с полем 0,8:
            // у первичной кнопки полупрозрачная белая, у вторичной цвет рамки.
            var divider = new Border { Width = 1, Margin = new Thickness(0, 8) };
            if (primary)
                divider.Background = new SolidColorBrush(Color.Parse("#55FFFFFF"));
            else
                ThemeBrushes.Bind(divider, Border.BackgroundProperty, "BorderColorBrush");

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            Grid.SetColumn(main, 0);
            Grid.SetColumn(divider, 1);
            Grid.SetColumn(arrow, 2);
            grid.Children.Add(main);
            grid.Children.Add(divider);
            grid.Children.Add(arrow);
            return grid;
        }

        /// <summary>Содержимое кнопки: иконка + подпись, окрашенные кистью ресурса темы.</summary>
        private static Control ThemedIconAndText(string iconKey, string text, string brushKey, double iconSize, bool centered,
            double? fontSize = null)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            if (centered)
                sp.HorizontalAlignment = HorizontalAlignment.Center;
            sp.Children.Add(IconHelper.MakeIcon(iconKey, iconSize, brushKey));
            var tb = new TextBlock
            {
                Text = text,
                FontSize = fontSize ?? UiMetrics.ActionFontSize + (centered ? 0.5 : 0),
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            ThemeBrushes.Bind(tb, TextBlock.ForegroundProperty, brushKey);
            sp.Children.Add(tb);
            return sp;
        }

        private Control DetailRow(string label, Binding binding)
        {
            // Отступ между строками меньше восьми из разметки: у системного шрифта
            // строка выше, чем у Segoe UI, и при восьми карточка выходила заметно
            // разреженнее версии для Windows при том же кегле.
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Высота строки задана явно: без неё шаг строк определяют метрики
            // системного шрифта, и карточка выходила заметно разреженнее, чем
            // в версии для Windows при том же кегле и тех же отступах.
            var labelBlock = new TextBlock
            {
                Text = label,
                FontSize = UiMetrics.ScaledFont(12),
                LineHeight = UiMetrics.ScaledFont(16)
            };
            ThemeBrushes.Bind(labelBlock, TextBlock.ForegroundProperty, "TextSecondaryColorBrush");
            grid.Children.Add(labelBlock);
            Grid.SetColumn(labelBlock, 0);

            // Значение полужирное, как в разметке WPF: подпись вторичная, значение основное.
            var valueBlock = new TextBlock
            {
                FontSize = UiMetrics.ScaledFont(12),
                LineHeight = UiMetrics.ScaledFont(16),
                FontWeight = FontWeight.SemiBold,
                TextWrapping = TextWrapping.Wrap
            };
            valueBlock.Bind(TextBlock.TextProperty, binding);
            grid.Children.Add(valueBlock);
            Grid.SetColumn(valueBlock, 1);
            return grid;
        }

        /// <summary>
        /// Кнопка-панель со скруглением и состояниями «обычное / hover / pressed»,
        /// кисти которых берутся из ресурсов темы (перекрашиваются при смене схемы).
        /// Используется для primary- и secondary-кнопок правой панели.
        /// </summary>
        private sealed class PanelButton : Button
        {
            private IBrush _baseBg = Brushes.Transparent;
            private IBrush _hoverBg = Brushes.Transparent;
            private IBrush _pressedBg = Brushes.Transparent;
            private IBrush _border = Brushes.Transparent;
            private IBrush _accent = Brushes.Transparent;
            private CornerRadius _radius;
            private bool _hovered;
            private bool _pressed;
            private bool _focused;

            public PanelButton(string baseBgKey, string hoverBgKey, string pressedBgKey, string borderKey, CornerRadius? cornerRadius = null)
            {
                _radius = cornerRadius ?? new CornerRadius(UiMetrics.RadiusLg);
                HorizontalContentAlignment = HorizontalAlignment.Center;
                VerticalContentAlignment = VerticalAlignment.Center;
                Padding = new Thickness(UiMetrics.ButtonPadH, UiMetrics.ButtonPadV);
                BorderThickness = new Thickness(1);
                Cursor = new Cursor(StandardCursorType.Hand);

                // Кастомный шаблон: скруглённый Border + ContentPresenter (без Fluent-хрома).
                Theme = new ControlTheme(typeof(Button))
                {
                    Setters =
                    {
                        new Setter(TemplatedControl.TemplateProperty, new FuncControlTemplate<PanelButton>((_, _) =>
                    {
                        var border = new Border { CornerRadius = _radius, BorderThickness = new Thickness(1) };
                        border[!Border.BackgroundProperty] = new TemplateBinding(TemplatedControl.BackgroundProperty);
                        border[!Border.BorderBrushProperty] = new TemplateBinding(TemplatedControl.BorderBrushProperty);
                        border[!Border.BorderThicknessProperty] = new TemplateBinding(TemplatedControl.BorderThicknessProperty);
                        border[!Border.PaddingProperty] = new TemplateBinding(TemplatedControl.PaddingProperty);
                        UiMetrics.AddBrushTransition(border);

                        var presenter = new ContentPresenter();
                        presenter[!ContentPresenter.ContentProperty] = new TemplateBinding(ContentControl.ContentProperty);
                        presenter[!ContentPresenter.HorizontalContentAlignmentProperty] = new TemplateBinding(ContentControl.HorizontalContentAlignmentProperty);
                        presenter[!ContentPresenter.VerticalContentAlignmentProperty] = new TemplateBinding(ContentControl.VerticalContentAlignmentProperty);
                        border.Child = presenter;
                        return border;
                    }))
                    }
                };

                Subscribe(baseBgKey, v => _baseBg = v);
                Subscribe(hoverBgKey, v => _hoverBg = v);
                Subscribe(pressedBgKey, v => _pressedBg = v);
                Subscribe(borderKey, v => _border = v);
                Subscribe("AccentBrush", v => _accent = v);

                PointerEntered += (_, _) => { _hovered = true; ApplyState(); };
                PointerExited += (_, _) => { _hovered = false; _pressed = false; ApplyState(); };
                PointerPressed += (_, _) => { _pressed = true; ApplyState(); };
                PointerReleased += (_, _) => { _pressed = false; ApplyState(); };
                PointerCaptureLost += (_, _) => { _pressed = false; ApplyState(); };

                this.GetObservable(IsEnabledProperty).Subscribe(new BoolObserver(_ => ApplyState()));
                this.GetObservable(IsKeyboardFocusWithinProperty).Subscribe(new BoolObserver(v => { _focused = v; ApplyState(); }));
                ApplyState();
            }

            private void Subscribe(string key, Action<IBrush> setter)
            {
                // Пустой ключ означает «прозрачно»: у автора значковые кнопки
                // верхней панели без подложки и без рамки, подсветка только
                // при наведении.
                if (string.IsNullOrEmpty(key))
                    return;
                // Подписка снимается вместе с уходом кнопки из дерева: список
                // _subs не освобождался нигде, и каждая пересборка правой панели
                // оставляла кнопку и всё её дерево укоренёнными.
                ThemeBrushes.Observe(this, key, brush => { setter(brush); ApplyState(); });
            }

            /// <summary>Применяет состояние к фону/границе/прозрачности кнопки.</summary>
            private void ApplyState()
            {
                if (!IsEnabled)
                {
                    Opacity = 0.4;
                    Background = _baseBg;
                    BorderBrush = _border;
                    BorderThickness = new Thickness(1);
                    return;
                }

                Opacity = 1.0;
                Background = _pressed ? _pressedBg : (_hovered ? _hoverBg : _baseBg);
                if (_focused)
                {
                    // Видимый focus-ринг акцентным цветом темы для клавиатурной навигации.
                    BorderBrush = _accent;
                    BorderThickness = new Thickness(2);
                }
                else
                {
                    BorderBrush = _border;
                    BorderThickness = new Thickness(1);
                }
            }


        }

        /// <summary>
        /// Простой наблюдатель ресурса-кисти темы: передаёт текущее значение в setter и
        /// при изменении (в т.ч. при смене схемы) вызывает onChanged.
        /// </summary>
        private sealed class BrushObserver : IObserver<object?>
        {
            private readonly Action<IBrush> _setter;
            private readonly Action _onChanged;

            public BrushObserver(Action<IBrush> setter, Action onChanged)
            {
                _setter = setter;
                _onChanged = onChanged;
            }

            public void OnCompleted() { }
            public void OnError(Exception error) { }
            public void OnNext(object? value)
            {
                if (value is IBrush brush)
                    _setter(brush);
                _onChanged();
            }
        }

        /// <summary>Простой наблюдатель bool (для IsEnabled / клавиатурного фокуса).</summary>
        private sealed class BoolObserver : IObserver<bool>
        {
            private readonly Action<bool> _onNext;
            public BoolObserver(Action<bool> onNext) => _onNext = onNext;
            public void OnCompleted() { }
            public void OnError(Exception error) { }
            public void OnNext(bool value) => _onNext(value);
        }

        /// <summary>Простой наблюдатель WindowState (для значка разворота кнопки окна).</summary>
        private sealed class WindowStateObserver : IObserver<WindowState>
        {
            private readonly Action _onNext;
            public WindowStateObserver(Action onNext) => _onNext = onNext;
            public void OnCompleted() { }
            public void OnError(Exception error) { }
            public void OnNext(WindowState value) => _onNext();
        }

        /// <summary>
        /// Сегментная кнопка переключателя (для сегментированного контроля): у выбранного
        /// сегмента акцентная заливка, а иконка/текст — цветом «на акценте»; у невыбранных —
        /// прозрачный фон с приглушённым текстом и hover/pressed-состояниями. Все кисти
        /// берутся из ресурсов темы (перекрашиваются при смене схемы). Если lockOn == true,
        /// активный сегмент нельзя «снять» кликом (поведение как у RadioButton).
        /// </summary>
        private sealed class SegmentButton : ToggleButton
        {
            private readonly string _iconKey;
            private readonly string _text;
            private readonly double _iconSize;
            private readonly double _textSize;
            private readonly bool _lockOn;

            private IBrush _hoverBg = Brushes.Transparent;
            private IBrush _pressedBg = Brushes.Transparent;
            private IBrush _accent = Brushes.Transparent;
            private IBrush _accentHover = Brushes.Transparent;
            private IBrush _accentPressed = Brushes.Transparent;

            private bool _hovered;
            private bool _pressed;
            private bool _focused;

            private Thickness _borderThickness = new(2);
            private IBrush? _restingBorder;
            private IBrush? _hoverBorder;
            private IBrush? _restingBg;

            /// <summary>Постоянная рамка в покое: нужна тегам панели фильтра.</summary>
            public void ShowRestingBorder(string brushKey)
                => ThemeBrushes.Observe(this, brushKey, brush => { _restingBorder = brush; ApplyState(); });

            /// <summary>Рамка при наведении: у чипов фильтра меняется только она.</summary>
            public void ShowHoverBorder(string brushKey)
                => ThemeBrushes.Observe(this, brushKey, brush => { _hoverBorder = brush; ApplyState(); });

            /// <summary>Толщина рамки: у чипов фильтра единица, как в разметке.</summary>
            public void SetBorderThickness(double thickness)
            {
                _borderThickness = new Thickness(thickness);
                ApplyState();
            }

            /// <summary>Заливка в покое: у чипов фильтра это фон карточки, а не пустота.</summary>
            public void ShowRestingBackground(string brushKey)
                => ThemeBrushes.Observe(this, brushKey, brush => { _restingBg = brush; ApplyState(); });

            public SegmentButton(string iconKey, string text, string hoverBgKey, string pressedBgKey, bool lockOn = true,
                double iconSize = 15, double cornerRadius = -1, double fontSize = 13)
            {
                _iconKey = iconKey;
                _text = text;
                _iconSize = iconSize;
                _textSize = fontSize;
                _lockOn = lockOn;
                var corner = cornerRadius >= 0 ? cornerRadius : UiMetrics.RadiusSm;

                HorizontalContentAlignment = HorizontalAlignment.Center;
                VerticalContentAlignment = VerticalAlignment.Center;
                Cursor = new Cursor(StandardCursorType.Hand);
                MinHeight = 30;
                Padding = new Thickness(12, 5);
                BorderThickness = new Thickness(2);
                BorderBrush = Brushes.Transparent;

                // Кастомный шаблон: скруглённый Border + ContentPresenter (без Fluent-хрома).
                Theme = new ControlTheme(typeof(ToggleButton))
                {
                    Setters =
                    {
                        new Setter(TemplatedControl.TemplateProperty, new FuncControlTemplate<SegmentButton>((_, _) =>
                    {
                        // Толщину локально не задаём: в Avalonia локальное значение
                        // старше значения шаблона, и TemplateBinding ниже не сработал бы.
                        // Рамка приходила кистью, но рисовать её было нечем.
                        var border = new Border { CornerRadius = new CornerRadius(corner) };
                        border[!Border.BackgroundProperty] = new TemplateBinding(TemplatedControl.BackgroundProperty);
                        border[!Border.BorderBrushProperty] = new TemplateBinding(TemplatedControl.BorderBrushProperty);
                        border[!Border.BorderThicknessProperty] = new TemplateBinding(TemplatedControl.BorderThicknessProperty);
                        // Без этого фон измеряется ровно по содержимому и обрезает текст.
                        border[!Border.PaddingProperty] = new TemplateBinding(TemplatedControl.PaddingProperty);
                        UiMetrics.AddBrushTransition(border);
                        var presenter = new ContentPresenter();
                        presenter[!ContentPresenter.ContentProperty] = new TemplateBinding(ContentControl.ContentProperty);
                        presenter[!ContentPresenter.HorizontalContentAlignmentProperty] = new TemplateBinding(ContentControl.HorizontalContentAlignmentProperty);
                        presenter[!ContentPresenter.VerticalContentAlignmentProperty] = new TemplateBinding(ContentControl.VerticalContentAlignmentProperty);
                        border.Child = presenter;
                        return border;
                    }))
                    }
                };

                Subscribe(hoverBgKey, v => _hoverBg = v);
                Subscribe(pressedBgKey, v => _pressedBg = v);
                Subscribe("AccentBrush", v => _accent = v);
                Subscribe("AccentHoverBrush", v => _accentHover = v);
                Subscribe("AccentPressedBrush", v => _accentPressed = v);

                PointerEntered += (_, _) => { _hovered = true; ApplyState(); };
                PointerExited += (_, _) => { _hovered = false; _pressed = false; ApplyState(); };
                PointerPressed += (_, _) => { _pressed = true; ApplyState(); };
                PointerReleased += (_, _) => { _pressed = false; ApplyState(); };
                PointerCaptureLost += (_, _) => { _pressed = false; ApplyState(); };

                this.GetObservable(IsCheckedProperty, v => v == true).Subscribe(new BoolObserver(_ => { UpdateContent(); ApplyState(); }));
                this.GetObservable(IsEnabledProperty).Subscribe(new BoolObserver(_ => ApplyState()));
                this.GetObservable(IsKeyboardFocusWithinProperty).Subscribe(new BoolObserver(v => { _focused = v; ApplyState(); }));

                UpdateContent();
                ApplyState();
            }

            /// <summary>Не даём снимать уже активный сегмент (как RadioButton), когда это требуется.</summary>
            protected override void Toggle()
            {
                if (_lockOn && IsChecked == true)
                    return;
                base.Toggle();
            }

            private void Subscribe(string key, Action<IBrush> setter)
                // Подписка снимается вместе с уходом кнопки из дерева: список
                // освобождался только у кнопок тегов, поэтому пять сегментов
                // верхней панели держали прежнее дерево окна после каждой
                // пересборки содержимого.
                => ThemeBrushes.Observe(this, key, brush => { setter(brush); ApplyState(); });

            /// <summary>Собирает содержимое «иконка + текст» с цветом по состоянию выбора.</summary>
            private void UpdateContent()
            {
                // Невыбранное состояние — приглушённый текст (как в WPF SegmentRadioButton),
                // выбранное — контрастный текст на акцентной заливке.
                var brushKey = IsChecked == true ? "TextOnAccentBrush" : "TextSecondaryBrush";
                var sp = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    VerticalAlignment = VerticalAlignment.Center
                };
                // Пустой ключ означает кнопку без иконки: IconHelper на пустой ключ
                // подставляет запасную папку, и она выглядела бы как настоящая иконка.
                if (!string.IsNullOrEmpty(_iconKey))
                    sp.Children.Add(IconHelper.MakeIcon(_iconKey, _iconSize, brushKey));
                if (!string.IsNullOrEmpty(_text))
                {
                    // Кегль берётся с самой кнопки: локальные 13 перебивали
                    // значение, заданное снаружи, и теги панели фильтра
                    // не становились мельче.
                    var tb = new TextBlock
                    {
                        Text = _text,
                        FontSize = _textSize,
                        FontWeight = FontWeight.SemiBold,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    ThemeBrushes.Bind(tb, TextBlock.ForegroundProperty, brushKey);
                    sp.Children.Add(tb);
                }
                Content = sp;
            }

            private void ApplyState()
            {
                if (!IsEnabled)
                {
                    Opacity = 0.55;
                    Background = Brushes.Transparent;
                    BorderBrush = Brushes.Transparent;
                    BorderThickness = new Thickness(0);
                    return;
                }

                Opacity = 1.0;
                // Толщину надо вернуть: при отключении она обнулялась и обратно
                // не восстанавливалась.
                BorderThickness = _borderThickness;
                var idle = _restingBg ?? Brushes.Transparent;
                if (IsChecked == true)
                    Background = _pressed ? _accentPressed : (_hovered ? _accentHover : _accent);
                else if (_restingBg is not null && _hoverBorder is not null)
                    // Разметка при наведении меняет у чипа только рамку.
                    Background = _pressed ? _pressedBg : idle;
                else
                    Background = _pressed ? _pressedBg : (_hovered ? _hoverBg : idle);

                // Толщина постоянна, меняется только цвет: иначе фокус
                // расширял бы кнопку на четыре пикселя, а по её правому краю
                // выравнивается подпись «Название» в шапке списка.
                // Рамка в покое видна только там, где её просили: у тегов
                // панели фильтра. У остальных сегментов она прозрачная.
                BorderBrush = _focused
                    ? _accent
                    : (_hovered && _hoverBorder is not null
                        ? _hoverBorder
                        : (IsChecked == true && _restingBorder is not null ? _accent : _restingBorder ?? Brushes.Transparent));
            }
        }

        /// <summary>Тип собственной кнопки управления окном.</summary>
        private enum WindowControlKind
        {
            Minimize,
            Maximize,
            Close
        }

        /// <summary>
        /// Собственная кнопка управления окном (свернуть/развернуть/закрыть).
        /// Значок строится из StreamGeometry; цвет значка и hover-подложка следуют
        /// теме через ThemeBrushes (как у PanelButton/SegmentButton). Иконка разворота
        /// переключается между «квадрат» и «два квадрата» по состоянию окна.
        /// </summary>
        private sealed class WindowControlButton : Button
        {
            // Контуры по разметке WPF (MainWindow.xaml:205-231): черта, квадрат,
            // два квадрата и крест в координатном поле 13 на 13. Черта рисуется
            // заливкой, остальные три обводкой толщиной 1.2.
            private const string MinimizeData = "M0,5.5 L11,5.5 L11,6.5 L0,6.5 Z";
            private const string MaximizeData = "M1,1 H12 V12 H1 Z";
            private const string RestoreData = "M3,1 H9 V7 H3 Z M1,3 H7 V9 H1 Z";
            private const string CloseData = "M1,1 L12,12 M12,1 L1,12";

            /// <summary>Толщина обводки значков окна (MainWindow.xaml:216, 221, 231).</summary>
            private const double GlyphStrokeThickness = 1.2;

            private readonly MainWindow _window;
            private readonly WindowControlKind _kind;
            private readonly Avalonia.Controls.Shapes.Path _glyph;
            private readonly IDisposable? _stateSub;

            private IBrush _hoverBg = Brushes.Transparent;
            private IBrush _pressedBg = Brushes.Transparent;
            private IBrush _restGlyphBrush = Brushes.Transparent;
            private IBrush _hoverGlyphBrush = Brushes.Transparent;
            private IBrush _accentGlyphBrush = Brushes.Transparent;
            private bool _hovered;
            private bool _pressed;
            private bool _onAccent;

            // Красная подложка кнопки «закрыть» (классический алый), не зависит от темы:
            // наведение — алый, нажатие — чуть темнее. Значок на ней всегда белый.
            private static readonly IBrush CloseHoverBrush = new SolidColorBrush(Color.Parse("#E81123"));
            private static readonly IBrush ClosePressedBrush = new SolidColorBrush(Color.Parse("#C50F1F"));

            public WindowControlButton(MainWindow window, WindowControlKind kind)
            {
                _window = window;
                _kind = kind;

                Width = UiMetrics.Scaled(46);
                Height = UiMetrics.Scaled(34);
                Padding = new Thickness(0);
                HorizontalContentAlignment = HorizontalAlignment.Center;
                VerticalContentAlignment = VerticalAlignment.Center;
                Cursor = new Cursor(StandardCursorType.Hand);

                // Кастомный шаблон: скруглённый Border + ContentPresenter (без Fluent-хрома).
                Theme = new ControlTheme(typeof(Button))
                {
                    Setters =
                    {
                        new Setter(TemplatedControl.TemplateProperty, new FuncControlTemplate<WindowControlButton>((_, _) =>
                        {
                            // Углы прямые: в шапке кнопки окна идут встык, как в разметке
                            // (App.xaml:90, CornerRadius="0").
                            var border = new Border { CornerRadius = new CornerRadius(0) };
                            border[!Border.BackgroundProperty] = new TemplateBinding(TemplatedControl.BackgroundProperty);
                            border[!Border.BorderBrushProperty] = new TemplateBinding(TemplatedControl.BorderBrushProperty);
                            border[!Border.PaddingProperty] = new TemplateBinding(TemplatedControl.PaddingProperty);
                            var presenter = new ContentPresenter();
                            presenter[!ContentPresenter.ContentProperty] = new TemplateBinding(ContentControl.ContentProperty);
                            presenter[!ContentPresenter.HorizontalContentAlignmentProperty] = new TemplateBinding(ContentControl.HorizontalContentAlignmentProperty);
                            presenter[!ContentPresenter.VerticalContentAlignmentProperty] = new TemplateBinding(ContentControl.VerticalContentAlignmentProperty);
                            border.Child = presenter;
                            return border;
                        }))
                    }
                };

                // Черта «свернуть» у автора 11 на 11, квадрат и крест 13 на 13
                // (MainWindow.xaml:207, 214, 229).
                var glyphSize = _kind == WindowControlKind.Minimize ? 11.0 : 13.0;
                _glyph = new Avalonia.Controls.Shapes.Path
                {
                    Width = UiMetrics.Scaled(glyphSize),
                    Height = UiMetrics.Scaled(glyphSize),
                    Stretch = Stretch.Uniform,
                    Data = BuildGeometry()
                };
                if (_kind != WindowControlKind.Minimize)
                    _glyph.StrokeThickness = GlyphStrokeThickness;
                Content = _glyph;

                // Цвет значка и hover-подложка следуют теме. Кисть значка не привязывается
                // напрямую: он перекрашивается по состоянию (белый на красной подложке
                // «закрыть», цвет подписи на акцентной шапке), см. ApplyState.
                // Состояний три, как у автора: приглушённый в покое (App.xaml:79),
                // цвет подписи под курсором (App.xaml:94-97) и ButtonTextBrush
                // на акцентной шапке (App.xaml:116-118).
                ThemeBrushes.Observe(this, "TextSecondaryColorBrush", b => { _restGlyphBrush = b; ApplyState(); });
                ThemeBrushes.Observe(this, "TextPrimaryColorBrush", b => { _hoverGlyphBrush = b; ApplyState(); });
                ThemeBrushes.Observe(this, "ButtonTextBrush", b => { _accentGlyphBrush = b; ApplyState(); });
                ThemeBrushes.Observe(this, "ItemHoverBrush", b => { _hoverBg = b; ApplyState(); });
                ThemeBrushes.Observe(this, "AccentPressedBrush", b => { _pressedBg = b; ApplyState(); });

                PointerEntered += (_, _) => { _hovered = true; ApplyState(); };
                PointerExited += (_, _) => { _hovered = false; _pressed = false; ApplyState(); };
                PointerPressed += (_, _) => { _pressed = true; ApplyState(); };
                PointerReleased += (_, _) => { _pressed = false; ApplyState(); };
                PointerCaptureLost += (_, _) => { _pressed = false; ApplyState(); };
                this.GetObservable(IsEnabledProperty).Subscribe(new BoolObserver(_ => ApplyState()));

                // Иконка разворота зависит от состояния окна: квадрат / два квадрата.
                if (_kind == WindowControlKind.Maximize)
                    _stateSub = window.GetObservable(Window.WindowStateProperty).Subscribe(new WindowStateObserver(UpdateGlyph));

                ApplyState();
            }

            private Geometry BuildGeometry()
            {
                var data = _kind switch
                {
                    WindowControlKind.Minimize => MinimizeData,
                    WindowControlKind.Close => CloseData,
                    WindowControlKind.Maximize => _window.WindowState == WindowState.Maximized
                        ? RestoreData
                        : MaximizeData,
                    _ => MaximizeData
                };
                return StreamGeometry.Parse(data);
            }

            private void UpdateGlyph() => _glyph.Data = BuildGeometry();

            private void ApplyState()
            {
                if (!IsEnabled)
                {
                    Opacity = 0.55;
                    Background = Brushes.Transparent;
                    BorderBrush = Brushes.Transparent;
                    return;
                }

                Opacity = 1.0;
                BorderBrush = Brushes.Transparent;

                // В покое значок приглушён, на акцентной шапке красится цветом подписи
                // на акценте, а под курсором и то и другое уступает основному цвету
                // текста, как триггер IsMouseOver в шаблоне автора (App.xaml:94-97).
                var restBrush = _onAccent ? _accentGlyphBrush : _restGlyphBrush;

                if (_kind == WindowControlKind.Close)
                {
                    // Кнопка «закрыть»: красная подложка при наведении/нажатии, значок — белый.
                    var redActive = _pressed || _hovered;
                    Background = _pressed ? ClosePressedBrush : (_hovered ? CloseHoverBrush : Brushes.Transparent);
                    SetGlyphBrush(redActive ? Brushes.White : restBrush);
                }
                else
                {
                    Background = _pressed ? _pressedBg : (_hovered ? _hoverBg : Brushes.Transparent);
                    SetGlyphBrush(_hovered || _pressed ? _hoverGlyphBrush : restBrush);
                }
            }

            /// <summary>Черта «свернуть» залита, квадрат и крест обведены.</summary>
            private void SetGlyphBrush(IBrush brush)
            {
                if (_kind == WindowControlKind.Minimize)
                    _glyph.Fill = brush;
                else
                    _glyph.Stroke = brush;
            }

            /// <summary>
            /// Шапка активного окна залита акцентом, и значок на ней читается только
            /// цветом ButtonTextBrush (MainWindow.xaml.cs:588-615).
            /// </summary>
            public void SetOnAccent(bool onAccent)
            {
                if (_onAccent == onAccent)
                    return;
                _onAccent = onAccent;
                ApplyState();
            }

            /// <summary>Подписка на состояние окна живёт, пока кнопка в дереве.</summary>
            protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
            {
                base.OnDetachedFromVisualTree(e);
                _stateSub?.Dispose();
            }
        }

        /// <summary>Описание колонки списка баз: ключ, заголовок, ширина.</summary>
        private readonly record struct ListColumn(string Key, string Header, double Width);

        /// <summary>Минимум под имя базы: колонка звёздная, но схлопываться ей нельзя.</summary>
        private const double NameColumnMinWidth = 220;

        /// <summary>
        /// Сдвиг пустой группы: у неё нет кнопки разворота, и без сдвига её
        /// заголовок начинался бы левее соседних. Считается как у автора
        /// (Converters/GroupOffsetConverter.cs:37): отступ уровня плюс ширина
        /// кнопки разворота, без масштабирования компактным режимом.
        /// </summary>
        private static double EmptyGroupOffsetFor(int level)
            => level * Converters.LevelToThicknessConverter.IndentStep
                + Converters.LevelToThicknessConverter.ExpanderWidth;

        /// <summary>
        /// Ширина колонки звезды «избранное» в заголовке и в строке базы.
        /// Компактным режимом ведущие колонки не сжимаются: у автора их ширины
        /// заданы числом, а компактный режим меняет только отступы и шрифты.
        /// </summary>
        private static double FavoriteColumnWidth => 28;

        /// <summary>
        /// Ведущая колонка на месте прежних кнопок групп во всех трёх сетках
        /// списка. Сами кнопки живут в панели команд, а колонка осталась
        /// небольшим отступом выравнивания и обнуляется вместе с ними
        /// (MainWindow.xaml:651 и 1026).
        /// </summary>
        private double GroupButtonsColumnWidth
            => (_vm?.ShowExpandCollapseButtons ?? true) ? 24 : 0;

        /// <summary>Ширина колонки булавки «закреплено» в заголовке и в строке базы.</summary>
        private static double PinColumnWidth => 26;

        /// <summary>
        /// Ширина фиксированной колонки «Действия» в заголовке и в строке базы.
        /// Компактным режимом не сжимается: остальные колонки списка тоже берут
        /// ширину из настроек как есть (MainWindow.xaml:529), а кнопки действий
        /// в сжатой колонке налезали на «Сервер/База».
        /// </summary>
        private double ActionsColumnWidth
            => _vm is { ActionsColumnWidth: > 0 } vm ? vm.ActionsColumnWidth : 170;

        /// <summary>
        /// Номер колонки с именем: место кнопок групп, компенсатор, звезда, булавка.
        /// Одинаков у заголовка и у строк, потому что набор ведущих колонок общий.
        /// </summary>
        private const int NameHeaderColumn = 4;

        /// <summary>Номер колонки строки с именем базы, он же номер колонки заголовка.</summary>
        private const int NameRowColumn = NameHeaderColumn;

        /// <summary>
        /// Имя ведущего блока строки базы: по нему контейнер дерева находит панель,
        /// чтобы поставить ей отступ вложенности. Сдвигается только она, а сама
        /// строка стоит от левого края, иначе значения уехали бы от заголовков.
        /// </summary>
        internal const string LeadBlockName = "ВедущийБлокСтроки";

        /// <summary>Имя сетки заголовка группы: по нему она находится при перетаскивании разделителя колонок.</summary>
        private const string GroupRowGridName = "СеткаСтрокиГруппы";

        /// <summary>Минимальная ширина колонки при перетаскивании разделителя.</summary>
        private const double MinColumnWidth = 40;

        /// <summary>
        /// Минимальная ширина колонки «Действия» при перетаскивании разделителя: под общий
        /// предел в 40 точек в неё не помещаются три кнопки-иконки (запуск, конфигуратор,
        /// очистка кеша), и часть действий становится недоступна. В WPF тот же предел
        /// держит обработчик перетаскивания, а не разметка: MinWidth у колонки не задан
        /// намеренно, чтобы скрытая колонка схлопывалась в ноль.
        /// </summary>
        private const double ActionsColumnMinWidth = 120;

        /// <summary>Ширина зоны захвата разделителя колонок.</summary>
        private const double ResizeGripWidth = 8;

        /// <summary>
        /// Ширина колонки имени: пока её не тянули за разделитель, колонка
        /// звёздная и занимает остаток, после перетаскивания становится заданной.
        /// </summary>
        private GridLength NameColumnLength()
        {
            var width = _vm?.NameColumnWidth ?? 0;
            return width > 0 ? new GridLength(width) : new GridLength(1, GridUnitType.Star);
        }

        /// <summary>
        /// Колонки списка в порядке отображения, кроме первой (имя базы),
        /// которая занимает оставшееся место. Состав и ширины берутся
        /// из настроек, поэтому заголовок и строки всегда согласованы.
        /// </summary>
        private List<ListColumn> ListColumns()
        {
            var columns = new List<ListColumn>();
            if (_vm is null)
                return columns;

            // Ширина из настроек, а при нуле (настройка ещё не трогалась) запасная
            // из разметки: там она задана параметром конвертера (MainWindow.xaml:512-569).
            void Add(bool visible, string key, string header, double width, double fallback)
            {
                if (visible)
                    columns.Add(new ListColumn(key, LocalizationManager.T(header), width > 0 ? width : fallback));
            }

            // Порядок колонок берётся из настроек; неизвестные ключи пропускаются,
            // поэтому пользовательский список не ломает сборку при изменении состава.
            foreach (var key in _vm.ColumnOrderKeys)
            {
                switch (key)
                {
                    case "Version":
                        Add(_vm.ShowVersionColumn, "Version", "Column.Version", _vm.VersionColumnWidth, 120);
                        break;
                    case "Configuration":
                        Add(_vm.ShowConfigurationColumn, "Configuration", "Column.Configuration", _vm.ConfigurationColumnWidth, 160);
                        break;
                    case "ConfigurationVersion":
                        Add(_vm.ShowConfigurationVersionColumn, "ConfigurationVersion", "Column.ConfigurationVersion", _vm.ConfigurationVersionColumnWidth, 80);
                        break;
                    case "LaunchMode":
                        Add(_vm.ShowLaunchModeColumn, "LaunchMode", "Column.LaunchMode", _vm.LaunchModeColumnWidth, 120);
                        break;
                    case "ServerBase":
                        Add(_vm.ShowServerColumn, "ServerBase", "Column.ServerBase", _vm.ServerColumnWidth, 200);
                        break;
                    case "LastLaunch":
                        Add(_vm.ShowLastLaunchColumn, "LastLaunch", "Column.LastLaunch", _vm.LastLaunchColumnWidth, 140);
                        break;
                    case "Size":
                        Add(_vm.ShowSizeColumn, "Size", "Column.Size", _vm.SizeColumnWidth, 90);
                        break;
                }
            }
            return columns;
        }

        /// <summary>
        /// Сколько колонок данных стоит до колонки «Действия». Колонка участвует
        /// в пользовательском порядке наравне с остальными, как в разметке после
        /// правки автора (MainWindow.Columns.cs, задача апстрима 103: перенос
        /// колонки в настройках не давал никакого эффекта). Если ключа
        /// «Действия» в сохранённом порядке нет, она встаёт сразу после режима
        /// запуска, как было раньше.
        /// </summary>
        private int ActionsOffsetInColumns(List<ListColumn> columns)
        {
            var order = _vm?.ColumnOrderKeys;
            if (order is not null)
            {
                var actionsAt = -1;
                for (var i = 0; i < order.Count; i++)
                    if (order[i] == "Actions")
                    {
                        actionsAt = i;
                        break;
                    }

                if (actionsAt >= 0)
                {
                    // Считаем только те колонки порядка, которые сейчас видимы:
                    // скрытая колонка места не занимает.
                    var before = 0;
                    for (var i = 0; i < actionsAt; i++)
                        for (var c = 0; c < columns.Count; c++)
                            if (columns[c].Key == order[i])
                            {
                                before++;
                                break;
                            }
                    return before;
                }
            }

            for (var i = 0; i < columns.Count; i++)
                if (columns[i].Key == "LaunchMode")
                    return i + 1;
            return columns.Count;
        }

        /// <summary>Значение колонки для конкретной базы.</summary>
        private static string ColumnValue(Infobase ib, string key) => key switch
        {
            // Версия показывается вместе с разрядностью, как в разметке
            // (MainWindow.xaml:1249): свойство PlatformVersionDisplay автор
            // добавил в модель, а колонка брала голую версию.
            "Version" => ib.PlatformVersionDisplay ?? string.Empty,
            "Configuration" => ib.ConfigurationName ?? string.Empty,
            "ConfigurationVersion" => ib.ConfigurationVersion ?? string.Empty,
            // Режим запуска показывается разобранным, а серверная колонка всегда
            // берёт ServerDatabaseDisplay, в том числе у веб-баз: подстановка WebUrl
            // была расхождением с разметкой (MainWindow.xaml:1261 и 1265).
            "LaunchMode" => ib.ParsedLaunchMode ?? string.Empty,
            "ServerBase" => ib.ServerDatabaseDisplay ?? string.Empty,
            "LastLaunch" => ib.LastLaunchDisplay ?? string.Empty,
            "Size" => ib.FileSizeDisplay ?? string.Empty,
            _ => string.Empty
        };

        /// <summary>
        /// Строка заголовков колонок над списком. Пересобирается вместе
        /// со списком, чтобы состав колонок совпадал со строками.
        /// </summary>
        private Control BuildColumnHeader()
        {
            _columnHeaderRow = new Grid();
            _columnHeader = new Border
            {
                // Имя как у шапки списка в разметке WPF: по нему ThemeManager
                // применяет шрифт области «Шапка списка».
                Name = "HeaderGrid",
                // Отступ 0,2 из разметки (MainWindow.xaml:484). Горизонтальный
                // обязан совпадать с отступом строки, иначе заголовки разъедутся
                // со значениями; в разметке он нулевой в обоих местах.
                Padding = new Thickness(0, 2),
                // Высоту шапке задавали кнопки блока групп; после их переноса
                // в панель команд её держит минимум из разметки
                // (MainWindow.xaml:639: MinHeight 36 и прозрачность 0.95).
                MinHeight = 36,
                Opacity = 0.95,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Child = _columnHeaderRow
            };
            ThemeBrushes.Bind(_columnHeader, Border.BorderBrushProperty, "BorderColorBrush");
            return _columnHeader;
        }

        /// <summary>
        /// Ставит пересборку заголовка в очередь диспетчера. Настройки колонок
        /// уведомляют о шестнадцати свойствах подряд, и без склейки заголовок
        /// пересобирался бы на каждое из них.
        /// </summary>
        /// <summary>
        /// Подсказка, описывающая колонку списка. Есть не у всех: набор взят
        /// из разметки WPF, где такие подсказки стоят только у части заголовков.
        /// </summary>
        private static string? ColumnHeaderTooltipKey(string columnKey) => columnKey switch
        {
            "Size" => "Main.ColumnSizeTooltip",
            "Configuration" => "Main.ColumnNameTooltip",
            _ => null
        };

        private void QueueColumnHeaderRefresh()
        {
            if (_columnHeaderRefreshQueued)
                return;
            _columnHeaderRefreshQueued = true;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _columnHeaderRefreshQueued = false;
                RefreshColumnHeader();
            });
        }

        /// <summary>
        /// Пересобирает панель инструментов и заголовки колонок по текущим настройкам.
        /// Слева направо: кнопки групп, компенсатор отступа дерева, звезда, булавка,
        /// имя базы, дальше колонки значений. Первые колонки повторяются в строке
        /// базы теми же ширинами, поэтому заголовок и значения стоят друг под другом.
        /// </summary>
        private void RefreshColumnHeader()
        {
            if (_vm is null || _columnHeaderRow is null || _columnHeader is null)
                return;

            _columnHeaderRow.Children.Clear();
            _columnHeaderRow.ColumnDefinitions.Clear();

            var columns = ListColumns();

            // Колонки заголовка строятся тем же построителем, что и колонки строк:
            // набор ведущих колонок обязан совпадать, иначе значения разъезжаются
            // с заголовками (MainWindow.xaml:1071-1076).
            _headerOffsetColumn = new ColumnDefinition { Width = new GridLength(0) };
            var actionsOffset = ActionsOffsetInColumns(columns);
            AddListColumns(_columnHeaderRow, _vm.ShowFavoritesButton, _vm.ShowPinnedButton, _headerOffsetColumn);


            var nameHeader = ColumnHeader(LocalizationManager.T("Column.Name"), IconHelper.ColumnIconKey("Name"));
            // У «Названия» отступ слева нулевой: заголовок равняется по тексту строк
            // списка, а не по границе колонки. В разметке WPF так же (MainWindow.xaml:739).
            nameHeader.Margin = new Thickness(0, 0, 8, 4);
            MakeSortableHeader(nameHeader, "Name", LocalizationManager.T("Main.ColumnNameSortTooltip"));
            _columnHeaderRow.Children.Add(nameHeader);
            // Подпись охватывает ведущие колонки и колонку имени и прижата влево,
            // как в разметке после переноса кнопок в панель команд
            // (MainWindow.xaml:739, Grid.Column=0 и ColumnSpan=5): ведущие колонки
            // стоят пустыми ради выравнивания значений, а подпись начинается
            // у левого края шапки, а не за ними.
            Grid.SetColumn(nameHeader, 0);
            Grid.SetColumnSpan(nameHeader, NameHeaderColumn + 1);
            nameHeader.HorizontalAlignment = HorizontalAlignment.Left;

            _headerColumnIndex.Clear();
            _headerColumnIndex["Name"] = NameHeaderColumn;

            var nameGrip = BuildResizeGrip("Name", NameHeaderColumn);
            _columnHeaderRow.Children.Add(nameGrip);

            // Заголовки идут по порядку колонок данных, перескакивая колонку
            // «Действия», встроенную после «Режима запуска».
            var dataColumn = NameHeaderColumn + 1;
            for (var i = 0; i < columns.Count; i++)
            {
                if (i == actionsOffset)
                    dataColumn++;
                _headerColumnIndex[columns[i].Key] = dataColumn;

                var text = ColumnHeader(columns[i].Header, IconHelper.ColumnIconKey(columns[i].Key));
                if (columns[i].Key == "LastLaunch")
                    MakeSortableHeader(text, "LastLaunchDate", LocalizationManager.T("Main.ColumnLastLaunchSortTooltip"));
                // Подсказка, описывающая саму колонку. В разметке WPF она есть
                // не у всех заголовков, набор взят оттуда (MainWindow.xaml:687, 697).
                if (ColumnHeaderTooltipKey(columns[i].Key) is { } tooltipKey)
                    ToolTip.SetTip(text, LocalizationManager.T(tooltipKey));
                _columnHeaderRow.Children.Add(text);
                Grid.SetColumn(text, dataColumn);
                AttachColumnContextMenu(text, columns[i].Key);

                var grip = BuildResizeGrip(columns[i].Key, dataColumn);
                _columnHeaderRow.Children.Add(grip);
                dataColumn++;
            }

            // Подпись колонки «Действия» — сразу после колонки «Режим запуска».
            // Разделитель у неё есть и в разметке (ActionsSplitter,
            // MainWindow.xaml:745), поэтому ширина тянется и сохраняется.
            // Скрытая колонка остаётся нулевой ширины (AddListColumns), поэтому
            // заголовок с разделителем не строится вовсе (issue #158).
            if (_vm.ShowActionsColumn)
            {
                var actionsColumn = NameHeaderColumn + 1 + actionsOffset;
                _headerColumnIndex["Actions"] = actionsColumn;
                var actionsHeader = ColumnHeader(LocalizationManager.T("Column.Actions"), IconHelper.ColumnIconKey("Actions"));
                ToolTip.SetTip(actionsHeader, LocalizationManager.T("Main.Actions"));
                _columnHeaderRow.Children.Add(actionsHeader);
                Grid.SetColumn(actionsHeader, actionsColumn);
                AttachColumnContextMenu(actionsHeader, "Actions");
                _columnHeaderRow.Children.Add(BuildResizeGrip("Actions", actionsColumn));
            }

            UpdateListMinWidth();

            QueueHeaderAlign();
        }

        /// <summary>
        /// Прикрепляет к заголовку колонки контекстное меню (issue #173): пункт
        /// «Скрыть колонку» скрывает колонку по её ключу, пункт «Открыть настройки
        /// колонок» открывает окно настроек сразу на подвкладке «Колонки».
        /// </summary>
        private void AttachColumnContextMenu(Control header, string key)
        {
            var hide = new MenuItem { Header = LocalizationManager.T("Column.HideColumn") };
            hide.Click += (_, _) => _vm?.SetColumnVisible(key, false);
            var open = new MenuItem { Header = LocalizationManager.T("Settings.Columns.OpenSettings") };
            open.Click += (_, _) => OpenSettingsOnColumnsTab();
            var menu = new ContextMenu();
            menu.Items.Add(hide);
            menu.Items.Add(open);
            header.ContextMenu = menu;
        }

        /// <summary>Открывает окно настроек сразу на подвкладке «Колонки» (issue #173).</summary>
        private void OpenSettingsOnColumnsTab()
        {
            if (_vm is null)
                return;
            var settings = new Configuration_Management.SettingsWindow(_vm);
            settings.SelectColumnsTab();
            settings.ShowDialog(this);
        }

        /// <summary>
        /// Блок кнопок над списком: развернуть и свернуть все группы и две
        /// сортировки групп (только при группировке), а также переключатель
        /// тегов в строках, который нужен всегда.
        /// </summary>
        private Control BuildGroupToolbar()
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 2,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left
            };

            if (_vm?.ShowExpandCollapseButtons == true)
            {
                // Здесь у автора значки из пакета (PackIcon Kind="ExpandAll"
                // и "CollapseAll", MainWindow.xaml:586 и 593), а не одноимённые
                // ключи его словаря: те шевроны и живут в других местах.
                panel.Children.Add(HeaderIconButton("IconExpandAllBox",
                    LocalizationManager.T("Main.ExpandAllGroups"), "ExpandAllGroupsCommand"));
                panel.Children.Add(HeaderIconButton("IconCollapseAllBox",
                    LocalizationManager.T("Main.CollapseAllGroups"), "CollapseAllGroupsCommand"));
                panel.Children.Add(HeaderIconButton("IconSortAscending",
                    LocalizationManager.T("Main.SortGroupsAscending"), "SortGroupsAscendingCommand"));
                panel.Children.Add(HeaderIconButton("IconSortDescending",
                    LocalizationManager.T("Main.SortGroupsDescending"), "SortGroupsDescendingCommand"));
            }

            var tagsToggle = BuildTagsInListToggle();
            panel.Children.Add(tagsToggle);

            return panel;
        }

        /// <summary>
        /// Переключатель показа тегов в строках списка. Сделан тем же
        /// сегментным контролом, что и переключатели верхней панели: у Fluent
        /// в нажатом состоянии свой синий фон, чужой для этой темы.
        /// </summary>
        private Control BuildTagsInListToggle()
        {
            var toggle = MakeSegmentToggle("IconTag", LocalizationManager.T("Main.ToggleListTags"), iconSize: 14);
            toggle.IsChecked = _vm?.ShowTags ?? false;
            toggle.VerticalAlignment = VerticalAlignment.Center;
            // Отступ у него слева, а не справа, как у остальных сегментов
            // (MainWindow.xaml:526).
            toggle.Margin = new Thickness(2, 0, 0, 0);
            toggle.Click += (_, _) =>
            {
                if (_vm is not null)
                    _vm.ShowTags = toggle.IsChecked == true;
            };
            return toggle;
        }

        /// <summary>Компактная иконко-кнопка панели инструментов над списком.</summary>
        private Button HeaderIconButton(string iconKey, string tooltip, string commandPath)
        {
            // Оформление берёт тема IconButton разметки (LightTheme.xaml:561):
            // прозрачный фон, скругление 8, подсветка при наведении, отступ 8.
            var button = new Button
            {
                // Значок 18, как у этих же кнопок в панели команд разметки
                // (MainWindow.xaml:499, 506, 513, 520).
                Content = IconHelper.MakeIcon(iconKey, UiMetrics.Scaled(18), "TextSecondaryBrush"),
                Padding = new Thickness(UiMetrics.Scaled(8)),
                MinWidth = 0,
                MinHeight = 0,
                VerticalAlignment = VerticalAlignment.Center
            };
            button.Styled(Themes.ControlThemes.IconButton);
            ToolTip.SetTip(button, tooltip);
            button.Bind(Button.CommandProperty, new Binding(commandPath));
            return button;
        }

        /// <summary>
        /// Зона захвата у правого края колонки заголовка: тонкая линия по центру
        /// и широкая невидимая полоса вокруг неё, иначе в разделитель трудно попасть.
        /// </summary>
        private Border BuildResizeGrip(string key, int column)
        {
            var line = new Border
            {
                Width = 1,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 4),
                Opacity = 0.55
            };
            ThemeBrushes.Bind(line, Border.BackgroundProperty, "BorderColorBrush");

            var grip = new Border
            {
                Width = ResizeGripWidth,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Stretch,
                Background = Brushes.Transparent,
                ZIndex = 2,
                Cursor = new Cursor(StandardCursorType.SizeWestEast),
                Tag = key,
                Child = line
            };
            ToolTip.SetTip(grip, LocalizationManager.T("Main.ResizeColumnTooltip"));
            Grid.SetColumn(grip, column);
            grip.PointerPressed += OnColumnResizePressed;
            grip.PointerMoved += OnColumnResizeMoved;
            grip.PointerReleased += OnColumnResizeReleased;
            // Захват теряется не только отпусканием кнопки: его снимает и оконная
            // система, и пересборка окна в компактном режиме. Без этого обработчика
            // перетаскивание осталось бы незавершённым, а ширина несохранённой.
            grip.PointerCaptureLost += OnColumnResizeCaptureLost;
            return grip;
        }

        /// <summary>Формат содержимого перетаскивания: сама нагрузка живёт в поле окна.</summary>
        private const string DragPayloadMarker = "ConfigurationManagement.Row";

        /// <summary>
        /// Фиксация того, что поедет: как в WPF, нагрузка берётся в нажатии,
        /// а не в движении. Иначе при сдвиге курсора на дочернюю строку под ним
        /// оказывается другой узел и вместо группы уезжает база.
        /// </summary>
        private void OnTreeDragPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            _dragPayload = null;
            var point = e.GetCurrentPoint(this);
            if (!point.Properties.IsLeftButtonPressed)
                return;

            _dragStartPoint = point.Position;

            // Клик по кнопке или полю ввода внутри строки перетаскивание не начинает:
            // звезда, булавка, чип тега и раскрытие группы должны работать как обычно.
            if (e.Source is not Visual source
                || source.FindAncestorOfType<Button>(includeSelf: true) is not null
                || source.FindAncestorOfType<TextBox>(includeSelf: true) is not null)
                return;

            var item = source.FindAncestorOfType<TreeViewItem>(includeSelf: true);
            _dragPayload = item?.DataContext switch
            {
                Infobase infobase => infobase,
                GroupNodeViewModel node when node.Group is not null => node,
                _ => null
            };
        }

        private async void OnTreeDragPointerMoved(object? sender, PointerEventArgs e)
        {
            if (_isDragging || _dragPayload is null || _vm is null)
                return;

            var point = e.GetCurrentPoint(this);
            if (!point.Properties.IsLeftButtonPressed)
            {
                _dragPayload = null;
                return;
            }

            // Порог сдвига обязателен: без него обычный клик по строке начинал бы
            // перетаскивание. Аналога SystemParameters в Avalonia нет.
            if (Math.Abs(point.Position.X - _dragStartPoint.X) < DragThreshold
                && Math.Abs(point.Position.Y - _dragStartPoint.Y) < DragThreshold)
                return;

            var transfer = new DataTransfer();
            transfer.Add(DataTransferItem.CreateText(DragPayloadMarker));

            _isDragging = true;
            try
            {
                await DragDrop.DoDragDropAsync(e, transfer, DragDropEffects.Move);
            }
            catch (Exception ex)
            {
                _vm.LogWarning($"Перетаскивание прервано: {ex.Message}");
            }
            finally
            {
                _isDragging = false;
                _dragPayload = null;
            }
        }

        /// <summary>Порог сдвига в пикселях, после которого нажатие считается перетаскиванием.</summary>
        private const double DragThreshold = 4;

        private void OnTreeDragOver(object? sender, DragEventArgs e)
        {
            e.DragEffects = DragDropEffects.None;
            ResolveDropTarget(e.Source as Visual, out var targetNode, out _);
            if (targetNode is not null && IsDropAllowed(_dragPayload, targetNode))
                e.DragEffects = DragDropEffects.Move;
            e.Handled = true;
        }

        private void OnTreeDrop(object? sender, DragEventArgs e)
        {
            e.Handled = true;
            if (_vm is null)
                return;

            // Отпускание поднимается из насоса сырых событий, а не со стека
            // DoDragDropAsync: исключение отсюда не дало бы завершиться самой
            // операции, и перетаскивание осталось бы включённым навсегда,
            // вместе с курсором и перехватом движений по всему приложению.
            try
            {
                ApplyDrop(e);
            }
            catch (Exception ex)
            {
                _vm.LogWarning($"Перенос не выполнен: {ex.Message}");
            }
        }

        private void ApplyDrop(DragEventArgs e)
        {
            if (_vm is null)
                return;
            var payload = _dragPayload;
            ResolveDropTarget(e.Source as Visual, out var targetNode, out var insertBefore);

            // Проверки повторяются здесь намеренно: в Avalonia отпускание приходит
            // на последнюю цель независимо от того, что вернул DragOver, поэтому
            // на отказ DragOver полагаться нельзя.
            if (targetNode is null || !IsDropAllowed(payload, targetNode))
                return;

            if (payload is GroupNodeViewModel sourceNode && sourceNode.Group is not null)
            {
                _vm.MoveGroupUnder(sourceNode.Group, targetNode.Group?.Id ?? string.Empty);
                return;
            }

            if (payload is not Infobase infobase)
                return;

            if (ReferenceEquals(insertBefore, infobase))
                insertBefore = null;

            // Сброс на «Закреплённые» группу не меняет: там лежат базы из разных групп,
            // и перенос туда означал бы потерю группы.
            if (string.Equals(targetNode.Marker, GroupNodeViewModel.PinnedMarker, StringComparison.Ordinal))
            {
                _vm.MoveInfobaseToGroup(infobase, infobase.Group ?? string.Empty, insertBefore);
                return;
            }

            var path = targetNode.Group is null
                ? string.Empty
                : GroupHierarchyHelper.GetFullPath(targetNode.Group, _vm.Groups);
            _vm.MoveInfobaseToGroup(infobase, path, insertBefore);
        }

        /// <summary>
        /// Допустимость сброса. Узлы без группы разрешены не все: «Все базы» и узел
        /// результата поиска группой не являются, и сброс на них молча обнулил бы
        /// группу базы. В WPF эта ловушка есть, здесь она закрыта.
        /// </summary>
        private bool IsDropAllowed(object? payload, GroupNodeViewModel targetNode)
        {
            if (_vm is null)
                return false;
            if (payload is Infobase)
            {
                return targetNode.Group is not null
                    || string.Equals(targetNode.Marker, GroupNodeViewModel.PinnedMarker, StringComparison.Ordinal)
                    || string.Equals(targetNode.Marker, GroupNodeViewModel.NoGroupMarker, StringComparison.Ordinal);
            }

            if (payload is not GroupNodeViewModel sourceNode || sourceNode.Group is null)
                return false;

            // Узел без группы означает для группы корень, но не любой: «Все базы»,
            // результат поиска и «Закреплённые» группой не являются, и сброс на них
            // молча вынес бы подгруппу наверх. Вернуть её на место в интерфейсе
            // нечем: смена родителя есть только у перетаскивания.
            if (targetNode.Group is null
                && !string.Equals(targetNode.Marker, GroupNodeViewModel.NoGroupMarker, StringComparison.Ordinal))
                return false;

            var targetId = targetNode.Group?.Id ?? string.Empty;
            if (string.Equals(sourceNode.Group.Id, targetId, StringComparison.OrdinalIgnoreCase))
                return false;

            // Перенос под собственного потомка создал бы цикл в иерархии.
            return string.IsNullOrEmpty(targetId)
                   || !GroupHierarchyHelper.IsAncestorOrSelf(targetId, sourceNode.Group.Id, _vm.Groups);
        }

        /// <summary>
        /// Цель сброса: группа и база, перед которой вставить. Курсор над строкой
        /// базы означает вставку перед ней в её же группу.
        /// </summary>
        private static void ResolveDropTarget(Visual? source, out GroupNodeViewModel? targetNode, out Infobase? insertBefore)
        {
            targetNode = null;
            insertBefore = null;
            if (source is null)
                return;

            var item = source.FindAncestorOfType<TreeViewItem>(includeSelf: true);
            while (item is not null)
            {
                switch (item.DataContext)
                {
                    case GroupNodeViewModel node:
                        targetNode = node;
                        return;
                    case Infobase infobase when insertBefore is null:
                        insertBefore = infobase;
                        break;
                }
                item = item.FindAncestorOfType<TreeViewItem>();
            }
        }

        private void OnColumnResizePressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is not Border grip || grip.Tag is not string key || _columnHeaderRow is null)
                return;
            if (_resizeKey is not null)
                return;
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                return;

            var column = Grid.GetColumn(grip);
            if (column < 0 || column >= _columnHeaderRow.ColumnDefinitions.Count)
                return;

            _resizeKey = key;
            _resizePointerId = e.Pointer.Id;
            _resizeStartWidth = _columnHeaderRow.ColumnDefinitions[column].ActualWidth;
            _resizeStartX = e.GetPosition(this).X;

            // Сетки строк собираются один раз на перетаскивание: во время него
            // дерево не пересобирается, а обход визуального дерева на каждое
            // движение указателя стоил бы дорого на списке в сотни баз.
            _resizeRowGrids.Clear();
            if (_tree is not null)
            {
                foreach (var card in _tree.GetVisualDescendants().OfType<InfobaseRowCard>())
                {
                    if (card.Child is Grid grid)
                        _resizeRowGrids.Add(grid);
                }
                // Заголовки групп ведут те же колонки и обязаны ехать вместе
                // со строками баз, иначе кнопки группы расходятся с кнопками
                // строк на всё время перетаскивания.
                foreach (var group in _tree.GetVisualDescendants().OfType<Grid>())
                {
                    if (group.Name == GroupRowGridName)
                        _resizeRowGrids.Add(group);
                }
            }

            e.Pointer.Capture(grip);
            e.Handled = true;
        }

        private void OnColumnResizeMoved(object? sender, PointerEventArgs e)
        {
            if (_resizeKey is null || e.Pointer.Id != _resizePointerId)
                return;
            if (sender is not Border grip || !ReferenceEquals(e.Pointer.Captured, grip))
                return;

            var minWidth = _resizeKey == "Actions" ? ActionsColumnMinWidth : MinColumnWidth;
            var width = Math.Max(minWidth, _resizeStartWidth + e.GetPosition(this).X - _resizeStartX);
            ApplyColumnWidth(_resizeKey, width);
            _vm?.UpdateColumnWidth(_resizeKey, width, save: false);
        }

        private void OnColumnResizeReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (_resizeKey is null || e.Pointer.Id != _resizePointerId)
                return;

            e.Pointer.Capture(null);
            FinishColumnResize();
            e.Handled = true;
        }

        private void OnColumnResizeCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        {
            if (_resizeKey is null || e.Pointer.Id != _resizePointerId)
                return;

            FinishColumnResize();
        }

        /// <summary>
        /// Завершает перетаскивание: пишет ширину в настройки один раз, а не
        /// на каждое движение указателя, и отпускает собранные сетки строк.
        /// </summary>
        private void FinishColumnResize()
        {
            if (_resizeKey is not null)
                _vm?.UpdateColumnWidth(_resizeKey, ColumnWidthOf(_resizeKey), save: true);

            _resizeKey = null;
            _resizeRowGrids.Clear();
        }

        /// <summary>Текущая ширина колонки заголовка по её ключу.</summary>
        private double ColumnWidthOf(string key)
        {
            var index = HeaderColumnIndex(key);
            return index >= 0 && _columnHeaderRow is not null && index < _columnHeaderRow.ColumnDefinitions.Count
                ? _columnHeaderRow.ColumnDefinitions[index].ActualWidth
                : 0;
        }

        /// <summary>Номер колонки заголовка по ключу колонки списка.</summary>
        private int HeaderColumnIndex(string key) =>
            _headerColumnIndex.TryGetValue(key, out var index) ? index : -1;

        /// <summary>
        /// Ведёт ширину колонки в двух сетках сразу: в заголовке и в каждой
        /// построенной строке. Пересборки дерева при этом не происходит, поэтому
        /// перетаскивание не мигает списком.
        /// </summary>
        private void ApplyColumnWidth(string key, double width)
        {
            var header = HeaderColumnIndex(key);
            if (header < 0 || _columnHeaderRow is null || header >= _columnHeaderRow.ColumnDefinitions.Count)
                return;

            _columnHeaderRow.ColumnDefinitions[header].Width = new GridLength(width);

            var row = header - (NameHeaderColumn - NameRowColumn);
            foreach (var grid in _resizeRowGrids)
            {
                if (row < 0 || row >= grid.ColumnDefinitions.Count)
                    continue;
                grid.ColumnDefinitions[row].Width = new GridLength(width);
            }

            // Минимум области считается заново: иначе после сужения колонки
            // прокручиваемая область осталась бы прежней ширины с пустотой справа.
            UpdateListMinWidth();
            // И заново выравнивается шапка: новая ширина колонки меняет и общую
            // ширину сеток, от равенства которой зависит совпадение колонок.
            QueueHeaderAlign();
        }

        /// <summary>
        /// Минимальная ширина области списка: сумма колонок заголовка плюс отступы.
        /// При более узком окне включается горизонтальная прокрутка, и заголовок
        /// едет вместе со строками, а не разъезжается с ними.
        /// </summary>
        private void UpdateListMinWidth()
        {
            if (_listContent is null || _columnHeaderRow is null
                || _columnHeaderRow.ColumnDefinitions.Count <= NameHeaderColumn)
                return;

            var definitions = _columnHeaderRow.ColumnDefinitions;
            // Считаются все ведущие колонки, включая нулевую с местом под кнопки
            // групп: без неё минимум занижался, и правые колонки подрезались
            // раньше, чем включалась горизонтальная прокрутка.
            double lead = 0;
            for (var i = 0; i < NameHeaderColumn; i++)
                lead += definitions[i].Width.IsAbsolute ? definitions[i].Width.Value : 0;

            var nameWidth = definitions[NameHeaderColumn].Width.IsAbsolute
                ? definitions[NameHeaderColumn].Width.Value
                : NameColumnMinWidth;

            double values = 0;
            for (var i = NameHeaderColumn + 1; i < definitions.Count; i++)
                values += definitions[i].Width.IsAbsolute ? definitions[i].Width.Value : 0;

            _listContent.MinWidth = nameWidth + lead
                + UiMetrics.PaddingControl * 2 + values;
        }

        /// <summary>Делает заголовок колонки кликабельным: клик меняет поле сортировки.</summary>
        private void MakeSortableHeader(Control header, string field, string tooltip)
        {
            header.Cursor = new Cursor(StandardCursorType.Hand);
            ToolTip.SetTip(header, tooltip);
            header.Tapped += (_, _) => _vm?.SetSortField(field);
        }

        /// <summary>
        /// Ставит выравнивание заголовка со строками в очередь диспетчера:
        /// положение строки известно только после раскладки.
        /// </summary>
        private void QueueHeaderAlign()
        {
            if (_headerAlignQueued)
                return;
            _headerAlignQueued = true;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _headerAlignQueued = false;
                AlignHeaderToRows();
            }, Avalonia.Threading.DispatcherPriority.Loaded);
        }

        /// <summary>
        /// Подгоняет ширину колонки-компенсатора так, чтобы звезда и булавка
        /// заголовка встали над теми же значками первой строки базы. Дерево
        /// сдвигает строки на отступ уровня, и без компенсации заголовок
        /// разошёлся бы со списком.
        /// </summary>
        private void AlignHeaderToRows()
        {
            if (_headerOffsetColumn is null || _columnHeaderRow is null || _tree is null
                || _columnHeaderRow.ColumnDefinitions.Count <= NameHeaderColumn)
                return;

            // Арифметика авторская (MainWindow.Columns.cs:265): компенсатор равен
            // разнице между началом первой колонки значений строки и началом той же
            // колонки заголовка, посчитанным без самого компенсатора. Звёздная колонка
            // имени исключена из обеих сумм ведущих колонок (issue #191): иначе компенсатор
            // зависит от собственного прошлого значения и ширина списка растёт до десятков
            // тысяч точек, из-за чего колонки, кроме «Названия», уезжают за правый край.
            // Ведущие колонки у заголовка и строки одинаковы, а общая ширина общая, поэтому
            // после совмещения имя занимает одинаковое место в обеих сетках, и разницу даёт
            // только сдвиг строки деревом, после чего значения стоят ровно под заголовками.
            Grid? rowGrid = null;
            double rowOrigin = 0;
            foreach (var card in _tree.GetVisualDescendants().OfType<InfobaseRowCard>())
            {
                if (card.Child is not Grid content)
                    continue;
                var origin = content.TranslatePoint(new Point(0, 0), _columnHeaderRow);
                if (origin is null)
                    continue;
                if (rowGrid is null || origin.Value.X < rowOrigin)
                {
                    rowGrid = content;
                    rowOrigin = origin.Value.X;
                }
            }

            double offset = 0;
            if (rowGrid is not null && rowGrid.ColumnDefinitions.Count > NameRowColumn)
            {
                double rowLead = 0;
                for (var i = 0; i < NameRowColumn; i++)
                    rowLead += rowGrid.ColumnDefinitions[i].ActualWidth;

                double headerLead = 0;
                for (var i = 0; i < NameHeaderColumn; i++)
                {
                    if (!ReferenceEquals(_columnHeaderRow.ColumnDefinitions[i], _headerOffsetColumn))
                        headerLead += _columnHeaderRow.ColumnDefinitions[i].ActualWidth;
                }

                offset = Math.Max(0, (rowOrigin + rowLead) - headerLead);
            }

            if (Math.Abs(offset - _headerOffsetColumn.Width.Value) > 0.5)
                _headerOffsetColumn.Width = new GridLength(offset);

            SyncHeaderWidthWithList();




        }

        /// <summary>
        /// Приравнивает ширину сетки заголовка ширине содержимого списка
        /// (MainWindow.Columns.cs:299). Колонка «Название» звёздная, и лишний
        /// пиксель общей ширины целиком уходит в неё: если заголовок шире строк
        /// на полосу прокрутки, все колонки значений строк оказываются левее
        /// своих заголовков ровно на эту разницу.
        /// </summary>
        private void SyncHeaderWidthWithList()
        {
            if (_columnHeaderRow is null || _tree is null)
                return;

            double extent = _tree.Bounds.Width;
            double viewport = _tree.Bounds.Width;
            if (TreeScroll is { } scroll)
            {
                extent = Math.Max(scroll.Extent.Width, scroll.Viewport.Width);
                viewport = scroll.Viewport.Width;
            }

            var target = Math.Max(extent, viewport);
            if (target > 0 && Math.Abs(_columnHeaderRow.Width - target) > 0.5)
                _columnHeaderRow.Width = target;
        }

        /// <summary>
        /// Заголовок колонки: иконка колонки и подпись. Иконки заголовков совпадают
        /// с иконками списка колонок на вкладке «Отображение» — оба берут ключ из
        /// <see cref="IconHelper.ColumnIconKey"/>.
        /// </summary>
        private static Control ColumnHeader(string text, string iconKey)
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 5,
                VerticalAlignment = VerticalAlignment.Center,
                // Отступы как в разметке WPF (Margin="6,0,6,4"): без них подпись
                // встаёт вплотную к соседней, а горизонтальный StackPanel меряет
                // детей без ограничения по ширине, поэтому сам текст не подрезается.
                Margin = new Thickness(6, 0, 6, 4),
                ClipToBounds = true
            };
            panel.Children.Add(IconHelper.MakeIcon(iconKey, UiMetrics.Scaled(13), "TextSecondaryBrush"));

            var block = new TextBlock
            {
                Text = text,
                FontSize = UiMetrics.ScaledFont(12),
                FontWeight = FontWeight.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            ThemeBrushes.Bind(block, TextBlock.ForegroundProperty, "TextSecondaryBrush");
            panel.Children.Add(block);
            return panel;
        }

        /// <summary>
        /// Панель отбора по тегам: по кнопке на каждый тег и кнопка сброса.
        /// Видимость подчинена переключателю «теги» в верхней панели, а состав
        /// пересобирается при каждом изменении набора тегов.
        /// </summary>
        private Control BuildTagFilterPanel()
        {
            _tagPanelItems = new WrapPanel { Orientation = Orientation.Horizontal };

            _tagClearButton = new Button
            {
                // Короткая подпись и кегль 11, как в разметке (MainWindow.xaml:392):
                // полная строка делала кнопку заметно длиннее.
                Content = ThemedIconAndText("IconClose", LocalizationManager.T("Common.Clear"),
                    "ButtonTextBrush", UiMetrics.Scaled(12), centered: false, fontSize: UiMetrics.ScaledFont(11)),
                Padding = new Thickness(4, 2),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            // Оформление и состояния берёт тема HeaderIconButton разметки
            // (LightTheme.xaml:586): наведение, нажатие и гашение у автора
            // заданы, а здесь кнопка была плоской без единого состояния.
            _tagClearButton.Styled(Themes.ControlThemes.HeaderIconButton);
            ToolTip.SetTip(_tagClearButton, LocalizationManager.T("Main.ClearTagFilters"));
            _tagClearButton.Bind(Button.CommandProperty, new Binding("ClearTagFiltersCommand"));

            // Подсказка остаётся на месте и когда тегов нет: панель не прячется,
            // иначе переключатель «теги» выглядел бы неработающим. Раскладка как
            // в WPF-версии: подсказка и кнопка сброса сверху, чипы тегов под ними.
            var hint = ThemedIconAndText("IconTag", LocalizationManager.T("Main.TagFilterTitle"),
                "TextSecondaryBrush", UiMetrics.ScaledFont(12), centered: false);
            hint.HorizontalAlignment = HorizontalAlignment.Left;

            // Кнопка справки рядом с заголовком панели, как в разметке WPF.
            var hintRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            hintRow.Children.Add(hint);
            hintRow.Children.Add(new Controls.HelpLink
            {
                HelpText = LocalizationManager.T("Main.TagFilterHelp"),
                VerticalAlignment = VerticalAlignment.Center
            });
            hintRow.HorizontalAlignment = HorizontalAlignment.Left;

            var header = new Grid();
            header.Children.Add(hintRow);
            header.Children.Add(_tagClearButton);

            var rows = new StackPanel { Orientation = Orientation.Vertical, Spacing = 6 };
            rows.Children.Add(header);
            rows.Children.Add(_tagPanelItems);

            // Карточка с полем 4,0,4,8, отступом 8,6, рамкой и скруглением 8
            // (MainWindow.xaml:367): у нас это была полоса во всю ширину окна
            // с одной нижней линией.
            _tagPanel = new Border
            {
                Margin = new Thickness(4, 0, 4, 8),
                Padding = new Thickness(8, 6),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Child = rows
            };
            ThemeBrushes.Bind(_tagPanel, Border.BackgroundProperty, "CardBackgroundBrush");
            ThemeBrushes.Bind(_tagPanel, Border.BorderBrushProperty, "BorderColorBrush");
            return _tagPanel;
        }

        /// <summary>Пересобирает кнопки тегов и обновляет видимость панели.</summary>
        private void RefreshTagFilterPanel()
        {
            if (_vm is null || _tagPanelItems is null || _tagPanel is null || _tagClearButton is null)
                return;

            // Старые кнопки держат подписки на ресурсы темы, поэтому освобождаются
            // явно: очистка коллекции детей сама по себе их не отпускает.
            foreach (var child in _tagPanelItems.Children.OfType<IDisposable>().ToList())
                child.Dispose();
            _tagPanelItems.Children.Clear();

            foreach (var tag in _vm.TagFilterItems)
            {
                var item = tag;
                // Теги панели фильтра у автора в скруглённой рамке и мельче
                // сегментов верхней панели: свои отступы, кегль, значок и радиус
                // (MainWindow.xaml:414 и 455).
                var button = new SegmentButton("IconTag", item.Name, "ItemHoverBrush", "ItemSelectedBrush",
                    iconSize: UiMetrics.Scaled(12), cornerRadius: 8, fontSize: UiMetrics.ScaledFont(11))
                {
                    Margin = new Thickness(0, 0, 6, 4),
                    IsChecked = item.IsSelected,
                    MinHeight = 0,
                    Padding = new Thickness(7, 2)
                };
                button.SetBorderThickness(1);
                button.ShowRestingBackground("CardBackgroundBrush");
                button.ShowRestingBorder("BorderColorBrush");
                button.ShowHoverBorder("AccentBrush");
                button.Click += (_, _) => _vm.SearchByTagCommand.Execute(item.Name);
                _tagPanelItems.Children.Add(button);
            }

            _tagClearButton.IsVisible = _vm.HasActiveTagFilter;
            _tagPanel.IsVisible = _vm.ShowTagFilterPanel;
        }

        /// <summary>
        /// Индикатор длительной фоновой работы: полупрозрачная подложка на всё
        /// окно и карточка с подписью и неопределённой полосой прогресса.
        /// Числа из разметки (MainWindow.xaml:2349-2366).
        /// </summary>
        private static Control BuildLoadingOverlay()
        {
            var message = new TextBlock
            {
                FontSize = 14,
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, 0, 0, 16),
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            ThemeBrushes.Bind(message, TextBlock.ForegroundProperty, "TextPrimaryColorBrush");
            message.Bind(TextBlock.TextProperty, new Binding("LoadingMessage"));

            // На программном рендере/в виртуализации индетерминантный индикатор — бесконечная
            // анимация, которая держит рендер-цикл постоянно занятым и даёт ~36% CPU при
            // «зависшем» окне (issue #153). Там показываем статичную заполненную полосу,
            // а сам оверлей не перехватывает мышь: даже если фоновая инициализация затянется,
            // окно останется отзывчивым и не будет жечь CPU.
            var disableAnimations = Services.LinuxRendering.DisableAnimations;
            var bar = new ProgressBar
            {
                IsIndeterminate = !disableAnimations,
                Value = disableAnimations ? 100 : 0,
                Height = 6,
                Background = new SolidColorBrush(Color.Parse("#22000000"))
            };
            ThemeBrushes.Bind(bar, TemplatedControl.ForegroundProperty, "AccentBrush");

            var content = new StackPanel();
            content.Children.Add(message);
            content.Children.Add(bar);

            var card = new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(32, 26),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 320,
                MaxWidth = 520,
                Child = content
            };
            ThemeBrushes.Bind(card, Border.BackgroundProperty, "CardBackgroundBrush");

            // Затемняющий слой поверх всего окна. На программном рендере/в виртуализации
            // полупрозрачный оверлей (альфа #99) требует постоянной альфа-компоновки
            // кадра и на X11 без композитора даёт высокую нагрузку CPU (issue #153),
            // поэтому там затемнение делаем сплошным непрозрачным.
            var dimColor = Services.LinuxRendering.OpaqueWindow
                ? Color.Parse("#FF000000")
                : Color.Parse("#99000000");
            var overlay = new Panel { Background = new SolidColorBrush(dimColor) };
            overlay.Children.Add(card);
            overlay.IsHitTestVisible = !disableAnimations;
            overlay.Bind(Control.IsVisibleProperty, new Binding("IsLoading"));
            return overlay;
        }

        private Control BuildStatusBar()
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _statusInfo = new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            ThemeBrushes.Bind(_statusInfo, TextBlock.ForegroundProperty, "TextOnAccentBrush");
            _statusInfo.Bind(TextBlock.TextProperty, new Binding("StatusBarInfo"));
            // Подсказка показывает строку целиком: в нижней панели она обрезается
            // многоточием. Контекстное меню с копированием строки подключения
            // взято из разметки WPF (MainWindow.xaml:2288-2292).
            _statusInfo.Bind(ToolTip.TipProperty, new Binding("StatusBarInfo"));
            if (_vm is not null)
            {
                var statusMenu = new ContextMenu().Styled(Themes.ControlThemes.ModernContextMenu);
                statusMenu.Items.Add(MenuAction("Main.CopyPath", _vm.CopyConnectionStringCommand, iconKey: "IconCopy"));
                _statusInfo.ContextMenu = statusMenu;
            }
            grid.Children.Add(_statusInfo);
            Grid.SetColumn(_statusInfo, 0);

            _syncMessage = new TextBlock { FontSize = 12, Margin = new Thickness(16, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            ThemeBrushes.Bind(_syncMessage, TextBlock.ForegroundProperty, "TextOnAccentBrush");
            _syncMessage.Bind(TextBlock.TextProperty, new Binding("SyncMessage"));
            ToolTip.SetTip(_syncMessage, LocalizationManager.T("Main.SyncResultTooltip"));
            grid.Children.Add(_syncMessage);
            Grid.SetColumn(_syncMessage, 1);

            var sessionToggleBtn = StatusBarIconButton("IconRecent");
            ToolTip.SetTip(sessionToggleBtn, LocalizationManager.T("Main.CurrentSession"));
            sessionToggleBtn.Bind(Button.CommandProperty, new Binding("ToggleSessionLaunchPanelCommand"));
            grid.Children.Add(sessionToggleBtn);
            Grid.SetColumn(sessionToggleBtn, 2);

            var toggleBtn = StatusBarIconButton("IconPageLayoutSidebarRight");
            // Подсказка меняется вместе с состоянием панели, как в разметке
            // (MainWindow.xaml:2337): раньше здесь стояла постоянная строка.
            toggleBtn.Bind(ToolTip.TipProperty, new Binding("RightPanelToggleTooltip"));
            toggleBtn.Bind(Button.CommandProperty, new Binding("ToggleRightPanelDetailsCommand"));
            grid.Children.Add(toggleBtn);
            Grid.SetColumn(toggleBtn, 3);

            // Фон панели и цвет текста в разметке заданы явно (MainWindow.xaml:2300):
            // тёмная полоса SidebarBrush с контрастным текстом, а не прозрачная
            // область с обычным текстом.
            var bar = new Border { Child = grid, Name = "StatusBarBorder", Padding = new Thickness(12, 6) };
            ThemeBrushes.Bind(bar, Border.BackgroundProperty, "SidebarBrush");
            return bar;
        }

        /// <summary>
        /// Кнопка строки состояния: оформление берёт тема StatusBarIconButton
        /// разметки (LightTheme.xaml:616). Нажатие у автора различается темами:
        /// в светлой это тёмная заливка, в тёмной прозрачность 0.85, и тема
        /// повторяет обе.
        /// </summary>
        private static Button StatusBarIconButton(string iconKey)
        {
            var button = new Button
            {
                Content = IconHelper.MakeIcon(iconKey, 18, "TextOnAccentBrush"),
                Margin = new Thickness(4, 0, 0, 0),
                MinWidth = 0,
                MinHeight = 0,
                VerticalAlignment = VerticalAlignment.Center
            };
            button.Styled(Themes.ControlThemes.StatusBarIconButton);
            return button;
        }

        /// <summary>Сводит состояние переключателей верхней панели с вьюмоделью.</summary>
        private void SyncTopBarToggles()
        {
            if (_vm is null)
                return;
            if (_groupByToggle is not null)
                _groupByToggle.IsChecked = _vm.GroupByGroup;
            if (_emptyGroupsToggle is not null)
                _emptyGroupsToggle.IsChecked = _vm.ShowEmptyGroups;
            if (_compactToggle is not null)
                _compactToggle.IsChecked = _vm.CompactMode;
        }

        // ======================= Обработчики =======================

        private void OnWindowLoaded(object? sender, RoutedEventArgs e)
        {
            // Инициализация выполняется синхронно при загрузке окна. Откладывать её на
            // следующий кадр нельзя: во время неё могут открываться модальные диалоги
            // (импорт/восстановление конфига), и внутри отложенного колбэка их вложенный
            // цикл сообщений приводил к зависанию приложения.
            _vm?.Initialize();
            // Декор главного окна строился в конструкторе по значению по умолчанию:
            // на этом этапе _settings во вьюмодели ещё не загружены (Initialize читает
            // их только сейчас), поэтому UseSystemTitleBar всегда возвращал false, и
            // сохранённая «Системная рамка окна» после перезапуска не применялась
            // (issue #222; у дополнительных окон настройки к моменту их создания уже
            // были загружены, поэтому там всё работало). После загрузки настроек
            // применяем сохранённое значение повторно: если оно отличается от того,
            // что выбрано при построении, обновляем декор и пересобираем содержимое
            // под нужный режим до привязки прокрутки/горячих клавиш. Прозрачность и
            // непрозрачность окна согласуются внутри ApplySystemDecorations через
            // _opaqueWindow, повторное применение их не ломает.
            var savedSystemTitleBar = _vm?.UseSystemTitleBar ?? false;
            if (savedSystemTitleBar != _useSystemTitleBar)
                ApplySystemTitleBar(savedSystemTitleBar);
            // Настройки читаются здесь, уже после построения содержимого, поэтому
            // переключатели верхней панели строились по значениям по умолчанию
            // и не показывали сохранённое состояние до первого щелчка.
            // Initialize присваивает поля напрямую, без уведомлений, так что
            // обработчик изменений вьюмодели их тоже не догонял.
            SyncTopBarToggles();
            RegisterHotkeys();
            // Шаблон дерева готов только после загрузки окна, раньше внутренней
            // прокрутки ещё нет.
            AttachVerticalScrollBar();
            if (_vm is not null)
            {
                // Переназначение клавиш меняет и привязки, и подписи в меню.
                _vm.HotkeysChanged += (_, _) =>
                {
                    RegisterHotkeys();
                    if (_tree is not null)
                        _tree.ContextMenu = BuildRowContextMenu();
                };

            }
            SetupTray();
            // Страховка от «зависшего» оверлея загрузки (issue #153): если фоновая
            // инициализация не завершилась за разумное время — например, на медленном
            // программном рендере/в виртуализации индетерминантный индикатор крутится
            // бесконечно, а блокирующий подложкой оверлей съедает ввод. Таймер сбрасывает
            // IsLoading, и окно возвращается к отзывчивости, даже если инициализация
            // где-то повисла.
            ArmLoadingOverlayWatchdog();
        }

        /// <summary>Максимальное время показа оверлея загрузки перед принудительным скрытием.</summary>
        private static readonly TimeSpan LoadingOverlayMaxDuration = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Запускает одноразовый таймер, который по истечении <see cref="LoadingOverlayMaxDuration"/>
        /// сбрасывает флаг <c>IsLoading</c>, если тот всё ещё взведён. Это последний рубеж:
        /// индикатор не должен оставаться на экране и жечь CPU/блокировать ввод бесконечно.
        /// </summary>
        private void ArmLoadingOverlayWatchdog()
        {
            var timer = new Avalonia.Threading.DispatcherTimer
            {
                Interval = LoadingOverlayMaxDuration
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (_vm is not null && _vm.IsLoading)
                {
                    _vm.IsLoading = false;
                    _vm.LogWarning("Оверлей загрузки скрыт по таймауту (фоновая инициализация не завершилась)");
                }
            };
            timer.Start();
        }

        /// <summary>
        /// Применяет компактный режим интерфейса: пересобирает главное окно с уменьшенными
        /// отступами, иконками и расстояниями. Вызывается из окна настроек при переключении.
        /// </summary>
        public void ApplyCompactMode(bool compact)
        {
            UiMetrics.Compact = compact;
            Content = BuildRoot();
            // После пересборки корня дерево, строки и заголовок — новые объекты, и их события
            // (BoundsProperty/ContainerPrepared) могут не сработать при прежней ширине окна.
            // Ставим выравнивание в очередь явно, чтобы компенсатор заголовка был пересчитан
            // от фактической ширины уже раскладённых строк (issue #214).
            QueueHeaderAlign();
        }

        /// <summary>
        /// Применяет декор главного окна по текущей настройке «Системный заголовок окна»
        /// (issue #159): стандартная системная рамка или собственная безрамковая с кнопками
        /// окна, зонами изменения размера и прозрачностью. Прозрачность не запрашивается,
        /// если окно работает в непрозрачном режиме (<see cref="_opaqueWindow"/>), чтобы не
        /// провоцировать непрерывную перерисовку фона на X11 с программным рендером.
        /// </summary>
        private IDisposable? _backgroundBinding;

        private void ApplySystemDecorations()
        {
            SystemDecorations = _useSystemTitleBar ? SystemDecorations.Full : SystemDecorations.None;

            // На X11 без композитора (или в виртуализации на программном рендере) любое
            // «прозрачное» окно заставляет оконный менеджер непрерывно перерисовывать фон,
            // что проявляется как «зависание» и высокая нагрузка CPU (~36%, issue #153).
            // Поэтому в непрозрачном режиме окно делается простым прямоугольником: без
            // запроса прозрачности и без расширения клиентской области (последнее в
            // безрамковом режиме тоже требует прозрачных полей под скругление/тень).
            // Расширение и прозрачность остаются только для «стекла» на Wayland, где
            // композитор обязателен и постоянной перерисовки фона нет.
            var opaque = _useSystemTitleBar || _opaqueWindow;
            ExtendClientAreaToDecorationsHint = !opaque;

            if (opaque)
            {
                // Убираем запрос уровня прозрачности — по умолчанию окно рисуется
                // непрозрачным прямоугольным фоном. Пустой список эквивалентен null
                // по поведению Avalonia, но не провоцирует CS8625. Сплошной фон задаём
                // явно, чтобы нативное окно гарантированно было непрозрачным.
                TransparencyLevelHint = Array.Empty<WindowTransparencyLevel>();

                // Фон берётся из темы, а не фиксированным тёмным цветом: он виден
                // в углах за скруглением подложки и давал там тёмные клинья в светлой
                // теме. Кисть та же, что у подложки.
                _backgroundBinding?.Dispose();
                _backgroundBinding = ThemeBrushes.Bind(this, TemplatedControl.BackgroundProperty,
                    "ContentBackgroundColorBrush");
            }
            else
            {
                // Прозрачность без размытия: AcrylicBlur/Blur включает непрерывную
                // перерисовку фона и в виртуализации давал ~36% CPU (issue #153).
                TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
                // Без прозрачного фона самого окна прозрачность не активируется:
                // содержимое рисуется поверх, а «стекло» даёт полупрозрачный фон корня.
                // Привязку фона к теме снимаем: иначе следующая смена темы или схемы
                // перезапишет прозрачный фон непрозрачной кистью, потому что простое
                // присваивание живущую привязку не отменяет.
                _backgroundBinding?.Dispose();
                _backgroundBinding = null;
                Background = Brushes.Transparent;
            }
        }

        /// <summary>
        /// Применяет настройку «Системный заголовок окна» на живом главном окне без
        /// перезапуска (issue #159). Значение уже сохранено во вьюмодели (кнопка
        /// «Сохранить» в настройках присваивает <c>UseSystemTitleBar</c> до вызова),
        /// здесь только обновляется декор и пересобирается содержимое под новый режим:
        /// при системной рамке убираются собственная шапка и зоны изменения размера.
        /// Если платформа не позволяет сменить декор живого окна (некоторые X11 WM),
        /// свойство SystemDecorations всё равно перечитывается, а контент приводится
        /// к согласованному виду; эффект гарантируется после следующего показа окна.
        /// </summary>
        public void ApplySystemTitleBar(bool useSystemTitleBar)
        {
            _useSystemTitleBar = useSystemTitleBar;
            ApplySystemDecorations();
            Content = BuildRoot();
        }

        /// <summary>
        /// Пересобирает главное окно при смене языка интерфейса, чтобы названия колонок,
        /// кнопки правой панели и подсказки (создаваемые через <c>LocalizationManager.T(...)</c>)
        /// обновились на новый язык сразу, а не после перезапуска. Компактный режим
        /// (<see cref="UiMetrics.Compact"/>) при этом сохраняется; выделение и прокрутка
        /// списка восстанавливаются после пересборки содержимого.
        /// </summary>
        private void OnLanguageChanged(object? sender, EventArgs e)
        {
            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
                RebuildAfterLanguageChange();
            else
                Avalonia.Threading.Dispatcher.UIThread.Post(RebuildAfterLanguageChange);
        }

        private void RebuildAfterLanguageChange()
        {
            var selected = (object?)_vm?.SelectedInfobase ?? _vm?.SelectedGroupNode;
            var offset = TreeScroll?.Offset;

            Content = BuildRoot();
            Title = ComposeWindowTitle();

            // Обновляем меню и подсказку трея, чтобы подписи кнопок и ToolTip
            // тоже переключились на новый язык без перезапуска.
            if (_trayIcon is not null)
            {
                _trayIcon.Menu = BuildTrayMenu();
                _trayIcon.ToolTipText = LocalizationManager.T("App.Title");
            }

            // Выделение и прокрутка восстанавливаются после того, как новое дерево
            // построено и разложено (иначе строки ещё не существуют).
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (selected is not null && _tree is not null && !ReferenceEquals(_tree.SelectedItem, selected))
                {
                    _tree.SelectionChanged -= OnTreeSelectionChanged;
                    try { _tree.SelectedItem = selected; }
                    finally { _tree.SelectionChanged += OnTreeSelectionChanged; }
                }
                if (offset is { } off && TreeScroll is { } scroll)
                    scroll.Offset = off;
            }, Avalonia.Threading.DispatcherPriority.Background);
        }

        /// <summary>Позиция прокрутки списка, снятая перед пересборкой дерева.</summary>
        private Avalonia.Vector? _treeScrollOffset;

        /// <summary>Внутренняя прокрутка дерева: вертикаль ведёт сам TreeView.</summary>
        private ScrollViewer? TreeScroll =>
            _tree?.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

        /// <summary>
        /// Связывает внешнюю полосу прокрутки с деревом. Полоса стоит отдельным
        /// столбцом, вне горизонтальной прокрутки, поэтому остаётся у правого
        /// края области даже когда колонки шире окна.
        /// </summary>
        private void AttachVerticalScrollBar()
        {
            // Компактный режим пересобирает окно целиком, и дерево с полосой
            // становятся другими объектами. Поэтому сверяемся с самой прокруткой,
            // а не с признаком «уже привязывались»: иначе после переключения
            // полоса остаётся подписанной на выброшенный ScrollViewer.
            if (_listVerticalBar is not { } bar || TreeScroll is not { } scroll)
                return;
            if (ReferenceEquals(_boundTreeScroll, scroll) && ReferenceEquals(_boundScrollBar, bar))
                return;

            // Собственные полосы дерева скрываем не только присоединённым свойством
            // на самом дереве (оно может не дойти до внутреннего ScrollViewer шаблона),
            // но и напрямую на найденной прокрутке. Иначе её вертикальная полоса
            // рисуется у правого края содержимого дерева, а когда колонки шире окна
            // и включается горизонтальная прокрутка, оказывается поверх строк списка,
            // а не у правого края области. Полоса прячется, прокрутка остаётся.
            ScrollViewer.SetVerticalScrollBarVisibility(scroll, ScrollBarVisibility.Hidden);
            ScrollViewer.SetHorizontalScrollBarVisibility(scroll, ScrollBarVisibility.Disabled);

            foreach (var link in _scrollBarLinks)
                link.Dispose();
            _scrollBarLinks.Clear();
            _boundTreeScroll = scroll;
            _boundScrollBar = bar;

            void Sync()
            {
                if (_syncingScrollBar)
                    return;
                _syncingScrollBar = true;
                try
                {
                    var hidden = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
                    bar.Maximum = hidden;
                    bar.ViewportSize = scroll.Viewport.Height;
                    // Шаги берёт сама прокрутка по своему содержимому. Без этого
                    // у отдельной полосы остаются значения RangeBase по умолчанию,
                    // и щелчок по дорожке двигает список на десять точек вместо
                    // страницы, а стрелка на одну точку вместо строки.
                    bar.SmallChange = scroll.SmallChange.Height;
                    bar.LargeChange = scroll.LargeChange.Height;
                    bar.Value = Math.Min(scroll.Offset.Y, hidden);
                }
                finally { _syncingScrollBar = false; }
            }

            _scrollBarLinks.Add(scroll.GetObservable(ScrollViewer.OffsetProperty)
                .Subscribe(new PropertyObserver<Vector>(_ => Sync())));
            _scrollBarLinks.Add(scroll.GetObservable(ScrollViewer.ExtentProperty)
                .Subscribe(new PropertyObserver<Size>(_ => Sync())));
            _scrollBarLinks.Add(scroll.GetObservable(ScrollViewer.ViewportProperty)
                .Subscribe(new PropertyObserver<Size>(_ => Sync())));
            _scrollBarLinks.Add(scroll.GetObservable(ScrollViewer.SmallChangeProperty)
                .Subscribe(new PropertyObserver<Size>(_ => Sync())));
            _scrollBarLinks.Add(scroll.GetObservable(ScrollViewer.LargeChangeProperty)
                .Subscribe(new PropertyObserver<Size>(_ => Sync())));
            Sync();
        }

        /// <summary>Наблюдатель за значением свойства.</summary>
        private sealed class PropertyObserver<T> : IObserver<T>
        {
            private readonly Action<T> _apply;
            public PropertyObserver(Action<T> apply) => _apply = apply;
            public void OnCompleted() { }
            public void OnError(Exception error) { }
            public void OnNext(T value) => _apply(value);
        }

        /// <summary>
        /// Запоминает позицию прокрутки до пересборки: список опустеет, и после
        /// неё прежнюю позицию узнать уже неоткуда.
        /// </summary>
        private void RememberTreeScroll()
            // Значение перезаписывается каждой пересборкой и не обнуляется после
            // применения: две пересборки подряд тогда восстановят одну и ту же
            // позицию, а не потеряют её из-за уже отработавшего вызова.
            => _treeScrollOffset = TreeScroll?.Offset ?? _treeScrollOffset;

        /// <summary>
        /// Возвращает выделение строки после пересборки дерева: узлы групп
        /// пересоздаются, и дерево теряет подсветку вместе с ними. Объекты баз
        /// при этом те же самые, поэтому выбранная база ищется по ссылке.
        /// Установка откладывается ниже компоновки, чтобы попасть после
        /// перестроения строк, а цель читается в момент выполнения: вызывающие
        /// меняют выбор уже после возврата из пересборки (создание, регистрация
        /// и удаление базы), и снятая заранее цель подсветила бы чужую строку.
        /// </summary>
        private void RestoreTreeSelection()
        {
            if (_vm is null)
                return;

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (_vm is null)
                    return;

                var target = (object?)_vm.SelectedInfobase ?? _vm.SelectedGroupNode;
                if (target is not null && !ReferenceEquals(_tree.SelectedItem, target))
                {
                    // Выбор ставится напрямую, без обработчика: он уже согласован
                    // с вьюмоделью, и повторный проход только сбросил бы парное поле.
                    _tree.SelectionChanged -= OnTreeSelectionChanged;
                    try { _tree.SelectedItem = target; }
                    finally { _tree.SelectionChanged += OnTreeSelectionChanged; }
                }

                // Прокрутка возвращается последней: у дерева включено
                // AutoScrollToSelectedItem, и установка выбора синхронно тянет
                // строку в видимую область, затирая прежнюю позицию.
                if (_treeScrollOffset is { } offset && TreeScroll is { } scroll)
                    scroll.Offset = offset;

                // Вернуть клавиатурный фокус строке после закрытия модального
                // диалога (например, сохранения настроек базы): контейнер прежней
                // строки уничтожен пересборкой, и фокус осел на окне. Если сейчас
                // идёт ввод в текстовом поле (поиск, теги), фокус не трогаем,
                // чтобы не выбивать курсор из поля во время набора.
                if (target is not null
                    && FocusManager?.GetFocusedElement() is not TextBox
                    && _tree.ContainerForItem(target) is { } row)
                {
                    row.Focus();
                }
            }, Avalonia.Threading.DispatcherPriority.Background);
        }

        private void OnTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (_vm is null)
                return;
            var selected = _tree.SelectedItem;
            switch (selected)
            {
                case Infobase ib:
                    _vm.SelectedInfobase = ib;
                    _vm.SelectedGroupNode = null;
                    break;
                case GroupNodeViewModel g:
                    _vm.SelectedGroupNode = g;
                    _vm.SelectedInfobase = null;
                    break;
                default:
                    break;
            }
        }

        /// <summary>
        /// Контекстное меню строки базы: те же действия, что и в WPF-версии,
        /// кроме тех, чьих команд в Avalonia-вьюмодели пока нет (регистрация
        /// COM-коннектора на Linux неприменима, выгрузка в dt и cf и история
        /// запусков ждут порта сервисов запуска).
        /// </summary>
        private ContextMenu BuildRowContextMenu()
        {
            var menu = new ContextMenu().Styled(Themes.ControlThemes.ModernContextMenu);
            if (_vm is null)
                return menu;

            var cacheMenu = new MenuItem
            {
                Header = LocalizationManager.T("Main.ClearCache"),
                Icon = MenuIcon("IconBroom", "#14B8A6")
            };
            cacheMenu.Styled(Themes.ControlThemes.ModernMenuItem);
            cacheMenu.Items.Add(MenuAction("Main.ClearProgramCache", _vm.ClearProgramCacheCommand));
            cacheMenu.Items.Add(MenuAction("Main.ClearUserCache", _vm.ClearUserCacheCommand));
            cacheMenu.Items.Add(new Separator());
            // Сочетание показано здесь, а не у программного кеша: Ctrl+Shift+C
            // открывает очистку обоих кешей. В WPF подпись стоит у программного,
            // хотя клавиша делает то же самое, что этот пункт.
            cacheMenu.Items.Add(MenuAction("Main.ClearCacheBoth", _vm.ClearCacheBothCommand, _vm.HotkeyClearCache));

            menu.Items.Add(MenuAction("Main.LaunchEnterprise", _vm.LaunchEnterpriseCommand, _vm.HotkeyEnterprise, "IconPlay", "#22C55E"));
            menu.Items.Add(MenuAction("Main.LaunchConfigurator", _vm.LaunchConfiguratorCommand, _vm.HotkeyConfigurator, "IconSettings", "#3B82F6"));
            menu.Items.Add(MenuAction("Main.EditSettings", _vm.EditInfobaseCommand, _vm.HotkeyEdit, "IconEdit", "#3B82F6"));
            menu.Items.Add(MenuAction("Main.RefreshConfigInfo", _vm.RefreshConfigurationInfoCommand, null, "IconCloudDownload", "#14B8A6"));
            // «Зарегистрировать COM-коннектор» здесь нет намеренно: внешнее соединение
            // это COM, в Linux регистрировать нечего. Windows-сторона решение подтвердила.
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuAction("Main.ToFavorites", _vm.ToggleFavoriteCommand, _vm.HotkeyFavorite, "IconStar", "#FBBF24"));
            menu.Items.Add(MenuAction("Main.Pin", _vm.TogglePinCommand, _vm.HotkeyPin, "IconPin", "#8B5CF6"));
            menu.Items.Add(cacheMenu);
            menu.Items.Add(MenuAction("Main.CopyConnectionString", _vm.CopyConnectionStringCommand, null, "IconCopy", "#06B6D4"));
            menu.Items.Add(MenuAction("Main.OpenCatalog", _vm.OpenInfobaseFolderCommand, null, "IconFolderOpen", "#0EA5E9"));
            menu.Items.Add(MenuAction("Main.DesktopShortcut", _vm.CreateDesktopShortcutCommand, null, "IconDesktopClassic", "#6366F1"));
            menu.Items.Add(MenuAction("Main.AddBase", _vm.AddInfobaseCommand, _vm.HotkeyAdd, "IconAdd", "#22C55E"));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuAction("Main.DumpToDt", _vm.DumpInfobaseDtCommand, null, "IconDatabaseExport", "#0EA5E9"));
            menu.Items.Add(MenuAction("Main.DumpConfigToCf", _vm.DumpConfigurationCfCommand, null, "IconFileExport", "#3B82F6"));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuAction("Main.Delete", _vm.DeleteInfobaseCommand, _vm.HotkeyDelete, "IconDelete", "#EF4444"));
            return menu;
        }

        /// <summary>
        /// Значок пункта меню. Цвет задаётся явно, а не ресурсом темы: в разметке
        /// WPF у каждого пункта свой цвет, и он один и тот же в светлой и тёмной.
        /// </summary>
        private static Control MenuIcon(string iconKey, string colorHex)
            => IconHelper.MakeIcon(iconKey, 16, new SolidColorBrush(Color.Parse(colorHex)));

        /// <summary>Пункт меню с подписью из словаря, командой и подсказкой сочетания клавиш.</summary>
        private static MenuItem MenuAction(string textKey, System.Windows.Input.ICommand command, string? gesture = null,
            string? iconKey = null, string? iconColor = null)
        {
            var item = new MenuItem
            {
                Header = LocalizationManager.T(textKey),
                Command = command
            };
            item.Styled(Themes.ControlThemes.ModernMenuItem);
            if (iconKey is not null && iconColor is not null)
                item.Icon = MenuIcon(iconKey, iconColor);
            if (Controls.HotkeyBox.TryParse(gesture, out var parsed) && parsed is not null)
                item.InputGesture = parsed;
            return item;
        }

        /// <summary>
        /// Горячие клавиши действий. Сочетания берутся из вьюмодели, оттуда же
        /// их показывают подсказки и контекстное меню, поэтому список и подписи
        /// не расходятся.
        /// Важно про порядок: в Avalonia привязки окна проверяются раньше, чем
        /// клавишу получит элемент с фокусом, в отличие от WPF. Ни одно из этих
        /// сочетаний не совпадает с правкой текста, поэтому ввод в поле поиска
        /// они не задевают, но добавлять сюда Ctrl+C, Ctrl+V и подобное нельзя:
        /// они отберут клавишу у поля ввода. Delete по этой же причине живёт
        /// в отдельном обработчике с проверкой фокуса, а не здесь.
        /// </summary>
        private void RegisterHotkeys()
        {
            if (_vm is null)
                return;

            KeyBindings.Clear();

            // Alt+1…Alt+9 запускают избранные базы по порядку слотов и ставятся
            // ПЕРЕД пользовательскими: Avalonia перебирает привязки по порядку
            // списка и останавливается на первой подошедшей, поэтому иначе
            // назначенный пользователем Alt+1 перебивал бы избранное. Версия
            // для Windows добивается того же с другого конца: там
            // RegisterFavoriteHotkeys сперва удаляет из InputBindings все
            // Alt+1…9, включая пользовательские (MainWindow.Hotkeys.cs:118).
            // Незанятый слот привязку всё равно имеет и клавишу поглощает,
            // но действия не выполняет: так же ведёт себя и версия для Windows.
            for (var number = 1; number <= 9; number++)
            {
                var slot = number;
                KeyBindings.Add(new KeyBinding
                {
                    Gesture = new KeyGesture((Key)((int)Key.D0 + slot), KeyModifiers.Alt),
                    Command = new ViewModels.RelayCommand(_ => _vm.LaunchFavoriteByHotkey(slot))
                });
            }

            // Delete в привязки не идёт: он правит текст, и в поле ввода
            // не должен удалять базу. Ему отдельный обработчик ниже.
            AddHotkey(_vm.HotkeyEnterprise, _vm.LaunchEnterpriseCommand);
            AddHotkey(_vm.HotkeyConfigurator, _vm.LaunchConfiguratorCommand);
            AddHotkey(_vm.HotkeyEdit, _vm.EditInfobaseCommand);
            AddHotkey(_vm.HotkeyAdd, _vm.AddInfobaseCommand);
            AddHotkey(_vm.HotkeyFavorite, _vm.ToggleFavoriteCommand);
            AddHotkey(_vm.HotkeyPin, _vm.TogglePinCommand);
            AddHotkey(_vm.HotkeyClearCache, _vm.ClearCacheCommand);
            // Переключение режимов списка баз: Все, Избранное, Недавние.
            AddHotkey(_vm.HotkeyShowAll, _vm.ShowAllCommand);
            AddHotkey(_vm.HotkeyShowFavorites, _vm.ShowFavoritesCommand);
            AddHotkey(_vm.HotkeyShowRecent, _vm.ShowRecentCommand);

            // Очистка строки поиска и сброс фильтра тегов — настраиваемые хоткеи (issue #160),
            // значения по умолчанию Ctrl+Shift+C / Ctrl+Shift+T задаются в настройках.
            // Добавляются ПОСЛЕ пользовательских, чтобы назначенные пользователем
            // сочетания имели приоритет.
            AddHotkey(_vm.HotkeyClearSearch, _vm.ClearSearchCommand);
            AddHotkey(_vm.HotkeyClearTags, _vm.ClearTagFiltersCommand);
            // Переключение подробностей правой панели информации — настраиваемый хоткей (issue #172);
            // значение по умолчанию Ctrl+D задаётся в настройках.
            AddHotkey(_vm.HotkeyRightPanelDetails, _vm.ToggleRightPanelDetailsCommand);
            // Смена пользователя — настраиваемый хоткей (issue #200).
            AddHotkey(_vm.HotkeySwitchUser, _vm.SwitchUserCommand);
            // Ctrl+Shift+Plus / Ctrl+Shift+Minus — развернуть/свернуть все узлы дерева.
            // Регистрируются обе раскладки (основная клавиатура Oem* и цифровой блок Add/Subtract).
            KeyBindings.Add(new KeyBinding
            {
                Gesture = new KeyGesture(Key.OemPlus, KeyModifiers.Control | KeyModifiers.Shift),
                Command = _vm.ExpandAllGroupsCommand
            });
            KeyBindings.Add(new KeyBinding
            {
                Gesture = new KeyGesture(Key.Add, KeyModifiers.Control | KeyModifiers.Shift),
                Command = _vm.ExpandAllGroupsCommand
            });
            KeyBindings.Add(new KeyBinding
            {
                Gesture = new KeyGesture(Key.OemMinus, KeyModifiers.Control | KeyModifiers.Shift),
                Command = _vm.CollapseAllGroupsCommand
            });
            KeyBindings.Add(new KeyBinding
            {
                Gesture = new KeyGesture(Key.Subtract, KeyModifiers.Control | KeyModifiers.Shift),
                Command = _vm.CollapseAllGroupsCommand
            });
        }

        /// <summary>
        /// Удаление базы по назначенному сочетанию. В общие привязки оно
        /// не идёт, потому что по умолчанию это Delete: клавиша текстовая,
        /// и в поле ввода она должна править текст, а не удалять базу.
        /// </summary>
        private void OnWindowKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Handled || _vm is null)
                return;

            // Ctrl+Shift++ / Ctrl+Shift+- — «развернуть все» / «свернуть все» (issue #160).
            // Дублируем назначенные в RegisterHotkeys KeyBindings надёжным явным разбором:
            // KeyBinding/KeyGesture на части раскладок и при разном состоянии фокуса
            // срабатывают только со второго нажатия. Прямой вызов тех же команд, что и у
            // кнопок верхней панели, делает хоткей детерминированным с первого нажатия.
            // Если привязка уже обработала жест (e.Handled == true), сюда не доходим —
            // повторного срабатывания нет.
            if ((e.KeyModifiers & KeyModifiers.Control) != 0 &&
                (e.KeyModifiers & KeyModifiers.Shift) != 0)
            {
                if (e.Key is Key.OemPlus or Key.Add)
                {
                    _vm.ExpandAllGroupsCommand.Execute(null);
                    e.Handled = true;
                    return;
                }

                if (e.Key is Key.OemMinus or Key.Subtract)
                {
                    _vm.CollapseAllGroupsCommand.Execute(null);
                    e.Handled = true;
                    return;
                }
            }

            // Esc при открытом диалоге закрывает сам диалог. Пока пользователь не
            // кликнул внутри диалога, событие приходит именно сюда: сфокусированной
            // остаётся кнопка главного окна, которой диалог и открыли, а клавиатурное
            // событие Avalonia ведёт вверх по дереву от сфокусированного элемента и
            // маршрута диалога не задевает вовсе (issue #226). После клика внутри
            // маршрут идёт через диалог, и Esc обрабатывает его собственный OnKeyDown.
            // Фокус в диалог не переносим: первым элементом обхода в безрамочном окне
            // оказывается кнопка «Свернуть» собственной полосы заголовка, и рамка
            // фокуса вставала бы на неё.
            if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None
                && TopmostModalDialog() is { } dialog)
            {
                dialog.CloseAsCancel();
                e.Handled = true;
                return;
            }

            // Esc уводит окно в трей, если так задано настройкой. В поле ввода
            // клавиша остаётся своей: там ей отменяют правку.
            if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None
                && _vm.EscapeToTray && _vm.ShowTrayIcon && CanRestoreHiddenWindow
                && FocusManager?.GetFocusedElement() is not TextBox
                // При открытом модальном диалоге (свойства базы, настройки) Esc
                // должен закрывать только сам диалог, а не уводить главное окно
                // в трей (issue #226).
                && !HasOpenModalDialog())
            {
                SaveWindowLayout();
                _vm.PersistSettings();
                ApplyTrayVisibility();
                Hide();
                e.Handled = true;
                return;
            }
            if (!Controls.HotkeyBox.TryParse(_vm.HotkeyDelete, out var gesture) || gesture is null)
                return;
            if (e.Key != gesture.Key || e.KeyModifiers != gesture.KeyModifiers)
                return;

            // Только для текстовых клавиш без модификаторов: назначенное F8
            // должно работать и в поле ввода, как любая другая горячая клавиша.
            var isTextEditingKey = gesture.KeyModifiers == KeyModifiers.None
                && gesture.Key is Key.Delete or Key.Back or Key.Insert;
            if (isTextEditingKey && FocusManager?.GetFocusedElement() is TextBox)
                return;

            if (_vm.DeleteInfobaseCommand.CanExecute(null))
                _vm.DeleteInfobaseCommand.Execute(null);
            e.Handled = true;
        }

        /// <summary>
        /// Верхнее по Z-порядку открытое окно, если это диалог, унаследованный от
        /// <see cref="ModalWindowBase"/>, иначе <c>null</c>. Порядок берём у платформы
        /// (<see cref="Window.SortWindowsByZOrder"/>), а не порядок открытия: у окон
        /// без отношения владения он последнему открытому не равен. Если сверху лежит
        /// окно другого рода (сообщение, ход обновления), метод возвращает <c>null</c>:
        /// закрывать вместо него диалог под ним нельзя.
        /// </summary>
        private ModalWindowBase? TopmostModalDialog()
        {
            if (Avalonia.Application.Current?.ApplicationLifetime
                    is not Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
                return null;

            var visible = new List<Window>();
            foreach (var window in desktop.Windows)
            {
                if (!ReferenceEquals(window, this) && window.IsVisible)
                    visible.Add(window);
            }

            if (visible.Count == 0)
                return null;

            var ordered = visible.ToArray();
            Window.SortWindowsByZOrder(ordered);
            return ordered[ordered.Length - 1] as ModalWindowBase;
        }

        /// <summary>
        /// Есть ли открытый модальный дочерний диалог (свойства базы, настройки и т.п.).
        /// Все дополнительные окна в приложении показываются модально (ShowDialog/
        /// ShowDialogSync). Проверяем флаг IsVisible, а не IsActive: на Linux/X11 окно
        /// после открытия не всегда сразу получает активацию (issue #226), и по одному
        /// лишь IsActive мы бы не распознали открытый диалог — тогда Esc уводил бы главное
        /// окно в трей, не закрыв диалог. Если видимо любое окно, кроме главного, Esc
        /// должен обработать сам диалог (см. ModalWindowBase.OnKeyDown), а не главное окно.
        /// Закрытые окна в списке имеют IsVisible == false и на результат не влияют.
        /// </summary>
        private bool HasOpenModalDialog()
        {
            if (Avalonia.Application.Current?.ApplicationLifetime
                    is not Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
                return false;

            foreach (var window in desktop.Windows)
            {
                if (!ReferenceEquals(window, this) && window.IsVisible)
                    return true;
            }

            return false;
        }

        private void AddHotkey(string? gesture, System.Windows.Input.ICommand? command)
        {
            if (command is null || !Controls.HotkeyBox.TryParse(gesture, out var parsed) || parsed is null)
                return;
            KeyBindings.Add(new KeyBinding { Gesture = parsed, Command = command });
        }

        // ======================= Трей =======================

        /// <summary>
        /// Строит меню трея с актуальными переводами. Вызывается при создании
        /// значка и при смене языка, чтобы подписи и подсказки обновились.
        /// </summary>
        private NativeMenu BuildTrayMenu()
        {
            var menu = new NativeMenu();
            // Состав меню зависит от данных: недавние базы и выбранная база
            // меняются в работе. Штатный запрос обновления перед показом
            // (NeedsUpdate) на Linux не приходит: его поднимает только
            // бэкенд macOS, в Avalonia.FreeDesktop вызова нет, проверено
            // и прогоном, и разбором сборок. Поэтому меню пересобирается
            // по изменению самих данных, а подписка оставлена на случай,
            // если событие появится.
            menu.NeedsUpdate += (_, _) => FillTrayMenu(menu);
            FillTrayMenu(menu);
            _trayMenu = menu;
            _traySignature = TrayMenuSignature();
            return menu;
        }

        /// <summary>Снимает обработчики вьюмодели, навешенные прошлой сборкой окна.</summary>
        private void DetachViewModelHandlers()
        {
            if (_vm is null)
                return;
            if (_groupNodesChanged is not null)
                _vm.GroupNodes.CollectionChanged -= _groupNodesChanged;
            if (_flatItemsChanged is not null)
                _vm.FlatItems.CollectionChanged -= _flatItemsChanged;
            if (_tagFiltersRebuilt is not null)
                _vm.TagFiltersRebuilt -= _tagFiltersRebuilt;
            if (_vmPropertyChanged is not null)
                _vm.PropertyChanged -= _vmPropertyChanged;
        }

        /// <summary>
        /// Ставит пересборку меню трея в очередь диспетчера. Одно действие даёт
        /// несколько уведомлений подряд (выбор, список, запуск), а пересборка
        /// заново экспортирует меню по DBus, поэтому вызовы склеиваются, как
        /// это уже делают QueueHeaderAlign и QueueColumnHeaderRefresh.
        /// </summary>
        private void QueueTrayMenuRefresh()
        {
            if (_trayRefreshQueued || _trayMenu is null || _vm is null)
                return;

            _trayRefreshQueued = true;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _trayRefreshQueued = false;
                RefreshTrayMenu();
            }, Avalonia.Threading.DispatcherPriority.Background);
        }

        /// <summary>
        /// Пересобирает меню трея, если его состав действительно изменился:
        /// уведомления приходят и на поиск, и на фильтр, поэтому сверяется
        /// отпечаток состава.
        /// </summary>
        private void RefreshTrayMenu()
        {
            if (_trayMenu is null || _vm is null)
                return;

            var signature = TrayMenuSignature();
            if (signature == _traySignature)
                return;

            _traySignature = signature;
            FillTrayMenu(_trayMenu);
        }

        /// <summary>Состав меню трея строкой: выбранная база и недавние.</summary>
        private string TrayMenuSignature()
        {
            if (_vm is null)
                return string.Empty;

            var builder = new System.Text.StringBuilder();
            builder.Append(_vm.SelectedInfobase?.Id).Append('|').Append(_vm.SelectedInfobase?.Name);
            foreach (var ib in _vm.RecentInfobases)
                builder.Append('|').Append(ib.Id).Append('~').Append(ib.Name);
            return builder.ToString();
        }

        /// <summary>Наполняет меню трея заново по текущему состоянию списка баз.</summary>
        private void FillTrayMenu(NativeMenu menu)
        {
            menu.Items.Clear();

            // Состав и порядок как в Windows-версии (MainWindow.Tray.cs:209):
            // открыть, недавние базы (или выбранная, если недавних нет),
            // синхронизация, настройки, выход. У каждой базы своё подменю
            // «Предприятие / Конфигуратор»: раньше пункт запускал только
            // Предприятие, а выбора не было.
            var showItem = new NativeMenuItem(LocalizationManager.T("Main.TrayOpen"));
            showItem.Click += (_, _) => ShowAndActivate();
            menu.Add(showItem);

            var recent = _vm?.RecentInfobases;
            if (recent is { Count: > 0 })
            {
                menu.Add(new NativeMenuItemSeparator());
                menu.Add(TrayHeader(LocalizationManager.T("Main.RecentBases")));
                foreach (var ib in recent)
                    menu.Add(TrayInfobaseItem(ib, TrayItemName(ib.Name, "Main.NoName")));
            }
            else if (_vm?.SelectedInfobase is { } sel)
            {
                menu.Add(new NativeMenuItemSeparator());
                menu.Add(TrayHeader(LocalizationManager.T("Main.SelectedBase")));
                // У выбранной базы сам пункт ничего не запускает, только раскрывает
                // подменю, и её имя не обрезается: так у автора
                // (MainWindow.Tray.cs:240).
                var selName = string.IsNullOrWhiteSpace(sel.Name)
                    ? LocalizationManager.T("Main.SelectedBaseNoName")
                    : sel.Name;
                menu.Add(TrayInfobaseItem(sel, selName, launchOnClick: false));
            }

            menu.Add(new NativeMenuItemSeparator());

            var sync = new NativeMenuItem(LocalizationManager.T("Main.SyncWithIbases"));
            sync.Click += (_, _) => _vm?.SynchronizeWithIbasesCommand.Execute(null);
            menu.Add(sync);

            var settings = new NativeMenuItem(LocalizationManager.T("Main.Settings"));
            settings.Click += (_, _) => _vm?.OpenSettingsCommand.Execute(null);
            menu.Add(settings);
            menu.Add(new NativeMenuItemSeparator());

            // Выход: разрешаем реальное закрытие и завершаем приложение.
            var exitItem = new NativeMenuItem(LocalizationManager.T("Main.Exit"));
            exitItem.Click += (_, _) =>
            {
                _allowCloseToTray = false;
                _vm?.ExitCommand.Execute(null);
            };
            menu.Add(exitItem);
        }

        /// <summary>
        /// Заголовок раздела в меню трея. В Windows это отдельный нерабочий
        /// пункт (MainWindow.Tray.cs:216), здесь он же, но недоступный:
        /// собственного вида у заголовка в системном меню нет.
        /// </summary>
        private static NativeMenuItem TrayHeader(string text) => new(text) { IsEnabled = false };

        /// <summary>
        /// Подпись базы в меню трея: пустое имя заменяется на подпись автора,
        /// длинное обрезается до 48 знаков, как в Windows-версии
        /// (MainWindow.Tray.cs:224).
        /// </summary>
        private static string TrayItemName(string? name, string emptyKey)
        {
            if (string.IsNullOrWhiteSpace(name))
                return LocalizationManager.T(emptyKey);
            return name.Length > 48 ? name.Substring(0, 45) + "…" : name;
        }

        /// <summary>
        /// Пункт базы с подменю выбора режима запуска: сам пункт запускает
        /// Предприятие, подменю даёт Предприятие и Конфигуратор
        /// (MainWindow.Tray.cs:271).
        /// </summary>
        private NativeMenuItem TrayInfobaseItem(Infobase ib, string title, bool launchOnClick = true)
        {
            var baseRef = ib;
            var item = new NativeMenuItem(title);
            if (launchOnClick)
                item.Click += (_, _) => LaunchInfobase(baseRef, configurator: false);

            var submenu = new NativeMenu();
            var enterprise = new NativeMenuItem(LocalizationManager.T("Main.Enterprise"));
            enterprise.Click += (_, _) => LaunchInfobase(baseRef, configurator: false);
            submenu.Add(enterprise);

            var configurator = new NativeMenuItem(LocalizationManager.T("Main.SectionConfigurator"));
            configurator.Click += (_, _) => LaunchInfobase(baseRef, configurator: true);
            submenu.Add(configurator);

            item.Menu = submenu;
            return item;
        }

        private void SetupTray()
        {
            try
            {
                var menu = BuildTrayMenu();

                var tray = new TrayIcon
                {
                    Icon = LoadTrayIcon(),
                    ToolTipText = LocalizationManager.T("App.Title"),
                    Menu = menu
                };
                // Одиночный клик левой кнопкой по иконке трея показывает и
                // фокусирует главное окно, как в WPF-версии (issue #224).
                // Правый клик открывает меню (Menu выше), левый — нет.
                tray.Clicked += (_, _) => ShowAndActivate();
                _trayIcon = tray;
                if (Application.Current is { } app)
                {
                    TrayIcon.SetIcons(app, new TrayIcons { tray });
                    _trayIconCreated = true;
                }

                ApplyTrayVisibility();

                // На GNOME Shell без расширения AppIndicator иконка трея не появится,
                // и приложение об этом никак не узнает: ошибки не будет, значка просто
                // не будет. Пишем в журнал, чтобы это не выглядело поломкой приложения.
                if (Services.LinuxDesktopEnvironment.TrayMayBeUnavailable)
                {
                    AppServices.GetRequiredService<Services.IAppLogger>().Warn(
                        $"Окружение {Services.LinuxDesktopEnvironment.Describe()}: " +
                        "иконка в трее может не отображаться без расширения AppIndicator.");
                }
            }
            catch
            {
                // Трей не обязателен для работы окна; игнорируем ошибки инициализации.
                // Примечание: на GNOME Shell без AppIndicator трей Avalonia может не отображаться —
                // это ограничение DE, окно продолжает работать обычным образом.
            }
        }

        /// <summary>Запускает базу из меню трея (Предприятие).</summary>
        private void LaunchInfobase(Infobase ib, bool configurator = false)
        {
            if (_vm is null)
                return;
            // Пункт меню держит ссылку на базу, а список мог смениться
            // импортом или удалением: запускать исчезнувшую нельзя.
            if (!_vm.Infobases.Contains(ib))
            {
                QueueTrayMenuRefresh();
                return;
            }
            _vm.LaunchFromTray(ib, configurator);
        }

        /// <summary>
        /// Загружает иконку трея — тот же значок приложения (app.ico), что и у
        /// заголовка окна. Без System.Drawing, через Avalonia WindowIcon.
        /// </summary>
        private static WindowIcon? LoadTrayIcon() => Services.AppIconLoader.LoadAppIcon();

        /// <summary>
        /// Восстанавливает сохранённые размер, позицию и состояние окна.
        /// Настройки читаются здесь из репозитория, а не из модели: она
        /// загружает их только в Initialize по событию Loaded, то есть уже
        /// после того, как окно построено и показано.
        /// </summary>
        private void ApplySavedWindowLayout()
        {
            Models.AppSettings settings;
            try
            {
                settings = AppServices.GetRequiredService<Services.IInfobaseRepository>().LoadSettings();
            }
            catch
            {
                // Настройки недоступны: окно открывается по центру, как раньше.
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
                return;
            }

            if (!settings.RememberWindowLayout)
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
                return;
            }

            if (settings.WindowWidth > 0 && settings.WindowHeight > 0)
            {
                Width = settings.WindowWidth;
                Height = settings.WindowHeight;
            }

            var left = settings.WindowLeft;
            var top = settings.WindowTop;
            if (left == 0 && top == 0)
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
            else
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Position = ClampToScreen(new PixelPoint((int)Math.Round(left), (int)Math.Round(top)));
            }

            // IsDefined обязателен: TryParse принимает и числовую строку, даже
            // когда числа нет среди членов перечисления, и «999» дошло бы
            // до окна. Тот же класс ошибки уже стрелял на разборе клавиш.
            if (Enum.TryParse<WindowState>(settings.WindowState, out var state)
                && Enum.IsDefined(state)
                && state != WindowState.Minimized)
            {
                WindowState = state;
            }
        }

        /// <summary>
        /// Уточняет прижатие к экрану после показа окна. В конструкторе размер
        /// рамки ещё не известен (FrameSize равен null), поэтому там прижатие
        /// считается по содержимому и оставляет за краем высоту заголовка.
        /// </summary>
        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            if (WindowStartupLocation == WindowStartupLocation.Manual
                && WindowState == WindowState.Normal)
            {
                Position = ClampToScreen(Position);
            }
        }

        /// <summary>
        /// Прижимает позицию к рабочей области монитора, на котором окно закрыли,
        /// чтобы оно не оказалось за границей экрана после смены конфигурации мониторов.
        /// </summary>
        private PixelPoint ClampToScreen(PixelPoint point)
        {
            Screen? screen;
            try
            {
                // Точка вне всех экранов (монитор отключили) даёт null, а не
                // исключение: тогда берётся основной, как это делает WPF-версия.
                screen = Screens.ScreenFromPoint(point) ?? Screens.Primary;
            }
            catch
            {
                // Сведений об экранах может не быть: на Wayland их отдаёт
                // не всякий сервер. Тогда позиция остаётся как сохранена.
                return point;
            }

            if (screen is null)
                return point;

            var area = screen.WorkingArea;
            var scaling = screen.Scaling > 0 ? screen.Scaling : 1.0;
            // Position это угол рамки, а Width и Height задают клиентскую часть,
            // поэтому размер берётся вместе с тем, что рисует менеджер окон.
            // До показа окна рамка ещё не известна, и прижатие уточняется
            // в OnOpened, когда FrameSize уже есть.
            var frame = FrameSize ?? new Size(Width, Height);
            var width = Math.Min((int)Math.Round(Math.Max(frame.Width, Width) * scaling), area.Width);
            var height = Math.Min((int)Math.Round(Math.Max(frame.Height, Height) * scaling), area.Height);
            return new PixelPoint(
                Math.Max(area.X, Math.Min(point.X, area.Right - width)),
                Math.Max(area.Y, Math.Min(point.Y, area.Bottom - height)));
        }

        /// <summary>
        /// Сохраняет размер, позицию и состояние окна. Позиция пишется в физических
        /// пикселях: в Avalonia Position задан в них, в отличие от Left и Top в WPF.
        /// </summary>
        private void SaveWindowLayout()
        {
            if (_vm is null)
                return;

            if (!_vm.RememberWindowLayout)
            {
                // Настройка выключена: сохранённый макет сбрасывается, иначе
                // следующий запуск открыл бы окно в старом месте и размере.
                if (_vm.SavedWindowWidth != 0 || _vm.SavedWindowHeight != 0
                    || _vm.SavedWindowLeft != 0 || _vm.SavedWindowTop != 0
                    || !string.IsNullOrEmpty(_vm.SavedWindowState))
                {
                    _vm.SaveWindowLayout(0, 0, 0, 0, string.Empty);
                }
                return;
            }

            // У спрятанного окна X11 отдаёт Position со сдвигом на высоту
            // заголовка, и каждый уход в трей с последующим выходом сдвигал бы
            // окно вниз. Геометрия такого окна уже записана перед Hide.
            if (!IsVisible)
                return;

            if (WindowState == WindowState.Normal)
            {
                RememberNormalBounds();
            }
            else if (WindowState == WindowState.Minimized)
            {
                // Свёрнутое окно ничего не говорит о своей геометрии.
                return;
            }

            // Развёрнутое окно сохраняется своим состоянием, но размером
            // и положением обычного: иначе следующий запуск взял бы размер
            // во весь экран как обычный. Так же устроена версия для Windows
            // (MainWindow.Events.cs, ветка Maximized и RestoreBounds).
            if (_normalBounds is not { } bounds)
                return;

            // Ничего не изменилось: лишняя запись настроек при закрытии не нужна.
            var state = WindowState.ToString();
            if (bounds.Width == _vm.SavedWindowWidth && bounds.Height == _vm.SavedWindowHeight
                && bounds.Position.X == _vm.SavedWindowLeft && bounds.Position.Y == _vm.SavedWindowTop
                && string.Equals(state, _vm.SavedWindowState, StringComparison.Ordinal))
            {
                return;
            }

            _vm.SaveWindowLayout(bounds.Width, bounds.Height,
                bounds.Position.X, bounds.Position.Y, state);
        }

        /// <summary>Геометрия окна в обычном состоянии, чтобы развёрнутое
        /// сохранялось размером, к которому оно вернётся.</summary>
        private (double Width, double Height, PixelPoint Position)? _normalBounds;

        /// <summary>Запоминает геометрию, пока окно в обычном состоянии.</summary>
        private void RememberNormalBounds()
        {
            if (WindowState == WindowState.Normal && IsVisible)
                _normalBounds = (ClientSize.Width, ClientSize.Height, Position);
        }

        /// <summary>
        /// Закрытие окна уводит приложение в трей, а не завершает его
        /// (свойство «закрытие в трей»). Реальный выход — команда «Выход».
        /// Перед уходом в трей сохраняем настройки (в т.ч. язык интерфейса),
        /// чтобы выбранный язык не терялся при последующем полном выходе.
        /// </summary>
        protected override void OnClosing(WindowClosingEventArgs e)
        {
            base.OnClosing(e);
            SaveWindowLayout();
            if (_allowCloseToTray && _vm is { CloseToTray: true } && CanRestoreHiddenWindow
                && e.CloseReason == WindowCloseReason.WindowClosing)
            {
                _vm.PersistSettings();
                ApplyTrayVisibility();
                e.Cancel = true;
                Hide();
            }
        }

        /// <summary>
        /// Показывает или прячет значок по настройкам. Значок нужен и когда сам
        /// он выключен, но закрытие уводит окно в трей: иначе окно нечем вернуть.
        /// </summary>
        private void ApplyTrayVisibility()
        {
            if (_trayIcon is null)
            {
                // Значок не создался при загрузке: пробуем ещё раз, иначе
                // включение настройки не даст ничего до перезапуска.
                if (TrayIconWanted)
                    SetupTray();
                return;
            }

            var wanted = TrayIconWanted;

            // Пока окно спрятано, значок обязан оставаться видимым: он
            // единственный надёжный путь назад. Если настройки требуют его
            // убрать, сперва возвращаем окно.
            if (!wanted && !IsVisible)
                ShowAndActivate();

            _trayIcon.IsVisible = wanted;
        }

        /// <summary>Позволяет повторно показать окно из трея/активации.</summary>
        public void ShowAndActivate()
        {
            if (!IsVisible)
                Show();
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            Activate();
        }
    }
}
#endif