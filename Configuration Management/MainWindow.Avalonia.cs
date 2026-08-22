#if LINUX
using System;
using System.Collections.Generic;
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
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
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
        private TextBlock _emptyTitle = null!;
        private SegmentButton? _tagsToggle;
        private Border? _columnHeader;
        private Grid? _columnHeaderRow;
        private ColumnDefinition? _headerOffsetColumn;
        private Control? _headerPinMark;
        private double _headerToolbarWidth;
        private readonly List<IDisposable> _columnHeaderSubscriptions = new();
        private Grid? _listContent;
        private bool _columnHeaderRefreshQueued;
        private bool _headerAlignQueued;
        private readonly Dictionary<string, int> _headerColumnIndex = new(StringComparer.Ordinal);
        private readonly List<IDisposable> _rightPanelSubscriptions = new();
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

        public MainWindow(MainViewModel viewModel)
        {
            _vm = viewModel;

            Title = LocalizationManager.T("App.Title");
            Width = 1200;
            Height = 760;
            MinWidth = 900;
            MinHeight = 600;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            DataContext = viewModel;

            Content = BuildRoot();
            Loaded += OnWindowLoaded;
            KeyDown += OnWindowKeyDown;
        }

        // ======================= Построение UI =======================

        private Control BuildRoot()
        {
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Star });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var topBar = BuildTopBar();
            var tagPanel = BuildTagFilterPanel();
            var mainArea = BuildMainArea();
            var statusBar = BuildStatusBar();

            Grid.SetRow(topBar, 0);
            Grid.SetRow(tagPanel, 1);
            Grid.SetRow(mainArea, 2);
            Grid.SetRow(statusBar, 3);

            grid.Children.Add(topBar);
            grid.Children.Add(tagPanel);
            grid.Children.Add(mainArea);
            grid.Children.Add(statusBar);

            // Фон рабочей области окна следует теме (перекрашивается при смене схемы).
            ThemeBrushes.Bind(grid, Panel.BackgroundProperty, "ContentBackgroundColorBrush");
            return grid;
        }

        private Control BuildTopBar()
        {
            var grid = new Grid { Margin = new Thickness(UiMetrics.TopBarH, UiMetrics.TopBarV) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 180 });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Слева: сегментные переключатели групп и тегов (с иконками и состояниями).
            var left = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
                Spacing = 2
            };

            var groupByToggle = MakeSegmentToggle("IconGroups", LocalizationManager.T("Main.ToggleGroups"));
            groupByToggle.IsChecked = _vm?.GroupByGroup ?? true;
            groupByToggle.Click += (_, _) => { if (_vm is not null) _vm.GroupByGroup = groupByToggle.IsChecked == true; };
            left.Children.Add(groupByToggle);

            _tagsToggle = MakeSegmentToggle("IconTag", LocalizationManager.T("Main.ToggleTags"));
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
            var tabs = BuildListModeSegments();
            grid.Children.Add(tabs);
            Grid.SetColumn(tabs, 2);

            // Справа: добавить базу, синхронизация, тема, настройки — иконки + подписи, состояния.
            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Spacing = 6
            };

            var addBtn = TopBarPrimaryButton("IconAdd", LocalizationManager.T("Main.Add"), LocalizationManager.T("Main.AddTooltip"));
            addBtn.Bind(Button.CommandProperty, new Binding("AddInfobaseCommand"));
            actions.Children.Add(addBtn);

            var syncBtn = TopBarSecondaryButton("IconSync", LocalizationManager.T("Main.Sync"), LocalizationManager.T("Main.SyncWithIbases"));
            syncBtn.Bind(Button.CommandProperty, new Binding("SynchronizeWithIbasesCommand"));
            actions.Children.Add(syncBtn);

            var themeBtn = TopBarIconButton("IconTheme", LocalizationManager.T("Main.Theme"));
            themeBtn.Bind(Button.CommandProperty, new Binding("ToggleThemeCommand"));
            actions.Children.Add(themeBtn);

            var settingsBtn = TopBarSecondaryButton("IconSettings", LocalizationManager.T("Main.Settings"), LocalizationManager.T("Main.SettingsTooltip"));
            settingsBtn.Bind(Button.CommandProperty, new Binding("OpenSettingsCommand"));
            actions.Children.Add(settingsBtn);

            grid.Children.Add(actions);
            Grid.SetColumn(actions, 3);

            var topBarBorder = new Border
            {
                Child = grid,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(UiMetrics.TopBarH, UiMetrics.TopBarV)
            };
            // Нижняя граница TopBar из темы.
            ThemeBrushes.Bind(topBarBorder, Border.BorderBrushProperty, "BorderColorBrush");
            return topBarBorder;
        }

        /// <summary>Сегментный переключатель (например «группы»/«теги») с иконкой и состояниями.</summary>
        private SegmentButton MakeSegmentToggle(string iconKey, string tooltip)
        {
            var segment = new SegmentButton(iconKey, string.Empty, "ItemHoverBrush", "ItemSelectedBrush", lockOn: false)
            {
                IsChecked = true
            };
            ToolTip.SetTip(segment, tooltip);
            return segment;
        }

        /// <summary>Сегментированный контроль фильтра списка: Все / Избранное / Недавние.</summary>
        private Control BuildListModeSegments()
        {
            var container = new Border
            {
                CornerRadius = new CornerRadius(UiMetrics.RadiusLg),
                Padding = new Thickness(3),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            };
            ThemeBrushes.Bind(container, Border.BackgroundProperty, "CardBackgroundBrush");
            ThemeBrushes.Bind(container, Border.BorderBrushProperty, "BorderColorBrush");
            UiMetrics.AddBrushTransition(container);

            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };

            var allSeg = new SegmentButton("IconList", LocalizationManager.T("Main.AllBases"), "ItemHoverBrush", "ItemSelectedBrush");
            allSeg.Bind(ToggleButton.IsCheckedProperty, new Binding("IsListModeAll") { Mode = BindingMode.TwoWay });
            panel.Children.Add(allSeg);

            var favSeg = new SegmentButton("IconFavorite", LocalizationManager.T("Main.Favorites"), "ItemHoverBrush", "ItemSelectedBrush");
            favSeg.Bind(ToggleButton.IsCheckedProperty, new Binding("IsListModeFavorites") { Mode = BindingMode.TwoWay });
            panel.Children.Add(favSeg);

            var recSeg = new SegmentButton("IconRecent", LocalizationManager.T("Main.Recent"), "ItemHoverBrush", "ItemSelectedBrush");
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
                VerticalContentAlignment = VerticalAlignment.Center,
                Watermark = LocalizationManager.T("Main.SearchPlaceholder")
            };
            _searchBox.Bind(TextBox.TextProperty, new Binding("SearchText") { Mode = BindingMode.TwoWay });
            grid.Children.Add(_searchBox);
            Grid.SetColumn(_searchBox, 1);

            var clearBtn = new Button
            {
                Content = IconHelper.MakeIcon("IconClose", 14, "TextSecondaryBrush"),
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

            // Hover-состояние: фон и граница подсвечиваются из ресурсов темы (без жёстких цветов).
            IBrush baseBg = Brushes.Transparent;
            IBrush hoverBg = Brushes.Transparent;
            IBrush baseBorder = Brushes.Transparent;
            IBrush hoverBorder = Brushes.Transparent;
            IBrush accentBorder = Brushes.Transparent;
            var hovered = false;
            var focused = false;

            void Refresh()
            {
                border.Background = (hovered || focused) ? hoverBg : baseBg;
                border.BorderBrush = focused ? accentBorder : (hovered ? hoverBorder : baseBorder);
                border.BorderThickness = focused ? new Thickness(2) : new Thickness(1);
            }

            if (Application.Current is { } app)
            {
                app.GetResourceObservable("CardBackgroundColorBrush").Subscribe(new BrushObserver(b => baseBg = b, Refresh));
                app.GetResourceObservable("ItemHoverBrush").Subscribe(new BrushObserver(b => hoverBg = b, Refresh));
                app.GetResourceObservable("BorderColorBrush").Subscribe(new BrushObserver(b => baseBorder = b, Refresh));
                app.GetResourceObservable("AccentBrush").Subscribe(new BrushObserver(b => { hoverBorder = b; accentBorder = b; }, Refresh));
            }

            border.PointerEntered += (_, _) => { hovered = true; Refresh(); };
            border.PointerExited += (_, _) => { hovered = false; Refresh(); };
            // Фокус-ринг поля поиска (клавиатурная навигация) акцентным цветом темы.
            _searchBox.GetObservable(TextBox.IsKeyboardFocusWithinProperty)
                .Subscribe(new BoolObserver(v => { focused = v; Refresh(); }));
            UiMetrics.AddBrushTransition(border);
            return border;
        }

        /// <summary>Primary-кнопка топ-бара: акцентный фон, иконка + подпись цветом «на акценте».</summary>
        private static PanelButton TopBarPrimaryButton(string iconKey, string text, string tooltip)
        {
            var button = new PanelButton("AccentBrush", "AccentHoverBrush", "AccentPressedBrush", "AccentBrush")
            {
                Content = ThemedIconAndText(iconKey, text, "TextOnAccentBrush", UiMetrics.Scaled(15), centered: false),
                Padding = new Thickness(UiMetrics.ButtonPadH, UiMetrics.ButtonPadV),
                HorizontalContentAlignment = HorizontalAlignment.Center
            };
            ToolTip.SetTip(button, tooltip);
            return button;
        }

        /// <summary>Secondary-кнопка топ-бара: приглушённый фон, иконка + подпись, hover/pressed.</summary>
        private static PanelButton TopBarSecondaryButton(string iconKey, string text, string tooltip)
        {
            var button = new PanelButton(
                "SecondaryButtonBackgroundBrush",
                "SecondaryButtonHoverBrush",
                "SecondaryButtonPressedBrush",
                "BorderColorBrush")
            {
                Content = ThemedIconAndText(iconKey, text, "ButtonTextBrush", UiMetrics.Scaled(15), centered: false),
                Padding = new Thickness(UiMetrics.ButtonPadH, UiMetrics.ButtonPadV),
                HorizontalContentAlignment = HorizontalAlignment.Center
            };
            ToolTip.SetTip(button, tooltip);
            return button;
        }

        /// <summary>Компактная иконко-кнопка топ-бара (например тема) с состояниями из темы.</summary>
        private static PanelButton TopBarIconButton(string iconKey, string tooltip)
        {
            var button = new PanelButton(
                "SecondaryButtonBackgroundBrush",
                "SecondaryButtonHoverBrush",
                "SecondaryButtonPressedBrush",
                "BorderColorBrush")
            {
                Content = IconHelper.MakeIcon(iconKey, UiMetrics.Scaled(16), "ButtonTextBrush"),
                Padding = new Thickness(UiMetrics.ButtonPadH, UiMetrics.ButtonPadV),
                HorizontalContentAlignment = HorizontalAlignment.Center
            };
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
            // Фон списка баз — фон рабочей области из темы.
            ThemeBrushes.Bind(_tree, TemplatedControl.BackgroundProperty, "ContentBackgroundColorBrush");
            // Горизонтальная прокрутка отключена: иначе строка растягивается
            // по сумме ширин колонок и уезжает за правый край, а заголовки,
            // живущие вне области прокрутки, перестают совпадать со значениями.
            ScrollViewer.SetHorizontalScrollBarVisibility(_tree, ScrollBarVisibility.Disabled);
            _tree.Bind(TreeView.ItemsSourceProperty, new Binding("GroupNodes"));
            _tree.SelectionMode = SelectionMode.Single;

            // Убираем стандартную подсветку контейнера TreeViewItem: карточка строки
            // сама рисует hover и выделение из ресурсов темы. Селектор сопоставляется
            // по ключу стиля, а он переопределён на TreeViewItem, иначе стиль
            // не нашёл бы контейнеры.
            var tviStyle = new Style(x => x.OfType<TreeViewItem>());
            tviStyle.Setters.Add(new Setter(TemplatedControl.BackgroundProperty, Brushes.Transparent));
            _tree.Styles.Add(tviStyle);

            // Фон покоя этим снят, но состояния выделения и наведения Fluent задаёт
            // не свойством контейнера, а вложенным стилем на части шаблона, поэтому
            // синяя полоса рисовалась бы за карточкой. Гасим её адресно.
            foreach (var state in new[] { ":selected", ":pointerover" })
            {
                var stateStyle = new Style(x => x.OfType<TreeViewItem>().Class(state)
                    .Template().OfType<Border>().Name("PART_LayoutRoot"));
                stateStyle.Setters.Add(new Setter(Border.BackgroundProperty, Brushes.Transparent));
                _tree.Styles.Add(stateStyle);
            }

            // Раскрытие контейнера связывается с моделью узла адресно, при подготовке
            // контейнера, а не стилем: стиль повесил бы привязку и на строки баз,
            // у которых свойства IsExpanded нет, и журнал заполнялся бы
            // предупреждениями привязки на каждое перестроение дерева.
            // Контейнеры дерева переиспользуются, поэтому прежняя привязка
            // освобождается: иначе на одном контейнере копились бы выражения
            // привязки по одному на каждую подготовку.
            var expandedBindings = new System.Runtime.CompilerServices.ConditionalWeakTable<Control, IDisposable>();
            _tree.ContainerPrepared += (_, e) =>
            {
                if (e.Container is not TreeViewItem container)
                    return;

                // Раскрытие группы добавляет строки, а с ними может измениться
                // и самый левый отступ, по которому выровнен заголовок.
                QueueHeaderAlign();

                if (expandedBindings.TryGetValue(container, out var previous))
                {
                    previous.Dispose();
                    expandedBindings.Remove(container);
                }

                if (container.DataContext is GroupNodeViewModel)
                {
                    expandedBindings.Add(container, container.Bind(TreeViewItem.IsExpandedProperty,
                        new Binding("IsExpanded") { Mode = BindingMode.TwoWay }));
                }
            };

            // Меню висит на дереве, как в WPF: над группой и над пустым местом
            // оно тоже открывается, а недоступные пункты гасит CanExecute.
            // Строку под курсором дерево выделяет само, по правому нажатию.
            _tree.ContextMenu = BuildRowContextMenu();

            _tree.ItemTemplate = new FuncTreeDataTemplate(
                typeof(object),
                (item, _) => BuildTreeRow(item),
                item => item is GroupNodeViewModel g ? g.Items : null);
            _tree.SelectionChanged += OnTreeSelectionChanged;

            // Прокрутку списка ведёт внешний ScrollViewer, общий с заголовком колонок.
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

            var leftPanel = new Border
            {
                Child = listArea,
                Margin = new Thickness(UiMetrics.TopBarH, UiMetrics.TopBarV, 8, UiMetrics.TopBarV),
                Padding = new Thickness(UiMetrics.Scaled(8), UiMetrics.Scaled(8))
            };

            grid.Children.Add(leftPanel);
            Grid.SetColumn(leftPanel, 0);

            // Показываем/скрываем заглушку при любых изменениях списка и поиска.
            if (_vm is not null)
            {
                // Строки пересобираются вместе с деревом, поэтому заголовок
                // выравнивается по ним заново: отступ уровня мог измениться.
                _vm.GroupNodes.CollectionChanged += (_, _) => { UpdateEmptyState(); QueueHeaderAlign(); };
                _vm.FlatItems.CollectionChanged += (_, _) => UpdateEmptyState();
                _vm.TagFiltersRebuilt += (_, _) => RefreshTagFilterPanel();
                _vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(MainViewModel.SearchText))
                        UpdateEmptyState();
                    // Заголовок строится до загрузки настроек, поэтому обновляется
                    // при уведомлении о колонках: иначе сохранённые ширина и состав
                    // применились бы к строкам, но не к уже собранному заголовку.
                    if (e.PropertyName is not null && e.PropertyName.Contains("Column", StringComparison.Ordinal))
                        QueueColumnHeaderRefresh();
                    // Кнопки групп живут в заголовке и видны только при группировке.
                    if (e.PropertyName == nameof(MainViewModel.ShowExpandCollapseButtons))
                        QueueColumnHeaderRefresh();
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
            }
            UpdateEmptyState();
            RefreshTagFilterPanel();
            RefreshColumnHeader();

            var rightPanel = new ScrollViewer
            {
                Name = "RightPanelBorder",
                Content = BuildRightPanel(),
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(UiMetrics.Scaled(16), UiMetrics.Scaled(14)),
                MinWidth = UiMetrics.RightPanelMin,
                MaxWidth = UiMetrics.RightPanelMax
            };

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

            _emptyIcon = IconHelper.MakeIcon("IconDatabase", 44, "TextSecondaryBrush");
            _emptyIcon.HorizontalAlignment = HorizontalAlignment.Center;
            _emptyIcon.Margin = new Thickness(0, 0, 0, 6);
            stack.Children.Add(_emptyIcon);

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
            var header = new Border
            {
                CornerRadius = new CornerRadius(UiMetrics.RadiusSm),
                Padding = new Thickness(6, 3),
                Margin = new Thickness(0, 1)
            };
            header.Bind(Border.BackgroundProperty, new Binding("HeaderBrush") { Source = group });

            // Имя и счётчик привязаны к узлу, а не подставлены строкой: состав узла
            // меняется и без пересборки дерева (закрепление базы), и тогда готовый
            // текст остался бы со старым числом.
            var caption = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };

            var text = new TextBlock { FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            text.Bind(TextBlock.TextProperty, new Binding("DisplayName") { Source = group });
            text.Bind(TextBlock.ForegroundProperty, new Binding("HeaderTextBrush") { Source = group });
            caption.Children.Add(text);

            var count = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            count.Bind(TextBlock.TextProperty,
                new Binding("TotalInfobaseCount") { Source = group, StringFormat = "({0})" });
            count.Bind(TextBlock.ForegroundProperty, new Binding("HeaderTextBrush") { Source = group });
            caption.Children.Add(count);

            header.Child = caption;
            return header;
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
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(showFavorite ? FavoriteColumnWidth : 0) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(showPin ? PinColumnWidth : 0) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(IconColumnWidth) });
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = NameColumnLength(),
                MinWidth = MinColumnWidth
            });

            // Колонки идут теми же ширинами, что и в заголовке, поэтому значения
            // строк выстраиваются под своими заголовками.
            var columns = ListColumns();
            foreach (var column in columns)
                grid.ColumnDefinitions.Add(
                    new ColumnDefinition { Width = new GridLength(column.Width), MinWidth = MinColumnWidth });

            if (showFavorite)
            {
                var favorite = RowMarkButton(card, ib, "IconFavorite", "FavoriteBrush",
                    nameof(Infobase.IsFavorite), () => ib.IsFavorite,
                    LocalizationManager.T("Main.ToggleFavoriteTooltip"), "ToggleFavoriteForCommand", FavoriteColumnWidth);
                grid.Children.Add(favorite);
                Grid.SetColumn(favorite, 0);
            }

            if (showPin)
            {
                var pin = RowMarkButton(card, ib, "IconPin", "AccentBrush",
                    nameof(Infobase.IsPinned), () => ib.IsPinned,
                    LocalizationManager.T("Main.TogglePinTooltip"), "TogglePinForCommand", PinColumnWidth);
                grid.Children.Add(pin);
                Grid.SetColumn(pin, 1);
            }

            // Иконка статуса базы слева: тип подключения (папка / глобус / сеть)
            // или «недоступна». Цвет зависит от статуса: янтарный — файловая,
            // синий — веб, фиолетовый — клиент-сервер, красный — недоступна.
            var connectionIconKey = ib.StatusIconKey;

            var iconBox = new Border
            {
                Width = UiMetrics.RowIconBox,
                Height = UiMetrics.RowIconBox,
                CornerRadius = new CornerRadius(UiMetrics.RadiusMd),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 0, 10, 0)
            };
            ToolTip.SetTip(iconBox, ib.StatusDisplay);
            card.AddSubscription(() => ThemeBrushes.Bind(iconBox, Border.BackgroundProperty, "CardBackgroundBrush"));
            card.AddSubscription(() => ThemeBrushes.Bind(iconBox, Border.BorderBrushProperty, "BorderColorBrush"));
            iconBox.Child = new Avalonia.Controls.Shapes.Path
            {
                Width = UiMetrics.RowIcon,
                Height = UiMetrics.RowIcon,
                Data = IconHelper.Geometry(connectionIconKey),
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Fill = new SolidColorBrush(Color.Parse(ib.StatusColorHex))
            };

            grid.Children.Add(iconBox);
            Grid.SetColumn(iconBox, 2);

            // Правая колонка: имя (крупно) + строки вторичной информации.
            // В компактном режиме уменьшаем и межстрочный промежуток, чтобы строки с
            // полным набором метаданных тоже «сжимались», а не оставались прежней высоты.
            var content = new StackPanel { Spacing = UiMetrics.Scaled(2), VerticalAlignment = VerticalAlignment.Center };

            // Имя базы кладётся в колонку напрямую: в горизонтальной панели оно
            // получало бы бесконечную ширину и при узкой колонке налезало бы
            // на соседние значения вместо обрезки многоточием.
            var name = new TextBlock
            {
                Text = ib.Name,
                FontSize = UiMetrics.RowNameFont,
                FontWeight = FontWeight.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            card.AddSubscription(() => ThemeBrushes.Bind(name, TextBlock.ForegroundProperty, "TextPrimaryBrush"));
            content.Children.Add(name);

            // Вторичной строкой остаётся только то, чего нет в колонках: тип
            // подключения и путь. Остальное ушло в колонки, иначе одни и те же
            // данные показывались бы дважды.
            var shown = new HashSet<string>(columns.Select(c => c.Key), StringComparer.Ordinal);
            var location = ib.Connection.Type switch
            {
                ConnectionType.WebServer => ib.Connection.WebUrl,
                _ => ib.ServerDatabaseDisplay
            };
            var summary = shown.Contains("ServerBase")
                ? ib.ConnectionTypeDisplay
                : JoinSegments(ib.ConnectionTypeDisplay, location);
            if (!string.IsNullOrWhiteSpace(summary))
                content.Children.Add(SecondaryText(summary, card));

            grid.Children.Add(content);
            Grid.SetColumn(content, 3);

            for (var i = 0; i < columns.Count; i++)
            {
                var value = ColumnValue(ib, columns[i].Key);
                var cell = SecondaryText(string.IsNullOrWhiteSpace(value) ? string.Empty : value, card);
                cell.VerticalAlignment = VerticalAlignment.Center;
                grid.Children.Add(cell);
                Grid.SetColumn(cell, i + 4);
            }

            if (_vm?.ShowTags == true)
            {
                // Теги идут второй строкой сетки во всю ширину, как в WPF-версии:
                // внутри колонки имени они переносились бы по её ширине и тянули
                // высоту строки вверх.
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var tags = BuildRowTags(card, ib);
                grid.Children.Add(tags);
                Grid.SetRow(tags, 1);
                Grid.SetColumn(tags, NameRowColumn);
                Grid.SetColumnSpan(tags, grid.ColumnDefinitions.Count - NameRowColumn);
            }

            card.Child = grid;
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
                FontSize = UiMetrics.Scaled(10),
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = UiMetrics.Scaled(180),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            ToolTip.SetTip(text, tag);
            Track(subscriptions, ThemeBrushes.Bind(text, TextBlock.ForegroundProperty, "TextSecondaryBrush"));

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

            var remove = new Button
            {
                Content = IconHelper.MakeIcon("IconClose", UiMetrics.Scaled(9), "TextSecondaryBrush", subscriptions),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                Margin = new Thickness(4, 0, 0, 0),
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

            var chip = new Border
            {
                CornerRadius = new CornerRadius(UiMetrics.RadiusMd),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(6, 1),
                Margin = new Thickness(0, 0, 4, 2),
                Child = row
            };
            Track(subscriptions, ThemeBrushes.Bind(chip, Border.BackgroundProperty, "CardBackgroundBrush"));
            Track(subscriptions, ThemeBrushes.Bind(chip, Border.BorderBrushProperty, "BorderColorBrush"));
            return chip;
        }

        /// <summary>Складывает подписку в приёмник, пропуская пустую (Application ещё не поднят).</summary>
        private static void Track(ICollection<IDisposable> sink, IDisposable? subscription)
        {
            if (subscription is not null)
                sink.Add(subscription);
        }

        /// <summary>Кнопка «+ тег» в конце списка тегов строки.</summary>
        private Control BuildAddTagButton(Infobase infobase, ICollection<IDisposable> subscriptions)
        {
            var text = new TextBlock
            {
                Text = LocalizationManager.T("Main.AddTagShort"),
                FontSize = UiMetrics.Scaled(10),
                VerticalAlignment = VerticalAlignment.Center
            };
            Track(subscriptions, ThemeBrushes.Bind(text, TextBlock.ForegroundProperty, "TextSecondaryBrush"));

            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            content.Children.Add(IconHelper.MakeIcon("IconTag", UiMetrics.Scaled(9), "TextSecondaryBrush", subscriptions));
            content.Children.Add(text);

            var button = new Button
            {
                Content = content,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(2, 1),
                MinWidth = 0,
                MinHeight = 0,
                Cursor = new Cursor(StandardCursorType.Hand),
                CommandParameter = infobase
            };
            ToolTip.SetTip(button, LocalizationManager.T("Main.AddTag"));
            button.Bind(Button.CommandProperty, new Binding("AddTagCommand") { Source = _vm });
            return button;
        }

        /// <summary>
        /// Кнопка-маркер в строке базы: звезда «избранное» или булавка «закреплено».
        /// Цвет иконки следит за состоянием самой базы, поэтому после переключения
        /// строку не нужно пересобирать, и за кистями темы он тоже следует.
        /// </summary>
        private Button RowMarkButton(InfobaseRowCard card, Infobase infobase, string iconKey,
            string activeBrushKey, string stateProperty, Func<bool> isActive,
            string tooltip, string commandPath, double width)
        {
            var icon = new Avalonia.Controls.Shapes.Path
            {
                Width = UiMetrics.Scaled(14),
                Height = UiMetrics.Scaled(14),
                Data = IconHelper.Geometry(iconKey),
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

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
                Content = icon,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                MinWidth = 0,
                MinHeight = 0,
                Width = width,
                HorizontalAlignment = HorizontalAlignment.Left,
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
                owner.AddSubscription(() => ThemeBrushes.Bind(block, TextBlock.ForegroundProperty, "TextSecondaryBrush"));
            return block;
        }

        private Control BuildRightPanel()
        {
            // Панель пересобирается вместе с окном (компактный режим), поэтому
            // прежние подписки на кисти темы освобождаются.
            foreach (var subscription in _rightPanelSubscriptions)
                subscription.Dispose();
            _rightPanelSubscriptions.Clear();

            var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };

            // Заголовок базы
            var nameBlock = new TextBlock { FontSize = 16, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
            nameBlock.Bind(TextBlock.TextProperty, new Binding("SelectedInfobase.Name"));

            var groupBlock = new TextBlock { FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
            groupBlock.Bind(TextBlock.TextProperty, new Binding("SelectedInfobase.GroupDisplay"));

            var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12), Spacing = 10 };
            header.Children.Add(IconHelper.MakeIcon("IconDatabase", 28));
            var headerText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            headerText.Children.Add(nameBlock);
            headerText.Children.Add(groupBlock);
            header.Children.Add(headerText);
            panel.Children.Add(header);

            // Основное действие (primary) — крупная акцентная кнопка вверху.
            panel.Children.Add(PrimaryActionButton("IconPlay", LocalizationManager.T("Main.LaunchEnterprise"), "LaunchEnterpriseCommand", LocalizationManager.T("Main.LaunchEnterpriseTooltip")));

            // Секции secondary-действий, сгруппированные по смыслу.
            panel.Children.Add(SectionCard(LocalizationManager.T("Main.SectionConfigurator"), "IconConfiguration",
                SecondaryActionButton("IconWrench", LocalizationManager.T("Main.LaunchConfiguratorSection"), "LaunchConfiguratorCommand", LocalizationManager.T("Main.LaunchConfiguratorSectionTooltip"))));

            panel.Children.Add(SectionCard(LocalizationManager.T("Main.SectionMaintenance"), "IconWrench",
                BuildClearCacheSplitButton(),
                SecondaryActionButton("IconEdit", LocalizationManager.T("Main.EditSettings"), "EditInfobaseCommand", LocalizationManager.T("Main.EditBaseTooltip")),
                SecondaryActionButton("IconOpen", LocalizationManager.T("Main.OpenFolder"), "OpenInfobaseFolderCommand", LocalizationManager.T("Main.OpenFolderTooltip")),
                SecondaryActionButton("IconKeyboard", LocalizationManager.T("Main.RunStarter"), "OpenNativeStarterCommand", LocalizationManager.T("Main.NativeStarterTooltip"))));

            panel.Children.Add(SectionCard(LocalizationManager.T("Main.SectionBaseList"), "IconList",
                SecondaryActionButton("IconAdd", LocalizationManager.T("Main.AddBaseOrGroup"), "AddInfobaseCommand", LocalizationManager.T("Main.AddBaseOrGroupTooltip")),
                SecondaryActionButton("IconShortcut", LocalizationManager.T("Main.DesktopShortcut"), "CreateDesktopShortcutCommand", LocalizationManager.T("Main.DesktopShortcutTooltip")),
                SecondaryActionButton("IconDelete", LocalizationManager.T("Main.Delete"), "DeleteInfobaseCommand", LocalizationManager.T("Main.DeleteTooltip"))));

            panel.Children.Add(SectionCard(LocalizationManager.T("Main.SectionMarks"), "IconStar",
                SecondaryActionButton("IconFavorite", LocalizationManager.T("Main.ToFavorites"), "ToggleFavoriteCommand", LocalizationManager.T("Main.ToggleFavoriteTooltip")),
                SecondaryActionButton("IconPin", LocalizationManager.T("Main.Pin"), "TogglePinCommand", LocalizationManager.T("Main.PinBaseTooltip"))));

            // Информация о подключении.
            panel.Children.Add(SectionCard(LocalizationManager.T("Main.SectionConnInfo"), "IconInfo",
                DetailRow(LocalizationManager.T("Main.Type"), new Binding("SelectedInfobase.ConnectionTypeDisplay")),
                DetailRow(LocalizationManager.T("Main.ServerPath"), new Binding("SelectedInfobase.ConnectionPathDisplay")),
                DetailRow(LocalizationManager.T("Column.ServerBase"), new Binding("SelectedInfobase.ServerDatabaseDisplay")),
                DetailRow(LocalizationManager.T("Main.ConnectionString"), new Binding("SelectedInfobase.ConnectionStringDisplay")),
                DetailRow(LocalizationManager.T("Main.Platform"), new Binding("SelectedInfobase.PlatformVersion")),
                DetailRow(LocalizationManager.T("Main.LaunchMode"), new Binding("SelectedInfobase.ParsedLaunchMode")),
                DetailRow(LocalizationManager.T("Main.Client"), new Binding("SelectedInfobase.ClientTypeDisplay")),
                DetailRow(LocalizationManager.T("Main.Bitness"), new Binding("SelectedInfobase.ArchitectureDisplay")),
                DetailRow(LocalizationManager.T("Main.Parameters"), new Binding("SelectedInfobase.LaunchParameters")),
                DetailRow(LocalizationManager.T("Main.LastLaunch"), new Binding("SelectedInfobase.LastLaunchDisplay"))));

            // Блок «Текущая сессия»: значения действуют только на следующий запуск.
            panel.Children.Add(BuildSessionCard());

            // Описание.
            var desc = new TextBlock { TextWrapping = TextWrapping.Wrap };
            desc.Bind(TextBlock.TextProperty, new Binding("SelectedInfobase.Description"));
            panel.Children.Add(SectionCard(LocalizationManager.T("Main.Description"), "IconInfo", desc));

            panel.Children.Add(SecondaryActionButton("IconExit", LocalizationManager.T("Main.Exit"), "ExitCommand",
                LocalizationManager.T("Main.ExitTooltip")));

            return panel;
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
                FontSize = UiMetrics.Scaled(11),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6)
            };
            Track(_rightPanelSubscriptions, ThemeBrushes.Bind(hint, TextBlock.ForegroundProperty, "TextSecondaryBrush"));
            ToolTip.SetTip(hint, LocalizationManager.T("Main.CurrentSessionHelp"));

            var card = SectionCard(LocalizationManager.T("Main.CurrentSession"), "IconInfo",
                hint,
                SessionGroupLabel(LocalizationManager.T("Main.ClientMode")),
                SessionOption(LocalizationManager.T("Main.SessionClientAuto"), "SessionClient", "IsSessionClientAuto"),
                SessionOption(LocalizationManager.T("Main.SessionClientOrdinary"), "SessionClient", "IsSessionClientOrdinary"),
                SessionOption(LocalizationManager.T("Main.SessionClientThickManaged"), "SessionClient", "IsSessionClientThick",
                    LocalizationManager.T("Main.SessionThickManagedTooltip")),
                SessionOption(LocalizationManager.T("Main.SessionClientThickOrdinary"), "SessionClient", "IsSessionClientThickOrdinary",
                    LocalizationManager.T("Main.SessionThickOrdinaryTooltip")),
                SessionOption(LocalizationManager.T("Main.SessionClientThin"), "SessionClient", "IsSessionClientThin"),
                SessionGroupLabel(LocalizationManager.T("Main.Bitness")),
                SessionOption(LocalizationManager.T("Main.SessionClientAuto"), "SessionArch", "IsSessionArchAuto"),
                SessionOption("32", "SessionArch", "IsSessionArch32"),
                SessionOption("64", "SessionArch", "IsSessionArch64"));

            card.Bind(Control.IsVisibleProperty, new Binding("ShowSessionLaunchPanel"));
            return card;
        }

        /// <summary>Подпись группы переключателей в блоке текущей сессии.</summary>
        private Control SessionGroupLabel(string text)
        {
            var block = new TextBlock
            {
                Text = text,
                FontSize = UiMetrics.Scaled(11),
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, 6, 0, 2)
            };
            Track(_rightPanelSubscriptions, ThemeBrushes.Bind(block, TextBlock.ForegroundProperty, "TextSecondaryBrush"));
            return block;
        }

        /// <summary>Переключатель в блоке текущей сессии: одна из взаимоисключающих опций.</summary>
        private static Control SessionOption(string text, string group, string propertyPath, string? tooltip = null)
        {
            var option = new RadioButton
            {
                Content = text,
                GroupName = group,
                FontSize = UiMetrics.Scaled(12),
                Margin = new Thickness(0, 1)
            };
            option.Bind(RadioButton.IsCheckedProperty, new Binding(propertyPath) { Mode = BindingMode.TwoWay });
            if (tooltip is not null)
                ToolTip.SetTip(option, tooltip);
            return option;
        }

        /// <summary>Карточка-секция: скруглённый фон/граница из темы + заголовок с иконкой и вложенные элементы.</summary>
        private static Control SectionCard(string title, string iconKey, params Control[] children)
        {
            var card = new Border
            {
                CornerRadius = new CornerRadius(UiMetrics.RadiusXl),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(UiMetrics.SectionPad),
                Margin = new Thickness(0, 0, 0, UiMetrics.SectionMarginBottom),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            ThemeBrushes.Bind(card, Border.BackgroundProperty, "CardBackgroundBrush");
            ThemeBrushes.Bind(card, Border.BorderBrushProperty, "BorderColorBrush");
            // Мягкая тень и плавные переходы цвета у секций-карточек.
            UiMetrics.AddSoftShadow(card);
            UiMetrics.AddBrushTransition(card);

            var content = new StackPanel { Spacing = UiMetrics.Gap };

            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 2) };
            header.Children.Add(IconHelper.MakeIcon(iconKey, 16, "TextSecondaryBrush"));
            var titleBlock = new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            ThemeBrushes.Bind(titleBlock, TextBlock.ForegroundProperty, "TextSecondaryBrush");
            header.Children.Add(titleBlock);
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
                Content = ThemedIconAndText(iconKey, text, "TextOnAccentBrush", UiMetrics.Scaled(18), centered: true),
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
                "BorderColorBrush")
            {
                Content = ThemedIconAndText(iconKey, text, "ButtonTextBrush", 16, centered: false),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 0, 0, 2)
            };
            ToolTip.SetTip(btn, tooltip);
            btn.Bind(Button.CommandProperty, new Binding(commandPath));
            return btn;
        }

        /// <summary>
        /// Split-кнопка «Очистка кеша» по аналогии с кнопкой запуска 1С:Предприятие:
        /// основная часть открывает окно очистки кеша (<see cref="CacheCleanWindow"/>)
        /// (с выделенной базой или без неё, если выбрана группа), а правая стрелка «▾»
        /// открывает выпадающее меню с выбором типа кеша и полным окном очистки.
        /// Доступна даже при выбранной группе.
        /// </summary>
        private static Control BuildClearCacheSplitButton()
        {
            var radius = UiMetrics.RadiusLg;

            // Основная часть: открывает окно очистки кеша.
            var main = new PanelButton(
                "SecondaryButtonBackgroundBrush",
                "SecondaryButtonHoverBrush",
                "SecondaryButtonPressedBrush",
                "BorderColorBrush",
                new CornerRadius(radius, 0, 0, radius))
            {
                Content = ThemedIconAndText("IconDelete", LocalizationManager.T("Main.ClearCache"), "ButtonTextBrush", 16, centered: false),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 0, 0, 2)
            };
            ToolTip.SetTip(main, LocalizationManager.T("Main.ClearCacheTooltip"));
            main.Bind(Button.CommandProperty, new Binding("ClearCacheCommand"));

            // Выпадающее меню, привязанное к кнопке-стрелке.
            var menu = new ContextMenu();

            var openDialog = new MenuItem { Header = LocalizationManager.T("Main.CacheCleanOpenDialog") };
            openDialog.Bind(MenuItem.CommandProperty, new Binding("ClearCacheCommand"));
            menu.Items.Add(openDialog);

            var program = new MenuItem { Header = LocalizationManager.T("Main.ClearProgramCache") };
            program.Bind(MenuItem.CommandProperty, new Binding("ClearProgramCacheCommand"));
            menu.Items.Add(program);

            var user = new MenuItem { Header = LocalizationManager.T("Main.ClearUserCache") };
            user.Bind(MenuItem.CommandProperty, new Binding("ClearUserCacheCommand"));
            menu.Items.Add(user);

            menu.Items.Add(new Separator());

            var both = new MenuItem { Header = LocalizationManager.T("Main.ClearCacheBoth") };
            both.Bind(MenuItem.CommandProperty, new Binding("ClearCacheBothCommand"));
            menu.Items.Add(both);

            var arrow = new PanelButton(
                "SecondaryButtonBackgroundBrush",
                "SecondaryButtonHoverBrush",
                "SecondaryButtonPressedBrush",
                "BorderColorBrush",
                new CornerRadius(0, radius, radius, 0))
            {
                Width = 36,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 0, 0, 2)
            };
            var arrowGlyph = new TextBlock
            {
                Text = "▾",
                FontSize = UiMetrics.Scaled(14),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            ThemeBrushes.Bind(arrowGlyph, TextBlock.ForegroundProperty, "ButtonTextBrush");
            arrow.Content = arrowGlyph;
            ToolTip.SetTip(arrow, LocalizationManager.T("Main.ClearCacheTooltip"));
            arrow.ContextMenu = menu;
            arrow.Click += (_, _) => menu.Open(arrow);

            // Объединяем обе части в один визуально цельный контрол.
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            Grid.SetColumn(main, 0);
            Grid.SetColumn(arrow, 1);
            grid.Children.Add(main);
            grid.Children.Add(arrow);
            return grid;
        }

        /// <summary>Содержимое кнопки: иконка + подпись, окрашенные кистью ресурса темы.</summary>
        private static Control ThemedIconAndText(string iconKey, string text, string brushKey, double iconSize, bool centered)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            if (centered)
                sp.HorizontalAlignment = HorizontalAlignment.Center;
            sp.Children.Add(IconHelper.MakeIcon(iconKey, iconSize, brushKey));
            var tb = new TextBlock
            {
                Text = text,
                FontSize = UiMetrics.Scaled(13),
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            ThemeBrushes.Bind(tb, TextBlock.ForegroundProperty, brushKey);
            sp.Children.Add(tb);
            return sp;
        }

        private Control DetailRow(string label, Binding binding)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 5) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var labelBlock = new TextBlock { Text = label, FontSize = 12, Opacity = 0.7 };
            grid.Children.Add(labelBlock);
            Grid.SetColumn(labelBlock, 0);

            var valueBlock = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
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
            private readonly List<IDisposable> _subs = new();
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
                if (Application.Current is not { } app)
                    return;
                _subs.Add(app.GetResourceObservable(key).Subscribe(new BrushSlot(setter, ApplyState)));
            }

            /// <summary>Применяет состояние к фону/границе/прозрачности кнопки.</summary>
            private void ApplyState()
            {
                if (!IsEnabled)
                {
                    Opacity = 0.55;
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

            /// <summary>Передаёт текущее значение ресурса-кисти в слот и перерисовывает состояние.</summary>
            private sealed class BrushSlot : IObserver<object?>
            {
                private readonly Action<IBrush> _setter;
                private readonly Action _onChanged;

                public BrushSlot(Action<IBrush> setter, Action onChanged)
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

        /// <summary>
        /// Сегментная кнопка переключателя (для сегментированного контроля): у выбранного
        /// сегмента акцентная заливка, а иконка/текст — цветом «на акценте»; у невыбранных —
        /// прозрачный фон с приглушённым текстом и hover/pressed-состояниями. Все кисти
        /// берутся из ресурсов темы (перекрашиваются при смене схемы). Если lockOn == true,
        /// активный сегмент нельзя «снять» кликом (поведение как у RadioButton).
        /// </summary>
        private sealed class SegmentButton : ToggleButton, IDisposable
        {
            private readonly List<IDisposable> _subs = new();

            /// <summary>Подписки содержимого: иконка и текст пересоздаются при каждой смене состояния.</summary>
            private readonly List<IDisposable> _contentSubs = new();

            /// <summary>
            /// Освобождает подписки на ресурсы темы. Кнопки тегов пересобираются
            /// при каждом обновлении набора, и без этого каждая пересборка
            /// оставляла бы по пять живых подписок на кнопку.
            /// </summary>
            public void Dispose()
            {
                ReleaseContentSubscriptions();
                foreach (var sub in _subs)
                    sub.Dispose();
                _subs.Clear();
            }

            private void ReleaseContentSubscriptions()
            {
                foreach (var sub in _contentSubs)
                    sub.Dispose();
                _contentSubs.Clear();
            }

            private readonly string _iconKey;
            private readonly string _text;
            private readonly double _iconSize;
            private readonly bool _lockOn;

            private IBrush _hoverBg = Brushes.Transparent;
            private IBrush _pressedBg = Brushes.Transparent;
            private IBrush _accent = Brushes.Transparent;
            private IBrush _accentHover = Brushes.Transparent;
            private IBrush _accentPressed = Brushes.Transparent;

            private bool _hovered;
            private bool _pressed;
            private bool _focused;

            public SegmentButton(string iconKey, string text, string hoverBgKey, string pressedBgKey, bool lockOn = true)
            {
                _iconKey = iconKey;
                _text = text;
                _iconSize = 15;
                _lockOn = lockOn;

                HorizontalContentAlignment = HorizontalAlignment.Center;
                VerticalContentAlignment = VerticalAlignment.Center;
                Cursor = new Cursor(StandardCursorType.Hand);
                MinHeight = 30;
                Padding = new Thickness(12, 5);
                BorderThickness = new Thickness(0);

                // Кастомный шаблон: скруглённый Border + ContentPresenter (без Fluent-хрома).
                Theme = new ControlTheme(typeof(ToggleButton))
                {
                    Setters =
                    {
                        new Setter(TemplatedControl.TemplateProperty, new FuncControlTemplate<SegmentButton>((_, _) =>
                    {
                        var border = new Border { CornerRadius = new CornerRadius(UiMetrics.RadiusSm), BorderThickness = new Thickness(0) };
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
            {
                if (Application.Current is not { } app)
                    return;
                _subs.Add(app.GetResourceObservable(key).Subscribe(new BrushObserver(setter, ApplyState)));
            }

            /// <summary>Собирает содержимое «иконка + текст» с цветом по состоянию выбора.</summary>
            private void UpdateContent()
            {
                // Содержимое пересоздаётся при каждой смене состояния, поэтому
                // подписки прежнего содержимого освобождаются: иначе они копились бы
                // на каждое переключение и жили до конца процесса.
                ReleaseContentSubscriptions();

                var brushKey = IsChecked == true ? "TextOnAccentBrush" : "TextPrimaryBrush";
                var sp = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    VerticalAlignment = VerticalAlignment.Center
                };
                // Пустой ключ означает кнопку без иконки: IconHelper на пустой ключ
                // подставляет запасную папку, и она выглядела бы как настоящая иконка.
                if (!string.IsNullOrEmpty(_iconKey))
                    sp.Children.Add(IconHelper.MakeIcon(_iconKey, _iconSize, brushKey, _contentSubs));
                if (!string.IsNullOrEmpty(_text))
                {
                    var tb = new TextBlock
                    {
                        Text = _text,
                        FontSize = 13,
                        FontWeight = FontWeight.SemiBold,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    var textSub = ThemeBrushes.Bind(tb, TextBlock.ForegroundProperty, brushKey);
                    if (textSub is not null)
                        _contentSubs.Add(textSub);
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
                if (IsChecked == true)
                    Background = _pressed ? _accentPressed : (_hovered ? _accentHover : _accent);
                else
                    Background = _pressed ? _pressedBg : (_hovered ? _hoverBg : Brushes.Transparent);

                if (_focused)
                {
                    BorderBrush = _accent;
                    BorderThickness = new Thickness(2);
                }
                else
                {
                    BorderBrush = Brushes.Transparent;
                    BorderThickness = new Thickness(0);
                }
            }
        }

        /// <summary>Описание колонки списка баз: ключ, заголовок, ширина.</summary>
        private readonly record struct ListColumn(string Key, string Header, double Width);

        /// <summary>Минимум под имя базы: колонка звёздная, но схлопываться ей нельзя.</summary>
        private const double NameColumnMinWidth = 220;

        /// <summary>Ширина колонки звезды «избранное» в заголовке и в строке базы.</summary>
        private static double FavoriteColumnWidth => UiMetrics.Scaled(26);

        /// <summary>Ширина колонки булавки «закреплено» в заголовке и в строке базы.</summary>
        private static double PinColumnWidth => UiMetrics.Scaled(24);

        /// <summary>
        /// Ширина колонки иконки базы: сама иконка и её правый отступ. В заголовке
        /// эта колонка пустая, но она есть, иначе подпись «Название» стояла бы
        /// левее имён строк на ширину иконки.
        /// </summary>
        private static double IconColumnWidth => UiMetrics.RowIconBox + 10;

        /// <summary>Ширина одной кнопки панели инструментов над списком.</summary>
        private static double ToolbarButtonWidth => UiMetrics.Scaled(24);

        /// <summary>Ширина блока кнопок групп: четыре кнопки с промежутками.</summary>
        private static double GroupToolbarWidth => ToolbarButtonWidth * 4 + 6 + UiMetrics.Scaled(6);

        /// <summary>
        /// Номер колонки заголовка с именем базы: компенсатор отступа дерева,
        /// звезда, булавка, иконка.
        /// </summary>
        private const int NameHeaderColumn = 4;

        /// <summary>Номер колонки заголовка с пометкой закрепления.</summary>
        private const int PinHeaderColumn = NameHeaderColumn - 2;

        /// <summary>Номер колонки строки с именем базы: звезда, булавка, иконка.</summary>
        private const int NameRowColumn = 3;

        /// <summary>Минимальная ширина колонки при перетаскивании разделителя.</summary>
        private const double MinColumnWidth = 40;

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

            // Ширина из настроек, а при нуле (настройка ещё не трогалась) свой
            // разумный размер под содержимое колонки.
            void Add(bool visible, string key, string header, double width, double fallback)
            {
                if (visible)
                    columns.Add(new ListColumn(key, LocalizationManager.T(header), width > 0 ? width : fallback));
            }

            Add(_vm.ShowVersionColumn, "Version", "Column.Version", _vm.VersionColumnWidth, 95);
            Add(_vm.ShowConfigurationColumn, "Configuration", "Column.Configuration", _vm.ConfigurationColumnWidth, 140);
            Add(_vm.ShowLaunchModeColumn, "LaunchMode", "Column.LaunchMode", _vm.LaunchModeColumnWidth, 115);
            Add(_vm.ShowServerColumn, "ServerBase", "Column.ServerBase", _vm.ServerColumnWidth, 140);
            Add(_vm.ShowLastLaunchColumn, "LastLaunch", "Column.LastLaunch", _vm.LastLaunchColumnWidth, 115);
            Add(_vm.ShowSizeColumn, "Size", "Column.Size", _vm.SizeColumnWidth, 65);
            return columns;
        }

        /// <summary>Значение колонки для конкретной базы.</summary>
        private static string ColumnValue(Infobase ib, string key) => key switch
        {
            "Version" => ib.PlatformVersion ?? string.Empty,
            "Configuration" => ib.ConfigurationDisplay ?? string.Empty,
            "LaunchMode" => ib.LaunchMode ?? string.Empty,
            "ServerBase" => ib.Connection.Type == ConnectionType.WebServer
                ? (ib.Connection.WebUrl ?? string.Empty)
                : (ib.ServerDatabaseDisplay ?? string.Empty),
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
                // Отступы совпадают с карточкой строки: колонки в обеих сетках
                // прижаты вправо, поэтому заголовки встают над значениями только
                // при одинаковом правом отступе.
                Padding = new Thickness(UiMetrics.PaddingControl, 4),
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

            // Прежние кнопки и подписи держат подписки на ресурсы темы: очистка
            // коллекции детей сама по себе их не отпускает.
            foreach (var subscription in _columnHeaderSubscriptions)
                subscription.Dispose();
            _columnHeaderSubscriptions.Clear();
            _columnHeaderRow.Children.Clear();
            _columnHeaderRow.ColumnDefinitions.Clear();

            var columns = ListColumns();
            // Ширина блока кнопок: четыре кнопки групп при группировке плюс
            // переключатель тегов, который показывается всегда.
            _headerToolbarWidth = (_vm.ShowExpandCollapseButtons ? GroupToolbarWidth : 0) + ToolbarButtonWidth + 2;
            var favoriteWidth = _vm.ShowFavoritesButton ? FavoriteColumnWidth : 0;
            var pinWidth = _vm.ShowPinnedButton ? PinColumnWidth : 0;

            _headerOffsetColumn = new ColumnDefinition { Width = new GridLength(0) };
            _columnHeaderRow.ColumnDefinitions.Add(_headerOffsetColumn);
            _columnHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(favoriteWidth) });
            _columnHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(pinWidth) });
            _columnHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(IconColumnWidth) });
            _columnHeaderRow.ColumnDefinitions.Add(
                new ColumnDefinition { Width = NameColumnLength(), MinWidth = MinColumnWidth });
            foreach (var column in columns)
                _columnHeaderRow.ColumnDefinitions.Add(
                    new ColumnDefinition { Width = new GridLength(column.Width), MinWidth = MinColumnWidth });

            _headerPinMark = null;
            if (_vm.ShowPinnedButton)
            {
                // Значок закрепления в заголовке — только пометка колонки, как в WPF.
                var pinMark = IconHelper.MakeIcon("IconPin", UiMetrics.Scaled(13), "TextSecondaryBrush", _columnHeaderSubscriptions);
                ToolTip.SetTip(pinMark, LocalizationManager.T("Main.Pinned"));
                _columnHeaderRow.Children.Add(pinMark);
                Grid.SetColumn(pinMark, PinHeaderColumn);
                _headerPinMark = pinMark;
            }

            // Кнопки лежат поверх компенсатора и пустых колонок звезды,
            // булавки и иконки: своя колонка сдвинула бы подписи вправо
            // от значений, а колонки заголовка тут ничего не показывают.
            var tools = BuildGroupToolbar();
            _columnHeaderRow.Children.Add(tools);
            Grid.SetColumn(tools, 0);
            Grid.SetColumnSpan(tools, NameHeaderColumn);
            tools.ZIndex = 1;

            var nameHeader = HeaderText(LocalizationManager.T("Column.Name"), _columnHeaderSubscriptions);
            MakeSortableHeader(nameHeader, "Name", LocalizationManager.T("Main.ColumnNameSortTooltip"));
            _columnHeaderRow.Children.Add(nameHeader);
            Grid.SetColumn(nameHeader, NameHeaderColumn);

            _headerColumnIndex.Clear();
            _headerColumnIndex["Name"] = NameHeaderColumn;
            for (var i = 0; i < columns.Count; i++)
                _headerColumnIndex[columns[i].Key] = NameHeaderColumn + 1 + i;

            var nameGrip = BuildResizeGrip("Name", NameHeaderColumn);
            _columnHeaderRow.Children.Add(nameGrip);

            for (var i = 0; i < columns.Count; i++)
            {
                var text = HeaderText(columns[i].Header, _columnHeaderSubscriptions);
                if (columns[i].Key == "LastLaunch")
                    MakeSortableHeader(text, "LastLaunchDate", LocalizationManager.T("Main.ColumnLastLaunchSortTooltip"));
                _columnHeaderRow.Children.Add(text);
                Grid.SetColumn(text, NameHeaderColumn + 1 + i);

                var grip = BuildResizeGrip(columns[i].Key, NameHeaderColumn + 1 + i);
                _columnHeaderRow.Children.Add(grip);
            }

            UpdateListMinWidth();

            QueueHeaderAlign();
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
                panel.Children.Add(HeaderIconButton("IconExpandAll",
                    LocalizationManager.T("Main.ExpandAllGroups"), "ExpandAllGroupsCommand"));
                panel.Children.Add(HeaderIconButton("IconCollapseAll",
                    LocalizationManager.T("Main.CollapseAllGroups"), "CollapseAllGroupsCommand"));
                panel.Children.Add(HeaderIconButton("IconSortAscending",
                    LocalizationManager.T("Main.SortGroupsAscending"), "SortGroupsAscendingCommand"));
                panel.Children.Add(HeaderIconButton("IconSortDescending",
                    LocalizationManager.T("Main.SortGroupsDescending"), "SortGroupsDescendingCommand"));
            }

            panel.Children.Add(BuildTagsInListToggle());
            return panel;
        }

        /// <summary>
        /// Переключатель показа тегов в строках списка. Сделан тем же
        /// сегментным контролом, что и переключатели верхней панели: у Fluent
        /// в нажатом состоянии свой синий фон, чужой для этой темы.
        /// </summary>
        private Control BuildTagsInListToggle()
        {
            var toggle = MakeSegmentToggle("IconTag", LocalizationManager.T("Main.ToggleListTags"));
            toggle.IsChecked = _vm?.ShowTags ?? false;
            toggle.VerticalAlignment = VerticalAlignment.Center;
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
            var button = new Button
            {
                Content = IconHelper.MakeIcon(iconKey, UiMetrics.Scaled(14), "TextSecondaryBrush", _columnHeaderSubscriptions),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(2, 0),
                MinWidth = 0,
                MinHeight = 0,
                Width = ToolbarButtonWidth,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = new Cursor(StandardCursorType.Hand)
            };
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
            var subscription = ThemeBrushes.Bind(line, Border.BackgroundProperty, "BorderColorBrush");
            if (subscription is not null)
                _columnHeaderSubscriptions.Add(subscription);

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

            var width = Math.Max(MinColumnWidth, _resizeStartWidth + e.GetPosition(this).X - _resizeStartX);
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
            double lead = 0;
            for (var i = 1; i < NameHeaderColumn; i++)
                lead += definitions[i].Width.IsAbsolute ? definitions[i].Width.Value : 0;

            var nameWidth = definitions[NameHeaderColumn].Width.IsAbsolute
                ? definitions[NameHeaderColumn].Width.Value
                : NameColumnMinWidth;

            double values = 0;
            for (var i = NameHeaderColumn + 1; i < definitions.Count; i++)
                values += definitions[i].Width.IsAbsolute ? definitions[i].Width.Value : 0;

            _listContent.MinWidth = nameWidth + Math.Max(lead, _headerToolbarWidth)
                + UiMetrics.PaddingControl * 2 + values;
        }

        /// <summary>Делает заголовок колонки кликабельным: клик меняет поле сортировки.</summary>
        private void MakeSortableHeader(TextBlock header, string field, string tooltip)
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
                || _columnHeaderRow.ColumnDefinitions.Count == 0)
                return;

            // Ориентир — самая левая из видимых строк: узлы разной вложенности
            // сдвинуты по-разному, и по первой встреченной заголовок уехал бы
            // вправо от большинства строк.
            double? left = null;
            foreach (var card in _tree.GetVisualDescendants().OfType<InfobaseRowCard>())
            {
                if (card.Child is not { } content)
                    continue;
                var origin = content.TranslatePoint(new Point(0, 0), _columnHeaderRow);
                if (origin is null)
                    continue;
                if (left is null || origin.Value.X < left.Value)
                    left = origin.Value.X;
            }
            if (left is null)
                return;

            // Пустые колонки звезды, булавки и иконки заголовка кнопки перекрывают,
            // а на подпись «Название» налезать не должны, отсюда нижняя граница.
            var lead = _columnHeaderRow.ColumnDefinitions[1].ActualWidth
                + _columnHeaderRow.ColumnDefinitions[2].ActualWidth
                + _columnHeaderRow.ColumnDefinitions[3].ActualWidth;
            var offset = Math.Max(Math.Max(0, _headerToolbarWidth - lead), left.Value);
            if (Math.Abs(offset - _headerOffsetColumn.Width.Value) > 0.5)
                _headerOffsetColumn.Width = new GridLength(offset);

            // Пометка булавки прячется, когда её место занял блок кнопок.
            if (_headerPinMark is not null)
                _headerPinMark.IsVisible = offset + _columnHeaderRow.ColumnDefinitions[1].ActualWidth
                    >= _headerToolbarWidth;
        }

        private static TextBlock HeaderText(string text, ICollection<IDisposable>? subscriptions = null)
        {
            var block = new TextBlock
            {
                Text = text,
                FontSize = UiMetrics.Scaled(12),
                FontWeight = FontWeight.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            var subscription = ThemeBrushes.Bind(block, TextBlock.ForegroundProperty, "TextSecondaryBrush");
            if (subscription is not null)
                subscriptions?.Add(subscription);
            return block;
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
                Content = ThemedIconAndText("IconClose", LocalizationManager.T("Main.ClearTagFilters"),
                    "ButtonTextBrush", UiMetrics.Scaled(12), centered: false),
                Padding = new Thickness(8, 2),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            _tagClearButton.Bind(Button.CommandProperty, new Binding("ClearTagFiltersCommand"));

            // Подсказка остаётся на месте и когда тегов нет: панель не прячется,
            // иначе переключатель «теги» выглядел бы неработающим. Раскладка как
            // в WPF-версии: подсказка и кнопка сброса сверху, чипы тегов под ними.
            var hint = ThemedIconAndText("IconTag", LocalizationManager.T("Main.TagFilterTitle"),
                "TextSecondaryBrush", UiMetrics.Scaled(12), centered: false);
            hint.HorizontalAlignment = HorizontalAlignment.Left;

            var header = new Grid();
            header.Children.Add(hint);
            header.Children.Add(_tagClearButton);

            var rows = new StackPanel { Orientation = Orientation.Vertical, Spacing = 6 };
            rows.Children.Add(header);
            rows.Children.Add(_tagPanelItems);

            _tagPanel = new Border
            {
                Padding = new Thickness(UiMetrics.TopBarH, 6),
                BorderThickness = new Thickness(0, 0, 0, 1),
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
                var button = new SegmentButton("IconTag", item.Name, "ItemHoverBrush", "ItemSelectedBrush")
                {
                    Margin = new Thickness(0, 0, 4, 0),
                    IsChecked = item.IsSelected
                };
                button.Click += (_, _) => _vm.SearchByTagCommand.Execute(item.Name);
                _tagPanelItems.Children.Add(button);
            }

            _tagClearButton.IsVisible = _vm.HasActiveTagFilter;
            _tagPanel.IsVisible = _vm.ShowTagFilterPanel;
        }

        private Control BuildStatusBar()
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _statusInfo = new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            _statusInfo.Bind(TextBlock.TextProperty, new Binding("StatusBarInfo"));
            grid.Children.Add(_statusInfo);
            Grid.SetColumn(_statusInfo, 0);

            _syncMessage = new TextBlock { FontSize = 12, Margin = new Thickness(16, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            _syncMessage.Bind(TextBlock.TextProperty, new Binding("SyncMessage"));
            grid.Children.Add(_syncMessage);
            Grid.SetColumn(_syncMessage, 1);

            var toggleBtn = new Button { Content = IconHelper.MakeIcon("IconPanel", 16), Margin = new Thickness(4, 0, 0, 0) };
            ToolTip.SetTip(toggleBtn, LocalizationManager.T("Main.RightPanel"));
            toggleBtn.Bind(Button.CommandProperty, new Binding("ToggleRightPanelDetailsCommand"));
            grid.Children.Add(toggleBtn);
            Grid.SetColumn(toggleBtn, 2);

            return new Border { Child = grid, Name = "StatusBarBorder", Padding = new Thickness(UiMetrics.TopBarH, 6) };
        }

        // ======================= Обработчики =======================

        private void OnWindowLoaded(object? sender, RoutedEventArgs e)
        {
            _vm?.Initialize();
            RegisterHotkeys();
            SetupTray();
        }

        /// <summary>
        /// Применяет компактный режим интерфейса: пересобирает главное окно с уменьшенными
        /// отступами, иконками и расстояниями. Вызывается из окна настроек при переключении.
        /// </summary>
        public void ApplyCompactMode(bool compact)
        {
            UiMetrics.Compact = compact;
            Content = BuildRoot();
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
            var menu = new ContextMenu();
            if (_vm is null)
                return menu;

            var cacheMenu = new MenuItem { Header = LocalizationManager.T("Main.ClearCache") };
            cacheMenu.Items.Add(MenuAction("Main.ClearProgramCache", _vm.ClearProgramCacheCommand));
            cacheMenu.Items.Add(MenuAction("Main.ClearUserCache", _vm.ClearUserCacheCommand));
            cacheMenu.Items.Add(new Separator());
            // Сочетание показано здесь, а не у программного кеша: Ctrl+Shift+C
            // открывает очистку обоих кешей. В WPF подпись стоит у программного,
            // хотя клавиша делает то же самое, что этот пункт.
            cacheMenu.Items.Add(MenuAction("Main.ClearCacheBoth", _vm.ClearCacheBothCommand, _vm.HotkeyClearCache));

            menu.Items.Add(MenuAction("Main.LaunchEnterprise", _vm.LaunchEnterpriseCommand, _vm.HotkeyEnterprise));
            menu.Items.Add(MenuAction("Main.LaunchConfigurator", _vm.LaunchConfiguratorCommand, _vm.HotkeyConfigurator));
            menu.Items.Add(MenuAction("Main.EditSettings", _vm.EditInfobaseCommand, _vm.HotkeyEdit));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuAction("Main.ToFavorites", _vm.ToggleFavoriteCommand, _vm.HotkeyFavorite));
            menu.Items.Add(MenuAction("Main.Pin", _vm.TogglePinCommand, _vm.HotkeyPin));
            menu.Items.Add(cacheMenu);
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuAction("Main.CopyConnectionString", _vm.CopyConnectionStringCommand));
            menu.Items.Add(MenuAction("Main.OpenCatalog", _vm.OpenInfobaseFolderCommand));
            menu.Items.Add(MenuAction("Main.DesktopShortcut", _vm.CreateDesktopShortcutCommand));
            menu.Items.Add(MenuAction("Main.AddBase", _vm.AddInfobaseCommand, _vm.HotkeyAdd));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuAction("Main.Delete", _vm.DeleteInfobaseCommand, _vm.HotkeyDelete));
            return menu;
        }

        /// <summary>Пункт меню с подписью из словаря, командой и подсказкой сочетания клавиш.</summary>
        private static MenuItem MenuAction(string textKey, System.Windows.Input.ICommand command, string? gesture = null)
        {
            var item = new MenuItem
            {
                Header = LocalizationManager.T(textKey),
                Command = command
            };
            if (TryParseGesture(gesture, out var parsed) && parsed is not null)
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
            // Delete в привязки не идёт: он правит текст, и в поле ввода
            // не должен удалять базу. Ему отдельный обработчик ниже.
            AddHotkey(_vm.HotkeyEnterprise, _vm.LaunchEnterpriseCommand);
            AddHotkey(_vm.HotkeyConfigurator, _vm.LaunchConfiguratorCommand);
            AddHotkey(_vm.HotkeyEdit, _vm.EditInfobaseCommand);
            AddHotkey(_vm.HotkeyAdd, _vm.AddInfobaseCommand);
            AddHotkey(_vm.HotkeyFavorite, _vm.ToggleFavoriteCommand);
            AddHotkey(_vm.HotkeyPin, _vm.TogglePinCommand);
            AddHotkey(_vm.HotkeyClearCache, _vm.ClearCacheCommand);
        }

        /// <summary>
        /// Удаление базы по Delete. Клавиша текстовая, поэтому команда
        /// срабатывает, только когда фокус не в поле ввода и событие дошло
        /// до окна необработанным.
        /// </summary>
        private void OnWindowKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Handled || _vm is null)
                return;
            if (e.Key != Key.Delete || e.KeyModifiers != KeyModifiers.None)
                return;
            if (FocusManager?.GetFocusedElement() is TextBox)
                return;

            if (_vm.DeleteInfobaseCommand.CanExecute(null))
                _vm.DeleteInfobaseCommand.Execute(null);
            e.Handled = true;
        }

        private void AddHotkey(string? gesture, System.Windows.Input.ICommand? command)
        {
            if (command is null || !TryParseGesture(gesture, out var parsed) || parsed is null)
                return;
            KeyBindings.Add(new KeyBinding { Gesture = parsed, Command = command });
        }

        /// <summary>
        /// Разбирает сочетание вида «F3», «Ctrl+E», «Ctrl+Shift+C», «Del».
        /// Сокращения Del, Ins и Esc разбору Avalonia неизвестны, поэтому
        /// раскрываются до полных имён клавиш, как это делает WPF-версия.
        /// </summary>
        private static bool TryParseGesture(string? text, out KeyGesture? gesture)
        {
            gesture = null;
            if (string.IsNullOrWhiteSpace(text))
                return false;

            var parts = text.Trim().Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0)
                return false;

            parts[^1] = parts[^1].ToLowerInvariant() switch
            {
                "del" => "Delete",
                "ins" => "Insert",
                "esc" => "Escape",
                _ => parts[^1]
            };

            try
            {
                gesture = KeyGesture.Parse(string.Join("+", parts));
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ======================= Трей =======================

        private void SetupTray()
        {
            try
            {
                var menu = new NativeMenu();

                var showItem = new NativeMenuItem(LocalizationManager.T("Main.ShowWindow"));
                showItem.Click += (_, _) => ShowAndActivate();
                menu.Add(showItem);
                menu.Add(new NativeMenuItemSeparator());

                // Недавние базы (быстрый запуск прямо из трея).
                var recent = _vm?.RecentInfobases;
                if (recent is { Count: > 0 })
                {
                    var recentMenu = new NativeMenu();
                    foreach (var ib in recent)
                    {
                        var item = new NativeMenuItem($"{ib.Name}  ({ib.ServerDatabaseDisplay})");
                        var baseRef = ib;
                        item.Click += (_, _) => LaunchInfobase(baseRef);
                        recentMenu.Add(item);
                    }
                    menu.Add(new NativeMenuItem(LocalizationManager.T("Main.RecentBases")) { Menu = recentMenu });
                    menu.Add(new NativeMenuItemSeparator());
                }

                // Запуск выбранной базы: Предприятие / Конфигуратор.
                if (_vm?.SelectedInfobase is { } sel)
                {
                    var ent = new NativeMenuItem($"{LocalizationManager.T("Main.LaunchEnterprise")}: {sel.Name}");
                    ent.Click += (_, _) => _vm.LaunchEnterpriseCommand.Execute(null);
                    menu.Add(ent);

                    var cfg = new NativeMenuItem($"{LocalizationManager.T("Main.LaunchConfigurator")}: {sel.Name}");
                    cfg.Click += (_, _) => _vm.LaunchConfiguratorCommand.Execute(null);
                    menu.Add(cfg);
                    menu.Add(new NativeMenuItemSeparator());
                }

                // Синхронизация и настройки.
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

                var tray = new TrayIcon
                {
                    Icon = LoadTrayIcon(),
                    ToolTipText = LocalizationManager.T("App.Title"),
                    Menu = menu
                };
                if (Application.Current is { } app)
                    TrayIcon.SetIcons(app, new TrayIcons { tray });

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
        private void LaunchInfobase(Infobase ib)
        {
            if (_vm is null)
                return;
            _vm.SelectedInfobase = ib;
            _vm.LaunchEnterpriseCommand.Execute(null);
        }

        /// <summary>
        /// Загружает иконку трея без System.Drawing — из PNG/ICO на диске либо из
        /// встроенного ресурса (tray_icon_preview.png), через Avalonia WindowIcon.
        /// </summary>
        private static WindowIcon? LoadTrayIcon()
        {
            try
            {
                foreach (var name in new[] { "tray_icon_preview.png", "app_icon_preview.png", "app.ico", "tray.ico" })
                {
                    foreach (var dir in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
                    {
                        var path = System.IO.Path.Combine(dir, name);
                        if (File.Exists(path))
                            return new WindowIcon(new Bitmap(path));
                    }

                    // Встроенный ресурс (добавлен как EmbeddedResource в Linux-конфигурацию).
                    if (name == "tray_icon_preview.png")
                    {
                        var asm = Assembly.GetExecutingAssembly();
                        using var stream = asm.GetManifestResourceStream(name);
                        if (stream is not null)
                            return new WindowIcon(new Bitmap(stream));
                    }
                }
            }
            catch
            {
                // иконка не обязательна — трей будет без иконки/с иконкой по умолчанию
            }
            return null;
        }

        /// <summary>
        /// Закрытие окна уводит приложение в трей, а не завершает его
        /// (свойство «закрытие в трей»). Реальный выход — команда «Выход».
        /// </summary>
        protected override void OnClosing(WindowClosingEventArgs e)
        {
            base.OnClosing(e);
            if (_allowCloseToTray && _vm is not null)
            {
                e.Cancel = true;
                Hide();
            }
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