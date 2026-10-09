#if !LINUX
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Configuration_Management.Models;
using Configuration_Management.Services;
using Xunit;

namespace ConfigurationManagement.Tests;

/// <summary>
/// Тесты установщика платформы 1С на Windows (<see cref="PlatformInstaller"/>):
/// чистые функции (аргументы тихой установки, поиск setup.exe, определение
/// установленной версии, проверка подписи) и сценарий запуска/распаковки на
/// fake-запускателях и fake-распаковке. Файл обёрнут в #if !LINUX: класс
/// PlatformInstaller собирается только на Windows (#if WINDOWS), поэтому при
/// кросс-сборке -p:BuildLinux=true эти проверки исключаются.
/// </summary>
public sealed class PlatformInstallerWindowsTests : IDisposable
{
    private readonly string _tempRoot;

    public PlatformInstallerWindowsTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "cm_inst_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch
        {
            // Очистка временного каталога — best effort.
        }
    }

    // --- BuildSilentArguments ---

    [Fact]
    public void BuildSilentArguments_NoDirectory_ReturnsDefaultArgs()
    {
        Assert.Equal(PlatformInstaller.DefaultSilentArgs, PlatformInstaller.BuildSilentArguments());
        Assert.Equal(PlatformInstaller.DefaultSilentArgs, PlatformInstaller.BuildSilentArguments(null));
        Assert.Equal(PlatformInstaller.DefaultSilentArgs, PlatformInstaller.BuildSilentArguments("   "));
    }

    [Fact]
    public void BuildSilentArguments_DirectoryWithSpaces_AddsQuotedDir()
    {
        var result = PlatformInstaller.BuildSilentArguments(@"C:\Program Files\1cv8");

        Assert.Equal("/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /DIR=\"C:\\Program Files\\1cv8\"", result);
    }

    [Fact]
    public void BuildSilentArguments_EscapesInnerQuotesAndAmpersand()
    {
        var result = PlatformInstaller.BuildSilentArguments(@"C:\1cv8\""a""&b");

        // Внутренние кавычки заменяются на апостроф, «&» экранируется как «^&».
        Assert.Contains("'a'", result);
        Assert.Contains("^&", result);
        Assert.DoesNotContain("\"a\"", result);
        Assert.StartsWith(PlatformInstaller.DefaultSilentArgs, result);
    }

    // --- FindSetupExecutable ---

    [Fact]
    public void FindSetupExecutable_FindsInRoot()
    {
        var dir = CreateDir("root");
        File.WriteAllText(Path.Combine(dir, "setup.exe"), "stub");

        Assert.Equal(Path.Combine(dir, "setup.exe"), PlatformInstaller.FindSetupExecutable(dir));
    }

    [Fact]
    public void FindSetupExecutable_FindsInNestedSubdirectory()
    {
        var root = CreateDir("nested");
        var nested = Directory.CreateDirectory(Path.Combine(root, "install", "x64")).FullName;
        File.WriteAllText(Path.Combine(nested, "setup.exe"), "stub");

        var found = PlatformInstaller.FindSetupExecutable(root);

        Assert.NotNull(found);
        Assert.Equal(nested, Path.GetDirectoryName(found));
    }

    [Fact]
    public void FindSetupExecutable_CaseInsensitive()
    {
        var dir = CreateDir("case");
        File.WriteAllText(Path.Combine(dir, "SETUP.EXE"), "stub");

        Assert.Equal(Path.Combine(dir, "SETUP.EXE"), PlatformInstaller.FindSetupExecutable(dir));
    }

    [Fact]
    public void FindSetupExecutable_TooDeep_ReturnsNull()
    {
        var root = CreateDir("deep");
        var deep = Directory.CreateDirectory(
            Path.Combine(root, "a", "b", "c", "d")).FullName;
        File.WriteAllText(Path.Combine(deep, "setup.exe"), "stub");

        // Глубина вложения «a/b/c/d» = 4 — превышает лимит 3.
        Assert.Null(PlatformInstaller.FindSetupExecutable(root));
    }

    [Fact]
    public void FindSetupExecutable_NotPresent_ReturnsNull()
    {
        var dir = CreateDir("empty");
        File.WriteAllText(Path.Combine(dir, "readme.txt"), "no setup here");

        Assert.Null(PlatformInstaller.FindSetupExecutable(dir));
    }

    [Fact]
    public void FindSetupExecutable_UpdateSetupName_FoundByFallback()
    {
        // issue #334: обновление-сборка дистрибутива кладёт установщик под именем
        // вида «update-setup.exe»/«updsetup.exe» — находится по подстроке «setup».
        var dir = CreateDir("updsetup");
        File.WriteAllText(Path.Combine(dir, "update-setup.exe"), "stub");

        var found = PlatformInstaller.FindSetupExecutable(dir);

        Assert.NotNull(found);
        Assert.Equal("update-setup.exe", Path.GetFileName(found));
    }

    [Fact]
    public void FindSetupExecutable_ExactNameHasPriorityOverSubstring()
    {
        // Точный setup.exe приоритетнее других *setup*.exe, даже если тот ближе.
        var root = CreateDir("priority");
        File.WriteAllText(Path.Combine(root, "updsetup.exe"), "stub");
        var nested = Directory.CreateDirectory(Path.Combine(root, "bin")).FullName;
        File.WriteAllText(Path.Combine(nested, "setup.exe"), "stub");

        var found = PlatformInstaller.FindSetupExecutable(root);

        Assert.Equal(Path.Combine(nested, "setup.exe"), found);
    }

    [Fact]
    public void FindSetupExecutable_NonexistentDirectory_ReturnsNull()
    {
        Assert.Null(PlatformInstaller.FindSetupExecutable(Path.Combine(_tempRoot, "missing")));
        Assert.Null(PlatformInstaller.FindSetupExecutable(""));
        Assert.Null(PlatformInstaller.FindSetupExecutable(null!));
    }

    // --- DetectNewVersionInstalled ---

    [Fact]
    public void DetectNewVersionInstalled_ExactMatch_ReturnsTrue()
    {
        Assert.True(PlatformInstaller.DetectNewVersionInstalled(
            "8.3.27.2214",
            new[] { "8.3.26.1890", "8.3.27.2214" }));
    }

    [Fact]
    public void DetectNewVersionInstalled_SuffixArchitecture_MatchedViaParseVariant()
    {
        // Display с суффиксом разрядности «(64)» приводится к чистой версии.
        Assert.True(PlatformInstaller.DetectNewVersionInstalled(
            "8.3.27.2214",
            new[] { "8.3.27.2214 (64)" }));
    }

    [Fact]
    public void DetectNewVersionInstalled_NotInstalled_ReturnsFalse()
    {
        Assert.False(PlatformInstaller.DetectNewVersionInstalled(
            "8.3.27.2214",
            new[] { "8.3.26.1890" }));

        Assert.False(PlatformInstaller.DetectNewVersionInstalled(
            "8.3.27.2214",
            Array.Empty<string>()));

        Assert.False(PlatformInstaller.DetectNewVersionInstalled("", new[] { "8.3.27.2214" }));
    }

    // --- HasValidSignature ---

    [Fact]
    public void HasValidSignature_MissingFile_ReturnsFalse()
    {
        Assert.False(PlatformInstaller.HasValidSignature(Path.Combine(_tempRoot, "missing_setup.exe")));
    }

    [Fact]
    public void HasValidSignature_UnsignedFile_ReturnsFalse()
    {
        var exe = Path.Combine(_tempRoot, "unsigned_setup.exe");
        File.WriteAllText(exe, "not a real signed executable");

        Assert.False(PlatformInstaller.HasValidSignature(exe));
    }

    // --- RunSetupCoreAsync (fake-запускатель) ---

    [Fact]
    public async Task RunSetupCoreAsync_Success_ReturnsExitCodeZero()
    {
        ProcessStartInfo? captured = null;
        var result = await PlatformInstaller.RunSetupCoreAsync(
            @"C:\dist\setup.exe",
            PlatformInstaller.DefaultSilentArgs,
            TimeSpan.FromSeconds(10),
            CancellationToken.None,
            psi =>
            {
                captured = psi;
                return new FakeInstallerProcess(exitCode: 0, hasExited: () => true);
            });

        Assert.NotNull(captured);
        Assert.Equal(@"C:\dist\setup.exe", captured.FileName);
        Assert.Equal(PlatformInstaller.DefaultSilentArgs, captured.Arguments);
        Assert.True(captured.UseShellExecute);
        Assert.Equal("runas", captured.Verb);

        Assert.True(result.Started);
        Assert.Equal(0, result.ExitCode);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task RunSetupCoreAsync_NonZeroExitCode_IsReturned()
    {
        var result = await PlatformInstaller.RunSetupCoreAsync(
            "setup.exe", "args", TimeSpan.FromSeconds(10), CancellationToken.None,
            _ => new FakeInstallerProcess(exitCode: 5, hasExited: () => true));

        Assert.True(result.Started);
        Assert.Equal(5, result.ExitCode);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task RunSetupCoreAsync_HangsUntilTimeout_KillsAndSetsTimedOut()
    {
        var process = new FakeInstallerProcess(exitCode: 0, hasExited: () => false);

        var result = await PlatformInstaller.RunSetupCoreAsync(
            "setup.exe", "args", TimeSpan.FromMilliseconds(300), CancellationToken.None,
            _ => process);

        Assert.True(result.Started);
        Assert.True(result.TimedOut);
        Assert.True(process.KillCalled);
    }

    [Fact]
    public async Task RunSetupCoreAsync_Cancellation_KillsAndThrows()
    {
        var process = new FakeInstallerProcess(exitCode: 0, hasExited: () => false);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PlatformInstaller.RunSetupCoreAsync(
                "setup.exe", "args", TimeSpan.FromSeconds(30), cts.Token, _ => process));

        Assert.True(process.KillCalled);
    }

    [Fact]
    public async Task RunSetupCoreAsync_StarterReturnsNull_StartedFalseCodeZero()
    {
        var result = await PlatformInstaller.RunSetupCoreAsync(
            "setup.exe", "args", TimeSpan.FromSeconds(10), CancellationToken.None,
            _ => null);

        Assert.False(result.Started);
        Assert.Equal(0, result.ExitCode);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task RunSetupCoreAsync_StarterThrows_StartedFalseCodeZero()
    {
        var result = await PlatformInstaller.RunSetupCoreAsync(
            "setup.exe", "args", TimeSpan.FromSeconds(10), CancellationToken.None,
            _ => throw new InvalidOperationException("UAC declined"));

        Assert.False(result.Started);
        Assert.Equal(0, result.ExitCode);
        Assert.False(result.TimedOut);
    }

    // --- InstallFromZipCoreAsync (fake-распаковка и fake-запуск) ---

    /// <summary>Создаёт временный файл с zip-подписью (PK\x03\x04): ранняя проверка
    /// magic-байтов (issue #334) должна пройти, чтобы тест дошёл до распаковки.</summary>
    private static string CreateFakeZipFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "cm_test_dist_" + Guid.NewGuid().ToString("N") + ".zip");
        File.WriteAllBytes(path, new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x00, 0x00 });
        return path;
    }

    [Fact]
    public async Task InstallFromZipCore_Success_CreatesAndRemovesTempDir()
    {
        const string version = "8.3.27.2214";
        string? createdDir = null;
        var log = new List<string>();
        var zipPath = CreateFakeZipFile();
        try
        {

        bool Extract(string zip, string dir)
        {
            createdDir = dir;
            File.WriteAllText(Path.Combine(dir, "setup.exe"), "stub");
            return true;
        }

        var result = await PlatformInstaller.InstallFromZipCoreAsync(
            zipPath, version, installDirectory: null,
            new Progress<string>(log.Add), CancellationToken.None,
            Extract,
            (exe, args, timeout, token) =>
                Task.FromResult(new PlatformInstaller.InstallerRunResult(Started: true, ExitCode: 0, TimedOut: false)),
            () => new[] { "8.3.26.1890", version });

        Assert.True(result.Success);
        Assert.Null(result.ErrorKey);
        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(createdDir);
        // Временный каталог удалён в finally, несмотря на успех.
        Assert.False(Directory.Exists(createdDir));
        Assert.NotEmpty(log);
        }
        finally
        {
            File.Delete(zipPath);
        }
    }

    [Fact]
    public async Task InstallFromZipCore_SetupNotFound_ReturnsErrorKey()
    {
        var zipPath = CreateFakeZipFile();
        try
        {
        var result = await PlatformInstaller.InstallFromZipCoreAsync(
            zipPath, "8.3.27.2214", null, null, CancellationToken.None,
            (zip, dir) => true,
            (exe, args, timeout, token) =>
                Task.FromResult(new PlatformInstaller.InstallerRunResult(Started: true, ExitCode: 0, TimedOut: false)),
            () => Array.Empty<string>());

        Assert.False(result.Success);
        Assert.Equal(PlatformInstaller.ErrorSetupNotFound, result.ErrorKey);
        }
        finally
        {
            File.Delete(zipPath);
        }
    }

    [Fact]
    public async Task InstallFromZipCore_SetupNotFound_LogsArchiveContents()
    {
        // issue #334: при отсутствии setup.exe в журнале — понятное сообщение
        // с фактическим содержимым архива.
        var log = new List<string>();
        var zipPath = CreateFakeZipFile();
        try
        {
        var result = await PlatformInstaller.InstallFromZipCoreAsync(
            zipPath, "8.3.27.2214", null, new Progress<string>(log.Add), CancellationToken.None,
            (zip, dir) =>
            {
                File.WriteAllText(Path.Combine(dir, "readme.txt"), "stub");
                File.WriteAllText(Path.Combine(dir, "1cv8.cfl"), "stub");
                return true;
            },
            (exe, args, timeout, token) =>
                Task.FromResult(new PlatformInstaller.InstallerRunResult(Started: true, ExitCode: 0, TimedOut: false)),
            () => Array.Empty<string>());

        Assert.False(result.Success);
        Assert.Equal(PlatformInstaller.ErrorSetupNotFound, result.ErrorKey);
        Assert.Contains(log, line => line.Contains("readme.txt") && line.Contains("1cv8.cfl"));
        }
        finally
        {
            File.Delete(zipPath);
        }
    }

    [Fact]
    public async Task InstallFromZipCore_NestedZipWithSetup_ExtractsAndInstalls()
    {
        // issue #334: обновление-сборка (*_updsetup*.zip) — zip внутри zip. После
        // распаковки вложенного архива установщик находится и установка проходит.
        const string version = "8.3.27.2214";
        var nestedExtractions = new List<string>();
        var runCalls = new List<string>();
        var zipPath = CreateFakeZipFile();
        try
        {
        var result = await PlatformInstaller.InstallFromZipCoreAsync(
            zipPath, version, null, null, CancellationToken.None,
            (zip, dir) =>
            {
                if (zip == zipPath)
                {
                    // Внешний архив: вложенный zip + readme (без setup.exe).
                    File.WriteAllText(Path.Combine(dir, "readme.txt"), "stub");
                    File.WriteAllBytes(
                        Path.Combine(dir, "8_3_27_2214_updsetup.zip"),
                        new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x00, 0x00 });
                    return true;
                }

                // Вложенный архив: setup.exe внутри (каталог создаёт распаковщик).
                nestedExtractions.Add(zip);
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "setup.exe"), "stub");
                return true;
            },
            (exe, args, timeout, token) =>
            {
                runCalls.Add(exe);
                return Task.FromResult(new PlatformInstaller.InstallerRunResult(Started: true, ExitCode: 0, TimedOut: false));
            },
            () => new[] { version });

        Assert.True(result.Success);
        Assert.Null(result.ErrorKey);
        Assert.Single(nestedExtractions);
        Assert.Contains("setup.exe", runCalls.Single(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(zipPath);
        }
    }

    [Fact]
    public async Task InstallFromZipCore_UpdSetupExeInArchive_FoundAndInstalled()
    {
        // issue #334: архив с установщиком «update-setup.exe» (без setup.exe) —
        // установка проходит через найденный файл.
        const string version = "8.3.27.2214";
        var runCalls = new List<string>();
        var zipPath = CreateFakeZipFile();
        try
        {
        var result = await PlatformInstaller.InstallFromZipCoreAsync(
            zipPath, version, null, null, CancellationToken.None,
            (zip, dir) =>
            {
                File.WriteAllText(Path.Combine(dir, "update-setup.exe"), "stub");
                return true;
            },
            (exe, args, timeout, token) =>
            {
                runCalls.Add(exe);
                return Task.FromResult(new PlatformInstaller.InstallerRunResult(Started: true, ExitCode: 0, TimedOut: false));
            },
            () => new[] { version });

        Assert.True(result.Success);
        Assert.Single(runCalls);
        Assert.Contains("update-setup.exe", runCalls.Single(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(zipPath);
        }
    }

    [Fact]
    public async Task InstallFromZipCore_ExtractFailed_ReturnsErrorKey()
    {
        var zipPath = CreateFakeZipFile();
        try
        {
        var result = await PlatformInstaller.InstallFromZipCoreAsync(
            zipPath, "8.3.27.2214", null, null, CancellationToken.None,
            (zip, dir) => false,
            (exe, args, timeout, token) =>
                Task.FromResult(new PlatformInstaller.InstallerRunResult(Started: true, ExitCode: 0, TimedOut: false)),
            () => Array.Empty<string>());

        Assert.False(result.Success);
        Assert.Equal(PlatformInstaller.ErrorExtractFailed, result.ErrorKey);
        }
        finally
        {
            File.Delete(zipPath);
        }
    }

    [Fact]
    public async Task InstallFromZipCore_TimedOut_ReturnsErrorKey()
    {
        string? createdDir = null;
        var zipPath = CreateFakeZipFile();
        try
        {
        bool Extract(string zip, string dir)
        {
            createdDir = dir;
            File.WriteAllText(Path.Combine(dir, "setup.exe"), "stub");
            return true;
        }

        var result = await PlatformInstaller.InstallFromZipCoreAsync(
            zipPath, "8.3.27.2214", null, null, CancellationToken.None,
            Extract,
            (exe, args, timeout, token) =>
                Task.FromResult(new PlatformInstaller.InstallerRunResult(Started: true, ExitCode: 0, TimedOut: true)),
            () => Array.Empty<string>());

        Assert.False(result.Success);
        Assert.Equal(PlatformInstaller.ErrorTimedOut, result.ErrorKey);
        Assert.False(Directory.Exists(createdDir));
        }
        finally
        {
            File.Delete(zipPath);
        }
    }

    [Fact]
    public async Task InstallFromZipCore_Cancelled_ReturnsCancelledError()
    {
        string? createdDir = null;
        bool Extract(string zip, string dir)
        {
            createdDir = dir;
            File.WriteAllText(Path.Combine(dir, "setup.exe"), "stub");
            return true;
        }

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await PlatformInstaller.InstallFromZipCoreAsync(
            "dist.zip", "8.3.27.2214", null, null, cts.Token,
            Extract,
            (exe, args, timeout, token) =>
                Task.FromResult(new PlatformInstaller.InstallerRunResult(Started: true, ExitCode: 0, TimedOut: false)),
            () => Array.Empty<string>());

        Assert.False(result.Success);
        Assert.Equal(PlatformInstaller.ErrorCancelled, result.ErrorKey);
        Assert.False(Directory.Exists(createdDir));
    }

    // --- DeleteVersionDirectoryCoreAsync (этап 0.3.9.215) ---

    [Fact]
    public async Task DeleteVersionDirectory_Success_RunsElevatedRemoveItemAndRefreshes()
    {
        ProcessStartInfo? captured = null;
        var refreshed = false;
        var version = new PlatformVersionInfo
        {
            Display = "8.3.27.1688",
            Path = @"C:\Program Files\1cv8\8.3.27.1688",
        };

        var result = await PlatformInstaller.DeleteVersionDirectoryCoreAsync(
            version, null, CancellationToken.None,
            psi =>
            {
                captured = psi;
                return new FakeInstallerProcess(exitCode: 0, hasExited: () => true);
            },
            () => refreshed = true);

        Assert.True(result.Success);
        Assert.Null(result.ErrorKey);

        // PowerShell с UAC и командой Remove-Item по каталогу версии.
        Assert.NotNull(captured);
        Assert.Equal("runas", captured.Verb);
        Assert.True(captured.UseShellExecute);
        Assert.Contains("powershell", captured.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Remove-Item", captured.Arguments);
        Assert.Contains(@"C:\Program Files\1cv8\8.3.27.1688", captured.Arguments);

        Assert.True(refreshed);
    }

    [Fact]
    public async Task DeleteVersionDirectory_NonZeroExitCode_ReturnsDeleteFailed()
    {
        var version = new PlatformVersionInfo { Display = "8.3.27.1688", Path = @"C:\1cv8\8.3.27.1688" };
        var refreshed = false;

        var result = await PlatformInstaller.DeleteVersionDirectoryCoreAsync(
            version, null, CancellationToken.None,
            _ => new FakeInstallerProcess(exitCode: 2, hasExited: () => true),
            () => refreshed = true);

        Assert.False(result.Success);
        Assert.Equal(PlatformInstaller.ErrorDeleteFailed, result.ErrorKey);
        Assert.False(refreshed); // при неудаче кэш не пересканируется
    }

    [Fact]
    public async Task DeleteVersionDirectory_StarterReturnsNull_ReturnsDeleteFailed()
    {
        var version = new PlatformVersionInfo { Display = "8.3.27.1688", Path = @"C:\1cv8\8.3.27.1688" };

        var result = await PlatformInstaller.DeleteVersionDirectoryCoreAsync(
            version, null, CancellationToken.None, _ => null, () => { });

        Assert.False(result.Success);
        Assert.Equal(PlatformInstaller.ErrorDeleteFailed, result.ErrorKey);
    }

    [Fact]
    public async Task DeleteVersionDirectory_StarterThrows_ReturnsDeleteFailed()
    {
        var version = new PlatformVersionInfo { Display = "8.3.27.1688", Path = @"C:\1cv8\8.3.27.1688" };

        var result = await PlatformInstaller.DeleteVersionDirectoryCoreAsync(
            version, null, CancellationToken.None,
            _ => throw new InvalidOperationException("UAC declined"),
            () => { });

        Assert.False(result.Success);
        Assert.Equal(PlatformInstaller.ErrorDeleteFailed, result.ErrorKey);
    }

    [Fact]
    public async Task DeleteVersionDirectory_Cancelled_ReturnsCancelledKey()
    {
        var version = new PlatformVersionInfo { Display = "8.3.27.1688", Path = @"C:\1cv8\8.3.27.1688" };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await PlatformInstaller.DeleteVersionDirectoryCoreAsync(
            version, null, cts.Token,
            _ => new FakeInstallerProcess(exitCode: 0, hasExited: () => true),
            () => { });

        Assert.False(result.Success);
        Assert.Equal(PlatformInstaller.ErrorCancelled, result.ErrorKey);
    }

    [Fact]
    public async Task DeleteVersionDirectory_EmptyPath_ReturnsDeleteFailed()
    {
        var version = new PlatformVersionInfo { Display = "8.3.27.1688", Path = "" };
        var started = false;

        var result = await PlatformInstaller.DeleteVersionDirectoryCoreAsync(
            version, null, CancellationToken.None,
            _ =>
            {
                started = true;
                return new FakeInstallerProcess(exitCode: 0, hasExited: () => true);
            },
            () => { });

        Assert.False(result.Success);
        Assert.Equal(PlatformInstaller.ErrorDeleteFailed, result.ErrorKey);
        Assert.False(started); // процесс не запускается без каталога
    }

    // --- Вспомогательные ---

    private string CreateDir(string name)
    {
        var dir = Path.Combine(_tempRoot, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Fake-процесс установщика для тестов <see cref="PlatformInstaller.RunSetupCoreAsync"/>.</summary>
    private sealed class FakeInstallerProcess : PlatformInstaller.IInstallerProcess
    {
        private readonly int _exitCode;
        private readonly Func<bool> _hasExited;

        public FakeInstallerProcess(int exitCode, Func<bool> hasExited)
        {
            _exitCode = exitCode;
            _hasExited = hasExited;
        }

        public bool KillCalled { get; private set; }

        public bool HasExited => _hasExited();

        public int ExitCode => _exitCode;

        public void Kill() => KillCalled = true;

        public void Dispose()
        {
        }
    }
}
#endif