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
    /// Класс разбит на partial-файлы по ответственности (*.Avalonia.{...}.cs); здесь —
    /// ядро: поля, конструктор и сборка верхнего уровня.
    /// </summary>
    public partial class MainWindow : Window
    {
        private MainViewModel? _vm;
        private TextBox _searchBox = null!;
        private TextBlock _statusInfo = null!;
        private TextBlock _syncMessage = null!;
        private LeveledTreeView _tree = null!;

        // Владельцы открытых ToolTip (issue #261). Записываются класс-обработчиком изменения
        // ToolTip.IsOpenProperty и не зависят от того, где физически отрисован попап (в визуальном
        // дереве окна или в оверлейном слое TopLevel), поэтому надёжно закрываются по ESC даже
        // тогда, когда старый обход дерева окна владельца не находит. Храним коллекцию, а не одно
        // поле, чтобы закрывались ВСЕ открытые подсказки, а не только последняя.
        private readonly HashSet<Control> _openToolTipOwners = new();

        // Подавленные после ESC владельцы подсказок (issue #261): пока владелец числится здесь
        // и указатель над ним (или не вышел короткий интервал), повторное открытие его ToolTip
        // блокируется обработчиком OnToolTipIsOpenChanged — тултип не «возвращается» при наведённом
        // курсоре. Подавление снимается, когда курсор ушёл с владельца и окно подавления истекло.
        private readonly HashSet<Control> _suppressedToolTipOwners = new();
        private long _lastToolTipEscTick;
        private const long ToolTipSuppressWindowMs = 800;

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

            // Регистрируем общий механизм закрытия подсказок (issue #270): глобальный реестр
            // открытых тултипов/пользовательских Popup/ContextMenu для всех окон Avalonia,
            // единый диагностический трейс CM_TOOLTIP_TRACE. Регистрация идемпотентна —
            // повторные вызовы из диалогов (ModalWindowBase) безопасны.
            ToolTipCloserAvalonia.Register();

            // ESC закрывает открытую подсказку (issue #261). Обрабатываем его на фазе
            // туннелирования (Preview): к моменту всплывающей фазы ToolTip.GetIsOpen на
            // элементе может быть уже сброшен (таймер показа/оверлейный попап), и открытая
            // подсказка не обнаружится — окно уйдёт в трей, а тултип останется висеть.
            AddHandler(InputElement.KeyDownEvent, OnPreviewKeyDownCloseToolTips, RoutingStrategies.Tunnel);

            // Отслеживаем владельца любого открытого ToolTip глобально (issue #261): подписка
            // на изменение присоединённого свойства ToolTip.IsOpenProperty на уровне класса
            // срабатывает независимо от того, лежит ли владелец в визуальном дереве окна или
            // в оверлейном слое TopLevel. Так первый ESC гарантированно закрывает подсказку,
            // а не сворачивает окно в трей с «зависшим» тултипом.
            ToolTip.IsOpenProperty.Changed.AddClassHandler<Control>(OnToolTipIsOpenChanged);

            // Отслеживаем открытые контекстные меню главного окна (issue #261). Те два элемента,
            // которые «не закрываются по ESC» (выпадающие меню запуска/выбора клиента, меню
            // «Утилиты», контекстные меню строк и заголовков), являются ContextMenu, а не стандартным
            // ToolTip. Класс-обработчик KeyDown закрывает открытое меню по ESC, когда фокус внутри
            // меню; Handled = true не даёт тому же ESC увести окно в трей. Первый ESC закрывает меню,
            // второй ESC — уже сворачивает окно в трей (инвариант issue #261).
            ContextMenu.KeyDownEvent.AddClassHandler<ContextMenu>((menu, e) =>
            {
                if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
                {
                    menu.Close();
                    e.Handled = true;
                }
            });

            // Шапка окна реагирует на активность: акцентная заливка у активного окна,
            // цвет карточки у неактивного (MainWindow.xaml.cs:78-79).
            Activated += (_, _) => ApplyTitleBarAppearance(true);
            // Подсказки скрываются при потере фокуса окна (issue #275), как контекстное меню:
            // при клике в другое окно/приложение открытый тултип исчезает, а не «висит» поверх.
            // Закрытие идёт через общий механизм ToolTipCloserAvalonia.CloseAll (issue #270) —
            // единая точка с общим трейсом для главного окна и диалогов.
            Deactivated += (_, _) =>
            {
                ApplyTitleBarAppearance(false);
                ToolTipCloserAvalonia.TraceLog("MainWindow.Deactivated: окно потеряло фокус");
                ToolTipCloserAvalonia.CloseAll();
            };

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
            // Перед открытием модального окна свойств базы запоминаем позицию прокрутки,
            // а при закрытии без сохранения («Нет») возвращаем её явно: пересборки не было,
            // иначе список «уезжает» после отмены правки (issue #252).
            _vm.TreeModalOpening += RememberTreeScroll;
            _vm.TreeModalClosed += RestoreTreeScrollAfterCancel;
            _vm.TreeRebuilt += RestoreTreeSelection;
            // «Найти в списке» (issue #285): цель выставлена и дерево пересобрано — окну
            // нужно показать строку, а не возвращать прежнюю позицию прокрутки.
            _vm.RevealFindInListRequested += RevealFindInList;

            // Смена языка интерфейса: названия колонок, кнопки правой панели и подсказки
            // создаются в коде через LocalizationManager.T(...), поэтому окно пересобирается,
            // чтобы переведённый текст появился сразу, а не после перезапуска.
            LocalizationManager.Instance.LanguageChanged += OnLanguageChanged;
        }

        /// <summary>
        /// Класс-обработчик изменения <see cref="ToolTip.IsOpenProperty"/>. Запоминает владельцев
        /// открытых подсказок в <see cref="_openToolTipOwners"/> (issue #261), чтобы их можно было
        /// закрыть по ESC детерминированно, не полагаясь на обход визуального дерева окна.
        /// </summary>
        private void OnToolTipIsOpenChanged(Control owner, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.NewValue is true)
            {
                // Вето на повторное открытие после ESC (issue #261): если владелец подавлен,
                // сразу гасим показанную подсказку и не запоминаем её в открытых.
                if (IsToolTipSuppressed(owner))
                {
                    ToolTip.SetIsOpen(owner, false);
                    return;
                }
                _openToolTipOwners.Add(owner);
            }
            else
            {
                _openToolTipOwners.Remove(owner);
            }
        }

        /// <summary>
        /// Заблокировано ли повторное открытие подсказки владельца после ESC (issue #261).
        /// Пока указатель над владельцем (<see cref="Control.IsPointerOver"/>) или не вышел
        /// короткий интервал — подавлено; как только курсор ушёл и окно истекло — подавление
        /// снимается, и тултип снова работает как обычно.
        /// </summary>
        private bool IsToolTipSuppressed(Control owner)
        {
            if (!_suppressedToolTipOwners.Contains(owner))
                return false;

            if (Environment.TickCount64 - _lastToolTipEscTick < ToolTipSuppressWindowMs || owner.IsPointerOver)
                return true;

            _suppressedToolTipOwners.Remove(owner);
            return false;
        }

        /// <summary>Подавляет повторное открытие подсказки владельца после закрытия по ESC (issue #261).</summary>
        private void SuppressToolTipOwner(Control owner)
        {
            if (_suppressedToolTipOwners.Add(owner))
                _lastToolTipEscTick = Environment.TickCount64;
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
                ClipToBounds = true,
                // Видимая рамка окна (issue #273): даже без системных теней/эффектов
                // (терминал, виртуализация) окно остаётся различимым, а не сливается
                // с фоном рабочего стола за ним.
                BorderThickness = new Thickness(1)
            };
            // Цвет рамки берём из темы — адаптируется к светлой/тёмной теме и схеме.
            ThemeBrushes.Bind(glass, Border.BorderBrushProperty, "BorderColorBrush");
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

            // «Утилиты»: общие команды, не привязанные к конкретной базе (issue #262).
            // Раньше такие команды лежали в контекстном меню базы; теперь собраны в
            // отдельное подменю общей панели, куда в будущем можно добавлять новые.
            var utilitiesMenu = BuildUtilitiesMenu();
            var utilitiesBtn = new Button
            {
                // Отдельная иконка (Apps-сетка), чтобы не путать с «Конфигуратором»
                // (гаечный ключ IconWrench) — issue #282.
                Content = ThemedIconAndText("IconApps",
                    LocalizationManager.T("Main.Utilities"), "TextSecondaryColorBrush",
                    UiMetrics.ScaledFont(13), centered: false),
                Padding = new Thickness(UiMetrics.ButtonPadH, UiMetrics.ButtonPadV),
                HorizontalContentAlignment = HorizontalAlignment.Center
            };
            utilitiesBtn.Styled(Themes.ControlThemes.IconButton);
            ToolTip.SetTip(utilitiesBtn, LocalizationManager.T("Main.UtilitiesTooltip"));
            utilitiesBtn.ContextMenu = utilitiesMenu;
            utilitiesBtn.Click += (_, _) => utilitiesMenu.Open(utilitiesBtn);
            panel.Children.Add(utilitiesBtn);

            // «Актуальные релизы» перенесены в подменю «Утилиты» (issue #279), отдельная
            // кнопка на панели не нужна — в меню команда уже есть (пункт первый).
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
                // Отступы шапки из метрик: компактный режим уменьшает высоту заголовка
                // окна вместе с кнопками управления (кнопки уже масштабируются через
                // UiMetrics.Scaled), как на Windows (issue #296).
                Padding = new Thickness(UiMetrics.TitleBarPadH, UiMetrics.TitleBarPadV)
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
    }
}
#endif