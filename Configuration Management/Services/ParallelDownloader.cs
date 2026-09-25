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
/// Файл делится на N сегментов (зон); каждый сегмент скачивается отдельным GET-запросом
/// с заголовком Range в собственный частичный файл <c><dest>.<i>.part</c>, после чего
/// сегменты последовательно склеиваются в итоговый файл, а части удаляются.
/// Используется для ускорения загрузки обновления, когда провайдер режет скорость
/// на одно соединение (issue #284): браузер однопоточно даёт низкую скорость, а менеджер
/// с несколькими потоками — максимум.
/// <para>
/// Начиная с 0.3.9.62 работа между сегментами распределяется динамически
/// (work stealing): каждый сегмент качает свою стартовую зону небольшими кусками
/// (~1 МБ), а завершив её — берёт следующий незанятый кусок из любой незавершённой
/// зоны, дописывая его в ЕЁ .part-файл. Благодаря этому все соединения работают до
/// самого конца файла, «хвост» докачивается несколькими потоками одновременно, а не
/// одним (раньше после ~50% скорость падала, а на 90–95% загрузка «замирала»).
/// Непрерывность каждого .part сохраняется: куски одной зоны выдаются строго по
/// одному и всегда начинаются с текущего конца её .part.
/// </para>
/// <para>
/// Класс не имеет платформенных зависимостей и собирается в обеих версиях приложения
/// (Windows/WPF и Linux/Avalonia). Чистые функции разбиения на диапазоны, выбора режима,
/// агрегации прогресса и динамического распределения работы покрыты юнит-тестами
/// в ConfigurationManagement.Tests. При любом сбое параллельного режима
/// <see cref="TryDownloadAsync"/> возвращает null — вызывающий код переходит к обычной
/// однопоточной загрузке с докачкой.
/// </para>
/// </summary>
internal static class ParallelDownloader
{
    /// <summary>Максимальное число одновременных соединений (разумный предел для GitHub/CDN).</summary>
    internal const int DefaultMaxParallelism = 8;

    /// <summary>
    /// Минимальный размер сегмента (он же размер окна динамической балансировки).
    /// При меньшем размере файла многопоточность не даёт выигрыша (накладные расходы
    /// на соединения больше пользы) — остаётся один сегмент, и вызывающий код использует
    /// однопоточную загрузку.
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
    /// Координатор динамического распределения работы между сегментами (work stealing,
    /// фикс 0.3.9.62, issue #284). Файл разбит на N стартовых зон (как раньше), но каждая
    /// зона качается небольшими кусками-окнами (~1 МБ). Рабочий поток сначала качает свою
    /// зону, а завершив её — берёт следующий незанятый кусок из любой незавершённой зоны,
    /// дописывая его в ЕЁ .part-файл. Так все соединения работают до самого конца файла,
    /// а «хвост» докачивается сразу несколькими потоками (раньше после ~50% скорость падала,
    /// а на 90–95% загрузка «замирала» на одном соединении).
    /// <para>
    /// Инварианты: куски одной зоны выдаются строго по одному (в любой момент максимум один
    /// поток пишет в её .part) и всегда начинаются с текущего конца её .part — непрерывность
    /// каждой части сохраняется, последовательная склейка .part не меняется. Поток, которому
    /// не досталось куска (все зоны временно заняты чужими кусками), ждёт освобождения.
    /// </para>
    /// </summary>
    internal sealed class SegmentWorkCoordinator
    {
        private readonly object _gate = new();
        private readonly long _windowBytes;
        private readonly DownloadRange[] _zones;
        private readonly long[] _progress;     // байт, уже записанных в .part зоны
        private readonly long[] _issuedLength; // длина выданного куска или -1 (кусок не выдан)
        private int _completedZones;

        public SegmentWorkCoordinator(IReadOnlyList<DownloadRange> zones, long windowBytes)
        {
            _zones = zones.ToArray();
            _windowBytes = Math.Max(1, windowBytes);
            _progress = new long[_zones.Length];
            _issuedLength = Enumerable.Repeat(-1L, _zones.Length).ToArray();
        }

        /// <summary>Число зон (стартовых сегментов).</summary>
        public int ZoneCount => _zones.Length;

        /// <summary>Все ли зоны полностью скачаны.</summary>
        public bool IsComplete
        {
            get { lock (_gate) { return _completedZones == _zones.Length; } }
        }

        /// <summary>
        /// Устанавливает фактический прогресс зоны (размер существующего .part при докачке
        /// между запусками). Байты, уже скачанные ранее, в агрегированный прогресс добавляет
        /// вызывающий код.
        /// </summary>
        public void SetProgress(int zoneIndex, long bytesWritten)
        {
            lock (_gate)
            {
                var clamped = Math.Clamp(bytesWritten, 0, _zones[zoneIndex].Length);
                _progress[zoneIndex] = clamped;
                if (clamped >= _zones[zoneIndex].Length)
                    _completedZones++;
            }
        }

        /// <summary>
        /// Выдаёт рабочему потоку <paramref name="workerId"/> следующий кусок: сначала из
        /// предпочитаемой зоны (свой сегмент), затем — из любой незавершённой (work stealing).
        /// Возвращает false, когда свободной работы нет (все зоны завершены либо их куски
        /// временно заняты другими потоками).
        /// </summary>
        public bool TryGetChunk(int workerId, int preferredZone, out int zoneIndex, out DownloadRange range)
        {
            lock (_gate)
            {
                if (TryTakeFromZone(preferredZone, out zoneIndex, out range))
                    return true;
                for (var z = 0; z < _zones.Length; z++)
                {
                    if (z == preferredZone)
                        continue;
                    if (TryTakeFromZone(z, out zoneIndex, out range))
                        return true;
                }
            }
            zoneIndex = -1;
            range = default;
            return false;
        }

        /// <summary>
        /// Фиксирует запись куска в .part зоны (размер куска в байтах) и освобождает зону
        /// для следующего куска. Пробуждает потоки, ожидающие свободной работы.
        /// </summary>
        public void CommitChunk(int zoneIndex, long writtenBytes)
        {
            lock (_gate)
            {
                _progress[zoneIndex] += writtenBytes;
                _issuedLength[zoneIndex] = -1;
                if (_progress[zoneIndex] >= _zones[zoneIndex].Length
                    && _progress[zoneIndex] - writtenBytes < _zones[zoneIndex].Length)
                {
                    _completedZones++;
                }
                Monitor.PulseAll(_gate);
            }
        }

        /// <summary>
        /// Ждёт появления свободной работы (когда все зоны заняты кусками других потоков).
        /// Возвращает true, если работу можно запросить заново; false при отмене.
        /// </summary>
        public bool WaitForWork(CancellationToken ct)
        {
            lock (_gate)
            {
                while (true)
                {
                    if (_completedZones == _zones.Length || HasFreeWork())
                        return true;
                    if (ct.IsCancellationRequested)
                    {
                        // Единообразно с сетевыми операциями: отмена прерывает загрузку.
                        ct.ThrowIfCancellationRequested();
                        return false;
                    }
                    // В .NET Core Monitor.Wait не принимает CancellationToken — ждём
                    // короткими интервалами и перепроверяем токен и наличие работы.
                    Monitor.Wait(_gate, 200);
                }
            }
        }

        /// <summary>Есть ли зона, у которой можно взять кусок прямо сейчас.</summary>
        private bool HasFreeWork()
        {
            for (var z = 0; z < _zones.Length; z++)
            {
                if (_issuedLength[z] < 0 && _progress[z] < _zones[z].Length)
                    return true;
            }
            return false;
        }

        /// <summary>Пытается выдать кусок из конкретной зоны (под lock вызывающего).</summary>
        private bool TryTakeFromZone(int zoneIndex, out int resultZone, out DownloadRange range)
        {
            resultZone = zoneIndex;
            if (_issuedLength[zoneIndex] >= 0 || _progress[zoneIndex] >= _zones[zoneIndex].Length)
            {
                range = default;
                return false;
            }

            var startInZone = _progress[zoneIndex];
            var chunkLength = Math.Min(_windowBytes, _zones[zoneIndex].Length - startInZone);
            _issuedLength[zoneIndex] = chunkLength;
            range = new DownloadRange(
                _zones[zoneIndex].Start + startInZone,
                _zones[zoneIndex].Start + startInZone + chunkLength - 1);
            return true;
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
    /// <para>
    /// Работа между сегментами распределяется динамически через
    /// <see cref="SegmentWorkCoordinator"/>: завершивший свою зону сегмент берёт следующий
    /// незанятый кусок из любой незавершённой зоны (фикс 0.3.9.62, issue #284), поэтому все
    /// соединения заняты до самого конца загрузки.
    /// </para>
    /// </summary>
    internal static async Task<string?> TryDownloadAsync(
        HttpClient http, string url, string destPath,
        Action<double>? reportProgress, CancellationToken cancellationToken = default)
    {
        var probe = await ProbeAsync(http, url, cancellationToken).ConfigureAwait(false);
        if (probe is null)
            return null;

        var zones = SplitRanges(probe.TotalBytes, DefaultMaxParallelism, MinSegmentBytes);
        if (zones.Count < 2)
            return null; // маленький файл — многопоточность не даёт выигрыша.

        var dir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // Прогресс стартует с учётом уже скачанных ранее частей (докачка между запусками).
        var progress = new ParallelProgressAggregator(probe.TotalBytes, reportProgress);
        var coordinator = new SegmentWorkCoordinator(zones, MinSegmentBytes);
        for (var i = 0; i < zones.Count; i++)
        {
            var partPath = PartPath(destPath, i);
            var len = SafeFileLength(partPath);
            if (len > zones[i].Length)
            {
                // Часть повреждена (длиннее зоны) — начинаем сегмент заново.
                TryDelete(partPath);
                continue;
            }
            coordinator.SetProgress(i, len);
            progress.Add(len);
        }
        progress.Publish();

        using var sharedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var semaphore = new SemaphoreSlim(DefaultMaxParallelism);

        var tasks = new List<Task>(zones.Count);
        for (var i = 0; i < zones.Count; i++)
        {
            var workerId = i;
            tasks.Add(Task.Run(async () =>
            {
                await semaphore.WaitAsync(sharedCts.Token).ConfigureAwait(false);
                try
                {
                    while (!coordinator.IsComplete)
                    {
                        if (!coordinator.TryGetChunk(workerId, workerId, out var zoneIndex, out var chunk))
                        {
                            // Все зоны временно заняты кусками других потоков — ждём освобождения.
                            if (!coordinator.WaitForWork(sharedCts.Token))
                                break;
                            continue;
                        }

                        var partPath = PartPath(destPath, zoneIndex);
                        await DownloadChunkAsync(
                                http, probe.Uri, partPath, zones[zoneIndex], chunk, progress, sharedCts.Token)
                            .ConfigureAwait(false);
                        coordinator.CommitChunk(zoneIndex, chunk.Length);
                    }
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
            CleanupParts(destPath, zones.Count);
            return null;
        }

        if (!await TryMergeAsync(destPath, zones, probe.TotalBytes, cancellationToken).ConfigureAwait(false))
        {
            CleanupParts(destPath, zones.Count);
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
    /// Скачивает один кусок (окно ~1 МБ) зоны в её .part-файл с докачкой: при каждом повторе
    /// Range запрашивается от фактического размера части (устойчивость к обрывам, как
    /// в однопоточной загрузке). Куски одной зоны выдаются строго по одному и следуют друг
    /// за другом, поэтому .part зоны остаётся непрерывным, а докачка между запусками
    /// сохраняется. Число попыток — <see cref="MaxAttemptsPerSegment"/>, пауза между ними
    /// растёт с номером попытки. Ответ 200 (сервер игнорирует Range) считается фатальным
    /// для параллельного режима — исключение прерывает остальные сегменты.
    /// </summary>
    private static async Task DownloadChunkAsync(
        HttpClient http, Uri uri, string partPath, DownloadRange zone, DownloadRange range,
        ParallelProgressAggregator progress, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= MaxAttemptsPerSegment; attempt++)
        {
            try
            {
                // Сколько байт зоны уже лежит в .part (может быть больше прогресса зоны,
                // если прошлый запуск оборвался в середине куска).
                var existing = SafeFileLength(partPath);
                if (existing > zone.Length)
                {
                    // Часть повреждена (длиннее зоны) — начинаем зону заново.
                    TryDelete(partPath);
                    existing = 0;
                }

                // Сколько байт ИМЕННО этого куска уже записано.
                var inChunk = Math.Clamp(existing - (range.Start - zone.Start), 0, range.Length);
                var remaining = range.Length - inChunk;
                if (remaining <= 0)
                    return; // кусок уже записан (например, после записи и сбоя Flush).

                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                // Диапазон от текущей позиции куска до его конца (концы включены).
                request.Headers.Range = new RangeHeaderValue(range.Start + inChunk, range.End);

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
                    partPath, FileMode.Append,
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

                // После куска .part зоны должен иметь ровно ожидаемый размер: сервер мог
                // отдать меньше (обрыв «без исключения») или больше запрошенного (нестандартный
                // Range) — в этом случае зона повреждена и начинается заново.
                if (SafeFileLength(partPath) != (range.Start - zone.Start) + range.Length)
                {
                    TryDelete(partPath);
                    throw new IOException("Кусок сегмента загружен не полностью.");
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

        throw new IOException($"Не удалось загрузить кусок {range.Start}-{range.End}.");
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