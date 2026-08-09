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
        }

        private void OnToggleTheme_Click(object sender, RoutedEventArgs e)
        {
            ThemeManager.ToggleTheme();
            UpdateThemeButton();
            _viewModel.SaveTheme(ThemeManager.CurrentTheme);
        }

        /// <summary>
        /// Синхронизирует выделение в дереве с выбранной базой.
        /// При выборе группы снимает выделение базы.
        /// </summary>
        private void OnMainTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is Infobase infobase)
            {
                _viewModel.SelectedInfobase = infobase;
            }
            else
            {
                _viewModel.SelectedInfobase = null;
            }
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

        /// <summary>
        /// Открывает выпадающее меню дополнительных функций
        /// (экспорт и загрузка списка баз).
        /// </summary>
        private void OnExtraFunctions_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.ContextMenu is null)
                return;

            button.ContextMenu.PlacementTarget = button;
            button.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                button.ContextMenu.IsOpen = true;
            }), System.Windows.Threading.DispatcherPriority.Input);
        }

        private void OnInfobaseTree_PreviewMouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_viewModel.LaunchEnterpriseCommand.CanExecute(null))
            {
                _viewModel.LaunchEnterpriseCommand.Execute(null);
            }
        }

        /// <summary>
        /// Выделяет базу под курсором при правом клике в дереве,
        /// чтобы команды контекстного меню применялись именно к этой базе.
        /// </summary>
        private void OnInfobaseTree_PreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var treeView = sender as TreeView;
            if (treeView is null)
            {
                return;
            }

            // Если клик попал по строке базы, выделяем её.
            var source = e.OriginalSource as DependencyObject;
            var treeViewItem = source is null ? null : FindAncestor<TreeViewItem>(source);
            if (treeViewItem?.DataContext is Infobase infobase)
            {
                treeViewItem.IsSelected = true;
                _viewModel.SelectedInfobase = infobase;
            }
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
            // поэтому ищем его через общий предок ListBoxItem.
            var listBoxItem = FindAncestor<ListBoxItem>(button);
            var tagBox = listBoxItem is null ? null : FindVisualChild<TextBox>(listBoxItem);
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

            // База определяется через общий предок ListBoxItem.
            var listBoxItem = FindAncestor<ListBoxItem>(button);
            if (listBoxItem?.DataContext is not Infobase infobase)
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
            // поэтому ищем её через общего предка ListBoxItem.
            var listBoxItem = FindAncestor<ListBoxItem>(tagBox);
            var addButton = listBoxItem is null
                ? null
                : FindVisualChildByName<Button>(listBoxItem, "AddTagButton");
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

        /// <summary>
        /// Синхронизирует ширины колонок строк списка при перетаскивании разделителя.
        /// </summary>
        private void OnColumnSplitter_DragDelta(object sender, DragDeltaEventArgs e)
        {
            _viewModel.UpdateColumnWidths(
                NameColumn?.ActualWidth ?? 0,
                VersionColumn?.ActualWidth ?? 0,
                LaunchModeColumn?.ActualWidth ?? 0,
                ServerColumn?.ActualWidth ?? 0);
        }

        /// <summary>
        /// Сохраняет ширины колонок списка баз после изменения размера разделителем.
        /// </summary>
        private void OnColumnSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            _viewModel.SaveColumnWidths(
                NameColumn?.ActualWidth ?? 0,
                VersionColumn?.ActualWidth ?? 0,
                LaunchModeColumn?.ActualWidth ?? 0,
                ServerColumn?.ActualWidth ?? 0);
        }

        private void UpdateThemeButton()
        {
            if (ThemeToggleButton is null)
                return;

            ThemeToggleButton.Content = ThemeManager.CurrentTheme == ThemeManager.DarkThemeName
                ? "☀️ Светлая"
                : "🌙 Тёмная";
        }
    }
}