using Configuration_Management.Localization;
using Configuration_Management.Models;
using Configuration_Management.ViewModels;
using Xunit;

namespace ConfigurationManagement.Tests;

/// <summary>
/// Тесты инварианта <see cref="GroupNodeViewModel.PopulateItems"/> (регресс 0.3.9.115,
/// issue #317): узел «Закреплённые» (<see cref="GroupNodeViewModel.PinnedMarker"/>)
/// хранит обёртки <see cref="PinnedInfobaseItem"/>, все остальные узлы — прямые
/// экземпляры <see cref="Infobase"/>. Именно эта разница требует, чтобы WPF-шаблон
/// строки базы применялся автоматически к <c>Infobase</c> (а не только по ключу
/// через ContentControl для закреплённых строк).
/// </summary>
public sealed class GroupNodeViewModelTests
{
    private static Infobase CreateBase(string name = "База") => new()
    {
        Name = name,
        Connection = new ConnectionSettings { Type = ConnectionType.File, FilePath = @"C:\bases\demo" }
    };

    [Fact]
    public void PopulateItems_PinnedNode_WrapsEachBaseIntoPinnedInfobaseItem()
    {
        var base1 = CreateBase("База 1");
        var base2 = CreateBase("База 2");
        var pinned = new GroupNodeViewModel(null, marker: GroupNodeViewModel.PinnedMarker);
        pinned.Infobases.Add(base1);
        pinned.Infobases.Add(base2);

        pinned.PopulateItems();

        Assert.Equal(2, pinned.Items.Count);
        Assert.All(pinned.Items, item => Assert.IsType<PinnedInfobaseItem>(item));
        Assert.Same(base1, Assert.IsType<PinnedInfobaseItem>(pinned.Items[0]).Base);
        Assert.Same(base2, Assert.IsType<PinnedInfobaseItem>(pinned.Items[1]).Base);
    }

    [Fact]
    public void PopulateItems_RegularGroup_KeepsRawInfobaseInstances()
    {
        var infobase = CreateBase();
        var group = new GroupNodeViewModel(new Group { Id = "g1", Name = "Группа" });
        group.Infobases.Add(infobase);

        group.PopulateItems();

        var item = Assert.Single(group.Items);
        Assert.Same(infobase, Assert.IsType<Infobase>(item));
    }

    [Fact]
    public void PopulateItems_NoGroupNode_KeepsRawInfobaseInstances()
    {
        var infobase = CreateBase();
        var noGroup = new GroupNodeViewModel(null, marker: GroupNodeViewModel.NoGroupMarker);
        noGroup.Infobases.Add(infobase);

        noGroup.PopulateItems();

        var item = Assert.Single(noGroup.Items);
        Assert.Same(infobase, Assert.IsType<Infobase>(item));
    }

    /// <summary>Строит дерево глубины 3: root → child → grandchild, плюс второй ребёнок у root.</summary>
    private static (GroupNodeViewModel Root, GroupNodeViewModel Child, GroupNodeViewModel Grandchild, GroupNodeViewModel Sibling)
        BuildDepthThreeTree()
    {
        var root = new GroupNodeViewModel(new Group { Id = "g1", Name = "Корень" });
        var child = new GroupNodeViewModel(new Group { Id = "g2", Name = "Дочка" }, parent: root);
        var grandchild = new GroupNodeViewModel(new Group { Id = "g3", Name = "Внучка" }, parent: child);
        var sibling = new GroupNodeViewModel(new Group { Id = "g4", Name = "Соседка" }, parent: root);
        root.Children.Add(child);
        root.Children.Add(sibling);
        child.Children.Add(grandchild);
        return (root, child, grandchild, sibling);
    }

    [Fact]
    public void TotalSubgroupCount_CountsAllNestedGroupsRecursively()
    {
        var (root, child, grandchild, sibling) = BuildDepthThreeTree();

        Assert.Equal(3, root.TotalSubgroupCount); // child + sibling + grandchild
        Assert.Equal(1, child.TotalSubgroupCount); // только grandchild
        Assert.Equal(0, grandchild.TotalSubgroupCount);
        Assert.Equal(0, sibling.TotalSubgroupCount);
    }

    [Fact]
    public void TotalInfobaseCount_AndTotalSubgroupCount_AreBothRecursive()
    {
        var (root, child, grandchild, _) = BuildDepthThreeTree();
        root.Infobases.Add(CreateBase());
        root.Infobases.Add(CreateBase());
        child.Infobases.Add(CreateBase());
        grandchild.Infobases.Add(CreateBase());

        Assert.Equal(3, root.TotalSubgroupCount);
        Assert.Equal(4, root.TotalInfobaseCount); // 2 (root) + 1 (child) + 1 (grandchild)
        Assert.Equal(1, child.TotalSubgroupCount);
        Assert.Equal(2, child.TotalInfobaseCount); // 1 (child) + 1 (grandchild)
        Assert.Equal(1, grandchild.TotalInfobaseCount);
    }

    [Fact]
    public void GroupCountSuffix_WithoutSubgroups_ShowsOnlyBaseCount()
    {
        var group = new GroupNodeViewModel(new Group { Id = "g1", Name = "Группа" });
        group.Infobases.Add(CreateBase());
        group.Infobases.Add(CreateBase());
        group.Infobases.Add(CreateBase());

        Assert.Equal("(3)", group.GroupCountSuffix);
    }

    [Fact]
    public void GroupCountSuffix_WithSubgroups_UsesLocalizedFormat()
    {
        var (root, _, _, _) = BuildDepthThreeTree();
        root.Infobases.Add(CreateBase());
        root.Infobases.Add(CreateBase());

        // В тестовой среде LocalizationManager не инициализирован и T возвращает ключ,
        // поэтому сравниваем суффикс со строкой, построенной через тот же механизм
        // (стиль ConnectionReplaceViewModelTests): ключ, порядок аргументов и скобки.
        var expected = "(" + string.Format(
            LocalizationManager.T("Main.GroupCountWithSubgroups"),
            root.TotalSubgroupCount, root.TotalInfobaseCount) + ")";
        Assert.Equal(expected, root.GroupCountSuffix);
        // При наличии подгрупп формат обязан отличаться от простого «(M)».
        Assert.NotEqual("(" + root.TotalInfobaseCount + ")", root.GroupCountSuffix);
    }

    [Fact]
    public void GroupCountSuffix_EmptyServiceNode_ShowsZeroInParens()
    {
        var pinned = new GroupNodeViewModel(null, marker: GroupNodeViewModel.PinnedMarker);

        Assert.Equal(0, pinned.TotalSubgroupCount);
        Assert.Equal(0, pinned.TotalInfobaseCount);
        Assert.Equal("(0)", pinned.GroupCountSuffix);
    }

    [Fact]
    public void NotifyCountChanged_RaisesCountNotificationsUpToParents()
    {
        var (root, child, _, _) = BuildDepthThreeTree();
        var rootEvents = new List<string?>();
        var childEvents = new List<string?>();
        root.PropertyChanged += (_, e) => rootEvents.Add(e.PropertyName);
        child.PropertyChanged += (_, e) => childEvents.Add(e.PropertyName);

        child.NotifyCountChanged();

        Assert.Contains(nameof(GroupNodeViewModel.TotalInfobaseCount), childEvents);
        Assert.Contains(nameof(GroupNodeViewModel.TotalSubgroupCount), childEvents);
        Assert.Contains(nameof(GroupNodeViewModel.GroupCountSuffix), childEvents);
        // Цепочка Parent?.NotifyCountChanged(): родитель получает те же уведомления.
        Assert.Contains(nameof(GroupNodeViewModel.TotalInfobaseCount), rootEvents);
        Assert.Contains(nameof(GroupNodeViewModel.TotalSubgroupCount), rootEvents);
        Assert.Contains(nameof(GroupNodeViewModel.GroupCountSuffix), rootEvents);
    }

    [Fact]
    public void PopulateItems_RaisesCountAndSuffixNotifications()
    {
        var group = new GroupNodeViewModel(new Group { Id = "g1", Name = "Группа" });
        var events = new List<string?>();
        group.PropertyChanged += (_, e) => events.Add(e.PropertyName);

        group.PopulateItems();

        Assert.Contains(nameof(GroupNodeViewModel.TotalInfobaseCount), events);
        Assert.Contains(nameof(GroupNodeViewModel.TotalSubgroupCount), events);
        Assert.Contains(nameof(GroupNodeViewModel.GroupCountSuffix), events);
    }

    // ===================== Ветки дерева групп (issue #341) =====================

    /// <summary>Рекурсивная свёртка/развёртка детей узла — та же рекурсия, что и
    /// в <c>MainViewModel.SetExpandedDeep</c> (ветка не трогает соседей).</summary>
    private static void SetExpandedDeep(IEnumerable<GroupNodeViewModel> nodes, bool expanded)
    {
        foreach (var node in nodes)
        {
            node.IsExpanded = expanded;
            SetExpandedDeep(node.Children, expanded);
        }
    }

    [Fact]
    public void BranchCollapse_AffectsOnlyOwnSubtree()
    {
        // Контракт «ветки» (issue #341): сворачивание группы и её потомков НЕ должно
        // менять соседние ветки и родительский узел.
        var (root, child, grandchild, sibling) = BuildDepthThreeTree();
        root.IsExpanded = true;
        child.IsExpanded = true;
        grandchild.IsExpanded = true;
        sibling.IsExpanded = true;

        child.IsExpanded = false;
        SetExpandedDeep(child.Children, expanded: false);

        Assert.False(child.IsExpanded);
        Assert.False(grandchild.IsExpanded);
        Assert.True(sibling.IsExpanded);   // соседняя ветка не тронута
        Assert.True(root.IsExpanded);      // родитель не тронут
    }

    [Fact]
    public void BranchExpand_AffectsOnlyOwnSubtree()
    {
        var (root, child, grandchild, sibling) = BuildDepthThreeTree();
        root.IsExpanded = false;
        child.IsExpanded = false;
        grandchild.IsExpanded = false;
        sibling.IsExpanded = false;

        child.IsExpanded = true;
        SetExpandedDeep(child.Children, expanded: true);

        Assert.True(child.IsExpanded);
        Assert.True(grandchild.IsExpanded);
        Assert.False(sibling.IsExpanded);  // соседняя ветка не тронута
        Assert.False(root.IsExpanded);     // родитель не тронут
    }

    [Fact]
    public void ToggleBranch_ExpandsCollapsedGroupThenCollapsesBack()
    {
        // Контракт Ctrl+клика по группе / Ctrl+Alt++- (issue #341): «свёрнутая группа →
        // toggle разворачивает ВЕТКУ (рекурсивно всех потомков)», «развёрнутая → toggle
        // сворачивает всю ветку». Соседние ветки и родитель не меняются ни в одну сторону.
        var (root, child, grandchild, sibling) = BuildDepthThreeTree();
        root.IsExpanded = true;
        child.IsExpanded = false;
        grandchild.IsExpanded = false;
        sibling.IsExpanded = true;

        // Расширение свёрнутой группы — рекурсивно разворачиваются дети.
        child.IsExpanded = true;
        SetExpandedDeep(child.Children, expanded: true);
        Assert.True(child.IsExpanded);
        Assert.True(grandchild.IsExpanded);
        Assert.True(sibling.IsExpanded);   // сосед не тронут
        Assert.True(root.IsExpanded);      // родитель не тронут

        // Обратный toggle развёрнутой группы — сворачивается вся ветка.
        child.IsExpanded = false;
        SetExpandedDeep(child.Children, expanded: false);
        Assert.False(child.IsExpanded);
        Assert.False(grandchild.IsExpanded);
        Assert.True(sibling.IsExpanded);   // сосед не тронут
        Assert.True(root.IsExpanded);      // родитель не тронут
    }

    [Fact]
    public void ToggleBranch_ToggleTwice_RestoresSubtreeAndLeavesNeighbors()
    {
        // Повторный toggle ветки (issue #341): свернуть развёрнутую ветку целиком,
        // затем снова развернуть — состояние поддерева возвращается в исходное,
        // а соседние ветки и родитель не затронуты ни на одном шаге. Это контракт
        // повторных Ctrl+кликов по группе/плюсику: каждый toggle применяется к
        // всей ветке рекурсивно, без «половинчатых» состояний.
        var (root, child, grandchild, sibling) = BuildDepthThreeTree();
        root.IsExpanded = true;
        child.IsExpanded = true;
        grandchild.IsExpanded = true;
        sibling.IsExpanded = true;

        // Toggle 1: развёрнутая ветка — сворачивается целиком.
        child.IsExpanded = false;
        SetExpandedDeep(child.Children, expanded: false);
        Assert.False(child.IsExpanded);
        Assert.False(grandchild.IsExpanded);
        Assert.True(sibling.IsExpanded);   // сосед не тронут
        Assert.True(root.IsExpanded);      // родитель не тронут

        // Toggle 2: свёрнутая ветка — снова разворачивается целиком.
        child.IsExpanded = true;
        SetExpandedDeep(child.Children, expanded: true);
        Assert.True(child.IsExpanded);
        Assert.True(grandchild.IsExpanded);
        Assert.True(sibling.IsExpanded);   // сосед не тронут
        Assert.True(root.IsExpanded);      // родитель не тронут
    }

    // ===================== Инварианты дерева (issue #351) =====================

    [Fact]
    public void BuildTree_OrphanGroupsBecomeRoots()
    {
        // Инвариант «папки не пропадают» (issue #351): группа с ParentId, указывающим
        // на отсутствующую в списке группу (сирота), НЕ теряется — становится корневой.
        // Так даже после дедупликации или ручной правки списка групп папки остаются
        // видимыми в дереве, а не выбрасываются из него.
        var groups = new List<Group>
        {
            new() { Id = "a", Name = "Учёт" },
            new() { Id = "b", Name = "Бухгалтерия", ParentId = "gone" }
        };

        var roots = GroupNodeViewModel.BuildTree(groups);

        Assert.Equal(2, roots.Count);
        Assert.Contains(roots, r => r.Group?.Id == "a");
        Assert.Contains(roots, r => r.Group?.Id == "b");
    }

    [Fact]
    public void PopulateItems_EmptyGroupsHiddenWhenFlagFalse()
    {
        // Поведение _showEmptyGroups (гипотеза C, issue #351): пустая группа (без баз
        // и без непустых потомков) попадает в Items узла только при includeEmptyGroups=true.
        var child = new GroupNodeViewModel(new Group { Id = "c1", Name = "Пустая" });
        var root = new GroupNodeViewModel(new Group { Id = "r1", Name = "Корень" });
        root.Children.Add(child);

        root.PopulateItems(includeEmptyGroups: false);
        Assert.DoesNotContain(child, root.Items);

        root.PopulateItems(includeEmptyGroups: true);
        Assert.Contains(child, root.Items);
    }
}
