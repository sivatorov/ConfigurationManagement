using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Configuration_Management.Localization;
using Configuration_Management.Models;
using Configuration_Management.Services;
using Configuration_Management.ViewModels;
using Xunit;

namespace ConfigurationManagement.Tests;

/// <summary>
/// Тесты ViewModel окна «Обновление платформы 1С» (этап 0.3.9.213, функция 9):
/// проверка каталога версий (fake <see cref="IPlatformUpdateService"/>), заполнение
/// строк со статусами/размерами/совместимыми базами, журнал и обработка ошибок
/// (авторизация, исключение сервиса) без падения VM; «Скачать и установить» с
/// fake загрузчиком/установщиком (download→install→refresh, прогресс 0..1, блокировка
/// повторного запуска); «Только скачать» с fake-диалогом сохранения и отменой;
/// «Удалить старые версии» с диалогом выбора версий и информационным уведомлением
/// при пустом списке кандидатов (issue #334); CanExecute команд от IsBusy/выделения.
/// </summary>
public sealed class PlatformUpdateViewModelTests
{
    private const long MiB = 1024 * 1024;

    // ---------- Хелперы ----------

    private static PlatformRelease Release(string version, params PlatformReleaseFile[] files)
    {
        var release = new PlatformRelease { Version = version };
        release.Files.AddRange(files);
        return release;
    }

    private static PlatformReleaseFile DistroFile(long sizeBytes, string fileName = "8.3.27.2214_x64.zip")
        => new()
        {
            FileName = fileName,
            Url = $"https://releases.1c.ru/dist/{fileName}",
            SizeBytes = sizeBytes,
            Architecture = "x64",
            Kind = PlatformDistributionKind.WindowsSetupZip,
        };

    private static Infobase Base(string id, string platformVersion)
        => new() { Id = id, Name = $"База {id}", PlatformVersion = platformVersion };

    private static FakePlatformUpdateService OkService(params PlatformRelease[] releases)
        => new()
        {
            AvailableResult = new PlatformCatalogResult { Status = PortalFetchStatus.Ok, Releases = releases },
        };

    private static PlatformUpdateViewModel CreateVm(
        IPlatformUpdateService? service = null,
        List<Infobase>? bases = null,
        Func<IReadOnlyList<string>>? installed = null,
        Func<string, string, IProgress<double>?, CancellationToken, Task<string?>>? download = null,
        Func<string, string, string?, IProgress<string>?, CancellationToken,
            Task<(bool Success, string? ErrorKey, int ExitCode)>>? install = null,
        Func<string?, string?>? saveDialog = null,
        Func<IReadOnlyList<string>>? runningProcesses = null,
        Func<bool>? isAdmin = null,
        Func<string, long?>? freeBytes = null,
        Func<string, bool>? signed = null,
        Func<string, string, bool>? confirm = null,
        Action<string, string, NotificationKind, NotificationEvent>? notify = null,
        FakeAppLogger? logger = null,
        Func<IReadOnlyList<PlatformVersionInfo>>? installedInfos = null,
        Func<IReadOnlyList<string>>? runningBinPaths = null,
        Func<PlatformVersionInfo, IProgress<string>?, CancellationToken,
            Task<(bool Success, string? ErrorKey)>>? deleteVersion = null,
        Func<string, string>? buildUninstall = null,
        Action<string>? copyCommand = null,
        Action<Action>? dispatchToUi = null,
        Func<IReadOnlyList<OldVersionCleanupEntry>, IReadOnlyList<PlatformVersionInfo>?>? chooseVersionsToDelete = null,
        Func<IReadOnlyList<PlatformDistributionOption>, PlatformDistributionOption?>? chooseDistribution = null,
        BackgroundDownloadManager? backgroundDownloads = null)
    {
        return new PlatformUpdateViewModel(
            service ?? OkService(),
            new FakeRepository(bases ?? new List<Infobase>()),
            installed ?? (() => new List<string>()),
            download ?? ((url, target, progress, ct) => Task.FromResult<string?>(target)),
            install ?? ((zip, version, dir, log, ct) =>
                Task.FromResult((Success: true, ErrorKey: (string?)null, ExitCode: 0))),
            saveDialog,
            runningProcesses ?? (() => new List<string>()),
            isAdmin ?? (() => true),
            freeBytes ?? (_ => null),
            signed ?? (_ => true),
            confirm ?? ((_, _) => true),
            notify ?? ((_, _, _, _) => { }),
            logger,
            installedInfos ?? (() => Array.Empty<PlatformVersionInfo>()),
            runningBinPaths ?? (() => Array.Empty<string>()),
            deleteVersion,
            buildUninstall,
            copyCommand,
            dispatchToUi,
            chooseDistribution: chooseDistribution,
            chooseVersionsToDelete: chooseVersionsToDelete,
            backgroundDownloads: backgroundDownloads);
    }

    // ---------- CheckUpdatesAsync ----------

    [Fact]
    public async Task CheckUpdatesAsync_FillsRows_WithStatusesSizesAndCompatibleBases()
    {
        var file = DistroFile(200 * MiB);
        var service = OkService(Release("8.3.27.2214", file), Release("8.3.27.1688"));
        service.PickedFile = file;

        var bases = new List<Infobase>
        {
            Base("1", "8.3.27.1644"),
            Base("2", "8.3.26.1182"),
            Base("3", ""),
        };
        var vm = CreateVm(service, bases, installed: () => new List<string> { "8.3.27.1688" });

        await vm.CheckUpdatesAsync();

        Assert.Equal(2, vm.Rows.Count);

        // Установленная версия со свежим обновлением в каталоге.
        var installedRow = vm.Rows.Single(r => r.Version == "8.3.27.1688");
        Assert.True(installedRow.IsInstalled);
        Assert.True(installedRow.HasUpdate);
        Assert.Equal("8.3.27.2214", installedRow.AvailableVersion);
        Assert.Equal(LocalizationManager.T("PlatformUpdate.Status.UpdateAvailable"), installedRow.StatusText);
        Assert.Equal(1, installedRow.CompatibleBases); // только база «8.3.27.1644» с префиксом 8.3.27
        Assert.Equal(PlatformUpdateRowViewModel.EmptySizeText, installedRow.SizeText); // файлы не подгружены

        // Доступная версия из каталога с выбранным файлом дистрибутива.
        var availableRow = vm.Rows.Single(r => r.Version == "8.3.27.2214");
        Assert.False(availableRow.IsInstalled);
        Assert.False(availableRow.HasUpdate);
        Assert.Equal(LocalizationManager.T("PlatformUpdate.Status.Available"), availableRow.StatusText);
        Assert.Equal(Infobase.FormatSize(file.SizeBytes), availableRow.SizeText);
        Assert.Equal(1, availableRow.CompatibleBases);

        Assert.False(vm.IsBusy);
        Assert.Contains(LocalizationManager.T("PlatformUpdate.Status.Checking"), vm.LogText);
    }

    [Fact]
    public async Task CheckUpdatesAsync_SortsRowsDescending()
    {
        var service = OkService(Release("8.3.10"), Release("8.3.9.2577"), Release("8.3.27.2214"));
        var vm = CreateVm(service);

        await vm.CheckUpdatesAsync();

        Assert.Equal(new[] { "8.3.27.2214", "8.3.10", "8.3.9.2577" }, vm.Rows.Select(r => r.Version).ToArray());
    }

    [Fact]
    public async Task CheckUpdatesAsync_AuthRequired_WritesLocalizedKeyAndKeepsVmAlive()
    {
        var service = new FakePlatformUpdateService
        {
            AvailableResult = new PlatformCatalogResult
            {
                Status = PortalFetchStatus.AuthRequired,
                ErrorKey = "PlatformUpdate.Error.AuthRequired",
            },
        };
        var vm = CreateVm(service);

        await vm.CheckUpdatesAsync();

        Assert.Empty(vm.Rows);
        Assert.False(vm.IsBusy);
        Assert.Contains(LocalizationManager.T("PlatformUpdate.Error.AuthRequired"), vm.LogText);
    }

    [Fact]
    public async Task CheckUpdatesAsync_ServiceThrows_WritesErrorAndDoesNotCrash()
    {
        var vm = CreateVm(new ThrowingPlatformUpdateService());

        await vm.CheckUpdatesAsync();

        Assert.Empty(vm.Rows);
        Assert.False(vm.IsBusy);
        Assert.Contains(LocalizationManager.T("PlatformUpdate.Error.Network"), vm.LogText);
    }

    [Fact]
    public async Task CheckUpdatesAsync_ServiceThrows_NotifiesErrorAndKeepsProgressZero()
    {
        // Issue #334: fake-делегат бросает исключение → статус «ошибка» (журнал +
        // уведомление), приложение не падает, прогресс не «вспыхивает» в 1 (анти-мигание).
        var notified = new List<(string Title, string Message, NotificationKind Kind, NotificationEvent Evt)>();
        var vm = CreateVm(
            new ThrowingPlatformUpdateService(),
            notify: (title, message, kind, evt) => notified.Add((title, message, kind, evt)));

        await vm.CheckUpdatesAsync();

        Assert.Empty(vm.Rows);
        Assert.False(vm.IsBusy);
        Assert.Equal(0, vm.Progress);
        var notification = Assert.Single(notified);
        Assert.Equal(NotificationKind.Error, notification.Kind);
        Assert.Equal(NotificationEvent.Update, notification.Evt);
        Assert.Contains(LocalizationManager.T("PlatformUpdate.Error.NetworkError"), vm.LogText);
    }

    [Fact]
    public async Task CheckUpdatesAsync_EmptyErrorKey_UsesDefaultNetworkErrorKey()
    {
        // Битая/устаревшая реализация провайдера вернула статус ошибки без ключа —
        // пользователь должен увидеть понятное сообщение, а не пустую строку.
        var service = new FakePlatformUpdateService
        {
            AvailableResult = new PlatformCatalogResult
            {
                Status = PortalFetchStatus.NetworkError,
                ErrorKey = string.Empty,
            },
        };
        var vm = CreateVm(service);

        await vm.CheckUpdatesAsync();

        Assert.Empty(vm.Rows);
        Assert.False(vm.IsBusy);
        Assert.Equal(0, vm.Progress);
        Assert.Contains(LocalizationManager.T("PlatformUpdate.Error.NetworkError"), vm.LogText);
    }

    [Fact]
    public async Task CheckUpdatesAsync_EmptyProviderResponse_WritesErrorWithoutException()
    {
        // Сценарий issue #334: портал вернул пустое тело (releases.1c.ru/project/Platform83) —
        // провайдер (PlatformUpdateService.FetchTextAsync) возвращает NetworkError БЕЗ
        // исключения; VM обязана показать понятное сообщение (общий ключ ErrorNetwork) и
        // не пробрасывать ошибку даже при запуске проверки из фонового потока.
        var service = new FakePlatformUpdateService
        {
            AvailableResult = new PlatformCatalogResult
            {
                Status = PortalFetchStatus.NetworkError,
                ErrorKey = string.Empty,
            },
        };
        var vm = CreateVm(service);

        await Task.Run(async () => await vm.CheckUpdatesAsync()); // исключение уронило бы сам тест

        Assert.Empty(vm.Rows);
        Assert.False(vm.IsBusy);
        Assert.Equal(0, vm.Progress);
        Assert.Contains(LocalizationManager.T("PlatformUpdate.Error.NetworkError"), vm.LogText);
    }

    [Fact]
    public async Task AppendLog_FromBackgroundThread_RaisesPropertyChangedWithoutException()
    {
        // Регрессия issue #334: AppendLog вызывается из фоновых задач (CheckUpdatesAsync
        // использует ConfigureAwait(false)), и обработчики UI получают уведомление на
        // фоновом потоке. Контракт VM: уведомление поднимается, исключений не бросается —
        // потокозависимые UI-действия (ScrollToEnd) выполняет само окно через Dispatcher.
        var vm = CreateVm();
        var notifications = new List<string?>();
        vm.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);

        await Task.Run(() => vm.AppendLog("Фоновая строка журнала"));

        Assert.Contains(nameof(PlatformUpdateViewModel.LogText), notifications);
        Assert.Contains("Фоновая строка журнала", vm.LogText);
    }

    [Fact]
    public async Task CheckUpdatesAsync_FromBackgroundThread_RaisesNotificationsWithoutException()
    {
        // Регрессия issue #334: запуск проверки из фонового потока (команда окна может
        // выполниться вне UI-потока) не бросает исключений, а уведомления PropertyChanged
        // поднимаются — UI сам перекидывает прокрутку в свой поток.
        var service = OkService(Release("8.3.27.2214"));
        var vm = CreateVm(service);
        var notifications = new List<string?>();
        vm.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);

        await Task.Run(async () => await vm.CheckUpdatesAsync());

        Assert.Single(vm.Rows);
        Assert.False(vm.IsBusy);
        Assert.Contains(nameof(PlatformUpdateViewModel.LogText), notifications);
        Assert.Contains(nameof(PlatformUpdateViewModel.IsBusy), notifications);
    }

    [Fact]
    public async Task CheckUpdatesAsync_SetsIsBusy_WhileOperationInProgress()
    {
        var gate = new TaskCompletionSource<PlatformCatalogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = CreateVm(new GatedPlatformUpdateService(gate.Task));

        var checkTask = vm.CheckUpdatesAsync();

        Assert.True(vm.IsBusy);
        Assert.False(vm.CheckCommand.CanExecute(null));
        Assert.False(vm.DownloadAndInstallCommand.CanExecute(null));

        gate.SetResult(new PlatformCatalogResult { Status = PortalFetchStatus.Ok, Releases = new List<PlatformRelease>() });
        await checkTask;

        Assert.False(vm.IsBusy);
        Assert.True(vm.CheckCommand.CanExecute(null));
    }

    // ---------- DownloadAndInstallAsync ----------

    [Fact]
    public async Task DownloadAndInstallAsync_RunsDownloadInstallRefresh_WithProgressAndLock()
    {
        var file = DistroFile(50 * MiB);
        var service = OkService(Release("8.3.27.2214", file), Release("8.3.27.1688"));
        service.PickedFile = file;

        var installedVersions = new List<string> { "8.3.27.1688" };
        var downloadCalls = new List<(string Url, string Target)>();
        var reportedProgress = new List<double>();
        var installCalls = new List<(string Zip, string Version)>();
        var downloadGate = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var vm = CreateVm(
            service,
            installed: () => installedVersions.ToList(),
            download: (url, target, progress, ct) =>
            {
                downloadCalls.Add((url, target));
                progress?.Report(0.25);
                progress?.Report(0.75);
                return downloadGate.Task;
            },
            install: (zip, version, dir, log, ct) =>
            {
                installCalls.Add((zip, version));
                return Task.FromResult((Success: true, ErrorKey: (string?)null, ExitCode: 0));
            });

        await vm.CheckUpdatesAsync();
        var row = vm.Rows.Single(r => r.Version == "8.3.27.2214");
        vm.SelectedRow = row;

        var operation = vm.DownloadAndInstallAsync();

        // Пока идёт загрузка — строка помечена, команды заблокированы, прогресс в 0..1.
        Assert.True(vm.IsBusy);
        Assert.True(row.IsDownloading);
        Assert.False(vm.DownloadAndInstallCommand.CanExecute(null)); // повторный запуск заблокирован
        Assert.False(vm.DownloadOnlyCommand.CanExecute(null));
        Assert.False(vm.CheckCommand.CanExecute(null));
        Assert.All(reportedProgress, p => Assert.InRange(p, 0, 1));

        // После установки пересканирование находит новую версию.
        installedVersions.Add("8.3.27.2214");
        downloadGate.SetResult(downloadCalls[0].Target);
        await operation;

        // Последовательность: download → install → refresh.
        Assert.Single(downloadCalls);
        Assert.Equal(file.Url, downloadCalls[0].Url);
        Assert.StartsWith(Path.Combine(Path.GetTempPath(), "cm_platformdl_"), downloadCalls[0].Target);
        Assert.EndsWith(file.FileName, Path.GetFileName(downloadCalls[0].Target));
        Assert.Single(installCalls);
        Assert.Equal(downloadCalls[0].Target, installCalls[0].Zip);
        Assert.Equal("8.3.27.2214", installCalls[0].Version);

        Assert.Equal(1, row.Progress);
        Assert.Equal(1, vm.Progress);
        Assert.False(row.IsDownloading);
        Assert.False(vm.IsBusy);

        // refresh: версия 8.3.27.2214 теперь помечена как установленная.
        var refreshed = vm.Rows.Single(r => r.Version == "8.3.27.2214");
        Assert.True(refreshed.IsInstalled);
        Assert.Contains(LocalizationManager.T("PlatformUpdate.Status.Installed"), vm.LogText);
    }

    [Fact]
    public async Task DownloadAndInstallAsync_InstallFailed_WritesErrorKeyToLog()
    {
        var file = DistroFile(10 * MiB);
        var service = OkService(Release("8.3.27.2214", file));
        service.PickedFile = file;

        var vm = CreateVm(
            service,
            install: (zip, version, dir, log, ct) =>
                Task.FromResult((Success: false, ErrorKey: (string?)"PlatformUpdate.Error.SetupNotFound", ExitCode: -1)));

        await vm.CheckUpdatesAsync();
        vm.SelectedRow = vm.Rows.Single();

        await vm.DownloadAndInstallAsync();

        Assert.False(vm.IsBusy);
        Assert.Contains(LocalizationManager.T("PlatformUpdate.Error.SetupNotFound"), vm.LogText);
        // При неудаче установки список не перестраивается.
        Assert.False(vm.Rows.Single().IsInstalled);
    }

    // ---------- DownloadOnlyAsync ----------

    [Fact]
    public async Task DownloadOnlyAsync_SavesToDialogPath()
    {
        var file = DistroFile(10 * MiB);
        var service = OkService(Release("8.3.27.2214", file));
        service.PickedFile = file;

        var savedPaths = new List<string>();
        const string targetPath = "/tmp/cm_saved/platform_8.3.27.2214_x64.zip";
        var vm = CreateVm(
            service,
            download: (url, target, progress, ct) =>
            {
                savedPaths.Add(target);
                return Task.FromResult<string?>(target);
            },
            saveDialog: _ => targetPath);

        await vm.CheckUpdatesAsync();
        vm.SelectedRow = vm.Rows.Single();

        await vm.DownloadOnlyAsync();

        // Файл сохранён именно в путь, выбранный диалогом (журнал не проверяем текстом:
        // без инициализации локализации LocalizationManager.T возвращает ключ без плейсхолдера).
        Assert.Equal(targetPath, Assert.Single(savedPaths));
        Assert.False(vm.IsBusy);
        Assert.Equal(1, vm.Progress);
        Assert.NotEmpty(vm.LogText);
    }

    [Fact]
    public async Task DownloadOnlyAsync_CancelledDialog_IsNoOp()
    {
        var file = DistroFile(10 * MiB);
        var service = OkService(Release("8.3.27.2214", file));
        service.PickedFile = file;

        var downloadCalls = new List<string>();
        var vm = CreateVm(
            service,
            download: (url, target, progress, ct) =>
            {
                downloadCalls.Add(target);
                return Task.FromResult<string?>(target);
            },
            saveDialog: _ => null);

        await vm.CheckUpdatesAsync();
        vm.SelectedRow = vm.Rows.Single();

        await vm.DownloadOnlyAsync();

        Assert.Empty(downloadCalls);
        Assert.False(vm.IsBusy);
        Assert.Contains(LocalizationManager.T("PlatformUpdate.Error.Cancelled"), vm.LogText);
    }

    [Fact]
    public async Task DownloadOnlyAsync_LoadsReleaseFilesLazily()
    {
        var file = DistroFile(10 * MiB);
        var release = new PlatformRelease { Version = "8.3.27.2214" }; // Files ещё не подгружены
        var service = OkService(release);
        service.PickedFile = file;
        service.FilesResult = new PlatformCatalogResult
        {
            Status = PortalFetchStatus.Ok,
            Release = new PlatformRelease { Version = "8.3.27.2214", Files = { file } },
        };

        var saved = new List<string>();
        var vm = CreateVm(
            service,
            download: (url, target, progress, ct) =>
            {
                saved.Add(target);
                return Task.FromResult<string?>(target);
            },
            saveDialog: _ => "C:\\temp\\platform.zip");

        await vm.CheckUpdatesAsync();
        var row = vm.Rows.Single();
        Assert.Equal(PlatformUpdateRowViewModel.EmptySizeText, row.SizeText); // файлы не загружены

        vm.SelectedRow = row;
        await vm.DownloadOnlyAsync();

        Assert.Equal(1, service.LoadFilesCalls);
        Assert.NotEqual(PlatformUpdateRowViewModel.EmptySizeText, row.SizeText); // размер появился
        Assert.Single(saved);
    }

    [Fact]
    public async Task DownloadOnlyAsync_VersionFilesPageWithoutFiles_LogsNoFilesAndAborts()
    {
        // issue #330 (комментарий 7OH): страница version_files получена (Ok), но файлов
        // не распознано — операция не продолжается с пустым выбором дистрибутива,
        // в журнал попадает понятное сообщение.
        var release = new PlatformRelease { Version = "8.3.27.2214" };
        var service = OkService(release);
        service.FilesResult = new PlatformCatalogResult
        {
            Status = PortalFetchStatus.Ok,
            Release = new PlatformRelease { Version = "8.3.27.2214" },
        };

        var downloadCalls = new List<string>();
        var vm = CreateVm(
            service,
            download: (url, target, progress, ct) =>
            {
                downloadCalls.Add(target);
                return Task.FromResult<string?>(target);
            },
            saveDialog: _ => "C:\\temp\\platform.zip");

        await vm.CheckUpdatesAsync();
        vm.SelectedRow = vm.Rows.Single();
        await vm.DownloadOnlyAsync();

        Assert.Equal(1, service.LoadFilesCalls);
        Assert.Empty(downloadCalls);
        Assert.False(vm.IsBusy);
        var expected = string.Format(
            LocalizationManager.T("PlatformDownload.Error.NoFiles"), "8.3.27.2214");
        Assert.Contains(expected, vm.LogText, StringComparison.Ordinal);
    }

    // ---------- Preflight и уведомления (этап 0.3.9.214) ----------

    [Fact]
    public async Task DownloadAndInstallAsync_PreflightWarnings_ShownBeforeInstall()
    {
        var file = DistroFile(50 * MiB);
        var service = OkService(Release("8.3.27.2214", file));
        service.PickedFile = file;

        var confirmCalls = new List<(string Title, string Message)>();
        var installCalls = new List<string>();
        var logger = new FakeAppLogger();

        var vm = CreateVm(
            service,
            install: (zip, version, dir, log, ct) =>
            {
                installCalls.Add(zip);
                return Task.FromResult((Success: true, ErrorKey: (string?)null, ExitCode: 0));
            },
            runningProcesses: () => new List<string> { "1cv8c" },
            confirm: (title, message) =>
            {
                confirmCalls.Add((title, message));
                return true;
            },
            logger: logger);

        await vm.CheckUpdatesAsync();
        vm.SelectedRow = vm.Rows.Single();
        await vm.DownloadAndInstallAsync();

        // Диалог подтверждения показан до установки, предупреждение в журнале окна.
        Assert.Single(confirmCalls);
        Assert.Equal(LocalizationManager.T("PlatformUpdate.Confirm.InstallTitle"), confirmCalls[0].Title);
        Assert.Contains(LocalizationManager.T("PlatformUpdate.Error.RunningProcesses"), confirmCalls[0].Message);
        Assert.Contains(LocalizationManager.T("PlatformUpdate.Error.RunningProcesses"), vm.LogText);

        // Подтверждено — установка выполнилась; предупреждения попали в IAppLogger.
        Assert.Single(installCalls);
        Assert.Contains("замечания перед установкой", logger.WarningsJoined);
    }

    [Fact]
    public async Task DownloadAndInstallAsync_PreflightCancelled_InstallNotExecuted()
    {
        var file = DistroFile(50 * MiB);
        var service = OkService(Release("8.3.27.2214", file));
        service.PickedFile = file;

        var installCalls = new List<string>();
        var vm = CreateVm(
            service,
            install: (zip, version, dir, log, ct) =>
            {
                installCalls.Add(zip);
                return Task.FromResult((Success: true, ErrorKey: (string?)null, ExitCode: 0));
            },
            runningProcesses: () => new List<string> { "1cv8c" },
            confirm: (_, _) => false);

        await vm.CheckUpdatesAsync();
        vm.SelectedRow = vm.Rows.Single();
        await vm.DownloadAndInstallAsync();

        // Отмена диалога останавливает операцию до запуска установщика.
        Assert.Empty(installCalls);
        Assert.Contains(LocalizationManager.T("PlatformUpdate.Error.Cancelled"), vm.LogText);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task DownloadAndInstallAsync_NotifySuccess_WithKindAndEvent()
    {
        var file = DistroFile(50 * MiB);
        var service = OkService(Release("8.3.27.2214", file));
        service.PickedFile = file;

        var notified = new List<(string Title, string Message, NotificationKind Kind, NotificationEvent Evt)>();
        var vm = CreateVm(service, notify: (title, message, kind, evt) =>
            notified.Add((title, message, kind, evt)));

        await vm.CheckUpdatesAsync();
        vm.SelectedRow = vm.Rows.Single();
        await vm.DownloadAndInstallAsync();

        var notification = Assert.Single(notified);
        Assert.Equal(NotificationKind.Success, notification.Kind);
        Assert.Equal(NotificationEvent.Update, notification.Evt);
        Assert.Equal(
            string.Format(LocalizationManager.T("Notify.PlatformUpdateDone"), "8.3.27.2214"),
            notification.Message);
    }

    [Fact]
    public async Task DownloadAndInstallAsync_PartialSuccess_NotifyWarning()
    {
        var file = DistroFile(50 * MiB);
        var service = OkService(Release("8.3.27.2214", file));
        service.PickedFile = file;

        var notified = new List<(string Title, string Message, NotificationKind Kind, NotificationEvent Evt)>();
        var vm = CreateVm(
            service,
            runningProcesses: () => new List<string> { "1cv8c" },
            confirm: (_, _) => true,
            notify: (title, message, kind, evt) =>
                notified.Add((title, message, kind, evt)));

        await vm.CheckUpdatesAsync();
        vm.SelectedRow = vm.Rows.Single();
        await vm.DownloadAndInstallAsync();

        // Установлено, но были предупреждения (занятые процессы) — Warning.
        var notification = Assert.Single(notified);
        Assert.Equal(NotificationKind.Warning, notification.Kind);
        Assert.Equal(NotificationEvent.Update, notification.Evt);
    }

    [Fact]
    public async Task DownloadAndInstallAsync_InstallFailed_NotifyError()
    {
        var file = DistroFile(10 * MiB);
        var service = OkService(Release("8.3.27.2214", file));
        service.PickedFile = file;

        var notified = new List<(string Title, string Message, NotificationKind Kind, NotificationEvent Evt)>();
        var vm = CreateVm(
            service,
            install: (zip, version, dir, log, ct) =>
                Task.FromResult((Success: false, ErrorKey: (string?)"PlatformUpdate.Error.SetupNotFound", ExitCode: -1)),
            notify: (title, message, kind, evt) =>
                notified.Add((title, message, kind, evt)));

        await vm.CheckUpdatesAsync();
        vm.SelectedRow = vm.Rows.Single();
        await vm.DownloadAndInstallAsync();

        var notification = Assert.Single(notified);
        Assert.Equal(NotificationKind.Error, notification.Kind);
        Assert.Equal(NotificationEvent.Update, notification.Evt);
        Assert.Equal(
            string.Format(
                LocalizationManager.T("Notify.PlatformUpdateError"),
                LocalizationManager.T("PlatformUpdate.Error.SetupNotFound")),
            notification.Message);
    }

    [Fact]
    public async Task DownloadOnlyAsync_NotifySuccess()
    {
        var file = DistroFile(10 * MiB);
        var service = OkService(Release("8.3.27.2214", file));
        service.PickedFile = file;

        var notified = new List<(string Title, string Message, NotificationKind Kind, NotificationEvent Evt)>();
        var vm = CreateVm(
            service,
            saveDialog: _ => "/tmp/cm_saved/platform_8.3.27.2214_x64.zip",
            notify: (title, message, kind, evt) =>
                notified.Add((title, message, kind, evt)));

        await vm.CheckUpdatesAsync();
        vm.SelectedRow = vm.Rows.Single();
        await vm.DownloadOnlyAsync();

        var notification = Assert.Single(notified);
        Assert.Equal(NotificationKind.Success, notification.Kind);
        Assert.Equal(NotificationEvent.Update, notification.Evt);
        Assert.Equal(
            string.Format(LocalizationManager.T("PlatformUpdate.Progress.Done"), "/tmp/cm_saved/platform_8.3.27.2214_x64.zip"),
            notification.Message);
    }

    // ---------- CanExecute ----------

    [Fact]
    public async Task Commands_CanExecute_DependOnSelectionAndBusyState()
    {
        var file = DistroFile(MiB);
        var service = OkService(Release("8.3.27.2214", file));
        service.PickedFile = file;
        var vm = CreateVm(service);

        // До проверки: без строк команды скачивания недоступны.
        Assert.False(vm.DownloadAndInstallCommand.CanExecute(null));
        Assert.False(vm.DownloadOnlyCommand.CanExecute(null));

        await vm.CheckUpdatesAsync();

        Assert.True(vm.CheckCommand.CanExecute(null));
        Assert.False(vm.DownloadAndInstallCommand.CanExecute(null)); // нет выделения
        Assert.False(vm.DownloadOnlyCommand.CanExecute(null));
        Assert.True(vm.RemoveOldVersionsCommand.CanExecute(null)); // активна с этапа 0.3.9.215

        vm.SelectedRow = vm.Rows.Single();
        Assert.True(vm.DownloadAndInstallCommand.CanExecute(null));
        Assert.True(vm.DownloadOnlyCommand.CanExecute(null));

        // IsBusy блокирует все команды.
        vm.IsBusy = true;
        Assert.False(vm.CheckCommand.CanExecute(null));
        Assert.False(vm.DownloadAndInstallCommand.CanExecute(null));
        Assert.False(vm.DownloadOnlyCommand.CanExecute(null));
        Assert.False(vm.RemoveOldVersionsCommand.CanExecute(null));
        vm.IsBusy = false;

        Assert.True(vm.DownloadAndInstallCommand.CanExecute(null));
    }

    // ---------- RemoveOldVersions (этап 0.3.9.215) ----------

    [Fact]
    public async Task RemoveOldVersions_Windows_DeletesCandidatesSequentiallyAndRefreshesRows()
    {
        var file = DistroFile(MiB);
        var service = OkService(Release("8.3.27.2214", file), Release("8.3.27.1688"), Release("8.3.26.1890"));
        service.PickedFile = file;

        var installedInfos = new List<PlatformVersionInfo>
        {
            new() { Display = "8.3.27.2214", Path = @"C:\Program Files\1cv8\8.3.27.2214" },
            new() { Display = "8.3.27.1688", Path = @"C:\Program Files\1cv8\8.3.27.1688" },
            new() { Display = "8.3.26.1890", Path = @"C:\Program Files\1cv8\8.3.26.1890" },
        };
        var deleted = new List<string>();
        var confirmCalls = 0;

        var vm = CreateVm(
            service,
            // Строковый список (для перестроения строк) и инфо с путями (для кандидатов)
            // — один источник, чтобы RefreshInstalledAsync после удаления видел актуальный состав.
            installed: () => installedInfos.Select(v => OldVersionCleaner.CleanVersion(v.Display)).ToList(),
            installedInfos: () => installedInfos.ToList(),
            deleteVersion: (version, log, ct) =>
            {
                deleted.Add(version.Display);
                installedInfos.RemoveAll(v => v.Display == version.Display);
                return Task.FromResult((Success: true, ErrorKey: (string?)null));
            },
            confirm: (_, _) =>
            {
                confirmCalls++;
                return true;
            });

        await vm.CheckUpdatesAsync();
        Assert.Equal(3, vm.Rows.Count(r => r.IsInstalled));

        await vm.RemoveOldVersionsAsync();

        // Кандидаты собраны: новейшая 8.3.27.2214 исключена, остальные удалены последовательно.
        Assert.Equal(1, confirmCalls);
        Assert.Equal(new[] { "8.3.27.1688", "8.3.26.1890" }, deleted);
        Assert.False(vm.IsBusy);

        // Список перестроен: удалённые версии теперь доступны, а не установлены.
        Assert.False(vm.Rows.Single(r => r.Version == "8.3.27.1688").IsInstalled);
        Assert.False(vm.Rows.Single(r => r.Version == "8.3.26.1890").IsInstalled);
        Assert.True(vm.Rows.Single(r => r.Version == "8.3.27.2214").IsInstalled);
    }

    [Fact]
    public async Task RemoveOldVersions_DeleteContinuesOnBackgroundThread_RowsRebuiltViaDispatcher()
    {
        // issue #334 (регресс CollectionView): удаление каталога завершается на фоновом
        // потоке (ConfigureAwait(false)), после чего RefreshInstalledAsync мутирует
        // ObservableCollection Rows. С маршаллером перестройка обязана пройти через
        // UI-поток (диспетчер), иначе WPF DataGrid бросает NotSupportedException.
        var file = DistroFile(MiB);
        var service = OkService(Release("8.3.27.2214", file), Release("8.3.27.1688"));
        service.PickedFile = file;

        var installedInfos = new List<PlatformVersionInfo>
        {
            new() { Display = "8.3.27.2214", Path = @"C:\Program Files\1cv8\8.3.27.2214" },
            new() { Display = "8.3.27.1688", Path = @"C:\Program Files\1cv8\8.3.27.1688" },
        };
        var dispatchCalls = 0;
        Action<Action> dispatch = action =>
        {
            Interlocked.Increment(ref dispatchCalls);
            action();
        };

        var vm = CreateVm(
            service,
            installed: () => installedInfos.Select(v => OldVersionCleaner.CleanVersion(v.Display)).ToList(),
            installedInfos: () => installedInfos.ToList(),
            deleteVersion: (version, log, ct) =>
            {
                installedInfos.RemoveAll(v => v.Display == version.Display);
                // Имитация реального удаления: продолжение — в фоновом потоке.
                return Task.Run(async () =>
                {
                    await Task.Delay(1).ConfigureAwait(false);
                    return (Success: true, ErrorKey: (string?)null);
                });
            },
            dispatchToUi: dispatch);

        await vm.CheckUpdatesAsync();
        Assert.Equal(2, vm.Rows.Count(r => r.IsInstalled));
        var dispatchedAfterCheck = Interlocked.CompareExchange(ref dispatchCalls, 0, 0);

        await vm.RemoveOldVersionsAsync();

        // Перестройка списка после удаления прошла через маршаллер (UI-поток),
        // и состав строк актуален: удалённая версия больше не «установлена».
        Assert.True(Interlocked.CompareExchange(ref dispatchCalls, 0, 0) > dispatchedAfterCheck,
            "RefreshInstalledAsync после удаления должен маршаллить перестройку Rows в UI-поток");
        Assert.False(vm.Rows.Single(r => r.Version == "8.3.27.1688").IsInstalled);
        Assert.True(vm.Rows.Single(r => r.Version == "8.3.27.2214").IsInstalled);
    }

    [Fact]
    public void AppendLog_WithDispatcher_MarshalsNotificationIntoUiThread()
    {
        // issue #334: журнал (PropertyChanged LogText) из фоновых продолжений не должен
        // обновлять UI-состояние напрямую — с маршаллером тело AppendLog уходит в
        // UI-поток (Dispatcher), как и перестройка Rows.
        var dispatchCalls = 0;
        var vm = CreateVm(dispatchToUi: action =>
        {
            Interlocked.Increment(ref dispatchCalls);
            action();
        });

        vm.AppendLog("строка журнала");

        Assert.Equal(1, dispatchCalls);
        Assert.Contains("строка журнала", vm.LogText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefreshInstalledAsync_FromBackgroundThread_RowsRebuiltViaDispatcher()
    {
        // RefreshInstalledAsync публичный и вызывается в том числе из фоновых
        // продолжений — перестройка Rows обязана идти через маршаллер (issue #334).
        var file = DistroFile(MiB);
        var service = OkService(Release("8.3.27.2214", file));
        service.PickedFile = file;
        var installed = new List<string> { "8.3.27.2214" };
        var dispatchCalls = 0;

        var vm = CreateVm(
            service,
            installed: () => installed.ToList(),
            dispatchToUi: action =>
            {
                Interlocked.Increment(ref dispatchCalls);
                action();
            });

        await vm.CheckUpdatesAsync();
        Assert.NotEmpty(vm.Rows);
        var dispatchedAfterCheck = Interlocked.CompareExchange(ref dispatchCalls, 0, 0);

        // Вызов из фонового потока (как продолжение ConfigureAwait(false)).
        await Task.Run(() => vm.RefreshInstalledAsync());

        Assert.True(Interlocked.CompareExchange(ref dispatchCalls, 0, 0) > dispatchedAfterCheck);
        Assert.Single(vm.Rows);
    }

    [Fact]
    public async Task RemoveOldVersions_AllVersionsRisky_FallbackSelectsNothing()
    {
        // issue #334: без диалога выбора (окружение без UI) по умолчанию предлагаются
        // только версии без признаков риска; если таких нет — операция не выполняется.
        // Сообщение «Нет версий для удаления» в этой ситуации НЕ показывается:
        // оно допустимо только когда платформа 1С вообще не установлена.
        var service = OkService(Release("8.3.27.2214"), Release("8.3.27.1688"), Release("8.3.26.1890"));
        var installedInfos = new List<PlatformVersionInfo>
        {
            new() { Display = "8.3.27.2214", Path = @"C:\Program Files\1cv8\8.3.27.2214" },
            new() { Display = "8.3.27.1688", Path = @"C:\Program Files\1cv8\8.3.27.1688" },
            new() { Display = "8.3.26.1890", Path = @"C:\Program Files\1cv8\8.3.26.1890" },
        };
        var deleted = new List<string>();

        var vm = CreateVm(
            service,
            bases: new List<Infobase> { Base("1", "8.3.27.1688") }, // база ссылается на 8.3.27.1688
            installedInfos: () => installedInfos.ToList(),
            runningBinPaths: () => new List<string>
            {
                @"C:\Program Files\1cv8\8.3.26.1890\bin\1cv8c.exe", // процесс запущен из 8.3.26.1890
            },
            deleteVersion: (version, log, ct) =>
            {
                deleted.Add(version.Display);
                return Task.FromResult((Success: true, ErrorKey: (string?)null));
            });

        await vm.RemoveOldVersionsAsync();

        // Новейшая + используемая базой + запущенная — версий без риска нет, ничего не удалено.
        Assert.Empty(deleted);
        Assert.Contains(LocalizationManager.T("PlatformUpdate.Error.Cancelled"), vm.LogText);
        Assert.DoesNotContain(LocalizationManager.T("PlatformUpdate.RemoveNothing"), vm.LogText);
    }

    [Fact]
    public async Task RemoveOldVersions_CancelledDialog_IsNoOp()
    {
        var installedInfos = new List<PlatformVersionInfo>
        {
            new() { Display = "8.3.27.2214" },
            new() { Display = "8.3.27.1688" },
        };
        var deleted = new List<string>();

        var vm = CreateVm(
            installedInfos: () => installedInfos.ToList(),
            deleteVersion: (version, log, ct) =>
            {
                deleted.Add(version.Display);
                return Task.FromResult((Success: true, ErrorKey: (string?)null));
            },
            confirm: (_, _) => false);

        await vm.RemoveOldVersionsAsync();

        Assert.Empty(deleted);
        Assert.False(vm.IsBusy);
        Assert.Contains(LocalizationManager.T("PlatformUpdate.Error.Cancelled"), vm.LogText);
    }

    [Fact]
    public async Task RemoveOldVersions_Linux_ShowsSudoCommandAndCopiesToClipboard()
    {
        var installedInfos = new List<PlatformVersionInfo>
        {
            new() { Display = "8.3.27.2214" },
            new() { Display = "8.3.27.1688" },
            new() { Display = "8.3.26.1890" },
        };
        var copied = new List<string>();

        // Без делегата удаления каталога — активна Linux-ветка (команда + буфер).
        var vm = CreateVm(
            installedInfos: () => installedInfos.ToList(),
            buildUninstall: version => $"sudo dpkg -r 1c-enterprise83-{version}",
            copyCommand: copied.Add,
            confirm: (_, _) => true);

        await vm.RemoveOldVersionsAsync();

        // Команды для всех кандидатов (новейшая исключена) скопированы в буфер.
        Assert.Equal(2, copied.Count);
        Assert.Contains(copied, c => c.Contains("1c-enterprise83-8.3.27.1688"));
        Assert.Contains(copied, c => c.Contains("1c-enterprise83-8.3.26.1890"));
        Assert.DoesNotContain(copied, c => c.Contains("1c-enterprise83-8.3.27.2214"));
        Assert.Contains(LocalizationManager.T("PlatformUpdate.Linux.Copied"), vm.LogText);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task RemoveOldVersions_NoCandidates_NotifiesInfo()
    {
        // issue #334: при отсутствии кандидатов пользователь должен УВИДЕТЬ результат —
        // информационное уведомление, а не только строку в журнале окна.
        var notified = new List<(string Title, string Message, NotificationKind Kind, NotificationEvent Evt)>();
        var vm = CreateVm(
            notify: (title, message, kind, evt) => notified.Add((title, message, kind, evt)));

        await vm.RemoveOldVersionsAsync();

        var notification = Assert.Single(notified);
        Assert.Equal(LocalizationManager.T("PlatformUpdate.RemoveNothing"), notification.Message);
        Assert.Equal(NotificationKind.Info, notification.Kind);
        Assert.Equal(NotificationEvent.Update, notification.Evt);
        Assert.Contains(LocalizationManager.T("PlatformUpdate.RemoveNothing"), vm.LogText);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task RemoveOldVersions_SelectionDialog_ShowsAllVersionsWithRiskFlags()
    {
        // issue #334 (комментарий автора): в диалоге ПОКАЗЫВАЮТСЯ ВСЕ установленные
        // версии с признаками (новейшая/используемые базами/процессами) — ничего
        // не фильтруется, решение за пользователем.
        var installedInfos = new List<PlatformVersionInfo>
        {
            new() { Display = "8.3.27.2214", Path = @"C:\1cv8\8.3.27.2214" },
            new() { Display = "8.3.27.1688", Path = @"C:\1cv8\8.3.27.1688" },
            new() { Display = "8.3.26.1890", Path = @"C:\1cv8\8.3.26.1890" },
        };
        var bases = new List<Infobase> { Base("1", "8.3.27.1688") };
        var runningBinPaths = new List<string> { @"C:\1cv8\8.3.26.1890\bin\1cv8c.exe" };
        IReadOnlyList<OldVersionCleanupEntry>? received = null;

        IReadOnlyList<PlatformVersionInfo>? Choose(IReadOnlyList<OldVersionCleanupEntry> entries)
        {
            received = entries;
            return entries.Select(e => e.Version).ToList();
        }

        var vm = CreateVm(
            service: OkService(),
            bases: bases,
            installedInfos: () => installedInfos.ToList(),
            runningBinPaths: () => runningBinPaths,
            chooseVersionsToDelete: Choose);

        await vm.RemoveOldVersionsAsync();

        Assert.NotNull(received);
        // Все 3 версии показаны, включая новейшую и используемые.
        Assert.Equal(3, received!.Count);
        var newest = Assert.Single(received, e => e.IsNewest);
        Assert.Equal("8.3.27.2214", newest.Version.Display);
        Assert.False(newest.IsCheckedByDefault); // чекбокс новейшей снят по умолчанию
        var usedByBase = Assert.Single(received, e => e.IsUsedByBases);
        Assert.Equal("8.3.27.1688", usedByBase.Version.Display);
        Assert.Equal(new[] { "База 1" }, usedByBase.ReferencingBaseNames.ToArray());
        Assert.False(usedByBase.IsCheckedByDefault);
        var usedByProcess = Assert.Single(received, e => e.IsUsedByProcesses);
        Assert.Equal("8.3.26.1890", usedByProcess.Version.Display);
        Assert.False(usedByProcess.IsCheckedByDefault);
    }

    [Fact]
    public async Task RemoveOldVersions_SelectionDialog_DeletesOnlyChosenVersions()
    {
        // issue #334: диалог со списком версий — пользователь выбирает подмножество,
        // удаляются только отмеченные версии.
        var installedInfos = new List<PlatformVersionInfo>
        {
            new() { Display = "8.3.27.2214", Path = @"C:\1cv8\8.3.27.2214" },
            new() { Display = "8.3.27.1688", Path = @"C:\1cv8\8.3.27.1688" },
            new() { Display = "8.3.26.1890", Path = @"C:\1cv8\8.3.26.1890" },
        };
        var deleted = new List<string>();
        var confirmCalls = 0;
        IReadOnlyList<PlatformVersionInfo>? Choose(IReadOnlyList<OldVersionCleanupEntry> entries)
            => entries.Where(e => !e.HasRiskMarkers).Select(e => e.Version).ToList();

        var vm = CreateVm(
            installedInfos: () => installedInfos.ToList(),
            deleteVersion: (version, log, ct) =>
            {
                deleted.Add(version.Display);
                installedInfos.RemoveAll(v => v.Display == version.Display);
                return Task.FromResult((Success: true, ErrorKey: (string?)null));
            },
            confirm: (_, _) =>
            {
                confirmCalls++;
                return true;
            },
            chooseVersionsToDelete: Choose);

        await vm.RemoveOldVersionsAsync();

        // Отмечены только версии без признаков риска (новейшая 8.3.27.2214 исключена
        // самим пользователем в диалоге); предупреждение не показывается.
        Assert.Equal(new[] { "8.3.27.1688", "8.3.26.1890" }, deleted);
        Assert.Equal(0, confirmCalls);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task RemoveOldVersions_RemoveNewestVersion_RequiresConfirmation()
    {
        // issue #334: при попытке удалить новейшую версию — предупреждение с
        // подтверждением; отмена подтверждения прерывает удаление.
        var installedInfos = new List<PlatformVersionInfo>
        {
            new() { Display = "8.3.27.2214", Path = @"C:\1cv8\8.3.27.2214" },
            new() { Display = "8.3.27.1688", Path = @"C:\1cv8\8.3.27.1688" },
        };
        var deleted = new List<string>();
        var confirmMessages = new List<string>();
        IReadOnlyList<PlatformVersionInfo>? Choose(IReadOnlyList<OldVersionCleanupEntry> entries)
            => entries.Where(e => e.IsNewest).Select(e => e.Version).ToList();

        var vm = CreateVm(
            installedInfos: () => installedInfos.ToList(),
            deleteVersion: (version, log, ct) =>
            {
                deleted.Add(version.Display);
                return Task.FromResult((Success: true, ErrorKey: (string?)null));
            },
            confirm: (_, message) =>
            {
                confirmMessages.Add(message);
                return false; // пользователь не подтвердил
            },
            chooseVersionsToDelete: Choose);

        await vm.RemoveOldVersionsAsync();

        Assert.Empty(deleted);
        // Показано одно предупреждение с подтверждением (шаблон сообщения о риске).
        var warning = Assert.Single(confirmMessages);
        Assert.Equal(LocalizationManager.T("PlatformUpdate.Confirm.RemoveRiskMessage"), warning);
        // Строка про новейшую версию — в журнале окна.
        Assert.Contains(LocalizationManager.T("PlatformUpdate.Confirm.RemoveRiskNewest"), vm.LogText);
        Assert.Contains(LocalizationManager.T("PlatformUpdate.Error.Cancelled"), vm.LogText);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task RemoveOldVersions_RemoveVersionUsedByBases_WarningContainsBaseNames()
    {
        // issue #334: при удалении версии, используемой базами, — подтверждение
        // со списком баз; после подтверждения версия удаляется.
        var installedInfos = new List<PlatformVersionInfo>
        {
            new() { Display = "8.3.27.2214", Path = @"C:\1cv8\8.3.27.2214" },
            new() { Display = "8.3.27.1688", Path = @"C:\1cv8\8.3.27.1688" },
        };
        var bases = new List<Infobase>
        {
            Base("1", "8.3.27.1688"),
            Base("2", "8.3.27.1688 (64)"),
        };
        var deleted = new List<string>();
        var logger = new FakeAppLogger();
        IReadOnlyList<PlatformVersionInfo>? Choose(IReadOnlyList<OldVersionCleanupEntry> entries)
            => entries.Where(e => e.IsUsedByBases).Select(e => e.Version).ToList();

        var vm = CreateVm(
            service: OkService(),
            bases: bases,
            installedInfos: () => installedInfos.ToList(),
            deleteVersion: (version, log, ct) =>
            {
                deleted.Add(version.Display);
                return Task.FromResult((Success: true, ErrorKey: (string?)null));
            },
            confirm: (_, _) => true,
            logger: logger,
            chooseVersionsToDelete: Choose);

        await vm.RemoveOldVersionsAsync();

        Assert.Equal(new[] { "8.3.27.1688" }, deleted);
        // Строка предупреждения о базах — в журнале окна.
        Assert.Contains(LocalizationManager.T("PlatformUpdate.Confirm.RemoveRiskBases"), vm.LogText);
        Assert.Contains(LocalizationManager.T("PlatformUpdate.Preflight.Continue"), vm.LogText);
        // Детали (имена баз) — в файловом журнале.
        Assert.Contains(logger.Warnings, m => m.Contains("8.3.27.1688") && m.Contains("База 1") && m.Contains("База 2"));
    }

    [Fact]
    public async Task RemoveOldVersions_SelectionDialogCancelled_NoDeletion()
    {
        var installedInfos = new List<PlatformVersionInfo>
        {
            new() { Display = "8.3.27.2214" },
            new() { Display = "8.3.27.1688" },
        };
        var deleted = new List<string>();

        var vm = CreateVm(
            installedInfos: () => installedInfos.ToList(),
            deleteVersion: (version, log, ct) =>
            {
                deleted.Add(version.Display);
                return Task.FromResult((Success: true, ErrorKey: (string?)null));
            },
            chooseVersionsToDelete: _ => null);

        await vm.RemoveOldVersionsAsync();

        Assert.Empty(deleted);
        Assert.False(vm.IsBusy);
        Assert.Contains(LocalizationManager.T("PlatformUpdate.Error.Cancelled"), vm.LogText);
    }

    [Fact]
    public async Task RemoveOldVersions_DeleteFailed_NotifiesError()
    {
        var installedInfos = new List<PlatformVersionInfo>
        {
            new() { Display = "8.3.27.2214" },
            new() { Display = "8.3.27.1688" },
        };
        var notified = new List<(string Title, string Message, NotificationKind Kind, NotificationEvent Evt)>();

        var vm = CreateVm(
            installedInfos: () => installedInfos.ToList(),
            deleteVersion: (version, log, ct) =>
                Task.FromResult((Success: false, ErrorKey: (string?)"PlatformUpdate.Error.DeleteFailed")),
            notify: (title, message, kind, evt) => notified.Add((title, message, kind, evt)));

        await vm.RemoveOldVersionsAsync();

        var notification = Assert.Single(notified);
        Assert.Equal(NotificationKind.Error, notification.Kind);
        Assert.Equal(NotificationEvent.Update, notification.Evt);
        Assert.Contains(LocalizationManager.T("PlatformUpdate.Error.DeleteFailed"), vm.LogText);
    }

    [Fact]
    public async Task SelectRow_WithCompatibleBases_WritesNamesToLog()
    {
        var service = OkService(Release("8.3.27.2214"), Release("8.3.27.1688"));
        var bases = new List<Infobase>
        {
            Base("1", "8.3.27.1644"),
            Base("2", "8.3.27.1606"),
            Base("3", "8.3.26.1182"),
        };

        var vm = CreateVm(service, bases, installed: () => new List<string> { "8.3.27.1688" });
        await vm.CheckUpdatesAsync();

        vm.SelectedRow = vm.Rows.Single(r => r.Version == "8.3.27.1688");

        Assert.Equal(2, vm.SelectedRow.CompatibleBases);
        Assert.Equal(new[] { "База 1", "База 2" }, vm.SelectedRow.CompatibleBaseNames);
        Assert.Contains("База 1", vm.LogText);
        Assert.Contains("База 2", vm.LogText);
    }

    // ---------- Диагностика скачивания (issue #334, комментарий 7OH) ----------

    [Fact]
    public async Task DownloadAndInstall_NoCompatibleDistribution_LogsReleaseFiles()
    {
        // issue #334: раньше при отсутствии вариантов дистрибутива для ОС выдавалось
        // вводящее в заблуждение «setup.exe не найден в архиве», а файловый журнал
        // оставался пустым. Теперь — точное сообщение с ПОЛНЫМ списком файлов релиза
        // в журнале окна И файловом журнале; скачивание и установка не выполняются.
        var otherFile = new PlatformReleaseFile
        {
            FileName = "readme.txt",
            Url = "https://releases.1c.ru/dist/readme.txt",
            Kind = PlatformDistributionKind.Other,
        };
        var service = OkService(Release("8.3.27.2214", otherFile));
        service.FilesResult = new PlatformCatalogResult
        {
            Status = PortalFetchStatus.Ok,
            Release = Release("8.3.27.2214", otherFile),
        };
        var logger = new FakeAppLogger();
        var downloadCalled = false;
        var installCalled = false;

        var vm = CreateVm(
            service,
            download: (_, _, _, _) =>
            {
                downloadCalled = true;
                return Task.FromResult<string?>(null);
            },
            install: (_, _, _, _, _) =>
            {
                installCalled = true;
                return Task.FromResult((Success: true, ErrorKey: (string?)null, ExitCode: 0));
            },
            logger: logger);
        await vm.CheckUpdatesAsync();
        vm.SelectedRow = vm.Rows.Single(r => r.Version == "8.3.27.2214");

        await vm.DownloadAndInstallAsync();

        // Список файлов релиза и точное объяснение — в журнале окна (в тестовой среде
        // LocalizationManager.T возвращает ключ — он и попадает в журнал).
        Assert.Contains(LocalizationManager.T("PlatformUpdate.Diag.DistributionFiles"), vm.LogText);
        Assert.Contains(LocalizationManager.T("PlatformUpdate.Diag.NoDistributions"), vm.LogText);
        // И в файловом журнале с фактическими данными («в логах пусто» больше невозможно).
        Assert.Contains(logger.Infos, m => m.Contains("readme.txt"));
        Assert.Contains(logger.Warnings, m => m.Contains("нет дистрибутива для текущей ОС")
            && m.Contains("readme.txt"));
        Assert.False(downloadCalled);
        Assert.False(installCalled);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task DownloadOnly_LogsUrlSavePathAndSize_DoesNotInstall()
    {
        // issue #334: каждый шаг «Только скачать» журналируется (ссылка, путь,
        // размер); установка после скачивания не запускается.
        var file = DistroFile(200 * MiB);
        var service = OkService(Release("8.3.27.2214", file));
        service.FilesResult = new PlatformCatalogResult
        {
            Status = PortalFetchStatus.Ok,
            Release = Release("8.3.27.2214", file),
        };
        var installCalls = 0;

        var logger = new FakeAppLogger();
        var vm = CreateVm(
            service,
            download: (url, target, progress, ct) => Task.FromResult<string?>(target),
            install: (_, _, _, _, _) =>
            {
                installCalls++;
                return Task.FromResult((Success: true, ErrorKey: (string?)null, ExitCode: 0));
            },
            saveDialog: _ => @"C:\out\8.3.27.2214_x64.zip",
            logger: logger);
        await vm.CheckUpdatesAsync();
        vm.SelectedRow = vm.Rows.Single(r => r.Version == "8.3.27.2214");

        await vm.DownloadOnlyAsync();

        // Ссылка, путь сохранения и размер — в журнале окна (ключи диагностических строк).
        Assert.Contains(LocalizationManager.T("PlatformUpdate.Diag.DownloadUrl"), vm.LogText);
        Assert.Contains(LocalizationManager.T("PlatformUpdate.Diag.SavePath"), vm.LogText);
        Assert.Contains(LocalizationManager.T("PlatformUpdate.Diag.DownloadedSize"), vm.LogText);
        // И в файловом журнале — с фактическими данными.
        Assert.Contains(logger.Infos, m => m.Contains(file.Url));
        Assert.Contains(logger.Infos, m => m.Contains(@"C:\out\8.3.27.2214_x64.zip"));
        // Установка не запускается.
        Assert.Equal(0, installCalls);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task DownloadAndInstall_LogsUrlAndSize()
    {
        // issue #334: «Скачать и установить» тоже журналирует ссылку и размер файла.
        var file = DistroFile(200 * MiB);
        var service = OkService(Release("8.3.27.2214", file));
        service.FilesResult = new PlatformCatalogResult
        {
            Status = PortalFetchStatus.Ok,
            Release = Release("8.3.27.2214", file),
        };

        var logger = new FakeAppLogger();
        var vm = CreateVm(
            service,
            download: (url, target, progress, ct) => Task.FromResult<string?>(target),
            logger: logger);
        await vm.CheckUpdatesAsync();
        vm.SelectedRow = vm.Rows.Single(r => r.Version == "8.3.27.2214");

        await vm.DownloadAndInstallAsync();

        Assert.Contains(LocalizationManager.T("PlatformUpdate.Diag.DownloadUrl"), vm.LogText);
        Assert.Contains(LocalizationManager.T("PlatformUpdate.Diag.DownloadedSize"), vm.LogText);
        // И в файловом журнале — с фактической ссылкой.
        Assert.Contains(logger.Infos, m => m.Contains(file.Url));
    }

    // ---------- Хоткей окна (этап 0.3.9.214) ----------

    [Fact]
    public void AppSettings_HotkeyPlatformUpdate_DefaultIsCtrlF9()
    {
        var settings = new AppSettings();

        Assert.Equal("Ctrl+F9", settings.HotkeyPlatformUpdate);
    }

    [Fact]
    public void AppSettings_NormalizeForLoad_PreservesHotkeyPlatformUpdate()
    {
        var settings = new AppSettings { HotkeyPlatformUpdate = "Ctrl+Shift+F10" };

        settings.NormalizeForLoad();

        Assert.Equal("Ctrl+Shift+F10", settings.HotkeyPlatformUpdate);
    }

    // ---------- Fake-сервисы ----------

    // ============== Фоновая регистрация загрузок (issue #334 п.1, 0.3.12.2) ==============

    [Fact]
    public async Task DownloadOnlyAsync_WithBackgroundManager_RegistersStartsAndCompletes()
    {
        var file = DistroFile(200 * MiB);
        var service = OkService(Release("8.3.27.2214", file));
        var manager = new BackgroundDownloadManager();
        CancellationToken? seenCt = null;
        var vm = CreateVm(
            service,
            download: (url, target, progress, ct) =>
            {
                seenCt = ct;
                progress?.Report(0.5);
                return Task.Delay(120).ContinueWith<string?>(_ => target);
            },
            saveDialog: _ => Path.Combine(Path.GetTempPath(), "cm_bg_test.zip"),
            backgroundDownloads: manager);

        await vm.CheckUpdatesAsync();
        vm.SelectedRow = vm.Rows.Single(r => r.Version == "8.3.27.2214");
        await vm.DownloadOnlyAsync();

        var entry = Assert.Single(manager.Snapshot());
        Assert.Equal($"platform-update:8.3.27.2214:{file.FileName}", entry.Id);
        Assert.Equal(BackgroundDownloadState.Completed, entry.State);
        // Реальный токен отмены вместо CancellationToken.None (issue #334 п.1).
        Assert.NotNull(seenCt);
        Assert.True(seenCt!.Value.CanBeCanceled);
    }

    [Fact]
    public async Task DownloadOnlyAsync_CancelledThroughManager_FailsWithCancelledKey()
    {
        var file = DistroFile(200 * MiB);
        var service = OkService(Release("8.3.27.2214", file));
        var manager = new BackgroundDownloadManager();
        var vm = CreateVm(
            service,
            download: (url, target, progress, ct) =>
            {
                // Имитация отмены из индикатора главного окна: запись в менеджере
                // отменяется, загрузка возвращает null.
                var entry = manager.Snapshot().FirstOrDefault(d => d.IsActive);
                Assert.NotNull(entry);
                manager.Cancel(entry!.Id);
                return Task.FromResult<string?>(null);
            },
            saveDialog: _ => Path.Combine(Path.GetTempPath(), "cm_bg_cancel_test.zip"),
            backgroundDownloads: manager);

        await vm.CheckUpdatesAsync();
        vm.SelectedRow = vm.Rows.Single(r => r.Version == "8.3.27.2214");
        await vm.DownloadOnlyAsync();

        var entry = Assert.Single(manager.Snapshot());
        Assert.Equal(BackgroundDownloadState.Failed, entry.State);
        Assert.Equal("Main.Downloads.Cancelled", entry.ErrorKey);
    }

    [Fact]
    public async Task DownloadOnlyAsync_WithoutManager_ReceivesCancellationTokenNone()
    {
        // Регрессия: без менеджера — прежнее поведение (CancellationToken.None).
        var file = DistroFile(200 * MiB);
        var service = OkService(Release("8.3.27.2214", file));
        CancellationToken? seenCt = null;
        var vm = CreateVm(
            service,
            download: (url, target, progress, ct) =>
            {
                seenCt = ct;
                return Task.FromResult<string?>(target);
            },
            saveDialog: _ => Path.Combine(Path.GetTempPath(), "cm_bg_none_test.zip"));

        await vm.CheckUpdatesAsync();
        vm.SelectedRow = vm.Rows.Single(r => r.Version == "8.3.27.2214");
        await vm.DownloadOnlyAsync();

        Assert.Equal(CancellationToken.None, seenCt);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task DownloadAndInstallAsync_WithBackgroundManager_CompletesEntryAfterDownload()
    {
        var file = DistroFile(200 * MiB);
        var service = OkService(Release("8.3.27.2214", file));
        var manager = new BackgroundDownloadManager();
        var notified = new List<(string Title, string Message, NotificationKind Kind, NotificationEvent Evt)>();
        var vm = CreateVm(
            service,
            install: (zip, version, dir, log, ct) =>
                Task.FromResult((Success: true, ErrorKey: (string?)null, ExitCode: 0)),
            notify: (title, message, kind, evt) => notified.Add((title, message, kind, evt)),
            backgroundDownloads: manager);

        await vm.CheckUpdatesAsync();
        vm.SelectedRow = vm.Rows.Single(r => r.Version == "8.3.27.2214");
        await vm.DownloadAndInstallAsync();

        var entry = Assert.Single(manager.Snapshot());
        Assert.Equal($"platform-update:8.3.27.2214:{file.FileName}", entry.Id);
        Assert.Equal(BackgroundDownloadState.Completed, entry.State);
    }

    // ============== Отмена выбора дистрибутива (issue #334 п.2, 0.3.12.2) ==============

    [Fact]
    public async Task DownloadOnlyAsync_DistributionChoiceCancelled_NoSaveDialogNoDownload()
    {
        // Отмена в диалоге выбора дистрибутива прерывает операцию: диалог «куда скачать»
        // НЕ показывается, скачивание не начинается, в журнале — сообщение об отмене.
        var x64 = DistroFile(200 * MiB, "8.3.27.2214_x64.zip");
        var x86 = new PlatformReleaseFile
        {
            FileName = "8.3.27.2214_x86.zip",
            Url = "https://releases.1c.ru/dist/8.3.27.2214_x86.zip",
            SizeBytes = 180 * MiB,
            Architecture = "x86",
            Kind = PlatformDistributionKind.WindowsSetupZip,
        };
        var service = OkService(Release("8.3.27.2214", x64, x86));
        var saveDialogCalls = 0;
        var downloadCalls = 0;
        var vm = CreateVm(
            service,
            download: (url, target, progress, ct) =>
            {
                Interlocked.Increment(ref downloadCalls);
                return Task.FromResult<string?>(target);
            },
            saveDialog: _ =>
            {
                Interlocked.Increment(ref saveDialogCalls);
                return null;
            },
            chooseDistribution: _ => null);

        await vm.CheckUpdatesAsync();
        vm.SelectedRow = vm.Rows.Single(r => r.Version == "8.3.27.2214");
        await vm.DownloadOnlyAsync();

        Assert.Equal(0, saveDialogCalls);
        Assert.Equal(0, downloadCalls);
        Assert.False(vm.IsBusy);
        Assert.Contains(LocalizationManager.T("Updates.Distribution.Cancelled"), vm.LogText);
    }

    [Fact]
    public async Task DownloadAndInstallAsync_DistributionChoiceCancelled_NoDownloadNoInstall()
    {
        var x64 = DistroFile(200 * MiB, "8.3.27.2214_x64.zip");
        var x86 = new PlatformReleaseFile
        {
            FileName = "8.3.27.2214_x86.zip",
            Url = "https://releases.1c.ru/dist/8.3.27.2214_x86.zip",
            SizeBytes = 180 * MiB,
            Architecture = "x86",
            Kind = PlatformDistributionKind.WindowsSetupZip,
        };
        var service = OkService(Release("8.3.27.2214", x64, x86));
        var installCalls = 0;
        var vm = CreateVm(
            service,
            install: (zip, version, dir, log, ct) =>
            {
                Interlocked.Increment(ref installCalls);
                return Task.FromResult((Success: true, ErrorKey: (string?)null, ExitCode: 0));
            },
            chooseDistribution: _ => null);

        await vm.CheckUpdatesAsync();
        vm.SelectedRow = vm.Rows.Single(r => r.Version == "8.3.27.2214");
        await vm.DownloadAndInstallAsync();

        Assert.Equal(0, installCalls);
        Assert.False(vm.IsBusy);
        Assert.Contains(LocalizationManager.T("Updates.Distribution.Cancelled"), vm.LogText);
    }

    [Fact]
    public async Task DownloadOnlyAsync_SingleOption_StillDownloadsImmediately()
    {
        // Регрессия: единственный вариант дистрибутива качается без вопросов.
        var file = DistroFile(200 * MiB);
        var service = OkService(Release("8.3.27.2214", file));
        var saveDialogCalls = 0;
        var vm = CreateVm(
            service,
            saveDialog: _ =>
            {
                Interlocked.Increment(ref saveDialogCalls);
                return Path.Combine(Path.GetTempPath(), "cm_dist_test.zip");
            },
            chooseDistribution: _ => throw new InvalidOperationException("диалог не должен показываться"));

        await vm.CheckUpdatesAsync();
        vm.SelectedRow = vm.Rows.Single(r => r.Version == "8.3.27.2214");
        await vm.DownloadOnlyAsync();

        Assert.Equal(1, saveDialogCalls);
        Assert.False(vm.IsBusy);
    }

    /// <summary>Fake каталога версий: заранее заданные результаты и запоминание вызовов.</summary>
    private sealed class FakePlatformUpdateService : IPlatformUpdateService
    {
        public PlatformCatalogResult AvailableResult { get; set; } = new() { Status = PortalFetchStatus.Ok };
        public PlatformCatalogResult FilesResult { get; set; } = new() { Status = PortalFetchStatus.Ok };
        public PlatformReleaseFile? PickedFile { get; set; }
        public int LoadFilesCalls { get; private set; }

        public Task<PlatformCatalogResult> GetAvailableReleasesAsync(CancellationToken ct = default)
            => Task.FromResult(AvailableResult);

        public Task<PlatformCatalogResult> GetAllAvailableReleasesAsync(CancellationToken ct = default)
            => Task.FromResult(AvailableResult);

        public Task<PlatformCatalogResult> GetAvailableReleasesForNickAsync(string nick, CancellationToken ct = default)
            => Task.FromResult(AvailableResult);

        public Task<PlatformCatalogResult> LoadReleaseFilesAsync(PlatformRelease release, CancellationToken ct = default)
        {
            LoadFilesCalls++;
            if (FilesResult.Release?.Files is { Count: > 0 } files)
            {
                release.Files.Clear();
                release.Files.AddRange(files);
            }

            return Task.FromResult(FilesResult);
        }

        public Task<PlatformCatalogResult> LoadReleaseFilesForNickAsync(PlatformRelease release, string nick, CancellationToken ct = default)
            => LoadReleaseFilesAsync(release, ct);

        public PlatformReleaseFile? PickDistribution(IReadOnlyList<PlatformReleaseFile> files)
            => PickedFile;
    }

    /// <summary>Fake с приостановленным ответом каталога (для проверки IsBusy в процессе).</summary>
    private sealed class GatedPlatformUpdateService : IPlatformUpdateService
    {
        private readonly Task<PlatformCatalogResult> _result;

        public GatedPlatformUpdateService(Task<PlatformCatalogResult> result) => _result = result;

        public Task<PlatformCatalogResult> GetAvailableReleasesAsync(CancellationToken ct = default) => _result;

        public Task<PlatformCatalogResult> GetAllAvailableReleasesAsync(CancellationToken ct = default) => _result;

        public Task<PlatformCatalogResult> GetAvailableReleasesForNickAsync(string nick, CancellationToken ct = default)
            => _result;

        public Task<PlatformCatalogResult> LoadReleaseFilesAsync(PlatformRelease release, CancellationToken ct = default)
            => Task.FromResult(new PlatformCatalogResult { Status = PortalFetchStatus.Ok, Release = release });

        public Task<PlatformCatalogResult> LoadReleaseFilesForNickAsync(PlatformRelease release, string nick, CancellationToken ct = default)
            => Task.FromResult(new PlatformCatalogResult { Status = PortalFetchStatus.Ok, Release = release });

        public PlatformReleaseFile? PickDistribution(IReadOnlyList<PlatformReleaseFile> files)
            => files.FirstOrDefault();
    }

    /// <summary>Fake, который бросает исключение при получении каталога.</summary>
    private sealed class ThrowingPlatformUpdateService : IPlatformUpdateService
    {
        public Task<PlatformCatalogResult> GetAvailableReleasesAsync(CancellationToken ct = default)
            => throw new InvalidOperationException("Сбой сети (тест)");

        public Task<PlatformCatalogResult> GetAllAvailableReleasesAsync(CancellationToken ct = default)
            => throw new InvalidOperationException("Сбой сети (тест)");

        public Task<PlatformCatalogResult> GetAvailableReleasesForNickAsync(string nick, CancellationToken ct = default)
            => throw new InvalidOperationException("Сбой сети (тест)");

        public Task<PlatformCatalogResult> LoadReleaseFilesAsync(PlatformRelease release, CancellationToken ct = default)
            => Task.FromResult(new PlatformCatalogResult { Status = PortalFetchStatus.Ok, Release = release });

        public Task<PlatformCatalogResult> LoadReleaseFilesForNickAsync(PlatformRelease release, string nick, CancellationToken ct = default)
            => Task.FromResult(new PlatformCatalogResult { Status = PortalFetchStatus.Ok, Release = release });

        public PlatformReleaseFile? PickDistribution(IReadOnlyList<PlatformReleaseFile> files)
            => files.FirstOrDefault();
    }

    /// <summary>Fake репозитория: базы в памяти, остальное — no-op (образец PlatformDownloadTests).</summary>
    private sealed class FakeRepository : IInfobaseRepository
    {
        private readonly List<Infobase> _bases;

        public FakeRepository(List<Infobase> bases) => _bases = bases;

        public List<Infobase> Load() => _bases;

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

        public AppSettings LoadSettings() => new();

        public void SaveSettings(AppSettings settings)
        {
        }

        public Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    // ---------- UI-маршалинг обновления коллекции (issue #334) ----------

    [Fact]
    public async Task CheckUpdatesAsync_SuccessRunsViaDispatcher()
    {
        // issue #334: успешный каталог заполняет ObservableCollection Rows строго через
        // маршаллер UI-потока (WPF CollectionView бросает NotSupportedException при
        // изменении SourceCollection из фонового потока). В тесте маршаллер — синхронный
        // накопитель: действие должно быть передано ему и выполнено. Журнал (AppendLog,
        // issue #334 0.3.10.3) тоже уходит через маршаллер — итоговый минимум: блок
        // перестройки Rows («Готово») плюс записи журнала.
        var service = OkService(Release("8.3.27.2214"));
        var dispatched = new List<Action>();
        Action<Action> dispatcher = action =>
        {
            dispatched.Add(action);
            action();
        };
        var vm = CreateVm(service, dispatchToUi: dispatcher);

        await vm.CheckUpdatesAsync();

        // Перестройка Rows и журнал — всё через маршаллер (не менее 2 вызовов:
        // AppendLog «Проверка…» до запроса и блок «Готово» с RebuildRows после).
        Assert.True(dispatched.Count >= 2, $"Ожидался маршалинг Rows и журнала, получено {dispatched.Count}");
        Assert.Single(vm.Rows);           // действие реально выполнилось (Rows заполнены)
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task CheckUpdatesAsync_ErrorDoesNotTouchRows()
    {
        // Ошибка каталога (AuthRequired): коллекция Rows НЕ меняется; маршалинг
        // используется только журналом (AppendLog, issue #334 0.3.10.3) — блок
        // перестройки Rows не вызывается, поэтому строк в списке нет.
        var service = new FakePlatformUpdateService
        {
            AvailableResult = new PlatformCatalogResult { Status = PortalFetchStatus.AuthRequired },
        };
        var dispatched = new List<Action>();
        Action<Action> dispatcher = action =>
        {
            dispatched.Add(action);
            action();
        };
        var vm = CreateVm(service, dispatchToUi: dispatcher);

        await vm.CheckUpdatesAsync();

        // Журнал маршаллится (AppendLog «Проверка…» и сообщение об ошибке), но
        // перестройки Rows нет — список пуст.
        Assert.NotEmpty(dispatched);
        Assert.Empty(vm.Rows);
    }

    /// <summary>Fake логгера приложения: собирает сообщения в списки для проверки этапов.</summary>
    private sealed class FakeAppLogger : IAppLogger
    {
        public List<string> Infos { get; } = new();
        public List<string> Warnings { get; } = new();
        public List<string> Errors { get; } = new();

        public string WarningsJoined => string.Join(" | ", Warnings);

        public void Info(string message) => Infos.Add(message);
        public void Warn(string message) => Warnings.Add(message);
        public void Error(string message, Exception? exception = null) => Errors.Add(message);
    }
}