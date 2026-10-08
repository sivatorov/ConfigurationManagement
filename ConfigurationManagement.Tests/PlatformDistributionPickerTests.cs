using System.Collections.Generic;
using Configuration_Management.Models;
using Configuration_Management.Services;
using Xunit;

namespace ConfigurationManagement.Tests;

/// <summary>
/// Тесты чистого выбора файла дистрибутива платформы 1С (issue #330):
/// маппинг «версия+разрядность+тип → файл» для Windows и Linux, список доступных
/// типов, распознавание тонкого клиента. Без сети и UI.
/// </summary>
public sealed class PlatformDistributionPickerTests
{
    private static PlatformReleaseFile File(string name, string? arch, PlatformDistributionKind kind)
        => new()
        {
            FileName = name,
            Url = $"https://releases.1c.ru/dist/{name}",
            Architecture = arch,
            Kind = kind,
            SizeBytes = 100,
        };

    private static List<PlatformReleaseFile> WindowsFiles() => new()
    {
        File("8.3.27.2214_x86.zip", "x86", PlatformDistributionKind.WindowsSetupZip),
        File("8.3.27.2214_x64.zip", "x64", PlatformDistributionKind.WindowsSetupZip),
        File("8.3.27.2214_thin_1c_x64.zip", "x64", PlatformDistributionKind.WindowsSetupZip),
    };

    private static List<PlatformReleaseFile> LinuxFiles() => new()
    {
        File("deb64_8.3.27.2214.tar.gz", "x64", PlatformDistributionKind.LinuxDeb),
        File("8.3.27.2214_x86_64.rpm", "x64", PlatformDistributionKind.LinuxRpm),
        File("8.3.27.2214.tar.gz", "x64", PlatformDistributionKind.LinuxTarGz),
    };

    [Fact]
    public void PickFile_WindowsAuto64_PicksFullClientX64()
    {
        var picked = PlatformDistributionPicker.PickFile(
            WindowsFiles(), is64Bit: true, PlatformDownloadType.Auto, isWindows: true);

        Assert.NotNull(picked);
        Assert.Equal("8.3.27.2214_x64.zip", picked!.FileName);
        Assert.Equal("x64", picked.Architecture);
    }

    [Fact]
    public void PickFile_WindowsAuto32_PicksX86Zip()
    {
        var picked = PlatformDistributionPicker.PickFile(
            WindowsFiles(), is64Bit: false, PlatformDownloadType.Auto, isWindows: true);

        Assert.NotNull(picked);
        Assert.Equal("8.3.27.2214_x86.zip", picked!.FileName);
    }

    [Fact]
    public void PickFile_WindowsThinClient_PicksThinZipEvenFor64()
    {
        var picked = PlatformDistributionPicker.PickFile(
            WindowsFiles(), is64Bit: true, PlatformDownloadType.ThinClient, isWindows: true);

        Assert.NotNull(picked);
        Assert.Contains("thin", picked!.FileName, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PickFile_WindowsFullClient_ExcludesThin()
    {
        var picked = PlatformDistributionPicker.PickFile(
            WindowsFiles(), is64Bit: true, PlatformDownloadType.Client, isWindows: true);

        Assert.NotNull(picked);
        Assert.DoesNotContain("thin", picked!.FileName, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PickFile_LinuxAuto64_PrefersDebOverTarGz()
    {
        var picked = PlatformDistributionPicker.PickFile(
            LinuxFiles(), is64Bit: true, PlatformDownloadType.Auto, isWindows: false);

        Assert.NotNull(picked);
        Assert.Equal("deb64_8.3.27.2214.tar.gz", picked!.FileName);
    }

    [Fact]
    public void PickFile_LinuxPackage_PicksRpmOrDeb()
    {
        var picked = PlatformDistributionPicker.PickFile(
            LinuxFiles(), is64Bit: true, PlatformDownloadType.Package, isWindows: false);

        Assert.NotNull(picked);
        Assert.Contains(picked!.Kind, new[] { PlatformDistributionKind.LinuxDeb, PlatformDistributionKind.LinuxRpm });
    }

    [Fact]
    public void PickFile_LinuxArchive_PicksTarGz()
    {
        var picked = PlatformDistributionPicker.PickFile(
            LinuxFiles(), is64Bit: true, PlatformDownloadType.Archive, isWindows: false);

        Assert.NotNull(picked);
        Assert.Equal(PlatformDistributionKind.LinuxTarGz, picked!.Kind);
    }

    [Fact]
    public void PickFile_EmptyFiles_ReturnsNull()
    {
        Assert.Null(PlatformDistributionPicker.PickFile(
            new List<PlatformReleaseFile>(), is64Bit: true, PlatformDownloadType.Auto, isWindows: true));
    }

    [Fact]
    public void PickFile_NoMatchingType_ReturnsNull()
    {
        // На Windows нет tar.gz — выбор типа «Архив» даёт null.
        Assert.Null(PlatformDistributionPicker.PickFile(
            WindowsFiles(), is64Bit: true, PlatformDownloadType.Archive, isWindows: true));
    }

    [Fact]
    public void AvailableTypes_Windows_ContainsAutoClientThinClient()
    {
        var types = PlatformDistributionPicker.AvailableTypes(WindowsFiles(), isWindows: true);

        Assert.Contains(PlatformDownloadType.Auto, types);
        Assert.Contains(PlatformDownloadType.Client, types);
        Assert.Contains(PlatformDownloadType.ThinClient, types);
        Assert.DoesNotContain(PlatformDownloadType.Package, types);
        Assert.DoesNotContain(PlatformDownloadType.Archive, types);
    }

    [Fact]
    public void AvailableTypes_Linux_ContainsAutoPackageArchive()
    {
        var types = PlatformDistributionPicker.AvailableTypes(LinuxFiles(), isWindows: false);

        Assert.Contains(PlatformDownloadType.Auto, types);
        Assert.Contains(PlatformDownloadType.Package, types);
        Assert.Contains(PlatformDownloadType.Archive, types);
        Assert.DoesNotContain(PlatformDownloadType.Client, types);
    }

    [Fact]
    public void IsThinClient_DetectsThinTokenInFileName()
    {
        Assert.True(PlatformDistributionPicker.IsThinClient(
            File("8.3.27.2214_thin_1c_x64.zip", "x64", PlatformDistributionKind.WindowsSetupZip)));
        Assert.False(PlatformDistributionPicker.IsThinClient(
            File("8.3.27.2214_x64.zip", "x64", PlatformDistributionKind.WindowsSetupZip)));
    }

    [Fact]
    public void TypeLocalizationKey_ReturnsKeysForAllTypes()
    {
        Assert.Equal("PlatformDownload.Type.Auto",
            PlatformDistributionPicker.TypeLocalizationKey(PlatformDownloadType.Auto));
        Assert.Equal("PlatformDownload.Type.ThinClient",
            PlatformDistributionPicker.TypeLocalizationKey(PlatformDownloadType.ThinClient));
    }

    // ---------- issue #330/#334: варианты дистрибутива для выбора пользователем ----------

    [Fact]
    public void BuildOptions_Windows_FullBeforeThin_X64First()
    {
        var options = PlatformDistributionPicker.BuildOptions(WindowsFiles(), isWindows: true, is64Bit: true);

        Assert.Equal(3, options.Count);
        // Порядок: полный клиент x64 (рекомендуемый) → полный x86 → тонкий x64.
        Assert.Equal("8.3.27.2214_x64.zip", options[0].File.FileName);
        Assert.True(options[0].IsRecommended);
        Assert.Equal("8.3.27.2214_x86.zip", options[1].File.FileName);
        Assert.False(options[1].IsRecommended);
        Assert.Equal("8.3.27.2214_thin_1c_x64.zip", options[2].File.FileName);
        Assert.False(options[2].IsRecommended);
        Assert.Contains("Полный клиент", options[0].DisplayName);
        Assert.Contains("x64", options[0].DisplayName);
    }

    [Fact]
    public void BuildOptions_Linux_DebRpmArchiveOrder()
    {
        var options = PlatformDistributionPicker.BuildOptions(LinuxFiles(), isWindows: false, is64Bit: true);

        Assert.Equal(3, options.Count);
        Assert.Equal("deb64_8.3.27.2214.tar.gz", options[0].File.FileName); // пакет deb
        Assert.True(options[0].IsRecommended);
        Assert.Equal("8.3.27.2214_x86_64.rpm", options[1].File.FileName);   // пакет rpm
        Assert.Equal("8.3.27.2214.tar.gz", options[2].File.FileName);       // архив tar.gz
    }

    [Fact]
    public void BuildOptions_EmptyOrForeignFiles_ReturnsEmpty()
    {
        Assert.Empty(PlatformDistributionPicker.BuildOptions(
            new List<PlatformReleaseFile>(), isWindows: true, is64Bit: true));
        // Только Linux-файлы — для Windows вариантов нет (issue #330: «только АВТО»).
        Assert.Empty(PlatformDistributionPicker.BuildOptions(
            LinuxFiles(), isWindows: true, is64Bit: true));
    }

    [Fact]
    public void BuildOptions_32Bit_RecommendsX86()
    {
        var options = PlatformDistributionPicker.BuildOptions(WindowsFiles(), isWindows: true, is64Bit: false);

        var recommended = options.FirstOrDefault(o => o.IsRecommended);
        Assert.NotNull(recommended);
        Assert.Equal("8.3.27.2214_x86.zip", recommended!.File.FileName);
    }
}