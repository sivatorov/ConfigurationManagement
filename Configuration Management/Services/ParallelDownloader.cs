using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Configuration_Management.Services;

/// <summary>
/// Многопоточная загрузка файла по HTTP Range (по аналогии с менеджерами загрузок).
/// Файл делится на N сегментов; каждый сегмент скачивается отдельным GET-запросом с
/// заголовком Range в собственный частичный файл <c><dest>.<i>.part</c>, после
/// чего сегменты последовательно склеиваются в итоговый файл, а части удаляются.
/// Используется для ускорения загрузки обновления, когда провайдер режет скорость
/// на одно соединение (issue #284): браузер однопоточно даёт низкую скорость, а менеджер
/// с несколькими потоками — максимум.
/// <para>
/// Класс не имеет платформенных зависимостей и собирается в обеих версиях приложения
/// (Windows/WPF и Linux/Avalonia). Чистые функции разбиения на диапазоны, выбора режима
/// и агрегации прогресса покрыты юнит-тестами в ConfigurationManagement.Tests.
/// При любом сбое параллельного режима <see cref="TryDownloadAsync"/> возвращает null —
/// вызывающий код переходит к обычной однопоточной загрузке с докачкой.
/// </para>
/// </summary>
internal static class ParallelDownloader
{
    /// <summary>Максимальное число одновременных соединений (разумный предел для GitHub/CDN).</summary>
    internal const int DefaultMaxParallelism = 8;

    /// <summary>
    /// Минимальный размер сегмента. При меньшем размере файла многопоточность не даёт
    /// выигрыша (накладные расходы на соединения больше пользы) — остаётся один сегмент,
    /// и вызывающий код использует однопоточную загрузку.
    /// </summary>
    internal const long MinSegmentBytes = 1024 * 1024; // 1 МБ

    /// <summary>Максимум попыток докачки одного сегмента (как в однопоточной загрузке).</summary>
    private const int MaxAttemptsPerSegment = 12;

    /// <summary>Таймаут одной попытки чтения сегмента (защита от «залипшего» соединения).</summary>
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromMinutes(20);

    /// <summary>Буфер чтения/записи (1 МБ — как в однопоточной загрузке, фикс 0.3.9.39).</summary>
    private const int BufferSize = 1024 * 1024;

    /// <summary>Диапазон байт одного сегмента (конец включительно).</summary>
    internal readonly record struct DownloadRange(long Start, long End)
    {
        /// <summary>Число байт в диапазоне.</summary>
        public long Length => End - Start + 1;
    }

    /// <summary>
    /// Считает число сегментов для файла размером <paramref name="totalBytes"/>: не больше
    /// <paramref name="maxSegments"/> и не больше <c>totalBytes / minSegmentBytes</c>, чтобы
    /// каждый сегмент был не меньше минимального. Возвращает 0 для пустого файла и 1 для
    /// файла, меньшего минимального сегмента (многопоточность бессмысленна).
    /// </summary>
    internal static int ComputeSegmentCount(long totalBytes, int maxSegments, long minSegmentBytes = MinSegmentBytes)
    {
        if (totalBytes <= 0)
            return 0;

        if (maxSegments < 1)
            maxSegments = 1;
        if (minSegmentBytes < 1)
            minSegmentBytes = 1;

        var byMinSize = (int)(totalBytes / minSegmentBytes);
        if (byMinSize < 1)
            byMinSize = 1;
        return Math.Min(byMinSize, maxSegments);
    }

    /// <summary>
    /// Делит файл на смежные непересекающиеся диапазоны, покрывающие весь файл от 0 до
    /// <c>totalBytes - 1</c>. Число диапазонов — <see cref="ComputeSegmentCount"/>. Размеры
    /// сегментов отличаются не более чем на 1 байт (остаток от деления распределяется по
    /// первым сегментам); последний сегмент всегда заканчивается ровно на <c>totalBytes - 1</c>.
    /// </summary>
    internal static List<DownloadRange> SplitRanges(long totalBytes, int maxSegments, long minSegmentBytes = MinSegmentBytes)
    {
        var result = new List<DownloadRange>();
        if (totalBytes <= 0)
            return result;

        var count = ComputeSegmentCount(totalBytes, maxSegments, minSegmentBytes);
        if (count <= 1)
        {
            result.Add(new DownloadRange(0, totalBytes - 1));
            return result;
        }

        var baseLength = totalBytes / count;
        var remainder = totalBytes % count;
        long start = 0;
        for (var i = 0; i < count; i++)
        {
            var length = baseLength + (i < remainder ? 1 : 0);
            var end = start + length - 1;
            result.Add(new DownloadRange(start, end));
            start = end + 1;
        }

        // Гарантия покрытия файла до последнего байта (защита от ошибок округления).
        result[count - 1] = result[count - 1] with { End = totalBytes - 1 };
        return result;
    }

    /// <summary>
    /// Признак того, что многопоточная загрузка имеет смысл: размер файла известен
    /// и сегментов больше одного (иначе выигрыша нет — остаётся однопоточный путь).
    /// </summary>
    internal static bool CanParallelize(long totalBytes, int maxSegments, long minSegmentBytes = MinSegmentBytes)
        => totalBytes > 0 && ComputeSegmentCount(totalBytes, maxSegments, minSegmentBytes) > 1;

    /// <summary>
    /// Агрегирует прогресс многопоточной загрузки: сумма скачанного по всем сегментам
    /// делится на общий размер файла. Процент публикуется не чаще раза на целый процент
    /// (защита от просадки скорости из-за частых обновлений интерфейса — фикс 0.3.9.39).
    /// Потокобезопасен: сегменты добавляют байты из своих задач.
    /// </summary>
    internal sealed class ParallelProgressAggregator
    {
        private readonly long _totalBytes;
        private readonly Action<double>? _report;
        private readonly object _gate = new();
        private long _downloaded;
        private int _lastPercent = -1;

        public ParallelProgressAggregator(long totalBytes, Action<double>? report)
        {
            _totalBytes = totalBytes;
            _report = report;
        }

        /// <summary>Суммарно скачано байт (включая докачанные ранее части).</summary>
        public long Downloaded => Interlocked.Read(ref _downloaded);

        /// <summary>Добавляет скачанные байты и публикует процент, если он изменился.</summary>
        public void Add(long delta)
        {
            if (delta <= 0)
                return;
            Interlocked.Add(ref _downloaded, delta);
            Publish();
        }

        /// <summary>Публикует текущий процент (вызывается в начале и по завершении загрузки).</summary>
        public void Publish()
        {
            if (_report is null || _totalBytes <= 0)
                return;

            var percent = (int)Math.Min(100, Downloaded * 100.0 / _totalBytes);
            lock (_gate)
            {
                if (percent == _lastPercent)
                    return;
                _lastPercent = percent;
            }
            _report(percent);
        }
    }

    /// <summary>
    /// Пытается скачать файл многопоточно. При успехе возвращает путь к итоговому файлу;
    /// при любом сбое — null: сервер не поддерживает Range (200 вместо 206), размер файла
    /// неизвестен, файл слишком мал, ошибка сегмента после всех повторов или несовпадение
    /// размера после склейки. В этом случае вызывающий код переходит к обычной однопоточной
    /// загрузке с докачкой. Прогресс (проценты 0–100) передаётся через
    /// <paramref name="reportProgress"/> — интерфейс не меняется по сравнению с событием
    /// <c>DownloadProgressChanged</c> однопоточной загрузки.
    /// </summary>
    internal static async Task<string?> TryDownloadAsync(
        HttpClient http, string url, string destPath,
        Action<double>? reportProgress, CancellationToken cancellationToken = default)
    {
        var probe = await ProbeAsync(http, url, cancellationToken).ConfigureAwait(false);
        if (probe is null)
            return null;

        var ranges = SplitRanges(probe.TotalBytes, DefaultMaxParallelism, MinSegmentBytes);
        if (ranges.Count < 2)
            return null; // маленький файл — многопоточность не даёт выигрыша.

        var dir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // Прогресс стартует с учётом уже скачанных ранее частей (докачка между запусками).
        var progress = new ParallelProgressAggregator(probe.TotalBytes, reportProgress);
        for (var i = 0; i < ranges.Count; i++)
        {
            var partPath = PartPath(destPath, i);
            var len = SafeFileLength(partPath);
            if (len > ranges[i].Length)
            {
                // Часть повреждена (длиннее диапазона) — начинаем сегмент заново.
                TryDelete(partPath);
                continue;
            }
            progress.Add(len);
        }
        progress.Publish();

        using var sharedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var semaphore = new SemaphoreSlim(DefaultMaxParallelism);

        var tasks = new List<Task>(ranges.Count);
        for (var i = 0; i < ranges.Count; i++)
        {
            var index = i;
            var partPath = PartPath(destPath, i);
            tasks.Add(Task.Run(async () =>
            {
                await semaphore.WaitAsync(sharedCts.Token).ConfigureAwait(false);
                try
                {
                    await DownloadSegmentAsync(http, probe.Uri, partPath, ranges[index], progress, sharedCts.Token)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // Сбой одного сегмента останавливает остальные (общая отмена).
                    sharedCts.Cancel();
                    throw;
                }
                finally
                {
                    semaphore.Release();
                }
            }, CancellationToken.None));
        }

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch
        {
            CleanupParts(destPath, ranges.Count);
            return null;
        }

        if (!await TryMergeAsync(destPath, ranges, probe.TotalBytes, cancellationToken).ConfigureAwait(false))
        {
            CleanupParts(destPath, ranges.Count);
            return null;
        }

        progress.Publish(); // 100%
        return destPath;
    }

    /// <summary>
    /// Определяет размер файла и поддержку Range запросом GET с <c>Range: bytes=0-0</c>.
    /// Ответ 206 подтверждает поддержку Range и содержит полный размер в Content-Range;
    /// ответ 200 означает, что сервер игнорирует Range (многопоточность невозможна).
    /// Возвращается также итоговый URI после редиректов GitHub (S3): сегменты запрашиваются
    /// уже по нему, без повторных редиректов для каждого сегмента.
    /// </summary>
    private static async Task<ProbeResult?> ProbeAsync(HttpClient http, string url, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new RangeHeaderValue(0, 0);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            using var response = await http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);

            if (response.StatusCode != HttpStatusCode.PartialContent)
                return null; // 200: Range проигнорирован, либо ошибка.

            var total = response.Content.Headers.ContentRange?.Length ?? -1;
            if (total <= 0)
                return null;

            var uri = response.RequestMessage?.RequestUri;
            if (uri is null)
                return null;

            // Дренируем тело (1 байт), чтобы соединение вернулось в пул.
            try { await response.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false); } catch { /* не критично */ }

            return new ProbeResult(total, uri);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Скачивает один сегмент в свой .part-файл с докачкой: при каждом повторе Range
    /// запрашивается от текущего размера части (устойчивость к обрывам, как в однопоточной
    /// загрузке). Число попыток — <see cref="MaxAttemptsPerSegment"/>, пауза между ними
    /// растёт с номером попытки. Ответ 200 (сервер игнорирует Range) считается фатальным
    /// для параллельного режима — исключение прерывает остальные сегменты.
    /// </summary>
    private static async Task DownloadSegmentAsync(
        HttpClient http, Uri uri, string partPath, DownloadRange range,
        ParallelProgressAggregator progress, CancellationToken ct)
    {
        // Уже скачанное учитывается в агрегированном прогрессе с самого начала.
        var existing = SafeFileLength(partPath);
        if (existing > range.Length)
        {
            // Часть повреждена (длиннее диапазона) — начинаем сегмент заново.
            TryDelete(partPath);
            existing = 0;
        }
        progress.Add(Math.Min(existing, range.Length));

        for (var attempt = 1; attempt <= MaxAttemptsPerSegment; attempt++)
        {
            try
            {
                var resumed = SafeFileLength(partPath);
                if (resumed > range.Length)
                {
                    TryDelete(partPath);
                    resumed = 0;
                }

                var remaining = range.Length - resumed;
                if (remaining <= 0)
                    return; // часть уже полная (например, после записи и сбоя Flush).

                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                // Диапазон от текущей позиции части до конца сегмента (концы включены).
                request.Headers.Range = new RangeHeaderValue(range.Start + resumed, range.End);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(AttemptTimeout);
                using var response = await http
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                    .ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    // Сервер проигнорировал Range (отдал весь файл) — многопоточность невозможна.
                    throw new RangeNotSupportedException();
                }
                response.EnsureSuccessStatusCode();

                await using var source = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
                await using (var target = new FileStream(
                    partPath, resumed > 0 ? FileMode.Append : FileMode.Create,
                    FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous))
                {
                    var buffer = new byte[BufferSize];
                    long written = 0;
                    int read;
                    while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cts.Token).ConfigureAwait(false)) > 0)
                    {
                        await target.WriteAsync(buffer.AsMemory(0, read), cts.Token).ConfigureAwait(false);
                        written += read;
                        progress.Add(read);
                    }
                }

                // Сегмент должен иметь ровно длину диапазона: сервер мог отдать меньше
                // (обрыв «без исключения») или больше запрошенного (нестандартный Range).
                if (resumed + SafeFileLength(partPath) != range.Length)
                {
                    TryDelete(partPath);
                    throw new IOException("Сегмент загружен не полностью.");
                }

                return;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Таймаут попытки — повтор с паузой. Отмена общего токена (сбой другого
                // сегмента) сюда не попадает: исключение уходит выше и прерывает загрузку.
            }
            catch (RangeNotSupportedException)
            {
                throw; // повторять бессмысленно: сервер не поддерживает Range.
            }
            catch
            {
                // Обрыв соединения — частичный файл остаётся, повторяем с докачкой.
            }

            if (attempt < MaxAttemptsPerSegment)
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(attempt, 5)), ct).ConfigureAwait(false);
        }

        throw new IOException($"Не удалось загрузить сегмент {range.Start}-{range.End}.");
    }

    /// <summary>
    /// Последовательно склеивает .part-файлы в итоговый файл и проверяет его размер.
    /// При несовпадении размера итоговый файл удаляется (возвращается false).
    /// </summary>
    private static async Task<bool> TryMergeAsync(
        string destPath, IReadOnlyList<DownloadRange> ranges, long totalBytes, CancellationToken ct)
    {
        try
        {
            var dir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            await using (var target = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous))
            {
                for (var i = 0; i < ranges.Count; i++)
                {
                    var partPath = PartPath(destPath, i);
                    await using var part = new FileStream(partPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous);
                    await part.CopyToAsync(target, BufferSize, ct).ConfigureAwait(false);
                }
            }

            if (SafeFileLength(destPath) != totalBytes)
            {
                TryDelete(destPath);
                return false;
            }

            return true;
        }
        catch
        {
            TryDelete(destPath);
            return false;
        }
    }

    /// <summary>Путь к частичному файлу сегмента.</summary>
    private static string PartPath(string destPath, int index) => $"{destPath}.{index}.part";

    /// <summary>Размер файла или 0, если файл отсутствует.</summary>
    private static long SafeFileLength(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            return fi.Exists ? fi.Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Удаляет все частичные файлы сегментов.</summary>
    private static void CleanupParts(string destPath, int count)
    {
        for (var i = 0; i < count; i++)
            TryDelete(PartPath(destPath, i));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Не критично — временный файл останется в %TEMP%.
        }
    }

    /// <summary>Результат проверки поддержки многопоточной загрузки: размер и URI без редиректов.</summary>
    private sealed class ProbeResult
    {
        public ProbeResult(long totalBytes, Uri uri)
        {
            TotalBytes = totalBytes;
            Uri = uri;
        }

        public long TotalBytes { get; }
        public Uri Uri { get; }
    }

    /// <summary>Сигнал того, что сервер не поддерживает Range (ответ 200 на запрос с Range).</summary>
    private sealed class RangeNotSupportedException : Exception
    {
    }
}