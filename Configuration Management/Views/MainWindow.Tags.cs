#if WINDOWS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.IO;
using MaterialDesignThemes.Wpf;
using Configuration_Management.Localization;
using Configuration_Management.Models;
using Configuration_Management.Services;
using Configuration_Management.Themes;
using Configuration_Management.ViewModels;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using Point = System.Windows.Point;

namespace Configuration_Management
{
    public partial class MainWindow
    {

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
            var tagBox = treeViewItem is null ? null : FindVisualChild<ComboBox>(treeViewItem);
            if (tagBox is null)
                return;

            // Скрываем кнопку «+ тег» и показываем поле ввода на её месте.
            button.Visibility = Visibility.Collapsed;
            tagBox.Text = string.Empty;
            tagBox.SelectedItem = null;
            tagBox.Visibility = Visibility.Visible;
            tagBox.Focus();
            Keyboard.Focus(tagBox);

            // Текст всегда пуст, но выделение во внутреннем поле — единый вид при
            // повторных открытиях. Шаблон ComboBox применяется уже после показа,
            // поэтому SelectAll откладываем до обработки разметки.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (tagBox.Template?.FindName("PART_EditableTextBox", tagBox) is TextBox editBox)
                    editBox.SelectAll();
            }), System.Windows.Threading.DispatcherPriority.Input);
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
            if (e.Key == Key.Escape)
            {
                CancelInlineTag(sender as ComboBox);
                e.Handled = true;
                return;
            }

            if (e.Key != Key.Enter)
                return;

            CommitInlineTag(sender as ComboBox);
            e.Handled = true;
        }

        /// <summary>
        /// Выбор существующего тега из выпадающего списка: добавляет тег сразу.
        /// При навигации стрелками список остаётся открытым, а Esc откатывает
        /// выделение — коммитим только завершённый выбор (список уже закрыт).
        /// </summary>
        private void OnInlineTagBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is not ComboBox { Visibility: Visibility.Visible } combo)
                return;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (combo.Visibility != Visibility.Visible || combo.IsDropDownOpen)
                    return;

                if (combo.SelectedItem is string tag && !string.IsNullOrWhiteSpace(tag))
                    CommitInlineTag(combo);
            }), System.Windows.Threading.DispatcherPriority.Input);
        }

        /// <summary>
        /// При потере фокуса полем ввода тега — сохраняем непустой тег и всегда скрываем поле.
        /// </summary>
        private void OnInlineTagBox_LostFocus(object sender, RoutedEventArgs e)
        {
            // Dispatcher: клик вне поля сначала переводит фокус, затем обрабатываем.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (sender is ComboBox { Visibility: Visibility.Visible } box)
                    CommitInlineTag(box);
            }), System.Windows.Threading.DispatcherPriority.Input);
        }

        /// <summary>Скрывает поле тега без добавления (Esc).</summary>
        private void CancelInlineTag(ComboBox? tagBox)
        {
            if (tagBox is null) return;
            tagBox.Text = string.Empty;
            tagBox.SelectedItem = null;
            HideInlineTagBox(tagBox);
        }

        private void HideInlineTagBox(ComboBox tagBox)
        {
            tagBox.Visibility = Visibility.Collapsed;
            var treeViewItem = FindAncestor<TreeViewItem>(tagBox);
            var addButton = treeViewItem is null
                ? null
                : FindVisualChildByName<Button>(treeViewItem, "AddTagButton");
            if (addButton is not null)
                addButton.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// Добавляет введённый тег к базе и скрывает поле ввода. Текст берётся
        /// из свободного ввода (Text): выбранный из списка тег туда уже попал.
        /// </summary>
        private void CommitInlineTag(ComboBox? tagBox)
        {
            if (tagBox is null || tagBox.Visibility != Visibility.Visible)
                return;

            var infobase = tagBox.DataContext;
            var tag = tagBox.Text?.Trim() ?? string.Empty;

            HideInlineTagBox(tagBox);
            tagBox.Text = string.Empty;
            tagBox.SelectedItem = null;

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

    }
}
#endif
