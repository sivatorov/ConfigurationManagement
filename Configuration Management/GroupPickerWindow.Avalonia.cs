#if LINUX
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls.Primitives;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Configuration_Management.Localization;
using Configuration_Management.Models;
using Configuration_Management.ViewModels;

namespace Configuration_Management
{
    /// <summary>
    /// Диалог выбора группы в виде дерева (или «Без группы» / корень).
    /// Avalonia/Linux-версия WPF-окна <see cref="GroupPickerWindow"/>.
    /// </summary>
    public class GroupPickerWindow : ModalWindowBase
    {
        private readonly IReadOnlyList<Group> _groups;
        private readonly List<Group> _allowed;
        private readonly string _currentGroupId;
        private readonly bool _allowNone;
        private readonly string _noneLabel;
        private bool _sortAscending = true;
        private GroupNodeViewModel? _selectedNode;

        private readonly TreeView _tree = new();
        private readonly Button _selectButton = new() { Content = LocalizationManager.T("Common.Select"), MinWidth = 110, IsDefault = true };
        private readonly RadioButton _sortAsc = new() { Content = LocalizationManager.T("Common.SortAsc"), IsChecked = true };
        private readonly RadioButton _sortDesc = new() { Content = LocalizationManager.T("Common.SortDesc") };

        /// <param name="groups">Список групп.</param>
        /// <param name="currentGroupId">Текущая выбранная группа (для подсветки).</param>
        /// <param name="excludeGroupId">Группа, которую нельзя выбрать (сама редактируемая + потомки отфильтруются).</param>
        /// <param name="allowNone">Разрешить выбор «Без группы» / корень.</param>
        /// <param name="noneLabel">Подпись корневого пункта.</param>
        public GroupPickerWindow(
            IEnumerable<Group> groups,
            string? currentGroupId = null,
            string? excludeGroupId = null,
            bool allowNone = true,
            string noneLabel = "")
        {
            Title = LocalizationManager.T("GroupPicker.Title");
            Width = 480;
            Height = 520;
            MinWidth = 420;
            MinHeight = 400;

            _groups = groups.ToList();
            _currentGroupId = currentGroupId ?? string.Empty;
            _allowNone = allowNone;
            _noneLabel = string.IsNullOrEmpty(noneLabel) ? LocalizationManager.T("Connection.NoGroup") : noneLabel;

            _allowed = string.IsNullOrEmpty(excludeGroupId)
                ? _groups.ToList()
                : _groups.Where(g =>
                        !string.Equals(g.Id, excludeGroupId, StringComparison.OrdinalIgnoreCase)
                        && !GroupHierarchyHelper.IsAncestorOrSelf(g.Id, excludeGroupId, _groups))
                    .ToList();

            Content = BuildRoot();
            RefreshTree();
        }

        /// <summary>Выбранная группа; null — без группы / корень.</summary>
        public Group? ResultGroup => _selectedNode?.Group;

        /// <summary>Id выбранной группы; пустая строка — корень / без группы.</summary>
        public string ResultGroupId => _selectedNode?.Group?.Id ?? string.Empty;

        /// <summary>Полный путь выбранной группы; пустая строка — без группы.</summary>
        public string ResultFullPath =>
            _selectedNode?.Group is null
                ? string.Empty
                : GroupHierarchyHelper.GetFullPath(_selectedNode.Group, _groups);

        private Control BuildRoot()
        {
            var grid = new Grid { Margin = new Thickness(16) };
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            grid.RowDefinitions.Add(new RowDefinition(new GridLength(1, GridUnitType.Star)));
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            // Панель сортировки
            var sortPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 8) };
            _sortAsc.GroupName = "Sort";
            _sortDesc.GroupName = "Sort";
            _sortAsc.Checked += (_, _) => { _sortAscending = true; _sortDesc.IsChecked = false; RefreshTree(); };
            _sortDesc.Checked += (_, _) => { _sortAscending = false; _sortAsc.IsChecked = false; RefreshTree(); };
            sortPanel.Children.Add(new TextBlock { Text = LocalizationManager.T("Common.SortLabel"), VerticalAlignment = VerticalAlignment.Center });
            sortPanel.Children.Add(_sortAsc);
            sortPanel.Children.Add(_sortDesc);
            Grid.SetRow(sortPanel, 0);
            grid.Children.Add(sortPanel);

            // Дерево
            _tree.SelectionMode = SelectionMode.Single;
            _tree.ItemTemplate = new FuncTreeDataTemplate(
                typeof(object),
                (item, _) => BuildTreeRow(item),
                item => item is GroupNodeViewModel g ? g.Children : null);
            _tree.SelectionChanged += (_, _) =>
            {
                _selectedNode = _tree.SelectedItem as GroupNodeViewModel;
                _selectButton.IsEnabled = _selectedNode is not null;
            };
            _tree.DoubleTapped += (_, _) =>
            {
                if (_selectButton.IsEnabled)
                    OnSelect_Click();
            };

            var border = new Border
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
            Grid.SetRow(border, 1);
            grid.Children.Add(border);

            // Кнопки
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8,
                Margin = new Thickness(0, 12, 0, 0)
            };
            var cancel = new Button { Content = LocalizationManager.T("Common.Cancel"), MinWidth = 100, IsCancel = true };
            cancel.Click += (_, _) => Close();
            buttons.Children.Add(cancel);
            _selectButton.Click += (_, _) => OnSelect_Click();
            buttons.Children.Add(_selectButton);
            Grid.SetRow(buttons, 2);
            grid.Children.Add(buttons);

            return grid;
        }

        private static Control BuildTreeRow(object? item)
        {
            if (item is GroupNodeViewModel node)
            {
                var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(4, 2) };
                var iconKey = node.Group is null ? "IconRootGroup" : "IconChevronRight";
                panel.Children.Add(IconHelper.MakeIcon(iconKey, 14));
                var text = new TextBlock { Text = node.DisplayName, VerticalAlignment = VerticalAlignment.Center };
                panel.Children.Add(text);
                return panel;
            }
            return new TextBlock { Text = item?.ToString() ?? string.Empty };
        }

        /// <summary>Перестраивает дерево с учётом текущего направления сортировки.</summary>
        private void RefreshTree()
        {
            var roots = GroupNodeViewModel.BuildTree(_allowed);
            var groupComparer = StringComparer.OrdinalIgnoreCase;
            roots.Sort(_sortAscending
                ? (a, b) => groupComparer.Compare(a.DisplayName, b.DisplayName)
                : (a, b) => groupComparer.Compare(b.DisplayName, a.DisplayName));
            foreach (var root in roots)
                root.SortChildrenRecursive(_sortAscending);

            var items = new List<GroupNodeViewModel>();
            if (_allowNone)
                items.Add(new GroupNodeViewModel(null, displayName: _noneLabel));
            items.AddRange(roots);

            _tree.ItemsSource = items;

            if (!string.IsNullOrEmpty(_currentGroupId))
                SelectById(items, _currentGroupId);
            else if (_allowNone && items.Count > 0)
            {
                _selectedNode = items[0];
                _selectButton.IsEnabled = true;
            }
            else
            {
                _selectButton.IsEnabled = false;
            }
        }

        private void SelectById(IEnumerable<GroupNodeViewModel> roots, string groupId)
        {
            foreach (var root in roots)
            {
                if (TrySelect(root, groupId))
                    return;
            }
        }

        private bool TrySelect(GroupNodeViewModel node, string groupId)
        {
            if (node.Group is not null &&
                string.Equals(node.Group.Id, groupId, StringComparison.OrdinalIgnoreCase))
            {
                _selectedNode = node;
                node.IsSelected = true;
                node.IsExpanded = true;
                _selectButton.IsEnabled = true;
                return true;
            }

            foreach (var child in node.Children)
            {
                if (TrySelect(child, groupId))
                    return true;
            }
            return false;
        }

        private void OnSelect_Click()
        {
            if (_selectedNode is null)
                return;
            DialogResult = true;
            Close();
        }
    }
}
#endif