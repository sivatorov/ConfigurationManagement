using System.Collections.Generic;
using System.Linq;
using Configuration_Management.Models;
using Configuration_Management.Services;
using Xunit;

namespace ConfigurationManagement.Tests;

/// <summary>
/// Тесты чистого построителя цепочек обновлений конфигурации
/// (<see cref="UpdateChainBuilder"/>, issue #352): прямое обновление, вариант «снизу вверх»
/// (жадный максимальный шаг), оптимальный вариант (минимум прыжков), случаи тупика
/// и отсутствия данных о совместимости.
/// </summary>
public sealed class UpdateChainBuilderTests
{
    /// <summary>Создаёт релиз каталога с «Списком версий» (версии, из которых можно
    /// обновиться напрямую до этой версии).</summary>
    private static PlatformRelease R(string version, params string[] sources)
        => new() { Version = version, Sources = sources.ToList() };

    /// <summary>Номера версий шагов варианта.</summary>
    private static string[] Steps(UpdateChainVariant variant)
        => variant.Steps.Select(s => s.Version).ToArray();

    [Fact]
    public void DirectUpdate_CurrentInTargetSources_ReturnsIsDirectUpdate()
    {
        // Последняя версия принимает обновление напрямую с текущей — цепочка не нужна.
        var releases = new List<PlatformRelease>
        {
            R("3.0.160.12", "3.0.150.5", "3.0.155.23"),
            R("3.0.158.71", "3.0.150.5"),
            R("3.0.150.5"),
        };

        var set = UpdateChainBuilder.Build("3.0.150.5", "3.0.160.12", releases);

        Assert.True(set.HasSourceData);
        Assert.True(set.IsDirectUpdate);
        Assert.Empty(set.Variants);
    }

    [Fact]
    public void Chain_BottomUpEqualsOptimal_SingleVariant()
    {
        // Один путь: 3.0.150.5 → 3.0.158.71 → 3.0.160.12; жадный и оптимальный совпадают.
        var releases = new List<PlatformRelease>
        {
            R("3.0.160.12", "3.0.158.71"),
            R("3.0.158.71", "3.0.150.5"),
            R("3.0.150.5"),
        };

        var set = UpdateChainBuilder.Build("3.0.150.5", "3.0.160.12", releases);

        Assert.False(set.IsDirectUpdate);
        Assert.Single(set.Variants);
        var variant = set.Variants[0];
        Assert.Equal(UpdateChainKind.BottomUp, variant.Kind);
        Assert.Equal(1, variant.Number);
        Assert.Equal(new[] { "3.0.158.71", "3.0.160.12" }, Steps(variant));
    }

    [Fact]
    public void Chain_GreedyDiffersFromOptimal_TwoVariants()
    {
        // Жадный (максимальный шаг) заводит в обход: 1→3→4→5→6 (4 шага),
        // оптимальный короче: 1→2→5→6 (3 шага).
        var releases = new List<PlatformRelease>
        {
            R("6.0.0.6", "5.0.0.5"),
            R("5.0.0.5", "2.0.0.2", "4.0.0.4"),
            R("4.0.0.4", "3.0.0.3"),
            R("3.0.0.3", "1.0.0.1"),
            R("2.0.0.2", "1.0.0.1"),
            R("1.0.0.1"),
        };

        var set = UpdateChainBuilder.Build("1.0.0.1", "6.0.0.6", releases);

        Assert.False(set.IsDirectUpdate);
        Assert.Equal(2, set.Variants.Count);

        var bottomUp = set.Variants[0];
        Assert.Equal(UpdateChainKind.BottomUp, bottomUp.Kind);
        Assert.Equal(1, bottomUp.Number);
        Assert.Equal(new[] { "3.0.0.3", "4.0.0.4", "5.0.0.5", "6.0.0.6" }, Steps(bottomUp));

        var optimal = set.Variants[1];
        Assert.Equal(UpdateChainKind.Optimal, optimal.Kind);
        Assert.Equal(2, optimal.Number);
        Assert.Equal(new[] { "2.0.0.2", "5.0.0.5", "6.0.0.6" }, Steps(optimal));
    }

    [Fact]
    public void Chain_GreedyHitsDeadEnd_OptimalStillWorks()
    {
        // Максимальный шаг с текущей ведёт в тупик (3.0.0.3 никуда не пускает),
        // а оптимальный путь существует: 1→2→4.
        var releases = new List<PlatformRelease>
        {
            R("4.0.0.4", "2.0.0.2"),
            R("3.0.0.3", "1.0.0.1"),
            R("2.0.0.2", "1.0.0.1"),
            R("1.0.0.1"),
        };

        var set = UpdateChainBuilder.Build("1.0.0.1", "4.0.0.4", releases);

        Assert.False(set.IsDirectUpdate);
        Assert.Single(set.Variants);
        Assert.Equal(UpdateChainKind.Optimal, set.Variants[0].Kind);
        Assert.Equal(1, set.Variants[0].Number);
        Assert.Equal(new[] { "2.0.0.2", "4.0.0.4" }, Steps(set.Variants[0]));
    }

    [Fact]
    public void Chain_Impossible_ReturnsNoVariants()
    {
        // 1.0.0.1 не входит ни в один «Список версий» — пути нет.
        var releases = new List<PlatformRelease>
        {
            R("3.0.0.3", "2.0.0.2"),
            R("2.0.0.2", "5.0.0.5"),
            R("1.0.0.1"),
        };

        var set = UpdateChainBuilder.Build("1.0.0.1", "3.0.0.3", releases);

        Assert.True(set.HasSourceData);
        Assert.False(set.IsDirectUpdate);
        Assert.Empty(set.Variants);
    }

    [Fact]
    public void NoSourceData_AllSourcesEmpty_ReturnsHasSourceDataFalse()
    {
        var releases = new List<PlatformRelease>
        {
            R("3.0.160.12"),
            R("3.0.158.71"),
            R("3.0.150.5"),
        };

        var set = UpdateChainBuilder.Build("3.0.150.5", "3.0.160.12", releases);

        Assert.False(set.HasSourceData);
        Assert.False(set.IsDirectUpdate);
        Assert.Empty(set.Variants);
    }

    [Fact]
    public void EmptyOrBlankCurrent_ReturnsSafeEmptySet()
    {
        var releases = new List<PlatformRelease> { R("3.0.160.12", "3.0.150.5") };

        var empty = UpdateChainBuilder.Build(string.Empty, "3.0.160.12", releases);
        var blank = UpdateChainBuilder.Build("   ", "3.0.160.12", releases);
        var nullTarget = UpdateChainBuilder.Build("3.0.150.5", string.Empty, releases);

        Assert.False(empty.HasSourceData);
        Assert.Empty(empty.Variants);
        Assert.False(blank.HasSourceData);
        Assert.Empty(blank.Variants);
        Assert.False(nullTarget.HasSourceData);
        Assert.Empty(nullTarget.Variants);
    }

    [Fact]
    public void TargetNotInCatalog_ReturnsNoVariants()
    {
        // Последняя версия (из проверки) отсутствует в каталоге — цепочка не строится.
        var releases = new List<PlatformRelease>
        {
            R("2.0.0.2", "1.0.0.1"),
            R("1.0.0.1"),
        };

        var set = UpdateChainBuilder.Build("1.0.0.1", "9.9.9.9", releases);

        Assert.True(set.HasSourceData);
        Assert.False(set.IsDirectUpdate);
        Assert.Empty(set.Variants);
    }

    [Fact]
    public void CanJump_ChecksNewerVersionAndSourcesMembership()
    {
        var target = R("3.0.160.12", "3.0.150.5");

        Assert.True(UpdateChainBuilder.CanJump("3.0.150.5", target));
        // Одинаковая версия — не прыжок.
        Assert.False(UpdateChainBuilder.CanJump("3.0.160.12", target));
        // Старше цели, но не в списке версий.
        Assert.False(UpdateChainBuilder.CanJump("3.0.140.0", target));
        // Пустая версия.
        Assert.False(UpdateChainBuilder.CanJump(string.Empty, target));
    }
}