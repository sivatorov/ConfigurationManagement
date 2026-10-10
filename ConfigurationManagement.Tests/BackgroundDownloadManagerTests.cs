using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Configuration_Management.Services;
using Xunit;

namespace ConfigurationManagement.Tests;

/// <summary>
/// Тесты менеджера фоновых загрузок (issue #334 п.1): регистрация, прогресс, отмена,
/// продолжение после «закрытия окна» (менеджер живёт независимо от окна), завершение/
/// ошибка, агрегированные свойства для индикатора главного окна, очистка завершённых.
/// </summary>
public sealed class BackgroundDownloadManagerTests
{
    [Fact]
    public void Start_RegistersActiveDownload()
    {
        var manager = new BackgroundDownloadManager();

        var entry = manager.Start("platform:8.3.27.2214:file.zip", "8.3.27.2214_x64.zip");

        Assert.Equal("platform:8.3.27.2214:file.zip", entry.Id);
        Assert.Equal("8.3.27.2214_x64.zip", entry.Title);
        Assert.True(entry.IsActive);
        Assert.Equal(BackgroundDownloadState.Active, entry.State);
        Assert.Equal(0, entry.Progress);
        Assert.Equal(1, manager.ActiveCount);
    }

    [Fact]
    public void Start_SameIdWhileActive_ReturnsSameEntry_NoDuplicate()
    {
        var manager = new BackgroundDownloadManager();

        var first = manager.Start("id", "Первый");
        var second = manager.Start("id", "Второй");

        Assert.Same(first, second);
        Assert.Equal("Первый", second.Title); // заголовок первоначальной записи сохранён
        Assert.Equal(1, manager.ActiveCount);
    }

    [Fact]
    public void Start_AfterCompletion_StartsNewEntry()
    {
        var manager = new BackgroundDownloadManager();
        manager.Start("id", "Первый");
        manager.Complete("id");

        var second = manager.Start("id", "Повтор");

        Assert.True(second.IsActive);
        var snapshot = Assert.Single(manager.Snapshot());
        Assert.Same(second, snapshot); // завершённая запись заменена новой
    }

    [Fact]
    public void ReportProgress_UpdatesEntry_Clamped()
    {
        var manager = new BackgroundDownloadManager();
        manager.Start("id", "Файл");

        manager.ReportProgress("id", 0.45);
        Assert.Equal(0.45, manager.Snapshot()[0].Progress);

        manager.ReportProgress("id", 5.0);
        Assert.Equal(1.0, manager.Snapshot()[0].Progress);

        // Прогресс неактивной записи игнорируется.
        manager.Complete("id");
        manager.ReportProgress("id", 0.9);
        Assert.Equal(1.0, manager.Snapshot()[0].Progress);
    }

    [Fact]
    public void Complete_MarksCompleted_RaisesEvents()
    {
        var manager = new BackgroundDownloadManager();
        var changed = 0;
        var completed = 0;
        manager.Changed += () => changed++;
        manager.Completed += _ => completed++;
        manager.Start("id", "Файл");

        manager.Complete("id");

        Assert.False(manager.Snapshot()[0].IsActive);
        Assert.Equal(BackgroundDownloadState.Completed, manager.Snapshot()[0].State);
        Assert.Equal(1.0, manager.Snapshot()[0].Progress);
        Assert.Equal(0, manager.ActiveCount);
        Assert.Equal(2, changed); // старт + завершение
        Assert.Equal(1, completed);
    }

    [Fact]
    public void Fail_MarksFailedWithErrorKey()
    {
        var manager = new BackgroundDownloadManager();
        var failed = 0;
        manager.Failed += _ => failed++;
        manager.Start("id", "Файл");

        manager.Fail("id", "PlatformUpdate.Error.NetworkError");

        Assert.Equal(BackgroundDownloadState.Failed, manager.Snapshot()[0].State);
        Assert.Equal("PlatformUpdate.Error.NetworkError", manager.Snapshot()[0].ErrorKey);
        Assert.Equal(1, failed);
    }

    [Fact]
    public void Cancel_SetsState_AndSignalsToken()
    {
        var manager = new BackgroundDownloadManager();
        var entry = manager.Start("id", "Файл");

        var cancelled = manager.Cancel("id");

        Assert.True(cancelled);
        Assert.Equal(BackgroundDownloadState.Cancelled, entry.State);
        Assert.False(entry.IsActive);
        Assert.Throws<OperationCanceledException>(() => entry.Cancellation.Token.ThrowIfCancellationRequested());
        // Повторная отмена неактивной записи — false.
        Assert.False(manager.Cancel("id"));
    }

    [Fact]
    public async Task Download_ContinuesAfterWindowClose_Simulation()
    {
        // Симуляция (issue #334 п.1): окно «умирает» сразу после старта, менеджер и
        // загрузка живут дальше; по завершении менеджер показывает Completed.
        var manager = new BackgroundDownloadManager();

        var entry = manager.Start("platform:test:file.zip", "file.zip");
        var windowClosed = true; // окно закрыто — ссылок на него нет, менеджер живёт

        var downloadTask = Task.Run(async () =>
        {
            await Task.Delay(50, entry.Cancellation.Token);
            manager.ReportProgress("platform:test:file.zip", 1.0);
            manager.Complete("platform:test:file.zip");
            return "done";
        });

        Assert.True(windowClosed);
        Assert.True(entry.IsActive); // пока загрузка идёт, она видна в индикаторе

        await downloadTask;

        var snapshot = manager.Snapshot().Single();
        Assert.Equal(BackgroundDownloadState.Completed, snapshot.State);
        Assert.Equal(0, manager.ActiveCount);
    }

    [Fact]
    public void CancelAll_CancelsOnlyActive()
    {
        var manager = new BackgroundDownloadManager();
        var active1 = manager.Start("a", "A");
        var active2 = manager.Start("b", "B");
        manager.Start("c", "C");
        manager.Complete("c");

        manager.CancelAll();

        Assert.Equal(BackgroundDownloadState.Cancelled, active1.State);
        Assert.Equal(BackgroundDownloadState.Cancelled, active2.State);
        Assert.Equal(BackgroundDownloadState.Completed, manager.Snapshot().Single(d => d.Id == "c").State);
        Assert.Equal(0, manager.ActiveCount);
    }

    [Fact]
    public void ClearFinished_RemovesNonActive()
    {
        var manager = new BackgroundDownloadManager();
        manager.Start("done", "Готово");
        manager.Complete("done");
        manager.Start("active", "Активно");

        manager.ClearFinished();

        var snapshot = manager.Snapshot();
        Assert.Single(snapshot);
        Assert.Equal("active", snapshot[0].Id);
    }

    [Fact]
    public void Progress_EventsFiredOnEveryChange()
    {
        var manager = new BackgroundDownloadManager();
        var changes = 0;
        manager.Changed += () => changes++;

        manager.Start("id", "Файл");
        manager.ReportProgress("id", 0.3);
        manager.ReportProgress("id", 0.6);
        manager.ReportProgress("unknown", 0.9); // неизвестный id — события нет
        manager.Complete("id");

        // Старт + 2 прогресса + завершение; «unknown» не меняет реестр.
        Assert.Equal(4, changes);
    }

    [Fact]
    public void Start_NullOrEmptyId_Throws()
    {
        var manager = new BackgroundDownloadManager();
        Assert.Throws<ArgumentException>(() => manager.Start("", "Файл"));
        Assert.Throws<ArgumentException>(() => manager.Start("  ", "Файл"));
    }

    [Fact]
    public void ReportProgress_AfterCompleteOrCancel_IsIgnored()
    {
        // Гарантия для гонки «окно закрыто → Complete/Cancel → поздний ReportProgress»
        // (issue #352, 0.3.12.3, одиночное скачивание): поздние отчёты прогресса не
        // «оживляют» запись и не меняют её состояние/прогресс.
        var manager = new BackgroundDownloadManager();
        var changes = 0;
        manager.Changed += () => changes++;

        var completed = manager.Start("update:База:file.cf", "file.cf");
        manager.ReportProgress("update:База:file.cf", 0.5);
        manager.Complete("update:База:file.cf");
        var changesAfterComplete = changes;

        var cancelled = manager.Start("update:База:other.cfu", "other.cfu");
        manager.Cancel("update:База:other.cfu");
        var changesAfterCancel = changes;

        // Поздние отчёты после завершения и после отмены.
        manager.ReportProgress("update:База:file.cf", 0.9);
        manager.ReportProgress("update:База:other.cfu", 0.7);

        Assert.Equal(BackgroundDownloadState.Completed, completed.State);
        Assert.Equal(1.0, completed.Progress);
        Assert.Equal(BackgroundDownloadState.Cancelled, cancelled.State);
        Assert.Equal(0.0, cancelled.Progress);
        // Поздние отчёты не поднимали событий Changed (последний Changed был от Cancel).
        Assert.Equal(changesAfterCancel, changes);
        Assert.True(changesAfterComplete < changesAfterCancel); // завершение + отмена дали события
        Assert.Equal(0, manager.ActiveCount);
    }

    [Fact]
    public async Task Cancel_SingleUpdateEntry_PropagatesToToken()
    {
        // Симуляция маршрутизации одиночного скачивания файла обновления конфигурации
        // (issue #352, 0.3.12.3): запись с id-форматом "update:<база>:<файл>" создаётся
        // до скачивания, отмена из индикатора главного окна сигнализирует в токен,
        // по которому Task.Run-загрузка обязана прерваться с OperationCanceledException,
        // состояние записи — Cancelled (аналог Download_ContinuesAfterWindowClose_Simulation).
        var manager = new BackgroundDownloadManager();

        var entry = manager.Start("update:База:file.cf", "file.cf");
        Assert.True(entry.IsActive);

        var downloadTask = Task.Run(async () =>
        {
            // Загрузка «висит» до отмены, как сетевой запрос на скачании.
            await Task.Delay(Timeout.Infinite, entry.Cancellation.Token);
            return "done";
        });

        Assert.True(manager.Cancel("update:База:file.cf"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => downloadTask);
        Assert.Equal(BackgroundDownloadState.Cancelled, entry.State);
        Assert.False(entry.IsActive);
        Assert.Equal(0, manager.ActiveCount);
    }
}
