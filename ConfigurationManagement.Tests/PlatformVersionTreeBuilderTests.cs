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
}