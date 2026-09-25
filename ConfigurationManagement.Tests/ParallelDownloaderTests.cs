using System;
using System.Collections.Generic;
using Configuration_Management.Services;
using Xunit;

namespace ConfigurationManagement.Tests;

/// <summary>
/// Тесты чистых частей многопоточной загрузки (issue #284): разбиение файла на диапазоны
/// HTTP Range, выбор режима, агрегация прогресса и динамическое распределение работы
/// между сегментами (SegmentWorkCoordinator, work stealing). Сетевая часть
/// (ParallelDownloader.TryDownloadAsync) здесь не покрывается — она проверяется реальной
/// загрузкой обновления.
/// </summary>
public sealed class ParallelDownloaderTests
{
    // ---------- Разбиение на диапазоны ----------

    [Fact]
    public void SplitRanges_LargeFile_ProducesRequestedSegmentCount()
    {
        var ranges = ParallelDownloader.SplitRanges(1000, 8, 1);

        Assert.Equal(8, ranges.Count);
        Assert.Equal(0, ranges[0].Start);
        Assert.Equal(999, ranges[^1].End);
    }

    [Fact]
    public void SplitRanges_RangesAreContiguousAndCoverWholeFile()
    {
        const long total = 10_000_000;
        var ranges = ParallelDownloader.SplitRanges(total, 8, 1);

        long cursor = 0;
        foreach (var range in ranges)
        {
            Assert.Equal(cursor, range.Start);
            Assert.True(range.End >= range.Start);
            Assert.Equal(range.Length, range.End - range.Start + 1);
            cursor = range.End + 1;
        }

        Assert.Equal(total, cursor);
    }

    [Fact]
    public void SplitRanges_SegmentCountLimitedByMinSegmentSize()
    {
        const long total = 10_000_000; // 10 МБ
        // Минимум 2 МБ на сегмент: ожидаем 5 сегментов, а не 8.
        var ranges = ParallelDownloader.SplitRanges(total, 8, 2_000_000);

        Assert.Equal(5, ranges.Count);
    }

    [Fact]
    public void SplitRanges_SmallFile_ReturnsSingleRange()
    {
        var ranges = ParallelDownloader.SplitRanges(512 * 1024, 8, 1024 * 1024);

        var expected = new ParallelDownloader.DownloadRange(0, 512 * 1024 - 1);
        Assert.Single(ranges);
        Assert.Equal(expected, ranges[0]);
    }

    [Fact]
    public void SplitRanges_LastRangeAlwaysEndsAtTotalMinusOne()
    {
        const long total = 12345;
        var ranges = ParallelDownloader.SplitRanges(total, 8, 1);

        Assert.Equal(total - 1, ranges[^1].End);
    }

    [Fact]
    public void SplitRanges_NonDivisibleTotal_DistributesRemainder()
    {
        // 10 байт на 3 сегмента: 4 + 3 + 3 (остаток уходит первым сегментам).
        var ranges = ParallelDownloader.SplitRanges(10, 3, 1);

        Assert.Equal(3, ranges.Count);
        Assert.Equal(4, ranges[0].Length);
        Assert.Equal(3, ranges[1].Length);
        Assert.Equal(3, ranges[2].Length);
    }

    [Fact]
    public void SplitRanges_MaxSegmentsOne_CoversWholeFile()
    {
        var ranges = ParallelDownloader.SplitRanges(777, 1, 1);

        Assert.Single(ranges);
        Assert.Equal(new ParallelDownloader.DownloadRange(0, 776), ranges[0]);
    }

    // ---------- Число сегментов и выбор режима ----------

    [Fact]
    public void ComputeSegmentCount_ClampsToMaxSegments()
    {
        Assert.Equal(8, ParallelDownloader.ComputeSegmentCount(100 * 1024 * 1024, 8, 1024 * 1024));
    }

    [Fact]
    public void ComputeSegmentCount_ZeroForEmptyFile()
    {
        Assert.Equal(0, ParallelDownloader.ComputeSegmentCount(0, 8, 1024 * 1024));
    }

    [Fact]
    public void ComputeSegmentCount_SingleSegmentForTinyFile()
    {
        Assert.Equal(1, ParallelDownloader.ComputeSegmentCount(500 * 1024, 8, 1024 * 1024));
    }

    [Fact]
    public void CanParallelize_FalseWhenParallelMakesNoSense()
    {
        // Пустой файл — нет.
        Assert.False(ParallelDownloader.CanParallelize(0, 8, 1024 * 1024));
        // Файл меньше минимального сегмента — один сегмент, выигрыша нет.
        Assert.False(ParallelDownloader.CanParallelize(500 * 1024, 8, 1024 * 1024));
    }

    [Fact]
    public void CanParallelize_TrueForLargeFile()
    {
        Assert.True(ParallelDownloader.CanParallelize(10 * 1024 * 1024, 8, 1024 * 1024));
    }

    // ---------- Агрегация прогресса ----------

    [Fact]
    public void Progress_AggregatesBytesAcrossSegments()
    {
        var reports = new List<double>();
        var aggregator = new ParallelDownloader.ParallelProgressAggregator(1000, reports.Add);

        aggregator.Add(250);
        aggregator.Add(250);
        Assert.Equal(500, aggregator.Downloaded);

        aggregator.Add(500);
        Assert.Equal(1000, aggregator.Downloaded);
        Assert.Equal(100, reports[^1]);
    }

    [Fact]
    public void Progress_PublishesOnlyOnIntegerPercentChange()
    {
        var reports = new List<double>();
        var aggregator = new ParallelDownloader.ParallelProgressAggregator(1000, reports.Add);

        aggregator.Add(5);  // 0.5% -> 0% (публикуется первый процент)
        aggregator.Add(4);  // 0.9% -> всё ещё 0%, без публикации
        aggregator.Add(1);  // 1.0% -> 1%
        aggregator.Add(50); // 6.0% -> 6%
        aggregator.Add(4);  // 6.4% -> 6%, без публикации

        Assert.Equal(new List<double> { 0, 1, 6 }, reports);
    }

    [Fact]
    public void Progress_CapsAtHundredAndPublishesOnce()
    {
        var reports = new List<double>();
        var aggregator = new ParallelDownloader.ParallelProgressAggregator(1000, reports.Add);

        aggregator.Add(1000);
        aggregator.Add(500); // сверх размера — процент остаётся 100

        Assert.Equal(100, reports[^1]);
        Assert.Single(reports);
    }

    [Fact]
    public void Progress_IgnoresNonPositiveDelta()
    {
        var reports = new List<double>();
        var aggregator = new ParallelDownloader.ParallelProgressAggregator(1000, reports.Add);

        aggregator.Add(0);
        aggregator.Add(-5);

        Assert.Empty(reports);
        Assert.Equal(0, aggregator.Downloaded);
    }

    [Fact]
    public void Progress_ForcePublishDoesNotDuplicateSamePercent()
    {
        var reports = new List<double>();
        var aggregator = new ParallelDownloader.ParallelProgressAggregator(1000, reports.Add);

        aggregator.Add(500); // 50% -> первая публикация
        var before = reports.Count;
        aggregator.Publish(); // тот же процент — дубликата быть не должно

        Assert.Equal(new List<double> { 50 }, reports);
        Assert.Equal(before, reports.Count);
    }

    [Fact]
    public void Progress_ReportsZeroTotal_DoesNotPublish()
    {
        var reports = new List<double>();
        var aggregator = new ParallelDownloader.ParallelProgressAggregator(0, reports.Add);

        aggregator.Add(10);
        aggregator.Publish();

        Assert.Empty(reports);
    }

    // ---------- Динамическое распределение работы (фикс 0.3.9.62, issue #284) ----------

    [Fact]
    public void Coordinator_CoversWholeFileWithoutGapsOrOverlaps()
    {
        const long total = 100L * 1024 * 1024; // 100 МБ
        var zones = ParallelDownloader.SplitRanges(total, 8, 1024 * 1024);
        var coordinator = new ParallelDownloader.SegmentWorkCoordinator(zones, 1024 * 1024);

        // Эмулируем 8 рабочих потоков: каждый сначала качает свою зону, затем — чужие.
        var chunks = new List<(int Zone, ParallelDownloader.DownloadRange Range)>();
        bool any;
        do
        {
            any = false;
            for (var worker = 0; worker < 8; worker++)
            {
                if (coordinator.TryGetChunk(worker, worker, out var zone, out var range))
                {
                    chunks.Add((zone, range));
                    coordinator.CommitChunk(zone, range.Length);
                    any = true;
                }
            }
        }
        while (any);

        Assert.True(coordinator.IsComplete);

        // Каждая зона покрыта своими кусками непрерывно, без пропусков и пересечений;
        // вместе зоны покрывают весь файл от 0 до total - 1.
        for (var z = 0; z < zones.Count; z++)
        {
            var pieces = chunks.Where(c => c.Zone == z)
                .Select(c => c.Range)
                .OrderBy(r => r.Start)
                .ToList();

            long cursor = zones[z].Start;
            foreach (var piece in pieces)
            {
                Assert.Equal(cursor, piece.Start);
                Assert.True(piece.End >= piece.Start);
                cursor = piece.End + 1;
            }

            Assert.Equal(zones[z].End + 1, cursor);
        }

        Assert.Equal(total, chunks.Sum(c => c.Range.Length));
        // Последний диапазон файла обязательно назначается.
        Assert.Equal(total - 1, chunks.Max(c => c.Range.End));
    }

    [Fact]
    public void Coordinator_FinishedSegmentTakesNextUnassignedChunk()
    {
        const long total = 8L * 1024 * 1024; // 8 МБ, 2 зоны по 4 МБ
        var zones = ParallelDownloader.SplitRanges(total, 2, 1024 * 1024);
        var coordinator = new ParallelDownloader.SegmentWorkCoordinator(zones, 1024 * 1024);

        // Сегмент 0 качает свою зону [0, 4 МБ) кусками по 1 МБ.
        for (var i = 0; i < 4; i++)
        {
            Assert.True(coordinator.TryGetChunk(0, 0, out var zone, out var range));
            Assert.Equal(0, zone);
            Assert.Equal(i * 1024 * 1024, range.Start);
            coordinator.CommitChunk(zone, range.Length);
        }

        // Зона 0 завершена — вместо завершения сегмент берёт следующий незанятый
        // диапазон из зоны 1 (work stealing): все соединения работают до конца файла.
        Assert.True(coordinator.TryGetChunk(0, 0, out var stolenZone, out var stolenRange));
        Assert.Equal(1, stolenZone);
        Assert.Equal(4L * 1024 * 1024, stolenRange.Start);
        Assert.Equal(1024 * 1024, stolenRange.Length);
        coordinator.CommitChunk(stolenZone, stolenRange.Length);

        Assert.False(coordinator.IsComplete);
    }

    [Fact]
    public void Coordinator_LastRangeIsAssignedToWorker()
    {
        const long total = 10_000_000;
        var zones = ParallelDownloader.SplitRanges(total, 8, 1);
        var coordinator = new ParallelDownloader.SegmentWorkCoordinator(zones, 1024 * 1024);

        // Один рабочий поток выкачивает всё до конца — последний диапазон обязан
        // назначиться и закончиться ровно на total - 1.
        ParallelDownloader.DownloadRange? last = null;
        while (coordinator.TryGetChunk(0, 0, out var zone, out var range))
        {
            last = range;
            coordinator.CommitChunk(zone, range.Length);
        }

        Assert.NotNull(last);
        Assert.Equal(total - 1, last.Value.End);
        Assert.True(coordinator.IsComplete);
    }

    [Fact]
    public void Coordinator_RespectsExistingPartProgress()
    {
        const long total = 8L * 1024 * 1024; // 8 МБ, 2 зоны по 4 МБ
        var zones = ParallelDownloader.SplitRanges(total, 2, 1024 * 1024);
        var coordinator = new ParallelDownloader.SegmentWorkCoordinator(zones, 1024 * 1024);

        // Зона 1 [4 МБ, 8 МБ) уже скачана на 2 МБ (докачка между запусками).
        coordinator.SetProgress(1, 2L * 1024 * 1024);

        // Следующий кусок начинается сразу после уже скачанного: 4 МБ + 2 МБ = 6 МБ.
        Assert.True(coordinator.TryGetChunk(1, 1, out var zone, out var range));
        Assert.Equal(1, zone);
        Assert.Equal(6L * 1024 * 1024, range.Start);
        Assert.Equal(1024 * 1024, range.Length);
    }

    [Fact]
    public void Coordinator_BalancesWorkBetweenWorkers()
    {
        const long total = 24L * 1024 * 1024; // 24 МБ, 3 зоны по 8 МБ
        const long window = 1024 * 1024;
        var zones = ParallelDownloader.SplitRanges(total, 3, 1024 * 1024);
        var coordinator = new ParallelDownloader.SegmentWorkCoordinator(zones, window);

        // Два воркера поочерёдно берут куски; зона 2 остаётся «хвостом» и делится
        // между ними поровну — никто не заканчивает раньше другого.
        var downloaded = new long[2];
        bool any;
        do
        {
            any = false;
            for (var worker = 0; worker < 2; worker++)
            {
                if (coordinator.TryGetChunk(worker, worker, out var zone, out var range))
                {
                    downloaded[worker] += range.Length;
                    coordinator.CommitChunk(zone, range.Length);
                    any = true;
                }
            }
        }
        while (any);

        Assert.True(coordinator.IsComplete);
        Assert.Equal(total, downloaded[0] + downloaded[1]);
        // «Хвост» файла распределился между обоими соединениями примерно поровну.
        Assert.True(Math.Abs(downloaded[0] - downloaded[1]) <= window,
            $"Ожидалась балансировка, получено {downloaded[0]} vs {downloaded[1]}");
    }
}