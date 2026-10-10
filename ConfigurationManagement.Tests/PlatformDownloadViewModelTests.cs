using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Configuration_Management.Localization;
using Configuration_Management.Models;
using Configuration_Management.Services;
using Configuration_Management.ViewModels;
using Xunit;

namespace ConfigurationManagement.Tests;

/// <summary>
/// Тесты ViewModel окна «Скачивание версии платформы 1С» (issue #330):
/// получение каталога версий, выбор файла под разрядность/тип, учётная запись ИТС
/// (выбранная/основная), скачивание в целевую папку с прогрессом и ИТОГ БЕЗ
/// автоматической установки (установщик вызывается только явной командой).
/// fake-сервисы без сети и UI.
/// </summary>
public sealed class PlatformDownloadViewModelTests
{
    private static PlatformRelease Release(string version, params PlatformReleaseFile[] files)
    {
        var release = new PlatformRelease { Version = version };
        release.Files.AddRange(files);
        return release;
    }

    private static PlatformReleaseFile File(string name, string? arch, PlatformDistributionKind kind)
        => new()
        {
            FileName = name,
            Url = $"https://releases.1c.ru/dist/{name}",
            Architecture = arch,
            Kind = kind,
            SizeBytes = 100,
        };

    private static PlatformDownloadViewModel CreateVm(
        IPlatformUpdateService? service = null,
        ItsAccount? account = null,
        Func<string, string, IProgress<double>?, CancellationToken, Task<string?>>? download = null,
        string? directory = null,
        bool is64Bit = true,
        bool isWindows = true,
        Action<Action>? dispatchToUi = null,
        IAppLogger? appLogger = null,
        BackgroundDownloadManager? backgroundDownloads = null)
    {
        var dir = directory ?? Path.Combine(Path.GetTempPath(), "cm_platformdl_" + Guid.NewGuid().ToString("N"));
        return new PlatformDownloadViewModel(
            service ?? new FakeCatalogService(),
            () => account,
            download ?? ((url, target, progress, ct) => Task.FromResult<string?>(target)),
            _ => true,
            is64Bit: is64Bit,
            defaultDirectory: dir,
            isWindows: isWindows,
            appLogger: appLogger,
            dispatchToUi: dispatchToUi,
            backgroundDownloads: backgroundDownloads);
    }

    // ---------- Каталог и выбор файла ----------

    [Fact]
    public async Task LoadCatalogAsync_FillsReleases_AndPicksFirstFile()
    {
        var fileX64 = File("8.3.27.2214_x64.zip", "x64", PlatformDistributionKind.WindowsSetupZip);
        var service = new FakeCatalogService
        {
            AvailableResult = new PlatformCatalogResult
            {
                Status = PortalFetchStatus.Ok,
                Releases = new[]
                {
                    Release("8.3.27.2214", fileX64, File("8.3.27.2214_x86.zip", "x86", PlatformDistributionKind.WindowsSetupZip)),
                    Release("8.3.27.1688"),
                },
            },
        };
        var vm = CreateVm(service);

        await vm.LoadCatalogAsync();

        Assert.Equal(2, vm.Releases.Count);
        Assert.Equal("8.3.27.2214", vm.SelectedRelease!.Version);
        Assert.NotNull(vm.PickedFile);
        Assert.Equal("8.3.27.2214_x64.zip", vm.PickedFile!.FileName);
        Assert.Contains("8.3.27.2214", vm.PickedFile.FileName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadCatalogAsync_FillsReleasesViaDispatcher()
    {
        // issue #330: каталог заполняет ObservableCollection Releases строго через маршаллер
        // UI-потока (WPF CollectionView бросает NotSupportedException при изменении
        // SourceCollection из фонового потока). Маршаллер в тесте — синхронный накопитель.
        var service = new FakeCatalogService
        {
            AvailableResult = new PlatformCatalogResult
            {
                Status = PortalFetchStatus.Ok,
                Releases = new[] { Release("8.3.27.2214"), Release("8.3.27.1688") },
            },
        };
        var dispatched = new List<Action>();
        Action<Action> dispatcher = action =>
        {
            dispatched.Add(action);
            action();
        };
        var vm = CreateVm(service, dispatchToUi: dispatcher);

        await vm.LoadCatalogAsync();

        // Маршалинг применён: заполнение каталога (включая дерево версий) идёт строго
        // через маршаллер. Подгрузка файлов выбранной версии в этом тесте НЕ диспетчеризует
        // RepickFile: у релизов fake-сервиса файлов нет, и с issue #330 (комментарий 7OH)
        // пустой список файлов прерывает обработку с понятным сообщением в журнале.
        Assert.Single(dispatched);
        Assert.Equal(2, vm.Releases.Count);        // действие выполнилось (список заполнен)
        Assert.Equal("8.3.27.2214", vm.SelectedRelease!.Version);
    }

    [Fact]
    public async Task LoadCatalogAsync_ErrorDoesNotDispatch()
    {
        // Ошибка каталога не трогает коллекцию и не вызывает маршалинг.
        var service = new FakeCatalogService
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

        await vm.LoadCatalogAsync();

        Assert.Empty(dispatched);
        Assert.Empty(vm.Releases);
    }

    [Fact]
    public async Task LoadCatalogAsync_AfterCasLogin_PopulatesReleases()
    {
        // #330/#334: полная цепочка окна «Скачивание версии платформы» — каталог
        // Platform83 через реальный PlatformUpdateService → OneCUpdatesService.GetPageTextAsync
        // с fake-CAS: первый запрос каталога → 302 на login.1c.ru?service=…, вход кредами
        // из справочника ИТС (issue #333), POST → 302 на security_check?ticket=… → GET
        // security_check (сессионная cookie) → повтор каталога → HTML с версиями.
        var dir = Path.Combine(Path.GetTempPath(), "cm_platformdl_login_" + Guid.NewGuid().ToString("N"));
        var repo = new MemRepo();
        var accounts = new ItsAccountsStore(repository: repo, profileService: null, directoryOverride: dir);
        accounts.Upsert(new ItsAccount { Name = "Основная", Login = "store-user", Password = "store-pwd" });

        var handler = new CasLoginHandler();
        var updates = new OneCUpdatesService(repo, new NoOpLogger(), handler, accounts);
        var service = new PlatformUpdateService(updates, new NoOpLogger());
        var vm = CreateVm(service, account: new ItsAccount { Name = "Основная", Login = "store-user", Password = "store-pwd" });

        await vm.LoadCatalogAsync();

        Assert.True(
            vm.Releases.Count > 0,
            $"Releases={vm.Releases.Count}, requests=[{string.Join(" | ", handler.Log)}]");
        Assert.Equal("8.3.27.2214", vm.Releases[0].Version);
    }

    [Fact]
    public async Task SwitchTo32Bit_PicksX86File()
    {
        var fileX64 = File("8.3.27.2214_x64.zip", "x64", PlatformDistributionKind.WindowsSetupZip);
        var fileX86 = File("8.3.27.2214_x86.zip", "x86", PlatformDistributionKind.WindowsSetupZip);
        var service = new FakeCatalogService
        {
            AvailableResult = new PlatformCatalogResult
            {
                Status = PortalFetchStatus.Ok,
                Releases = new[] { Release("8.3.27.2214", fileX64, fileX86) },
            },
        };
        var vm = CreateVm(service, is64Bit: true);

        await vm.LoadCatalogAsync();
        Assert.NotNull(vm.PickedFile);
        Assert.Equal("8.3.27.2214_x64.zip", vm.PickedFile!.FileName);

        vm.Is64Bit = false;
        Assert.Equal("8.3.27.2214_x86.zip", vm.PickedFile!.FileName);
    }

    // ---------- Issue #330 п.1: фильтр разрядности в списке файлов ----------

    [Fact]
    public async Task SwitchTo32Bit_DistributionOptionsContainOnlyX86AndNoArchFiles()
    {
        var fileX64 = File("8.3.27.2214_x64.zip", "x64", PlatformDistributionKind.WindowsSetupZip);
        var fileX86 = File("8.3.27.2214_x86.zip", "x86", PlatformDistributionKind.WindowsSetupZip);
        var fileTar = File("8.3.27.2214.tar.gz", null, PlatformDistributionKind.LinuxTarGz);
        var service = new FakeCatalogService
        {
            AvailableResult = new PlatformCatalogResult
            {
                Status = PortalFetchStatus.Ok,
                Releases = new[] { Release("8.3.27.2214", fileX64, fileX86, fileTar) },
            },
        };
        var vm = CreateVm(service, is64Bit: true);

        await vm.LoadCatalogAsync();

        // x64: в вариантах только 64-битный клиент (файлы без разрядности — не для Windows).
        Assert.All(vm.DistributionOptions,
            o => Assert.False(string.Equals(o.Architecture, "x86", StringComparison.OrdinalIgnoreCase),
                "32-битный файл не должен попадать в список при x64"));
        Assert.Contains(vm.DistributionOptions, o => o.File.FileName == "8.3.27.2214_x64.zip");
        Assert.DoesNotContain(vm.DistributionOptions, o => o.File.FileName == "8.3.27.2214_x86.zip");

        vm.Is64Bit = false;

        // x86: в вариантах только 32-битный клиент; 64-битный исчез.
        Assert.Contains(vm.DistributionOptions, o => o.File.FileName == "8.3.27.2214_x86.zip");
        Assert.DoesNotContain(vm.DistributionOptions, o => o.File.FileName == "8.3.27.2214_x64.zip");
        Assert.Equal("8.3.27.2214_x86.zip", vm.PickedFile!.FileName);
    }

    [Fact]
    public async Task SwitchTo32Bit_SelectedDistributionOfOtherArch_IsReset()
    {
        var fileX64 = File("8.3.27.2214_x64.zip", "x64", PlatformDistributionKind.WindowsSetupZip);
        var fileX86 = File("8.3.27.2214_x86.zip", "x86", PlatformDistributionKind.WindowsSetupZip);
        var service = new FakeCatalogService
        {
            AvailableResult = new PlatformCatalogResult
            {
                Status = PortalFetchStatus.Ok,
                Releases = new[] { Release("8.3.27.2214", fileX64, fileX86) },
            },
        };
        var vm = CreateVm(service, is64Bit: true);

        await vm.LoadCatalogAsync();
        Assert.NotNull(vm.SelectedDistribution);
        Assert.Equal("8.3.27.2214_x64.zip", vm.SelectedDistribution!.File.FileName);

        vm.Is64Bit = false;

        // Невалидный выбор сброшен: вариант x64 больше не проходит фильтр разрядности.
        Assert.NotNull(vm.SelectedDistribution);
        Assert.Equal("8.3.27.2214_x86.zip", vm.SelectedDistribution!.File.FileName);
        Assert.Contains(vm.SelectedDistribution, vm.DistributionOptions);
    }

    // ---------- Issue #330 п.2/#334 п.2: группы файлов релиза ----------

    [Fact]
    public void FileGroups_Build_GroupsByPageOrder_AndFallsBackToDefaultGroup()
    {
        var option = (string name, string? group) =>
        {
            var file = File(name, "x64", PlatformDistributionKind.WindowsSetupZip);
            file.Group = group;
            return new PlatformDistributionOption(file);
        };

        var options = new List<PlatformDistributionOption>
        {
            option("8.3.27.2214_x64.zip", "Технологическая платформа"),
            option("8.3.27.2214_thin_x64.zip", "Тонкий клиент"),
            option("8.3.27.2214_x86.zip", "Технологическая платформа"), // порядок страницы сохраняется
            option("legacy_8.3.20.zip", null),
        };

        var groups = PlatformFileGroupViewModel.Build(options, "Файлы релиза");

        Assert.Equal(3, groups.Count);
        Assert.Equal("Технологическая платформа", groups[0].Title);
        Assert.Equal(new[] { "8.3.27.2214_x64.zip", "8.3.27.2214_x86.zip" },
            groups[0].Files.Select(f => f.File.FileName).ToArray());
        Assert.Equal("Тонкий клиент", groups[1].Title);
        Assert.Single(groups[1].Files);
        Assert.Equal("Файлы релиза", groups[2].Title);
        Assert.Equal("legacy_8.3.20.zip", groups[2].Files[0].File.FileName);
    }

    [Fact]
    public async Task LoadCatalogAsync_FilesWithoutGroups_SingleFallbackGroup_NoHeaderItems()
    {
        // Старые релизы без групп: одна фолбэк-группа, в плоском списке заголовков нет.
        var fileX64 = File("8.3.27.2214_x64.zip", "x64", PlatformDistributionKind.WindowsSetupZip);
        var service = new FakeCatalogService
        {
            AvailableResult = new PlatformCatalogResult
            {
                Status = PortalFetchStatus.Ok,
                Releases = new[] { Release("8.3.27.2214", fileX64) },
            },
        };
        var vm = CreateVm(service);

        await vm.LoadCatalogAsync();

        var group = Assert.Single(vm.DistributionFileGroups);
        Assert.Single(group.Files);
        Assert.DoesNotContain(vm.DistributionOptionsGrouped, i => i is PlatformFileGroupHeaderItem);
        Assert.Contains(vm.PickedFile!, vm.DistributionOptionsGrouped.OfType<PlatformDistributionOption>().Select(o => o.File));
    }

    [Fact]
    public async Task LoadCatalogAsync_GroupedFiles_FlatListHasHeadersThenOptions()
    {
        var grouped = new PlatformReleaseFile
        {
            FileName = "8.3.27.2214_thin_x64.zip",
            Url = "https://releases.1c.ru/dist/8.3.27.2214_thin_x64.zip",
            Architecture = "x64",
            Kind = PlatformDistributionKind.WindowsSetupZip,
            Group = "Тонкий клиент",
        };
        var plain = File("8.3.27.2214_x64.zip", "x64", PlatformDistributionKind.WindowsSetupZip);
        var service = new FakeCatalogService
        {
            AvailableResult = new PlatformCatalogResult
            {
                Status = PortalFetchStatus.Ok,
                Releases = new[] { Release("8.3.27.2214", plain, grouped) },
            },
        };
        var vm = CreateVm(service);

        await vm.LoadCatalogAsync();

        // Две группы → в плоском списке два заголовка-разделителя.
        Assert.Equal(2, vm.DistributionFileGroups.Count);
        Assert.Equal(2, vm.DistributionOptionsGrouped.OfType<PlatformFileGroupHeaderItem>().Count());
        Assert.Equal(2, vm.DistributionOptionsGrouped.OfType<PlatformDistributionOption>().Count());

        // Порядок: заголовок первой группы → её вариант → заголовок второй группы → вариант.
        Assert.IsType<PlatformFileGroupHeaderItem>(vm.DistributionOptionsGrouped[0]);
        Assert.IsType<PlatformDistributionOption>(vm.DistributionOptionsGrouped[1]);
        Assert.IsType<PlatformFileGroupHeaderItem>(vm.DistributionOptionsGrouped[2]);
        Assert.IsType<PlatformDistributionOption>(vm.DistributionOptionsGrouped[3]);
    }

    [Fact]
    public async Task ThinClientType_PicksThinZip()
    {
        var fileFull = File("8.3.27.2214_x64.zip", "x64", PlatformDistributionKind.WindowsSetupZip);
        var fileThin = File("8.3.27.2214_thin_1c_x64.zip", "x64", PlatformDistributionKind.WindowsSetupZip);
        var service = new FakeCatalogService
        {
            AvailableResult = new PlatformCatalogResult
            {
                Status = PortalFetchStatus.Ok,
                Releases = new[] { Release("8.3.27.2214", fileFull, fileThin) },
            },
        };
        var vm = CreateVm(service);

        await vm.LoadCatalogAsync();
        vm.DownloadType = PlatformDownloadType.ThinClient;

        Assert.NotNull(vm.PickedFile);
        Assert.Contains("thin", vm.PickedFile!.FileName, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- Учётная запись ИТС ----------

    [Fact]
    public void ResolveAccount_SetsNameAndFlag()
    {
        var account = new ItsAccount { Id = "a1", Name = "Основная", Login = "user@mail.ru", IsPrimary = true };
        var vm = CreateVm(account: account);

        Assert.True(vm.HasAccount);
        Assert.Equal("Основная", vm.AccountName);
    }

    [Fact]
    public void ResolveAccount_NullAccount_NoFlag()
    {
        var vm = CreateVm(account: null);

        Assert.False(vm.HasAccount);
        Assert.Empty(vm.AccountName);
    }

    [Fact]
    public async Task DownloadAsync_WithoutAccount_WarnsInLog()
    {
        var service = new FakeCatalogService
        {
            AvailableResult = new PlatformCatalogResult
            {
                Status = PortalFetchStatus.Ok,
                Releases = new[]
                {
                    Release("8.3.27.2214", File("8.3.27.2214_x64.zip", "x64", PlatformDistributionKind.WindowsSetupZip)),
                },
            },
        };
        var vm = CreateVm(service, account: null);

        await vm.LoadCatalogAsync();
        await vm.DownloadAsync();

        Assert.Contains(LocalizationManager.T("PlatformDownload.WarnNoAccount"), vm.LogText);
        Assert.True(vm.HasDownloaded);
    }

    // ---------- Скачивание ----------

    [Fact]
    public async Task DownloadAsync_SavesToTargetDirectory_SetsDownloadedPath()
    {
        var service = new FakeCatalogService
        {
            AvailableResult = new PlatformCatalogResult
            {
                Status = PortalFetchStatus.Ok,
                Releases = new[]
                {
                    Release("8.3.27.2214", File("8.3.27.2214_x64.zip", "x64", PlatformDistributionKind.WindowsSetupZip)),
                },
            },
        };
        var dir = Path.Combine(Path.GetTempPath(), "cm_dl_" + Guid.NewGuid().ToString("N"));
        var vm = CreateVm(service, account: new ItsAccount { Name = "ИТС", Login = "login" }, directory: dir);

        await vm.LoadCatalogAsync();
        Assert.True(vm.DownloadCommand.CanExecute(null));

        await vm.DownloadAsync();

        Assert.False(string.IsNullOrWhiteSpace(vm.DownloadedPath));
        Assert.StartsWith(dir, vm.DownloadedPath, StringComparison.OrdinalIgnoreCase);
        Assert.True(vm.HasDownloaded);
        Assert.False(string.IsNullOrWhiteSpace(vm.ResultText));
        Assert.Contains("8.3.27.2214_x64.zip", vm.DownloadedPath, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- Дерево версий: поиск и свернуть/развернуть все (issue #330, 7OH) ----------

    [Fact]
    public async Task VersionSearchQuery_FiltersTreeBySubstring()
    {
        var service = new FakeCatalogService
        {
            AvailableResult = new PlatformCatalogResult
            {
                Status = PortalFetchStatus.Ok,
                Releases = new[]
                {
                    Release("8.3.27.2214"),
                    Release("8.3.27.1688"),
                    Release("8.3.9.1"),
                    Release("8.5.1.42"),
                },
            },
        };
        var vm = CreateVm(service);

        await vm.LoadCatalogAsync();
        Assert.Equal(2, vm.VersionTree.Count);   // линии 8.5 и 8.3

        vm.VersionSearchQuery = "1688";

        // Остаются только линия 8.3 → группа 8.3.27 → лист 8.3.27.1688.
        var line = Assert.Single(vm.VersionTree);
        Assert.Equal("8.3", line.Name);
        var group = Assert.Single(line.Children);
        Assert.Equal("8.3.27", group.Name);
        Assert.Equal("8.3.27.1688", Assert.Single(group.Children).Name);

        // Выбранные версия/файл при поиске не сбрасываются.
        Assert.NotNull(vm.SelectedRelease);

        vm.VersionSearchQuery = "   ";
        Assert.Equal(2, vm.VersionTree.Count);   // пустой запрос — полное дерево
    }

    [Fact]
    public async Task CollapseAllAndExpandAll_ToggleNodeState()
    {
        var service = new FakeCatalogService
        {
            AvailableResult = new PlatformCatalogResult
            {
                Status = PortalFetchStatus.Ok,
                Releases = new[] { Release("8.3.27.2214"), Release("8.3.9.1") },
            },
        };
        var vm = CreateVm(service);

        await vm.LoadCatalogAsync();

        vm.CollapseAllCommand.Execute(null);
        Assert.All(vm.VersionTree, root => Assert.False(root.IsExpanded));
        Assert.All(vm.VersionTree.SelectMany(r => r.Children), n => Assert.False(n.IsExpanded));

        vm.ExpandAllCommand.Execute(null);
        Assert.All(vm.VersionTree, root => Assert.True(root.IsExpanded));
        Assert.All(vm.VersionTree.SelectMany(r => r.Children), n => Assert.True(n.IsExpanded));
    }

    [Fact]
    public async Task LoadCatalogAsync_EmptyFilesAfterOk_LogsNoFilesMessage()
    {
        // issue #330 (комментарий 7OH): страница version_files получена, но файлов
        // не распознано — вместо молча пустого списка «Выбор файла» в журнале
        // появляется понятное сообщение.
        var service = new FakeCatalogService
        {
            AvailableResult = new PlatformCatalogResult
            {
                Status = PortalFetchStatus.Ok,
                Releases = new[] { Release("8.3.27.2214") },
            },
        };
        var vm = CreateVm(service);

        await vm.LoadCatalogAsync();

        var expected = string.Format(
            LocalizationManager.T("PlatformDownload.Error.NoFiles"), "8.3.27.2214");
        Assert.Contains(expected, vm.LogText, StringComparison.Ordinal);
        Assert.Empty(vm.DistributionOptions);
        Assert.Null(vm.PickedFile);
    }

    [Fact]
    public async Task DownloadAsync_ProgressReported()
    {
        var gate = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeCatalogService
        {
            AvailableResult = new PlatformCatalogResult
            {
                Status = PortalFetchStatus.Ok,
                Releases = new[]
                {
                    Release("8.3.27.2214", File("8.3.27.2214_x64.zip", "x64", PlatformDistributionKind.WindowsSetupZip)),
                },
            },
        };
        var vm = CreateVm(service,
            account: new ItsAccount { Name = "ИТС", Login = "login" },
            download: (url, target, progress, ct) =>
            {
                progress?.Report(0.5);
                return gate.Task;
            });

        await vm.LoadCatalogAsync();
        var operation = vm.DownloadAsync();

        // Progress<T> может доставлять отчёт асинхронно (захваченный SynchronizationContext
        // в среде тестов), поэтому ждём достижения 0.5, а не полагаемся на синхронность.
        var reached = await WaitUntilAsync(() => vm.Progress >= 0.5, TimeSpan.FromSeconds(5));
        Assert.True(reached, "Прогресс не дошёл до 0.5 во время загрузки");

        gate.SetResult(Path.Combine(vm.TargetDirectory, "8.3.27.2214_8.3.27.2214_x64.zip"));
        await operation;

        Assert.Equal(1.0, vm.Progress, 3);
        Assert.True(vm.HasDownloaded);
    }

    [Fact]
    public async Task AppendLog_FromBackgroundThread_RaisesPropertyChangedWithoutException()
    {
        // Регрессия issue #330: AppendLog вызывается из фоновых задач (LoadCatalogAsync
        // использует ConfigureAwait(false)), и обработчики UI получают уведомление на
        // фоновом потоке. Контракт VM: уведомление поднимается, исключений не бросается —
        // потокозависимые UI-действия (ScrollToEnd) выполняет само окно через Dispatcher.
        var vm = CreateVm();
        var notifications = new List<string?>();
        vm.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);

        await Task.Run(() => vm.AppendLog("Фоновая строка журнала"));

        Assert.Contains(nameof(PlatformDownloadViewModel.LogText), notifications);
        Assert.Contains("Фоновая строка журнала", vm.LogText);
    }

    // ---------- Интеграция с порталом 1С (вход CAS) ----------

    /// <summary>Логгер-заглушка.</summary>
    private sealed class NoOpLogger : IAppLogger
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    /// <summary>Логгер, накапливающий сообщения (проверка диагностики, issue #330).</summary>
    private sealed class CapturingLogger : IAppLogger
    {
        public List<string> Infos { get; } = new();
        public List<string> Warnings { get; } = new();
        public List<string> Errors { get; } = new();

        public void Info(string message) => Infos.Add(message);
        public void Warn(string message) => Warnings.Add(message);
        public void Error(string message, Exception? exception = null) => Errors.Add(message);
    }

    /// <summary>Репозиторий в памяти: настройки пусты, старые поля авторизации не заданы
    /// (креды должны прийти из справочника ИТС — issue #333/#334).</summary>
    private sealed class MemRepo : IInfobaseRepository
    {
        public AppSettings Settings { get; set; } = new();

        public List<Infobase> Load() => new();
        public void Save(List<Infobase> infobases) { }
        public Task SaveAsync(List<Infobase> infobases, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public List<Group> LoadGroups() => new();
        public void SaveGroups(List<Group> groups) { }
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

    /// <summary>Fake-CAS: каталог Platform83 → 302 на login.1c.ru?service=… → форма с токеном
    /// execution → POST логина → 302 на releases.1c.ru/public/security_check?ticket=… → GET
    /// security_check (cookie) → повтор каталога → HTML с версиями. Маршрутизация по пути
    /// (без query), т.к. service= формы содержит /public/security_check.</summary>
    private sealed class CasLoginHandler : HttpMessageHandler
    {
        private int _projectRequests;

        public System.Collections.Generic.List<string> Log { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            Log.Add($"{request.Method} {request.RequestUri}");
            HttpResponseMessage response;

            if (path.Contains("/project/", StringComparison.OrdinalIgnoreCase))
            {
                _projectRequests++;
                response = _projectRequests == 1
                    ? Redirect(new Uri("https://login.1c.ru/login?service=https://releases.1c.ru/public/security_check"))
                    : Ok(VersionsTableHtml);
            }
            else if (path.Contains("/public/security_check", StringComparison.OrdinalIgnoreCase))
            {
                response = Ok("<html>session established</html>");
            }
            else if (path.Contains("/login", StringComparison.OrdinalIgnoreCase))
            {
                if (request.Method == HttpMethod.Post && request.Content is not null)
                {
                    response = Redirect(new Uri("https://releases.1c.ru/public/security_check?ticket=ST-123"));
                }
                else
                {
                    response = Ok("<form><input type=\"hidden\" name=\"execution\" value=\"e1s2\"/></form>");
                }
            }
            else
            {
                response = new HttpResponseMessage(HttpStatusCode.Found);
            }

            response.RequestMessage = request;
            return response;
        }

        private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body),
        };

        private static HttpResponseMessage Redirect(Uri location)
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = location;
            return response;
        }

        private const string VersionsTableHtml = """
            <html><body>
            <table id="versionsTable">
              <tr><td><a href="/version_files?nick=Platform83&ver=8.3.27.2214">8.3.27.2214</a></td></tr>
              <tr><td><a href="/version_files?nick=Platform83&ver=8.3.27.1688">8.3.27.1688</a></td></tr>
            </table>
            </body></html>
            """;
    }

    // ---------- Диагностика журнала (issue #330, комментарий 7OH от 2026-10-09) ----------

    [Fact]
    public async Task LoadCatalogAsync_EmptyFiles_WritesDiagnosticsToWindowLog()
    {
        // Страница version_files получена, но файлов не распознано: в журнал окна
        // выводится диагностическая строка (PlatformDownload.Status.FilesDiag), а в
        // файловый журнал — URL запроса, длина ответа и число распознанных файлов,
        // чтобы пользователь мог прислать диагностику (issue #330).
        var logger = new CapturingLogger();
        var service = new FakeCatalogService
        {
            AvailableResult = new PlatformCatalogResult
            {
                Status = PortalFetchStatus.Ok,
                Releases = new[] { Release("8.3.27.2214") },
            },
            FilesFetchedUrl = "https://releases.1c.ru/version_files?nick=Platform83&ver=8.3.27.2214",
            FilesBodyLength = 1234,
            FilesParsedFileCount = 0,
        };
        var vm = CreateVm(service, appLogger: logger);

        await vm.LoadCatalogAsync();

        // Журнал окна: статус ошибки и диагностическая строка (в тестовой среде
        // LocalizationManager.T возвращает ключ — он и попадает в журнал).
        Assert.Contains(LocalizationManager.T("PlatformDownload.Error.NoFiles"), vm.LogText);
        Assert.Contains(LocalizationManager.T("PlatformDownload.Status.FilesDiag"), vm.LogText);

        // Файловый журнал: фактические URL/длина/число файлов.
        var warning = Assert.Single(logger.Warnings);
        Assert.Contains("version_files?nick=Platform83&ver=8.3.27.2214", warning);
        Assert.Contains("bodyLength=1234", warning);
        Assert.Contains("parsedFiles=0", warning);
    }

    [Fact]
    public async Task LoadCatalogAsync_FilesLoaded_LogsFileCount()
    {
        var fileX64 = File("8.3.27.2214_x64.zip", "x64", PlatformDistributionKind.WindowsSetupZip);
        var logger = new CapturingLogger();
        var service = new FakeCatalogService
        {
            AvailableResult = new PlatformCatalogResult
            {
                Status = PortalFetchStatus.Ok,
                Releases = new[] { Release("8.3.27.2214", fileX64) },
            },
            FilesParsedFileCount = 1,
        };
        var vm = CreateVm(service, appLogger: logger);

        await vm.LoadCatalogAsync();

        // Журнал окна: ключ строки «файлов дистрибутива: N» (в тестовой среде — ключ).
        Assert.Contains(LocalizationManager.T("PlatformDownload.Status.Files"), vm.LogText);
        // Файловый журнал: диагностика с числом файлов.
        Assert.Contains(logger.Infos, m => m.Contains("parsedFiles=1", StringComparison.Ordinal));
        Assert.NotNull(vm.PickedFile);
    }

    // ---------- Фоновые загрузки (issue #334 п.1) ----------

    [Fact]
    public async Task DownloadAsync_RegistersEntryInManager_AndCompletesIt()
    {
        var manager = new BackgroundDownloadManager();
        var fileX64 = File("8.3.27.2214_x64.zip", "x64", PlatformDistributionKind.WindowsSetupZip);
        var service = new FakeCatalogService
        {
            AvailableResult = new PlatformCatalogResult
            {
                Status = PortalFetchStatus.Ok,
                Releases = new[] { Release("8.3.27.2214", fileX64) },
            },
        };
        var vm = CreateVm(service, backgroundDownloads: manager);

        await vm.LoadCatalogAsync();
        await vm.DownloadAsync();

        var snapshot = Assert.Single(manager.Snapshot());
        Assert.Equal(BackgroundDownloadState.Completed, snapshot.State);
        Assert.Equal("8.3.27.2214_x64.zip", snapshot.Title);
        Assert.Equal(1.0, snapshot.Progress);
        Assert.Equal(0, manager.ActiveCount);
    }

    [Fact]
    public async Task DownloadAsync_LivesAfterWindowClose_AndReportsProgress()
    {
        // Симуляция закрытия окна: окно уничтожено сразу после старта скачивания,
        // загрузка продолжается через делегат и видна в менеджере.
        var manager = new BackgroundDownloadManager();
        var fileX64 = File("8.3.27.2214_x64.zip", "x64", PlatformDistributionKind.WindowsSetupZip);
        var service = new FakeCatalogService
        {
            AvailableResult = new PlatformCatalogResult
            {
                Status = PortalFetchStatus.Ok,
                Releases = new[] { Release("8.3.27.2214", fileX64) },
            },
        };
        var downloadFinished = new TaskCompletionSource();
        var vm = CreateVm(
            service,
            download: async (url, target, progress, ct) =>
            {
                await Task.Delay(30, ct);
                progress?.Report(0.5);
                await Task.Delay(30, ct);
                downloadFinished.TrySetResult();
                return target;
            },
            backgroundDownloads: manager);

        await vm.LoadCatalogAsync();
        var task = vm.DownloadAsync();

        // «Окно закрыто»: локальных ссылок на VM больше не держим —
        // асинхронная операция продолжает жить и завершает менеджер-запись.
        var reported = await WaitUntilAsync(() => manager.Snapshot().Any(d => d.Progress > 0), TimeSpan.FromSeconds(5));
        Assert.True(reported, "Прогресс фонового скачивания должен попадать в менеджер");

        await task;
        Assert.True(downloadFinished.Task.IsCompleted);
        Assert.Equal(BackgroundDownloadState.Completed, manager.Snapshot().Single().State);
    }

    [Fact]
    public async Task DownloadAsync_CancelViaManager_CancelsDownload()
    {
        var manager = new BackgroundDownloadManager();
        var cancelled = new TaskCompletionSource();
        var fileX64 = File("8.3.27.2214_x64.zip", "x64", PlatformDistributionKind.WindowsSetupZip);
        var service = new FakeCatalogService
        {
            AvailableResult = new PlatformCatalogResult
            {
                Status = PortalFetchStatus.Ok,
                Releases = new[] { Release("8.3.27.2214", fileX64) },
            },
        };
        var vm = CreateVm(
            service,
            download: async (url, target, progress, ct) =>
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                    return target;
                }
                catch (OperationCanceledException)
                {
                    cancelled.TrySetResult();
                    throw;
                }
            },
            backgroundDownloads: manager);

        await vm.LoadCatalogAsync();
        var task = vm.DownloadAsync();

        var started = await WaitUntilAsync(() => manager.ActiveCount > 0, TimeSpan.FromSeconds(5));
        Assert.True(started);
        Assert.True(manager.Cancel(manager.Snapshot().First(d => d.IsActive).Id));

        var cancelledOk = await Task.WhenAny(cancelled.Task, Task.Delay(5000)) == cancelled.Task;
        Assert.True(cancelledOk, "Отмена из менеджера должна дойти до загрузки");

        await task; // OperationCanceledException обрабатывается внутри VM
        Assert.Equal(BackgroundDownloadState.Cancelled, manager.Snapshot().Single().State);
    }

    [Fact]
    public async Task DownloadAsync_Failure_MarksManagerEntryFailed()
    {
        var manager = new BackgroundDownloadManager();
        var fileX64 = File("8.3.27.2214_x64.zip", "x64", PlatformDistributionKind.WindowsSetupZip);
        var service = new FakeCatalogService
        {
            AvailableResult = new PlatformCatalogResult
            {
                Status = PortalFetchStatus.Ok,
                Releases = new[] { Release("8.3.27.2214", fileX64) },
            },
        };
        var vm = CreateVm(
            service,
            download: (_, _, _, _) => Task.FromResult<string?>(null),
            backgroundDownloads: manager);

        await vm.LoadCatalogAsync();
        await vm.DownloadAsync();

        Assert.Equal(BackgroundDownloadState.Failed, manager.Snapshot().Single().State);
    }

    // ---------- Fake-сервис ----------

    /// <summary>Ждёт выполнения условия с таймаутом (для асинхронных отчётов прогресса).</summary>
    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > timeout)
                return false;
            await Task.Delay(20);
        }

        return true;
    }

    private sealed class FakeCatalogService : IPlatformUpdateService
    {
        public PlatformCatalogResult AvailableResult { get; set; } = new() { Status = PortalFetchStatus.Ok };

        /// <summary>Диагностика загрузки файлов версии (issue #330): URL/длина/число файлов.</summary>
        public string? FilesFetchedUrl { get; set; }
        public int FilesBodyLength { get; set; }
        public int FilesParsedFileCount { get; set; }

        public Task<PlatformCatalogResult> GetAvailableReleasesAsync(CancellationToken ct = default)
            => Task.FromResult(AvailableResult);

        public Task<PlatformCatalogResult> GetAllAvailableReleasesAsync(CancellationToken ct = default)
            => Task.FromResult(AvailableResult);

        public Task<PlatformCatalogResult> GetAvailableReleasesForNickAsync(string nick, CancellationToken ct = default)
            => Task.FromResult(AvailableResult);

        public Task<PlatformCatalogResult> LoadReleaseFilesAsync(PlatformRelease release, CancellationToken ct = default)
            => Task.FromResult(new PlatformCatalogResult
            {
                Status = PortalFetchStatus.Ok,
                Release = release,
                FetchedUrl = FilesFetchedUrl,
                BodyLength = FilesBodyLength,
                ParsedFileCount = FilesParsedFileCount,
            });

        public Task<PlatformCatalogResult> LoadReleaseFilesForNickAsync(PlatformRelease release, string nick, CancellationToken ct = default)
            => Task.FromResult(new PlatformCatalogResult
            {
                Status = PortalFetchStatus.Ok,
                Release = release,
                FetchedUrl = FilesFetchedUrl,
                BodyLength = FilesBodyLength,
                ParsedFileCount = FilesParsedFileCount,
            });

        public PlatformReleaseFile? PickDistribution(IReadOnlyList<PlatformReleaseFile> files)
            => files.FirstOrDefault();
    }
}