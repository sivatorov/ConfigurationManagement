using System;
using System.Collections.Generic;
using System.Linq;
using Configuration_Management.Models;
using Configuration_Management.Services;
using Xunit;

namespace ConfigurationManagement.Tests;

/// <summary>
/// Тесты чистого отбора кандидатов на удаление старых версий платформы 1С
/// (этап 0.3.9.215, <see cref="OldVersionCleaner"/>): новейшая версия исключается,
/// версии, на которые ссылаются базы репозитория (точное и префиксное совпадение),
/// исключаются; версии запущенных процессов исключаются по пути bin; пустой вход
/// даёт пустой список; результат отсортирован по убыванию.
/// </summary>
public sealed class OldVersionCleanerTests
{
    private static PlatformVersionInfo Installed(string display, string? path = null)
        => new() { Display = display, Path = path ?? string.Empty };

    private static Infobase Base(string id, string platformVersion)
        => new() { Id = id, Name = $"База {id}", PlatformVersion = platformVersion };

    // ---------- Новейшая исключена ----------

    [Fact]
    public void SelectCandidates_ExcludesNewestVersion()
    {
        var installed = new[]
        {
            Installed("8.3.27.2214"),
            Installed("8.3.27.1688"),
            Installed("8.3.26.1890"),
        };

        var result = OldVersionCleaner.SelectCandidates(installed, Array.Empty<Infobase>(), Array.Empty<string>());

        Assert.Equal(new[] { "8.3.27.1688", "8.3.26.1890" }, result.Select(v => v.Display).ToArray());
    }

    [Fact]
    public void SelectCandidates_ExcludesAllArchitecturesOfNewestVersion()
    {
        // 64- и 32-битные установки одного номера — обе новейшие (численное сравнение равно 0).
        var installed = new[]
        {
            Installed("8.3.27.2214 (64)"),
            Installed("8.3.27.2214 (32)"),
            Installed("8.3.27.1688 (64)"),
        };

        var result = OldVersionCleaner.SelectCandidates(installed, Array.Empty<Infobase>(), Array.Empty<string>());

        Assert.Single(result);
        Assert.Equal("8.3.27.1688 (64)", result[0].Display);
    }

    // ---------- Используемые базами исключены ----------

    [Fact]
    public void SelectCandidates_ExcludesVersionReferencedByBase_ExactMatch()
    {
        var installed = new[]
        {
            Installed("8.3.27.2214"),
            Installed("8.3.27.1688"),
            Installed("8.3.26.1890"),
        };
        var bases = new List<Infobase> { Base("1", "8.3.27.1688") };

        var result = OldVersionCleaner.SelectCandidates(installed, bases, Array.Empty<string>());

        Assert.Equal(new[] { "8.3.26.1890" }, result.Select(v => v.Display).ToArray());
    }

    [Fact]
    public void SelectCandidates_ExcludesVersionReferencedByBase_WithArchitectureSuffix()
    {
        // База ссылается на «8.3.27.1688 (64)» — та же версия численно.
        var installed = new[] { Installed("8.3.27.2214"), Installed("8.3.27.1688") };
        var bases = new List<Infobase> { Base("1", "8.3.27.1688 (64)") };

        var result = OldVersionCleaner.SelectCandidates(installed, bases, Array.Empty<string>());

        Assert.Empty(result);
    }

    [Fact]
    public void SelectCandidates_ExcludesWholeFamily_WhenBaseUsesPartialPrefix()
    {
        // База с частичной версией «8.3.26» охватывает все сборки семейства:
        // обе старые 8.3.26.* исключены, новейшая 8.3.27.2214 — по новизне.
        var installed = new[]
        {
            Installed("8.3.27.2214"),
            Installed("8.3.26.1890"),
            Installed("8.3.26.1644"),
        };
        var bases = new List<Infobase> { Base("1", "8.3.26") };

        var result = OldVersionCleaner.SelectCandidates(installed, bases, Array.Empty<string>());

        Assert.Empty(result);
    }

    [Fact]
    public void SelectCandidates_EmptyBaseVersion_DoesNotExcludeAnything()
    {
        var installed = new[] { Installed("8.3.27.2214"), Installed("8.3.27.1688") };
        var bases = new List<Infobase> { Base("1", "") };

        var result = OldVersionCleaner.SelectCandidates(installed, bases, Array.Empty<string>());

        Assert.Single(result);
        Assert.Equal("8.3.27.1688", result[0].Display);
    }

    // ---------- Запущенный процесс исключён ----------

    [Fact]
    public void SelectCandidates_ExcludesVersionRunningFromItsBinDirectory()
    {
        const string runningDir = @"C:\Program Files\1cv8\8.3.27.1688";
        var installed = new[]
        {
            Installed("8.3.27.2214", @"C:\Program Files\1cv8\8.3.27.2214"),
            Installed("8.3.27.1688", runningDir),
            Installed("8.3.26.1890", @"C:\Program Files\1cv8\8.3.26.1890"),
        };
        var runningBinPaths = new List<string>
        {
            System.IO.Path.Combine(runningDir, "bin", "1cv8c.exe"),
        };

        var result = OldVersionCleaner.SelectCandidates(installed, Array.Empty<Infobase>(), runningBinPaths);

        // 8.3.27.1688 запущена — исключена; осталась только 8.3.26.1890.
        Assert.Equal(new[] { "8.3.26.1890" }, result.Select(v => v.Display).ToArray());
    }

    [Fact]
    public void SelectCandidates_PathWithSimilarPrefix_IsNotTreatedAsRunning()
    {
        // «8.3.27.16882» — другой каталог, его процесс не должен исключать «8.3.27.1688».
        var installed = new[]
        {
            Installed("8.3.27.2214", @"C:\Program Files\1cv8\8.3.27.2214"),
            Installed("8.3.27.1688", @"C:\Program Files\1cv8\8.3.27.1688"),
        };
        var runningBinPaths = new List<string>
        {
            @"C:\Program Files\1cv8\8.3.27.16882\bin\1cv8c.exe",
        };

        var result = OldVersionCleaner.SelectCandidates(installed, Array.Empty<Infobase>(), runningBinPaths);

        Assert.Single(result);
        Assert.Equal("8.3.27.1688", result[0].Display);
    }

    // ---------- Пустой вход / сортировка ----------

    [Fact]
    public void SelectCandidates_EmptyInstalled_ReturnsEmpty()
    {
        var result = OldVersionCleaner.SelectCandidates(
            Array.Empty<PlatformVersionInfo>(), Array.Empty<Infobase>(), Array.Empty<string>());

        Assert.Empty(result);

        Assert.Empty(OldVersionCleaner.SelectCandidates(
            null!, new List<Infobase> { Base("1", "8.3.27.1688") }, Array.Empty<string>()));
    }

    [Fact]
    public void SelectCandidates_ResultIsSortedByVersionDescending()
    {
        var installed = new[]
        {
            Installed("8.3.27.2214"),
            Installed("8.3.10.2012"),
            Installed("8.3.9.2577"),
            Installed("8.3.27.1688"),
        };

        var result = OldVersionCleaner.SelectCandidates(installed, Array.Empty<Infobase>(), Array.Empty<string>());

        Assert.Equal(new[] { "8.3.27.1688", "8.3.10.2012", "8.3.9.2577" }, result.Select(v => v.Display).ToArray());
    }

    // ---------- Вспомогательные хелперы ----------

    [Fact]
    public void CleanVersion_StripsArchitectureSuffix()
    {
        Assert.Equal("8.3.27.2214", OldVersionCleaner.CleanVersion("8.3.27.2214 (64)"));
        Assert.Equal("8.3.27.2214", OldVersionCleaner.CleanVersion("8.3.27.2214 (32)"));
        Assert.Equal("8.3.27.2214", OldVersionCleaner.CleanVersion("8.3.27.2214"));
        Assert.Equal("", OldVersionCleaner.CleanVersion(null));
    }

    [Fact]
    public void ExtractExecutablePath_TakesFirstTokenRespectingQuotes()
    {
        Assert.Equal(
            @"C:\Program Files\1cv8\8.3.27.1688\bin\1cv8c.exe",
            OldVersionCleaner.ExtractExecutablePath(
                "\"C:\\Program Files\\1cv8\\8.3.27.1688\\bin\\1cv8c.exe\" /F \"C:\\base\""));

        Assert.Equal(
            "/opt/1cv8/8.3.27.1688/bin/1cv8c",
            OldVersionCleaner.ExtractExecutablePath("/opt/1cv8/8.3.27.1688/bin/1cv8c /F /home/user/base"));

        Assert.Null(OldVersionCleaner.ExtractExecutablePath(null));
        Assert.Null(OldVersionCleaner.ExtractExecutablePath("   "));
        Assert.Null(OldVersionCleaner.ExtractExecutablePath("\"не закрытая кавычка"));
    }

    // ---------- issue #334: полный список версий с признаками риска ----------

    [Fact]
    public void SelectDeletionEntries_IncludesAllVersions_WithRiskFlags()
    {
        // Требование автора issue: пользователь САМ решает, что считать старым, —
        // в списке остаются ВСЕ версии, включая новейшую и используемые, только
        // с выставленными признаками.
        var installed = new[]
        {
            Installed("8.3.27.2214", @"C:\1cv8\8.3.27.2214"),
            Installed("8.3.27.1688", @"C:\1cv8\8.3.27.1688"),
            Installed("8.3.26.1890", @"C:\1cv8\8.3.26.1890"),
        };
        var bases = new List<Infobase>
        {
            Base("1", "8.3.27.1688"),
            Base("2", "8.3.27.1688"),
        };
        var runningBinPaths = new List<string> { @"C:\1cv8\8.3.26.1890\bin\1cv8c.exe" };

        var entries = OldVersionCleaner.SelectDeletionEntries(installed, bases, runningBinPaths);

        Assert.Equal(3, entries.Count);

        var newest = Assert.Single(entries, e => e.IsNewest);
        Assert.Equal("8.3.27.2214", newest.Version.Display);
        Assert.False(newest.IsUsedByBases);
        Assert.False(newest.IsUsedByProcesses);

        var usedByBases = Assert.Single(entries, e => e.IsUsedByBases);
        Assert.Equal("8.3.27.1688", usedByBases.Version.Display);
        Assert.Equal(new[] { "База 1", "База 2" }, usedByBases.ReferencingBaseNames.ToArray());
        Assert.False(usedByBases.IsNewest);

        var usedByProcesses = Assert.Single(entries, e => e.IsUsedByProcesses);
        Assert.Equal("8.3.26.1890", usedByProcesses.Version.Display);
        Assert.Equal(
            new[] { @"C:\1cv8\8.3.26.1890\bin\1cv8c.exe" },
            usedByProcesses.RunningProcessPaths.ToArray());
    }

    [Fact]
    public void SelectDeletionEntries_DefaultChecked_OnlyForNonRiskVersions()
    {
        // Защита от глупостей: у новейшей/используемых версий флажок по умолчанию снят,
        // у «просто старых» — установлен.
        var installed = new[]
        {
            Installed("8.3.27.2214"),
            Installed("8.3.27.1688"),
            Installed("8.3.26.1890"),
        };
        var bases = new List<Infobase> { Base("1", "8.3.27.1688") };

        var entries = OldVersionCleaner.SelectDeletionEntries(installed, bases, Array.Empty<string>());

        Assert.False(entries.Single(e => e.Version.Display == "8.3.27.2214").IsCheckedByDefault);
        Assert.False(entries.Single(e => e.Version.Display == "8.3.27.1688").IsCheckedByDefault);
        Assert.True(entries.Single(e => e.Version.Display == "8.3.26.1890").IsCheckedByDefault);
    }

    [Fact]
    public void SelectDeletionEntries_BothArchitecturesOfNewest_AreMarkedNewest()
    {
        var installed = new[]
        {
            Installed("8.3.27.2214 (64)"),
            Installed("8.3.27.2214 (32)"),
            Installed("8.3.27.1688 (64)"),
        };

        var entries = OldVersionCleaner.SelectDeletionEntries(
            installed, Array.Empty<Infobase>(), Array.Empty<string>());

        Assert.True(entries.Single(e => e.Version.Display == "8.3.27.2214 (64)").IsNewest);
        Assert.True(entries.Single(e => e.Version.Display == "8.3.27.2214 (32)").IsNewest);
        Assert.False(entries.Single(e => e.Version.Display == "8.3.27.1688 (64)").IsNewest);
    }

    [Fact]
    public void SelectDeletionEntries_EmptyInstalled_ReturnsEmpty()
    {
        Assert.Empty(OldVersionCleaner.SelectDeletionEntries(
            Array.Empty<PlatformVersionInfo>(), Array.Empty<Infobase>(), Array.Empty<string>()));
    }

    [Fact]
    public void SelectDeletionEntries_ResultIsSortedByVersionDescending()
    {
        var installed = new[]
        {
            Installed("8.3.10.2012"),
            Installed("8.3.27.2214"),
            Installed("8.3.9.2577"),
        };

        var entries = OldVersionCleaner.SelectDeletionEntries(
            installed, Array.Empty<Infobase>(), Array.Empty<string>());

        Assert.Equal(
            new[] { "8.3.27.2214", "8.3.10.2012", "8.3.9.2577" },
            entries.Select(e => e.Version.Display).ToArray());
    }

    [Fact]
    public void BuildDeletionConfirmations_NewestSelected_ReturnsNewestWarning()
    {
        var installed = new[]
        {
            Installed("8.3.27.2214"),
            Installed("8.3.27.1688"),
        };
        var entries = OldVersionCleaner.SelectDeletionEntries(
            installed, Array.Empty<Infobase>(), Array.Empty<string>());
        var selected = new[] { installed[0] };

        var warnings = OldVersionCleaner.BuildDeletionConfirmations(entries, selected);

        var warning = Assert.Single(warnings);
        Assert.Equal(OldVersionCleanupRiskKind.Newest, warning.Kind);
        Assert.Equal("8.3.27.2214", warning.VersionDisplay);
        Assert.Empty(warning.Details);
    }

    [Fact]
    public void BuildDeletionConfirmations_BaseReferencedSelected_ReturnsWarningWithBaseNames()
    {
        var installed = new[] { Installed("8.3.27.2214"), Installed("8.3.27.1688") };
        var bases = new List<Infobase> { Base("1", "8.3.27.1688"), Base("2", "8.3.27.1688 (64)") };
        var entries = OldVersionCleaner.SelectDeletionEntries(installed, bases, Array.Empty<string>());
        var selected = new[] { installed[1] };

        var warnings = OldVersionCleaner.BuildDeletionConfirmations(entries, selected);

        var warning = Assert.Single(warnings);
        Assert.Equal(OldVersionCleanupRiskKind.UsedByBases, warning.Kind);
        Assert.Equal("8.3.27.1688", warning.VersionDisplay);
        Assert.Equal(new[] { "База 1", "База 2" }, warning.Details.ToArray());
    }

    [Fact]
    public void BuildDeletionConfirmations_ProcessRunningSelected_ReturnsWarningWithProcessPath()
    {
        var installed = new[]
        {
            Installed("8.3.27.2214", @"C:\1cv8\8.3.27.2214"),
            Installed("8.3.27.1688", @"C:\1cv8\8.3.27.1688"),
        };
        var runningBinPaths = new List<string> { @"C:\1cv8\8.3.27.1688\bin\1cv8c.exe" };
        var entries = OldVersionCleaner.SelectDeletionEntries(
            installed, Array.Empty<Infobase>(), runningBinPaths);
        var selected = new[] { installed[1] };

        var warnings = OldVersionCleaner.BuildDeletionConfirmations(entries, selected);

        var warning = Assert.Single(warnings);
        Assert.Equal(OldVersionCleanupRiskKind.UsedByProcesses, warning.Kind);
        Assert.Equal(
            new[] { @"C:\1cv8\8.3.27.1688\bin\1cv8c.exe" },
            warning.Details.ToArray());
    }

    [Fact]
    public void BuildDeletionConfirmations_NonRiskOrUnselectedVersions_NoWarnings()
    {
        var installed = new[]
        {
            Installed("8.3.27.2214"),
            Installed("8.3.27.1688"),
            Installed("8.3.26.1890"),
        };
        var entries = OldVersionCleaner.SelectDeletionEntries(
            installed, Array.Empty<Infobase>(), Array.Empty<string>());

        // Отмечена только «просто старая» версия — предупреждений нет.
        Assert.Empty(OldVersionCleaner.BuildDeletionConfirmations(
            entries, new[] { installed[2] }));
        // Пустой выбор — предупреждений нет.
        Assert.Empty(OldVersionCleaner.BuildDeletionConfirmations(
            entries, Array.Empty<PlatformVersionInfo>()));
    }
}