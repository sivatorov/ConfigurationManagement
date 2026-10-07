using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
    /// <c>--cluster=<uuid></c> (код -1) и принимает только <c>--cluster <uuid></c>.
    /// Без кэша каждая загрузка данных кластера запускала rac дважды (первая попытка
    /// всегда падала), что заметно удлиняло подключение/автообновление.
    /// Значение — индекс формата: 0 = "--cluster=<uuid>", 1 = "--cluster <uuid>".
    /// </summary>
    private readonly Dictionary<string, int> _jobListFormats = new(StringComparer.Ordinal);

    public RacClient(IAppLogger logger)
    {
        _logger = logger;
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

        return RacOutputParser.ToClusters(output);
    }

    /// <inheritdoc />
    public async Task<RacClusterInfo?> GetClusterInfoAsync(
        RacConnectionParams parameters, Guid clusterId, CancellationToken cancellationToken = default)
    {
        var output = await RunAsync(parameters, cancellationToken, "cluster", "info",
                $"--cluster={clusterId}")
            .ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(output) ? null : RacOutputParser.ToClusterInfo(output);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RacProcessInfo>> GetProcessesAsync(
        RacConnectionParams parameters, Guid clusterId, CancellationToken cancellationToken = default)
    {
        var output = await RunAsync(parameters, cancellationToken, "process", "list",
                $"--cluster={clusterId}")
            .ConfigureAwait(false);
        var processes = RacOutputParser.ToProcesses(output);
        EnsureParsedOrThrow(output, processes.Count, "process list");
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
        EnsureParsedOrThrow(output, sessions.Count, "session list");
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
        EnsureParsedOrThrow(output, connections.Count, "connection list");
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
        EnsureParsedOrThrow(output, locks.Count, "lock list");
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
        EnsureParsedOrThrow(output, infobases.Count, "infobase summary list");
        return infobases;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RacJobInfo>> GetJobsAsync(
        RacConnectionParams parameters, Guid clusterId, CancellationToken cancellationToken = default)
    {
        // issue #324: rac 8.5.4.1878 отклоняет «job list --cluster=<uuid>» (код -1,
        // «Ошибка разбора параметра: --cluster=…»), хотя остальные list-команды с тем же
        // параметром работают. Форматы: прежний --cluster=<uuid> (0) и --cluster <uuid> (1).
        // После первого успеха формат запоминается по ключу подключения — повторные
        // загрузки запускают rac один раз (раньше каждая загрузка тратила ~1 с на
        // заведомо падающую первую попытку).
        var formatKey = JobListFormatKey(parameters, clusterId);
        var startIndex = _jobListFormats.TryGetValue(formatKey, out var known) ? known : 0;

        string? output = null;
        RacClientException? lastError = null;
        var failures = new List<string>();
        var usedFallback = false;

        // До двух попыток: сначала известный/первый формат, при неуспехе — второй.
        for (var attempt = 0; attempt < 2 && output is null; attempt++)
        {
            var index = (startIndex + attempt) % 2;
            try
            {
                output = await RunAsync(parameters, cancellationToken, JobListArgs(index, clusterId))
                    .ConfigureAwait(false);
                lastError = null;
                if (index != 0)
                    usedFallback = true;
                // Запоминаем рабочий формат; при смене версии платформы неудачная попытка
                // ниже удалит ключ, и на следующем вызове форматы перепробуются заново.
                _jobListFormats[formatKey] = index;
            }
            catch (RacClientException ex)
            {
                lastError = ex;
                failures.Add($"формат '{(index == 0 ? "--cluster=<uuid>" : "--cluster <uuid>")}': {ex.Message}");
            }
        }

        if (output is null)
        {
            // Все форматы неуспешны — только тогда [WARN] в журнал (issue #324: раньше
            // WARN писался после первой попытки даже при успехе второй и сбивал с толку).
            foreach (var failure in failures)
                _logger.Warn($"RAC: job list ({failure})");
            throw lastError ?? new RacClientException("job list: не удалось получить вывод rac.");
        }

        if (usedFallback)
        {
            _logger.Info(
                "RAC: job list — первый формат '--cluster=<uuid>' не поддержан rac, " +
                "применён формат '--cluster <uuid>' (успешно).");
        }

        var jobs = RacOutputParser.ToJobs(output);
        EnsureParsedOrThrow(output, jobs.Count, "job list");
        return jobs;
    }

    /// <summary>
    /// Аргументы команды «job list» по индексу формата (issue #324): 0 —
    /// <c>--cluster=<uuid></c> (отклоняется rac 8.5.4), 1 — <c>--cluster <uuid></c>
    /// двумя токенами (работает). Internal — для юнит-тестов без запуска rac.
    /// </summary>
    internal static string[] JobListArgs(int formatIndex, Guid clusterId) => formatIndex == 0
        ? new[] { "job", "list", $"--cluster={clusterId}" }
        : new[] { "job", "list", "--cluster", clusterId.ToString() };

    /// <summary>
    /// Ключ запомненного формата «job list» (issue #324): точка подключения, учётная
    /// запись и кластер. Без пароля — пароль в ключе не хранится (секреты не должны
    /// попадать в строковые ключи словаря и логи). Internal — для юнит-тестов.
    /// </summary>
    internal static string JobListFormatKey(RacConnectionParams parameters, Guid clusterId)
        => $"{parameters.Address}:{parameters.Port}|{parameters.User}|{clusterId}";

    /// <summary>
    /// Нераспознанный вывод rac (issue #324): rac завершился с кодом 0 и вернул НЕпустой вывод,
    /// но ни табличный разбор, ни формат блоков «ключ : значение» не дали ни одной строки
    /// данных — вероятно, новая версия формата. Бросаем <see cref="RacOutputParseException"/>,
    /// чтобы монитор показал понятную ошибку и ОСТАНОВИЛ автообновление вместо тихих повторов
    /// каждые 5 с. Пустой вывод — легитимный случай (данных нет) и ошибкой не считается.
    /// Internal — для юнит-тестов правила «непустой вывод + 0 строк» без запуска процесса rac.
    /// </summary>
    internal static void EnsureParsedOrThrow(string output, int rowCount, string commandName)
    {
        if (!string.IsNullOrWhiteSpace(output) && rowCount == 0)
        {
            throw new RacOutputParseException(
                $"Вывод rac «{commandName}» не распознан (возможно, новая версия формата): " +
                $"код 0, но 0 строк данных при непустом выводе.");
        }
    }

    /// <inheritdoc />
    public Task<bool> SetJobStateAsync(
        RacConnectionParams parameters, Guid clusterId, Guid jobId, RacJobAction action,
        CancellationToken cancellationToken = default) =>
        RunActionAsync(parameters, cancellationToken, "job", JobActionCommand(action),
            $"--cluster={clusterId}", $"--job={jobId}");

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
        // Диагностика «подключение стало дольше» (issue #324): время выполнения одной
        // rac-команды видно в журнале — где остаются секунды (старт rac или сервер).
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var rac = OneCPlatformLocator.FindRacExecutable();
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
                $"RAC: выполнено, exit={process.ExitCode}, stdout={stdout.Length} симв., " +
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