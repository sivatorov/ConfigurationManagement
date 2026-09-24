using Configuration_Management.Models;

namespace Configuration_Management.Services;

/// <summary>
/// Чистая логика расписания заданий (issue #286): расчёт следующего момента запуска
/// с учётом времени «HH:mm» и дней недели. Вынесена в отдельный статический класс,
/// чтобы полностью покрыть юнит-тестами без зависимостей от UI и файловой системы.
/// </summary>
public static class ScheduleCalculator
{
    /// <summary>
    /// Возвращает следующий момент запуска задания, не раньше <paramref name="from"/>.
    /// Учитывает время (<see cref="ScheduledTask.Time"/>) и дни недели
    /// (<see cref="ScheduledTask.DaysOfWeek"/>; пустой список — ежедневно).
    /// Возвращает null, если задание выключено (выполняться не должно).
    /// </summary>
    public static DateTime? ComputeNextRun(ScheduledTask task, DateTime from)
    {
        if (task is null || !task.Enabled)
            return null;

        var time = task.GetTime();
        // 8 дней вперёд достаточно всегда: при ежедневном расписании момент попадётся
        // на первый же день, при днях недели — максимум на 7-й.
        for (var i = 0; i <= 7; i++)
        {
            var day = from.Date.AddDays(i);
            if (!task.MatchesDay(day.DayOfWeek))
                continue;

            var candidate = day.Add(time);
            if (candidate >= from)
                return candidate;
        }

        return null;
    }
}