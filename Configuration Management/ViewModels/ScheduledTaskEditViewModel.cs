using Configuration_Management.Localization;
using Configuration_Management.Models;

namespace Configuration_Management.ViewModels;

/// <summary>
/// Пункт выпадающего списка (тип задания / сценарий / база).
/// </summary>
public sealed class ScheduledTaskOption
{
    public ScheduledTaskOption(string value, string text)
    {
        Value = value;
        Text = text;
    }

    public string Value { get; }
    public string Text { get; }

    /// <summary>Отображение в выпадающих списках без явной привязки.</summary>
    public override string ToString() => Text;
}

/// <summary>
/// Форма редактирования задания по расписанию (issue #286): поля формы и валидация.
/// Чистый .NET, используется обеими платформами.
/// </summary>
public class ScheduledTaskEditViewModel : ViewModelBase
{
    private IReadOnlyList<BackupScenario> _scenarios;
    private readonly IReadOnlyList<Infobase> _infobases;

    public ScheduledTaskEditViewModel(ScheduledTask? task,
        IReadOnlyList<BackupScenario> scenarios,
        IReadOnlyList<Infobase> infobases)
    {
        _scenarios = scenarios ?? Array.Empty<BackupScenario>();
        _infobases = infobases ?? Array.Empty<Infobase>();

        Name = task?.Name ?? "";
        Enabled = task?.Enabled ?? true;
        Kind = task?.Kind ?? ScheduledTaskKind.Backup;
        Time = string.IsNullOrWhiteSpace(task?.Time) ? "02:00" : task!.Time.Trim();
        ConfigFilePath = task?.ConfigFilePath ?? "";
        SelectedScenarioId = task?.ScenarioId ?? "";
        SelectedInfobaseId = task?.InfobaseId ?? "";

        var days = task?.DaysOfWeek ?? new List<DayOfWeek>();
        IsMonday = days.Contains(DayOfWeek.Monday);
        IsTuesday = days.Contains(DayOfWeek.Tuesday);
        IsWednesday = days.Contains(DayOfWeek.Wednesday);
        IsThursday = days.Contains(DayOfWeek.Thursday);
        IsFriday = days.Contains(DayOfWeek.Friday);
        IsSaturday = days.Contains(DayOfWeek.Saturday);
        IsSunday = days.Contains(DayOfWeek.Sunday);
    }

    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public ScheduledTaskKind Kind { get; set; } = ScheduledTaskKind.Backup;
    public string Time { get; set; } = "02:00";
    public string ConfigFilePath { get; set; } = "";
    public string SelectedScenarioId { get; set; } = "";
    public string SelectedInfobaseId { get; set; } = "";

    public bool IsMonday { get; set; }
    public bool IsTuesday { get; set; }
    public bool IsWednesday { get; set; }
    public bool IsThursday { get; set; }
    public bool IsFriday { get; set; }
    public bool IsSaturday { get; set; }
    public bool IsSunday { get; set; }

    /// <summary>Доступные типы задания для выпадающего списка.</summary>
    public IReadOnlyList<ScheduledTaskOption> KindOptions => new[]
    {
        new ScheduledTaskOption("Backup", LocalizationManager.T("Schedule.Kind.Backup")),
        new ScheduledTaskOption("UpdateConfig", LocalizationManager.T("Schedule.Kind.UpdateConfig")),
        new ScheduledTaskOption("BackupThenUpdateConfig", LocalizationManager.T("Schedule.Kind.BackupThenUpdateConfig")),
        new ScheduledTaskOption("UpdateApp", LocalizationManager.T("Schedule.Kind.UpdateApp"))
    };

    /// <summary>Доступные сценарии резервирования.</summary>
    public IReadOnlyList<ScheduledTaskOption> ScenarioOptions => _scenarios
        .Select(s => new ScheduledTaskOption(s.Id, s.Name))
        .ToList();

    /// <summary>
    /// Обновляет список доступных сценариев после возврата из окна «Сценарии резервирования»
    /// (issue #292): вновь созданные/изменённые сценарии появляются в выборе без переоткрытия
    /// окна задания.
    /// </summary>
    public void UpdateScenarios(IReadOnlyList<BackupScenario> scenarios)
    {
        _scenarios = scenarios ?? Array.Empty<BackupScenario>();
    }

    /// <summary>Доступные информационные базы.</summary>
    public IReadOnlyList<ScheduledTaskOption> InfobaseOptions => _infobases
        .Select(b => new ScheduledTaskOption(b.Id, b.Name))
        .ToList();

    /// <summary>Нужен ли выбор сценария (для типов с резервной копией).</summary>
    public bool NeedsScenario => Kind is ScheduledTaskKind.Backup or ScheduledTaskKind.BackupThenUpdateConfig;

    /// <summary>Нужен ли выбор базы (кроме обновления приложения).</summary>
    public bool NeedsInfobase => Kind is ScheduledTaskKind.Backup
        or ScheduledTaskKind.UpdateConfig
        or ScheduledTaskKind.BackupThenUpdateConfig;

    /// <summary>Нужен ли путь к файлу .cf (для обновления конфигурации).</summary>
    public bool NeedsCfgFile => Kind is ScheduledTaskKind.UpdateConfig or ScheduledTaskKind.BackupThenUpdateConfig;

    /// <summary>
    /// Валидация полей формы. Возвращает ключ локализации ошибки либо <c>null</c>,
    /// если всё корректно.
    /// </summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
            return "Schedule.NameRequired";

        if (!TimeSpan.TryParse(Time, out _))
            return "Schedule.InvalidTime";

        switch (Kind)
        {
            case ScheduledTaskKind.Backup:
            case ScheduledTaskKind.BackupThenUpdateConfig:
                if (string.IsNullOrWhiteSpace(SelectedScenarioId))
                    return "Schedule.ScenarioRequired";
                break;
            case ScheduledTaskKind.UpdateConfig:
                break;
        }

        if (NeedsInfobase && string.IsNullOrWhiteSpace(SelectedInfobaseId))
            return "Schedule.BaseRequired";

        if (NeedsCfgFile && string.IsNullOrWhiteSpace(ConfigFilePath))
            return "Schedule.CfgFileRequired";

        return null;
    }

    /// <summary>Переносит заполненные поля формы в задание.</summary>
    public void ApplyTo(ScheduledTask task)
    {
        task.Name = Name.Trim();
        task.Enabled = Enabled;
        task.Kind = Kind;
        task.Time = TimeSpan.TryParse(Time, out var t) ? t.ToString(@"hh\:mm") : "00:00";
        task.ScenarioId = string.IsNullOrWhiteSpace(SelectedScenarioId) ? null : SelectedScenarioId.Trim();
        task.InfobaseId = string.IsNullOrWhiteSpace(SelectedInfobaseId) ? null : SelectedInfobaseId.Trim();
        task.ConfigFilePath = string.IsNullOrWhiteSpace(ConfigFilePath) ? null : ConfigFilePath.Trim();
        task.DaysOfWeek = CollectDays();
    }

    private List<DayOfWeek> CollectDays()
    {
        var days = new List<DayOfWeek>();
        if (IsMonday) days.Add(DayOfWeek.Monday);
        if (IsTuesday) days.Add(DayOfWeek.Tuesday);
        if (IsWednesday) days.Add(DayOfWeek.Wednesday);
        if (IsThursday) days.Add(DayOfWeek.Thursday);
        if (IsFriday) days.Add(DayOfWeek.Friday);
        if (IsSaturday) days.Add(DayOfWeek.Saturday);
        if (IsSunday) days.Add(DayOfWeek.Sunday);
        return days;
    }
}