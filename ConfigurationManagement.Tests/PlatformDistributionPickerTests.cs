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

    // ---------- issue #334: обновление-сборка дистрибутива (*_updsetup*.zip) ----------

    [Fact]
    public void IsUpdateSetupPackage_DetectsUpdsetupInFileName()
    {
        Assert.True(PlatformDistributionPicker.IsUpdateSetupPackage(
            File("8_3_27_2214_updsetup.zip", "x64", PlatformDistributionKind.WindowsSetupZip)));
        Assert.True(PlatformDistributionPicker.IsUpdateSetupPackage(
            File("8.3.27.2214_update-setup.zip", "x64", PlatformDistributionKind.WindowsSetupZip)));
        Assert.False(PlatformDistributionPicker.IsUpdateSetupPackage(
            File("8.3.27.2214_x64.zip", "x64", PlatformDistributionKind.WindowsSetupZip)));
    }

    [Fact]
    public void PickFile_WindowsAuto_PrefersSetupOverUpdsetup()
    {
        // issue #334: updsetup-архив не содержит setup.exe — в автовыборе отдаётся
        // предпочтение обычному дистрибутиву.
        var files = new List<PlatformReleaseFile>
        {
            File("8_3_27_2214_updsetup.zip", "x64", PlatformDistributionKind.WindowsSetupZip),
            File("8.3.27.2214_x64.zip", "x64", PlatformDistributionKind.WindowsSetupZip),
        };

        Assert.Equal("8.3.27.2214_x64.zip",
            PlatformDistributionPicker.PickFile(files, true, PlatformDownloadType.Auto, true)!.FileName);
    }

    [Fact]
    public void PickFile_WindowsAuto_UpdsetupOnly_StillPicked()
    {
        // Если updsetup — единственный zip, он используется (не блокируем пользователя).
        var files = new List<PlatformReleaseFile>
        {
            File("8_3_27_2214_updsetup.zip", "x64", PlatformDistributionKind.WindowsSetupZip),
        };

        Assert.Equal("8_3_27_2214_updsetup.zip",
            PlatformDistributionPicker.PickFile(files, true, PlatformDownloadType.Auto, true)!.FileName);
    }

    [Fact]
    public void PickFile_WindowsClient_ExcludesUpdsetup()
    {
        var files = new List<PlatformReleaseFile>
        {
            File("8_3_27_2214_updsetup.zip", "x64", PlatformDistributionKind.WindowsSetupZip),
            File("8.3.27.2214_x64.zip", "x64", PlatformDistributionKind.WindowsSetupZip),
        };

        Assert.Equal("8.3.27.2214_x64.zip",
            PlatformDistributionPicker.PickFile(files, true, PlatformDownloadType.Client, true)!.FileName);
    }

    [Fact]
    public void BuildOptions_Windows_UpdsetupSortedLast_NotRecommended()
    {
        var files = new List<PlatformReleaseFile>
        {
            File("8_3_27_2214_updsetup.zip", "x64", PlatformDistributionKind.WindowsSetupZip),
            File("8.3.27.2214_x64.zip", "x64", PlatformDistributionKind.WindowsSetupZip),
        };

        var options = PlatformDistributionPicker.BuildOptions(files, isWindows: true, is64Bit: true);

        Assert.Equal(2, options.Count);
        Assert.Equal("8.3.27.2214_x64.zip", options[0].File.FileName);
        Assert.True(options[0].IsRecommended);
        Assert.Equal("8_3_27_2214_updsetup.zip", options[1].File.FileName);
        Assert.False(options[1].IsRecommended);
    }

    [Fact]
    public void DisplayName_RarWindowsArchive_ShowsActualExtension()
    {
        // issue #330: дистрибутивы платформы отдаются и архивами .rar — подпись
        // варианта не должна вводить в заблуждение «(zip)».
        var option = new PlatformDistributionOption(
            File("setup_8_5_1_1522.rar", "x64", PlatformDistributionKind.WindowsSetupZip));

        Assert.StartsWith("Полный клиент (rar)", option.DisplayName);
        Assert.Contains("x64", option.DisplayName);
    }

    [Fact]
    public void DisplayName_ThinClientRarArchive_ShowsThinClientWithExtension()
    {
        var option = new PlatformDistributionOption(
            File("setup_8_5_1_1522_thin_64.7z", "x64", PlatformDistributionKind.WindowsSetupZip));

        Assert.StartsWith("Тонкий клиент (7z)", option.DisplayName);
    }

    // ---------- issue #330 (0.3.11): уникальные подписи — имя файла в DisplayName ----------

    [Fact]
    public void DisplayName_WindowsVariant_ContainsFileName()
    {
        // У файлов релиза размер неизвестен (0), подписи типа/разрядности одинаковые —
        // без имени файла строки списка выбора неотличимы (issue #330).
        var option = new PlatformDistributionOption(
            File("setuptc64_8_3_27_2325.rar", "x64", PlatformDistributionKind.WindowsSetupZip));

        Assert.Contains("setuptc64_8_3_27_2325.rar", option.DisplayName);
        Assert.StartsWith("Полный клиент (rar) · x64 · setuptc64_8_3_27_2325.rar", option.DisplayName);
    }

    [Fact]
    public void DisplayName_TwoFullClientsOfDifferentVersions_AreDistinct()
    {
        var first = new PlatformDistributionOption(
            File("setup64_8_3_26_2014.rar", "x64", PlatformDistributionKind.WindowsSetupZip));
        var second = new PlatformDistributionOption(
            File("setup64_8_3_27_2325.rar", "x64", PlatformDistributionKind.WindowsSetupZip));

        Assert.NotEqual(first.DisplayName, second.DisplayName);
        Assert.Contains("setup64_8_3_26_2014.rar", first.DisplayName);
        Assert.Contains("setup64_8_3_27_2325.rar", second.DisplayName);
    }

    [Fact]
    public void DisplayName_ThinFullUpdsetup_AreDistinctAndContainFileNames()
    {
        var full = new PlatformDistributionOption(
            File("setup64_8_3_27_2325.rar", "x64", PlatformDistributionKind.WindowsSetupZip));
        var thin = new PlatformDistributionOption(
            File("setuptc64_8_3_27_2325.rar", "x64", PlatformDistributionKind.WindowsSetupZip));
        var updsetup = new PlatformDistributionOption(
            File("8_3_27_2325_updsetup.rar", "x64", PlatformDistributionKind.WindowsSetupZip));

        Assert.Contains("setup64_8_3_27_2325.rar", full.DisplayName);
        Assert.Contains("setuptc64_8_3_27_2325.rar", thin.DisplayName);
        Assert.Contains("8_3_27_2325_updsetup.rar", updsetup.DisplayName);
        Assert.NotEqual(full.DisplayName, thin.DisplayName);
        Assert.NotEqual(full.DisplayName, updsetup.DisplayName);
    }

    [Fact]
    public void DisplayName_SingleLinuxPackage_MayOmitFileName()
    {
        // Единственный Linux-пакет допустимо показывать без имени файла.
        var option = new PlatformDistributionOption(
            File("deb64_8.3.27.2325.tar.gz", "x64", PlatformDistributionKind.LinuxDeb));

        Assert.DoesNotContain("deb64_8.3.27.2325.tar.gz", option.DisplayName);
        Assert.StartsWith("Пакет deb", option.DisplayName);
    }

    [Fact]
    public void DisplayName_TwoLinuxPackagesOfSameKind_IncludeFileNames()
    {
        // Как BuildOptions: при нескольких файлах одного типа имя файла включается в подпись.
        var deb1 = new PlatformDistributionOption(
            File("deb64_8.3.27.2325.tar.gz", "x64", PlatformDistributionKind.LinuxDeb), includeFileName: true);
        var deb2 = new PlatformDistributionOption(
            File("deb_8.3.27.2325.tar.gz", "x86", PlatformDistributionKind.LinuxDeb), includeFileName: true);

        // Оба варианта одного типа — подписи обязаны содержать имя файла (issue #330).
        Assert.Contains("deb64_8.3.27.2325.tar.gz", deb1.DisplayName);
        Assert.Contains("deb_8.3.27.2325.tar.gz", deb2.DisplayName);
        Assert.NotEqual(deb1.DisplayName, deb2.DisplayName);
    }

    [Fact]
    public void BuildOptions_LinuxMultiplePackagesOfSameKind_DisplayNamesContainFileNames()
    {
        var files = new List<PlatformReleaseFile>
        {
            File("deb64_8.3.27.2325.tar.gz", "x64", PlatformDistributionKind.LinuxDeb),
            File("deb_8.3.27.2325.tar.gz", "x86", PlatformDistributionKind.LinuxDeb),
        };

        var options = PlatformDistributionPicker.BuildOptions(files, isWindows: false, is64Bit: true);

        Assert.All(options, o => Assert.Contains(o.File.FileName, o.DisplayName));
        var names = options.Select(o => o.DisplayName).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Fact]
    public void BuildOptions_Windows_DisplayNamesAreUniqueAndContainFileNames()
    {
        var options = PlatformDistributionPicker.BuildOptions(WindowsFiles(), isWindows: true, is64Bit: true);

        Assert.Equal(3, options.Count);
        var names = options.Select(o => o.DisplayName).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.All(options, o => Assert.Contains(o.File.FileName, o.DisplayName));
    }

    [Fact]
    public void BuildOptions_SortingUnchanged_Regression()
    {
        // Регресс: добавление имени файла в подпись не меняет сортировку вариантов.
        var options = PlatformDistributionPicker.BuildOptions(WindowsFiles(), isWindows: true, is64Bit: true);

        Assert.Equal(
            new[] { "8.3.27.2214_x64.zip", "8.3.27.2214_x86.zip", "8.3.27.2214_thin_1c_x64.zip" },
            options.Select(o => o.File.FileName).ToArray());

        var linux = PlatformDistributionPicker.BuildOptions(LinuxFiles(), isWindows: false, is64Bit: true);
        Assert.Equal(
            new[] { "deb64_8.3.27.2214.tar.gz", "8.3.27.2214_x86_64.rpm", "8.3.27.2214.tar.gz" },
            linux.Select(o => o.File.FileName).ToArray());
    }
}