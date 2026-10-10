using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Configuration_Management.Models;
using Configuration_Management.Services;
using Xunit;

namespace ConfigurationManagement.Tests;

/// <summary>
/// Тесты формирования адреса каталога релизов по нику конфигурации/сегменту базы
/// (<see cref="OneCUpdatesService.BuildNickUrl"/>) — без сетевых запросов (issue #322):
/// персональный сегмент базы должен подставляться в адрес releases.1c.ru/project/<ник>.
/// С 0.3.12.2 (issue #352.1) — также GetReleaseFileChoicesAsync с известной целевой
/// версией (fake-транспорт): адрес без ver резолвится в известную версию без запроса
/// каталога проекта; без параметра — прежнее поведение (резолв по каталогу).
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

    // ========== GetReleaseFileChoicesAsync: известная версия (issue #352.1, 0.3.12.2) ==========

    [Fact]
    public async Task GetReleaseFileChoicesAsync_KnownLatestVersion_UsesItWithoutCatalogRequest()
    {
        // Регрессия 7OH (issue #352): адрес каталога проекта без ver + известная целевая
        // версия → страница файлов запрашивается для ЭТОЙ версии, каталог проекта
        // (allUpdates) НЕ запрашивается — резолв глобальной последней исключён.
        var handler = new RecordingHandler(url =>
        {
            if (url.Contains("version_files?nick=Trade110&ver=11.5.27.98", StringComparison.OrdinalIgnoreCase))
                return Html("""
                    <html><body>
                      <a href="/version_file?nick=Trade110&ver=11.5.27.98&path=Trade%5c11_5_27_98%5cTrade_11_5_27_98.rar">Полный дистрибутив</a>
                    </body></html>
                    """);
            return Html("<html><body>unexpected</body></html>");
        });
        var service = new OneCUpdatesService(new FakeRepository(), new FakeLogger(), handler);

        var choices = await service.GetReleaseFileChoicesAsync(
            "https://releases.1c.ru/project/Trade110", CancellationToken.None, "11.5.27.98");

        var choice = Assert.Single(choices);
        Assert.Contains("Trade_11_5_27_98.rar", choice.Url, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(handler.RequestedUrls, u =>
            u.Contains("version_files?nick=Trade110&ver=11.5.27.98", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(handler.RequestedUrls, u => u.Contains("allUpdates", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetReleaseFileChoicesAsync_WithoutKnownVersion_ResolvesLatestViaCatalog()
    {
        // Прежнее поведение (регрессия): без knownLatestVersion каталог запрашивается,
        // страница файлов — для последней версии из таблицы каталога.
        var handler = new RecordingHandler(url =>
        {
            if (url.Contains("allUpdates=true", StringComparison.OrdinalIgnoreCase))
                return Html("""
                    <table id="versionsTable">
                      <tr><td><a href="/version_files?nick=Trade110&ver=11.5.27.98">11.5.27.98</a></td></tr>
                      <tr><td><a href="/version_files?nick=Trade110&ver=11.5.26.118">11.5.26.118</a></td></tr>
                    </table>
                    """);
            if (url.Contains("version_files?nick=Trade110&ver=11.5.27.98", StringComparison.OrdinalIgnoreCase))
                return Html("""
                    <html><body>
                      <a href="/version_file?nick=Trade110&ver=11.5.27.98&path=Trade%5c11_5_27_98%5cTrade_11_5_27_98_updsetup.cf">Дистрибутив обновления</a>
                    </body></html>
                    """);
            return Html("<html><body>unexpected</body></html>");
        });
        var service = new OneCUpdatesService(new FakeRepository(), new FakeLogger(), handler);

        var choices = await service.GetReleaseFileChoicesAsync(
            "https://releases.1c.ru/project/Trade110", CancellationToken.None);

        var choice = Assert.Single(choices);
        Assert.Contains("Trade_11_5_27_98_updsetup.cf", choice.Url, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(handler.RequestedUrls, u => u.Contains("allUpdates=true", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(handler.RequestedUrls, u =>
            u.Contains("version_files?nick=Trade110&ver=11.5.27.98", StringComparison.OrdinalIgnoreCase));
    }

    private static HttpResponseMessage Html(string body)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "text/html"),
        };

    /// <summary>Обработчик-рекордер: адреса всех запросов сохраняются, ответ — по маршруту.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<string, HttpResponseMessage> _responder;

        public RecordingHandler(Func<string, HttpResponseMessage> responder) => _responder = responder;

        public List<string> RequestedUrls { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            RequestedUrls.Add(url);
            return Task.FromResult(_responder(url));
        }
    }

    /// <summary>Fake репозитория: настройки портала в памяти, базы/группы пустые.</summary>
    private sealed class FakeRepository : IInfobaseRepository
    {
        public AppSettings Settings { get; set; } = new();

        public List<Infobase> Load() => new();

        public void Save(List<Infobase> infobases)
        {
        }

        public Task SaveAsync(List<Infobase> infobases, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public List<Group> LoadGroups() => new();

        public void SaveGroups(List<Group> groups)
        {
        }

        public Task SaveGroupsAsync(List<Group> groups, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public AppSettings LoadSettings() => Settings;

        public void SaveSettings(AppSettings settings) => Settings = settings;

        public Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            Settings = settings;
            return Task.CompletedTask;
        }
    }

    /// <summary>Fake логгер: сообщения игнорируются.</summary>
    private sealed class FakeLogger : IAppLogger
    {
        public void Info(string message)
        {
        }

        public void Warn(string message)
        {
        }

        public void Error(string message, Exception? exception = null)
        {
        }
    }
}
