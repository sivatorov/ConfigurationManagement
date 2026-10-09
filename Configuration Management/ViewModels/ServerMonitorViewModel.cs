using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Windows.Input;
using Configuration_Management.Localization;
using Configuration_Management.Models;
using Configuration_Management.Services;

namespace Configuration_Management.ViewModels;

/// <summary>
/// ViewModel окна «Серверы 1С» (0.3.9.124, цикл 0.3.9.123–0.3.9.126): подключение
/// к серверу 1С через rac (адрес/порт/логин/пароль), список кластеров, вкладки
/// «Рабочие процессы / Сеансы / Соединения / Блокировки / Информация о кластере»,
/// ручное обновление, действия (завершение сеанса / разрыв соединения с
/// подтверждением) и автообновление по таймеру 5 с (этап 3, 0.3.9.125).
/// Чистый .NET без платформенных зависимостей — обе платформы (WPF и Avalonia);
/// окна только привязываются.
/// </summary>
public sealed class ServerMonitorViewModel : ViewModelBase, IDisposable
{
    /// <summary>Период автообновления данных кластера, миллисекунды (~5 секунд).</summary>
    public const int AutoRefreshIntervalMs = 5000;

    private readonly IRacClient _rac;
    private readonly IDialogService _dialogs;
    private readonly Action<Action>? _dispatchToUi;
    private Timer? _autoRefreshTimer;
    private int _busy;

    private string _serverAddress = "localhost";
    private int _serverPort = IRacClient.DefaultPort;
    private string _userName = string.Empty;
    private string _password = string.Empty;
    private Guid? _selectedClusterId;
    private string _statusText = string.Empty;
    private string _errorMessage = string.Empty;
    private bool _hasConnected;
    private RacClusterInfo? _clusterInfo;
    private string _clusterInfoText = string.Empty;
    private RacSessionRow? _selectedSession;
    private RacConnectionRow? _selectedConnection;
    private RacJobRow? _selectedJob;
    private Guid? _selectedJobInfobaseId;

    private ICommand? _connectCommand;
    private ICommand? _refreshCommand;
    private ICommand? _terminateSessionCommand;
    private ICommand? _disconnectConnectionCommand;
    private ICommand? _pauseJobCommand;
    private ICommand? _resumeJobCommand;
    private ICommand? _toggleAutoRefreshCommand;
    private ICommand? _saveClusterPropertiesCommand;

    /// <summary>Переключатель автообновления (issue #324): включено по умолчанию.</summary>
    private bool _isAutoRefreshEnabled = true;

    /// <summary>Интервал автообновления, секунды (по умолчанию 5; диапазон 1–60).</summary>
    private int _autoRefreshIntervalSeconds = AutoRefreshIntervalMs / 1000;

    /// <summary>
    /// Кэш имён информационных баз выбранного кластера (GUID из «job list» → имя из
    /// «infobase summary list»). Заполняется при загрузке данных кластера; задания без
    /// базы и неизвестные GUID показываются как «—».
    /// </summary>
    private readonly Dictionary<Guid, string> _infobaseNames = new();

    /// <param name="rac">Клиент rac (список кластеров, данные кластера).</param>
    /// <param name="dialogs">Диалоги (сообщения об ошибках; подтверждения действий — этап 3).</param>
    /// <param name="dispatchToUi">
    /// Доставка применения результатов в UI-поток (передаёт окно); null — результаты
    /// применяются прямо из рабочего потока (тесты).
    /// </param>
    public ServerMonitorViewModel(
        IRacClient rac,
        IDialogService dialogs,
        Action<Action>? dispatchToUi = null)
    {
        _rac = rac ?? throw new ArgumentNullException(nameof(rac));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _dispatchToUi = dispatchToUi;
    }

    // ===================== Подключение =====================

    /// <summary>
    /// Адрес сервера 1С (host или IP). Нестандартный порт можно указать полем
    /// <see cref="ServerPort"/> или прямо здесь как «host:port» (например «localhost:27545»,
    /// как в командной строке rac) — порт из адреса имеет приоритет (issue #324).
    /// </summary>
    public string ServerAddress
    {
        get => _serverAddress;
        set => SetProperty(ref _serverAddress, value?.Trim() ?? string.Empty);
    }

    /// <summary>
    /// Порт агента сервера 1С (ragent), по умолчанию 1540; для RAS — 1545.
    /// Некорректные значения (<see cref="IRacClient.DefaultPort"/> при ≤ 0).
    /// Если порт указан в <see cref="ServerAddress"/> как «host:port» — приоритет у него.
    /// </summary>
    public int ServerPort
    {
        get => _serverPort;
        set => SetProperty(ref _serverPort, value > 0 ? value : IRacClient.DefaultPort);
    }

    /// <summary>Логин администратора кластера (пустая строка — без аутентификации).</summary>
    public string UserName
    {
        get => _userName;
        set => SetProperty(ref _userName, value ?? string.Empty);
    }

    /// <summary>
    /// Пароль администратора кластера. Живёт ТОЛЬКО в памяти окна (в тестах сеттер
    /// открытый; в UI пароль передаётся вручную из PasswordBox при подключении),
    /// на диск не сохраняется (см. решения планирования, раздел 5 плана).
    /// </summary>
    public string Password
    {
        get => _password;
        set => SetProperty(ref _password, value ?? string.Empty);
    }

    // ===================== Кластеры =====================

    /// <summary>Кластеры сервера (модели из rac «cluster list»).</summary>
    public IReadOnlyList<RacCluster> Clusters { get; private set; } = Array.Empty<RacCluster>();

    /// <summary>Кластеры для выпадающего списка (форматированные строки).</summary>
    public IReadOnlyList<RacClusterRow> ClusterRows { get; private set; } = Array.Empty<RacClusterRow>();

    /// <summary>
    /// Выбранный кластер. При установке значения после подключения запускается
    /// загрузка данных кластера (процессы/сеансы/соединения/блокировки/инфо).
    /// </summary>
    public Guid? SelectedClusterId
    {
        get => _selectedClusterId;
        set
        {
            if (!SetProperty(ref _selectedClusterId, value))
                return;

            // Смена кластера делает фильтр по базе бессмысленным (идентификаторы
            // баз другого кластера) — сбрасываем на «Все базы» и очищаем кэш имён.
            if (value != _selectedJobInfobaseId)
            {
                _selectedJobInfobaseId = null;
                OnPropertyChanged(nameof(SelectedJobInfobaseId));
            }

            if (value is Guid id && HasConnected)
                _ = LoadClusterDataAsync(id);
        }
    }

    // ===================== Команды =====================

    /// <summary>«Подключиться»: список кластеров, при успехе — данные первого кластера.</summary>
    public ICommand ConnectCommand =>
        _connectCommand ??= new RelayCommand(async () => await ConnectAsync());

    /// <summary>«Обновить»: перечитать данные выбранного кластера (после подключения).</summary>
    public ICommand RefreshCommand =>
        _refreshCommand ??= new RelayCommand(Refresh);

    // ===================== Действия =====================

    /// <summary>«Завершить сеанс»: подтверждение → session terminate → обновление списков.</summary>
    public ICommand TerminateSessionCommand =>
        _terminateSessionCommand ??= new RelayCommand(async () => await TerminateSessionAsync());

    /// <summary>«Разорвать соединение»: подтверждение → connection disconnect → обновление списков.</summary>
    public ICommand DisconnectConnectionCommand =>
        _disconnectConnectionCommand ??= new RelayCommand(async () => await DisconnectConnectionAsync());

    /// <summary>«Приостановить»: подтверждение → job pause → обновление списков.</summary>
    public ICommand PauseJobCommand =>
        _pauseJobCommand ??= new RelayCommand(async () => await PauseSelectedJobAsync());

    /// <summary>«Возобновить»: подтверждение → job resume → обновление списков.</summary>
    public ICommand ResumeJobCommand =>
        _resumeJobCommand ??= new RelayCommand(async () => await ResumeSelectedJobAsync());

    // ===================== Вкладки =====================

    /// <summary>Рабочие процессы кластера (rphost/rmngr).</summary>
    public ObservableCollection<RacProcessRow> Processes { get; } = new();

    /// <summary>Сеансы пользователей кластера.</summary>
    public ObservableCollection<RacSessionRow> Sessions { get; } = new();

    /// <summary>Соединения клиентов кластера.</summary>
    public ObservableCollection<RacConnectionRow> Connections { get; } = new();

    /// <summary>Блокировки объектов данных кластера.</summary>
    public ObservableCollection<RacLockRow> Locks { get; } = new();

    /// <summary>Регламентные задания кластера (все, до фильтра по базе).</summary>
    public ObservableCollection<RacJobRow> Jobs { get; } = new();

    /// <summary>Регламентные задания с учётом фильтра по базе (таблица биндится сюда).</summary>
    public ObservableCollection<RacJobRow> FilteredJobs { get; } = new();

    /// <summary>
    /// Свойства кластера «свойство — значение» на вкладке «Информация о кластере»
    /// (issue #324, C3): ограниченный набор параметров доступен для правки
    /// (см. <see cref="RacClusterPropertyRow.IsEditable"/>), остальные — только чтение.
    /// </summary>
    public ObservableCollection<RacClusterPropertyRow> ClusterProperties { get; } = new();

    /// <summary>«Сохранить изменения» на вкладке «Информация о кластере» (rac «cluster update»).</summary>
    public ICommand SaveClusterPropertiesCommand =>
        _saveClusterPropertiesCommand ??= new RelayCommand(async () => await SaveClusterPropertiesAsync());

    /// <summary>Есть ли хотя бы одно задание (для индикатора пустого списка).</summary>
    public bool HasJobs => Jobs.Count > 0;

    /// <summary>Строки фильтра «по базе»: «Все базы» + информационные базы кластера.</summary>
    public IReadOnlyList<RacJobFilterRow> JobInfobaseFilterRows { get; private set; } =
        Array.Empty<RacJobFilterRow>();

    /// <summary>Информация о кластере (команда «cluster info»); null, если не получена.</summary>
    public RacClusterInfo? ClusterInfo
    {
        get => _clusterInfo;
        private set => SetProperty(ref _clusterInfo, value);
    }

    /// <summary>Текст вкладки «Информация о кластере» («ключ: значение» построчно).</summary>
    public string ClusterInfoText
    {
        get => _clusterInfoText;
        private set => SetProperty(ref _clusterInfoText, value);
    }

    /// <summary>Выбранный сеанс на вкладке «Сеансы» (кнопка «Завершить сеанс»).</summary>
    public RacSessionRow? SelectedSession
    {
        get => _selectedSession;
        set => SetProperty(ref _selectedSession, value);
    }

    /// <summary>Выбранное соединение на вкладке «Соединения» (кнопка «Разорвать соединение»).</summary>
    public RacConnectionRow? SelectedConnection
    {
        get => _selectedConnection;
        set => SetProperty(ref _selectedConnection, value);
    }

    /// <summary>Выбранное регламентное задание на вкладке «Регламентные задания».</summary>
    public RacJobRow? SelectedJob
    {
        get => _selectedJob;
        set
        {
            if (!SetProperty(ref _selectedJob, value))
                return;
            // Кнопки «Приостановить/Возобновить» зависят от состояния выбранного задания.
            OnPropertyChanged(nameof(CanPauseSelectedJob));
            OnPropertyChanged(nameof(CanResumeSelectedJob));
        }
    }

    /// <summary>
    /// Фильтр «по базе»: null — все задания, GUID — только задания выбранной базы.
    /// При изменении пересобирается <see cref="FilteredJobs"/>.
    /// </summary>
    public Guid? SelectedJobInfobaseId
    {
        get => _selectedJobInfobaseId;
        set
        {
            if (!SetProperty(ref _selectedJobInfobaseId, value))
                return;
            ApplyJobFilter();
        }
    }

    /// <summary>Разрешено ли «Приостановить» для выбранного задания.</summary>
    public bool CanPauseSelectedJob => SelectedJob?.CanPause ?? false;

    /// <summary>Разрешено ли «Возобновить» для выбранного задания.</summary>
    public bool CanResumeSelectedJob => SelectedJob?.CanResume ?? false;

    // ===================== Автообновление =====================

    /// <summary>Запущен ли таймер автообновления (после успешного подключения).</summary>
    public bool AutoRefreshActive => _autoRefreshTimer is not null;

    /// <summary>Переключатель автообновления (issue #324): вкл/выкл на форме монитора.</summary>
    public bool IsAutoRefreshEnabled
    {
        get => _isAutoRefreshEnabled;
        set => SetAutoRefreshEnabled(value);
    }

    /// <summary>
    /// Интервал автообновления, секунды (1–60; по умолчанию 5). Смена значения при работающем
    /// таймере перезапускает его (issue #324, комментарий 17/18: «как отключить Автообновления»).
    /// </summary>
    public int AutoRefreshIntervalSeconds
    {
        get => _autoRefreshIntervalSeconds;
        set
        {
            var clamped = Math.Clamp(value, 1, 60);
            if (!SetProperty(ref _autoRefreshIntervalSeconds, clamped))
                return;
            if (_autoRefreshTimer is not null && IsAutoRefreshEnabled)
            {
                StopAutoRefresh();
                StartAutoRefresh();
            }
        }
    }

    /// <summary>Команда переключения автообновления (вкл/выкл).</summary>
    public ICommand ToggleAutoRefreshCommand =>
        _toggleAutoRefreshCommand ??= new RelayCommand(() => SetAutoRefreshEnabled(!IsAutoRefreshEnabled));

    /// <summary>
    /// Включает/выключает автообновление. Выключение останавливает таймер; включение
    /// запускает его только при установленном подключении (ручное «Обновить» доступно всегда).
    /// </summary>
    public void SetAutoRefreshEnabled(bool enabled)
    {
        if (!SetProperty(ref _isAutoRefreshEnabled, enabled))
            return;

        if (enabled)
        {
            if (HasConnected && _autoRefreshTimer is null)
                StartAutoRefresh();
        }
        else
        {
            StopAutoRefresh();
        }
        // Статусная строка внизу окна различает «выключено переключателем» и
        // «остановлено после ошибки» (issue #324): при переключении она обязана
        // обновиться даже если таймер не стартовал/не останавливался.
        OnPropertyChanged(nameof(AutoRefreshText));
    }

    /// <summary>
    /// Подпись состояния автообновления для статусной строки окна (issue #324):
    /// «вкл (N с)» — таймер реально работает; «выкл» — переключатель снят;
    /// «остановлено (ошибка)» — переключатель включён, но таймер остановлен из-за
    /// сбоя загрузки данных (показывает ФАКТИЧЕСКОЕ состояние, а не положение галки).
    /// </summary>
    public string AutoRefreshText
    {
        get
        {
            if (!IsAutoRefreshEnabled)
                return LocalizationManager.T("ServerMonitor.AutoRefreshOff");
            return AutoRefreshActive
                ? string.Format(LocalizationManager.T("ServerMonitor.AutoRefreshOnFormat"), AutoRefreshIntervalSeconds)
                : LocalizationManager.T("ServerMonitor.AutoRefreshStopped");
        }
    }
    // ===================== Статус =====================

    /// <summary>Строка состояния («Подключение…», «Кластеров: N», ошибки).</summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>Подробное сообщение последней ошибки (для статус-строки и диалога).</summary>
    public string ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    /// <summary>Успешно ли установлено подключение (список кластеров получен).</summary>
    public bool HasConnected
    {
        get => _hasConnected;
        private set => SetProperty(ref _hasConnected, value);
    }

    /// <summary>Выполняется ли сейчас запрос (флаг занятости исключает наложение, см. ProcessInspectorViewModel).</summary>
    public bool IsBusy => _busy == 1;

    // ===================== Действия =====================

    /// <summary>
    /// «Подключиться»: получить список кластеров (rac «cluster list»); при успехе —
    /// выбрать первый кластер и загрузить его данные. Ошибки не роняют окно —
    /// пишутся в статус-строку/ErrorMessage.
    /// </summary>
    public async Task ConnectAsync()
    {
        if (!TryEnterBusy())
            return;

        Guid? initialClusterId = null;
        try
        {
            ErrorMessage = string.Empty;
            StatusText = LocalizationManager.T("ServerMonitor.Status.Connecting");

            var clusters = await _rac.GetClustersAsync(BuildParams()).ConfigureAwait(false);

            ApplyClusters(clusters);
            HasConnected = true;
            StatusText = clusters.Count == 0
                ? LocalizationManager.T("ServerMonitor.Status.NoClusters")
                : string.Format(LocalizationManager.T("ServerMonitor.Status.ConnectedFormat"), clusters.Count);

            // Автообновление запускается только после успешного подключения И при
            // включённом переключателе: галку могли снять ДО подключения — таймер
            // в этом случае не должен стартовать (issue #324, комментарий 7OH).
            if (IsAutoRefreshEnabled)
                StartAutoRefresh();

            // Первый кластер выбираем автоматически. Сам выбор (и запускаемая им
            // загрузка данных) делаем ПОСЛЕ выхода из busy: назначение внутри
            // ConnectAsync молча пропускало LoadClusterDataAsync через TryEnterBusy,
            // и данные приходили только через таймер ~5 с спустя (issue #324:
            // «подключение локально занимает почти 5 секунд»).
            if (SelectedClusterId is null && ClusterRows.Count > 0)
                initialClusterId = ClusterRows[0].Id;
        }
        catch (Exception ex)
        {
            HasConnected = false;
            // issue #324: в сообщении об ошибке указываем ЦЕЛЕВОЙ адрес:порт, к которому
            // шло подключение (поля могли быть заполнены сохранёнными значениями, и
            // пользователю должно быть видно, куда именно «ушёл» запрос), а не только
            // текст rac, который при повторном нажатии может отличаться.
            var (host, port) = RacConnectionAddress.Split(ServerAddress, ServerPort);
            ErrorMessage = string.Format(
                LocalizationManager.T("ServerMonitor.Status.ConnectFailedDetail"),
                host, port, BuildErrorMessage(ex));
            StatusText = LocalizationManager.T("ServerMonitor.Status.ConnectFailed");
            // Без подключения таймер автообновления не работает.
            StopAutoRefresh();
        }
        finally
        {
            ExitBusy();
        }

        // busy освобождён — запускам первичную загрузку данных выбранного кластера.
        // Важно: ApplyClusters при единственном кластере выбирает его ДО HasConnected=true,
        // и сеттер тогда не запускает загрузку; без этого блока данные приходили бы
        // только от таймера ~5 с спустя (issue #324).
        if (SelectedClusterId is { } clusterId && HasConnected && _busy == 0)
            _ = LoadClusterDataAsync(clusterId);
        else if (initialClusterId is { } initialId)
            SelectedClusterId = initialId;
    }

    /// <summary>
    /// Загружает данные выбранного кластера: процессы, сеансы, соединения, блокировки,
    /// регламентные задания, информационные базы (для имён владельцев заданий) и
    /// «cluster info» (параллельно). Результаты применяются через <see cref="_dispatchToUi"/>
    /// (null — напрямую, тесты). Каждая вкладка загружается ИЗОЛИРОВАННО (issue #324,
    /// лог 7OH от 2026-10-08): сбой ОДНОЙ rac-команды (например, нераспознанная схема
    /// вывода при exit=0 и непустом stdout) больше не проваливает общий
    /// <see cref="Task.WhenAll"/> — остальные вкладки заполняются тем, что удалось
    /// разобрать, а сбойные перечисляются в <see cref="ErrorMessage"/>; автообновление
    /// останавливается, ручное «Обновить» остаётся доступным.
    /// </summary>
    public async Task LoadClusterDataAsync(Guid clusterId, CancellationToken cancellationToken = default)
    {
        if (!TryEnterBusy())
            return;

        try
        {
            ErrorMessage = string.Empty;
            StatusText = LocalizationManager.T("ServerMonitor.Status.Loading");

            var parameters = BuildParams();
            var errors = new List<string>();
            var hasParseError = false;

            // Изолированная загрузка одной вкладки: исключение не роняет общий цикл.
            async Task<IReadOnlyList<T>> LoadTabAsync<T>(
                string tabLabel, Func<Task<IReadOnlyList<T>>> load)
            {
                try
                {
                    return await load().ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (RacOutputParseException ex)
                {
                    hasParseError = true;
                    errors.Add($"{tabLabel}: {BuildErrorMessage(ex)}");
                    return Array.Empty<T>();
                }
                catch (Exception ex)
                {
                    errors.Add($"{tabLabel}: {BuildErrorMessage(ex)}");
                    return Array.Empty<T>();
                }
            }

            async Task<RacClusterInfo?> LoadInfoAsync()
            {
                try
                {
                    return await _rac.GetClusterInfoAsync(parameters, clusterId, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    errors.Add(
                        $"{LocalizationManager.T("ServerMonitor.Tabs.Info")}: {BuildErrorMessage(ex)}");
                    return null;
                }
            }

            var processesTask = LoadTabAsync(
                LocalizationManager.T("ServerMonitor.Tabs.Processes"),
                () => _rac.GetProcessesAsync(parameters, clusterId, cancellationToken));
            var sessionsTask = LoadTabAsync(
                LocalizationManager.T("ServerMonitor.Tabs.Sessions"),
                () => _rac.GetSessionsAsync(parameters, clusterId, cancellationToken));
            var connectionsTask = LoadTabAsync(
                LocalizationManager.T("ServerMonitor.Tabs.Connections"),
                () => _rac.GetConnectionsAsync(parameters, clusterId, cancellationToken));
            var locksTask = LoadTabAsync(
                LocalizationManager.T("ServerMonitor.Tabs.Locks"),
                () => _rac.GetLocksAsync(parameters, clusterId, cancellationToken));
            var jobsTask = LoadTabAsync(
                LocalizationManager.T("ServerMonitor.Tabs.Jobs"),
                () => _rac.GetJobsAsync(parameters, clusterId, cancellationToken));
            var infobasesTask = LoadTabAsync(
                LocalizationManager.T("ServerMonitor.Tabs.Jobs"),
                () => _rac.GetInfobasesAsync(parameters, clusterId, cancellationToken));
            var infoTask = LoadInfoAsync();

            await Task.WhenAll(
                processesTask, sessionsTask, connectionsTask, locksTask, jobsTask, infobasesTask, infoTask)
                .ConfigureAwait(false);

            var processes = await processesTask.ConfigureAwait(false);
            var sessions = await sessionsTask.ConfigureAwait(false);
            var connections = await connectionsTask.ConfigureAwait(false);
            var locks = await locksTask.ConfigureAwait(false);
            var jobs = await jobsTask.ConfigureAwait(false);
            var infobases = await infobasesTask.ConfigureAwait(false);
            var info = await infoTask.ConfigureAwait(false);

            ApplyClusterData(processes, sessions, connections, locks, jobs, infobases, info);

            if (errors.Count > 0)
            {
                // Часть данных не загружена (issue #324): сбойные вкладки перечислены
                // в ошибке, остальные заполнены. Автообновление останавливается — как
                // и раньше при полной ошибке; ручное «Обновить» остаётся доступным,
                // после успеха таймер возобновится.
                ErrorMessage = LocalizationManager.T("ServerMonitor.Status.PartialLoad") +
                               "\n" + string.Join("\n", errors);
                StatusText = hasParseError
                    ? LocalizationManager.T("ServerMonitor.Status.ParseFailed")
                    : LocalizationManager.T("ServerMonitor.Status.LoadFailed");
                StopAutoRefresh();
            }
            else
            {
                StatusText = string.Format(
                    LocalizationManager.T("ServerMonitor.Status.LoadedFormat"),
                    processes.Count, sessions.Count, connections.Count, locks.Count, jobs.Count);

                // issue #324: после УСПЕШНОЙ загрузки автообновление возобновляется, если
                // переключатель включён (например, ручное «Обновить» после ошибки разбора).
                if (IsAutoRefreshEnabled && _autoRefreshTimer is null)
                    StartAutoRefresh();
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = LocalizationManager.T("ServerMonitor.Status.Cancelled");
        }
        catch (Exception ex)
        {
            // issue #324: любая ошибка загрузки останавливает таймер автообновления
            // (бесконечный ретрай каждые 5 с после разрыва соединения не нужен —
            // «после ошибки продолжает пытаться получить данные»). Ручное «Обновить»
            // остаётся доступным; после успешной загрузки таймер возобновляется,
            // если переключатель включён.
            ErrorMessage = BuildErrorMessage(ex);
            StatusText = LocalizationManager.T("ServerMonitor.Status.LoadFailed");
            StopAutoRefresh();
        }
        finally
        {
            ExitBusy();
        }
    }

    /// <summary>«Обновить»: перечитать данные выбранного кластера (без подключения — no-op).</summary>
    public void Refresh()
    {
        if (!HasConnected || SelectedClusterId is not Guid id)
            return;
        _ = LoadClusterDataAsync(id);
    }

    /// <summary>
    /// «Завершить сеанс»: подтверждение (предупреждение о потере несохранённых данных),
    /// команда rac «session terminate», обновление списков; ошибка — предупреждение +
    /// статус-строка. Образец — KillSelected из ProcessInspectorViewModel.
    /// </summary>
    public async Task TerminateSessionAsync()
    {
        var row = SelectedSession;
        if (row is null || !HasConnected || SelectedClusterId is not Guid clusterId)
            return;

        if (!_dialogs.Confirm(
                string.Format(LocalizationManager.T("ServerMonitor.TerminateConfirmFormat"), row.User),
                LocalizationManager.T("ServerMonitor.TerminateTitle")))
            return;

        try
        {
            var ok = await _rac.TerminateSessionAsync(BuildParams(), clusterId, row.Id).ConfigureAwait(false);
            if (!ok)
            {
                var detail = BuildActionError(_rac.LastActionError);
                _dialogs.ShowWarning(
                    string.Format(LocalizationManager.T("ServerMonitor.TerminateFailedFormat"), row.User) + "\n" + detail,
                    LocalizationManager.T("ServerMonitor.TerminateTitle"));
                StatusText = detail;
                return;
            }

            StatusText = string.Format(
                LocalizationManager.T("ServerMonitor.Status.TerminatedFormat"), row.User);
        }
        catch (Exception ex)
        {
            _dialogs.ShowWarning(
                string.Format(LocalizationManager.T("ServerMonitor.TerminateFailedFormat"), row.User) + "\n" + BuildErrorMessage(ex),
                LocalizationManager.T("ServerMonitor.TerminateTitle"));
            StatusText = BuildErrorMessage(ex);
        }
        finally
        {
            // После действия списки перечитываются (сеанс мог исчезнуть).
            Refresh();
        }
    }

    /// <summary>
    /// «Разорвать соединение»: подтверждение, команда rac «connection disconnect»,
    /// обновление списков; ошибка — предупреждение + статус-строка.
    /// </summary>
    public async Task DisconnectConnectionAsync()
    {
        var row = SelectedConnection;
        if (row is null || !HasConnected || SelectedClusterId is not Guid clusterId)
            return;

        if (!_dialogs.Confirm(
                string.Format(LocalizationManager.T("ServerMonitor.DisconnectConfirmFormat"), row.Host),
                LocalizationManager.T("ServerMonitor.DisconnectTitle")))
            return;

        try
        {
            var ok = await _rac.DisconnectConnectionAsync(BuildParams(), clusterId, row.Id).ConfigureAwait(false);
            if (!ok)
            {
                var detail = BuildActionError(_rac.LastActionError);
                _dialogs.ShowWarning(
                    string.Format(LocalizationManager.T("ServerMonitor.DisconnectFailedFormat"), row.Host) + "\n" + detail,
                    LocalizationManager.T("ServerMonitor.DisconnectTitle"));
                StatusText = detail;
                return;
            }

            StatusText = string.Format(
                LocalizationManager.T("ServerMonitor.Status.DisconnectedFormat"), row.Host);
        }
        catch (Exception ex)
        {
            _dialogs.ShowWarning(
                string.Format(LocalizationManager.T("ServerMonitor.DisconnectFailedFormat"), row.Host) + "\n" + BuildErrorMessage(ex),
                LocalizationManager.T("ServerMonitor.DisconnectTitle"));
            StatusText = BuildErrorMessage(ex);
        }
        finally
        {
            Refresh();
        }
    }

    /// <summary>
    /// «Приостановить»: подтверждение, команда rac «job pause», обновление списков;
    /// ошибка — предупреждение + статус-строка. Образец — <see cref="TerminateSessionAsync"/>.
    /// </summary>
    public async Task PauseSelectedJobAsync()
    {
        var row = SelectedJob;
        if (row is null || !row.CanPause || !HasConnected || SelectedClusterId is not Guid clusterId)
            return;

        if (!_dialogs.Confirm(
                string.Format(LocalizationManager.T("ServerMonitor.Job.PauseConfirmFormat"), row.Name),
                LocalizationManager.T("ServerMonitor.Job.PauseTitle")))
            return;

        try
        {
            var ok = await _rac.SetJobStateAsync(BuildParams(), clusterId, row.Id, RacJobAction.Pause)
                .ConfigureAwait(false);
            if (!ok)
            {
                var detail = BuildActionError(_rac.LastActionError);
                _dialogs.ShowWarning(
                    string.Format(LocalizationManager.T("ServerMonitor.Job.PauseFailedFormat"), row.Name) + "\n" + detail,
                    LocalizationManager.T("ServerMonitor.Job.PauseTitle"));
                StatusText = detail;
                return;
            }

            StatusText = string.Format(
                LocalizationManager.T("ServerMonitor.Job.Status.PausedFormat"), row.Name);
        }
        catch (Exception ex)
        {
            _dialogs.ShowWarning(
                string.Format(LocalizationManager.T("ServerMonitor.Job.PauseFailedFormat"), row.Name) + "\n" + BuildErrorMessage(ex),
                LocalizationManager.T("ServerMonitor.Job.PauseTitle"));
            StatusText = BuildErrorMessage(ex);
        }
        finally
        {
            // После действия состояние задания могло измениться — списки перечитываются.
            Refresh();
        }
    }

    /// <summary>
    /// «Возобновить»: подтверждение, команда rac «job resume», обновление списков;
    /// ошибка — предупреждение + статус-строка.
    /// </summary>
    public async Task ResumeSelectedJobAsync()
    {
        var row = SelectedJob;
        if (row is null || !row.CanResume || !HasConnected || SelectedClusterId is not Guid clusterId)
            return;

        if (!_dialogs.Confirm(
                string.Format(LocalizationManager.T("ServerMonitor.Job.ResumeConfirmFormat"), row.Name),
                LocalizationManager.T("ServerMonitor.Job.ResumeTitle")))
            return;

        try
        {
            var ok = await _rac.SetJobStateAsync(BuildParams(), clusterId, row.Id, RacJobAction.Resume)
                .ConfigureAwait(false);
            if (!ok)
            {
                var detail = BuildActionError(_rac.LastActionError);
                _dialogs.ShowWarning(
                    string.Format(LocalizationManager.T("ServerMonitor.Job.ResumeFailedFormat"), row.Name) + "\n" + detail,
                    LocalizationManager.T("ServerMonitor.Job.ResumeTitle"));
                StatusText = detail;
                return;
            }

            StatusText = string.Format(
                LocalizationManager.T("ServerMonitor.Job.Status.ResumedFormat"), row.Name);
        }
        catch (Exception ex)
        {
            _dialogs.ShowWarning(
                string.Format(LocalizationManager.T("ServerMonitor.Job.ResumeFailedFormat"), row.Name) + "\n" + BuildErrorMessage(ex),
                LocalizationManager.T("ServerMonitor.Job.ResumeTitle"));
            StatusText = BuildErrorMessage(ex);
        }
        finally
        {
            Refresh();
        }
    }

    /// <summary>
    /// Запускает таймер автообновления данных выбранного кластера (5 с). Создаётся при
    /// успешном подключении, если переключатель автообновления включён; повторный запуск —
    /// no-op. Тик идёт через <see cref="Refresh"/> с тем же флагом занятости, что и ручное
    /// «Обновить» (наложение исключено). При выключенном переключателе — no-op: единая
    /// защита всех точек запуска (issue #324, «галка снята, а таймер работает»).
    /// </summary>
    private void StartAutoRefresh()
    {
        if (!IsAutoRefreshEnabled)
            return;
        var intervalMs = AutoRefreshIntervalSeconds * 1000;
        _autoRefreshTimer ??= new Timer(_ => Refresh(), null, intervalMs, intervalMs);
        OnPropertyChanged(nameof(AutoRefreshActive));
        OnPropertyChanged(nameof(AutoRefreshText));
        OnPropertyChanged(nameof(IsAutoRefreshEnabled));
    }

    /// <summary>Останавливает таймер автообновления (Dispose окна, ошибка подключения).</summary>
    private void StopAutoRefresh()
    {
        _autoRefreshTimer?.Dispose();
        _autoRefreshTimer = null;
        OnPropertyChanged(nameof(AutoRefreshActive));
        OnPropertyChanged(nameof(AutoRefreshText));
        OnPropertyChanged(nameof(IsAutoRefreshEnabled));
    }

    /// <inheritdoc />
    /// <summary>Останавливает таймер автообновления (вызывается окном при закрытии).</summary>
    public void Dispose() => StopAutoRefresh();

    // ===================== Внутреннее =====================

    private RacConnectionParams BuildParams()
    {
        // Порт можно указать полем «Порт» или прямо в адресе как «host:port»
        // (например «localhost:27545» — как в командной строке rac, issue #324):
        // порт из адреса имеет приоритет над значением поля.
        var (host, port) = RacConnectionAddress.Split(ServerAddress, ServerPort);
        return new RacConnectionParams
        {
            Address = host,
            Port = port,
            User = UserName?.Trim() ?? string.Empty,
            Password = Password ?? string.Empty
        };
    }

    private void ApplyClusters(IReadOnlyList<RacCluster> clusters)
    {
        void Apply()
        {
            Clusters = clusters;
            ClusterRows = clusters
                .Where(c => c is not null)
                .Select(c => new RacClusterRow(c))
                .ToList();
            OnPropertyChanged(nameof(Clusters));
            OnPropertyChanged(nameof(ClusterRows));

            // issue #324: единственный кластер выбирается в списке СРАЗУ (до завершения
            // ConnectAsync); при нескольких — выбор не навязывается (прежнее поведение);
            // при отсутствии — сбрасывается.
            if (ClusterRows.Count == 1)
                SelectedClusterId = ClusterRows[0].Id;
            else if (ClusterRows.Count == 0)
                SelectedClusterId = null;
        }

        if (_dispatchToUi is null)
            Apply();
        else
            _dispatchToUi(Apply);
    }

    private void ApplyClusterData(
        IReadOnlyList<RacProcessInfo> processes,
        IReadOnlyList<RacSessionInfo> sessions,
        IReadOnlyList<RacConnectionInfo> connections,
        IReadOnlyList<RacLockInfo> locks,
        IReadOnlyList<RacJobInfo> jobs,
        IReadOnlyList<RacInfobaseSummary> infobases,
        RacClusterInfo? info)
    {
        void Apply()
        {
            // Выбор сохраняем по идентификатору: после автообновления строка с тем же
            // Id остаётся выбранной, исчезнувшая (завершённый сеанс) — сбрасывается.
            var sessionId = SelectedSession?.Id;
            var connectionId = SelectedConnection?.Id;
            var jobId = SelectedJob?.Id;
            var jobFilter = SelectedJobInfobaseId;

            // Кэш имён информационных баз кластера: колонка «База» и фильтр используют
            // имя из «infobase summary list», тогда как задания отдают GUID ИБ.
            _infobaseNames.Clear();
            foreach (var ib in infobases)
            {
                if (ib.InfobaseId != Guid.Empty && !_infobaseNames.ContainsKey(ib.InfobaseId))
                    _infobaseNames[ib.InfobaseId] = ib.Name;
            }

            ReplaceRows(Processes, processes.Select(p => new RacProcessRow(p)));
            // issue #324, C2: в строку сеанса передаётся имя информационной базы
            // (сопоставление infobase-id из «session list» со «infobase summary list»).
            ReplaceRows(Sessions, sessions.Select(s => new RacSessionRow(s, SessionInfobaseName(s))));
            ReplaceRows(Connections, connections.Select(c => new RacConnectionRow(c)));
            ReplaceRows(Locks, locks.Select(l => new RacLockRow(l)));
            ReplaceRows(Jobs, jobs.Select(j => new RacJobRow(j, InfobaseName(j))));
            OnPropertyChanged(nameof(HasJobs));
            ClusterInfo = info;
            ClusterInfoText = FormatClusterInfo(info);
            ReplaceRows(ClusterProperties, BuildClusterPropertyRows(info));

            JobInfobaseFilterRows = BuildJobFilterRows(infobases);
            OnPropertyChanged(nameof(JobInfobaseFilterRows));

            // Фильтр сохраняется по Id; если база исчезла из списка — сбрасываем на «Все».
            if (jobFilter is Guid fid && !_infobaseNames.ContainsKey(fid))
                jobFilter = null;
            SelectedJobInfobaseId = jobFilter;

            // Пересборка после ReplaceRows(Jobs, …): фильтр мог не меняться, поэтому
            // ApplyJobFilter вызываем явно, затем восстанавливаем выбор по Id.
            ApplyJobFilter();
            SelectedJob = jobId is Guid j ? FilteredJobs.FirstOrDefault(x => x.Id == j) : null;

            SelectedSession = sessionId is Guid s ? Sessions.FirstOrDefault(x => x.Id == s) : null;
            SelectedConnection = connectionId is Guid c ? Connections.FirstOrDefault(x => x.Id == c) : null;
        }

        if (_dispatchToUi is null)
            Apply();
        else
            _dispatchToUi(Apply);
    }

    /// <summary>Имя базы-владельца задания из кэша; «—» для заданий без базы/неизвестных GUID.</summary>
    private string InfobaseName(RacJobInfo job) =>
        job.InfobaseId is Guid id && _infobaseNames.TryGetValue(id, out var name) && !string.IsNullOrWhiteSpace(name)
            ? name
            : LocalizationManager.T("ServerMonitor.Job.UnknownBase");

    /// <summary>
    /// Имя информационной базы сеанса из кэша (issue #324, C2: колонка «Информационная
    /// база» на вкладке «Сеансы»); «—» для сеансов без базы/неизвестных GUID.
    /// </summary>
    private string SessionInfobaseName(RacSessionInfo session) =>
        session.InfobaseId is Guid id && _infobaseNames.TryGetValue(id, out var name) && !string.IsNullOrWhiteSpace(name)
            ? name
            : LocalizationManager.T("ServerMonitor.Session.UnknownBase");

    /// <summary>Строки фильтра «по базе»: «Все базы» + имена баз кластера (по алфавиту).</summary>
    private static IReadOnlyList<RacJobFilterRow> BuildJobFilterRows(
        IReadOnlyList<RacInfobaseSummary> infobases)
    {
        var rows = new List<RacJobFilterRow> { RacJobFilterRow.All };
        rows.AddRange(infobases
            .Where(ib => ib.InfobaseId != Guid.Empty)
            .OrderBy(ib => ib.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(ib => new RacJobFilterRow(ib.InfobaseId, ib.Name)));
        return rows;
    }

    /// <summary>Пересобирает <see cref="FilteredJobs"/> по выбранному фильтру базы.</summary>
    private void ApplyJobFilter()
    {
        var filter = SelectedJobInfobaseId;
        IEnumerable<RacJobRow> rows = filter is Guid id
            ? Jobs.Where(j => j.InfobaseId == id)
            : Jobs;
        ReplaceRows(FilteredJobs, rows);
    }

    private static void ReplaceRows<T>(ObservableCollection<T> target, IEnumerable<T> rows)
    {
        target.Clear();
        foreach (var row in rows)
            target.Add(row);
    }

    /// <summary>
    /// Строки «свойство — значение» для вкладки «Информация о кластере» (issue #324, C3):
    /// редактируется безопасный набор параметров (имя, таймауты/лимиты, уровень
    /// безопасности, пинг, аутентификация); остальные свойства — только чтение.
    /// Ключи rac сопоставляются регистронезависимо с нормализацией дефисов
    /// («expiration-timeout» ≡ «expirationTimeout»).
    /// </summary>
    private static IReadOnlyList<RacClusterPropertyRow> BuildClusterPropertyRows(RacClusterInfo? info)
    {
        var rows = new List<RacClusterPropertyRow>();
        if (info is null)
            return rows;

        foreach (var pair in info.Properties)
            rows.Add(new RacClusterPropertyRow(pair.Key, pair.Value, IsEditableClusterProperty(pair.Key)));

        // Резерв: словарь пуст (не распознан) — типизированные поля как только-читаемые.
        if (rows.Count == 0 && info.Name.Length > 0)
        {
            rows.Add(new RacClusterPropertyRow("name", info.Name, editable: true));
            rows.Add(new RacClusterPropertyRow("hostName", info.HostName, editable: false));
        }

        return rows;
    }

    /// <summary>Ключ свойства rac (регистронезависимо, дефисы нормализуются) в канонической форме.</summary>
    internal static string NormalizeClusterPropertyKey(string rawKey) =>
        rawKey?.Replace("-", string.Empty).Replace("_", string.Empty).Trim().ToLowerInvariant() ?? string.Empty;

    /// <summary>Входит ли свойство кластера в безопасный набор правки (rac «cluster update»).</summary>
    internal static bool IsEditableClusterProperty(string rawKey) =>
        NormalizeClusterPropertyKey(rawKey) switch
        {
            "name" or "expirationtimeout" or "lifetimelimit" or "maxmemorysize" or
            "maxmemorytimelimit" or "securitylevel" or "pingperiod" or "pingtimeout" or
            "maxauthattempts" or "authlockduration" => true,
            _ => false
        };

    /// <summary>
    /// Собирает изменения параметров кластера из правленых строк вкладки
    /// «Информация о кластере» (issue #324, C3). Числовые значения разбираются
    /// инвариантно; при ошибке разбора возвращает null и показывает предупреждение.
    /// </summary>
    private RacClusterUpdate? BuildClusterUpdateFromEdits()
    {
        var update = new RacClusterUpdate();
        foreach (var row in ClusterProperties)
        {
            if (!row.IsChanged)
                continue;

            var value = row.EditValue?.Trim() ?? string.Empty;
            switch (NormalizeClusterPropertyKey(row.RawKey))
            {
                case "name":
                    update.Name = value;
                    break;
                case "expirationtimeout":
                case "lifetimelimit":
                case "maxmemorysize":
                case "maxmemorytimelimit":
                case "pingperiod":
                case "pingtimeout":
                case "authlockduration":
                    if (!TryParseLong(row, value, out var longValue))
                        return null;
                    AssignLong(row.RawKey, longValue);
                    break;
                case "securitylevel":
                case "maxauthattempts":
                    if (!TryParseInt(row, value, out var intValue))
                        return null;
                    AssignInt(row.RawKey, intValue);
                    break;
            }
        }

        return update;

        void AssignLong(string rawKey, long value)
        {
            switch (NormalizeClusterPropertyKey(rawKey))
            {
                case "expirationtimeout": update.ExpirationTimeout = value; break;
                case "lifetimelimit": update.LifetimeLimit = value; break;
                case "maxmemorysize": update.MaxMemorySize = value; break;
                case "maxmemorytimelimit": update.MaxMemoryTimeLimit = value; break;
                case "pingperiod": update.PingPeriod = value; break;
                case "pingtimeout": update.PingTimeout = value; break;
                case "authlockduration": update.AuthLockDuration = value; break;
            }
        }

        void AssignInt(string rawKey, int value)
        {
            switch (NormalizeClusterPropertyKey(rawKey))
            {
                case "securitylevel": update.SecurityLevel = value; break;
                case "maxauthattempts": update.MaxAuthAttempts = value; break;
            }
        }
    }

    /// <summary>Разбор числового значения поля правки (инвариантно) с понятной ошибкой.</summary>
    private bool TryParseLong(RacClusterPropertyRow row, string value, out long result)
    {
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result))
            return true;

        var message = string.Format(
            LocalizationManager.T("ServerMonitor.Cluster.InvalidNumberFormat"),
            row.DisplayName, value);
        _dialogs.ShowWarning(message, LocalizationManager.T("ServerMonitor.Cluster.SaveTitle"));
        return false;
    }

    /// <summary>Разбор целочисленного значения поля правки (инвариантно) с понятной ошибкой.</summary>
    private bool TryParseInt(RacClusterPropertyRow row, string value, out int result)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result))
            return true;

        var message = string.Format(
            LocalizationManager.T("ServerMonitor.Cluster.InvalidNumberFormat"),
            row.DisplayName, value);
        _dialogs.ShowWarning(message, LocalizationManager.T("ServerMonitor.Cluster.SaveTitle"));
        return false;
    }

    /// <summary>
    /// «Сохранить изменения» на вкладке «Информация о кластере» (issue #324, C3):
    /// подтверждение → rac «cluster update» с ТОЛЬКО изменёнными параметрами →
    /// перечитывание данных кластера. Ошибки не роняют окно.
    /// </summary>
    public async Task SaveClusterPropertiesAsync()
    {
        if (!HasConnected || SelectedClusterId is not Guid clusterId)
            return;

        var update = BuildClusterUpdateFromEdits();
        if (update is null)
            return; // предупреждение о неверном числе уже показано
        if (update.IsEmpty)
        {
            StatusText = LocalizationManager.T("ServerMonitor.Cluster.NothingToSave");
            return;
        }

        if (!_dialogs.Confirm(
                string.Format(
                    LocalizationManager.T("ServerMonitor.Cluster.ConfirmFormat"),
                    CountChanges(update)),
                LocalizationManager.T("ServerMonitor.Cluster.SaveTitle")))
            return;

        try
        {
            var ok = await _rac.UpdateClusterAsync(BuildParams(), clusterId, update).ConfigureAwait(false);
            if (!ok)
            {
                var detail = BuildActionError(_rac.LastActionError);
                _dialogs.ShowWarning(
                    LocalizationManager.T("ServerMonitor.Cluster.SaveFailedFormat") + "\n" + detail,
                    LocalizationManager.T("ServerMonitor.Cluster.SaveTitle"));
                StatusText = detail;
                return;
            }

            StatusText = LocalizationManager.T("ServerMonitor.Cluster.SavedFormat");
        }
        catch (Exception ex)
        {
            _dialogs.ShowWarning(
                LocalizationManager.T("ServerMonitor.Cluster.SaveFailedFormat") + "\n" + BuildErrorMessage(ex),
                LocalizationManager.T("ServerMonitor.Cluster.SaveTitle"));
            StatusText = BuildErrorMessage(ex);
        }
        finally
        {
            // После обновления параметры кластера перечитываются (и строки правки
            // пересобираются из фактического вывода rac).
            Refresh();
        }
    }

    /// <summary>Число изменённых параметров для текста подтверждения.</summary>
    private static int CountChanges(RacClusterUpdate update) =>
        (update.Name is null ? 0 : 1) +
        (update.ExpirationTimeout is null ? 0 : 1) +
        (update.LifetimeLimit is null ? 0 : 1) +
        (update.MaxMemorySize is null ? 0 : 1) +
        (update.MaxMemoryTimeLimit is null ? 0 : 1) +
        (update.SecurityLevel is null ? 0 : 1) +
        (update.PingPeriod is null ? 0 : 1) +
        (update.PingTimeout is null ? 0 : 1) +
        (update.MaxAuthAttempts is null ? 0 : 1) +
        (update.AuthLockDuration is null ? 0 : 1);

    private static string FormatClusterInfo(RacClusterInfo? info)
    {
        if (info is null)
            return LocalizationManager.T("ServerMonitor.Empty.Info");

        var sb = new StringBuilder();
        if (info.Properties.Count > 0)
        {
            foreach (var pair in info.Properties)
                sb.AppendLine($"{pair.Key}: {pair.Value}");
        }
        else
        {
            // Резерв: если словарь пуст (не распознан), выводим типизированные поля.
            sb.AppendLine($"name: {info.Name}");
            sb.AppendLine($"hostName: {info.HostName}");
            sb.AppendLine($"port: {info.Port}");
            sb.AppendLine($"expirationTimeout: {info.ExpirationTimeout}");
            sb.AppendLine($"lifetimeLimit: {info.LifetimeLimit}");
            sb.AppendLine($"maxMemorySize: {info.MaxMemorySize}");
            sb.AppendLine($"maxMemoryTimeLimit: {info.MaxMemoryTimeLimit}");
            sb.AppendLine($"securityLevel: {info.SecurityLevel}");
            sb.AppendLine($"sessionIdleTimeout: {info.SessionIdleTimeout}");
            sb.AppendLine($"sessionMaxMemorySize: {info.SessionMaxMemorySize}");
            sb.AppendLine($"sessionMaxTimeLimit: {info.SessionMaxTimeLimit}");
        }

        return sb.ToString().TrimEnd();
    }

    private static string BuildErrorMessage(Exception ex)
    {
        var message = ex.Message;
        return string.IsNullOrWhiteSpace(message)
            ? LocalizationManager.T("ServerMonitor.Errors.Unknown")
            : message;
    }

    /// <summary>Текст ошибки действия rac (из <see cref="IRacClient.LastActionError"/>).</summary>
    private static string BuildActionError(string? lastActionError) =>
        string.IsNullOrWhiteSpace(lastActionError)
            ? LocalizationManager.T("ServerMonitor.Errors.Unknown")
            : lastActionError;

    private bool TryEnterBusy()
    {
        // Один запрос за раз: длительная rac-команда не должна копить очередь (см. ProcessInspectorViewModel:80).
        if (Interlocked.Exchange(ref _busy, 1) == 1)
            return false;
        OnPropertyChanged(nameof(IsBusy));
        return true;
    }

    private void ExitBusy()
    {
        Interlocked.Exchange(ref _busy, 0);
        OnPropertyChanged(nameof(IsBusy));
    }
}