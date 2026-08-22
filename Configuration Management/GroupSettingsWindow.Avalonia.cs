#if LINUX
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls.Primitives;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Configuration_Management.Localization;
using Configuration_Management.Models;
using Configuration_Management.Services;
using Configuration_Management.ViewModels;

namespace Configuration_Management
{
    /// <summary>
    /// Диалог управления группами информационных баз. Поддерживает иерархическую
    /// структуру групп (корневые и вложенные группы). Avalonia/Linux-версия WPF-окна
    /// <see cref="GroupSettingsWindow"/>.
    /// </summary>
    public class GroupSettingsWindow : ModalWindowBase
    {
        private readonly ObservableCollection<Group> _groups;
        private readonly IDialogService _dialogs;
        private readonly TreeView _tree = new();
        private readonly Button _doneButton = new() { Content = LocalizationManager.T("GroupSettings.Done"), MinWidth = 110, IsDefault = true };

        /// <summary>
        /// Создаёт диалог управления группами.
        /// </summary>
        /// <param name="groups">Текущий список групп.</param>
        public GroupSettingsWindow(IEnumerable<Group> groups)
        {
            Title = LocalizationManager.T("GroupSettings.Title");
            Width = 540;
            Height = 560;
            MinWidth = 480;
            MinHeight = 460;

            _groups = new ObservableCollection<Group>(groups);
            _dialogs = AppServices.GetRequiredService<IDialogService>();

            Content = BuildRoot();
            RebuildTree();
        }

        /// <summary>
        /// Возвращает итоговый список групп (плоский, с сохранённой иерархией).
        /// </summary>
        public List<Group> Result => _groups.ToList();

        /// <summary>Перестраивает дерево групп из плоского списка.</summary>
        private void RebuildTree()
        {
            _tree.ItemsSource = GroupNodeViewModel.BuildTree(_groups);
        }

        private GroupNodeViewModel? SelectedNode =>
            _tree.SelectedItem as GroupNodeViewModel;

        private Control BuildRoot()
        {
            var grid = new Grid { Margin = new Thickness(16) };
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            grid.RowDefinitions.Add(new RowDefinition(new GridLength(1, GridUnitType.Star)));
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            var header = new TextBlock
            {
                Text = LocalizationManager.T("GroupSettings.GroupsHeader"),
                FontSize = 15,
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(header, 0);
            grid.Children.Add(header);

            _tree.SelectionMode = SelectionMode.Single;
            _tree.ItemTemplate = new FuncTreeDataTemplate(
                typeof(object),
                (item, _) => BuildTreeRow(item),
                item => item is GroupNodeViewModel g ? g.Children : null);

            var treeBorder = new Border
            {
                Child = new ScrollViewer
                {
                    Content = _tree,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Padding = new Thickness(8, 8)
                },
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6)
            };
            Grid.SetRow(treeBorder, 1);
            grid.Children.Add(treeBorder);

            // Кнопки управления
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0), Spacing = 8 };
            actions.Children.Add(MakeButton(LocalizationManager.T("GroupSettings.AddRoot"), "IconAdd", OnAddRoot_Click));
            actions.Children.Add(MakeButton(LocalizationManager.T("GroupSettings.AddSubgroup"), "IconAdd", OnAddSubgroup_Click));
            actions.Children.Add(MakeButton(LocalizationManager.T("Common.Edit"), "IconEdit", OnEdit_Click));
            actions.Children.Add(MakeButton(LocalizationManager.T("Common.Delete"), "IconDelete", OnDelete_Click));
            Grid.SetRow(actions, 2);
            grid.Children.Add(actions);

            // Нижняя панель
            var bottom = new Grid { Margin = new Thickness(0, 12, 0, 0) };
            bottom.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            bottom.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            _doneButton.Click += (_, _) => { DialogResult = true; Close(); };
            Grid.SetColumn(_doneButton, 1);
            bottom.Children.Add(_doneButton);
            Grid.SetRow(bottom, 3);
            grid.Children.Add(bottom);

            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            return grid;
        }

        private static Button MakeButton(string text, string iconKey, Action onClick)
        {
            var button = new Button { Content = IconHelper.IconAndText(iconKey, text) };
            button.Click += (_, _) => onClick();
            return button;
        }

        private static Control BuildTreeRow(object? item)
        {
            if (item is GroupNodeViewModel node)
            {
                return new TextBlock { Text = node.DisplayName, Margin = new Thickness(4, 2), VerticalAlignment = VerticalAlignment.Center };
            }
            return new TextBlock { Text = item?.ToString() ?? string.Empty };
        }

        private void OnAddRoot_Click()
        {
            var dialog = new GroupEditWindow(_groups, parent: null);
            if (dialog.ShowDialogSync(this))
            {
                _groups.Add(dialog.Result);
                RebuildTree();
                SelectGroup(dialog.Result);
            }
        }

        private void OnAddSubgroup_Click()
        {
            var parent = SelectedNode?.Group;
            if (parent is null)
            {
                _dialogs.ShowInfo(LocalizationManager.T("GroupSettings.NoGroupSelected"), LocalizationManager.T("Common.Warning"));
                return;
            }

            var dialog = new GroupEditWindow(_groups, parent);
            if (dialog.ShowDialogSync(this))
            {
                _groups.Add(dialog.Result);
                RebuildTree();
                SelectGroup(dialog.Result);
            }
        }

        private void OnEdit_Click()
        {
            if (SelectedNode?.Group is not Group group)
                return;

            var dialog = new GroupEditWindow(_groups, group.ParentId, group);
            if (dialog.ShowDialogSync(this))
            {
                var index = _groups.IndexOf(group);
                if (index >= 0)
                {
                    _groups[index] = dialog.Result;
                }

                RebuildTree();
                SelectGroup(dialog.Result);
            }
        }

        private void OnDelete_Click()
        {
            if (SelectedNode?.Group is not Group group)
                return;

            var subgroupCount = _groups.Count(g =>
                string.Equals(g.ParentId, group.Id, StringComparison.OrdinalIgnoreCase));

            var message = string.Format(LocalizationManager.T("GroupSettings.DeleteConfirm"), group.Name);
            if (subgroupCount > 0)
            {
                message += string.Format(LocalizationManager.T("GroupSettings.DeleteHasSubgroups"), subgroupCount);
            }

            if (!_dialogs.Confirm(message, LocalizationManager.T("GroupSettings.DeleteConfirmTitle")))
                return;

            var toRemove = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { group.Id };
            CollectDescendants(group.Id, toRemove);

            for (var i = _groups.Count - 1; i >= 0; i--)
            {
                if (toRemove.Contains(_groups[i].Id))
                    _groups.RemoveAt(i);
            }
            RebuildTree();
        }

        /// <summary>Собирает идентификаторы всех групп-потомков указанной группы.</summary>
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

        /// <summary>Выбирает узел с указанной группой в дереве (разворачивая предков).</summary>
        private void SelectGroup(Group group)
        {
            foreach (var root in _tree.ItemsSource?.OfType<GroupNodeViewModel>() ?? Enumerable.Empty<GroupNodeViewModel>())
            {
                var node = FindNode(root, group);
                if (node is not null)
                {
                    for (var n = node; n is not null; n = n.Parent)
                        n.IsExpanded = true;
                    node.IsSelected = true;
                    return;
                }
            }
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
}
#endif