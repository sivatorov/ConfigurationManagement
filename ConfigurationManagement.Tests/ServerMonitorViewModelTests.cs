using Configuration_Management.Localization;
using Configuration_Management.Models;
using Configuration_Management.Services;
using Configuration_Management.ViewModels;
using Xunit;

namespace ConfigurationManagement.Tests;

/// <summary>
/// Тесты встроенного монитора серверов 1С — ViewModel (этапы 2–3, 0.3.9.124–125):
/// дефолты подключения, подключение с fake-клиентом rac (кластеры), загрузка данных
/// кластера во вкладки, команды действий (завершение сеанса / разрыв соединения с
/// подтверждением), автообновление по таймеру и флаг занятости, форматирование
/// роу-строк (память, время, состояния).
/// </summary>
public sealed class ServerMonitorViewModelTests
{
    [Fact]
    public void Defaults_PortIs1540_AddressLocalhost_CommandsExist()
    {
        var vm = new ServerMonitorViewModel(new FakeRacClient(), new RecordingDialogs());

        Assert.Equal("localhost", vm.ServerAddress);
        Assert.Equal(IRacClient.DefaultPort, vm.ServerPort);
        Assert.Equal(1540, vm.ServerPort);
        Assert.Equal(string.Empty, vm.UserName);
        Assert.Equal(string.Empty, vm.Password);
        Assert.False(vm.HasConnected);
        Assert.False(vm.IsBusy);
        Assert.False(vm.AutoRefreshActive);
        Assert.NotNull(vm.ConnectCommand);
        Assert.NotNull(vm.RefreshCommand);
        Assert.NotNull(vm.TerminateSessionCommand);
        Assert.NotNull(vm.DisconnectConnectionCommand);
        Assert.Empty(vm.Processes);
        Assert.Empty(vm.Sessions);
        Assert.Empty(vm.Connections);
        Assert.Empty(vm.Locks);
        Assert.Empty(vm.Clusters);
        Assert.Empty(vm.ClusterRows);
    }

    [Fact]
    public async Task ConnectAsync_WithClusters_SetsConnected_AndSelectsFirst()
    {
        var vm = new ServerMonitorViewModel(new FakeRacClient(), new RecordingDialogs());

        await vm.ConnectAsync();

        Assert.True(vm.HasConnected);
        Assert.Equal(2, vm.Clusters.Count);
        Assert.Equal(2, vm.ClusterRows.Count);
        Assert.Equal(FakeRacClient.FirstClusterId, vm.SelectedClusterId);
        Assert.NotEmpty(vm.StatusText);
        Assert.Empty(vm.ErrorMessage);
    }

    [Fact]
    public async Task ConnectAsync_OnClientFailure_ReportsError_WithoutConnected()
    {
        var vm = new ServerMonitorViewModel(new FakeRacClient(throwOnClusters: true), new RecordingDialogs());

        await vm.ConnectAsync();

        Assert.False(vm.HasConnected);
        Assert.NotEmpty(vm.ErrorMessage);
        Assert.NotEmpty(vm.StatusText);
    }

    [Fact]
    public async Task ConnectAsync_HostPortInAddress_TakesPriorityOverPortField()
    {
        // Issue #324: нестандартный порт агента/RAS можно указать прямо в адресе «host:port»
        // (как в командной строке rac.exe localhost:27545 cluster list) — порт из адреса
        // приоритетнее значения поля «Порт», иначе rac получил бы «localhost:27545:1540».
        var client = new FakeRacClient();
        var vm = new ServerMonitorViewModel(client, new RecordingDialogs());
        vm.ServerAddress = "localhost:27545";
        vm.ServerPort = 1540;

        await vm.ConnectAsync();

        Assert.True(vm.HasConnected);
        Assert.NotNull(client.LastParams);
        Assert.Equal("localhost", client.LastParams.Address);
        Assert.Equal(27545, client.LastParams.Port);
        Assert.NotEmpty(vm.Clusters);
    }

    [Fact]
    public async Task LoadClusterDataAsync_FillsTabs_AndClusterInfo()
    {
        var vm = new ServerMonitorViewModel(new FakeRacClient(), new RecordingDialogs());

        await vm.LoadClusterDataAsync(FakeRacClient.FirstClusterId);

        Assert.Single(vm.Processes);
        Assert.Single(vm.Sessions);
        Assert.Single(vm.Connections);
        Assert.Single(vm.Locks);
        Assert.NotNull(vm.ClusterInfo);
        Assert.NotEmpty(vm.ClusterInfoText);
        Assert.Contains("name:", vm.ClusterInfoText);
    }

    [Fact]
    public void Refresh_WithoutConnection_DoesNothing()
    {
        var vm = new ServerMonitorViewModel(new FakeRacClient(), new RecordingDialogs());
        vm.Refresh();
        Assert.Empty(vm.Processes);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public void Password_Settable_InMemory()
    {
        var vm = new ServerMonitorViewModel(new FakeRacClient(), new RecordingDialogs());
        vm.Password = "secret";
        Assert.Equal("secret", vm.Password);
    }

    // ===================== Действия: завершение сеанса =====================

    [Fact]
    public async Task TerminateSession_Confirm_CallsClient_WithCorrectArguments_AndRefreshes()
    {
        var client = new FakeRacClient();
        var dialogs = new RecordingDialogs();
        var vm = new ServerMonitorViewModel(client, dialogs);
        await vm.ConnectAsync();
        await vm.LoadClusterDataAsync(FakeRacClient.FirstClusterId);

        var session = vm.Sessions.Single();
        vm.SelectedSession = session;
        await vm.TerminateSessionAsync();

        // Подтверждение запрошено ровно один раз.
        Assert.Single(dialogs.Confirms);
        Assert.Empty(dialogs.Warnings);

        // Клиент вызван с верными аргументами: кластер + id выбранного сеанса.
        Assert.Single(client.TerminateCalls);
        Assert.Equal(FakeRacClient.FirstClusterId, client.TerminateCalls[0].clusterId);
        Assert.Equal(session.Id, client.TerminateCalls[0].sessionId);
        Assert.Empty(client.LastActionError);

        // После действия списки перечитаны (Refresh в finally).
        Assert.NotEmpty(vm.StatusText);
        Assert.Empty(vm.ErrorMessage);
    }

    [Fact]
    public async Task TerminateSession_WithoutSelection_DoesNothing()
    {
        var client = new FakeRacClient();
        var dialogs = new RecordingDialogs();
        var vm = new ServerMonitorViewModel(client, dialogs);
        await vm.ConnectAsync();

        await vm.TerminateSessionAsync();

        Assert.Empty(dialogs.Confirms);
        Assert.Empty(client.TerminateCalls);
    }

    [Fact]
    public async Task TerminateSession_Cancelled_DoesNotCallClient()
    {
        var client = new FakeRacClient();
        var dialogs = new RecordingDialogs { ConfirmResult = false };
        var vm = new ServerMonitorViewModel(client, dialogs);
        await vm.ConnectAsync();
        await vm.LoadClusterDataAsync(FakeRacClient.FirstClusterId);

        vm.SelectedSession = vm.Sessions.Single();
        await vm.TerminateSessionAsync();

        Assert.Single(dialogs.Confirms);
        Assert.Empty(client.TerminateCalls);
    }

    [Fact]
    public async Task TerminateSession_ClientReturnsFalse_ShowsWarning_AndSetsStatus()
    {
        var client = new FakeRacClient(actionFails: true);
        var dialogs = new RecordingDialogs();
        var vm = new ServerMonitorViewModel(client, dialogs);
        await vm.ConnectAsync();
        await vm.LoadClusterDataAsync(FakeRacClient.FirstClusterId);

        vm.SelectedSession = vm.Sessions.Single();
        await vm.TerminateSessionAsync();

        Assert.Single(client.TerminateCalls);
        Assert.Single(dialogs.Warnings);
        Assert.NotEmpty(vm.StatusText);
    }

    [Fact]
    public async Task TerminateSession_ClientThrows_ShowsWarning_AndDoesNotCrash()
    {
        var client = new FakeRacClient(throwOnAction: true);
        var dialogs = new RecordingDialogs();
        var vm = new ServerMonitorViewModel(client, dialogs);
        await vm.ConnectAsync();
        await vm.LoadClusterDataAsync(FakeRacClient.FirstClusterId);

        vm.SelectedSession = vm.Sessions.Single();
        await vm.TerminateSessionAsync();

        Assert.Single(dialogs.Warnings);
        Assert.NotEmpty(vm.StatusText);
        Assert.False(vm.IsBusy);
    }

    // ===================== Действия: разрыв соединения =====================

    [Fact]
    public async Task DisconnectConnection_Confirm_CallsClient_WithCorrectArguments()
    {
        var client = new FakeRacClient();
        var dialogs = new RecordingDialogs();
        var vm = new ServerMonitorViewModel(client, dialogs);
        await vm.ConnectAsync();
        await vm.LoadClusterDataAsync(FakeRacClient.FirstClusterId);

        var connection = vm.Connections.Single();
        vm.SelectedConnection = connection;
        await vm.DisconnectConnectionAsync();

        Assert.Single(dialogs.Confirms);
        Assert.Empty(dialogs.Warnings);
        Assert.Single(client.DisconnectCalls);
        Assert.Equal(FakeRacClient.FirstClusterId, client.DisconnectCalls[0].clusterId);
        Assert.Equal(connection.Id, client.DisconnectCalls[0].connectionId);
        Assert.NotEmpty(vm.StatusText);
    }

    [Fact]
    public async Task DisconnectConnection_Cancelled_DoesNotCallClient()
    {
        var client = new FakeRacClient();
        var dialogs = new RecordingDialogs { ConfirmResult = false };
        var vm = new ServerMonitorViewModel(client, dialogs);
        await vm.ConnectAsync();
        await vm.LoadClusterDataAsync(FakeRacClient.FirstClusterId);

        vm.SelectedConnection = vm.Connections.Single();
        await vm.DisconnectConnectionAsync();

        Assert.Single(dialogs.Confirms);
        Assert.Empty(client.DisconnectCalls);
    }

    [Fact]
    public async Task DisconnectConnection_ClientFails_ShowsWarning_AndSetsStatus()
    {
        var client = new FakeRacClient(actionFails: true);
        var dialogs = new RecordingDialogs();
        var vm = new ServerMonitorViewModel(client, dialogs);
        await vm.ConnectAsync();
        await vm.LoadClusterDataAsync(FakeRacClient.FirstClusterId);

        vm.SelectedConnection = vm.Connections.Single();
        await vm.DisconnectConnectionAsync();

        Assert.Single(client.DisconnectCalls);
        Assert.Single(dialogs.Warnings);
        Assert.NotEmpty(vm.StatusText);
    }

    // ===================== Регламентные задания =====================

    [Fact]
    public async Task LoadClusterDataAsync_FillsJobsTab_AndInfobaseFilter()
    {
        var vm = new ServerMonitorViewModel(new FakeRacClient(), new RecordingDialogs());

        await vm.LoadClusterDataAsync(FakeRacClient.FirstClusterId);

        Assert.Equal(2, vm.Jobs.Count);
        Assert.Equal(2, vm.FilteredJobs.Count);
        // Фильтр: «Все базы» + база-владелец из маппинга.
        Assert.Contains(vm.JobInfobaseFilterRows, r => r.Id is null);
        Assert.Contains(vm.JobInfobaseFilterRows, r => r.Id == FakeRacClient.FirstInfobaseId);
        // Имя базы подставлено из кэша «infobase summary list»; задание без базы —
        // помечается ключом-заглушкой (в тестах локализация не инициализирована —
        // сравнение самосогласовано через тот же ключ).
        Assert.Equal("Бухгалтерия", vm.Jobs.Single(j => j.Id == FakeRacClient.FirstJobId).InfobaseName);
        Assert.Equal(
            LocalizationManager.T("ServerMonitor.Job.UnknownBase"),
            vm.Jobs.Single(j => j.Id == FakeRacClient.SecondJobId).InfobaseName);
    }

    [Fact]
    public async Task JobFilter_FiltersByInfobase_AndAllRestores()
    {
        var vm = new ServerMonitorViewModel(new FakeRacClient(), new RecordingDialogs());
        await vm.LoadClusterDataAsync(FakeRacClient.FirstClusterId);

        vm.SelectedJobInfobaseId = FakeRacClient.FirstInfobaseId;

        var row = Assert.Single(vm.FilteredJobs);
        Assert.Equal(FakeRacClient.FirstJobId, row.Id);

        vm.SelectedJobInfobaseId = null;

        Assert.Equal(2, vm.FilteredJobs.Count);
    }

    [Fact]
    public async Task Reload_PreservesJobSelection_AndJobFilter()
    {
        var vm = new ServerMonitorViewModel(new FakeRacClient(), new RecordingDialogs());
        await vm.LoadClusterDataAsync(FakeRacClient.FirstClusterId);

        vm.SelectedJob = vm.Jobs.Single(j => j.Id == FakeRacClient.FirstJobId);
        vm.SelectedJobInfobaseId = FakeRacClient.FirstInfobaseId;

        await vm.LoadClusterDataAsync(FakeRacClient.FirstClusterId);

        Assert.NotNull(vm.SelectedJob);
        Assert.Equal(FakeRacClient.FirstJobId, vm.SelectedJob!.Id);
        Assert.Equal(FakeRacClient.FirstInfobaseId, vm.SelectedJobInfobaseId);
        Assert.Single(vm.FilteredJobs);
    }

    [Fact]
    public async Task PauseSelectedJob_Confirm_CallsClient_WithPauseAction()
    {
        var client = new FakeRacClient();
        var dialogs = new RecordingDialogs();
        var vm = new ServerMonitorViewModel(client, dialogs);
        await vm.ConnectAsync();
        await vm.LoadClusterDataAsync(FakeRacClient.FirstClusterId);

        vm.SelectedJob = vm.Jobs.Single(j => j.Id == FakeRacClient.FirstJobId);
        await vm.PauseSelectedJobAsync();

        Assert.Single(dialogs.Confirms);
        Assert.Empty(dialogs.Warnings);
        var call = Assert.Single(client.StateCalls);
        Assert.Equal((FakeRacClient.FirstClusterId, FakeRacClient.FirstJobId, RacJobAction.Pause), call);
        Assert.NotEmpty(vm.StatusText);
    }

    [Fact]
    public async Task PauseSelectedJob_Cancelled_DoesNotCallClient()
    {
        var client = new FakeRacClient();
        var dialogs = new RecordingDialogs { ConfirmResult = false };
        var vm = new ServerMonitorViewModel(client, dialogs);
        await vm.ConnectAsync();
        await vm.LoadClusterDataAsync(FakeRacClient.FirstClusterId);

        vm.SelectedJob = vm.Jobs.Single(j => j.Id == FakeRacClient.FirstJobId);
        await vm.PauseSelectedJobAsync();

        Assert.Single(dialogs.Confirms);
        Assert.Empty(client.StateCalls);
    }

    [Fact]
    public async Task PauseSelectedJob_NotAllowedForPausedJob_DoesNothing()
    {
        var client = new FakeRacClient();
        var dialogs = new RecordingDialogs();
        var vm = new ServerMonitorViewModel(client, dialogs);
        await vm.LoadClusterDataAsync(FakeRacClient.FirstClusterId);

        // Второе задание в фейке имеет состояние «paused» — приостановить нельзя.
        vm.SelectedJob = vm.Jobs.Single(j => j.Id == FakeRacClient.SecondJobId);
        await vm.PauseSelectedJobAsync();

        Assert.Empty(client.StateCalls);
        Assert.Empty(dialogs.Confirms);
    }

    [Fact]
    public async Task ResumeSelectedJob_Confirm_CallsClient_WithResumeAction()
    {
        var client = new FakeRacClient();
        var dialogs = new RecordingDialogs();
        var vm = new ServerMonitorViewModel(client, dialogs);
        await vm.ConnectAsync();
        await vm.LoadClusterDataAsync(FakeRacClient.FirstClusterId);

        vm.SelectedJob = vm.Jobs.Single(j => j.Id == FakeRacClient.SecondJobId);
        await vm.ResumeSelectedJobAsync();

        var call = Assert.Single(client.StateCalls);
        Assert.Equal((FakeRacClient.FirstClusterId, FakeRacClient.SecondJobId, RacJobAction.Resume), call);
        Assert.NotEmpty(vm.StatusText);
    }

    [Fact]
    public async Task PauseSelectedJob_ClientReturnsFalse_ShowsWarning_WithDetail()
    {
        var client = new FakeRacClient(actionFails: true);
        var dialogs = new RecordingDialogs();
        var vm = new ServerMonitorViewModel(client, dialogs);
        await vm.ConnectAsync();
        await vm.LoadClusterDataAsync(FakeRacClient.FirstClusterId);

        vm.SelectedJob = vm.Jobs.Single(j => j.Id == FakeRacClient.FirstJobId);
        await vm.PauseSelectedJobAsync();

        Assert.Single(client.StateCalls);
        var warning = Assert.Single(dialogs.Warnings);
        Assert.Contains("нет прав", warning.message);
        Assert.NotEmpty(vm.StatusText);
    }

    [Fact]
    public async Task PauseSelectedJob_ClientThrows_ShowsWarning_DoesNotCrash()
    {
        var client = new FakeRacClient(throwOnAction: true);
        var dialogs = new RecordingDialogs();
        var vm = new ServerMonitorViewModel(client, dialogs);
        await vm.ConnectAsync();
        await vm.LoadClusterDataAsync(FakeRacClient.FirstClusterId);

        vm.SelectedJob = vm.Jobs.Single(j => j.Id == FakeRacClient.FirstJobId);
        await vm.PauseSelectedJobAsync();

        Assert.Single(dialogs.Warnings);
        Assert.NotEmpty(vm.StatusText);
        Assert.False(vm.IsBusy);
    }

    // ===================== Автообновление =====================

    [Fact]
    public void AutoRefresh_NotStarted_WithoutConnection()
    {
        var vm = new ServerMonitorViewModel(new FakeRacClient(), new RecordingDialogs());
        Assert.False(vm.AutoRefreshActive);
    }

    [Fact]
    public async Task AutoRefresh_StartsAfterConnect_AndStopsOnDispose()
    {
        var vm = new ServerMonitorViewModel(new FakeRacClient(), new RecordingDialogs());

        Assert.False(vm.AutoRefreshActive);

        await vm.ConnectAsync();

        Assert.True(vm.HasConnected);
        Assert.True(vm.AutoRefreshActive);
        Assert.Equal(ServerMonitorViewModel.AutoRefreshIntervalMs, 5000);

        vm.Dispose();

        Assert.False(vm.AutoRefreshActive);
    }

    [Fact]
    public async Task AutoRefresh_DoesNotStart_OnFailedConnect()
    {
        var vm = new ServerMonitorViewModel(new FakeRacClient(throwOnClusters: true), new RecordingDialogs());

        await vm.ConnectAsync();

        Assert.False(vm.HasConnected);
        Assert.False(vm.AutoRefreshActive);
    }

    [Fact]
    public async Task AutoRefresh_DisabledBeforeConnect_TimerDoesNotStart()
    {
        // issue #324: галка автообновления снята ДО подключения — таймер не должен
        // запускаться (раньше ConnectAsync стартовал его безусловно) и индикатор
        // должен оставаться «выключено».
        var vm = new ServerMonitorViewModel(new FakeRacClient(), new RecordingDialogs());
        vm.SetAutoRefreshEnabled(false);

        await vm.ConnectAsync();

        Assert.True(vm.HasConnected);
        Assert.False(vm.IsAutoRefreshEnabled);
        Assert.False(vm.AutoRefreshActive);
    }

    [Fact]
    public async Task LoadClusterData_NetworkError_StopsAutoRefresh()
    {
        // issue #324: обычная ошибка загрузки (не только парсинг) останавливает таймер —
        // бесконечный ретрай каждые 5 с после разрыва соединения не нужен. Ручное
        // «Обновить» остаётся доступным; после успеха таймер возобновится.
        var vm = new ServerMonitorViewModel(
            new FakeRacClient(singleCluster: true, throwOnLoad: true), new RecordingDialogs());

        await vm.ConnectAsync();
        // Единственный кластер выбран автоматически → фоновая загрузка данных падает
        // с обычным исключением (не RacOutputParseException); ждём её завершения.
        await Task.Delay(50);

        Assert.True(vm.HasConnected);
        Assert.False(vm.AutoRefreshActive);
        Assert.False(string.IsNullOrEmpty(vm.ErrorMessage));
    }

    [Fact]
    public async Task LoadClusterDataAsync_BusyFlag_SkipsOverlappingRequests()
    {
        // Fake-клиент с задержкой: пока первый запрос выполняется, второй не должен
        // наложиться (флаг занятости через Interlocked пропускает его).
        var client = new FakeRacClient(delay: TimeSpan.FromMilliseconds(300));
        var vm = new ServerMonitorViewModel(client, new RecordingDialogs());

        var first = vm.LoadClusterDataAsync(FakeRacClient.FirstClusterId);
        // Первый вызов синхронно устанавливает флаг занятости до первого await.
        Assert.True(vm.IsBusy);

        var second = vm.LoadClusterDataAsync(FakeRacClient.FirstClusterId);
        // Второй вызов сразу возвращается: флаг занятости уже установлен.
        Assert.True(second.IsCompleted);
        await second;

        await first;

        Assert.False(vm.IsBusy);
        // Данные прочитаны ровно один раз.
        Assert.Equal(1, client.ProcessCalls);
    }

    // ===================== Форматирование роу-строк =====================

    [Fact]
    public void SessionRow_FormatsMemory_Duration_State()
    {
        var row = new RacSessionRow(new RacSessionInfo
        {
            Id = Guid.NewGuid(),
            User = "Иванов",
            Memory = 512 * 1024 * 1024,
            DurationAll = 3600_000,
            DurationCurrent = 90_000,
            State = "active"
        });

        Assert.Contains("512", row.MemoryText);
        Assert.EndsWith(LocalizationManager.T("ServerMonitor.Mb"), row.MemoryText);
        Assert.Equal("01:00:00", row.DurationAllText);
        Assert.Equal("00:01:30", row.DurationCurrentText);
        Assert.Equal("#16A34A", row.StateColorHex);
        Assert.False(string.IsNullOrWhiteSpace(row.StateText));
        Assert.Equal("—", new RacSessionRow(new RacSessionInfo { State = "sleep" }).StartedAtText);
    }

    [Fact]
    public void SessionRow_StateColors_AndBlockedSymbol()
    {
        Assert.Equal("#DC2626", new RacSessionRow(new RacSessionInfo { State = "dead" }).StateColorHex);
        Assert.Equal("#D97706", new RacSessionRow(new RacSessionInfo { State = "wait" }).StateColorHex);
        Assert.Equal("#64748B", new RacSessionRow(new RacSessionInfo { State = "unknown" }).StateColorHex);
        Assert.Equal("●", new RacSessionRow(new RacSessionInfo { BlockedByLs = true }).BlockedSymbol);
        Assert.Equal("●", new RacSessionRow(new RacSessionInfo { BlockedByDeadlock = true }).BlockedSymbol);
        Assert.Equal(string.Empty, new RacSessionRow(new RacSessionInfo()).BlockedSymbol);
    }

    [Fact]
    public void ProcessRow_FormatsMemory_Cpu_Running()
    {
        var row = new RacProcessRow(new RacProcessInfo
        {
            Id = Guid.NewGuid(),
            Pid = 1234,
            MemorySize = 1024 * 1024 * 1024,
            Cpu = 12.5,
            Running = true,
            Threads = 8
        });

        Assert.Contains("1024", row.MemorySizeText);
        Assert.EndsWith(LocalizationManager.T("ServerMonitor.Mb"), row.MemorySizeText);
        Assert.Contains("12", row.CpuText);
        Assert.EndsWith("%", row.CpuText);
        Assert.Equal("#16A34A", row.StateColorHex);
        Assert.Equal(1234, row.Pid);
    }

    [Fact]
    public void ConnectionRow_FormatsDuration_Blocked()
    {
        var row = new RacConnectionRow(new RacConnectionInfo
        {
            Id = Guid.NewGuid(),
            Duration = 1800_000,
            Blocked = true
        });

        Assert.Equal("00:30:00", row.DurationText);
        Assert.Equal("#D97706", row.BlockedColorHex);
        Assert.Equal("—", new RacConnectionRow(new RacConnectionInfo()).EstablishedAtText);
    }

    // ---------- issue #324: автовыбор кластера, ошибка разбора, автообновление ----------

    [Fact]
    public async Task Connect_SingleCluster_AutoSelectedImmediately()
    {
        var vm = new ServerMonitorViewModel(new FakeRacClient(singleCluster: true), new RecordingDialogs());

        await vm.ConnectAsync();

        Assert.Single(vm.ClusterRows);
        Assert.Equal(FakeRacClient.FirstClusterId, vm.SelectedClusterId);
    }

    [Fact]
    public async Task LoadClusterData_ParseError_SetsErrorAndStopsAutoRefresh()
    {
        var vm = new ServerMonitorViewModel(new FakeRacClient(throwOnParse: true), new RecordingDialogs());
        await vm.ConnectAsync();
        // Фоновая загрузка первого кластера споткнётся об ошибку разбора; ждём её завершения
        // (перекрытие запросов исключено флагом занятости), затем явная загрузка.
        await Task.Delay(50);
        await vm.LoadClusterDataAsync(FakeRacClient.FirstClusterId);

        // Бесконечный ретрай каждые 5 с прекращён: таймер остановлен, ошибка показана.
        Assert.False(vm.AutoRefreshActive);
        Assert.False(string.IsNullOrEmpty(vm.ErrorMessage));
        Assert.False(string.IsNullOrEmpty(vm.StatusText));
    }

    [Fact]
    public async Task LoadClusterData_ParseErrorOnOneTab_OtherTabsStillFill()
    {
        // issue #324 (лог 7OH от 2026-10-08): rac вернул exit=0 и непустой stdout по всем
        // шести list-командам, но сбой разбора ОДНОЙ из них раньше проваливал общий
        // Task.WhenAll — и ВСЕ вкладки оставались пустыми. Теперь сбойная вкладка пуста,
        // остальные заполнены, ошибка перечислена, автообновление остановлено.
        var vm = new ServerMonitorViewModel(
            new FakeRacClient(throwParseOnSessions: true), new RecordingDialogs());

        await vm.LoadClusterDataAsync(FakeRacClient.FirstClusterId);

        Assert.Empty(vm.Sessions);            // сбойная вкладка
        Assert.NotEmpty(vm.Processes);        // остальные заполнены
        Assert.NotEmpty(vm.Connections);
        Assert.NotEmpty(vm.Locks);
        Assert.NotEmpty(vm.Jobs);
        Assert.NotNull(vm.ClusterInfo);
        // Сбойная вкладка названа в ошибке: текст исключения fake-клиента упоминает
        // команду session list (культуро-независимый фрагмент — не локализация).
        Assert.Contains("session list", vm.ErrorMessage);
        Assert.False(vm.AutoRefreshActive);
    }

    [Fact]
    public async Task SetAutoRefreshEnabled_TurnsOffAndOn_Timer()
    {
        var vm = new ServerMonitorViewModel(new FakeRacClient(), new RecordingDialogs());
        await vm.ConnectAsync();
        Assert.True(vm.AutoRefreshActive);

        vm.SetAutoRefreshEnabled(false);
        Assert.False(vm.AutoRefreshActive);
        Assert.False(vm.IsAutoRefreshEnabled);

        vm.SetAutoRefreshEnabled(true);
        Assert.True(vm.IsAutoRefreshEnabled);
        Assert.True(vm.AutoRefreshActive);
    }

    [Fact]
    public void AutoRefreshIntervalSeconds_ClampsToRange()
    {
        var vm = new ServerMonitorViewModel(new FakeRacClient(), new RecordingDialogs());

        vm.AutoRefreshIntervalSeconds = 0;
        Assert.Equal(1, vm.AutoRefreshIntervalSeconds);

        vm.AutoRefreshIntervalSeconds = 999;
        Assert.Equal(60, vm.AutoRefreshIntervalSeconds);

        vm.AutoRefreshIntervalSeconds = 10;
        Assert.Equal(10, vm.AutoRefreshIntervalSeconds);
    }

    // ===================== Fakes =====================

    /// <summary>Fake-клиент rac для тестов: два кластера, по одной строке данных.</summary>
    private sealed class FakeRacClient : IRacClient
    {
        public static readonly System.Guid FirstClusterId = System.Guid.Parse("11111111-1111-1111-1111-111111111111");
        private static readonly System.Guid SecondClusterId = System.Guid.Parse("22222222-2222-2222-2222-222222222222");

        /// <summary>Идентификатор регламентного задания «Обмен данными».</summary>
        public static readonly System.Guid FirstJobId = System.Guid.Parse("aaaaaaaa-1111-2222-3333-444455556666");

        /// <summary>Идентификатор регламентного задания без базы (служебное).</summary>
        public static readonly System.Guid SecondJobId = System.Guid.Parse("bbbbbbbb-1111-2222-3333-444455556666");

        /// <summary>Идентификатор информационной базы-владельца первого задания.</summary>
        public static readonly System.Guid FirstInfobaseId = System.Guid.Parse("cccccccc-1111-2222-3333-444455556666");

        private readonly bool _throwOnClusters;
        private readonly bool _throwOnAction;
        private readonly bool _actionFails;
        private readonly TimeSpan _delay;
        private readonly bool _throwOnParse;
        private readonly bool _singleCluster;
        private readonly bool _throwOnLoad;
        private readonly bool _throwParseOnSessions;

        public FakeRacClient(
            bool throwOnClusters = false, bool throwOnAction = false,
            bool actionFails = false, TimeSpan? delay = null,
            bool throwOnParse = false, bool singleCluster = false,
            bool throwOnLoad = false, bool throwParseOnSessions = false)
        {
            _throwOnClusters = throwOnClusters;
            _throwOnAction = throwOnAction;
            _actionFails = actionFails;
            _delay = delay ?? TimeSpan.Zero;
            _throwOnParse = throwOnParse;
            _singleCluster = singleCluster;
            _throwOnLoad = throwOnLoad;
            _throwParseOnSessions = throwParseOnSessions;
        }

        /// <summary>Текст последней ошибки действия (как в реальном клиенте).</summary>
        public string LastActionError { get; private set; } = string.Empty;

        /// <summary>Вызовы «session terminate»: (clusterId, sessionId).</summary>
        public List<(System.Guid clusterId, System.Guid sessionId)> TerminateCalls { get; } = new();

        /// <summary>Вызовы «connection disconnect»: (clusterId, connectionId).</summary>
        public List<(System.Guid clusterId, System.Guid connectionId)> DisconnectCalls { get; } = new();

        /// <summary>Параметры последнего вызова «cluster list» (проверка нормализации host:port).</summary>
        public RacConnectionParams? LastParams { get; private set; }

        /// <summary>Сколько раз запрошены рабочие процессы (для проверки флага занятости).</summary>
        public int ProcessCalls { get; private set; }

        /// <summary>Сколько раз запрошены регламентные задания.</summary>
        public int JobCalls { get; private set; }

        /// <summary>Вызовы «job pause/resume/disable/enable»: (clusterId, jobId, action).</summary>
        public List<(System.Guid clusterId, System.Guid jobId, RacJobAction action)> StateCalls { get; } = new();

        public Task<IReadOnlyList<RacCluster>> GetClustersAsync(
            RacConnectionParams parameters, CancellationToken cancellationToken = default)
        {
            LastParams = parameters;
            if (_throwOnClusters)
                throw new RacClientException("rac not found");
            if (_singleCluster)
            {
                return Task.FromResult<IReadOnlyList<RacCluster>>(new[]
                {
                    new RacCluster { Id = FirstClusterId, Name = "Главный кластер", Port = 1541 }
                });
            }
            return Task.FromResult<IReadOnlyList<RacCluster>>(new[]
            {
                new RacCluster { Id = FirstClusterId, Name = "Главный кластер", Port = 1541 },
                new RacCluster { Id = SecondClusterId, Name = "Второй", Port = 1542 }
            });
        }

        public Task<RacClusterInfo?> GetClusterInfoAsync(
            RacConnectionParams parameters, Guid clusterId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<RacClusterInfo?>(new RacClusterInfo
            {
                Name = "Главный кластер",
                HostName = "srv1",
                Port = 1541,
                Properties = new Dictionary<string, string>
                {
                    ["name"] = "Главный кластер",
                    ["hostName"] = "srv1",
                    ["port"] = "1541"
                }
            });
        }

        public async Task<IReadOnlyList<RacProcessInfo>> GetProcessesAsync(
            RacConnectionParams parameters, Guid clusterId, CancellationToken cancellationToken = default)
        {
            ProcessCalls++;
            if (_throwOnParse)
                throw new RacOutputParseException("тест: вывод rac не распознан");
            if (_throwOnLoad)
                throw new InvalidOperationException("тест: сетевая ошибка загрузки");
            if (_delay > TimeSpan.Zero)
                await Task.Delay(_delay, cancellationToken).ConfigureAwait(false);
            return new[]
            {
                new RacProcessInfo
                {
                    Id = System.Guid.NewGuid(),
                    Host = "srv1",
                    Pid = 1234,
                    Port = 1560,
                    StartedAt = new DateTime(2026, 9, 28, 10, 0, 0),
                    MemorySize = 512 * 1024 * 1024,
                    Threads = 8,
                    Cpu = 12.5,
                    Running = true,
                    Infobases = 3
                }
            };
        }

        public Task<IReadOnlyList<RacSessionInfo>> GetSessionsAsync(
            RacConnectionParams parameters, Guid clusterId, CancellationToken cancellationToken = default)
        {
            // issue #324: сбой разбора ОДНОЙ вкладки (сеансы) — остальные заполняются.
            if (_throwParseOnSessions)
                throw new RacOutputParseException("тест: вывод session list не распознан");

            return Task.FromResult<IReadOnlyList<RacSessionInfo>>(new[]
            {
                new RacSessionInfo
                {
                    Id = System.Guid.NewGuid(),
                    User = "Иванов",
                    Host = "client1",
                    AppId = "1CV8",
                    StartedAt = new DateTime(2026, 9, 28, 9, 30, 0),
                    LastActiveAt = new DateTime(2026, 9, 28, 10, 5, 0),
                    State = "active",
                    Memory = 256 * 1024 * 1024,
                    DurationAll = 3600_000
                }
            });
        }

        public Task<IReadOnlyList<RacConnectionInfo>> GetConnectionsAsync(
            RacConnectionParams parameters, Guid clusterId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<RacConnectionInfo>>(new[]
            {
                new RacConnectionInfo
                {
                    Id = System.Guid.NewGuid(),
                    SessionId = System.Guid.NewGuid(),
                    Connector = "1CV8",
                    Host = "client1",
                    Port = 52000,
                    EstablishedAt = new DateTime(2026, 9, 28, 9, 30, 0),
                    Duration = 1800_000
                }
            });
        }

        public Task<IReadOnlyList<RacLockInfo>> GetLocksAsync(
            RacConnectionParams parameters, Guid clusterId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<RacLockInfo>>(new[]
            {
                new RacLockInfo
                {
                    Id = System.Guid.NewGuid(),
                    SessionId = System.Guid.NewGuid(),
                    ConnectionId = System.Guid.NewGuid(),
                    TransactionId = System.Guid.NewGuid(),
                    Waiting = true,
                    Blocking = false,
                    Object = "Справочник.Номенклатура"
                }
            });
        }

        // Базы кластера: одна база-владелец для маппинга имён заданий и фильтра.
        public Task<IReadOnlyList<RacInfobaseSummary>> GetInfobasesAsync(
            RacConnectionParams parameters, Guid clusterId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<RacInfobaseSummary>>(new[]
            {
                new RacInfobaseSummary { InfobaseId = FirstInfobaseId, Name = "Бухгалтерия" }
            });
        }

        public Task<IReadOnlyList<RacJobInfo>> GetJobsAsync(
            RacConnectionParams parameters, Guid clusterId, CancellationToken cancellationToken = default)
        {
            JobCalls++;
            return Task.FromResult<IReadOnlyList<RacJobInfo>>(new[]
            {
                new RacJobInfo
                {
                    Id = FirstJobId,
                    InfobaseId = FirstInfobaseId,
                    Name = "Обмен данными",
                    MethodName = "ВыполнитьОбмен",
                    Predefined = true,
                    Schedule = "0 0 3 * * ?",
                    State = "scheduled",
                    NextStart = new DateTime(2026, 10, 1, 3, 0, 0),
                    LastStart = new DateTime(2026, 9, 30, 3, 0, 0),
                    LastEnd = new DateTime(2026, 9, 30, 3, 10, 0),
                    LastSuccess = true
                },
                new RacJobInfo
                {
                    Id = SecondJobId,
                    InfobaseId = null,
                    Name = "Служебное задание",
                    MethodName = "СлужебныйМетод",
                    State = "paused"
                }
            });
        }

        public Task<bool> SetJobStateAsync(
            RacConnectionParams parameters, Guid clusterId, Guid jobId, RacJobAction action,
            CancellationToken cancellationToken = default)
        {
            StateCalls.Add((clusterId, jobId, action));
            if (_throwOnAction)
                throw new RacClientException("нет прав администратора");
            if (_actionFails)
            {
                LastActionError = "rac: у пользователя нет прав на изменение состояния задания";
                return Task.FromResult(false);
            }
            return Task.FromResult(true);
        }

        public Task<bool> TerminateSessionAsync(
            RacConnectionParams parameters, Guid clusterId, Guid sessionId,
            CancellationToken cancellationToken = default)
        {
            TerminateCalls.Add((clusterId, sessionId));
            if (_throwOnAction)
                throw new RacClientException("нет прав администратора");
            if (_actionFails)
            {
                LastActionError = "rac: у пользователя нет прав на завершение сеанса";
                return Task.FromResult(false);
            }
            return Task.FromResult(true);
        }

        public Task<bool> DisconnectConnectionAsync(
            RacConnectionParams parameters, Guid clusterId, Guid connectionId,
            CancellationToken cancellationToken = default)
        {
            DisconnectCalls.Add((clusterId, connectionId));
            if (_throwOnAction)
                throw new RacClientException("нет прав администратора");
            if (_actionFails)
            {
                LastActionError = "rac: у пользователя нет прав на разрыв соединения";
                return Task.FromResult(false);
            }
            return Task.FromResult(true);
        }
    }

    /// <summary>
    /// Запись диалогов для тестов: методы фиксируют вызовы (подтверждения, предупреждения),
    /// ничего не показывая; результат подтверждения настраивается (<see cref="ConfirmResult"/>).
    /// </summary>
    private sealed class RecordingDialogs : IDialogService
    {
        public bool ConfirmResult { get; set; } = true;

        public List<(string message, string title)> Confirms { get; } = new();
        public List<(string message, string title)> Warnings { get; } = new();

        public void ShowInfo(string message, string title = "") { }
        public void ShowError(string message, string title = "") { }
        public void ShowWarning(string message, string title = "") => Warnings.Add((message, title));
        public bool Confirm(string message, string title = "")
        {
            Confirms.Add((message, title));
            return ConfirmResult;
        }
        public string? OpenFileDialog(string title = "", string filter = "", string? initialDirectory = null) => null;
        public string? SaveFileDialog(string title = "", string defaultFileName = "", string filter = "", string? initialDirectory = null) => null;
        public string? OpenFolderDialog(string title = "", string? initialDirectory = null) => null;
    }
}