using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Configuration_Management.Models;
using Configuration_Management.Services;
using Xunit;

namespace ConfigurationManagement.Tests;

/// <summary>
/// Тесты разрешения конечного адреса дистрибутива при скачивании обновления конфигурации
/// (issue #352): адрес из окна проверки обновлений — это цепочка страниц (каталог релизов →
/// страница файлов релиза → страница скачивания файла), а не сам архив. Раньше скачивалась
/// HTML-страница со списком релизов/страница скачивания файла, сохранённая как «.zip».
/// Сетевой стек — fake <see cref="HttpMessageHandler"/>, реальная сеть не используется.
/// </summary>
public sealed class OneCUpdatesDistributionResolutionTests
{
    private const string CatalogUrl = "https://releases.1c.ru/project/AccountingCorp30";

    private const string CatalogHtml = """
        <html><body>
        <table id="versionsTable">
          <tr><td><a href="/version_files?nick=AccountingCorp30&ver=3.0.206.19">3.0.206.19</a></td><td>3.0.167.18</td></tr>
          <tr><td><a href="/version_files?nick=AccountingCorp30&ver=3.0.167.18">3.0.167.18</a></td><td>3.0.142.32</td></tr>
        </table>
        </body></html>
        """;

    /// <summary>Страница файлов релиза: имя файла ведёт на ПРОМЕЖУТОЧНУЮ страницу
    /// скачивания этого файла (её адрес тоже заканчивается на «.zip»).</summary>
    private const string VersionFilesHtml = """
        <html><body>
        <table>
          <tr><td><a href="/total_buh/3_0_206_19/setup_1cv8.zip">Дистрибутив setup_1cv8.zip</a></td></tr>
          <tr><td><a href="/total_buh/3_0_206_19/readme.txt">readme.txt</a></td></tr>
        </table>
        </body></html>
        """;

    /// <summary>Реалистичная страница файлов релиза: ссылка на файл ведёт на промежуточную
    /// страницу скачивания version_file (адрес заканчивается на «.zip», как у Trade110).</summary>
    private const string RealLikeVersionFilesHtml = """
        <html><body>
        <table>
          <tr><td><a href="/version_file?nick=Trade110&ver=11.5.26.118&path=Trade\11_5_26_118\Trade_11_5_26_118_updsetup.zip">Дистрибутив Trade_11_5_26_118_updsetup.zip</a></td></tr>
        </table>
        </body></html>
        """;

    /// <summary>Промежуточная страница скачивания файла: реальный файл отдаётся
    /// по ссылке transfer_file.</summary>
    private const string FilePageHtml = """
        <html><body>
        <h1>Файл setup_1cv8.zip</h1>
        <a href="/transfer_file?file=setup_1cv8.zip">Скачать файл</a>
        </body></html>
        """;

    private static readonly byte[] DistributionBytes =
        { 0x50, 0x4B, 0x03, 0x04, 0x11, 0x22, 0x33, 0x44, 0x55 };

    // ======================= Скачивание через цепочку страниц =======================

    [Fact]
    public async Task DownloadUpdateAsync_FromCatalogPage_FollowsPagesAndSavesBinary()
    {
        // issue #352: кнопка «Скачать» передаёт адрес КАТАЛОГА релизов (project/<nick>).
        // Раньше он сохранялся как «.zip» — страница со списком релизов. Теперь сервис
        // переходит: каталог → version_files → страница файла → transfer_file → архив.
        var requests = new List<string>();
        var handler = new RoutingHandler(url =>
        {
            requests.Add(url);
            // ВАЖНО: transfer_file проверяется раньше setup_1cv8.zip —
            // имя файла входит в query-часть адреса передачи.
            if (url.Contains("transfer_file", StringComparison.OrdinalIgnoreCase))
                return Binary(DistributionBytes, "application/zip");
            if (url.Contains("/project/", StringComparison.OrdinalIgnoreCase))
                return Html(CatalogHtml);
            if (url.Contains("version_files", StringComparison.OrdinalIgnoreCase))
                return Html(VersionFilesHtml);
            return Html(FilePageHtml);
        });
        var logger = new FakeLogger();
        var service = new OneCUpdatesService(new FakeRepository(), logger, handler);
        var dir = Path.Combine(Path.GetTempPath(), $"cm_drt_{Guid.NewGuid():N}");
        var targetPath = Path.Combine(dir, "BP_3.0.206.19.zip");

        try
        {
            var saved = await service.DownloadUpdateAsync(CatalogUrl, targetPath);

            Assert.True(saved is not null,
                "saved is null; requests=" + string.Join(" | ", requests)
                + "; log=" + string.Join(" | ", logger.Messages));
            Assert.True(File.Exists(saved));
            Assert.Equal(DistributionBytes, await File.ReadAllBytesAsync(saved!));
            // Ровно 4 запроса: каталог, список файлов, страница файла, передача файла.
            Assert.Equal(4, requests.Count);
            Assert.Contains(requests, u => u.Contains("version_files", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(requests, u => u.Contains("transfer_file", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TryDeleteDirectory(dir);
        }
    }

    [Fact]
    public async Task DownloadUpdateAsync_FromVersionFilesPage_ResolvesRealFile()
    {
        // issue #352: шаг цепочки — ссылка version_files; раньше сохранялась страница
        // скачивания файла (адрес тоже «.zip»). Теперь содержимое страницы анализируется.
        var handler = new RoutingHandler(url =>
        {
            if (url.Contains("transfer_file", StringComparison.OrdinalIgnoreCase))
                return Binary(DistributionBytes, "application/zip");
            if (url.Contains("version_files", StringComparison.OrdinalIgnoreCase))
                return Html(VersionFilesHtml);
            return Html(FilePageHtml);
        });
        var logger = new FakeLogger();
        var service = new OneCUpdatesService(new FakeRepository(), logger, handler);
        var dir = Path.Combine(Path.GetTempPath(), $"cm_drt_{Guid.NewGuid():N}");
        var targetPath = Path.Combine(dir, "BP_3.0.206.19.zip");

        try
        {
            var saved = await service.DownloadUpdateAsync(
                "https://releases.1c.ru/version_files?nick=AccountingCorp30&ver=3.0.206.19",
                targetPath);

            Assert.True(saved is not null,
                "saved is null; log=" + string.Join(" | ", logger.Messages));
            Assert.Equal(DistributionBytes, await File.ReadAllBytesAsync(saved!));
        }
        finally
        {
            TryDeleteDirectory(dir);
        }
    }

    [Fact]
    public async Task DownloadUpdateAsync_FromVersionFilePage_ResolveByDownloadCaptionAnchor()
    {
        // issue #352 (комментарий 7OH от 2026-10-08): промежуточная страница скачивания
        // (version_file?...updsetup.zip) содержит кнопку «Скачать дистрибутив», чей href
        // НЕ содержит transfer_file и не заканчивается расширением дистрибутива. Парсер
        // должен перейти по ней по ПОДПИСИ кнопки.
        const string versionFilePageHtml = """
            <html><body>
              <h1>Trade 11.5.26.118</h1>
              <div class="download">
                <a href="/get_dist?rid=98765" class="btn btn-primary">Скачать дистрибутив</a>
              </div>
            </body></html>
            """;
        var requests = new List<string>();
        var handler = new RoutingHandler(url =>
        {
            requests.Add(url);
            if (url.Contains("get_dist", StringComparison.OrdinalIgnoreCase))
                return Binary(DistributionBytes, "application/octet-stream");
            if (url.Contains("version_files", StringComparison.OrdinalIgnoreCase))
                return Html(RealLikeVersionFilesHtml);
            return Html(versionFilePageHtml);
        });
        var service = CreateService(handler);
        var dir = Path.Combine(Path.GetTempPath(), $"cm_drt_{Guid.NewGuid():N}");
        var targetPath = Path.Combine(dir, "Trade_11.5.26.118.zip");

        try
        {
            var saved = await service.DownloadUpdateAsync(
                "https://releases.1c.ru/version_files?nick=Trade110&ver=11.5.26.118", targetPath);

            Assert.True(saved is not null,
                "saved is null; requests=" + string.Join(" | ", requests));
            Assert.Equal(DistributionBytes, await File.ReadAllBytesAsync(saved!));
            // Ровно 3 запроса: список файлов, страница скачивания файла, конечный файл.
            Assert.Equal(3, requests.Count);
            Assert.Contains(requests, u => u.Contains("version_file?", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(requests, u => u.Contains("get_dist", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TryDeleteDirectory(dir);
        }
    }

    [Fact]
    public async Task DownloadUpdateAsync_AdditionalFilePage_ResolvesCfDistribution()
    {
        // issue #352 (комментарий 7OH от 2026-10-08): ссылка additional_file на файл .cf —
        // промежуточная страница с кнопкой «Скачать файл»; итоговый файл сохраняется
        // с расширением .cf конечного адреса.
        const string additionalFilePageHtml = """
            <html><body>
              <h1>RasshirenieGISMTsRPT.cf</h1>
              <a href="/transfer_file?nick=Trade110&path=Trade%5cExtrafiles%5cRasshirenieGISMTsRPT.cf">Скачать файл</a>
            </body></html>
            """;
        var requests = new List<string>();
        var handler = new RoutingHandler(url =>
        {
            requests.Add(url);
            if (url.Contains("transfer_file", StringComparison.OrdinalIgnoreCase))
                return Binary(DistributionBytes, "application/octet-stream");
            return Html(additionalFilePageHtml);
        });
        var service = CreateService(handler);
        var dir = Path.Combine(Path.GetTempPath(), $"cm_drt_{Guid.NewGuid():N}");
        var targetPath = Path.Combine(dir, "RasshirenieGISMTsRPT.zip");

        try
        {
            var saved = await service.DownloadUpdateAsync(
                "https://releases.1c.ru/additional_file?nick=Trade110&path=Trade%5cExtrafiles%5cRasshirenieGISMTsRPT.cf",
                targetPath);

            Assert.True(saved is not null,
                "saved is null; requests=" + string.Join(" | ", requests));
            Assert.EndsWith(".cf", saved, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(DistributionBytes, await File.ReadAllBytesAsync(saved!));
            Assert.False(File.Exists(targetPath));
        }
        finally
        {
            TryDeleteDirectory(dir);
        }
    }

    [Fact]
    public async Task DownloadUpdateAsync_CaptionAnchorWithStubHref_UsesTransferFileFromBody()
    {
        // Запасной путь: кнопка «Скачать дистрибутив» — заглушка («#»), реальный адрес
        // передачи файла встречается только в onclick-обработчике (вне атрибута href).
        const string stubPageHtml = """
            <html><body>
              <a href="#" onclick="window.location='/transfer_file?file=setup_1cv8.zip'">Скачать дистрибутив</a>
            </body></html>
            """;
        var handler = new RoutingHandler(url =>
            url.Contains("transfer_file", StringComparison.OrdinalIgnoreCase)
                ? Binary(DistributionBytes, "application/zip")
                : Html(stubPageHtml));
        var service = CreateService(handler);
        var dir = Path.Combine(Path.GetTempPath(), $"cm_drt_{Guid.NewGuid():N}");
        var targetPath = Path.Combine(dir, "setup_1cv8.zip");

        try
        {
            var saved = await service.DownloadUpdateAsync(
                "https://releases.1c.ru/version_file?nick=X&ver=1&path=setup_1cv8.zip", targetPath);

            Assert.NotNull(saved);
            Assert.Equal(DistributionBytes, await File.ReadAllBytesAsync(saved!));
        }
        finally
        {
            TryDeleteDirectory(dir);
        }
    }

    [Fact]
    public async Task DownloadUpdateAsync_DirectBinaryUrl_SavesWithoutResolution()
    {
        var handler = new RoutingHandler(_ => Binary(DistributionBytes, "application/zip"));
        var service = CreateService(handler);
        var dir = Path.Combine(Path.GetTempPath(), $"cm_drt_{Guid.NewGuid():N}");
        var targetPath = Path.Combine(dir, "direct.zip");

        try
        {
            var saved = await service.DownloadUpdateAsync(
                "https://releases.1c.ru/transfer_file?file=setup_1cv8.zip", targetPath);

            Assert.NotNull(saved);
            Assert.Equal(DistributionBytes, await File.ReadAllBytesAsync(saved!));
            Assert.Single(handler.Requests);
        }
        finally
        {
            TryDeleteDirectory(dir);
        }
    }

    [Fact]
    public async Task DownloadUpdateAsync_AdditionalFileBinaryTypedAsHtml_SavesFileDirectly()
    {
        // issue #352 (комментарий 7OH от 2026-10-09): additional_file отдаёт сам файл
        // (расширение конфигурации .cf), ошибочно помечая его text/html — страница не
        // содержит ссылок, и ответ должен сохраняться как бинарный файл, а не приводить
        // к «В ответе не найдена ссылка на дистрибутив».
        const string url =
            "https://releases.1c.ru/additional_file?nick=Trade110&path=Trade%5cExtrafiles%5cRasshirenieGISMTsRPT.cf";
        var handler = new RoutingHandler(_ => Binary(DistributionBytes, "text/html"));
        var service = CreateService(handler);
        var dir = Path.Combine(Path.GetTempPath(), $"cm_drt_{Guid.NewGuid():N}");
        var targetPath = Path.Combine(dir, "RasshirenieGISMTsRPT.zip");

        try
        {
            var saved = await service.DownloadUpdateAsync(url, targetPath);

            Assert.NotNull(saved);
            Assert.EndsWith(".cf", saved, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(DistributionBytes, await File.ReadAllBytesAsync(saved!));
            Assert.Single(handler.Requests);
            // Временный файл загрузки (.download) после успеха переименован, не оставлен.
            Assert.False(File.Exists(saved + OneCUpdatesService.PartialSuffix));
        }
        finally
        {
            TryDeleteDirectory(dir);
        }
    }

    [Fact]
    public async Task DownloadUpdateAsync_TransferFileBinaryTypedAsHtml_SavesFileDirectly()
    {
        // transfer_file ведёт себя так же: binary-контент с HTML-типом сохраняется напрямую,
        // без попытки «искать следующую ссылку» (issue #352, комментарий 7OH 2026-10-09).
        var handler = new RoutingHandler(_ => Binary(DistributionBytes, "text/html"));
        var service = CreateService(handler);
        var dir = Path.Combine(Path.GetTempPath(), $"cm_drt_{Guid.NewGuid():N}");
        var targetPath = Path.Combine(dir, "setup_1cv8.zip");

        try
        {
            var saved = await service.DownloadUpdateAsync(
                "https://releases.1c.ru/transfer_file?file=setup_1cv8.zip", targetPath);

            Assert.NotNull(saved);
            Assert.Equal(DistributionBytes, await File.ReadAllBytesAsync(saved!));
            Assert.Single(handler.Requests);
        }
        finally
        {
            TryDeleteDirectory(dir);
        }
    }

    [Fact]
    public async Task DownloadUpdateAsync_AdditionalFileHtmlErrorPage_ReturnsNullWithoutSaving()
    {
        // Настоящий HTML-документ от файлового эндпоинта без ссылки на файл — ошибка
        // портала: мусор под именем дистрибутива не сохраняется (issue #352).
        var handler = new RoutingHandler(_ => Html(
            """<html><body><h1>Файл не найден</h1></body></html>"""));
        var service = CreateService(handler);
        var dir = Path.Combine(Path.GetTempPath(), $"cm_drt_{Guid.NewGuid():N}");
        var targetPath = Path.Combine(dir, "missing.zip");

        try
        {
            var saved = await service.DownloadUpdateAsync(
                "https://releases.1c.ru/additional_file?nick=X&path=Trade%5cmissing.cf", targetPath);

            Assert.Null(saved);
            Assert.False(File.Exists(targetPath));
            Assert.False(File.Exists(targetPath + OneCUpdatesService.PartialSuffix));
            Assert.Single(handler.Requests);
        }
        finally
        {
            TryDeleteDirectory(dir);
        }
    }

    [Fact]
    public async Task DownloadUpdateAsync_OnSaveError_RemovesTempFile()
    {
        // Прерванное скачивание (поток ответа упал mid-stream): временный файл .download
        // удаляется, финальное имя не создаётся — повторный запуск не сочтёт файл скачанным.
        var handler = new RoutingHandler(_ => ThrowingBinary(DistributionBytes.Length));
        var service = CreateService(handler);
        var dir = Path.Combine(Path.GetTempPath(), $"cm_drt_{Guid.NewGuid():N}");
        var targetPath = Path.Combine(dir, "broken.zip");

        try
        {
            var saved = await service.DownloadUpdateAsync(
                "https://releases.1c.ru/files/setup_1cv8.zip", targetPath);

            Assert.Null(saved);
            Assert.False(File.Exists(targetPath));
            Assert.False(File.Exists(targetPath + OneCUpdatesService.PartialSuffix));
        }
        finally
        {
            TryDeleteDirectory(dir);
        }
    }

    [Fact]
    public async Task DownloadUpdateAsync_RarDistribution_AdjustsTargetExtension()
    {
        // Диалог сохранения предлагает «.zip», а дистрибутив оказался «.rar» (issue #352):
        // итоговый файл получает расширение конечного адреса.
        var handler = new RoutingHandler(_ => Binary(DistributionBytes, "application/octet-stream"));
        var service = CreateService(handler);
        var dir = Path.Combine(Path.GetTempPath(), $"cm_drt_{Guid.NewGuid():N}");
        var targetPath = Path.Combine(dir, "BP_3.0.206.19.zip");

        try
        {
            var saved = await service.DownloadUpdateAsync(
                "https://releases.1c.ru/files/setup_1cv8.rar", targetPath);

            Assert.NotNull(saved);
            Assert.EndsWith(".rar", saved, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Combine(dir, "BP_3.0.206.19.rar")));
            Assert.False(File.Exists(targetPath));
        }
        finally
        {
            TryDeleteDirectory(dir);
        }
    }

    [Fact]
    public async Task DownloadUpdateAsync_SelfReferencingPage_ReturnsNullWithoutSaving()
    {
        // Страница, ссылающаяся сама на себя (единственный кандидат уже посещён):
        // зацикливание не допускается, файл не сохраняется.
        var handler = new RoutingHandler(_ => Html(
            """<html><a href="/version_files?nick=X&ver=1">1</a></html>"""));
        var service = CreateService(handler);
        var dir = Path.Combine(Path.GetTempPath(), $"cm_drt_{Guid.NewGuid():N}");
        var targetPath = Path.Combine(dir, "loop.zip");

        try
        {
            var saved = await service.DownloadUpdateAsync(
                "https://releases.1c.ru/version_files?nick=X&ver=1", targetPath);

            Assert.Null(saved);
            Assert.False(File.Exists(targetPath));
            Assert.Single(handler.Requests);
        }
        finally
        {
            TryDeleteDirectory(dir);
        }
    }

    [Fact]
    public async Task DownloadUpdateAsync_LoginPageInsteadOfDistribution_ReturnsNull()
    {
        var handler = new RoutingHandler(_ => Html(
            """<html><form id="login-form"><input type="text" name="username"/><input type="password" name="password"/></form></html>"""));
        var service = CreateService(handler);
        var dir = Path.Combine(Path.GetTempPath(), $"cm_drt_{Guid.NewGuid():N}");
        var targetPath = Path.Combine(dir, "auth.zip");

        try
        {
            var saved = await service.DownloadUpdateAsync(
                "https://releases.1c.ru/version_files?nick=X&ver=1", targetPath);

            Assert.Null(saved);
            Assert.False(File.Exists(targetPath));
        }
        finally
        {
            TryDeleteDirectory(dir);
        }
    }

    // ======================= Выбор следующей ссылки =======================

    [Fact]
    public void SelectDistributionCandidates_CatalogPage_FindsVersionFilesLinkOfLatestRelease()
    {
        var candidates = OneCUpdatesService.SelectDistributionCandidates(CatalogHtml);

        // На странице каталога нет архивов — кандидаты становятся ссылками version_files,
        // первая из них — последняя (самая новая) версия.
        Assert.NotEmpty(candidates);
        Assert.Contains("version_files", candidates[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("3.0.206.19", candidates[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SelectDistributionCandidates_ListingPage_PrefersSetupZipOverOtherLinks()
    {
        var candidates = OneCUpdatesService.SelectDistributionCandidates(VersionFilesHtml);

        Assert.NotEmpty(candidates);
        Assert.Contains("setup_1cv8.zip", candidates[0], StringComparison.OrdinalIgnoreCase);
        // Ссылки version_files на странице списка файлов отсутствуют — только дистрибутив.
        Assert.DoesNotContain(candidates, c => c.Contains("version_files", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SelectDistributionCandidates_FilePage_FindsTransferFileLink()
    {
        var candidates = OneCUpdatesService.SelectDistributionCandidates(FilePageHtml);

        Assert.NotEmpty(candidates);
        Assert.Contains("transfer_file", candidates[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SelectDistributionCandidates_DownloadCaptionAnchor_WithoutKnownExtension_FoundFirst()
    {
        // issue #352 (2026-10-08): кнопка «Скачать дистрибутив» ведёт на адрес без
        // transfer_file и без расширения дистрибутива — кандидат берётся по подписи.
        const string page = """
            <html><body>
              <a href="/get_dist?rid=98765" class="btn">Скачать дистрибутив</a>
              <a href="/version_files?nick=X&ver=1">Версия 1</a>
            </body></html>
            """;

        var candidates = OneCUpdatesService.SelectDistributionCandidates(page);

        Assert.NotEmpty(candidates);
        Assert.Equal("/get_dist?rid=98765", candidates[0]);
    }

    [Fact]
    public void SelectDistributionCandidates_DownloadCaptionWithNestedTags_Recognized()
    {
        // Подпись кнопки может быть размечена внутренними тегами: <span>Скачать</span> дистрибутив.
        const string page = """
            <html><body>
              <a href="/get_dist?rid=1"><span>Скачать</span> дистрибутив</a>
            </body></html>
            """;

        var candidates = OneCUpdatesService.SelectDistributionCandidates(page);

        Assert.NotEmpty(candidates);
        Assert.Equal("/get_dist?rid=1", candidates[0]);
    }

    [Fact]
    public void SelectDistributionCandidates_DownloadCaptionHtmlEncodedHref_Decoded()
    {
        // HTML-сущности в href («&») декодируются — иначе сервер получил бы битый адрес.
        const string page = """
            <html><body>
              <a href="/transfer_file?nick=X&ver=1&path=a.zip">Скачать дистрибутив</a>
            </body></html>
            """;

        var candidates = OneCUpdatesService.SelectDistributionCandidates(page);

        Assert.NotEmpty(candidates);
        Assert.Equal("/transfer_file?nick=X&ver=1&path=a.zip", candidates[0]);
    }

    [Fact]
    public void SelectDistributionCandidates_StubAnchorHrefs_Skipped()
    {
        // Заглушечные адреса кнопки («#», javascript:) не должны попадать в кандидаты.
        const string page = """
            <html><body>
              <a href="#" onclick="go()">Скачать дистрибутив</a>
              <a href="javascript:void(0)">Скачать файл</a>
            </body></html>
            """;

        var candidates = OneCUpdatesService.SelectDistributionCandidates(page);

        Assert.DoesNotContain(candidates, c => c.StartsWith("#", StringComparison.Ordinal));
        Assert.DoesNotContain(candidates, c => c.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SelectDistributionCandidates_TransferFileOutsideHref_Found()
    {
        // Адрес передачи файла встречается только в JS-редиректе — не в атрибуте href.
        const string page = """
            <html><body><script>
              window.location.href = '/transfer_file?nick=X&path=Trade\\setup_1cv8.zip';
            </script></body></html>
            """;

        var candidates = OneCUpdatesService.SelectDistributionCandidates(page);

        Assert.Contains(candidates, c => c.Contains("transfer_file", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SelectDistributionCandidates_AdditionalFileOutsideHref_Found()
    {
        // additional_file-адрес вне href (JS/JSON страницы) тоже распознаётся как кандидат.
        const string page = """
            <html><body><script>
              window.location.href = '/additional_file?nick=X&path=Trade\\Extrafiles\\file.cf';
            </script></body></html>
            """;

        var candidates = OneCUpdatesService.SelectDistributionCandidates(page);

        Assert.Contains(candidates, c => c.Contains("additional_file", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SelectDistributionCandidates_EmptyBody_ReturnsEmptyList()
    {
        Assert.Empty(OneCUpdatesService.SelectDistributionCandidates(string.Empty));
        Assert.Empty(OneCUpdatesService.SelectDistributionCandidates("   "));
    }

    // ======================= Расширение итогового файла =======================

    [Fact]
    public void AdjustTargetExtension_MatchingExtension_Unchanged()
    {
        var path = Path.Combine("dir", "file.zip");
        Assert.Equal(path, OneCUpdatesService.AdjustTargetExtension(
            path, "https://releases.1c.ru/files/setup_1cv8.zip"));
    }

    [Fact]
    public void AdjustTargetExtension_DifferentExtension_Changes()
    {
        var result = OneCUpdatesService.AdjustTargetExtension(
            Path.Combine("dir", "file.zip"), "https://releases.1c.ru/files/setup_1cv8.rar");
        Assert.Equal(Path.Combine("dir", "file.rar"), result);
    }

    [Fact]
    public void AdjustTargetExtension_NoKnownExtension_Unchanged()
    {
        var path = Path.Combine("dir", "file.zip");
        Assert.Equal(path, OneCUpdatesService.AdjustTargetExtension(
            path, "https://releases.1c.ru/transfer_file?file=setup_1cv8"));
        Assert.Equal(path, OneCUpdatesService.AdjustTargetExtension(path, null));
        Assert.Equal(path, OneCUpdatesService.AdjustTargetExtension(path, string.Empty));
    }

    [Fact]
    public void GetDistributionExtension_FileNameInQuery_Recognized()
    {
        // issue #352: адрес передачи файла несёт имя файла только в query
        // (transfer_file?path=…\Trade_11_5_26_118_updsetup.zip, …\RasshirenieGISMTsRPT.cf).
        Assert.Equal(".zip", OneCUpdatesService.GetDistributionExtension(
            "https://releases.1c.ru/transfer_file?nick=X&path=Trade%5c11_5%5csetup_updsetup.zip"));
        Assert.Equal(".cf", OneCUpdatesService.GetDistributionExtension(
            "https://releases.1c.ru/transfer_file?nick=X&path=Trade%5cExtrafiles%5cRasshirenieGISMTsRPT.cf"));
        Assert.Equal(".rar", OneCUpdatesService.GetDistributionExtension(
            "https://releases.1c.ru/transfer_file?file=setup_1cv8.rar"));
        // Без расширения — пусто (прежнее поведение).
        Assert.Equal(string.Empty, OneCUpdatesService.GetDistributionExtension(
            "https://releases.1c.ru/transfer_file?file=setup_1cv8"));
        Assert.Equal(string.Empty, OneCUpdatesService.GetDistributionExtension(
            "https://releases.1c.ru/transfer_file?nick=X&ver=1"));
    }

    [Fact]
    public void GetDistributionExtension_RecognizesDistributionExtensions_IgnoresQuery()
    {
        Assert.Equal(".zip", OneCUpdatesService.GetDistributionExtension(
            "https://releases.1c.ru/files/setup_1cv8.zip?param=1"));
        Assert.Equal(".rar", OneCUpdatesService.GetDistributionExtension("https://x.y/a.rar"));
        Assert.Equal(".7z", OneCUpdatesService.GetDistributionExtension("https://x.y/a.7z"));
        Assert.Equal(".exe", OneCUpdatesService.GetDistributionExtension("https://x.y/a.EXE"));
        Assert.Equal(".cf", OneCUpdatesService.GetDistributionExtension("https://x.y/1cv8.cf"));
        Assert.Equal(".cfu", OneCUpdatesService.GetDistributionExtension("https://x.y/1cv8.cfu"));
        Assert.Equal(string.Empty, OneCUpdatesService.GetDistributionExtension("https://x.y/page.html"));
        Assert.Equal(string.Empty, OneCUpdatesService.GetDistributionExtension(null));
    }

    // ======================= Вспомогательное =======================

    private static OneCUpdatesService CreateService(HttpMessageHandler handler)
        => new(new FakeRepository(), new FakeLogger(), handler);

    private static HttpResponseMessage Html(string body)
    {
        var content = new StringContent(body, System.Text.Encoding.UTF8, "text/html");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static HttpResponseMessage Binary(byte[] bytes, string mediaType)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    /// <summary>Бинарный ответ, чей поток падает исключением в середине чтения —
    /// имитация обрыва соединения при скачивании (issue #352).</summary>
    private static HttpResponseMessage ThrowingBinary(int totalLength)
    {
        var content = new StreamContent(new ThrowingReadStream(totalLength));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    /// <summary>Поток, отдающий половину байтов, а затем бросающий IOException.</summary>
    private sealed class ThrowingReadStream : Stream
    {
        private readonly byte[] _data;
        private int _position;

        public ThrowingReadStream(int totalLength)
        {
            _data = new byte[totalLength];
            for (var i = 0; i < totalLength; i++)
                _data[i] = (byte)(i % 251 + 1);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= _data.Length / 2)
                throw new IOException("Соединение оборвано (имитация, issue #352).");

            var toRead = Math.Min(count, _data.Length / 2 - _position);
            Array.Copy(_data, _position, buffer, offset, toRead);
            _position += toRead;
            return toRead;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static void TryDeleteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // Не критично для теста.
        }
    }

    /// <summary>Fake HTTP-обработчик: адрес запроса обрабатывается делегатом теста.</summary>
    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly Func<string, HttpResponseMessage> _responder;

        public RoutingHandler(Func<string, HttpResponseMessage> responder) => _responder = responder;

        public List<string> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            Requests.Add(url);
            var response = _responder(url);
            response.RequestMessage = request;
            return Task.FromResult(response);
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

    /// <summary>Fake логгер: все сообщения накапливаются в списке.</summary>
    private sealed class FakeLogger : IAppLogger
    {
        public List<string> Messages { get; } = new();

        public void Info(string message) => Messages.Add(message);

        public void Warn(string message) => Messages.Add(message);

        public void Error(string message, Exception? exception = null) => Messages.Add(message);
    }
}
