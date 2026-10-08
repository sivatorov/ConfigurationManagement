using Configuration_Management.Services;
using Xunit;

namespace ConfigurationManagement.Tests;

/// <summary>
/// Тесты формирования адреса каталога релизов по нику конфигурации/сегменту базы
/// (<see cref="OneCUpdatesService.BuildNickUrl"/>) — без сетевых запросов (issue #322):
/// персональный сегмент базы должен подставляться в адрес releases.1c.ru/project/<ник>.
/// </summary>
public sealed class OneCUpdatesUrlTests
{
    [Fact]
    public void BuildNickUrl_ReturnsProjectUrlWithEscapedNick()
    {
        var url = OneCUpdatesService.BuildNickUrl("AccountingCorp30");

        Assert.Equal("https://releases.1c.ru/project/AccountingCorp30", url);
    }

    [Fact]
    public void BuildNickUrl_EmptyOrWhitespace_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, OneCUpdatesService.BuildNickUrl(null));
        Assert.Equal(string.Empty, OneCUpdatesService.BuildNickUrl(string.Empty));
        Assert.Equal(string.Empty, OneCUpdatesService.BuildNickUrl("   "));
    }

    [Fact]
    public void BuildNickUrl_TrimsInput()
    {
        var url = OneCUpdatesService.BuildNickUrl("  AccountingCorp30  ");

        Assert.Equal("https://releases.1c.ru/project/AccountingCorp30", url);
    }

    [Fact]
    public void BuildAllUpdatesCatalogUrl_AddsParameterWithAnchor()
    {
        // issue #352: полный каталог версий доступен только с allUpdates=true#updates.
        var url = OneCUpdatesService.BuildAllUpdatesCatalogUrl("https://releases.1c.ru/project/AccountingCorp30");

        Assert.Equal("https://releases.1c.ru/project/AccountingCorp30?allUpdates=true#updates", url);
    }

    [Fact]
    public void BuildAllUpdatesCatalogUrl_ExistingQuery_UsesAmpersand()
    {
        var url = OneCUpdatesService.BuildAllUpdatesCatalogUrl(
            "https://releases.1c.ru/project/AccountingCorp30?ver=3.0");

        Assert.Equal("https://releases.1c.ru/project/AccountingCorp30?ver=3.0&allUpdates=true#updates", url);
    }

    [Fact]
    public void BuildAllUpdatesCatalogUrl_Idempotent_DoesNotDuplicate()
    {
        const string url = "https://releases.1c.ru/project/Platform83?allUpdates=true#updates";

        Assert.Equal(url, OneCUpdatesService.BuildAllUpdatesCatalogUrl(url));
    }

    [Fact]
    public void BuildAllUpdatesCatalogUrl_Empty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, OneCUpdatesService.BuildAllUpdatesCatalogUrl(null));
        Assert.Equal(string.Empty, OneCUpdatesService.BuildAllUpdatesCatalogUrl(string.Empty));
        Assert.Equal(string.Empty, OneCUpdatesService.BuildAllUpdatesCatalogUrl("   "));
    }

    [Fact]
    public void BuildNickUrl_EscapesNonAsciiAndSpaces()
    {
        var url = OneCUpdatesService.BuildNickUrl("Корп Икс");

        // Пробелы и не-ASCII символы экранируются (Uri.EscapeDataString).
        Assert.StartsWith("https://releases.1c.ru/project/", url);
        Assert.DoesNotContain(" ", url);
        Assert.Contains("%", url);
    }
}