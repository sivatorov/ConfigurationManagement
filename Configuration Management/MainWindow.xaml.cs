using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Configuration_Management.Models;
using Configuration_Management.Themes;
using Configuration_Management.ViewModels;

namespace Configuration_Management
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _viewModel;

        public MainWindow()
        {
            InitializeComponent();
            _viewModel = new MainViewModel();
            DataContext = _viewModel;

            // Применяем сохранённую тему оформления при запуске.
            if (!string.IsNullOrEmpty(_viewModel.SavedTheme))
            {
                ThemeManager.ApplyTheme(_viewModel.SavedTheme);
            }

            UpdateThemeButton();

            // Применяем сохранённые ширины колонок списка баз.
            ApplySavedColumnWidths();

            // Применяем сохранённые размер, позицию и состояние окна.
            ApplySavedWindowLayout();
        }

        /// <summary>
        /// Восстанавливает сохранённые размер, позицию и состояние окна приложения.
        /// </summary>
        private void ApplySavedWindowLayout()
        {
            var width = _viewModel.SavedWindowWidth;
            var height = _viewModel.SavedWindowHeight;

            if (width > 0 && height > 0)
            {
                // Не допускаем выход окна за пределы рабочей области экрана.
                var area = SystemParameters.WorkArea;
                var left = _viewModel.SavedWindowLeft;
                var top = _viewModel.SavedWindowTop;
                if (left <= 0 && top <= 0)
                {
                    // Если позиция не сохранена — центрируем окно.
                    WindowStartupLocation = WindowStartupLocation.CenterScreen;
                }
                else
                {
                    // Ограничиваем позицию, чтобы окно оставалось видимым.
                    var safeLeft = Math.Max(area.Left, Math.Min(left, area.Right - Math.Min(width, area.Width)));
                    var safeTop = Math.Max(area.Top, Math.Min(top, area.Bottom - Math.Min(height, area.Height)));
                    Left = safeLeft;
                    Top = safeTop;
                }

                Width = width;
                Height = height;
            }

            // Восстанавливаем развёрнутое состояние окна.
            if (Enum.TryParse<WindowState>(_viewModel.SavedWindowState, out var state) &&
                state != WindowState.Minimized)
            {
                WindowState = state;
            }
        }

        /// <summary>
        /// Сохраняет размер, позицию и состояние окна приложения при закрытии.
        /// </summary>
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            // Сохраняем только в обычном состоянии, чтобы не сохранить развёрнутое окно как размер по умолчанию.
            if (WindowState == WindowState.Normal)
            {
                _viewModel.SaveWindowLayout(Width, Height, Left, Top, WindowState.ToString());
            }
            else if (WindowState == WindowState.Maximized)
            {
                _viewModel.SaveWindowLayout(RestoreBounds.Width, RestoreBounds.Height, RestoreBounds.Left, RestoreBounds.Top, WindowState.ToString());
            }

            // Останавливаем автоматическую синхронизацию при закрытии окна.
            _viewModel.StopAutoSync();

            base.OnClosing(e);
        }

        private void OnToggleTheme_Click(object sender, RoutedEventArgs e)
        {
            ThemeManager.ToggleTheme();
            UpdateThemeButton();
            _viewModel.SaveTheme(ThemeManager.CurrentTheme);
        }

        /// <summary>
        /// Выравнивает колонки заголовка по фактическому положению колонки «Название»
        /// первой видимой базы в списке. Это необходимо, потому что при группировке
        /// базы смещаются вправо отступами вложенности дерева, и фиксированный сдвиг
        /// заголовка (рассчитанный для баз верхнего уровня) перестаёт совпадать с данными.
        /// </summary>
        private void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            AlignHeaderToData();
            // Повторное выравнивание после завершения первичной компоновки,
            // когда уже известны реальные размеры контейнеров дерева.
            Dispatcher.BeginInvoke(new Action(AlignHeaderToData), System.Windows.Threading.DispatcherPriority.Loaded);

            // Запускаем автоматическую синхронизацию с файлом ibases.v8i.
            _viewModel.StartAutoSync();
        }

        /// <summary>
        /// Пересчитывает выравнивание заголовка при переключении режима группировки,
        /// когда дерево перестраивается и меняется глубина вложенности баз.
        /// </summary>
        private void OnGroupByToggle_Click(object sender, RoutedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(AlignHeaderToData), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        /// <summary>
        /// Подстраивает ширину колонки-компенсатора заголовка (HeaderOffsetColumn) так,
        /// чтобы колонка «Название» заголовка совпадала с колонкой «Название» первой
        /// видимой строки базы в дереве.
        /// </summary>
        private void AlignHeaderToData()
        {
            if (HeaderOffsetColumn is null || MainTree is null)
                return;

            var item = FindFirstInfobaseItem(MainTree);
            if (item is null)
                return;

            var nameCell = FindNameCell(item);
            if (nameCell is null)
                return;

            // Положение колонки «Название» данных относительно дерева (левого края списка).
            // Заголовок использует тот же левый край, поэтому это значение напрямую
            // задаёт компенсирующую колонку за вычетом двух колонок ★/📌 (26+26).
            var dataX = nameCell.TranslatePoint(new Point(0, 0), MainTree).X;
            var offset = Math.Max(0, dataX - 52);
            HeaderOffsetColumn.Width = new GridLength(offset);
        }

        /// <summary>
        /// Ищет первый реально созданный (видимый) элемент дерева с базой.
        /// </summary>
        private static TreeViewItem? FindFirstInfobaseItem(DependencyObject parent)
        {
            if (parent is TreeViewItem tvi && tvi.DataContext is Infobase)
                return tvi;

            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var result = FindFirstInfobaseItem(VisualTreeHelper.GetChild(parent, i));
                if (result is not null)
                    return result;
            }
            return null;
        }

        /// <summary>
        /// Находит текстовый элемент колонки «Название» (Grid.Column=3) строки базы.
        /// </summary>
        private static TextBlock? FindNameCell(DependencyObject parent)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is TextBlock tb && Grid.GetColumn(tb) == 3)
                    return tb;

                var result = FindNameCell(child);
                if (result is not null)
                    return result;
            }
            return null;
        }

        /// <summary>
        /// Синхронизирует выделение в дереве с выбранной базой.
        /// При выборе группы снимает выделение базы.
        /// </summary>
        private void OnMainTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            // Выбором базы управляет code-behind через обработчики кликов
            // (OnInfobaseTree_PreviewMouseLeftButtonDown), которые явно устанавливают
            // TreeViewItem.IsSelected и SelectedInfobase. Здесь лишь фиксируем результат
            // изменения выбранного элемента, не трогая свойство Infobase.IsSelected.
            // Ранее двухсторонняя привязка IsSelected к модели порождала каскад событий
            // SelectedItemChanged (база дублируется в «Закреплённых» и в своей группе),
            // что приводило к бесконечной рекурсии и StackOverflowException.
            if (e.NewValue is Infobase infobase)
            {
                _viewModel.SelectedInfobase = infobase;
                // Выбор базы снимает выбор группы.
                _viewModel.SelectedGroupNode = null;
            }
            else if (e.NewValue is GroupNodeViewModel groupNode)
            {
                // Выбор группы снимает выбор базы и фиксирует выбранную группу.
                _viewModel.SelectedInfobase = null;
                _viewModel.SelectedGroupNode = groupNode;
            }
            else if (e.NewValue is null)
            {
                _viewModel.SelectedInfobase = null;
                _viewModel.SelectedGroupNode = null;
            }

            // Принудительно пересчитываем состояние кнопок («Изменить», «Удалить» и др.),
            // чтобы они активировались при программной установке выделения в дереве.
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }

        /// <summary>
        /// Открывает выпадающее меню выбора типа клиента при нажатии на стрелку
        /// кнопки запуска 1С:Предприятие.
        /// </summary>
        private void OnLaunchSplitButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.ContextMenu is null)
                return;

            button.ContextMenu.PlacementTarget = button;
            button.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;

            // Открываем меню отложенно, чтобы клик по кнопке не закрыл его сразу.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                button.ContextMenu.IsOpen = true;
            }), System.Windows.Threading.DispatcherPriority.Input);
        }

        private void OnInfobaseTree_PreviewMouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // Двойной клик по группе сворачивает/разворачивает её в зависимости от текущего состояния.
            // Двойной клик по базе по-прежнему запускает 1С.
            var source = e.OriginalSource as DependencyObject;
            var treeViewItem = source is null ? null : FindAncestor<TreeViewItem>(source);
            if (treeViewItem?.DataContext is GroupNodeViewModel groupNode && groupNode.Group is not null)
            {
                _viewModel.ToggleGroupExpandedCommand.Execute(groupNode);
                return;
            }

            if (_viewModel.LaunchEnterpriseCommand.CanExecute(null))
            {
                _viewModel.LaunchEnterpriseCommand.Execute(null);
            }
        }

        /// <summary>
        /// Выделяет базу или группу под курсором при правом клике в дереве,
        /// чтобы команды контекстного меню применялись именно к этому элементу.
        /// </summary>
        private void OnInfobaseTree_PreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var treeView = sender as TreeView;
            if (treeView is null)
            {
                return;
            }

            // Если клик попал по строке базы или группы, выделяем её.
            var source = e.OriginalSource as DependencyObject;
            var treeViewItem = source is null ? null : FindAncestor<TreeViewItem>(source);
            switch (treeViewItem?.DataContext)
            {
                case Infobase infobase:
                    treeViewItem.IsSelected = true;
                    _viewModel.SelectedInfobase = infobase;
                    break;
                case GroupNodeViewModel groupNode when groupNode.Group is not null:
                    treeViewItem.IsSelected = true;
                    _viewModel.SelectedInfobase = null;
                    _viewModel.SelectedGroupNode = groupNode;
                    break;
            }
        }

        /// <summary>
        /// Выделяет базу или группу под курсором при левом клике в дереве.
        /// Сами устанавливаем выбор и помечаем событие обработанным, чтобы
        /// собственная логика TreeView не сбросила выделение. Клики по
        /// интерактивным элементам строки (кнопки, поле ввода) не
        /// перехватываются, чтобы они продолжали работать.
        /// </summary>
        private void OnInfobaseTree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var treeView = sender as TreeView;
            if (treeView is null)
            {
                return;
            }

            var source = e.OriginalSource as DependencyObject;
            if (source is null)
            {
                return;
            }

            // Если клик пришёлся по интерактивному элементу (кнопка, поле ввода),
            // не вмешиваемся, чтобы они продолжали работать.
            if (FindAncestor<Button>(source) is not null ||
                FindAncestor<TextBox>(source) is not null)
            {
                return;
            }

            var treeViewItem = FindAncestor<TreeViewItem>(source);
            if (treeViewItem is null)
            {
                return;
            }

            switch (treeViewItem.DataContext)
            {
                case Infobase infobase:
                    ApplySelection(treeViewItem, infobase);
                    break;
                case GroupNodeViewModel groupNode when groupNode.Group is not null:
                    ApplyGroupSelection(treeViewItem, groupNode);
                    break;
                default:
                    return;
            }

            // Помечаем клик обработанным, чтобы TreeView не сбросил выбранный элемент.
            e.Handled = true;
        }

        /// <summary>
        /// Устанавливает выделение указанного узла группы и синхронизирует
        /// выбранную группу в модели представления (снимая выбор базы).
        /// </summary>
        private void ApplyGroupSelection(TreeViewItem item, GroupNodeViewModel groupNode)
        {
            item.IsSelected = true;
            item.Focus();
            _viewModel.SelectedInfobase = null;
            _viewModel.SelectedGroupNode = groupNode;

            // Принудительно пересчитываем состояние кнопок («Изменить», «Удалить» и др.),
            // т.к. программная установка выделения не всегда гарантирует автоматический
            // пересчёт CanExecute команд через CommandManager.
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }

        /// <summary>
        /// Устанавливает выделение указанного элемента дерева и синхронизирует
        /// выбранную базу в модели представления.
        /// </summary>
        private void ApplySelection(TreeViewItem item, Infobase infobase)
        {
            item.IsSelected = true;
            item.Focus();
            _viewModel.SelectedInfobase = infobase;
        }

        /// <summary>
        /// Ищет предка заданного типа в визуальном дереве.
        /// </summary>
        private static T? FindAncestor<T>(DependencyObject current) where T : DependencyObject
        {
            while (current is not null)
            {
                if (current is T typed)
                    return typed;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        /// <summary>
        /// Показывает поле ввода тега прямо в строке названия базы.
        /// </summary>
        private void OnAddTagInline_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button)
                return;

            // InlineTagBox находится в том же StackPanel, что и кнопка «+ тег»,
            // поэтому ищем его через общий предок TreeViewItem.
            var treeViewItem = FindAncestor<TreeViewItem>(button);
            var tagBox = treeViewItem is null ? null : FindVisualChild<TextBox>(treeViewItem);
            if (tagBox is null)
                return;

            // Скрываем кнопку «+ тег» и показываем поле ввода на её месте.
            button.Visibility = Visibility.Collapsed;
            tagBox.Text = string.Empty;
            tagBox.Visibility = Visibility.Visible;
            tagBox.Focus();
            Keyboard.Focus(tagBox);
        }

        /// <summary>
        /// Удаляет тег из базы при нажатии на кнопку «✕» у тега.
        /// </summary>
        private void OnRemoveTag_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button)
                return;

            // База определяется через общий предок TreeViewItem.
            var treeViewItem = FindAncestor<TreeViewItem>(button);
            if (treeViewItem?.DataContext is not Infobase infobase)
                return;

            // Тег — это DataContext кнопки (кнопка находится в ItemsControl.ItemTemplate тегов).
            if (button.DataContext is not string tag)
                return;

            if (_viewModel.RemoveTagCommand.CanExecute(null))
            {
                _viewModel.RemoveTagCommand.Execute(new object[] { infobase, tag });
            }
        }

        /// <summary>
        /// Обрабатывает нажатие Enter в поле ввода тега: добавляет тег и скрывает поле.
        /// </summary>
        private void OnInlineTagBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
                return;

            CommitInlineTag(sender as TextBox);
            e.Handled = true;
        }

        /// <summary>
        /// При потере фокуса полем ввода тега добавляет тег и скрывает поле.
        /// </summary>
        private void OnInlineTagBox_LostFocus(object sender, RoutedEventArgs e)
        {
            CommitInlineTag(sender as TextBox);
        }

        /// <summary>
        /// Добавляет введённый тег к базе и скрывает поле ввода.
        /// </summary>
        private void CommitInlineTag(TextBox? tagBox)
        {
            if (tagBox is null)
                return;

            var infobase = tagBox.DataContext;
            var tag = tagBox.Text?.Trim() ?? string.Empty;

            // Скрываем поле ввода и возвращаем кнопку «+ тег».
            tagBox.Visibility = Visibility.Collapsed;

            // Кнопка «+ тег» находится рядом с полем ввода в одном StackPanel,
            // поэтому ищем её через общего предка TreeViewItem.
            var treeViewItem = FindAncestor<TreeViewItem>(tagBox);
            var addButton = treeViewItem is null
                ? null
                : FindVisualChildByName<Button>(treeViewItem, "AddTagButton");
            if (addButton is not null)
            {
                addButton.Visibility = Visibility.Visible;
            }

            if (string.IsNullOrEmpty(tag) || infobase is null)
                return;

            if (_viewModel.AddTagInlineCommand.CanExecute(null))
            {
                _viewModel.AddTagInlineCommand.Execute(new object[] { infobase, tag });
            }
        }

        /// <summary>
        /// Ищет дочерний элемент заданного типа в визуальном дереве.
        /// </summary>
        private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typedChild)
                    return typedChild;

                var result = FindVisualChild<T>(child);
                if (result is not null)
                    return result;
            }
            return null;
        }

        /// <summary>
        /// Ищет дочерний элемент заданного типа с указанным именем в визуальном дереве.
        /// </summary>
        private static T? FindVisualChildByName<T>(DependencyObject parent, string name) where T : FrameworkElement
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typedChild && typedChild.Name == name)
                    return typedChild;

                var result = FindVisualChildByName<T>(child, name);
                if (result is not null)
                    return result;
            }
            return null;
        }

        /// <summary>
        /// Применяет сохранённые ширины колонок списка баз.
        /// </summary>
        private void ApplySavedColumnWidths()
        {
            SetColumnWidth(NameColumn, _viewModel.NameColumnWidth);
            SetColumnWidth(VersionColumn, _viewModel.VersionColumnWidth);
            SetColumnWidth(LaunchModeColumn, _viewModel.LaunchModeColumnWidth);
            SetColumnWidth(ServerColumn, _viewModel.ServerColumnWidth);
            SetColumnWidth(LastLaunchColumn, _viewModel.LastLaunchColumnWidth);
        }

        /// <summary>
        /// Устанавливает ширину колонки, если задано значение больше нуля.
        /// </summary>
        private static void SetColumnWidth(ColumnDefinition? column, double width)
        {
            if (column is null || width <= 0)
                return;
            column.Width = new GridLength(width);
        }

        // Поля для ручного перетаскивания разделителя колонок.
        private ColumnDefinition? _resizeColumn;
        private double _resizeStartWidth;
        private Point _resizeStartMouse;

        /// <summary>
        /// Определяет колонку, ширину которой меняет данный разделитель.
        /// Разделитель в Grid.Column=N расположен слева от колонки N, поэтому он меняет колонку N-1.
        /// </summary>
        private ColumnDefinition? GetSplitterTargetColumn(object sender)
        {
            if (ReferenceEquals(sender, VersionSplitter))
                return NameColumn;
            if (ReferenceEquals(sender, LaunchModeSplitter))
                return VersionColumn;
            if (ReferenceEquals(sender, ServerSplitter))
                return LaunchModeColumn;
            if (ReferenceEquals(sender, LastLaunchSplitter))
                return ServerColumn;
            return null;
        }

        /// <summary>
        /// Начинает перетаскивание разделителя: захватывает мышь и запоминает стартовые значения.
        /// </summary>
        private void OnColumnResize_MouseDown(object sender, MouseButtonEventArgs e)
        {
            var column = GetSplitterTargetColumn(sender);
            if (column is null)
                return;

            _resizeColumn = column;
            _resizeStartWidth = column.ActualWidth;
            _resizeStartMouse = e.GetPosition(this);

            if (sender is UIElement element)
                element.CaptureMouse();

            e.Handled = true;
        }

        /// <summary>
        /// Меняет ширину только целевой колонки при движении мыши.
        /// Соседние колонки не затрагиваются: разность впитывает последняя гибкая колонка (*).
        /// </summary>
        private void OnColumnResize_MouseMove(object sender, MouseEventArgs e)
        {
            if (_resizeColumn is null || sender is not UIElement element || !element.IsMouseCaptured)
                return;

            var current = e.GetPosition(this);
            var delta = current.X - _resizeStartMouse.X;

            var newWidth = _resizeStartWidth + delta;
            if (newWidth < 40)
                newWidth = 40;

            _resizeColumn.Width = new GridLength(newWidth);

            _viewModel.UpdateColumnWidths(
                NameColumn?.ActualWidth ?? 0,
                VersionColumn?.ActualWidth ?? 0,
                LaunchModeColumn?.ActualWidth ?? 0,
                ServerColumn?.ActualWidth ?? 0,
                LastLaunchColumn?.ActualWidth ?? 0);
        }

        /// <summary>
        /// Завершает перетаскивание разделителя и сохраняет ширины колонок.
        /// </summary>
        private void OnColumnResize_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is UIElement element)
                element.ReleaseMouseCapture();

            if (_resizeColumn is not null)
            {
                _viewModel.SaveColumnWidths(
                    NameColumn?.ActualWidth ?? 0,
                    VersionColumn?.ActualWidth ?? 0,
                    LaunchModeColumn?.ActualWidth ?? 0,
                    ServerColumn?.ActualWidth ?? 0,
                    LastLaunchColumn?.ActualWidth ?? 0);
            }

            _resizeColumn = null;
            e.Handled = true;
        }

        private void UpdateThemeButton()
        {
            if (ThemeToggleButton is null)
                return;

            ThemeToggleButton.Content = ThemeManager.CurrentTheme == ThemeManager.DarkThemeName
                ? "☀️ Светлая"
                : "🌙 Тёмная";
        }

        /// <summary>
        /// Прокручивает список баз колесом мыши.
        /// Внутренний ScrollViewer дерева выключен, поэтому колесо мыши нужно
        /// обрабатывать на внешнем контейнере, иначе событие перехватывается
        /// вложенными элементами и прокрутка не срабатывает.
        /// </summary>
        private void OnDbList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            var scrollViewer = sender as ScrollViewer;
            if (scrollViewer is null)
                return;

            // Если содержимое не превышает размер контейнера — прокручивать нечего.
            if (scrollViewer.ScrollableHeight <= 0)
                return;

            var delta = e.Delta > 0 ? -1 : 1;
            // Умножаем, чтобы прокрутка была плавной (значение сравнимой с обычной прокруткой).
            scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset + delta * 48);

            // Не даём вложенным ScrollViewer (например, внутри TreeView) перехватить событие.
            e.Handled = true;
        }

        /// <summary>
        /// Прокрутка колесом мыши над фиксированным заголовком таблицы.
        /// Сам заголовок не прокручивается, а событие перенаправляется на список баз,
        /// чтобы колесо работало и над областью заголовков.
        /// </summary>
        private void OnDbHeader_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (DbListScroll is null || DbListScroll.ScrollableHeight <= 0)
                return;

            var delta = e.Delta > 0 ? -1 : 1;
            DbListScroll.ScrollToVerticalOffset(DbListScroll.VerticalOffset + delta * 48);

            e.Handled = true;
        }
    }
}