using Configuration_Management.Models;
using Configuration_Management.Services;
using Xunit;

namespace ConfigurationManagement.Tests;

/// <summary>
/// Тесты дерева версий каталога платформы (<see cref="PlatformVersionTreeBuilder"/>,
/// issue #330): иерархия «линия → группа сборок → полная версия» с сортировкой
/// по убыванию числовыми сегментами.
/// </summary>
public sealed class PlatformVersionTreeBuilderTests
{
    [Fact]
    public void BuildFromCatalog_GroupsLinesBuildGroupsAndLeaves()
    {
        var releases = new[]
        {
            new PlatformRelease { Version = "8.3.27.2214" },
            new PlatformRelease { Version = "8.3.27.1688" },
            new PlatformRelease { Version = "8.3.9.1" },
            new PlatformRelease { Version = "8.5.1.42" },
        };

        var tree = PlatformVersionTreeBuilder.BuildFromCatalog(releases);

        Assert.Equal(2, tree.Count);
        Assert.Equal("8.5", tree[0].Name);   // 8.5 новее 8.3 — выше
        Assert.Equal("8.3", tree[1].Name);

        var line83 = tree[1];
        Assert.Equal(2, line83.Children.Count);
        Assert.Equal("8.3.27", line83.Children[0].Name); // 8.3.27 новее 8.3.9
        Assert.Equal("8.3.9", line83.Children[1].Name);

        var group8327 = line83.Children[0];
        Assert.Equal(2, group8327.Children.Count);
        Assert.All(group8327.Children, n => Assert.True(n.IsLeaf));
        Assert.Equal("8.3.27.2214", group8327.Children[0].Name);
        Assert.Equal("8.3.27.2214", group8327.Children[0].Release!.Version);
        Assert.Equal("8.3.27.1688", group8327.Children[1].Name);
        Assert.Equal("8.3.27.1688", group8327.Children[1].Release!.Version);

        // Группы/линии не несут релиза.
        Assert.False(tree[0].IsLeaf);
        Assert.Null(line83.Children[0].Release);
    }

    [Fact]
    public void BuildFromCatalog_Empty_ReturnsEmpty()
    {
        Assert.Empty(PlatformVersionTreeBuilder.BuildFromCatalog(null));
        Assert.Empty(PlatformVersionTreeBuilder.BuildFromCatalog(new PlatformRelease[0]));
    }

    [Fact]
    public void BuildFromCatalog_InvalidVersions_Skipped()
    {
        var releases = new[]
        {
            new PlatformRelease { Version = "8.3.27.2214" },
            new PlatformRelease { Version = "" },
            new PlatformRelease { Version = "  " },
        };

        var tree = PlatformVersionTreeBuilder.BuildFromCatalog(releases);

        Assert.Single(tree);
        Assert.Equal("8.3", tree[0].Name);
    }

    [Fact]
    public void BuildFromCatalog_TwoSegmentVersion_BecomesLeafOfLine()
    {
        var releases = new[] { new PlatformRelease { Version = "8.5.42" } };

        var tree = PlatformVersionTreeBuilder.BuildFromCatalog(releases);

        Assert.Single(tree);
        Assert.Equal("8.5", tree[0].Name);
        Assert.Single(tree[0].Children);
        Assert.True(tree[0].Children[0].IsLeaf);
        Assert.Equal("8.5.42", tree[0].Children[0].Name);
    }

    // ---------- Фильтр поиска по дереву (issue #330, комментарий 7OH) ----------

    [Fact]
    public void Filter_EmptyQuery_ReturnsFullTree()
    {
        var tree = BuildSampleTree();

        // Пустой/пробельный запрос возвращает всё дерево (2 линии: 8.5 и 8.3).
        Assert.Equal(2, PlatformVersionTreeBuilder.Filter(tree, null).Count);
        Assert.Equal(2, PlatformVersionTreeBuilder.Filter(tree, "").Count);
        Assert.Equal(2, PlatformVersionTreeBuilder.Filter(tree, "   ").Count);
        Assert.Empty(PlatformVersionTreeBuilder.Filter(null, "8.3"));
    }

    [Fact]
    public void Filter_BySubstring_KeepsOnlyPathToMatchingLeaves()
    {
        var tree = BuildSampleTree();

        var filtered = PlatformVersionTreeBuilder.Filter(tree, "1688");

        // Остаются линия 8.3 → группа 8.3.27 → лист 8.3.27.1688; линия 8.5 исчезает.
        var line = Assert.Single(filtered);
        Assert.Equal("8.3", line.Name);
        var group = Assert.Single(line.Children);
        Assert.Equal("8.3.27", group.Name);
        Assert.Equal("8.3.27.1688", Assert.Single(group.Children).Name);
        Assert.NotNull(Assert.Single(group.Children).Release);
    }

    [Fact]
    public void Filter_CaseInsensitive_MatchesLeaves()
    {
        var tree = BuildSampleTree();

        // Регистр не важен и достаточно частичного совпадения («8.5.1» находит 8.5.1.42).
        var filtered = PlatformVersionTreeBuilder.Filter(tree, "8.5.1");

        var line = Assert.Single(filtered);
        Assert.Equal("8.5", line.Name);
        var group = Assert.Single(line.Children);
        Assert.Equal("8.5.1", group.Name);
        Assert.Equal("8.5.1.42", Assert.Single(group.Children).Name);
    }

    [Fact]
    public void Filter_NoMatches_ReturnsEmptyList()
    {
        var tree = BuildSampleTree();

        Assert.Empty(PlatformVersionTreeBuilder.Filter(tree, "9.9.9.9"));
    }

    [Fact]
    public void Filter_DoesNotMutateOriginalTree()
    {
        var tree = BuildSampleTree();

        PlatformVersionTreeBuilder.Filter(tree, "1688");

        // Полное дерево сохраняется: линия 8.3 по-прежнему содержит обе группы.
        Assert.Equal(2, tree.Count);
        var line83 = tree.Single(n => n.Name == "8.3");
        Assert.Equal(2, line83.Children.Count);
    }

    private static IReadOnlyList<PlatformCatalogNode> BuildSampleTree()
        => PlatformVersionTreeBuilder.BuildFromCatalog(new[]
        {
            new PlatformRelease { Version = "8.3.27.2214" },
            new PlatformRelease { Version = "8.3.27.1688" },
            new PlatformRelease { Version = "8.3.9.1" },
            new PlatformRelease { Version = "8.5.1.42" },
        });
}