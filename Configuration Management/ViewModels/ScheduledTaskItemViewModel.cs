using Configuration_Management.Localization;
using Configuration_Management.Models;

namespace Configuration_Management.ViewModels;

/// <summary>
/// Строка списка заданий по расписанию: отображаемые поля и действия
/// (выполнить/изменить/удалить). Чистый .NET, используется обеими платформами.
/// </summary>
public class ScheduledTaskItemViewModel : ViewModelBase
{
    private readonly Action<ScheduledTaskItemViewModel> _runAction;
    private readonly Action<ScheduledTaskItemViewModel> _editAction;
    private readonly Action<ScheduledTaskItemViewModel> _deleteAction;

    public ScheduledTaskItemViewModel(ScheduledTask task,
        Action<ScheduledTaskItemViewModel>? run = null,
        Action<ScheduledTaskItemViewModel>? edit = null,
        Action<ScheduledTaskItemViewModel>? delete = null)
    {
        Task = task;
        _runAction = run ?? (_ => { });
        _editAction = edit ?? (_ => { });
        _deleteAction = delete ?? (_ => { });
    }

    /// <summary>Исходная модель задания.</summary>
    public ScheduledTask Task { get; }

    public string Id => Task.Id;
    public string Name => Task.Name;
    public string KindText => KindName(Task.Kind);

    /// <summary>Краткое описание расписания: время + дни недели.</summary>
    public string ScheduleSummary => BuildScheduleSummary(Task);

    /// <summary>Статус последнего запуска (или «не выполнялось»).</summary>
    public string LastRunSummary
    {
        get
        {
            if (Task.LastRunAt is null)
                return LocalizationManager.T("Schedule.NeverRun");
            var status = Task.LastRunSuccess
                ? LocalizationManager.T("Schedule.LastRunOk")
                : LocalizationManager.T("Schedule.LastRunFailed");
            var message = string.IsNullOrWhiteSpace(Task.LastRunMessage) ? "" : " — " + Task.LastRunMessage;
            return $"{Task.LastRunAt:yyyy-MM-dd HH:mm} {status}{message}";
        }
    }

    public void Run() => _runAction(this);
    public void Edit() => _editAction(this);
    public void Delete() => _deleteAction(this);

    /// <summary>Человекочитаемое имя типа задания.</summary>
    public static string KindName(ScheduledTaskKind kind) => kind switch
    {
        ScheduledTaskKind.Backup => LocalizationManager.T("Schedule.Kind.Backup"),
        ScheduledTaskKind.UpdateConfig => LocalizationManager.T("Schedule.Kind.UpdateConfig"),
        ScheduledTaskKind.BackupThenUpdateConfig => LocalizationManager.T("Schedule.Kind.BackupThenUpdateConfig"),
        ScheduledTaskKind.UpdateApp => LocalizationManager.T("Schedule.Kind.UpdateApp"),
        _ => LocalizationManager.T("Schedule.Kind.Backup")
    };

    /// <summary>Собирает «HH:mm, Пн, Ср, Пт» или «HH:mm, ежедневно».</summary>
    private static string BuildScheduleSummary(ScheduledTask task)
    {
        var time = string.IsNullOrWhiteSpace(task.Time) ? "00:00" : task.Time.Trim();
        if (task.DaysOfWeek is not { Count: > 0 })
            return time + ", " + LocalizationManager.T("Schedule.EveryDay");
        return time + ", " + string.Join(", ", task.DaysOfWeek.Select(DayName));
    }

    private static string DayName(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => LocalizationManager.T("Schedule.Monday"),
        DayOfWeek.Tuesday => LocalizationManager.T("Schedule.Tuesday"),
        DayOfWeek.Wednesday => LocalizationManager.T("Schedule.Wednesday"),
        DayOfWeek.Thursday => LocalizationManager.T("Schedule.Thursday"),
        DayOfWeek.Friday => LocalizationManager.T("Schedule.Friday"),
        DayOfWeek.Saturday => LocalizationManager.T("Schedule.Saturday"),
        _ => LocalizationManager.T("Schedule.Sunday")
    };
}