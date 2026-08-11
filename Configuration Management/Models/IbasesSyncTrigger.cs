namespace Configuration_Management.Models;

/// <summary>
/// Момент запуска автоматической синхронизации приложения с файлом ibases.v8i.
/// </summary>
public enum IbasesSyncTrigger
{
    /// <summary>Синхронизировать только при запуске приложения.</summary>
    OnStartup,

    /// <summary>Синхронизировать через заданный интервал времени.</summary>
    Interval,

    /// <summary>Синхронизировать по расписанию в заданное время.</summary>
    Schedule
}