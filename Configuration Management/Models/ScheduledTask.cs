using System.Linq;

namespace Configuration_Management.Models;

/// <summary>
/// Тип задания по расписанию (issue #286).
/// </summary>
public enum ScheduledTaskKind
{
    /// <summary>Резервная копия ИБ по существующему сценарию резервирования.</summary>
    Backup,

    /// <summary>Обновление конфигурации ИБ из файла .cf (/LoadCfg + /UpdateDBCfg).</summary>
    UpdateConfig,

    /// <summary>Связка «Копия → обновление»: сначала резервная копия, затем обновление конфигурации.</summary>
    BackupThenUpdateConfig,

    /// <summary>Обновление самого приложения через GitHub Releases (без диалога).</summary>
    UpdateApp
}

/// <summary>
/// Задание по расписанию: тип действия, целевая ИБ, время и дни недели запуска.
/// Хранится в отдельном JSON-файле (см. Services.ScheduledTaskStore).
/// Выполняется фоновым планировщиком, пока приложение запущено.
/// </summary>
public class ScheduledTask
{
    /// <summary>Идентификатор задания (GUID).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Наименование задания.</summary>
    public string Name { get; set; } = "";

    /// <summary>Тип выполняемого действия.</summary>
    public ScheduledTaskKind Kind { get; set; } = ScheduledTaskKind.Backup;

    /// <summary>Включено ли задание (выключенные пропускаются планировщиком).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Время запуска в формате «HH:mm». Невалидное значение трактуется как 00:00.
    /// </summary>
    public string Time { get; set; } = "02:00";

    /// <summary>
    /// Дни недели выполнения. Пустой список означает «ежедневно».
    /// </summary>
    public List<DayOfWeek> DaysOfWeek { get; set; } = new();

    /// <summary>Идентификатор сценария резервирования (для Backup / BackupThenUpdateConfig).</summary>
    public string? ScenarioId { get; set; }

    /// <summary>Идентификатор целевой ИБ (для Backup / UpdateConfig / BackupThenUpdateConfig).</summary>
    public string? InfobaseId { get; set; }

    /// <summary>Путь к файлу .cf с новой конфигурацией (для UpdateConfig / BackupThenUpdateConfig).</summary>
    public string? ConfigFilePath { get; set; }

    /// <summary>Момент последнего запуска задания (null — ещё не выполнялось).</summary>
    public DateTime? LastRunAt { get; set; }

    /// <summary>Успешно ли завершился последний запуск.</summary>
    public bool LastRunSuccess { get; set; }

    /// <summary>Сообщение о результате последнего запуска (путь/ошибка).</summary>
    public string? LastRunMessage { get; set; }

    /// <summary>Подходит ли день недели под расписание (пустой список — любой день).</summary>
    public bool MatchesDay(DayOfWeek day) => DaysOfWeek.Count == 0 || DaysOfWeek.Contains(day);

    /// <summary>Время запуска как TimeSpan («HH:mm»), при ошибке парсинга — 00:00.</summary>
    public TimeSpan GetTime() => TimeSpan.TryParse(Time, out var t) ? t : TimeSpan.Zero;
}