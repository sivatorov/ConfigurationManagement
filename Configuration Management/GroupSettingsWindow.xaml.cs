using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Configuration_Management.Models;
using Configuration_Management.ViewModels;

namespace Configuration_Management
{
    /// <summary>
    /// Диалог управления группами информационных баз.
    /// Поддерживает иерархическую структуру групп (корневые и вложенные группы).
    /// </summary>
    public partial class GroupSettingsWindow : Window
    {
        private readonly ObservableCollection<Group> _groups;

        /// <summary>
        /// Создаёт диалог управления группами.
        /// </summary>
        /// <param name="groups">Текущий список групп.</param>
        public GroupSettingsWindow(IEnumerable<Group> groups)
        {
            InitializeComponent();
            _groups = new ObservableCollection<Group>(groups);
            RebuildTree();
        }

        /// <summary>
        /// Возвращает итоговый список групп (плоский, с сохранённой иерархией).
        /// </summary>
        public List<Group> Result => _groups.ToList();

        /// <summary>
        /// Перестраивает дерево групп из плоского списка.
        /// </summary>
        private void RebuildTree()
        {
            GroupsTree.ItemsSource = GroupNodeViewModel.BuildTree(_groups);
        }

        /// <summary>
        /// Возвращает выбранный узел дерева.
        /// </summary>
        private GroupNodeViewModel? SelectedNode =>
            GroupsTree.SelectedItem as GroupNodeViewModel;

        private void OnAddRoot_Click(object sender, RoutedEventArgs e)
        {
            // Новая корневая группа.
            var dialog = new GroupEditWindow(_groups, parent: null)
            {
                Owner = this
            };
            if (dialog.ShowDialog() == true)
            {
                _groups.Add(dialog.Result);
                RebuildTree();
                SelectGroup(dialog.Result);
            }
        }

        private void OnAddSubgroup_Click(object sender, RoutedEventArgs e)
        {
            // Новая подгруппа внутри выбранной группы.
            var parent = SelectedNode?.Group;
            if (parent is null)
            {
                MessageBox.Show(
                    "Выберите группу, внутри которой нужно создать подгруппу.",
                    "Внимание",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var dialog = new GroupEditWindow(_groups, parent)
            {
                Owner = this
            };
            if (dialog.ShowDialog() == true)
            {
                _groups.Add(dialog.Result);
                RebuildTree();
                SelectGroup(dialog.Result);
            }
        }

        private void OnEdit_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedNode?.Group is not Group group)
                return;

            var dialog = new GroupEditWindow(_groups, group.ParentId, group)
            {
                Owner = this
            };
            if (dialog.ShowDialog() == true)
            {
                // Обновляем группу в коллекции, сохраняя ссылку на объект,
                // чтобы не потерять ParentId у дочерних групп.
                var index = _groups.IndexOf(group);
                if (index >= 0)
                {
                    _groups[index] = dialog.Result;
                }

                RebuildTree();
                SelectGroup(dialog.Result);
            }
        }

        private void OnDelete_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedNode?.Group is not Group group)
                return;

            var subgroupCount = _groups.Count(g =>
                string.Equals(g.ParentId, group.Id, StringComparison.OrdinalIgnoreCase));

            var message = $"Удалить группу «{group.Name}»?";
            if (subgroupCount > 0)
            {
                message += $"\n\nВнутри группы находится подгрупп: {subgroupCount}.\n" +
                           "Они также будут удалены.";
            }

            var result = MessageBox.Show(
                message,
                "Подтверждение удаления",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
                return;

            // Собираем все удаляемые группы (саму группу и всех потомков).
            var toRemove = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { group.Id };
            CollectDescendants(group.Id, toRemove);

            _groups.RemoveAllInPlace(g => toRemove.Contains(g.Id));
            RebuildTree();
        }

        /// <summary>
        /// Собирает идентификаторы всех групп-потомков указанной группы.
        /// </summary>
        private void CollectDescendants(string parentId, ISet<string> result)
        {
            var children = _groups
                .Where(g => string.Equals(g.ParentId, parentId, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var child in children)
            {
                if (result.Add(child.Id))
                {
                    CollectDescendants(child.Id, result);
                }
            }
        }

        private void OnDone_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }

        /// <summary>
        /// Выбирает узел с указанной группой в дереве (разворачивая предков).
        /// </summary>
        private void SelectGroup(Group group)
        {
            foreach (var root in GroupsTree.Items.OfType<GroupNodeViewModel>())
            {
                var found = SelectInContainer(GroupsTree, root, group);
                if (found)
                    return;
            }
        }

        /// <summary>
        /// Рекурсивно находит и выбирает узел с указанной группой.
        /// </summary>
        private static bool SelectInContainer(ItemsControl container, GroupNodeViewModel node, Group target)
        {
            // Разворачиваем узел, чтобы его потомки стали доступны.
            node.IsExpanded = true;

            if (node.Group is not null &&
                string.Equals(node.Group.Id, target.Id, StringComparison.OrdinalIgnoreCase))
            {
                var item = container.ItemContainerGenerator.ContainerFromItem(node) as TreeViewItem;
                if (item is not null)
                {
                    item.IsSelected = true;
                    item.BringIntoView();
                }
                return true;
            }

            foreach (var child in node.Children)
            {
                // Получаем контейнер текущего узла для доступа к его ItemsControl.
                var childContainer = container.ItemContainerGenerator.ContainerFromItem(node) as ItemsControl;
                if (childContainer is not null && SelectInContainer(childContainer, child, target))
                {
                    return true;
                }
            }

            return false;
        }

        private static GroupNodeViewModel? FindNode(GroupNodeViewModel root, Group target)
        {
            if (root.Group is not null &&
                string.Equals(root.Group.Id, target.Id, StringComparison.OrdinalIgnoreCase))
            {
                return root;
            }

            foreach (var child in root.Children)
            {
                var found = FindNode(child, target);
                if (found is not null)
                    return found;
            }

            return null;
        }
    }

    /// <summary>
    /// Вспомогательные методы расширения для коллекций.
    /// </summary>
    internal static class ObservableCollectionExtensions
    {
        /// <summary>
        /// Удаляет из коллекции все элементы, удовлетворяющие предикату.
        /// </summary>
        public static void RemoveAllInPlace<T>(this ObservableCollection<T> collection, Func<T, bool> predicate)
        {
            for (var i = collection.Count - 1; i >= 0; i--)
            {
                if (predicate(collection[i]))
                {
                    collection.RemoveAt(i);
                }
            }
        }
    }
}