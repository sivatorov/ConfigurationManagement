using System.Text.Json;
using Configuration_Management.Services;
using Xunit;

namespace ConfigurationManagement.Tests;

/// <summary>
/// Тесты выбора ассета релиза в GitHubReleaseService (issue #358): точное имя
/// «ConfigurationManagement.exe» (bare exe) побеждает ZIP-архив с признаком
/// «win-x64»; ZIP-архивы вообще не должны выбираться загрузчиком автообновления.
/// </summary>
public sealed class GitHubReleaseServiceTests
{
#if !LINUX
    private const string ExactAssetName = "ConfigurationManagement.exe";
#else
    private const string ExactAssetName = "ConfigurationManagement";
#endif

    private static JsonElement ParseAssets(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static string AssetsJson(params (string Name, long Size)[] assets)
    {
        var items = string.Join(",", assets.Select(a =>
            $"{{\"name\":\"{a.Name}\",\"size\":{a.Size},\"browser_download_url\":\"https://github.com/sivatorov/ConfigurationManagement/releases/download/v0.3.11.0/{Uri.EscapeDataString(a.Name)}\"}}"));
        return $"{{\"assets\":[{items}]}}";
    }

    [Fact]
    public void FindAsset_ExactNameBeatsWinX64Zip()
    {
        var root = ParseAssets(AssetsJson(
            ("ConfigurationManagement-0.3.11.0-win-x64.zip", 77_000_000),
            (ExactAssetName, 66_000_000)));

        var (url, size) = GitHubReleaseService.FindAsset(root);

        Assert.Contains("/" + Uri.EscapeDataString(ExactAssetName), url, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(66_000_000, size);
    }

    [Fact]
    public void FindAsset_ZipOnlyAsset_IsNotSelected()
    {
        // В релизе нет bare-exe — загрузчик НЕ должен взять zip «win-x64» (issue #358).
        var root = ParseAssets(AssetsJson(
            ("ConfigurationManagement-0.3.11.0-win-x64.zip", 77_000_000)));

        var (url, size) = GitHubReleaseService.FindAsset(root);

        Assert.Equal(string.Empty, url);
        Assert.Equal(0, size);
    }

    [Fact]
    public void FindAsset_FallsBackToNonZipWinX64Asset()
    {
#if !LINUX
        // Bare-exe отсутствует, но есть некий exe с признаком win-x64 — допустимый запасной путь.
        var root = ParseAssets(AssetsJson(("ConfigurationManagement-0.3.11.0-win-x64.pdb.exe", 55_000_000)));

        var (url, size) = GitHubReleaseService.FindAsset(root);

        Assert.False(string.IsNullOrEmpty(url));
        Assert.Equal(55_000_000, size);
#endif
    }

    [Fact]
    public void FindAsset_ExactNameWinsRegardlessOfOrder()
    {
        var root = ParseAssets(AssetsJson(
            ("ConfigurationManagement-0.3.11.0-win-x64.7z", 10_000_000),
            (ExactAssetName, 66_000_000),
            ("SHA256SUMS.txt", 500)));

        var (url, size) = GitHubReleaseService.FindAsset(root);

        Assert.Contains("/" + Uri.EscapeDataString(ExactAssetName), url, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(66_000_000, size);
    }

    [Fact]
    public void IsPlatformAsset_RejectsZip()
    {
        Assert.False(GitHubReleaseService.IsPlatformAsset("ConfigurationManagement-0.3.11.0-win-x64.zip"));
        Assert.False(GitHubReleaseService.IsPlatformAsset("something-win-x64.ZIP"));
    }

#if !LINUX
    [Fact]
    public void IsPlatformAsset_AcceptsExeAndWinX64()
    {
        Assert.True(GitHubReleaseService.IsPlatformAsset("ConfigurationManagement.exe"));
        Assert.True(GitHubReleaseService.IsPlatformAsset("ConfigurationManagement-0.3.11.0-win-x64"));
    }
#else
    [Fact]
    public void IsPlatformAsset_AcceptsLinuxNames()
    {
        Assert.True(GitHubReleaseService.IsPlatformAsset("ConfigurationManagement"));
        Assert.True(GitHubReleaseService.IsPlatformAsset("ConfigurationManagement-0.3.11.0-linux-x64"));
    }
#endif
}
