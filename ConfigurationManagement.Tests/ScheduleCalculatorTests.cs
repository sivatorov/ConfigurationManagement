using Configuration_Management.Models;
using Configuration_Management.Services;
using Xunit;

namespace ConfigurationManagement.Tests;

public sealed class ScheduleCalculatorTests
{
    private static ScheduledTask NewTask(string time = "02:00", params DayOfWeek[] days) => new()
    {
        Enabled = true,
        Time = time,
        DaysOfWeek = days.ToList()
    };

    [Fact]
    public void DailyTask_BeforeTime_NextRunIsToday()
    {
        var task = NewTask("14:30");
        var from = new DateTime(2026, 9, 24, 10, 0, 0); // до времени запуска

        var next = ScheduleCalculator.ComputeNextRun(task, from);

        Assert.Equal(new DateTime(2026, 9, 24, 14, 30, 0), next);
    }

    [Fact]
    public void DailyTask_AfterTime_NextRunIsTomorrow()
    {
        var task = NewTask("14:30");
        var from = new DateTime(2026, 9, 24, 16, 0, 0); // после времени запуска

        var next = ScheduleCalculator.ComputeNextRun(task, from);

        Assert.Equal(new DateTime(2026, 9, 25, 14, 30, 0), next);
    }

    [Fact]
    public void DailyTask_ExactlyAtTime_NextRunIsNow()
    {
        var task = NewTask("14:30");
        var from = new DateTime(2026, 9, 24, 14, 30, 0);

        var next = ScheduleCalculator.ComputeNextRun(task, from);

        Assert.Equal(from, next);
    }

    [Fact]
    public void WeekdayTask_SkipsToNextAllowedDay()
    {
        // 2026-09-24 — четверг (Thursday). Задание только на понедельник.
        var task = NewTask("09:00", DayOfWeek.Monday);
        var from = new DateTime(2026, 9, 24, 12, 0, 0);

        var next = ScheduleCalculator.ComputeNextRun(task, from);

        // Ближайший понедельник — 2026-09-28.
        Assert.Equal(new DateTime(2026, 9, 28, 9, 0, 0), next);
    }

    [Fact]
    public void WeekdayTask_SameDayBeforeTime_ReturnsToday()
    {
        // 2026-09-24 — четверг. Задание на четверг и субботу.
        var task = NewTask("09:00", DayOfWeek.Thursday, DayOfWeek.Saturday);
        var from = new DateTime(2026, 9, 24, 7, 0, 0);

        var next = ScheduleCalculator.ComputeNextRun(task, from);

        Assert.Equal(new DateTime(2026, 9, 24, 9, 0, 0), next);
    }

    [Fact]
    public void DisabledTask_ReturnsNull()
    {
        var task = NewTask("09:00");
        task.Enabled = false;

        Assert.Null(ScheduleCalculator.ComputeNextRun(task, new DateTime(2026, 9, 24, 8, 0, 0)));
    }

    [Fact]
    public void InvalidTime_TreatedAsMidnight()
    {
        var task = NewTask("not-a-time");
        var from = new DateTime(2026, 9, 24, 10, 0, 0);

        var next = ScheduleCalculator.ComputeNextRun(task, from);

        Assert.Equal(new DateTime(2026, 9, 25, 0, 0, 0), next);
    }

}