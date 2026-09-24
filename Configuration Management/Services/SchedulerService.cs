using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Configuration_Management.Localization;
using Configuration_Management.Models;

namespace Configuration_Management.Services;

/// <summary>
/// Планировщик заданий по расписанию (issue #286). Работает, пока приложение запущено:
/// периодически проверяет наступление времени заданий (время «HH:mm» + дни недели) и
/// выполняет их последовательно (одна операция 1С за раз). Момент запуска вычисляется
/// чистым классом <see cref="ScheduleCalculator"/>; пропущенные запуски (приложение было
/// выключено) не «догоняются». Результаты записываются в задание и в журнал.
/// </summary>
public class SchedulerService : IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(5);

    private readonly IScheduledTaskStore _tasks;
    private readonly IBackupScenarioStore _scenarios;
    private readonly IInfobaseRepository _repository;
    private readonly IBackupService _backup;
    private readonly IConfigUpdateService _configUpdate;
    private readonly GitHubReleaseService _gitHub;
    private readonly UpdateService _updateService;
    private readonly IAppLogger? _logger;

    private Timer? _timer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, DateTime?> _nextRuns = new(StringComparer.Ordinal);

    public SchedulerService(
        IScheduledTaskStore tasks,
        IBackupScenarioStore scenarios,
        IInfobaseRepository repository,
        IBackupService backup,
        IConfigUpdateService configUpdate,
        GitHubReleaseService gitHub,
        UpdateService updateService,
        IAppLogger? logger = null)
    {
        _tasks = tasks;
        _scenarios = scenarios;
        _repository = repository;
        _backup = backup;
        _configUpdate = configUpdate;
        _gitHub = gitHub;
        _updateService = updateService;
        _logger = logger;
    }

    /// <summary>Запускает планировщик (после входа в профиль). Повторный вызов перезапускает.</summary>
    public void Start()
    {
        Stop();
        RebuildNextRuns();
        _timer = new Timer(_ => Tick(), null, InitialDelay, TickInterval);
    }

    /// <summary>Останавливает планировщик (при выходе из приложения).</summary>
    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>
    /// Выполняет задание немедленно (кнопка «Выполнить сейчас» в окне заданий).
    /// Записывает результат и сохраняет задание. Возвращает null при неожиданной ошибке.
    /// </summary>
    public async Task<BackupRunResult?> RunNowAsync(ScheduledTask task)
    {
        if (task is null)
            return null;
        var result = await ExecuteAsync(task).ConfigureAwait(false);
        task.LastRunAt = DateTime.Now;
        SaveResult(task, result);
        return result;
    }

    private void Tick()
    {
        // Не выполняем параллельную обработку: операции 1С конфликтуют между собой.
        if (!_gate.Wait(0))
            return;
        try
        {
            ProcessDueAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger?.Error("Ошибка фонового планировщика заданий", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ProcessDueAsync()
    {
        var now = DateTime.Now;
        foreach (var task in _tasks.LoadAll())
        {
            if (!task.Enabled)
                continue;

            var next = _nextRuns.TryGetValue(task.Id, out var cached) ? cached : null;
            if (next is null || now < next)
                continue;

            _logger?.Info($"Расписание: наступило время задания «{task.Name}» ({task.Kind}).");
            var result = await ExecuteAsync(task).ConfigureAwait(false);
            task.LastRunAt = DateTime.Now;
            SaveResult(task, result);
            // Следующий момент — с запасом в минуту, чтобы не выполнить задание дважды подряд.
            _nextRuns[task.Id] = ScheduleCalculator.ComputeNextRun(task, DateTime.Now.AddMinutes(1));
        }
    }

    private void SaveResult(ScheduledTask task, BackupRunResult? result)
    {
        if (result is not null)
        {
            var files = result.CreatedFiles ?? Array.Empty<string>();
            task.LastRunSuccess = result.Success;
            task.LastRunMessage = result.Success
                ? (files.Count > 0 ? string.Join("; ", files) : result.ScenarioName)
                : result.ErrorMessage;
        }
        else
        {
            task.LastRunSuccess = false;
            task.LastRunMessage = LocalizationManager.T("Schedule.RunFailed");
        }

        try { _tasks.Save(task); }
        catch (Exception ex) { _logger?.Error("Не удалось сохранить состояние задания", ex); }
    }

    private async Task<BackupRunResult?> ExecuteAsync(ScheduledTask task)
    {
        try
        {
            return task.Kind switch
            {
                ScheduledTaskKind.Backup => await RunBackupAsync(task).ConfigureAwait(false),
                ScheduledTaskKind.UpdateConfig => await RunUpdateConfigAsync(task).ConfigureAwait(false),
                ScheduledTaskKind.BackupThenUpdateConfig => await RunBackupThenUpdateAsync(task).ConfigureAwait(false),
                ScheduledTaskKind.UpdateApp => await RunUpdateAppAsync(task).ConfigureAwait(false),
                _ => null
            };
        }
        catch (Exception ex)
        {
            _logger?.Error($"Ошибка выполнения задания «{task.Name}» ({task.Kind})", ex);
            return new BackupRunResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    private async Task<BackupRunResult?> RunBackupAsync(ScheduledTask task)
    {
        var infobase = ResolveInfobase(task.InfobaseId);
        if (infobase is null)
            return Fail(LocalizationManager.T("Schedule.BaseNotFound"));

        var scenario = ResolveScenario(task.ScenarioId);
        if (scenario is null)
            return Fail(LocalizationManager.T("Schedule.ScenarioNotFound"));

        return await _backup.RunAsync(infobase, scenario).ConfigureAwait(false);
    }

    private async Task<BackupRunResult?> RunUpdateConfigAsync(ScheduledTask task)
    {
        var infobase = ResolveInfobase(task.InfobaseId);
        if (infobase is null)
            return Fail(LocalizationManager.T("Schedule.BaseNotFound"));

        if (string.IsNullOrWhiteSpace(task.ConfigFilePath))
            return Fail(LocalizationManager.T("Schedule.CfgFileRequired"));

        return await _configUpdate.UpdateConfigAsync(infobase, task.ConfigFilePath!).ConfigureAwait(false);
    }

    private async Task<BackupRunResult?> RunBackupThenUpdateAsync(ScheduledTask task)
    {
        var backupResult = await RunBackupAsync(task).ConfigureAwait(false);
        if (backupResult is null)
            return null;
        if (!backupResult.Success)
        {
            // Копия не удалась — обновлять конфигурацию не начинаем.
            _logger?.Warn($"Задание «{task.Name}»: резервная копия не создана, обновление пропущено.");
            return backupResult;
        }

        return await RunUpdateConfigAsync(task).ConfigureAwait(false);
    }

    private async Task<BackupRunResult?> RunUpdateAppAsync(ScheduledTask task)
    {
        var result = new BackupRunResult();
        var release = await _gitHub.GetLatestReleaseAsync().ConfigureAwait(false);
        if (release is null)
        {
            result.Success = false;
            result.ErrorMessage = LocalizationManager.T("Update.CheckFailed");
            return result;
        }

        if (!GitHubReleaseService.IsNewerThan(release, VersionInfo.Display()))
        {
            result.Success = false;
            result.ErrorMessage = LocalizationManager.T("Update.UpToDate");
            return result;
        }

        if (string.IsNullOrWhiteSpace(release.DownloadUrl))
        {
            result.Success = false;
            result.ErrorMessage = LocalizationManager.T("Update.NoDownloadUrl");
            return result;
        }

        result.Success = true;
        result.ScenarioName = release.TagName ?? "";
        // Метод скачивает новый исполняемый файл, применяет обновление и перезапускает
        // приложение (в том числе без диалогов). Ошибки он обрабатывает сам.
        await _updateService.DownloadAndInstallAutoAsync(release).ConfigureAwait(false);
        return result;
    }

    private Infobase? ResolveInfobase(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;
        try
        {
            return _repository.Load().FirstOrDefault(
                b => string.Equals(b.Id, id, StringComparison.Ordinal));
        }
        catch
        {
            return null;
        }
    }

    private BackupScenario? ResolveScenario(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;
        try
        {
            return _scenarios.Get(id!);
        }
        catch
        {
            return null;
        }
    }

    private static BackupRunResult Fail(string message) =>
        new() { Success = false, ErrorMessage = message };

    private void RebuildNextRuns()
    {
        _nextRuns.Clear();
        var now = DateTime.Now;
        foreach (var task in _tasks.LoadAll())
            _nextRuns[task.Id] = ScheduleCalculator.ComputeNextRun(task, now);
    }

    public void Dispose()
    {
        Stop();
        _gate.Dispose();
    }
}