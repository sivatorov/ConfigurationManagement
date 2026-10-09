using Configuration_Management.Services;
using Xunit;

namespace ConfigurationManagement.Tests;

/// <summary>
/// Тесты планирования докачки цепочки обновлений (issue #352, комментарий 7OH от
/// 2026-10-09): перед запуском загрузки файлы, уже скачанные ранее (существуют и
/// непусты), пропускаются — счётчик «Скачивается X из Y» строится только по реально
/// требующим скачивания файлам. Временный файл загрузки (.download) скачанным
/// не считается.
/// </summary>
public sealed class UpdateChainDownloadPlannerTests : IDisposable
{
    private readonly string _dir;

    public UpdateChainDownloadPlannerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"cm_chain_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // Не критично для теста.
        }
    }

    private string PathOf(string name) => Path.Combine(_dir, name);

    // ======================= IsDownloaded =======================

    [Fact]
    public void IsDownloaded_ExistingNonEmptyFile_True()
    {
        var path = PathOf("UT11_11.5.25.92.zip");
        File.WriteAllText(path, "data");

        Assert.True(UpdateChainDownloadPlanner.IsDownloaded(path));
    }

    [Fact]
    public void IsDownloaded_EmptyFile_False()
    {
        var path = PathOf("UT11_11.5.25.80.zip");
        File.WriteAllText(path, string.Empty);

        Assert.False(UpdateChainDownloadPlanner.IsDownloaded(path));
    }

    [Fact]
    public void IsDownloaded_MissingFile_False()
    {
        Assert.False(UpdateChainDownloadPlanner.IsDownloaded(PathOf("missing.zip")));
        Assert.False(UpdateChainDownloadPlanner.IsDownloaded(null));
        Assert.False(UpdateChainDownloadPlanner.IsDownloaded(string.Empty));
    }

    [Fact]
    public void IsDownloaded_TempDownloadFile_NotCounted()
    {
        // Временный файл загрузки (.download), даже непустой, скачанным не считается:
        // докачка пойдёт под финальное имя заново (issue #352).
        var tempPath = PathOf("UT11_11.5.25.80.zip") + OneCUpdatesService.PartialSuffix;
        File.WriteAllText(tempPath, "частично скачанные данные");

        Assert.False(UpdateChainDownloadPlanner.IsDownloaded(tempPath));
        Assert.False(UpdateChainDownloadPlanner.IsDownloaded(PathOf("UT11_11.5.25.80.zip")));
    }

    // ======================= SelectPendingSteps =======================

    [Fact]
    public void SelectPendingSteps_SkipsDownloaded_CountsOnlyPending()
    {
        // Сценарий 7OH: из 6 файлов цепочки 4 уже скачаны (в т.ч. остался .download
        // от прерванной загрузки и пустой файл) — докачать нужно только 2, счётчик
        // должен показывать «Скачивается 1 из 2», а не «2 из 6».
        File.WriteAllText(PathOf("v1.zip"), "ok");
        File.WriteAllText(PathOf("v2.zip"), "ok");
        File.WriteAllText(PathOf("v3.zip"), string.Empty);                    // пустой — качать
        // v4.zip отсутствует — качать
        File.WriteAllText(PathOf("v5.zip") + OneCUpdatesService.PartialSuffix, "partial"); // качать
        File.WriteAllText(PathOf("v6.zip"), "ok");

        var pending = UpdateChainDownloadPlanner.SelectPendingSteps(new[]
        {
            PathOf("v1.zip"), PathOf("v2.zip"), PathOf("v3.zip"),
            PathOf("v4.zip"), PathOf("v5.zip"), PathOf("v6.zip"),
        });

        Assert.Equal(new[] { 2, 3, 4 }, pending);
    }

    [Fact]
    public void SelectPendingSteps_AllDownloaded_EmptyPendingList()
    {
        // Если ВСЕ файлы уже скачаны — качать нечего (issue #352).
        foreach (var name in new[] { "v1.zip", "v2.zip", "v3.zip" })
            File.WriteAllText(PathOf(name), "ok");

        var pending = UpdateChainDownloadPlanner.SelectPendingSteps(new[]
        {
            PathOf("v1.zip"), PathOf("v2.zip"), PathOf("v3.zip"),
        });

        Assert.Empty(pending);
    }

    [Fact]
    public void SelectPendingSteps_NothingDownloaded_AllIndexesPending()
    {
        var paths = new[] { PathOf("a.zip"), PathOf("b.zip"), PathOf("c.zip") };

        var pending = UpdateChainDownloadPlanner.SelectPendingSteps(paths);

        Assert.Equal(new[] { 0, 1, 2 }, pending);
    }

    [Fact]
    public void SelectPendingSteps_EmptyList_ReturnsEmpty()
    {
        Assert.Empty(UpdateChainDownloadPlanner.SelectPendingSteps(Array.Empty<string>()));
    }
}
