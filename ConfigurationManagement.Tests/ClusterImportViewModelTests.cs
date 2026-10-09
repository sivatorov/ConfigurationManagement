using System.Collections.Generic;
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
/// Тесты вью-модели диалога импорта баз из кластера 1С (этап 2, 0.3.9.173):
/// подключение к ragent/RAS через fake-клиент rac (кластеры), автовыбор первого кластера,
/// загрузка баз и чеклист с пометкой дубликатов/файловых баз, сводка перед подтверждением,
/// команда «Импортировать» (только отмеченные новые), флаг занятости и обработка ошибок.
/// </summary>
public sealed class ClusterImportViewModelTests
{
    [Fact]
    public void Defaults_AddressLocalhost_Port1540_CommandsExist()
    {
        var vm = new ClusterImportViewModel(new FakeRacClient(), Array.Empty<Infobase>());

        Assert.Equal("localhost", vm.ServerAddress);
        Assert.Equal(IRacClient.DefaultPort, vm.ServerPort);
        Assert.Equal(1540, vm.ServerPort);
        Assert.Equal(string.Empty, vm.UserName);
        Assert.Equal(string.Empty, vm.Password);
        Assert.True(vm.UseClusterGrouping);
        Assert.False(vm.IsBusy);
        Assert.False(vm.ImportCompleted);
        Assert.Empty(vm.Rows);
        Assert.Empty(vm.SelectedBases);
        Assert.Equal(0, vm.ReadyToImportCount);
        Assert.Equal(0, vm.DuplicateCount);
        Assert.NotNull(vm.ConnectCommand);
        Assert.NotNull(vm.LoadBasesCommand);
        Assert.NotNull(vm.ImportCommand);
        Assert.NotNull(vm.SelectAllCommand);
        Assert.NotNull(vm.SelectNoneCommand);
    }

    [Fact]
    public async Task Connect_Success_FillsClusters_AutoSelectsFirst_AndLoadsBases()
    {
        var client = new FakeRacClient();
        var vm = new ClusterImportViewModel(client, Array.Empty<Infobase>());

        await vm.ConnectAsync();

        Assert.Equal(3, vm.Clusters.Count);
        Assert.Equal(FakeRacClient.FirstClusterId, vm.SelectedClusterId);
        Assert.Equal(1, client.ClustersCalls);
        Assert.Equal(FakeRacClient.FirstClusterId, client.InfobaseClusterIds.Single());
        Assert.Equal(3, vm.Rows.Count);
        Assert.Empty(vm.ErrorMessage);
        Assert.False(vm.IsBusy);

        // Поля строки чеклиста: подпись (описание/СУБД/сервер/имя БД), кластер, строка подключения.
        var buh = vm.Rows.Single(r => r.Name == "Бухгалтерия");
        Assert.Equal("Основная база • MSSQLServer • sql-01 • buho", buh.Subtitle);
        Assert.Equal("Главный кластер", buh.ClusterName);
        Assert.Equal("Srvr=\"srv1\";Ref=\"Бухгалтерия\"", buh.ConnectionString);
        Assert.False(buh.IsDuplicate);
        Assert.Null(buh.SkipReason);
        Assert.True(buh.IsChecked);
        Assert.NotNull(buh.Tag);
    }

    [Fact]
    public async Task Connect_Failure_ReportsError_AndWindowStaysUsable()
    {
        var client = new FakeRacClient(throwOnClusters: true);
        var vm = new ClusterImportViewModel(client, Array.Empty<Infobase>());

        await vm.ConnectAsync();

        Assert.NotEmpty(vm.ErrorMessage);
        Assert.False(vm.IsBusy);
        Assert.Empty(vm.Clusters);
        Assert.Empty(vm.Rows);
    }

    [Fact]
    public async Task Connect_EmptyAddress_ValidatesWithoutRac()
    {
        var client = new FakeRacClient();
        var vm = new ClusterImportViewModel(client, Array.Empty<Infobase>());
        vm.ServerAddress = "   ";

        await vm.ConnectAsync();

        Assert.Equal(0, client.ClustersCalls);
        Assert.NotEmpty(vm.ErrorMessage);
        Assert.False(vm.IsBusy);
        Assert.Empty(vm.Rows);
    }

    [Fact]
    public async Task Connect_HostPortInAddress_TakesPriorityOverPortField()
    {
        // Issue #324: пользователь указывает нестандартный порт прямо в адресе «host:port»
        // (как в командной строке rac.exe localhost:27545 cluster list), а поле «Порт»
        // остаётся дефолтным (1540). Без нормализации rac получил бы невалидный токен
        // «localhost:27545:1540» и подключение бы не установилось.
        var client = new FakeRacClient();
        var vm = new ClusterImportViewModel(client, Array.Empty<Infobase>());
        vm.ServerAddress = "localhost:27545";
        vm.ServerPort = 1540;

        await vm.ConnectAsync();

        Assert.NotNull(client.LastParams);
        Assert.Equal("localhost", client.LastParams.Address);
        Assert.Equal(27545, client.LastParams.Port);
        Assert.NotEmpty(vm.Clusters);
    }

    [Fact]
    public async Task ChangingCluster_ReloadsBases_AndClusterInfoIsCachedPerCluster()
    {
        var client = new FakeRacClient();
        var vm = new ClusterImportViewModel(client, Array.Empty<Infobase>());
        await vm.ConnectAsync();

        // Автовыбор первого кластера → базы загружены с его id, cluster info запрошен один раз.
        Assert.Equal(FakeRacClient.FirstClusterId, client.InfobaseClusterIds.Single());
        Assert.Equal(1, client.ClusterInfoCalls);

        vm.SelectedClusterId = FakeRacClient.SecondClusterId;

        Assert.Equal(2, client.InfobaseCalls);
        Assert.Equal(FakeRacClient.SecondClusterId, client.InfobaseClusterIds[^1]);
        Assert.All(vm.Rows, r => Assert.Equal("Второй кластер", r.ClusterName));
        Assert.Equal("srv2", vm.Rows[0].Tag.Connection.Server);
        Assert.Equal(2541, vm.Rows[0].Tag.Connection.Port); // порт КЛАСТЕРА, не ragent/RAS

        // Повторная загрузка первого кластера: cluster info берётся из кэша,
        // GetClusterInfoAsync повторно не вызывается.
        await vm.LoadBasesAsync(FakeRacClient.FirstClusterId);
        Assert.Equal(2, client.ClusterInfoCalls);
        Assert.Equal(3, client.InfobaseCalls);
    }

    [Fact]
    public async Task Duplicate_IsMarked_NotChecked_AndNotImportedEvenWhenChecked()
    {
        var existing = RacInfobaseMapper.ToInfobase(
            new RacInfobaseSummary { Name = "Бухгалтерия", Dbms = "MSSQLServer" },
            "srv1", 1541, null, "Группа");
        var client = new FakeRacClient();
        var vm = new ClusterImportViewModel(client, new[] { existing });

        await vm.ConnectAsync();

        var dup = vm.Rows.Single(r => r.Name == "Бухгалтерия");
        Assert.True(dup.IsDuplicate);
        Assert.False(dup.IsChecked);
        Assert.Equal(LocalizationManager.T("ClusterImport.AlreadyExists"), dup.SkipReason);

        // Принудительный чек пользователем не приводит к импорту — фильтр на этапе подтверждения.
        dup.IsChecked = true;
        vm.Import();

        Assert.DoesNotContain(vm.SelectedBases, b => b.Name == "Бухгалтерия");
    }

    [Fact]
    public async Task FileBase_HasSkipReason_NotChecked_AndNotImported()
    {
        var vm = new ClusterImportViewModel(new FakeRacClient(), Array.Empty<Infobase>());
        await vm.ConnectAsync();

        var fileRow = vm.Rows.Single(r => r.Name == "ФайловаяБаза");
        Assert.False(fileRow.IsDuplicate);
        Assert.False(fileRow.IsChecked);
        Assert.Equal(LocalizationManager.T("ClusterImport.SkipFileBase"), fileRow.SkipReason);

        fileRow.IsChecked = true;
        vm.Import();

        Assert.DoesNotContain(vm.SelectedBases, b => b.Name == "ФайловаяБаза");
    }

    [Fact]
    public async Task Summary_CountsReadyToImport_AndDuplicates()
    {
        var existing = RacInfobaseMapper.ToInfobase(
            new RacInfobaseSummary { Name = "Бухгалтерия", Dbms = "MSSQLServer" },
            "srv1", 1541, null, "Группа");
        var vm = new ClusterImportViewModel(new FakeRacClient(), new[] { existing });
        await vm.ConnectAsync();

        // Первый кластер: Бухгалтерия (дубликат), Управление персоналом (новая), ФайловаяБаза (пропущена).
        Assert.Equal(1, vm.ReadyToImportCount);
        Assert.Equal(2, vm.DuplicateCount);

        vm.SelectNone();
        Assert.Equal(0, vm.ReadyToImportCount);

        vm.SelectAll();
        Assert.Equal(1, vm.ReadyToImportCount);
        Assert.Equal(2, vm.DuplicateCount);

        // Ручное снятие отметки новой строки пересчитывает сводку.
        var newRow = vm.Rows.Single(r => r.Name == "Управление персоналом");
        newRow.IsChecked = false;
        Assert.Equal(0, vm.ReadyToImportCount);
    }

    [Fact]
    public async Task Import_ReturnsOnlyCheckedNewBases_WithMappedFields()
    {
        var client = new FakeRacClient();
        var vm = new ClusterImportViewModel(client, Array.Empty<Infobase>());
        await vm.ConnectAsync();

        // Отмечаем только одну из двух новых баз — остальные строки сняты.
        vm.SelectNone();
        vm.Rows.Single(r => r.Name == "Управление персоналом").IsChecked = true;
        vm.Import();

        Assert.True(vm.ImportCompleted);
        var ib = Assert.Single(vm.SelectedBases);
        Assert.Equal("Управление персоналом", ib.Name);
        Assert.Equal("Главный кластер", ib.Group); // UseClusterGrouping=true по умолчанию
        Assert.Equal(ConnectionType.ClientServer, ib.Connection.Type);
        Assert.Equal("srv1", ib.Connection.Server);         // хост из cluster info (hostName)
        Assert.Equal(1541, ib.Connection.Port);             // порт кластера, не порт RAS
        Assert.Equal("Управление персоналом", ib.Connection.DatabaseName);
        Assert.Equal(AuthenticationMode.Prompt, ib.Connection.AuthenticationMode);
    }

    [Fact]
    public async Task IsBusy_BlocksReentrantConnect()
    {
        // Fake-клиент с задержкой: пока первый запрос выполняется, второй не должен
        // наложиться (флаг занятости через Interlocked пропускает его).
        var client = new FakeRacClient(clusterDelay: TimeSpan.FromMilliseconds(300));
        var vm = new ClusterImportViewModel(client, Array.Empty<Infobase>());

        var first = vm.ConnectAsync();
        // Первый вызов синхронно устанавливает флаг занятости до первого await.
        Assert.True(vm.IsBusy);

        var second = vm.ConnectAsync();
        // Второй вызов сразу возвращается: флаг занятости уже установлен.
        Assert.True(second.IsCompleted);
        await second;

        await first;

        Assert.False(vm.IsBusy);
        // Кластеры запрошены ровно один раз.
        Assert.Equal(1, client.ClustersCalls);
    }

    [Fact]
    public async Task IsBusy_BlocksReentrantLoadBases()
    {
        var client = new FakeRacClient(loadDelay: TimeSpan.FromMilliseconds(300));
        var vm = new ClusterImportViewModel(client, Array.Empty<Infobase>());
        await vm.ConnectAsync();

        // Автовыбор первого кластера запустил фоновую загрузку баз — она ещё идёт.
        Assert.True(vm.IsBusy);

        var second = vm.LoadBasesAsync(FakeRacClient.FirstClusterId);
        // Повторный вход отклонён флагом занятости.
        Assert.True(second.IsCompleted);
        await second;

        // Дожидаемся завершения фоновой загрузки (таймаут-цикл вместо сна).
        var deadline = System.DateTime.UtcNow.AddSeconds(5);
        while (vm.IsBusy && System.DateTime.UtcNow < deadline)
            await Task.Delay(20);

        Assert.False(vm.IsBusy);
        // Базы прочитаны ровно один раз.
        Assert.Equal(1, client.InfobaseCalls);
    }

    [Fact]
    public async Task EmptyCluster_NoRows_NoError()
    {
        var client = new FakeRacClient();
        var vm = new ClusterImportViewModel(client, Array.Empty<Infobase>());
        await vm.ConnectAsync();

        vm.SelectedClusterId = FakeRacClient.ThirdClusterId;

        Assert.Empty(vm.Rows);
        Assert.Empty(vm.ErrorMessage);
        Assert.Equal(0, vm.ReadyToImportCount);
        Assert.Equal(0, vm.DuplicateCount);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task LoadBases_Failure_ReportsError_NotBusy()
    {
        var client = new FakeRacClient(throwOnLoad: true);
        var vm = new ClusterImportViewModel(client, Array.Empty<Infobase>());

        await vm.ConnectAsync(); // автовыбор первого → загрузка баз падает внутри LoadBasesAsync

        Assert.Equal(1, client.InfobaseCalls);
        Assert.NotEmpty(vm.ErrorMessage);
        Assert.False(vm.IsBusy);
        Assert.Empty(vm.Rows);
    }

    [Fact]
    public async Task LoadBases_Cancelled_SetsStatus_WithoutError()
    {
        var client = new FakeRacClient(throwCancel: true);
        var vm = new ClusterImportViewModel(client, Array.Empty<Infobase>());

        await vm.ConnectAsync();

        Assert.Empty(vm.ErrorMessage);
        Assert.False(vm.IsBusy);
        Assert.NotEmpty(vm.StatusText);
    }

    // ===================== Fakes =====================

    /// <summary>
    /// Fake-клиент rac для тестов: три кластера (первый — 3 базы, вторая — 2, третий — пустой),
    /// «cluster info» с именем хоста для каждой базы; подсчёт вызовов и записи clusterId.
    /// </summary>
    private sealed class FakeRacClient : IRacClient
    {
        public static readonly System.Guid FirstClusterId = System.Guid.Parse("11111111-1111-1111-1111-111111111111");
        public static readonly System.Guid SecondClusterId = System.Guid.Parse("22222222-2222-2222-2222-222222222222");
        public static readonly System.Guid ThirdClusterId = System.Guid.Parse("33333333-3333-3333-3333-333333333333");

        private readonly bool _throwOnClusters;
        private readonly bool _throwOnLoad;
        private readonly bool _throwCancel;
        private readonly TimeSpan _clusterDelay;
        private readonly TimeSpan _loadDelay;

        public FakeRacClient(
            bool throwOnClusters = false, bool throwOnLoad = false, bool throwCancel = false,
            TimeSpan? clusterDelay = null, TimeSpan? loadDelay = null)
        {
            _throwOnClusters = throwOnClusters;
            _throwOnLoad = throwOnLoad;
            _throwCancel = throwCancel;
            _clusterDelay = clusterDelay ?? TimeSpan.Zero;
            _loadDelay = loadDelay ?? TimeSpan.Zero;
        }

        /// <summary>Текст последней ошибки действия (как в реальном клиенте).</summary>
        public string LastActionError { get; } = string.Empty;

        /// <summary>«cluster update» (issue #324, C3) тестами окна импорта не используется.</summary>
        public Task<bool> UpdateClusterAsync(
            RacConnectionParams parameters, Guid clusterId, RacClusterUpdate changes,
            CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        /// <summary>Сколько раз запрошен список кластеров.</summary>
        public int ClustersCalls { get; private set; }

        /// <summary>Параметры последнего вызова «cluster list» (проверка нормализации host:port).</summary>
        public RacConnectionParams? LastParams { get; private set; }

        /// <summary>Сколько раз запрошена информация о кластере («cluster info»).</summary>
        public int ClusterInfoCalls { get; private set; }

        /// <summary>Сколько раз запрошены информационные базы кластера.</summary>
        public int InfobaseCalls { get; private set; }

        /// <summary>clusterId, переданные в «infobase summary list» (порядок вызовов).</summary>
        public List<System.Guid> InfobaseClusterIds { get; } = new();

        public async Task<IReadOnlyList<RacCluster>> GetClustersAsync(
            RacConnectionParams parameters, CancellationToken cancellationToken = default)
        {
            ClustersCalls++;
            LastParams = parameters;
            if (_clusterDelay > TimeSpan.Zero)
                await Task.Delay(_clusterDelay, cancellationToken).ConfigureAwait(false);
            if (_throwOnClusters)
                throw new RacClientException("rac not found");
            return new[]
            {
                new RacCluster { Id = FirstClusterId, Name = "Главный кластер", Port = 1541 },
                new RacCluster { Id = SecondClusterId, Name = "Второй кластер", Port = 2541 },
                new RacCluster { Id = ThirdClusterId, Name = "Пустой кластер", Port = 1541 }
            };
        }

        public Task<RacClusterInfo?> GetClusterInfoAsync(
            RacConnectionParams parameters, System.Guid clusterId, CancellationToken cancellationToken = default)
        {
            ClusterInfoCalls++;
            var host = clusterId == FirstClusterId ? "srv1" : clusterId == SecondClusterId ? "srv2" : "srv3";
            return Task.FromResult<RacClusterInfo?>(new RacClusterInfo
            {
                Name = host == "srv1" ? "Главный кластер" : host == "srv2" ? "Второй кластер" : "Пустой кластер",
                HostName = host,
                Port = clusterId == SecondClusterId ? 2541 : 1541
            });
        }

        public async Task<IReadOnlyList<RacInfobaseSummary>> GetInfobasesAsync(
            RacConnectionParams parameters, System.Guid clusterId, CancellationToken cancellationToken = default)
        {
            InfobaseCalls++;
            InfobaseClusterIds.Add(clusterId);
            if (_loadDelay > TimeSpan.Zero)
                await Task.Delay(_loadDelay, cancellationToken).ConfigureAwait(false);
            if (_throwOnLoad)
                throw new RacClientException("нет прав на просмотр баз");
            if (_throwCancel)
                throw new OperationCanceledException();

            if (clusterId == FirstClusterId)
            {
                return new[]
                {
                    new RacInfobaseSummary { Name = "Бухгалтерия", Descr = "Основная база", Dbms = "MSSQLServer", DbServer = "sql-01", DbName = "buho" },
                    new RacInfobaseSummary { Name = "Управление персоналом", Dbms = "PostgreSQL", DbServer = "pg-01", DbName = "hr" },
                    new RacInfobaseSummary { Name = "ФайловаяБаза", Descr = "Файловая ИБ кластера", Dbms = "" }
                };
            }

            if (clusterId == SecondClusterId)
            {
                return new[]
                {
                    new RacInfobaseSummary { Name = "Зарплата", Descr = "Расчёт заработной платы", Dbms = "PostgreSQL", DbServer = "pg-02", DbName = "zp" },
                    new RacInfobaseSummary { Name = "Документооборот", Dbms = "MSSQLServer", DbServer = "sql-02", DbName = "do" }
                };
            }

            return Array.Empty<RacInfobaseSummary>();
        }

        public Task<IReadOnlyList<RacProcessInfo>> GetProcessesAsync(
            RacConnectionParams parameters, System.Guid clusterId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RacProcessInfo>>(Array.Empty<RacProcessInfo>());

        public Task<IReadOnlyList<RacSessionInfo>> GetSessionsAsync(
            RacConnectionParams parameters, System.Guid clusterId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RacSessionInfo>>(Array.Empty<RacSessionInfo>());

        public Task<IReadOnlyList<RacConnectionInfo>> GetConnectionsAsync(
            RacConnectionParams parameters, System.Guid clusterId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RacConnectionInfo>>(Array.Empty<RacConnectionInfo>());

        public Task<IReadOnlyList<RacLockInfo>> GetLocksAsync(
            RacConnectionParams parameters, System.Guid clusterId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RacLockInfo>>(Array.Empty<RacLockInfo>());

        // Регламентные задания импорт баз не использует — пустой список.
        public Task<IReadOnlyList<RacJobInfo>> GetJobsAsync(
            RacConnectionParams parameters, System.Guid clusterId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RacJobInfo>>(Array.Empty<RacJobInfo>());

        public Task<bool> SetJobStateAsync(
            RacConnectionParams parameters, System.Guid clusterId, System.Guid jobId,
            RacJobAction action, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> TerminateSessionAsync(
            RacConnectionParams parameters, System.Guid clusterId, System.Guid sessionId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> DisconnectConnectionAsync(
            RacConnectionParams parameters, System.Guid clusterId, System.Guid connectionId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }
}