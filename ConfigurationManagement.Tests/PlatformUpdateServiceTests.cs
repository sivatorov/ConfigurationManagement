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
/// Тесты сервиса каталога технологической платформы 1С
/// (<see cref="PlatformUpdateService"/>) на fake-провайдере текста страницы —
/// без сетевых запросов: маппинг ошибок (пусто/сеть/отмена/авторизация/404),
/// получение списка версий, ленивая подгрузка файлов релиза и выбор дистрибутива
/// под ОС/разрядность.
/// </summary>
public sealed class PlatformUpdateServiceTests
{
    /// <summary>HTML каталога с таблицей #versionsTable (фикстура, как у парсера).</summary>
    private const string VersionsTableHtml = """
        <html><body>
        <table id="versionsTable">
          <tr><td><a href="/version_files?nick=Platform83&ver=8.3.27.2214">8.3.27.2214</a></td></tr>
          <tr><td><a href="/version_files?nick=Platform83&ver=8.3.27.1688">8.3.27.1688</a></td></tr>
          <tr><td><a href="/version_files?nick=Platform83&ver=8.3.10">8.3.10</a></td></tr>
          <tr><td><a href="/version_files?nick=Platform83&ver=8.3.9">8.3.9</a></td></tr>
        </table>
        </body></html>
        """;

    /// <summary>HTML страницы входа портала (маркер login.1c.ru).</summary>
    private const string LoginPageHtml = """
        <html><body>
        <form action="https://login.1c.ru/portal/login" method="post">
          <input type="text" name="username" />
        </form>
        </body></html>
        """;

    /// <summary>Ответ version_files: JSON со ссылками на zip/deb/rpm.</summary>
    private const string VersionFilesJson = """
        [{"FileName":"8.3.27.2214_x64.zip","url":"https://releases.1c.ru/dist/8.3.27.2214_x64.zip","size":314572800},
         {"FileName":"8.3.27.2214_x86.zip","url":"https://releases.1c.ru/dist/8.3.27.2214_x86.zip","size":241172480},
         {"FileName":"8.3.27.2214_amd64.deb","url":"https://releases.1c.ru/dist/8.3.27.2214_amd64.deb","size":161061273},
         {"FileName":"8.3.27.2214_arm64.tar.gz","url":"https://releases.1c.ru/dist/8.3.27.2214_arm64.tar.gz","size":33554432}]
        """;

    [Fact]
    public async Task GetAvailableReleasesAsync_SuccessfulHtml_ReturnsOkWithSortedReleases()
    {
        var service = CreateService(_ => Task.FromResult<string?>(VersionsTableHtml));

        var result = await service.GetAvailableReleasesAsync();

        Assert.Equal(PortalFetchStatus.Ok, result.Status);
        Assert.Equal(string.Empty, result.ErrorKey);
        Assert.Equal(new[] { "8.3.27.2214", "8.3.27.1688", "8.3.10", "8.3.9" },
            result.Releases.Select(r => r.Version));
    }

    [Fact]
    public async Task GetAvailableReleasesAsync_UsesProjectUrlForPlatformNick()
    {
        string? requestedUrl = null;
        var service = CreateService(url =>
        {
            requestedUrl = url;
            return Task.FromResult<string?>(VersionsTableHtml);
        });

        await service.GetAvailableReleasesAsync();

        // issue #330: каталог запрашивается с allUpdates=true — полный список версий.
        Assert.Equal(
            $"https://releases.1c.ru/project/{OneCPlatformCatalogParser.PlatformNick}?allUpdates=true#updates",
            requestedUrl);
    }

    [Fact]
    public async Task GetAvailableReleasesAsync_NullText_ReturnsNetworkError()
    {
        var service = CreateService(_ => Task.FromResult<string?>(null));

        var result = await service.GetAvailableReleasesAsync();

        Assert.Equal(PortalFetchStatus.NetworkError, result.Status);
        Assert.Equal(PlatformUpdateService.ErrorNetwork, result.ErrorKey);
    }

    [Fact]
    public async Task GetAvailableReleasesAsync_EmptyText_ReturnsNetworkError()
    {
        var service = CreateService(_ => Task.FromResult<string?>(string.Empty));

        var result = await service.GetAvailableReleasesAsync();

        Assert.Equal(PortalFetchStatus.NetworkError, result.Status);
    }

    [Fact]
    public async Task GetAvailableReleasesAsync_ProviderThrows_ReturnsNetworkError()
    {
        var service = CreateService(_ => throw new InvalidOperationException("boom"));

        var result = await service.GetAvailableReleasesAsync();

        Assert.Equal(PortalFetchStatus.NetworkError, result.Status);
        Assert.Equal(PlatformUpdateService.ErrorNetwork, result.ErrorKey);
    }

    [Fact]
    public async Task GetAvailableReleasesAsync_Cancelled_ReturnsCancelled()
    {
        var service = CreateService(_ => throw new OperationCanceledException());

        var result = await service.GetAvailableReleasesAsync();

        Assert.Equal(PortalFetchStatus.Cancelled, result.Status);
        Assert.Equal(PlatformUpdateService.ErrorCancelled, result.ErrorKey);
    }

    [Fact]
    public async Task GetAvailableReleasesAsync_LoginPageText_ReturnsAuthRequired()
    {
        var service = CreateService(_ => Task.FromResult<string?>(LoginPageHtml));

        var result = await service.GetAvailableReleasesAsync();

        Assert.Equal(PortalFetchStatus.AuthRequired, result.Status);
        Assert.Equal(PlatformUpdateService.ErrorAuthRequired, result.ErrorKey);
    }

    [Fact]
    public async Task GetAvailableReleasesAsync_NotFoundMarker_ReturnsNotFound()
    {
        var service = CreateService(_ => Task.FromResult<string?>("<html><title>404 Not Found</title></html>"));

        var result = await service.GetAvailableReleasesAsync();

        Assert.Equal(PortalFetchStatus.NotFound, result.Status);
        Assert.Equal(PlatformUpdateService.ErrorNotFound, result.ErrorKey);
    }

    [Fact]
    public async Task GetAvailableReleasesForNickAsync_Platform85_BuildsUrlAndParses()
    {
        // issue #334: пожелание проверять и releases.1c.ru/project/Platform85.
        const string html = """
            <html><body>
            <table id="versionsTable">
              <tr><td><a href="/version_files?nick=Platform85&ver=8.5.1.123">8.5.1.123</a></td></tr>
            </table>
            </body></html>
            """;
        string? requestedUrl = null;
        var service = CreateService(url =>
        {
            requestedUrl = url;
            return Task.FromResult<string?>(html);
        });

        var result = await service.GetAvailableReleasesForNickAsync(OneCPlatformCatalogParser.Platform85Nick);

        Assert.Equal(PortalFetchStatus.Ok, result.Status);
        // issue #330/#334: Platform85 запрашивается с allUpdates=true.
        Assert.Equal("https://releases.1c.ru/project/Platform85?allUpdates=true#updates", requestedUrl);
        Assert.Equal("8.5.1.123", result.Releases[0].Version);
    }

    [Fact]
    public async Task GetAvailableReleasesForNickAsync_EmptyNick_FallsBackToPlatform83()
    {
        string? requestedUrl = null;
        var service = CreateService(url =>
        {
            requestedUrl = url;
            return Task.FromResult<string?>(VersionsTableHtml);
        });

        var result = await service.GetAvailableReleasesForNickAsync("   ");

        Assert.Equal(PortalFetchStatus.Ok, result.Status);
        // issue #330: даже для пустого ника каталог запрашивается с allUpdates=true.
        Assert.Equal("https://releases.1c.ru/project/Platform83?allUpdates=true#updates", requestedUrl);
    }

    [Fact]
    public async Task GetAvailableReleasesAsync_Login401_PortalAuthFailedStatus_ReturnsAuthFailedKey()
    {
        // Issue #334: после POST учётных данных сервер отвечает 401 — окно должно показать
        // «вход не подтверждён», а НЕ «каталог не получен — NetworkError» (как в логе 21:45:17).
        var handler = new PortalAuthFailHandler();
        var repo = new MemRepoSettings();
        repo.Settings.UpdatesLogin = "its-user";
        repo.Settings.UpdatesPassword = "secret";
        var logger = new StubLogger();
        var updates = new OneCUpdatesService(repo, logger, handler);
        var service = new PlatformUpdateService(updates, logger);

        var result = await service.GetAvailableReleasesAsync();

        Assert.Equal(PortalFetchStatus.AuthFailed, result.Status);
        Assert.Equal(PlatformUpdateService.ErrorAuthFailed, result.ErrorKey);
    }

    [Fact]
    public async Task GetAvailableReleasesAsync_FormUnavailable_PortalReturnsFormUnavailableKey()
    {
        // Третья итерация CAS (issue #323/#330/#334): форма входа изменилась радикально
        // (OAuth/JS-челлендж) — маппинг в отдельный ключ FormUnavailable с понятным текстом,
        // а НЕ вводящий в заблуждение NetworkError/AuthRequired.
        var handler = new PortalOAuthChallengeHandler();
        var repo = new MemRepoSettings();
        repo.Settings.UpdatesLogin = "its-user";
        repo.Settings.UpdatesPassword = "secret";
        var logger = new StubLogger();
        var updates = new OneCUpdatesService(repo, logger, handler);
        var service = new PlatformUpdateService(updates, logger);

        var result = await service.GetAvailableReleasesAsync();

        Assert.Equal(PortalFetchStatus.FormUnavailable, result.Status);
        Assert.Equal(PlatformUpdateService.ErrorAuthFormUnavailable, result.ErrorKey);
    }

    [Fact]
    public async Task LoadReleaseFilesAsync_FillsFilesAndSavesAbsoluteVersionFilesUrl()
    {
        var release = new PlatformRelease { Version = "8.3.27.2214", VersionFilesUrl = "/version_files?nick=Platform83&ver=8.3.27.2214" };
        var service = CreateService(_ => Task.FromResult<string?>(VersionFilesJson));

        var result = await service.LoadReleaseFilesAsync(release);

        Assert.Equal(PortalFetchStatus.Ok, result.Status);
        Assert.Same(release, result.Release);
        Assert.Equal("https://releases.1c.ru/version_files?nick=Platform83&ver=8.3.27.2214", release.VersionFilesUrl);
        Assert.Equal(4, release.Files.Count);
        Assert.Contains(release.Files, f => f.FileName == "8.3.27.2214_x64.zip" && f.Kind == PlatformDistributionKind.WindowsSetupZip);
        Assert.Contains(release.Files, f => f.FileName == "8.3.27.2214_amd64.deb" && f.Kind == PlatformDistributionKind.LinuxDeb);
    }

    [Fact]
    public async Task LoadReleaseFilesAsync_EmptyVersionFilesUrl_BuildsFromVersion()
    {
        var release = new PlatformRelease { Version = "8.3.27.2214" };
        string? requestedUrl = null;
        var service = CreateService(url =>
        {
            requestedUrl = url;
            return Task.FromResult<string?>(VersionFilesJson);
        });

        var result = await service.LoadReleaseFilesAsync(release);

        Assert.Equal(PortalFetchStatus.Ok, result.Status);
        Assert.Equal("https://releases.1c.ru/version_files?nick=Platform83&ver=8.3.27.2214", requestedUrl);
    }

    [Fact]
    public async Task CatalogHtmlEntitiesInHref_LoadReleaseFiles_RequestsDecodedUrl()
    {
        // issue #330 (комментарий 7OH): реальный HTML каталога содержит & в href
        // version_files-ссылок. Ранее адрес уходил на портал с параметром «amp;ver»,
        // сервер отвечал страницей без дистрибутивов — список «Выбор файла» был пуст.
        const string htmlWithEntities = """
            <html><body>
            <table id="versionsTable">
              <tr><td><a href="/version_files?nick=Platform83&ver=8.3.27.2214">8.3.27.2214</a></td></tr>
              <tr><td><a href="/version_files?nick=Platform83&ver=8.3.27.1688">8.3.27.1688</a></td></tr>
            </table>
            </body></html>
            """;
        string? requestedUrl = null;
        var service = CreateService(url =>
        {
            requestedUrl = url;
            return Task.FromResult<string?>(
                url.Contains("project/", StringComparison.OrdinalIgnoreCase)
                    ? htmlWithEntities
                    : VersionFilesJson);
        });

        var catalog = await service.GetAvailableReleasesAsync();
        Assert.Equal(PortalFetchStatus.Ok, catalog.Status);

        var result = await service.LoadReleaseFilesAsync(catalog.Releases[0]);

        Assert.Equal(PortalFetchStatus.Ok, result.Status);
        Assert.Equal(
            "https://releases.1c.ru/version_files?nick=Platform83&ver=8.3.27.2214",
            requestedUrl);
        Assert.Equal(4, catalog.Releases[0].Files.Count);
    }

    [Fact]
    public async Task LoadReleaseFilesAsync_TransferFileMarkup_ParsesFilesAndMakesUrlsAbsolute()
    {
        // issue #330 (комментарий 7OH от 2026-10-09): альтернативная разметка страницы
        // version_files — дистрибутивы перечислены эндпоинтами transfer_file (без прямых
        // ссылок на архивы в href). Парсер распознаёт файлы по query-параметру path,
        // сервис делает адреса абсолютными (иначе не скачиваются).
        const string transferMarkup = """
            <html><body>
            <table>
              <tr><td>Технологическая платформа 8.3 для Windows (64-бит)</td>
                  <td><a href="transfer_file?nick=Platform83&path=Distr%2Fsetup_8_3_27_2214_x64.zip">Скачать</a></td></tr>
              <tr><td>Технологическая платформа 8.3 для Linux (deb)</td>
                  <td><a href="/transfer_file?nick=Platform83&path=Distr%2F8_3_27_2214_amd64.deb">Скачать</a></td></tr>
            </table>
            </body></html>
            """;
        var release = new PlatformRelease { Version = "8.3.27.2214", VersionFilesUrl = "/version_files?nick=Platform83&ver=8.3.27.2214" };
        var service = CreateService(_ => Task.FromResult<string?>(transferMarkup));

        var result = await service.LoadReleaseFilesAsync(release);

        Assert.Equal(PortalFetchStatus.Ok, result.Status);
        Assert.Equal(2, release.Files.Count);
        Assert.Contains(release.Files, f => f.FileName == "setup_8_3_27_2214_x64.zip"
            && f.Kind == PlatformDistributionKind.WindowsSetupZip
            && f.Url.Contains("transfer_file?nick=Platform83&path=Distr%2Fsetup_8_3_27_2214_x64.zip", StringComparison.Ordinal));
        Assert.Contains(release.Files, f => f.FileName == "8_3_27_2214_amd64.deb"
            && f.Kind == PlatformDistributionKind.LinuxDeb
            && f.Url.StartsWith("https://releases.1c.ru/transfer_file", StringComparison.Ordinal));

        // Диагностика (issue #330): URL, длина ответа и число распознанных файлов.
        Assert.Equal("https://releases.1c.ru/version_files?nick=Platform83&ver=8.3.27.2214", result.FetchedUrl);
        Assert.Equal(transferMarkup.Length, result.BodyLength);
        Assert.Equal(2, result.ParsedFileCount);
    }

    [Fact]
    public async Task LoadReleaseFilesAsync_RelativeFileLinks_MadeAbsolute()
    {
        // Прямые ссылки на архивы могут быть относительными (/version_files/get/…):
        // ViewModel передаёт адрес напрямую в загрузчик — он обязан стать абсолютным.
        const string relativeMarkup = """
            <a href="/version_files/get/8.3.27.2214_x64.zip">x64</a>
            """;
        var release = new PlatformRelease { Version = "8.3.27.2214", VersionFilesUrl = "/version_files?nick=Platform83&ver=8.3.27.2214" };
        var service = CreateService(_ => Task.FromResult<string?>(relativeMarkup));

        var result = await service.LoadReleaseFilesAsync(release);

        Assert.Equal(PortalFetchStatus.Ok, result.Status);
        var zip = Assert.Single(release.Files);
        Assert.StartsWith("https://releases.1c.ru/", zip.Url, StringComparison.Ordinal);
        Assert.EndsWith("version_files/get/8.3.27.2214_x64.zip", zip.Url, StringComparison.Ordinal);
    }

    // --- PickDistribution / PickForPlatform ---

    [Fact]
    public void PickForPlatform_Windows_PrefersX64ZipOverX86()
    {
        var files = new List<PlatformReleaseFile>
        {
            NewFile("setup_x86.zip", "x86", PlatformDistributionKind.WindowsSetupZip),
            NewFile("setup_x64.zip", "x64", PlatformDistributionKind.WindowsSetupZip),
        };

        var picked = PlatformUpdateService.PickForPlatform(files, isWindows: true);

        Assert.NotNull(picked);
        Assert.Equal("setup_x64.zip", picked.FileName);
    }

    [Fact]
    public void PickForPlatform_Windows_OnlyX86Zip_PicksX86()
    {
        var files = new List<PlatformReleaseFile>
        {
            NewFile("setup_x86.zip", "x86", PlatformDistributionKind.WindowsSetupZip),
        };

        var picked = PlatformUpdateService.PickForPlatform(files, isWindows: true);

        Assert.Equal("setup_x86.zip", picked!.FileName);
    }

    [Fact]
    public void PickForPlatform_Windows_NoZip_ReturnsNull()
    {
        var files = new List<PlatformReleaseFile>
        {
            NewFile("8.3.27.2214_amd64.deb", "x64", PlatformDistributionKind.LinuxDeb),
        };

        var picked = PlatformUpdateService.PickForPlatform(files, isWindows: true);

        Assert.Null(picked);
    }

    [Fact]
    public void PickForPlatform_Linux_PrefersX64DebOverX86Rpm()
    {
        var files = new List<PlatformReleaseFile>
        {
            NewFile("8.3.27.2214_i386.rpm", "x86", PlatformDistributionKind.LinuxRpm),
            NewFile("8.3.27.2214_amd64.deb", "x64", PlatformDistributionKind.LinuxDeb),
        };

        var picked = PlatformUpdateService.PickForPlatform(files, isWindows: false);

        Assert.Equal("8.3.27.2214_amd64.deb", picked!.FileName);
    }

    [Fact]
    public void PickForPlatform_Linux_NoX64Package_PicksAnyPackage()
    {
        var files = new List<PlatformReleaseFile>
        {
            NewFile("8.3.27.2214_i386.rpm", "x86", PlatformDistributionKind.LinuxRpm),
            NewFile("8.3.27.2214.tar.gz", null, PlatformDistributionKind.LinuxTarGz),
        };

        var picked = PlatformUpdateService.PickForPlatform(files, isWindows: false);

        Assert.Equal("8.3.27.2214_i386.rpm", picked!.FileName);
    }

    [Fact]
    public void PickForPlatform_Linux_OnlyTarGz_PicksTarGz()
    {
        var files = new List<PlatformReleaseFile>
        {
            NewFile("8.3.27.2214_arm64.tar.gz", null, PlatformDistributionKind.LinuxTarGz),
        };

        var picked = PlatformUpdateService.PickForPlatform(files, isWindows: false);

        Assert.Equal("8.3.27.2214_arm64.tar.gz", picked!.FileName);
    }

    [Fact]
    public void PickForPlatform_EmptyList_ReturnsNull()
    {
        Assert.Null(PlatformUpdateService.PickForPlatform(new List<PlatformReleaseFile>(), isWindows: true));
        Assert.Null(PlatformUpdateService.PickForPlatform(new List<PlatformReleaseFile>(), isWindows: false));
        Assert.Null(PlatformUpdateService.PickForPlatform(null!, isWindows: true));
    }

    /// <summary>Обработчик: каталог → 302 на login.1c.ru, форма с execution, POST → 401
    /// (учётные данные не приняты). Для теста AuthFailed (issue #334).</summary>
    private sealed class PortalAuthFailHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            HttpResponseMessage response;

            if (path.Contains("/project/", StringComparison.OrdinalIgnoreCase))
            {
                response = Redirect(new Uri("https://login.1c.ru/login?service=x"));
            }
            else if (path.Contains("/login", StringComparison.OrdinalIgnoreCase))
            {
                response = request.Method == HttpMethod.Post
                    ? new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("<html>нет</html>") }
                    : Ok("<form><input type=\"hidden\" name=\"execution\" value=\"e1s2\"/></form>");
            }
            else
            {
                response = new HttpResponseMessage(HttpStatusCode.Found);
            }

            response.RequestMessage = request;
            return Task.FromResult(response);
        }

        private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body),
        };

        private static HttpResponseMessage Redirect(Uri location)
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = location;
            return response;
        }
    }

    /// <summary>Обработчик: каталог → 302 на login.1c.ru, GET формы возвращает HTML БЕЗ execution/lt,
    /// но с маркерами OAuth/JS-челленджа — программный вход невозможен (issue #323/#330/#334).</summary>
    private sealed class PortalOAuthChallengeHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            HttpResponseMessage response;

            if (path.Contains("/project/", StringComparison.OrdinalIgnoreCase))
            {
                response = Redirect(new Uri("https://login.1c.ru/login?service=x"));
            }
            else if (path.Contains("/login", StringComparison.OrdinalIgnoreCase))
            {
                response = Ok(
                    """<html><script src="/oauth/authorize?client_id=portal"></script><body>challenge</body></html>""");
            }
            else
            {
                response = new HttpResponseMessage(HttpStatusCode.Found);
            }

            response.RequestMessage = request;
            return Task.FromResult(response);
        }

        private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body),
        };

        private static HttpResponseMessage Redirect(Uri location)
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = location;
            return response;
        }
    }

    /// <summary>Репозиторий с настройками в памяти (для входных данных авторизации).</summary>
    private sealed class MemRepoSettings : IInfobaseRepository
    {
        public AppSettings Settings { get; set; } = new();

        public List<Infobase> Load() => new();
        public void Save(List<Infobase> infobases) { }
        public Task SaveAsync(List<Infobase> infobases, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public List<Group> LoadGroups() => new();
        public void SaveGroups(List<Group> groups) { }
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

    /// <summary>Создаёт сервис с инжектируемым провайдером текста страницы.</summary>
    private static PlatformUpdateService CreateService(Func<string, Task<string?>> provider)
    {
        return new PlatformUpdateService(new StubUpdates(), new StubLogger(), (url, _) => provider(url));
    }

    /// <summary>Создаёт файл дистрибутива для тестов выбора.</summary>
    private static PlatformReleaseFile NewFile(string fileName, string? architecture, PlatformDistributionKind kind)
        => new()
        {
            FileName = fileName,
            Url = $"https://releases.1c.ru/dist/{fileName}",
            Architecture = architecture,
            Kind = kind,
        };

    /// <summary>Заглушка IOneCUpdatesService: сетевые методы не используются (fake-провайдер).</summary>
    private sealed class StubUpdates : IOneCUpdatesService
    {
        public IReadOnlyList<OneCConfigType> BuiltInConfigTypes => Array.Empty<OneCConfigType>();

        public string BuildUpdateUrl(OneCConfigType? config, OneCConfigEdition? edition, string? urlOverride, string? urlSegment = null)
            => string.Empty;

        public Task<ConfigUpdateCheckResult> CheckForUpdatesAsync(
            string configName, string currentVersion, string url, CancellationToken ct = default)
            => Task.FromResult(new ConfigUpdateCheckResult());

        public Task<string?> DownloadUpdateAsync(
            string url, string targetPath, IProgress<double>? progress = null, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public Task<string?> DownloadDistributionAsync(
            string url, string targetPath, IProgress<double>? progress = null, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public Task<string?> GetPageTextAsync(string url, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public Task<PortalPageResult> FetchPageAsync(string url, CancellationToken ct = default)
            => Task.FromResult(new PortalPageResult { Status = PortalFetchStatus.NetworkError });

        public Task<ConfigUpdateCatalogResult> GetUpdateCatalogAsync(string url, CancellationToken ct = default)
            => Task.FromResult(new ConfigUpdateCatalogResult { Status = PortalFetchStatus.NetworkError });
    }

    /// <summary>Заглушка журнала приложения.</summary>
    private sealed class StubLogger : IAppLogger
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