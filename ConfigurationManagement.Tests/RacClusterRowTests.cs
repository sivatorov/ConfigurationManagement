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

    [Fact]
    public void ToString_EqualsDisplayText()
    {
        // issue #324 (0.3.9.330): WPF-ComboBox с шаблоном ModernComboBox в некоторых
        // случаях показывает выбранный элемент через ToString — там должно быть
        // читаемое имя кластера, а не «Configuration_Management.ViewModels.RacClusterRow».
        var row = new RacClusterRow(new RacCluster
        {
            Id = Guid.NewGuid(),
            Name = "Локальный кластер",
            Port = 27541,
        });

        Assert.Equal(row.DisplayText, row.ToString());
        Assert.Equal("Локальный кластер (27541)", row.ToString());
    }

    [Fact]
    public void RacClusterToString_NameAndPort_FormatsNameWithPort()
    {
        var cluster = new RacCluster { Id = Guid.NewGuid(), Name = "Кластер 1", Port = 1541 };
        Assert.Equal("Кластер 1 (1541)", cluster.ToString());
    }

    [Fact]
    public void RacClusterToString_EmptyName_ShowsHostThenPlaceholder()
    {
        // Без имени, но с хостом — показываем хост; без обоих — плейсхолдер с портом.
        var withHost = new RacCluster { Id = Guid.NewGuid(), Name = "  ", Host = "ALF", Port = 27541 };
        var withoutHost = new RacCluster { Id = Guid.NewGuid(), Name = string.Empty, Port = 27541 };
        var withoutAnything = new RacCluster { Id = Guid.NewGuid(), Name = string.Empty, Port = 0 };

        Assert.Equal("ALF (27541)", withHost.ToString());
        Assert.Equal("(27541)", withoutHost.ToString());
        Assert.Equal("—", withoutAnything.ToString());
    }

    [Fact]
    public void RacClusterToString_GuidLikeAndKeyName_TreatedAsPlaceholder()
    {
        // Модель может отображаться без шаблона (ComboBox → ToString): GUID-первичный
        // ключ и текст ключа «name» не должны попасть в подпись (issue #324).
        var guidLike = new RacCluster { Id = Guid.NewGuid(), Name = Guid.NewGuid().ToString(), Port = 1541 };
        var keyLike = new RacCluster { Id = Guid.NewGuid(), Name = "NAME", Host = "srv", Port = 1541 };

        Assert.Equal("(1541)", guidLike.ToString());
        Assert.Equal("srv (1541)", keyLike.ToString());
    }
}