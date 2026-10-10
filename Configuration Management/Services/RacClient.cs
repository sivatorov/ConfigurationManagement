using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Configuration_Management.Models;

namespace Configuration_Management.Services;

/// <summary>
/// Клиент утилиты rac (Remote Administration Client) — сервисный слой встроенного монитора
/// серверов 1С (цикл 0.3.9.123–0.3.9.126). Чистый сервис — без UI-зависимостей.
/// Команды выполняются прямым запуском rac (без shell, через ArgumentList) с захватом
/// stdout/stderr, параллельным чтением и таймаутом. Кодировка вывода определяется
/// автоматически: UTF-8, а на Windows — также OEM (cp866) / ANSI (cp1251) кодовые страницы
/// (issue #324: «в терминале rac работает, монитор вывод не распознаёт»). Пароль
/// администратора кластера передаётся аргументом <c>--password</c>, но НЕ пишется
/// в журнал (маскируется <see cref="SensitiveDataMasker.MaskRacPassword"/>).
/// </summary>
public sealed class RacClient : IRacClient
{
    /// <summary>Строгий UTF-8 (невалидные байты — ошибка декодирования).</summary>
    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Кодовые страницы Windows для fallback-декодирования вывода rac.</summary>
    private static readonly Encoding[] WindowsFallbackEncodings;

    static RacClient()
    {
        // Кодовые страницы 866/1251 доступны и на Linux (.NET Core) только после
        // регистрации провайдера CodePagesEncodingProvider.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        WindowsFallbackEncodings = new[] { Encoding.GetEncoding(866), Encoding.GetEncoding(1251) };
    }

    /// <summary>Таймаут выполнения одной rac-команды (30 секунд).</summary>
    public static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    private readonly IAppLogger _logger;

    /// <summary>
    /// Запомненный рабочий формат команды «job list» по ключу подключения
    /// (адрес:порт|user|clusterId, issue #324): rac 8.5.4.1878 отклоняет
    /// <c>--cluster=<uuid></c> (код -1) и, по логам 2026-10-07/08, также
    /// <c>--cluster <uuid></c> двумя токенами — добавлен формат 2 (позиционный
    /// <c><uuid></c>). Без кэша каждая загрузка данных кластера запускала rac
    /// дважды (первая попытка всегда падала), что заметно удлиняло
    /// подключение/автообновление. Значение — индекс формата: 0 = "--cluster=<uuid>",
    /// 1 = "--cluster <uuid>", 2 = "<uuid>" (см. <see cref="JobListArgs"/>).
    /// Карта восстанавливается с диска (<see cref="RacJobListFormatStore"/>), чтобы
    /// после перезапуска приложения не тратить время на заведомо падающие попытки
    /// («подключение локально занимает почти 5 секунд», issue #324).
    /// </summary>
    private readonly Dictionary<string, int> _jobListFormats;

    /// <summary>
    /// Кэш последнего вывода «cluster list» по ключу подключения (issue #324):
    /// вывод «cluster info --cluster=<uuid>» на новых rac ПОСТРОЧНО совпадает с блоком
    /// соответствующего кластера в «cluster list» (в логе 8.5.4.1878 — по 1060 символов),
    /// поэтому info переиспользуется из кэша в течение TTL вместо запуска rac.
    /// Каждый запуск rac на Windows стоит ~1.1 с (старт процесса + comcntr) — на цикл
    /// из 8 вызовов экономится один.
    /// </summary>
    private readonly Dictionary<string, (string Output, DateTime Timestamp)> _clusterListCache = new();

    /// <summary>Срок годности кэша вывода «cluster list» (issue #324).</summary>
    internal static readonly TimeSpan ClusterListCacheTtl = TimeSpan.FromSeconds(30);

    public RacClient(IAppLogger logger)
    {
        _logger = logger;
        _jobListFormats = RacJobListFormatStore.Load();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RacCluster>> GetClustersAsync(
        RacConnectionParams parameters, CancellationToken cancellationToken = default)
    {
        var output = await RunAsync(parameters, cancellationToken, "cluster", "list")
            .ConfigureAwait(false);

        // rac завершился успешно (exit=0), но вывода нет: это либо пустой список кластеров,
        // либо подключение ушло на RAS/неправильный порт и «рабочего» ответа нет. Предупреждение
        // в журнале позволяет отличить такой случай от ошибки подключения (issue #324).
        if (string.IsNullOrWhiteSpace(output))
        {
            _logger.Warn(
                "RAC: cluster list завершился с кодом 0, но вернул пустой вывод — " +
                "кластеры не найдены. Проверьте порт: агент ragent (1540) или RAS (1545), " +
                "а не порт кластера (1541).");
        }
        else
        {
            // Кэшируем вывод для переиспользования в GetClusterInfoAsync (issue #324):
            // экономит один запуск rac (~1.1 с) на каждый цикл обновления данных.
            _clusterListCache[ConnectionKey(parameters)] = (output, DateTime.UtcNow);
        }

        var clusters = RacOutputParser.ToClusters(output);
        // Диагностика «в списке ключ вместо имени» (issue #324): в журнал попадают
        // распознанные подписи кластеров — сразу видно, что вернул парсер.
        if (clusters.Count > 0)
        {
            _logger.Info("RAC: cluster list — распознано кластеров: " + string.Join("; ",
                clusters.Select(c =>
                    $"«{(string.IsNullOrWhiteSpace(c.Name) ? (string.IsNullOrWhiteSpace(c.Host) ? "?" : c.Host) : c.Name)}» (порт {c.Port})")));
        }

        // issue #324: непустой вывод без единого кластера — так же подозрителен, как
        // и для остальных list-команд (WARN + понятная ошибка вместо «кластеров: 0»).
        EnsureParsedOrThrow(output, clusters.Count, "cluster list", _logger);

        return clusters;
    }

    /// <inheritdoc />
    public async Task<RacClusterInfo?> GetClusterInfoAsync(
        RacConnectionParams parameters, Guid clusterId, CancellationToken cancellationToken = default)
    {
        // Переиспользование кэша cluster list (issue #324): блок кластера из свежего
        // «cluster list» идентичен выводу «cluster info» — rac не запускается второй раз.
        if (_clusterListCache.TryGetValue(ConnectionKey(parameters), out var cached) &&
            DateTime.UtcNow - cached.Timestamp <= ClusterListCacheTtl)
        {
            var block = ExtractClusterBlock(cached.Output, clusterId);
            if (block is not null)
            {
                _logger.Info("RAC: cluster info — переиспользован кэш cluster list (экономия запуска rac, issue #324).");
                return RacOutputParser.ToClusterInfo(block);
            }
        }

        var output = await RunAsync(parameters, cancellationToken, "cluster", "info",
                $"--cluster={clusterId}")
            .ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(output) ? null : RacOutputParser.ToClusterInfo(output);
    }

    /// <summary>
    /// Извлекает из вывода «cluster list» (формат блоков «ключ : значение») текст блока
    /// кластера с идентификатором <paramref name="clusterId"/> — он построчно совпадает
    /// с выводом «cluster info --cluster=<uuid>» (issue #324). Возвращает null, если
    /// вывод не в формате блоков или кластер не найден. Internal — для юнит-тестов.
    /// </summary>
    internal static string? ExtractClusterBlock(string? clusterListOutput, Guid clusterId)
    {
        if (string.IsNullOrWhiteSpace(clusterListOutput) || clusterId == Guid.Empty)
            return null;

        var builder = new StringBuilder();
        var inTargetBlock = false;

        foreach (var rawLine in clusterListOutput.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line))
            {
                // Пустая строка внутри целевого блока сохраняется; после блока — конец.
                if (inTargetBlock)
                    builder.AppendLine(line);
                continue;
            }

            var colon = line.IndexOf(':');
            if (colon > 0 &&
                line.Substring(0, colon).Trim().Equals("cluster", StringComparison.OrdinalIgnoreCase))
            {
                var value = line.Substring(colon + 1).Trim();
                var isTarget = Guid.TryParse(value, out var parsed) && parsed == clusterId;
                if (inTargetBlock)
                    break;              // начался следующий кластер — блок завершён
                if (!isTarget)
                    continue;           // чужой блок — пропускаем строки до своего
                inTargetBlock = true;
            }

            if (inTargetBlock)
                builder.AppendLine(line);
        }

        return inTargetBlock && builder.Length > 0 ? builder.ToString() : null;
    }

    /// <summary>Ключ подключения без кластера: точка подключения и учётная запись
    /// (без пароля — секреты не попадают в ключи словарей, issue #324).</summary>
    private static string ConnectionKey(RacConnectionParams parameters)
        => $"{parameters.Address}:{parameters.Port}|{parameters.User}";

    /// <inheritdoc />
    public async Task<IReadOnlyList<RacProcessInfo>> GetProcessesAsync(
        RacConnectionParams parameters, Guid clusterId, CancellationToken cancellationToken = default)
    {
        var output = await RunAsync(parameters, cancellationToken, "process", "list",
                $"--cluster={clusterId}")
            .ConfigureAwait(false);
        var processes = RacOutputParser.ToProcesses(output);
        EnsureParsedOrThrow(output, processes.Count, "process list", _logger);
        return processes;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RacSessionInfo>> GetSessionsAsync(
        RacConnectionParams parameters, Guid clusterId, CancellationToken cancellationToken = default)
    {
        var output = await RunAsync(parameters, cancellationToken, "session", "list",
                $"--cluster={clusterId}")
            .ConfigureAwait(false);
        var sessions = RacOutputParser.ToSessions(output);
        EnsureParsedOrThrow(output, sessions.Count, "session list", _logger);
        return sessions;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RacConnectionInfo>> GetConnectionsAsync(
        RacConnectionParams parameters, Guid clusterId, CancellationToken cancellationToken = default)
    {
        var output = await RunAsync(parameters, cancellationToken, "connection", "list",
                $"--cluster={clusterId}")
            .ConfigureAwait(false);
        var connections = RacOutputParser.ToConnections(output);
        EnsureParsedOrThrow(output, connections.Count, "connection list", _logger);
        return connections;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RacLockInfo>> GetLocksAsync(
        RacConnectionParams parameters, Guid clusterId, CancellationToken cancellationToken = default)
    {
        var output = await RunAsync(parameters, cancellationToken, "lock", "list",
                $"--cluster={clusterId}")
            .ConfigureAwait(false);
        var locks = RacOutputParser.ToLocks(output);
        EnsureParsedOrThrow(output, locks.Count, "lock list", _logger);
        return locks;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RacInfobaseSummary>> GetInfobasesAsync(
        RacConnectionParams parameters, Guid clusterId, CancellationToken cancellationToken = default)
    {
        var output = await RunAsync(parameters, cancellationToken, "infobase", "summary", "list",
                $"--cluster={clusterId}")
            .ConfigureAwait(false);
        var infobases = RacOutputParser.ToInfobaseSummaries(output);
        EnsureParsedOrThrow(output, infobases.Count, "infobase summary list", _logger);
        return infobases;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RacJobInfo>> GetJobsAsync(
        RacConnectionParams parameters, Guid clusterId, CancellationToken cancellationToken = default)
    {
        // issue #324: rac 8.5.4.1878 отклоняет «job list --cluster=<uuid>» (код -1,
        // «Ошибка разбора параметра: --cluster=…») и «--cluster <uuid>» двумя токенами:
        // в логах от 2026-10-07/08 ОБЕ попытки падают (успех в логе — параллельный
        // «process list», а не job list). Перебираем форматы: 0 — --cluster=<uuid>,
        // 1 — --cluster <uuid>, 2 — позиционный <uuid> без имени параметра. После
        // первого успеха формат запоминается по ключу подключения — повторные
        // загрузки запускают rac один раз.
        var formatKey = JobListFormatKey(parameters, clusterId);
        var startIndex = _jobListFormats.TryGetValue(formatKey, out var known) ? known : 0;

        string? output = null;
        var failures = new List<string>();
        var usedFallback = false;
        var successFormat = -1;

        // До трёх попыток: сначала известный/первый формат, при неуспехе — остальные.
        for (var attempt = 0; attempt < JobListFormatCount && output is null; attempt++)
        {
            var index = (startIndex + attempt) % JobListFormatCount;
            try
            {
                var attemptOutput = await RunAsync(parameters, cancellationToken, JobListArgs(index, clusterId))
                    .ConfigureAwait(false);

                // issue #324 (комментарий 7OH от 2026-10-09): rac 8.5.4.1878 может завершить
                // «job list <uuid>» с кодом 0, но вернуть СПРАВКУ об использовании
                // («Использование: rac [режим] [команда] …», stdout=1833 симв.) — данных нет.
                // Такой вывод считается НЕуспехом формата: формат не кэшируется,
                // запрос повторяется с альтернативным синтаксисом.
                if (LooksLikeUsageHelp(attemptOutput))
                {
                    failures.Add(
                        $"формат '{JobListFormatName(index)}': rac вернул справку об использовании " +
                        $"({attemptOutput.Length} симв.) вместо списка заданий");
                    continue;
                }

                output = attemptOutput;
                usedFallback = attempt > 0;
                successFormat = index;
                // Запоминаем рабочий формат (в памяти и на диске); при смене версии
                // платформы неудачная попытка ниже удалит ключ, и на следующем вызове
                // форматы перепробуются заново.
                _jobListFormats[formatKey] = index;
                RacJobListFormatStore.Save(_jobListFormats);
            }
            catch (RacClientException ex)
            {
                failures.Add($"формат '{JobListFormatName(index)}': {ex.Message}");
            }
        }

        if (output is null)
        {
            // Все форматы неуспешны — один сводный [WARN] в журнал (issue #324: раньше
            // WARN писался и при успехе одной из попыток, сбивая с толку).
            _logger.Warn("RAC: job list — все форматы отклонены rac: " + string.Join("; ", failures));
            // Запись формата устарела (сменилась версия платформы) — удаляем из кэша,
            // чтобы на следующем вызове форматы перепробовались заново.
            _jobListFormats.Remove(formatKey);
            RacJobListFormatStore.Save(_jobListFormats);
            throw new RacClientException(
                "job list: rac отклонил все известные форматы команды (" +
                string.Join("; ", failures) +
                "). Пришлите, пожалуйста, вывод «rac <адрес:порт> job list --help» вашей платформы.");
        }

        if (usedFallback)
        {
            _logger.Info(
                "RAC: job list — стандартный формат '--cluster=<uuid>' не поддержан rac, " +
                $"применён формат '{JobListFormatName(successFormat)}' (успешно).");
        }

        var jobs = RacOutputParser.ToJobs(output);
        // Защита от новых форм справки (issue #324, 0.3.11): непустой stdout с 0 записей,
        // выглядящий как текст справки, — это НЕуспех: формат не должен оставаться
        // закэшированным (иначе WARN «вывод rac не распознан» повторяется на каждом опросе).
        if (jobs.Count == 0 && LooksLikeUsageHelp(output))
        {
            _logger.Warn(
                "RAC: job list — rac вернул справку об использовании вместо данных " +
                $"(exit=0, stdout={output.Length} симв., 0 записей); кэш формата сброшен.");
            _jobListFormats.Remove(formatKey);
            RacJobListFormatStore.Save(_jobListFormats);
        }
        EnsureParsedOrThrow(output, jobs.Count, "job list", _logger);
        return jobs;
    }

    /// <summary>Число первых строк вывода rac, сканируемых на признаки справки (issue
    /// #324, 0.3.11: у 8.5.4.1878 перед «Использование:» стоит многострочный баннер
    /// «1C:Enterprise 8.5 Remote Administrative Client Utility …», поэтому проверка
    /// только первой строки не распознавала справку — формат ошибочно кэшировался).</summary>
    internal const int UsageHelpScanLines = 15;

    /// <summary>
    /// Признак того, что rac вместо данных вернул СПРАВКУ об использовании команды
    /// (issue #324, комментарий 7OH от 2026-10-09: «job list <uuid>» на 8.5.4.1878
    /// завершается с кодом 0, но stdout — текст «1C:Enterprise 8.5 Remote Administrative
    /// Client Utility …» + «Использование: rac [режим] [команда] …», 0 записей).
    /// <para>
    /// С 0.3.11 сканируются ПЕРВЫЕ <see cref="UsageHelpScanLines"/> строк вывода (не
    /// только первая): справка распознаётся по префиксу строки «Использование:»
    /// (русская локаль) / «Usage:» (английская) или по подстроке «rac [» (шаблон
    /// команды в справке) в пределах окна строк. Data-вывод (таблица/блоки
    /// «ключ : значение») такими признаками в первых строках не обладает.
    /// Internal — для юнит-тестов без запуска процесса rac.
    /// </para>
    /// </summary>
    internal static bool LooksLikeUsageHelp(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return false;

        var scanned = 0;
        foreach (var rawLine in output.Split('\n'))
        {
            if (scanned >= UsageHelpScanLines)
                break;

            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0)
                continue;
            scanned++;

            if (line.StartsWith("Использование:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Usage:", StringComparison.OrdinalIgnoreCase))
                return true;

            // Шаблон команды в справке: «rac [режим] [команда] …» — встречается
            // и в русской, и в английской локали; в data-выводе «rac [» не бывает.
            if (line.Contains("rac [", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>Число известных форматов команды «job list» (issue #324).</summary>
    internal const int JobListFormatCount = 3;

    /// <summary>
    /// Аргументы команды «job list» по индексу формата (issue #324): 0 —
    /// <c>--cluster=<uuid></c> (стандарт), 1 — <c>--cluster <uuid></c> двумя токенами,
    /// 2 — позиционный <c><uuid></c> (без имени параметра). Internal — для юнит-тестов
    /// без запуска rac.
    /// </summary>
    internal static string[] JobListArgs(int formatIndex, Guid clusterId) => formatIndex switch
    {
        1 => new[] { "job", "list", "--cluster", clusterId.ToString() },
        2 => new[] { "job", "list", clusterId.ToString() },
        _ => new[] { "job", "list", $"--cluster={clusterId}" }
    };

    /// <summary>Человекочитаемое имя формата «job list» для журнала (issue #324).</summary>
    internal static string JobListFormatName(int formatIndex) => formatIndex switch
    {
        1 => "--cluster <uuid>",
        2 => "<uuid>",
        _ => "--cluster=<uuid>"
    };

    /// <summary>
    /// Ключ запомненного формата «job list» (issue #324): точка подключения, учётная
    /// запись и кластер. Без пароля — пароль в ключе не хранится (секреты не должны
    /// попадать в строковые ключи словаря и логи). Internal — для юнит-тестов.
    /// </summary>
    internal static string JobListFormatKey(RacConnectionParams parameters, Guid clusterId)
        => $"{ConnectionKey(parameters)}|{clusterId}";

    /// <summary>
    /// Нераспознанный вывод rac (issue #324): rac завершился с кодом 0 и вернул НЕпустой вывод,
    /// но ни табличный разбор, ни формат блоков «ключ : значение» не дали ни одной строки
    /// данных — вероятно, новая версия формата. Бросаем <see cref="RacOutputParseException"/>,
    /// чтобы монитор показал понятную ошибку и ОСТАНОВИЛ автообновление вместо тихих повторов
    /// каждые 5 с. Пустой вывод — легитимный случай (данных нет) и ошибкой не считается.
    /// Internal — для юнит-тестов правила «непустой вывод + 0 строк» без запуска процесса rac.
    /// </summary>
    internal static void EnsureParsedOrThrow(
        string output, int rowCount, string commandName, IAppLogger? logger = null)
    {
        if (!string.IsNullOrWhiteSpace(output) && rowCount == 0)
        {
            // Диагностика «exit=0, stdout>0, а вкладки пустые» (issue #324, лог 7OH
            // от 2026-10-08): раньше сбой разбора молча уходил в статус-строку окна,
            // в журнале не оставалось НИЧЕГО — по логу невозможно понять, какая из
            // шести параллельных list-команд не распознана и в каком виде пришёл
            // вывод. Пишем WARN с командой, объёмом и началом вывода.
            logger?.Warn(
                $"RAC: {commandName} — вывод rac не распознан: exit=0, stdout={output.Length} симв., " +
                $"0 записей. Начало вывода: «{Preview(output)}»");
            throw new RacOutputParseException(
                $"Вывод rac «{commandName}» не распознан (возможно, новая версия формата): " +
                $"код 0, но 0 строк данных при непустом выводе.");
        }
    }

    /// <summary>
    /// Однострочный фрагмент вывода для журнала: переводы строк и табуляции заменены
    /// пробелами, берутся первые 200 символов. Internal — для юнит-тестов формата.
    /// </summary>
    internal static string Preview(string output)
    {
        var flat = string.Join(
            " ",
            output.Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries));
        flat = flat.Trim();
        return flat.Length <= 200 ? flat : flat.Substring(0, 200) + "…";
    }

    /// <inheritdoc />
    public Task<bool> SetJobStateAsync(
        RacConnectionParams parameters, Guid clusterId, Guid jobId, RacJobAction action,
        CancellationToken cancellationToken = default) =>
        RunActionAsync(parameters, cancellationToken, "job", JobActionCommand(action),
            $"--cluster={clusterId}", $"--job={jobId}");

    /// <inheritdoc />
    public Task<bool> UpdateClusterAsync(
        RacConnectionParams parameters, Guid clusterId, RacClusterUpdate changes,
        CancellationToken cancellationToken = default) =>
        RunActionAsync(parameters, cancellationToken, ClusterUpdateArgs(clusterId, changes));

    /// <summary>
    /// Аргументы команды «cluster update» (issue #324, C3): <c>cluster update
    /// --cluster=<uuid></c> + параметры ТОЛЬКО изменённых свойств. Значение с
    /// пробелами (имя кластера) остаётся одним токеном — аргументы передаются через
    /// ArgumentList без shell; пароль в аргументах маскируется при журналировании
    /// (<see cref="SensitiveDataMasker.MaskRacPassword"/>). Internal — для юнит-тестов
    /// сборки аргументов без запуска rac.
    /// </summary>
    internal static string[] ClusterUpdateArgs(Guid clusterId, RacClusterUpdate? changes)
    {
        if (changes is null || changes.IsEmpty)
            throw new ArgumentException("Нет изменений параметров кластера для сохранения.", nameof(changes));

        var args = new List<string>(12) { "cluster", "update", $"--cluster={clusterId}" };
        if (changes.Name is not null)
            args.Add($"--name={changes.Name}");
        if (changes.ExpirationTimeout is { } expirationTimeout)
            args.Add($"--expiration-timeout={expirationTimeout.ToString(CultureInfo.InvariantCulture)}");
        if (changes.LifetimeLimit is { } lifetimeLimit)
            args.Add($"--lifetime-limit={lifetimeLimit.ToString(CultureInfo.InvariantCulture)}");
        if (changes.MaxMemorySize is { } maxMemorySize)
            args.Add($"--max-memory-size={maxMemorySize.ToString(CultureInfo.InvariantCulture)}");
        if (changes.MaxMemoryTimeLimit is { } maxMemoryTimeLimit)
            args.Add($"--max-memory-time-limit={maxMemoryTimeLimit.ToString(CultureInfo.InvariantCulture)}");
        if (changes.SecurityLevel is { } securityLevel)
            args.Add($"--security-level={securityLevel.ToString(CultureInfo.InvariantCulture)}");
        if (changes.PingPeriod is { } pingPeriod)
            args.Add($"--ping-period={pingPeriod.ToString(CultureInfo.InvariantCulture)}");
        if (changes.PingTimeout is { } pingTimeout)
            args.Add($"--ping-timeout={pingTimeout.ToString(CultureInfo.InvariantCulture)}");
        if (changes.MaxAuthAttempts is { } maxAuthAttempts)
            args.Add($"--max-auth-attempts={maxAuthAttempts.ToString(CultureInfo.InvariantCulture)}");
        if (changes.AuthLockDuration is { } authLockDuration)
            args.Add($"--auth-lock-duration={authLockDuration.ToString(CultureInfo.InvariantCulture)}");
        return args.ToArray();
    }

    /// <inheritdoc />
    public Task<bool> TerminateSessionAsync(
        RacConnectionParams parameters, Guid clusterId, Guid sessionId,
        CancellationToken cancellationToken = default) =>
        RunActionAsync(parameters, cancellationToken, "session", "terminate",
            $"--cluster={clusterId}", $"--session={sessionId}");

    /// <inheritdoc />
    public Task<bool> DisconnectConnectionAsync(
        RacConnectionParams parameters, Guid clusterId, Guid connectionId,
        CancellationToken cancellationToken = default) =>
        RunActionAsync(parameters, cancellationToken, "connection", "disconnect",
            $"--cluster={clusterId}", $"--connection={connectionId}");

    /// <inheritdoc />
    public string LastActionError { get; private set; } = string.Empty;

    /// <summary>
    /// Собирает аргументы командной строки rac:
    /// <c>[адрес[:порт] --user=U --password=P] <команда> [--cluster=uuid ...]</c>.
    /// Точка подключения передаётся ЕДИНЫМ токеном в формате <c>host:port</c> первым аргументом
    /// (rac.exe host:port cluster list) — раздельные <c>--host=</c>/<c>--port=</c> не разбираются
    /// новыми версиями платформы (issue #324). Если порт ≤ 0 — только <c>host</c>; если адрес
    /// пуст — токен подключения опускается. Логин/пароль передаются как <c>--user=</c>/<c>--password=</c>.
    /// Значение с пробелами (адрес, пароль) остаётся одним токеном — аргументы передаются через
    /// ArgumentList без shell. Internal — для юнит-тестов сборки аргументов (без запуска процесса).
    /// </summary>
    internal static IReadOnlyList<string> BuildArguments(
        RacConnectionParams parameters, params string[] commandAndArgs)
    {
        var args = new List<string>(commandAndArgs.Length + 5);
        if (!string.IsNullOrWhiteSpace(parameters.Address))
            args.Add(parameters.Port > 0
                ? $"{parameters.Address}:{parameters.Port}"
                : parameters.Address);
        if (!string.IsNullOrEmpty(parameters.User))
            args.Add($"--user={parameters.User}");
        if (!string.IsNullOrEmpty(parameters.Password))
            args.Add($"--password={parameters.Password}");
        args.AddRange(commandAndArgs);
        return args;
    }

    /// <summary>Подкоманда rac для операции управления регламентным заданием.</summary>
    private static string JobActionCommand(RacJobAction action) => action switch
    {
        RacJobAction.Pause => "pause",
        RacJobAction.Resume => "resume",
        RacJobAction.Disable => "disable",
        _ => "enable"
    };

    /// <summary>
    /// Выполняет rac-команду действия (завершение сеанса / разрыв соединения /
    /// изменение состояния задания).
    /// Возвращает true при ExitCode 0; при неудаче — false и текст ошибки
    /// (сообщение rac + stderr) в <see cref="LastActionError"/>. Исключения наружу
    /// не пробрасываются: ViewModel показывает пользователю LastActionError.
    /// </summary>
    private async Task<bool> RunActionAsync(
        RacConnectionParams parameters, CancellationToken cancellationToken,
        params string[] commandAndArgs)
    {
        try
        {
            await RunAsync(parameters, cancellationToken, commandAndArgs).ConfigureAwait(false);
            LastActionError = string.Empty;
            return true;
        }
        catch (OperationCanceledException)
        {
            LastActionError = "Операция отменена.";
            return false;
        }
        catch (Exception ex)
        {
            LastActionError = string.IsNullOrWhiteSpace(ex.Message)
                ? "Неизвестная ошибка rac."
                : ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Выполняет rac-команду: поиск исполняемого файла, прямой запуск без shell
    /// (UseShellExecute=false, CreateNoWindow=true, UTF-8), параллельное чтение
    /// stdout/stderr, таймаут 30 с. Возвращает stdout при ExitCode 0; при ошибке —
    /// <see cref="RacClientException"/> с текстом stderr.
    /// </summary>
    private async Task<string> RunAsync(
        RacConnectionParams parameters, CancellationToken cancellationToken,
        params string[] commandAndArgs)
    {
        var args = BuildArguments(parameters, commandAndArgs);
        // Метка команды для журнала: строка «выполнено» должна указывать, КАКАЯ именно
        // list-команда завершилась (issue #324: шесть параллельных команд — по строкам
        // «выполнено, exit=0, stdout=N» их невозможно сопоставить с вкладками).
        var commandLabel = SensitiveDataMasker.MaskRacPassword(string.Join(" ", commandAndArgs));
        // Диагностика «подключение стало дольше» (issue #324): время выполнения одной
        // rac-команды видно в журнале — где остаются секунды (старт rac или сервер).
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // Поиск rac может сканировать каталоги установленных платформ и быть заметной
        // частью общего времени подключения (issue #324: «локально почти 5 секунд») —
        // выделяем его тайминг в журнал, чтобы знать вклад этого этапа.
        var locatorStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var rac = OneCPlatformLocator.FindRacExecutable();
        locatorStopwatch.Stop();
        if (locatorStopwatch.ElapsedMilliseconds > 200)
        {
            _logger.Info($"RAC: поиск rac занял {locatorStopwatch.ElapsedMilliseconds} мс");
        }

        if (rac is null)
        {
            const string message =
                "Не найден исполняемый файл rac в каталоге установленной платформы 1С.";
            _logger.Warn($"RAC: {message}");
            throw new RacClientException(message);
        }

        // Пароль маскируется: в журнал rac-команда попадает без --password=<значение>.
        // Путь к найденному исполняемому файлу и полная командная строка (без секретов)
        // позволяют сравнить команду приложения с рабочей командой из терминала, когда
        // «в терминале rac работает, а монитор данные кластера не видит» (issue #324).
        _logger.Info(
            $"RAC: rac={rac}, команда: {SensitiveDataMasker.MaskRacPassword(string.Join(" ", args))}");

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = rac,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                    // Кодировку НЕ задаём: байты читаются сырыми и декодируются
                    // DecodeRacOutput (UTF-8 / cp866 / cp1251) — rac на Windows пишет
                    // в OEM-кодовой странице консоли, а не в UTF-8 (issue #324).
                }
            };

            // ArgumentList — каждый аргумент отдельным токеном, без shell: корректная
            // передача значений с пробелами и спецсимволами (пароль, имена баз).
            foreach (var arg in args)
                process.StartInfo.ArgumentList.Add(arg);

            if (!process.Start())
                throw new RacClientException($"Не удалось запустить rac: {rac}.");

            // Читаем stdout/stderr параллельно с ожиданием выхода: иначе большой вывод
            // может переполнить буфер канала и заблокировать завершение процесса.
            // Кодировка заранее неизвестна (UTF-8 на Linux, cp866/cp1251 на Windows),
            // поэтому читаем СЫРЫЕ байты и декодируем их позже (issue #324).
            var stdoutTask = ReadToEndBytesAsync(process.StandardOutput.BaseStream);
            var stderrTask = ReadToEndBytesAsync(process.StandardError.BaseStream);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(CommandTimeout);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                KillProcess(process);
                if (cancellationToken.IsCancellationRequested)
                    throw;

                const string message = "Превышен таймаут ожидания ответа rac (30 с).";
                _logger.Warn($"RAC: {message}");
                throw new RacClientException(message);
            }

            var stdout = DecodeRacOutput(await stdoutTask.ConfigureAwait(false));
            var stderr = DecodeRacOutput(await stderrTask.ConfigureAwait(false));

            if (process.ExitCode != 0)
            {
                // Ненулевой код выхода rac: ошибка подключения, отсутствие прав либо
                // неподдерживаемая команда (например lock list на старых платформах).
                // В сообщение включаем точку подключения (адрес:порт), к которой шла
                // команда: пользователь должен видеть, куда именно «ушёл» запрос —
                // при повторных нажатиях тексты ошибок rac могут отличаться (issue #324).
                var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                var token = parameters.Port > 0
                    ? $"{parameters.Address}:{parameters.Port}"
                    : parameters.Address;
                var message = string.IsNullOrWhiteSpace(token)
                    ? $"rac завершился с кодом {process.ExitCode}."
                    : $"rac ({token}) завершился с кодом {process.ExitCode}.";
                if (!string.IsNullOrWhiteSpace(detail))
                    message += " " + detail.Trim();

                _logger.Warn($"RAC: {message}");
                throw new RacClientException(message);
            }

            // Итог выполнения в журнале: код выхода, объём вывода и длительность —
            // по ним видно, что rac вернул данные (или пустой список) и где задержка
            // при «монитор не видит кластер» / «подключение стало дольше» (issue #324).
            stopwatch.Stop();
            _logger.Info(
                $"RAC: {commandLabel} — выполнено, exit={process.ExitCode}, stdout={stdout.Length} симв., " +
                $"stderr={stderr.Length} симв., за {stopwatch.ElapsedMilliseconds} мс");
            return stdout;
        }
        catch (RacClientException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var message = $"Не удалось выполнить команду rac: {ex.Message}";
            _logger.Error($"RAC: {message}", ex);
            throw new RacClientException(message, ex);
        }
    }

    private static void KillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Процесс мог завершиться сам — игнорируем.
        }
    }

    /// <summary>Читает поток до конца в массив байтов (без декодирования).</summary>
    private static async Task<byte[]> ReadToEndBytesAsync(Stream stream)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer).ConfigureAwait(false);
        return buffer.ToArray();
    }

    /// <summary>
    /// Декодирует байты вывода rac в текст. Сначала строгий UTF-8 (Linux/Avalonia);
    /// если байты невалидны как UTF-8 (rac на Windows пишет в OEM/ANSI кодовой странице,
    /// обычно cp866, issue #324) — пробуем cp866 и cp1251 и берём вариант с наименьшим
    /// числом символов замены U+FFFD. Internal — для юнит-тестов без запуска процесса.
    /// </summary>
    internal static string DecodeRacOutput(byte[] bytes)
    {
        if (bytes is null || bytes.Length == 0)
            return string.Empty;

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // Не UTF-8 — переходим к кодовым страницам Windows.
        }

        string? best = null;
        var bestBad = int.MaxValue;
        foreach (var encoding in WindowsFallbackEncodings)
        {
            var decoded = encoding.GetString(bytes);
            var bad = CountReplacementChars(decoded);
            if (bad < bestBad)
            {
                bestBad = bad;
                best = decoded;
            }
        }
        return best ?? WindowsFallbackEncodings[0].GetString(bytes);
    }

    /// <summary>Считает символы замены U+FFFD (признак неверной кодировки).</summary>
    private static int CountReplacementChars(string text)
    {
        var count = 0;
        foreach (var ch in text)
        {
            if (ch == '\uFFFD')
                count++;
        }
        return count;
    }
}