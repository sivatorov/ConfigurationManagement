using Configuration_Management.Models;
using Xunit;

namespace ConfigurationManagement.Tests;

/// <summary>
/// Тесты выбора начальной вкладки окна свойств базы (issue #355): двойной клик
/// по колонкам «Конфигурация»/«№ релиза» списка баз должен открывать свойства
/// сразу на вкладке «Платформа». Общая логика для WPF- и Avalonia-версий окна
/// живёт в <see cref="InfobasePropertiesTabs"/>.
/// </summary>
public sealed class InfobasePropertiesTabsTests
{
    [Fact]
    public void GetTabIndex_Platform_ReturnsPlatformTabIndex()
    {
        Assert.Equal(InfobasePropertiesTabs.PlatformTabIndex,
            InfobasePropertiesTabs.GetTabIndex(InfobasePropertiesTab.Platform));
    }

    [Fact]
    public void GetTabIndex_Default_ReturnsBaseTabIndex()
    {
        Assert.Equal(InfobasePropertiesTabs.BaseTabIndex,
            InfobasePropertiesTabs.GetTabIndex(InfobasePropertiesTab.Default));
    }

    [Fact]
    public void PlatformTab_IsBitnessPlusOne_OrderMatchesWindowLayout()
    {
        // Порядок вкладок окна свойств: База, Подключение, Хранилище, Авторизация,
        // Запуск, Разрядность, Платформа, Идентификатор. «Платформа» — седьмая.
        Assert.Equal(InfobasePropertiesTabs.BitnessTabIndex + 1, InfobasePropertiesTabs.PlatformTabIndex);
        Assert.Equal(InfobasePropertiesTabs.PlatformTabIndex + 1, InfobasePropertiesTabs.IdTabIndex);
        Assert.Equal(InfobasePropertiesTabs.TabCount, InfobasePropertiesTabs.IdTabIndex + 1);
    }

    [Fact]
    public void TabCount_MatchesNumberOfTabs()
    {
        // 8 вкладок: Base, Connection, Repository, Auth, Launch, Bitness, Platform, Id.
        Assert.Equal(8, InfobasePropertiesTabs.TabCount);
        Assert.Equal(0, InfobasePropertiesTabs.BaseTabIndex);
    }

    [Theory]
    [InlineData("Configuration")]
    [InlineData("ConfigurationVersion")]
    public void IsConfigurationColumnKey_ColumnKeys_ReturnTrue(string key)
    {
        Assert.True(InfobasePropertiesTabs.IsConfigurationColumnKey(key));
    }

    [Theory]
    [InlineData("Version")]
    [InlineData("LaunchMode")]
    [InlineData("Server")]
    [InlineData("LastLaunch")]
    [InlineData("Size")]
    [InlineData("Modified")]
    [InlineData("LastBackup")]
    [InlineData("Actions")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("configuration")] // регистр важен: Tag в WPF-разметке задаётся точно
    public void IsConfigurationColumnKey_OtherKeys_ReturnFalse(string? key)
    {
        Assert.False(InfobasePropertiesTabs.IsConfigurationColumnKey(key));
    }
}
