using System;
using System.IO;
using Configuration_Management.Services;
using Xunit;

namespace ConfigurationManagement.Tests;

/// <summary>
/// Тесты быстрого поиска rac (<see cref="OneCPlatformLocator.FindRacIn1cv8Roots"/>,
/// issue #324, 0.3.9.330): «поиск rac занял 4717 мс» — общий поиск рекурсивно сканирует
/// корни, быстрый путь проверяет только стандартный макет <корень>\1cv8\<версия>\bin.
/// </summary>
public sealed class OneCPlatformLocatorTests
{
    [Theory]
    [InlineData("8.5.4.1878", true)]
    [InlineData("8.3.27", true)]
    [InlineData("8.3", false)]
    [InlineData("8.3.27.1688x64", false)]
    [InlineData("templates", false)]
    [InlineData("", false)]
    public void IsVersionDirectoryName_DetectsVersionFolders(string? name, bool expected)
        => Assert.Equal(expected, OneCPlatformLocator.IsVersionDirectoryName(name));

    [Fact]
    public void FindRacIn1cv8Roots_PicksNewestVersion()
    {
        using var temp = new TempDirectory();
        var older = Path.Combine(temp.Path, "1cv8", "8.3.20.1000", "bin");
        var newest = Path.Combine(temp.Path, "1cv8", "8.5.4.1878", "bin");
        Directory.CreateDirectory(older);
        Directory.CreateDirectory(newest);
        WriteRacBinary(older);
        WriteRacBinary(newest);

        var rac = OneCPlatformLocator.FindRacIn1cv8Roots(new[] { temp.Path });

        Assert.NotNull(rac);
        // Путь новейшей версии, имя файла зависит от платформы (rac.exe / rac).
        Assert.Equal(newest, Path.GetDirectoryName(rac));
    }

    [Fact]
    public void FindRacIn1cv8Roots_SkipsVersionWithoutRac()
    {
        // В новейшей версии rac нет — поиск не должен вернуть её bin-каталог.
        using var temp = new TempDirectory();
        var newest = Path.Combine(temp.Path, "1cv8", "8.5.4.1878", "bin");
        var older = Path.Combine(temp.Path, "1cv8", "8.3.20.1000", "bin");
        Directory.CreateDirectory(newest);
        Directory.CreateDirectory(older);
        WriteRacBinary(older);

        var rac = OneCPlatformLocator.FindRacIn1cv8Roots(new[] { temp.Path });

        Assert.NotNull(rac);
        Assert.Equal(older, Path.GetDirectoryName(rac));
    }

    [Fact]
    public void FindRacIn1cv8Roots_NoVersions_ReturnsNull()
    {
        using var temp = new TempDirectory();
        // Каталог 1cv8 есть, но без каталогов версий (или его нет вовсе).
        Directory.CreateDirectory(Path.Combine(temp.Path, "1cv8"));

        Assert.Null(OneCPlatformLocator.FindRacIn1cv8Roots(new[] { temp.Path }));
        Assert.Null(OneCPlatformLocator.FindRacIn1cv8Roots(new[] { Path.Combine(temp.Path, "missing") }));
    }

    [Fact]
    public void FindRacIn1cv8Roots_IgnoresNonVersionDirectories()
    {
        // Каталоги «templates»/«distrib» не версии — rac в них не ищем (issue #324:
        // перебор посторонних каталогов замедлял поиск).
        using var temp = new TempDirectory();
        var templates = Path.Combine(temp.Path, "1cv8", "templates", "bin");
        Directory.CreateDirectory(templates);
        WriteRacBinary(templates);

        Assert.Null(OneCPlatformLocator.FindRacIn1cv8Roots(new[] { temp.Path }));
    }

    /// <summary>Создаёт фиктивный rac в каталоге bin (имя зависит от платформы).</summary>
    private static void WriteRacBinary(string binDir)
    {
        Directory.CreateDirectory(binDir);
        File.WriteAllText(Path.Combine(binDir, "rac"), string.Empty);
        File.WriteAllText(Path.Combine(binDir, "rac.exe"), string.Empty);
    }

    /// <summary>Временный каталог, удаляемый при освобождении.</summary>
    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; }

        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "cm-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Тестовые каталоги не критичны — оставляем как есть при сбое удаления.
            }
        }
    }
}
