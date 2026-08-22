#if LINUX
using System;
using System.Collections.Generic;
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

namespace Configuration_Management
{
    /// <summary>
    /// Диалог выбора версии платформы 1С (как в стартере): фильтр Все / x32 / x64,
    /// сортировка, дерево 8.3 → 8.3.27 → 8.3.27.2214 (x64). Avalonia/Linux-версия
    /// WPF-окна <see cref="PlatformVersionPickerWindow"/>.
    /// </summary>
    public class PlatformVersionPickerWindow : ModalWindowBase
    {
        private string _selectedVersion = string.Empty;
        private List<PlatformVersionInfo> _allInfos = new();
        private readonly string _currentVersion;
        private bool _sortAscending; // по умолчанию — свежие версии сверху
        private string _archFilter = "all";

        private readonly TreeView _tree = new();
        private readonly Button _selectButton = new() { Content = LocalizationManager.T("Common.Select"), MinWidth = 110, IsDefault = true };
        private readonly RadioButton _filterAll = new() { Content = LocalizationManager.T("Common.All"), IsChecked = true, GroupName = "Arch" };
        private readonly RadioButton _filterX32 = new() { Content = LocalizationManager.T("PlatformVersionPicker.FilterX32"), GroupName = "Arch" };
        private readonly RadioButton _filterX64 = new() { Content = LocalizationManager.T("PlatformVersionPicker.FilterX64"), GroupName = "Arch" };
        private readonly RadioButton _sortAsc = new() { Content = LocalizationManager.T("Common.SortAsc") };
        private readonly RadioButton _sortDesc = new() { Content = LocalizationManager.T("Common.SortDesc"), IsChecked = true };

        public PlatformVersionPickerWindow(IEnumerable<string> installedPlatformVersions, string currentVersion)
        {
            Title = LocalizationManager.T("PlatformVersionPicker.Title");
            Width = 560;
            Height = 580;
            MinWidth = 480;
            MinHeight = 440;

            _currentVersion = currentVersion ?? "";

            var extras = PlatformVersionService.GetAdditionalSearchPaths();
            _allInfos = PlatformVersionService.FindInstalledVersionInfos(extras);
            if (_allInfos.Count == 0 && installedPlatformVersions != null)
            {
                _allInfos = installedPlatformVersions
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => new PlatformVersionInfo { Display = s.Trim(), Path = "" })
                    .ToList();
            }
            else if (installedPlatformVersions != null)
            {
                var known = new HashSet<string>(_allInfos.Select(i => i.Display), StringComparer.OrdinalIgnoreCase);
                foreach (var s in installedPlatformVersions)
                {
                    if (string.IsNullOrWhiteSpace(s) || known.Contains(s.Trim())) continue;
                    _allInfos.Add(new PlatformVersionInfo { Display = s.Trim(), Path = "" });
                }
            }

            Content = BuildRoot();
            RefreshTree();
        }

        public string Result => _selectedVersion;

        private Control BuildRoot()
        {
            var grid = new Grid { Margin = new Thickness(16) };
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            grid.RowDefinitions.Add(new RowDefinition(new GridLength(1, GridUnitType.Star)));
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            // Панель фильтров/сортировки
            var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Margin = new Thickness(0, 0, 0, 8), VerticalAlignment = VerticalAlignment.Center };
            var filterPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            filterPanel.Children.Add(new TextBlock { Text = LocalizationManager.T("PlatformVersionPicker.FilterLabel"), VerticalAlignment = VerticalAlignment.Center });
            _filterAll.Checked += (_, _) => { _archFilter = "all"; RefreshTree(); };
            _filterX32.Checked += (_, _) => { _archFilter = "x32"; RefreshTree(); };
            _filterX64.Checked += (_, _) => { _archFilter = "x64"; RefreshTree(); };
            filterPanel.Children.Add(_filterAll);
            filterPanel.Children.Add(_filterX32);
            filterPanel.Children.Add(_filterX64);
            top.Children.Add(filterPanel);

            var sortPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            sortPanel.Children.Add(new TextBlock { Text = LocalizationManager.T("Common.SortLabel"), VerticalAlignment = VerticalAlignment.Center });
            _sortAsc.Checked += (_, _) => { _sortAscending = true; _sortDesc.IsChecked = false; RefreshTree(); };
            _sortDesc.Checked += (_, _) => { _sortAscending = false; _sortAsc.IsChecked = false; RefreshTree(); };
            sortPanel.Children.Add(_sortAsc);
            sortPanel.Children.Add(_sortDesc);
            top.Children.Add(sortPanel);

            Grid.SetRow(top, 0);
            grid.Children.Add(top);

            // Дерево
            _tree.SelectionMode = SelectionMode.Single;
            _tree.ItemTemplate = new FuncTreeDataTemplate(
                typeof(object),
                (item, _) => BuildTreeRow(item),
                item => item is PlatformVersionGroup g && g.Children.Count > 0 ? g.Children : null);
            _tree.SelectionChanged += (_, _) =>
            {
                if (_tree.SelectedItem is PlatformVersionGroup { IsLeaf: true, Variant: { } variant })
                {
                    _selectedVersion = variant;
                    _selectButton.IsEnabled = true;
                }
                else
                {
                    _selectButton.IsEnabled = false;
                }
            };
            _tree.DoubleTapped += (_, _) =>
            {
                if (!string.IsNullOrEmpty(_selectedVersion))
                    OnSelect_Click();
            };

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
            if (item is PlatformVersionGroup node)
            {
                var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(4, 2) };
                var iconKey = node.IsLeaf ? "IconConfiguration" : (node.Kind == PlatformNodeKind.Line ? "IconChevronRight" : "IconBullet");
                panel.Children.Add(IconHelper.MakeIcon(iconKey, 14));
                var text = new TextBlock
                {
                    Text = node.Name,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontWeight = node.IsCurrent ? FontWeight.Bold : FontWeight.Normal
                };
                panel.Children.Add(text);
                return panel;
            }
            return new TextBlock { Text = item?.ToString() ?? string.Empty };
        }

        private void RefreshTree()
        {
            var filtered = FilterByArchitecture(_allInfos, _archFilter);
            var tree = PlatformVersionService.BuildGroupedTree(filtered);
            if (_sortAscending)
                tree = ReverseTreeOrder(tree);

            _tree.ItemsSource = tree;

            if (!string.IsNullOrWhiteSpace(_currentVersion))
                SelectCurrent(tree);
        }

        private static List<PlatformVersionInfo> FilterByArchitecture(IEnumerable<PlatformVersionInfo> infos, string filter)
        {
            if (filter == "all")
                return infos.ToList();

            return infos.Where(i =>
            {
                PlatformVersionService.ParseVariant(i.Display, out _, out var arch);
                var label = PlatformVersionService.FormatArchitectureLabel(arch);
                if (filter == "x64")
                    return label == "x64" || string.IsNullOrEmpty(label);
                if (filter == "x32")
                    return label == "x32";
                return true;
            }).ToList();
        }

        private static List<PlatformVersionGroup> ReverseTreeOrder(List<PlatformVersionGroup> roots)
        {
            var list = roots.AsEnumerable().Reverse().ToList();
            foreach (var node in list)
                ReverseChildren(node);
            return list;
        }

        private static void ReverseChildren(PlatformVersionGroup node)
        {
            if (node.Children.Count == 0) return;
            node.Children = node.Children.AsEnumerable().Reverse().ToList();
            foreach (var c in node.Children)
                ReverseChildren(c);
        }

        private void SelectCurrent(IEnumerable<PlatformVersionGroup> roots)
        {
            var leaf = FindBestLeaf(roots, _currentVersion);
            if (leaf is null) return;
            leaf.IsCurrent = true;
            SelectNodeInTree(_tree, leaf);
        }

        private static PlatformVersionGroup? FindBestLeaf(IEnumerable<PlatformVersionGroup> nodes, string currentVersion)
        {
            if (string.IsNullOrWhiteSpace(currentVersion)) return null;

            ParseVersionAndArch(currentVersion, out var version, out var arch);
            var parts = version.Split('.', StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length >= 4)
                return FindExactLeaf(nodes, currentVersion);

            var linePrefix = string.Join(".", parts.Take(2));
            var line = nodes.FirstOrDefault(n =>
                !n.IsLeaf && string.Equals(n.Name, linePrefix, StringComparison.OrdinalIgnoreCase));
            if (line is null) return null;

            if (parts.Length == 3)
            {
                var buildPrefix = string.Join(".", parts.Take(3));
                var build = line.Children.FirstOrDefault(n =>
                    !n.IsLeaf && string.Equals(n.Name, buildPrefix, StringComparison.OrdinalIgnoreCase));
                return build is null ? null : FirstLeaf(build.Children, arch);
            }

            return FirstLeaf(line.Children, arch);
        }

        private static PlatformVersionGroup? FirstLeaf(IEnumerable<PlatformVersionGroup> nodes, string? arch)
        {
            foreach (var n in nodes)
            {
                if (n.IsLeaf)
                {
                    if (arch is null || MatchesArch(n.Variant, arch))
                        return n;
                    continue;
                }
                var found = FirstLeaf(n.Children, arch);
                if (found is not null) return found;
            }
            return null;
        }

        private static PlatformVersionGroup? FindExactLeaf(IEnumerable<PlatformVersionGroup> nodes, string currentVersion)
        {
            foreach (var n in nodes)
            {
                if (n.IsLeaf && MatchesCurrent(n.Variant ?? n.Name, currentVersion))
                    return n;
                var found = FindExactLeaf(n.Children, currentVersion);
                if (found is not null) return found;
            }
            return null;
        }

        private static bool MatchesCurrent(string variant, string currentVersion)
        {
            if (string.IsNullOrWhiteSpace(currentVersion)) return false;
            PlatformVersionService.ParseVariant(variant, out var version, out _);
            PlatformVersionService.ParseVariant(currentVersion, out var cur, out _);
            if (string.Equals(version.Trim(), cur.Trim(), StringComparison.OrdinalIgnoreCase))
                return true;
            return string.Equals(variant.Trim(), currentVersion.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static bool MatchesArch(string? variant, string arch)
        {
            PlatformVersionService.ParseVariant(variant ?? string.Empty, out _, out var a);
            return string.Equals(a, arch, StringComparison.OrdinalIgnoreCase);
        }

        private static void ParseVersionAndArch(string variant, out string version, out string? arch)
        {
            version = variant.Trim();
            arch = null;
            var end = variant.LastIndexOf(')');
            var start = variant.LastIndexOf('(');
            if (end >= 0 && start >= 0 && start < end)
            {
                var a = variant.Substring(start + 1, end - start - 1).Trim();
                if (a == "64" || a == "32")
                {
                    arch = a;
                    version = variant.Substring(0, start).Trim();
                }
            }
        }

        private static bool SelectNodeInTree(TreeView parent, PlatformVersionGroup target)
        {
            foreach (var item in parent.Items)
            {
                if (item is not PlatformVersionGroup node) continue;
                if (ReferenceEquals(node, target))
                {
                    node.IsSelected = true;
                    return true;
                }
                foreach (var child in node.Children)
                {
                    if (SelectNodeInTree(child, target))
                        return true;
                }
            }
            return false;
        }

        private static bool SelectNodeInTree(PlatformVersionGroup parent, PlatformVersionGroup target)
        {
            if (ReferenceEquals(parent, target))
            {
                parent.IsSelected = true;
                return true;
            }
            foreach (var child in parent.Children)
            {
                if (SelectNodeInTree(child, target))
                    return true;
            }
            return false;
        }

        private void OnSelect_Click()
        {
            if (string.IsNullOrEmpty(_selectedVersion))
                return;
            DialogResult = true;
            Close();
        }
    }
}
#endif