using System.Collections.Generic;
using System.IO;
using System.Linq;
using Configuration_Management.Models;
using Configuration_Management.Services;
using Xunit;

namespace ConfigurationManagement.Tests;

/// <summary>
/// Тесты ограничения «Не повышать» (issue #352.4): SelectCappedTarget выбирает максимум
/// среди релизов с теми же первыми двумя числами версии (3.1.2.345 → можно 3.1.3.456,
/// нельзя 3.2.3.456), а также хелпера начальной папки цепочки GetChainInitialFolder
/// (issue #352.2).
/// </summary>
public sealed class UpdateChainVersionCapTests
{
    /// <summary>Создаёт релиз каталога с «Списком версий».</summary>
    private static PlatformRelease R(string version, params string[] sources)
        => new() { Version = version, Sources = sources.ToList() };

    // ===================== SelectCappedTarget (issue #352.4) =====================

    [Fact]
    public void SelectCappedTarget_PicksMaxOfSameMajorMinor()
    {
        // Текущая 3.1.2.345: из каталога с 3.1.3.456 и 3.2.3.456 кап = 3.1.3.456.
        var releases = new List<PlatformRelease>
        {
            R("3.2.3.456"),
            R("3.1.3.456"),
            R("3.1.2.345"),
        };

        var capped = UpdateChainBuilder.SelectCappedTarget("3.1.2.345", releases);

        Assert.Equal("3.1.3.456", capped);
    }

    [Fact]
    public void SelectCappedTarget_IgnoresOtherMajorMinorLines()
    {
        var releases = new List<PlatformRelease>
        {
            R("3.2.0.1"),
            R("4.0.0.1"),
            R("3.1.9.9"),
        };

        Assert.Equal("3.1.9.9", UpdateChainBuilder.SelectCappedTarget("3.1.2.345", releases));
    }

    [Fact]
    public void SelectCappedTarget_CappedTargetReachableDirectly_IsDirectUpdate()
    {
        // Кап-цель принимает текущую версию напрямую — обновление без цепочки.
        var releases = new List<PlatformRelease>
        {
            R("3.2.3.456"),
            R("3.1.3.456", "3.1.2.345"),
            R("3.1.2.345"),
        };

        var capped = UpdateChainBuilder.SelectCappedTarget("3.1.2.345", releases);
        var set = UpdateChainBuilder.Build("3.1.2.345", capped!, releases);

        Assert.True(set.HasSourceData);
        Assert.True(set.IsDirectUpdate);
        Assert.Empty(set.Variants);
    }

    [Fact]
    public void SelectCappedTarget_CappedTargetRequiresChain_BuiltWithinLine()
    {
        // Кап-цель требует цепочку — шаги только внутри 3.1.x (3.2.x исключён капом,
        // хотя глобально новее).
        var releases = new List<PlatformRelease>
        {
            R("3.2.3.456"),
            R("3.1.3.456", "3.1.2.400"),
            R("3.1.2.400", "3.1.2.345"),
            R("3.1.2.345"),
        };

        var capped = UpdateChainBuilder.SelectCappedTarget("3.1.2.345", releases);
        Assert.Equal("3.1.3.456", capped);

        var set = UpdateChainBuilder.Build("3.1.2.345", capped!, releases);

        Assert.False(set.IsDirectUpdate);
        Assert.NotEmpty(set.Variants);
        var variant = set.Variants[0];
        Assert.All(variant.Steps, step =>
            Assert.StartsWith("3.1.", step.Version));
        Assert.Equal("3.1.3.456", variant.Steps[^1].Version);
    }

    [Fact]
    public void SelectCappedTarget_CurrentVersionMissing_UsesMaxOfLine()
    {
        // Текущей версии нет в каталоге (отозвана) — кап всё равно берёт максимум 3.1.x.
        var releases = new List<PlatformRelease>
        {
            R("3.1.8.100", "3.1.7.50"),
            R("3.1.7.50", "3.1.7.10"),
            R("3.2.1.1"),
        };

        var capped = UpdateChainBuilder.SelectCappedTarget("3.1.6.10", releases);

        Assert.Equal("3.1.8.100", capped);

        var set = UpdateChainBuilder.Build("3.1.6.10", capped!, releases);
        Assert.True(set.IsCurrentVersionMissing);
    }

    [Fact]
    public void SelectCappedTarget_NoVersionsOfSameLine_ReturnsNull()
    {
        var releases = new List<PlatformRelease>
        {
            R("3.2.3.456"),
            R("4.0.0.1"),
        };

        Assert.Null(UpdateChainBuilder.SelectCappedTarget("3.1.2.345", releases));
    }

    [Fact]
    public void SelectCappedTarget_EmptyOrUnparsableCurrent_ReturnsNull()
    {
        var releases = new List<PlatformRelease> { R("3.1.3.456") };

        Assert.Null(UpdateChainBuilder.SelectCappedTarget(string.Empty, releases));
        Assert.Null(UpdateChainBuilder.SelectCappedTarget("   ", releases));
        Assert.Null(UpdateChainBuilder.SelectCappedTarget("не версия", releases));
        Assert.Null(UpdateChainBuilder.SelectCappedTarget("3", releases));
    }

    [Fact]
    public void SelectCappedTarget_EmptyCatalog_ReturnsNull()
    {
        Assert.Null(UpdateChainBuilder.SelectCappedTarget("3.1.2.345", new List<PlatformRelease>()));
    }

    [Fact]
    public void SelectCappedTarget_TwoDigitSegments_CompareNumerically()
    {
        // 3.1.10 > 3.1.9 численно, а не лексикографически.
        var releases = new List<PlatformRelease>
        {
            R("3.1.9.500"),
            R("3.1.10.1"),
        };

        Assert.Equal("3.1.10.1", UpdateChainBuilder.SelectCappedTarget("3.1.2.345", releases));
    }

    [Fact]
    public void CapOff_BuildBehaviourUnchanged_Regression()
    {
        // Без капа (галочка снята) поведение Build прежнее — цель 3.2.3.456 достижима.
        var releases = new List<PlatformRelease>
        {
            R("3.2.3.456", "3.1.2.345"),
            R("3.1.2.345"),
        };

        var set = UpdateChainBuilder.Build("3.1.2.345", "3.2.3.456", releases);

        Assert.True(set.IsDirectUpdate);
    }

    // ===================== GetChainInitialFolder (issue #352.2) =====================

    [Fact]
    public void GetChainInitialFolder_ExistingSavedFolder_ReturnsSaved()
    {
        var existing = Path.GetTempPath();

        Assert.Equal(existing, UpdateChainDownloadPlanner.GetChainInitialFolder(existing));
    }

    [Fact]
    public void GetChainInitialFolder_MissingOrEmptySavedFolder_ReturnsUserProfile()
    {
        Assert.Equal(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
            UpdateChainDownloadPlanner.GetChainInitialFolder(null));
        Assert.Equal(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
            UpdateChainDownloadPlanner.GetChainInitialFolder(string.Empty));
        Assert.Equal(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
            UpdateChainDownloadPlanner.GetChainInitialFolder(@"Z:\несуществующий\каталог"));
    }
}
