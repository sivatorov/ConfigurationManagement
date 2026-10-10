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

    // ============ Резолв nick/ver для URL каталога проекта (issue #352.1) ============

    [Fact]
    public void ExtractNickFromProjectUrl_ProjectUrlWithoutQuery_ReturnsNick()
    {
        // Ссылка из лога 7OH (issue #352): у каталога проекта нет query с nick/ver.
        var nick = OneCUpdatesService.ExtractNickFromProjectUrl("https://releases.1c.ru/project/Trade110");

        Assert.Equal("Trade110", nick);
    }

    [Fact]
    public void ExtractNickFromProjectUrl_WithQueryAndAnchor_TakesOnlyPathSegment()
    {
        var nick = OneCUpdatesService.ExtractNickFromProjectUrl(
            "https://releases.1c.ru/project/Trade110?allUpdates=true#updates");

        Assert.Equal("Trade110", nick);
    }

    [Fact]
    public void ExtractNickFromProjectUrl_NonProjectUrl_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, OneCUpdatesService.ExtractNickFromProjectUrl(
            "https://releases.1c.ru/version_files?nick=Trade110&ver=11.5.27.98"));
        Assert.Equal(string.Empty, OneCUpdatesService.ExtractNickFromProjectUrl(null));
        Assert.Equal(string.Empty, OneCUpdatesService.ExtractNickFromProjectUrl(string.Empty));
    }

    [Fact]
    public void ExtractNickAndVersion_AdditionalFileWithoutVer_VerEmpty()
    {
        // Точный URL из лога 7OH (issue #352): additional_file с path, но без ver.
        var (nick, ver) = OneCUpdatesService.ExtractNickAndVersion(
            "https://releases.1c.ru/additional_file?nick=Trade110&path=Trade%5cExtrafiles%5cRasshirenieGISMTsRPT.cf");

        Assert.Equal("Trade110", nick);
        Assert.Equal(string.Empty, ver);
    }

    [Fact]
    public void FindLatestVersionFilesUrl_ProjectCatalogHtml_ReturnsFirstVersionFilesLink()
    {
        // Таблица каталога отсортирована от новых версий к старым — берётся первая ссылка.
        const string html = """
            <table id="versionsTable">
              <tr><td><a href="/version_files?nick=Trade110&ver=11.5.27.98">11.5.27.98</a></td></tr>
              <tr><td><a href="/version_files?nick=Trade110&ver=11.5.26.118">11.5.26.118</a></td></tr>
            </table>
            """;

        var url = OneCUpdatesService.FindLatestVersionFilesUrl(
            html, "https://releases.1c.ru/project/Trade110?allUpdates=true#updates");

        Assert.Equal("https://releases.1c.ru/version_files?nick=Trade110&ver=11.5.27.98", url);
    }

    [Fact]
    public void FindLatestVersionFilesUrl_RelativeLinkWithoutHost_ResolvedAgainstBase()
    {
        const string html = """<a href="version_files?nick=Trade110&ver=11.5.27.98">11.5.27.98</a>""";

        var url = OneCUpdatesService.FindLatestVersionFilesUrl(
            html, "https://releases.1c.ru/project/Trade110");

        Assert.Equal("https://releases.1c.ru/project/version_files?nick=Trade110&ver=11.5.27.98", url);
    }

    [Fact]
    public void FindLatestVersionFilesUrl_SkipsLinksWithoutVer()
    {
        const string html = """
            <a href="/version_files?nick=Trade110">все версии</a>
            <a href="/version_files?nick=Trade110&ver=11.5.27.98">11.5.27.98</a>
            """;

        var url = OneCUpdatesService.FindLatestVersionFilesUrl(
            html, "https://releases.1c.ru/project/Trade110");

        Assert.Equal("https://releases.1c.ru/version_files?nick=Trade110&ver=11.5.27.98", url);
    }

    [Fact]
    public void FindLatestVersionFilesUrl_NoSuitableLinks_ReturnsNull()
    {
        Assert.Null(OneCUpdatesService.FindLatestVersionFilesUrl(
            "<a href=\"/project/Trade110\">каталог</a>", "https://releases.1c.ru"));
        Assert.Null(OneCUpdatesService.FindLatestVersionFilesUrl(null, "https://releases.1c.ru"));
        Assert.Null(OneCUpdatesService.FindLatestVersionFilesUrl(string.Empty, null));
    }
}