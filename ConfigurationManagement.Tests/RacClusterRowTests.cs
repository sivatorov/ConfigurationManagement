using System;
using Configuration_Management.Models;
using Configuration_Management.ViewModels;
using Xunit;

namespace ConfigurationManagement.Tests;

/// <summary>
/// Тесты строки выбора кластера окна «Серверы 1С» (<see cref="RacClusterRow"/>,
/// issue #324): выпадающий список всегда показывает значение, а не ключ поля —
/// при пустом имени кластера используется нейтральный плейсхолдер с портом.
/// </summary>
public sealed class RacClusterRowTests
{
    [Fact]
    public void DisplayText_NameAndPort_FormatsNameWithPort()
    {
        var row = new RacClusterRow(new RacCluster
        {
            Id = Guid.NewGuid(),
            Name = "Локальный кластер",
            Port = 27541,
        });

        Assert.Equal("Локальный кластер (27541)", row.DisplayText);
    }

    [Fact]
    public void DisplayText_NameOnly_OmitsZeroPort()
    {
        var row = new RacClusterRow(new RacCluster
        {
            Id = Guid.NewGuid(),
            Name = "Кластер",
            Port = 0,
        });

        Assert.Equal("Кластер", row.DisplayText);
    }

    [Fact]
    public void DisplayText_EmptyName_FallbackPlaceholderWithPort()
    {
        // «Ключ вместо названия в поле списка» (issue #324): rac вернул пустое «name» —
        // строка не должна отображать ключ поля; показываем нейтральный плейсхолдер.
        var row = new RacClusterRow(new RacCluster
        {
            Id = Guid.NewGuid(),
            Name = "   ",
            Port = 27541,
        });

        Assert.Equal("(27541)", row.DisplayText);
    }

    [Fact]
    public void DisplayText_EmptyNameAndZeroPort_Dash()
    {
        var row = new RacClusterRow(new RacCluster
        {
            Id = Guid.NewGuid(),
            Name = string.Empty,
            Port = 0,
        });

        Assert.Equal("—", row.DisplayText);
    }

    [Fact]
    public void DisplayText_EmptyName_WithHost_ShowsHostWithPort()
    {
        // issue #324: когда rac не отдал «name», но известен «host», показываем
        // осмысленный хост вместо пустого плейсхолдера.
        var row = new RacClusterRow(new RacCluster
        {
            Id = Guid.NewGuid(),
            Name = string.Empty,
            Host = "ALF",
            Port = 27541,
        });

        Assert.Equal("ALF (27541)", row.DisplayText);
    }

    [Fact]
    public void DisplayText_GuidLikeName_TreatedAsPlaceholder()
    {
        // issue #324: GUID (первичный ключ) не должен отображаться вместо имени.
        var row = new RacClusterRow(new RacCluster
        {
            Id = Guid.NewGuid(),
            Name = Guid.NewGuid().ToString(),
            Port = 1541,
        });

        Assert.Equal("(1541)", row.DisplayText);
    }

    [Fact]
    public void DisplayText_KeyLikeName_TreatedAsPlaceholder()
    {
        // issue #324: «в поле по-прежнему ключ вместо имени» — сам текст ключа «name»
        // (или его значение целиком, если парсер не снял кавычки) именем не является.
        var row = new RacClusterRow(new RacCluster
        {
            Id = Guid.NewGuid(),
            Name = "name",
            Port = 1541,
        });

        Assert.Equal("(1541)", row.DisplayText);
    }
}