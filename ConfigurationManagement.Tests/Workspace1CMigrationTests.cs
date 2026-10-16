using System.Text.Json;
using Configuration_Management.Models;
using Configuration_Management.Services;
using Xunit;

namespace ConfigurationManagement.Tests;

/// <summary>
/// Тесты сервиса перехода на Workspace 1C (0.3.12.70): выбор ассета по ОС,
/// разбор ответа GitHub API, флаг показа диалога и сохранение в настройки.
/// </summary>
public sealed class Workspace1CMigrationTests
{
    private static JsonElement ParseRoot(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static string ReleaseJson(params (string Name, long Size)[] assets)
    {
        var items = string.Join(",", assets.Select(a =>
            $"{{\"name\":\"{a.Name}\",\"size\":{a.Size},\"browser_download_url\":\"https://github.com/sivatorov/Workspace1C/releases/download/v1.0.0.0/{Uri.EscapeDataString(a.Name)}\"}}"));
        return $"{{\"tag_name\":\"v1.0.0.0\",\"name\":\"Workspace 1C 1.0.0.0\",\"html_url\":\"https://github.com/sivatorov/Workspace1C/releases/tag/v1.0.0.0\",\"assets\":[{items}]}}";
    }

    // ===================== Выбор ассета по ОС =====================

#if !LINUX
    [Fact]
    public void FindAsset_SelectsWindowsExe()
    {
        // В релизе есть оба ассета — Windows-версия должна выбрать Workspace1C.exe.
        var root = ParseRoot(ReleaseJson(
            ("Workspace1C-linux-x64", 55_000_000),
            ("Workspace1C.exe", 66_000_000)));

        var (url, size) = Workspace1CMigrationService.FindAsset(root);

        Assert.Contains("Workspace1C.exe", url, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(66_000_000, size);
    }
#else
    [Fact]
    public void FindAsset_SelectsLinuxX64()
    {
        // В релизе есть оба ассета — Linux-версия должна выбрать Workspace1C-linux-x64.
        var root = ParseRoot(ReleaseJson(
            ("Workspace1C.exe", 66_000_000),
            ("Workspace1C-linux-x64", 55_000_000)));

        var (url, size) = Workspace1CMigrationService.FindAsset(root);

        Assert.Contains("Workspace1C-linux-x64", url, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(55_000_000, size);
    }
#endif

    [Fact]
    public void FindAsset_NoMatchingAsset_ReturnsEmpty()
    {
        // В релизе только посторонние файлы — подходящего ассета нет.
        var root = ParseRoot(ReleaseJson(("SHA256SUMS.txt", 500)));

        var (url, size) = Workspace1CMigrationService.FindAsset(root);

        Assert.Equal(string.Empty, url);
        Assert.Equal(0, size);
    }

    [Fact]
    public void FindAsset_EmptyAssets_ReturnsEmpty()
    {
        var root = ParseRoot("{\"assets\":[]}");

        var (url, size) = Workspace1CMigrationService.FindAsset(root);

        Assert.Equal(string.Empty, url);
        Assert.Equal(0, size);
    }

#if !LINUX
    [Fact]
    public void IsPlatformAsset_AcceptsWindowsExe()
    {
        Assert.True(Workspace1CMigrationService.IsPlatformAsset("Workspace1C.exe"));
        Assert.True(Workspace1CMigrationService.IsPlatformAsset("Workspace1C-1.0.0.0.exe"));
        // Чужой ассет платформы и архив не подходят.
        Assert.False(Workspace1CMigrationService.IsPlatformAsset("Workspace1C-linux-x64"));
        Assert.False(Workspace1CMigrationService.IsPlatformAsset("Workspace1C.zip"));
        Assert.False(Workspace1CMigrationService.IsPlatformAsset("SHA256SUMS.txt"));
    }
#else
    [Fact]
    public void IsPlatformAsset_AcceptsLinuxX64()
    {
        Assert.True(Workspace1CMigrationService.IsPlatformAsset("Workspace1C-linux-x64"));
        Assert.True(Workspace1CMigrationService.IsPlatformAsset("Workspace1C-1.0.0.0-linux-x64"));
        // Чужой ассет платформы и архив не подходят.
        Assert.False(Workspace1CMigrationService.IsPlatformAsset("Workspace1C.exe"));
        Assert.False(Workspace1CMigrationService.IsPlatformAsset("Workspace1C.zip"));
        Assert.False(Workspace1CMigrationService.IsPlatformAsset("SHA256SUMS.txt"));
    }
#endif

    // ===================== Разбор ответа GitHub API =====================

    [Fact]
    public void ParseRelease_ExtractsTagAndAsset()
    {
        var release = Workspace1CMigrationService.ParseRelease(ReleaseJson(
            ("Workspace1C.exe", 66_000_000)));

        Assert.NotNull(release);
        Assert.Equal("v1.0.0.0", release!.TagName);
        Assert.Equal("Workspace 1C 1.0.0.0", release.Name);
        Assert.Contains("Workspace1C", release.DownloadUrl, StringComparison.OrdinalIgnoreCase);
        Assert.True(release.AssetSize > 0);
    }

    [Fact]
    public void ParseRelease_InvalidJson_ReturnsNull()
    {
        Assert.Null(Workspace1CMigrationService.ParseRelease("not json"));
        Assert.Null(Workspace1CMigrationService.ParseRelease("[]"));
        Assert.Null(Workspace1CMigrationService.ParseRelease(""));
    }

    [Fact]
    public void ParseRelease_ReleaseWithoutAssets_HasNoDownloadUrl()
    {
        var release = Workspace1CMigrationService.ParseRelease(
            "{\"tag_name\":\"v1.0.0.0\",\"name\":\"Workspace 1C\",\"assets\":[]}");

        Assert.NotNull(release);
        Assert.True(string.IsNullOrEmpty(release!.DownloadUrl));
    }

    // ===================== Флаг показа диалога =====================

    [Fact]
    public void AppSettings_Workspace1CMigrationShown_DefaultsToFalse()
    {
        // Новый пользователь: диалог перехода ещё не показывался.
        var settings = new AppSettings();
        Assert.False(settings.Workspace1CMigrationShown);
    }

    [Fact]
    public void AppSettings_Workspace1CMigrationShown_CanBeSet()
    {
        var settings = new AppSettings { Workspace1CMigrationShown = true };
        Assert.True(settings.Workspace1CMigrationShown);
    }

    // ===================== Сохранение в настройки =====================

    [Fact]
    public void SaveSettings_RoundTrip_Workspace1CMigrationShown()
    {
        using var dir = new TempDir();
        var repo = new InfobaseRepository(directory: dir.Path);

        repo.SaveSettings(new AppSettings { Workspace1CMigrationShown = true });
        var loaded = repo.LoadSettings();

        Assert.True(loaded.Workspace1CMigrationShown);
    }

    [Fact]
    public void SaveSettings_DefaultFlag_IsFalseAfterRoundTrip()
    {
        using var dir = new TempDir();
        var repo = new InfobaseRepository(directory: dir.Path);

        repo.SaveSettings(new AppSettings());
        var loaded = repo.LoadSettings();

        Assert.False(loaded.Workspace1CMigrationShown);
    }

    /// <summary>Временный каталог данных репозитория, удаляемый вместе с содержимым.</summary>
    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cm-workspace1c-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Уборка временных файлов не должна ронять тест.
            }
        }
    }
}