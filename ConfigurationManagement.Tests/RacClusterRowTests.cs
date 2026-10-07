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
}