#if WINDOWS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Configuration_Management.Models;

namespace Configuration_Management.Services;


/// <summary>
/// Обращается к COM-коннектору 1С (<c>V8*.COMConnector</c>) в отдельном процессе-агенте.
/// <para>
/// Зачем. <c>comcntr.dll</c> грузится прямо в процесс приложения, и под CoreCLR (.NET 5+)
/// вызов его метода <c>Connect</c> обрывает процесс нативным fast-fail
/// (<c>0xC0000409</c>, STATUS_STACK_BUFFER_OVERRUN) — без управляемого исключения, поэтому
/// его нельзя ни перехватить, ни записать в журнал. Под .NET Framework 4.8 тот же вызов
/// возвращает обычное исключение, то есть дело в рантайме, а не в данных базы: обрывается
/// даже подключение к заведомо рабочей базе. Из-за этого приложение молча умирало на старте,
/// как только в списке была хотя бы одна файловая или клиент-серверная база с незаполненными
/// ConfigurationName/ConfigurationVersion — для них запускается фоновое дочитывание сведений.
/// </para>
/// <para>
/// Как. COM живёт в дочернем экземпляре этого же приложения, запущенном с ключом
/// <see cref="SwitchName"/>. Агент один на сессию и обслуживает запросы построчно, поэтому
/// список из десятков баз не превращается в десятки запусков. Строка подключения содержит
/// пароль и передаётся через stdin, а не аргументом: командная строка процесса видна другим
/// процессам пользователя и попадает в журналы аудита запуска. Полезная нагрузка едет
/// в Base64 — иначе табуляция или перевод строки внутри значения испортили бы протокол.
/// </para>
/// <para>
/// Пароль. Текст ошибки, который возвращает 1С, умеет цитировать строку подключения целиком.
/// Разбирать такой текст бесполезно: <see cref="OneCComConnector"/> строит строку без
/// экранирования, поэтому кавычка, <c>;</c> или перевод строки внутри пароля делают её
/// неоднозначной; а сообщение вдобавок может прийти усечённым посреди значения. Любое
/// правило разбора при этом либо выпускает хвост пароля, либо съедает диагностику.
/// </para>
/// <para>
/// Поэтому агент отдаёт и текст ошибки, и опознавательный код, а решение принимает родитель —
/// и принимает его по строке подключения, а не по содержимому текста: нет параметра
/// <c>Pwd</c> в строке, значит 1С пароля не видела и процитировать не могла, текст безопасен
/// по построению. Есть — текст не выпускается вовсе, остаётся код. Никаких догадок о том,
/// что именно 1С процитировала: попытки угадывать (вырезать значение, искать начало пароля)
/// проваливались одна за другой. Для баз без пароля, а это большинство, диагностика
/// сохраняется целиком. Агент о пароле не знает ничего.
/// </para>
/// </summary>
internal static class ComReadHost
{
    /// <summary>Ключ командной строки, включающий режим агента.</summary>
    public const string SwitchName = "--read-config-com";

    /// <summary>Префикс строки результата в стандартном выводе агента.</summary>
    private const string ResultPrefix = "CFGINFO\t";

    /// <summary>Запас к таймауту запроса: агент должен успеть ответить сам, прежде чем его убьют.</summary>
    private const int AgentGraceMs = 2000;

    /// <summary>
    /// Сколько отказов агента подряд считать системными. Разовый сбой (антивирус, убийство
    /// из диспетчера) не должен глушить COM на всю сессию, а систематический — обязан.
    /// </summary>
    private const int FailuresBeforeLatch = 2;

    /// <summary>
    /// Порог для сбоев канала. Мягче общего: разовые обрывы лечатся перезапуском агента,
    /// глушить из-за них COM на сессию незачем. Но и совсем не считать нельзя — иначе
    /// систематический мусор в потоке агента давал бы бесконечную череду перезапусков.
    /// </summary>
    private const int TransportFailuresBeforeLatch = 5;

    /// <summary>
    /// Сколько ждать, пока умирающий процесс завершится сам, прежде чем убивать его.
    /// Нужно, чтобы снять настоящий код возврата: Windows Error Reporting придерживает
    /// упавший процесс, а код убийства (0xFFFFFFFF) для диагностики бесполезен.
    /// </summary>
    private const int SelfExitGraceMs = 3000;

    private static readonly object Sync = new();

    /// <summary>
    /// Короткий монитор только для состояния защёлки. Отдельный от <see cref="Sync"/>:
    /// тот удерживается всё время запроса, а состояние меняется в том числе из потока
    /// интерфейса. Держится наносекунды и никогда — во время ввода-вывода, поэтому
    /// заморозить интерфейс не может. Раньше поля менялись по отдельности атомарными
    /// записями, и между проверкой поколения и записью защёлки оставалась щель, в которую
    /// проваливалась команда пользователя.
    /// </summary>
    private static readonly object StateLock = new();
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Переменные среды, которыми рантайм включает запись аварийного дампа или пускает
    /// в процесс чужой код. У агента их гасим: в его памяти в момент падения лежит строка
    /// подключения с паролем.
    /// <para>
    /// Мера действует по возможности, а не гарантированно. Настоящий <c>__fastfail</c>
    /// по своему назначению уходит в отчёт мимо обработчиков, а машинную политику
    /// Windows Error Reporting из падающего процесса не отключить.
    /// </para>
    /// </summary>
    private static readonly string[] DumpEnvironmentVariables =
    {
        // Профилировщик и стартовые перехватчики — тоже способ снять память процесса.
        "CORECLR_ENABLE_PROFILING",
        "CORECLR_PROFILER",
        "CORECLR_PROFILER_PATH",
        "DOTNET_STARTUP_HOOKS",
        "DOTNET_DbgEnableMiniDump",
        "DOTNET_DbgMiniDumpType",
        "DOTNET_DbgMiniDumpName",
        "DOTNET_CreateDumpDiagnostics",
        "DOTNET_EnableCrashReport",
        "COMPlus_DbgEnableMiniDump",
        "COMPlus_DbgMiniDumpType",
        "COMPlus_DbgMiniDumpName",
        "COMPlus_CreateDumpDiagnostics",
        "COMPlus_EnableCrashReport"
    };

    /// <summary>Строгий декодер: недопустимую последовательность нельзя молча превращать в U+FFFD.</summary>
    private static readonly UTF8Encoding Utf8Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static Process? _agent;

    // Каналы обнуляются в Shutdown и StopAgent вне общего монитора — намеренно, чтобы
    // закрытие окна не ждало конца текущего чтения. Поэтому доступ к ним объявляем
    // volatile: иначе упорядоченность записи и чтения держалась бы на модели памяти
    // конкретной архитектуры, а не на языке.
    private static volatile StreamWriter? _agentInput;
    private static volatile StreamReader? _agentOutput;
    private static int _comUnavailable;
    private static int _hardFailures;
    private static int _transportFailures;
    private static int _shuttingDown;
    private static int _sequence;

    /// <summary>
    /// Поколение сброса. Растёт при каждом <see cref="ResetAvailability"/>. Отказ запроса,
    /// начатого до сброса, не должен защёлкивать недоступность заново — иначе ручная
    /// команда пользователя молча съедалась бы уже летящим фоновым запросом.
    /// </summary>
    private static int _resetEpoch;

    /// <summary>
    /// COM признан неработоспособным в этой сессии. Дальнейшие попытки пропускаются
    /// без запуска процессов, пока не будет вызван <see cref="ResetAvailability"/>.
    /// </summary>
    public static bool ComUnavailable
    {
        get { lock (StateLock) return _comUnavailable != 0; }
    }

    /// <summary>
    /// Приложение закрывается. Отказы, случившиеся после этого, не отражают состояние COM
    /// и в журнал не пишутся: на выходе во время фонового дочитывания списка каждая
    /// оставшаяся база иначе добавляла бы туда по строке.
    /// </summary>
    public static bool IsShuttingDown => Volatile.Read(ref _shuttingDown) != 0;

    /// <summary>
    /// Снимает признак недоступности: явные действия пользователя (регистрация коннектора,
    /// команда обновления сведений) должны давать возможность попробовать снова.
    /// Вызывать вместе с <see cref="OneCComConnector.ResetAvailabilityCache"/> — иначе
    /// останется второй, более ранний выключатель.
    /// </summary>
    public static void ResetAvailability()
    {
        lock (StateLock)
        {
            _resetEpoch++;
            _comUnavailable = 0;
            _hardFailures = 0;
            _transportFailures = 0;
        }
    }

    /// <summary>
    /// Текущее поколение сброса — снимок на время запроса. Вызывающий может снять его
    /// раньше и передать в <see cref="Read"/>: запрос, начавшийся до сброса, не должен
    /// получать право защёлкивать недоступность по новому поколению (issue #175).
    /// </summary>
    internal static int CurrentEpoch()
    {
        lock (StateLock) return _resetEpoch;
    }

    /// <summary>
    /// Успешное чтение обнуляет счётчики отказов — но только если с начала запроса
    /// не было сброса. Запрос прежнего поколения (например начатый до смены имени
    /// COM-коннектора) говорит об уже неактуальном наборе имён, и его успех не должен
    /// ослаблять защиту от повторных крахов для нового (issue #175). Проверка
    /// симметрична <see cref="RegisterFailure"/> и <see cref="LatchIfSameEpoch"/>.
    /// </summary>
    private static void NoteSuccess(int epoch)
    {
        lock (StateLock)
        {
            if (_resetEpoch != epoch)
                return;

            _hardFailures = 0;
            _transportFailures = 0;
        }
    }

    /// <summary>
    /// Глушит COM, если с начала запроса не было сброса. Отдельный метод нужен для
    /// разрядов, которые защёлкивают сразу, без накопления счётчика.
    /// </summary>
    private static void LatchIfSameEpoch(int epoch)
    {
        lock (StateLock)
        {
            if (_resetEpoch == epoch)
                _comUnavailable = 1;
        }
    }

    // ---------------------------------------------------------------- сторона родителя

    /// <summary>
    /// Читает наименование и версию конфигурации через агента.
    /// Основной процесс не пострадает, даже если COM-вызов оборвёт агента.
    /// </summary>
    /// <param name="connectString">Строка подключения (может содержать пароль).</param>
    /// <param name="timeoutMs">Предельное время COM-вызова.</param>
    /// <param name="progIds">
    /// Список ProgID для перебора (issue #175). Null — стандартный
    /// <see cref="OneCComConnector.KnownProgIds"/>. Кастомный список передаётся агенту.
    /// </param>
    /// <param name="startEpoch">
    /// Поколение сброса на момент начала операции у вызывающего (issue #175). Нужно,
    /// когда между подготовкой запроса и этим вызовом пользователь мог сменить имя
    /// COM-коннектора: сброс поколения снимает у уже начатого запроса право защёлкнуть
    /// недоступность, иначе отказ по прежнему списку имён погасил бы COM для нового.
    /// Null — снять поколение здесь.
    /// </param>
    public static ComReadResult Read(string connectString, int timeoutMs,
        IReadOnlyList<string>? progIds = null, int? startEpoch = null)
    {
        if (string.IsNullOrWhiteSpace(connectString))
            return ComReadResult.Fail(ComFailureKind.Transport);

        // Стандартный список не отправляем агенту явно: протокол по умолчанию остаётся
        // прежним (три поля), и агент сам возьмёт KnownProgIds. Явно уезжает только
        // кастомный перечень из шаблона имени COM-коннектора.
        var effectiveProgIds = progIds ?? OneCComConnector.KnownProgIds;
        var customProgIds = !ReferenceEquals(progIds, OneCComConnector.KnownProgIds);

        if (ComUnavailable)
            return ComReadResult.Fail(ComFailureKind.Disabled);

        var epoch = startEpoch ?? CurrentEpoch();

        lock (Sync)
        {
            // Перепроверка под монитором: пока мы ждали очереди, соседний запрос мог
            // уже уронить агента и защёлкнуть недоступность.
            if (ComUnavailable)
                return ComReadResult.Fail(ComFailureKind.Disabled);

            if (!EnsureAgent(out var startDetail))
            {
                // Закрытие приложения — не отказ COM. Иначе на выходе во время фонового
                // дочитывания каждая следующая база писала бы в журнал ложную ошибку
                // запуска и приближала защёлку.
                if (Volatile.Read(ref _shuttingDown) != 0)
                    return ComReadResult.Fail(ComFailureKind.Transport);

                // Систематический отказ запуска — тоже отказ: иначе на списке из десятков баз
                // мы будем пытаться и писать в журнал по разу на каждую, и так каждый старт.
                return RegisterFailure(epoch, ComFailureKind.AgentStart, startDetail);
            }

            var input = _agentInput;
            var output = _agentOutput;
            if (input is null || output is null)
            {
                // Та же оговорка, что и выше: Shutdown обнуляет каналы вне монитора и может
                // успеть между удачным EnsureAgent и этой строкой. Закрытие приложения —
                // не отказ COM, иначе в журнал уходила бы ложная ошибка запуска.
                if (Volatile.Read(ref _shuttingDown) != 0)
                    return ComReadResult.Fail(ComFailureKind.Transport);

                return RegisterFailure(epoch, ComFailureKind.AgentStart, null);
            }

            var seq = unchecked(++_sequence);

            try
            {
                var request = string.Join("\t",
                    seq.ToString(CultureInfo.InvariantCulture),
                    timeoutMs.ToString(CultureInfo.InvariantCulture),
                    Encode(connectString));
                if (customProgIds)
                    request += "\t" + Encode(string.Join("\t", effectiveProgIds));
                input.WriteLine(request);

                // Диагноз, присланный агентом до гибели. Перебор ProgID идёт внутри агента,
                // и коннектор, опрошенный раньше, успевает назвать настоящую причину — а
                // следующий обрывает процесс нативным fast-fail и уносит её с собой. Родитель
                // придерживает такую причину и предъявляет её вместо голого «агент завершился».
                ComReadResult? partial = null;
                var deadline = Environment.TickCount64 + timeoutMs + AgentGraceMs;
                string? line;

                while (true)
                {
                    var remaining = (int)(deadline - Environment.TickCount64);

                    var pending = Task.Run(() => output.ReadLine());
                    // Задачу мы можем бросить, не дождавшись. Штатно она завершается сама
                    // (закрытый канал даёт null), но наблюдателя вешаем на случай исключения:
                    // иначе оно всплывёт в TaskScheduler.UnobservedTaskException, а тот
                    // показывает пользователю диалог о фатальной ошибке.
                    _ = pending.ContinueWith(static t => _ = t.Exception,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);

                    if (remaining <= 0 || !pending.Wait(remaining))
                    {
                        // Живой агент отвечает сам не позже собственного Join(timeoutMs), поэтому
                        // молчание дольше бюджета означает, что агента уже нет. Ждать закрытия
                        // канала нельзя: на машинах с включённым Windows Error Reporting упавший
                        // процесс удерживается, и EOF приходит через десятки секунд — крах
                        // выглядел бы обычным таймаутом, и защёлка не сработала бы никогда.
                        var gone = AgentGone(epoch, StopAgent(SelfExitGraceMs));
                        return partial ?? gone;
                    }

                    line = pending.Result;
                    if (line is null)
                        break;

                    var frame = ParseResponse(line, seq, effectiveProgIds, out var badFrame, out var isPartial);
                    if (badFrame || !isPartial)
                    {
                        // Кадр окончательный (или испорченный) — разбираем его обычным путём ниже.
                        break;
                    }

                    // Промежуточный диагноз: запоминаем последний и ждём окончательного ответа.
                    partial = frame;
                }

                if (line is null)
                {
                    // Закрытие приложения тоже обрывает канал. Это не отказ COM: считать
                    // его крахом значило бы писать в журнал ложную запись и приближать
                    // защёлку на каждом выходе во время фонового дочитывания.
                    if (Volatile.Read(ref _shuttingDown) != 0)
                        return ComReadResult.Fail(ComFailureKind.Transport);

                    var gone = AgentGone(epoch, StopAgent(SelfExitGraceMs));
                    return partial ?? gone;
                }

                var result = ParseResponse(line, seq, effectiveProgIds, out var desynchronized, out _);
                if (desynchronized)
                {
                    // Придержанный диагноз здесь дороже сообщения о сбое обмена: агент мог
                    // назвать настоящую причину, а следом погибнуть посреди записи
                    // окончательного кадра — тогда до нас доезжает обрывок.
                    // Ответ не от нашего запроса. Дальше соответствие «запрос — ответ» уже
                    // не восстановить, а молча продолжать нельзя: сведения о конфигурации
                    // начали бы приписываться чужим базам без единого признака ошибки.
                    // Считаем отказом: систематический мусор в потоке агента иначе давал бы
                    // бесконечную череду перезапусков процесса без всякого ограничителя.
                    StopAgent(graceMs: 0);
                    var broken = RegisterFailure(epoch, ComFailureKind.Transport, null);
                    return partial ?? broken;
                }

                switch (result.Failure)
                {
                    case ComFailureKind.NotRegistered:
                        // Отсутствие коннектора само не изменится — глушим и убираем агента.
                        LatchIfSameEpoch(epoch);
                        StopAgent(graceMs: 0);
                        break;

                    case ComFailureKind.Timeout:
                        // Агент ответил, но внутри него остался повисший STA-поток с незакрытым
                        // COM-объектом. Оставлять такого агента нельзя: потоки будут копиться,
                        // а поздний обрыв одного из них припишется чужому запросу. Грация здесь
                        // не нужна — мы точно знаем, что процесс жив.
                        StopAgent(graceMs: 0);
                        // Учитываем по мягкому порогу: список недоступных клиент-серверных баз
                        // иначе тратил бы полный таймаут и перезапуск процесса на каждую, и так
                        // на каждом старте — защёлка это никогда не останавливала.
                        //
                        // Придержанный диагноз предъявляем и здесь: типичный случай — ранний
                        // коннектор уже назвал причину, а следующий завис на недоступном
                        // сервере. «Превышен таймаут» в этой паре — заведомо худший ответ.
                        var timedOut = RegisterFailure(epoch, result.Failure, result.Detail, result.Code);
                        return partial ?? timedOut;

                    case ComFailureKind.BadRequest:
                        // Агент не понял запрос. Оставлять его нельзя: если он не понимает
                        // протокол, не поймёт и следующий запрос, а без учёта отказа поток
                        // непонятых запросов шёл бы бесконечно и без ограничителя.
                        StopAgent(graceMs: 0);
                        return RegisterFailure(epoch, result.Failure, null);

                    case ComFailureKind.None:
                        // Счётчик сбрасывает только успех. Сбрасывать его на любой ответ
                        // означало бы, что на чередующемся списке двух отказов подряд
                        // не наберётся никогда.
                        NoteSuccess(epoch);
                        break;
                }

                return result;
            }
            catch (Exception ex)
            {
                StopAgent(graceMs: 0);

                // Обрыв канала при закрытии приложения отказом не считаем.
                if (Volatile.Read(ref _shuttingDown) != 0)
                    return ComReadResult.Fail(ComFailureKind.Transport);

                return RegisterFailure(epoch, ComFailureKind.Transport, ex.Message);
            }
        }
    }

    /// <summary>
    /// Останавливает агента. Намеренно не берёт <see cref="Sync"/>: метод вызывается из
    /// <c>App.OnExit</c> в потоке интерфейса, а монитор удерживается всё время запроса —
    /// иначе закрытие окна замирало бы до конца текущего чтения.
    /// </summary>
    public static void Shutdown()
    {
        // Признак ставим до того, как забирать процесс: активный Read должен понять,
        // что оборванный канал — это выход из приложения, а не отказ COM.
        Volatile.Write(ref _shuttingDown, 1);

        var process = Interlocked.Exchange(ref _agent, null);
        _agentInput = null;
        _agentOutput = null;
        KillAndDispose(process, graceMs: 0, waitAfterKillMs: 0);
    }

    /// <summary>
    /// Агент не ответил и исчез. Настоящий крах опознаётся по коду возврата; штатный выход
    /// с нулевым кодом крахом не считаем — это внутренний сбой агента, и говорить
    /// пользователю «аварийно завершил, код 0x00000000» было бы самопротиворечиво.
    /// </summary>
    private static ComReadResult AgentGone(int epoch, int? exitCode) =>
        exitCode is null or 0
            ? RegisterFailure(epoch, ComFailureKind.Transport, null)
            : RegisterFailure(epoch, ComFailureKind.AgentCrashed, FormatExitCode(exitCode));

    /// <summary>Учитывает отказ и решает, пора ли глушить COM на сессию.</summary>
    private static ComReadResult RegisterFailure(
        int epoch, ComFailureKind kind, string? detail, string? code = null)
    {
        // Проверка поколения и изменение счётчика — одной неделимой операцией. Сброс,
        // случившийся после начала запроса, отменяет право этого запроса защёлкивать:
        // пользователь уже попросил попробовать снова.
        // Счётчики раздельные. Общий с двумя порогами давал результат, зависящий от порядка:
        // один разовый сбой канала поднимал счётчик, и следующий настоящий крах защёлкивал
        // COM по жёсткому порогу, хотя ни один из разрядов своего порога не достиг.
        var soft = kind is ComFailureKind.Transport
                        or ComFailureKind.Timeout
                        or ComFailureKind.BadRequest;

        lock (StateLock)
        {
            if (_resetEpoch != epoch)
                return ComReadResult.Fail(kind, detail, code);

            if (soft)
            {
                if (++_transportFailures >= TransportFailuresBeforeLatch)
                    _comUnavailable = 1;
            }
            else if (++_hardFailures >= FailuresBeforeLatch)
            {
                _comUnavailable = 1;
            }
        }

        return ComReadResult.Fail(kind, detail, code);
    }

    /// <summary>
    /// Путь к сборке точки входа. Нужен только при запуске через <c>dotnet App.dll</c>;
    /// в однофайловой публикации возвращает пустую строку, и вызывающий это учитывает.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "SingleFile", "IL3000:Avoid accessing Assembly file path when publishing as a single file",
        Justification = "Ветка достижима только при запуске через dotnet.exe, где сборка не встроена; пустой результат обработан вызывающим.")]
    private static string? EntryAssemblyLocation() => Assembly.GetEntryAssembly()?.Location;

    /// <summary>Код возврата для показа. Null — код неизвестен, это отдельный случай.</summary>
    private static string? FormatExitCode(int? exitCode) =>
        exitCode is null ? null : "0x" + exitCode.Value.ToString("X8", CultureInfo.InvariantCulture);

    private static bool EnsureAgent(out string? detail)
    {
        detail = null;

        if (Volatile.Read(ref _shuttingDown) != 0)
        {
            detail = "shutdown";
            return false;
        }

        // HasExited бросает InvalidOperationException, если Shutdown из другого потока
        // успел освободить объект между чтением поля и обращением к свойству.
        try
        {
            if (_agent is { HasExited: false } && _agentInput is not null && _agentOutput is not null)
                return true;
        }
        catch (InvalidOperationException)
        {
        }

        StopAgent(graceMs: 0);

        var host = Environment.ProcessPath;
        if (string.IsNullOrEmpty(host))
        {
            detail = "Environment.ProcessPath";
            return false;
        }

        var psi = new ProcessStartInfo
        {
            FileName = host,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom
        };

        // Агент наследует окружение родителя, а рантайм умеет писать дамп по переменным
        // среды. В памяти агента в момент падения лежит строка подключения с паролем,
        // поэтому диагностические дампы у него отключаем принудительно — независимо от
        // того, что настроено снаружи для основного приложения.
        foreach (var variable in DumpEnvironmentVariables)
            psi.Environment.Remove(variable);

        // Запуск через `dotnet App.dll`: ProcessPath указывает на dotnet.exe, и одного ключа мало.
        if (IsDotnetHost(host))
        {
            // В single-file образе Location пуст, и анализатор об этом предупреждает. Ветка
            // сюда не попадает: у single-file ProcessPath — это сам exe, а не dotnet.exe.
            // Пустое значение всё равно обработано ниже, поэтому предупреждение подавляем.
            var entry = EntryAssemblyLocation();
            if (string.IsNullOrEmpty(entry))
            {
                detail = "dotnet host";
                return false;
            }
            psi.ArgumentList.Add(entry);
        }

        psi.ArgumentList.Add(SwitchName);

        try
        {
            var process = Process.Start(psi);
            if (process is null)
            {
                detail = "Process.Start";
                return false;
            }

            // stderr обязательно вычитывать, иначе переполнение буфера подвесит агента
            // на записи, а нас — на чтении stdout. Содержимое нам не нужно.
            process.ErrorDataReceived += static (_, _) => { };
            process.BeginErrorReadLine();

            _agent = process;
            _agentInput = new StreamWriter(process.StandardInput.BaseStream, Utf8NoBom) { AutoFlush = true };
            _agentOutput = process.StandardOutput;

            // Пока мы запускали агента, приложение могло начать закрываться.
            if (Volatile.Read(ref _shuttingDown) != 0)
            {
                StopAgent(graceMs: 0);
                detail = "shutdown";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            StopAgent(graceMs: 0);
            return false;
        }
    }

    private static bool IsDotnetHost(string path) =>
        string.Equals(Path.GetFileNameWithoutExtension(path), "dotnet", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Останавливает агента и возвращает его код возврата; null — если узнать не удалось.
    /// <paramref name="graceMs"/> — сколько ждать самостоятельного завершения: нужно только
    /// когда процесс предположительно умирает и код возврата нам важен.
    /// </summary>
    private static int? StopAgent(int graceMs)
    {
        var process = Interlocked.Exchange(ref _agent, null);
        _agentInput = null;
        _agentOutput = null;
        return KillAndDispose(process, graceMs, waitAfterKillMs: 3000);
    }

    private static int? KillAndDispose(Process? process, int graceMs, int waitAfterKillMs)
    {
        if (process is null)
            return null;

        int? exitCode = null;
        try
        {
            // Код возврата снимаем до убийства: иначе настоящий 0xC0000409 подменится
            // кодом принудительного завершения (Kill даёт 0xFFFFFFFF), и единственная
            // примета того самого дефекта, ради которого всё построено, пропадёт.
            if (!process.HasExited && graceMs > 0)
                process.WaitForExit(graceMs);

            if (process.HasExited)
            {
                exitCode = process.ExitCode;
            }
            else
            {
                try { process.Kill(entireProcessTree: true); } catch { /* уже мёртв */ }
                // Код убитого процесса не берём: он всегда 0xFFFFFFFF и ничего не говорит.
                if (waitAfterKillMs > 0)
                    process.WaitForExit(waitAfterKillMs);
            }
        }
        catch
        {
            // Код возврата — только для диагностики, ради него падать незачем.
        }
        finally
        {
            try { process.Dispose(); } catch { }
        }

        return exitCode;
    }

    /// <summary>
    /// Разбирает кадр ответа агента.
    /// <paramref name="isPartial"/> — промежуточный диагноз: перебор ProgID ещё идёт, и агент
    /// присылает уже полученную причину заранее, на случай если следующий коннектор оборвёт
    /// процесс. Такой кадр не завершает запрос.
    /// <paramref name="progIds"/> — фактический список ProgID, которым агент перебирал кандидатов
    /// (в т.ч. кастомный из шаблона имени COM-коннектора, issue #175). Имя в подробности разрядов
    /// <see cref="ComFailureKind.InstanceFailed"/>/<see cref="ComFailureKind.NoConnection"/>
    /// проверяется по этому списку, а не по стандартному <see cref="OneCComConnector.KnownProgIds"/>.
    /// </summary>
    internal static ComReadResult ParseResponse(
        string line, int expectedSeq, IReadOnlyList<string> progIds,
        out bool desynchronized, out bool isPartial)
    {
        desynchronized = false;
        isPartial = false;

        if (!line.StartsWith(ResultPrefix, StringComparison.Ordinal))
        {
            desynchronized = true;
            return ComReadResult.Fail(ComFailureKind.Transport);
        }

        var parts = line[ResultPrefix.Length..].Split('\t');
        if (parts.Length < 2
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seq)
            || seq != expectedSeq)
        {
            desynchronized = true;
            return ComReadResult.Fail(ComFailureKind.Transport);
        }

        // Поле фактически использованного ProgID появилось в 0.3.7.7 (issue #175). Принимаем
        // и старые кадры без него (4 поля), и новые (5 полей): обе стороны поставляются вместе,
        // но строгий разбор не должен ломаться на уже записанном/присланном формате.
        if ((parts.Length == 4 || parts.Length == 5)
            && string.Equals(parts[1], "OK", StringComparison.Ordinal))
        {
            if (!TryDecode(parts[2], out var name) || !TryDecode(parts[3], out var version))
            {
                // Повреждённая нагрузка успеха — такая же рассинхронизация, как и у ошибки.
                // Иначе битый ответ молча превращался бы в разовый сбой обмена, агент
                // оставался бы жить, и следующий ответ снова читался бы не тот.
                desynchronized = true;
                return ComReadResult.Fail(ComFailureKind.Transport);
            }

            string? usedProgId = null;
            if (parts.Length == 5)
            {
                if (!TryDecode(parts[4], out var decodedProgId))
                {
                    desynchronized = true;
                    return ComReadResult.Fail(ComFailureKind.Transport);
                }
                usedProgId = decodedProgId;
            }

            return ComReadResult.Ok(new OneCConfigInfo(name, version), usedProgId);
        }

        // Промежуточный кадр отличается только меткой: поля те же, что у ошибки.
        var partialFrame = parts.Length == 5
            && string.Equals(parts[1], "PARTIAL", StringComparison.Ordinal);
        var isErr = string.Equals(parts[1], "ERR", StringComparison.Ordinal);

        // Ошибка приходит кадром из 5 полей (старый формат) или из 6 — шестым полем уезжает
        // последний перебранный ProgID (issue #175). PARTIAL остаётся в 5 полей: имя текущего
        // ProgID уже есть в его подробности.
        if (partialFrame && parts.Length == 5
            || isErr && (parts.Length == 5 || parts.Length == 6))
        {
            isPartial = partialFrame;

            // Поля ошибки разбираем так же строго, как поля успеха: повреждённое
            // содержимое — это сбой обмена, а не пустая подробность.
            if (!TryDecode(parts[3], out var errCode) || !TryDecode(parts[4], out var errText))
            {
                desynchronized = true;
                return ComReadResult.Fail(ComFailureKind.Transport);
            }

            // Шестое поле — последний перебранный ProgID. Принимаем только известный из
            // фактического списка перебора: иначе подделанный кадр внёс бы произвольный
            // текст в диагностику мимо решения о показе пароля (та же дисциплина, что
            // у подробностей InstanceFailed/NoConnection).
            string? lastProgId = null;
            if (parts.Length == 6)
            {
                if (!TryDecode(parts[5], out var decodedProgId) || !IsKnownProgId(decodedProgId, progIds))
                {
                    desynchronized = true;
                    return ComReadResult.Fail(ComFailureKind.Transport);
                }
                lastProgId = decodedProgId;
            }

            // Неопознанный разряд не отображаем в Transport с сохранением текста: подробность
            // Transport показывается пользователю мимо решения о пароле, и тогда свободный
            // текст 1С поехал бы в журнал и в диалог в обход этого решения. Правило «весь
            // свободный текст 1С проходит через признак пароля» должно держаться на разборе,
            // а не на том, что два списка разрядов в разных концах файла совпадают.
            if (!TryMapToken(parts[2], out var kind))
            {
                desynchronized = true;
                return ComReadResult.Fail(ComFailureKind.Transport);
            }

            // Форма подробности проверяется на приёме для каждого разряда. Свободный текст
            // разрешён единственному разряду — ошибке самой базы, и только он проходит через
            // решение о показе пароля. Всем прочим разрядам подробность либо запрещена, либо
            // обязана иметь проверяемый вид: число миллисекунд или имя известного ProgID.
            //
            // Раньше это правило держалось наполовину: строгость была введена только для
            // отказа разбора запроса, а три разряда принимали произвольный текст, который
            // затем подставлялся в сообщение пользователю мимо решения о пароле. Утечки
            // не возникало лишь потому, что агент таких строк туда не клал, — то есть
            // защита держалась на дисциплине отправителя. Здесь она держится на разборе.
            if (!DetailAllowed(kind, errCode, errText, progIds))
            {
                desynchronized = true;
                return ComReadResult.Fail(ComFailureKind.Transport);
            }

            // Код и текст приходят раздельно: текст может быть отброшен родителем,
            // если у базы есть пароль, а код останется в любом случае. Последний перебранный
            // ProgID уезжает в UsedProgId, чтобы диагностика показала его и при неуспехе.
            return lastProgId is null
                ? ComReadResult.Fail(kind, errText, errCode)
                : new ComReadResult(null, kind, errText, errCode, lastProgId);
        }

        // Кадр разобрать не удалось. Это тоже рассинхронизация: продолжать с таким агентом
        // нельзя, иначе систематически битые ответы шли бы без счёта и без перезапуска.
        desynchronized = true;
        return ComReadResult.Fail(ComFailureKind.Transport);
    }

    // Полезная нагрузка едет в Base64: строка подключения и сообщения 1С могут содержать
    // табуляцию и переводы строк, а протокол построчный и разделён табуляцией.
    internal static string Encode(string? value) =>
        Convert.ToBase64String(Utf8NoBom.GetBytes(value ?? string.Empty));

    private static string Decode(string? value) =>
        TryDecode(value, out var decoded) ? decoded : string.Empty;

    private static bool TryDecode(string? value, out string decoded)
    {
        if (string.IsNullOrEmpty(value))
        {
            decoded = string.Empty;
            return true;
        }

        try
        {
            // Строгий декодер: недопустимую последовательность нельзя молча превращать
            // в U+FFFD и выдавать за успешно прочитанное имя конфигурации.
            decoded = Utf8Strict.GetString(Convert.FromBase64String(value));
            return true;
        }
        catch
        {
            decoded = string.Empty;
            return false;
        }
    }

    // ---------------------------------------------------------------- сторона агента

    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint uMode);

    private const uint SEM_FAILCRITICALERRORS = 0x0001;
    private const uint SEM_NOGPFAULTERRORBOX = 0x0002;

    /// <summary>
    /// Если приложение запущено как агент — обслуживает запросы до закрытия stdin и возвращает true.
    /// Вызывается первой строкой Main, до создания Application: агенту не нужны ни WPF, ни темы.
    /// </summary>
    public static bool TryHandleCommandLine(string[]? args)
    {
        if (args is null)
            return false;

        var isAgent = Array.Exists(args, a =>
            string.Equals(a, SwitchName, StringComparison.OrdinalIgnoreCase));
        if (!isAgent)
            return false;

        // Падение агента — штатное событие этой схемы, а не повод показывать пользователю
        // системное окно «Unknown Hard Error» и писать на диск дамп, в памяти которого
        // лежит строка подключения вместе с паролем.
        try { SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX); } catch { }

        try
        {
            RunAgentLoop();
        }
        catch (IOException)
        {
            // Родитель закрылся посреди обмена — выходим тихо, а не аварийно.
        }
        catch (ObjectDisposedException)
        {
        }
        catch
        {
            // Агент не должен завершаться необработанным исключением: это даёт лишний
            // отчёт WER и выглядит как крах COM, которым не является.
        }

        return true;
    }

    private static void RunAgentLoop()
    {
        // Кодировку задаём своими потоками, а не через Console.OutputEncoding: у процесса
        // без консоли Console отдаёт кодовую страницу ANSI, и кириллица в имени конфигурации
        // приезжала бы родителю мусором.
        using var input = new StreamReader(Console.OpenStandardInput(), Utf8NoBom);
        using var output = new StreamWriter(Console.OpenStandardOutput(), Utf8NoBom) { AutoFlush = true };

        string? request;
        while ((request = input.ReadLine()) is not null)
        {
            if (request.Length == 0)
                continue;

            var parts = request.Split('\t');
            var seq = parts.Length > 0 ? parts[0] : "0";

            string response;
            try
            {
                response = HandleRequest(parts, seq, output);
            }
            catch (Exception ex)
            {
                response = Error(seq, "DBERR", Hresult(ex), ex.Message);
            }

            output.WriteLine(response);
        }
    }

    private static string HandleRequest(string[] parts, string seq, StreamWriter output)
    {
        // Формат запроса: seq \t timeoutMs \t base64(строка подключения)
        // [\t base64(список ProgID через таб)]. Четвёртое поле опционально: его присылает
        // только кастомный шаблон имени COM-коннектора (issue #175), и в нём перечень
        // кандидатов для перебора уезжает агенту целиком. Без него агент берёт KnownProgIds.
        // Пароль агенту не передаётся: решение, показывать ли текст ошибки, принимает родитель.
        //
        // Повреждённый запрос — это сбой обмена, а не ошибка базы. Раньше он отвечал DBERR,
        // и у родителя получался ложный диагноз «ошибка 1С при подключении», а внутренний
        // английский текст доезжал до диалога пользователя. Отвечаем отдельным разрядом
        // и без текста: родителю здесь сказать нечего, кроме локализованной общей фразы.
        if (parts.Length != 3 && parts.Length != 4)
            return Error(seq, "BADREQ", null, null);

        var timeoutMs = int.TryParse(parts[1], NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var parsed) && parsed > 0 ? parsed : 8000;

        if (!TryDecode(parts[2], out var connectString) || connectString.Length == 0)
            return Error(seq, "BADREQ", null, null);

        // Список ProgID для перебора. По умолчанию — стандартный KnownProgIds; кастомный
        // перечень из шаблона имени COM-коннектора приезжает четвёртым полем (см. выше).
        IReadOnlyList<string> progIds;
        if (parts.Length == 4)
        {
            if (!TryDecode(parts[3], out var encodedProgIds) || encodedProgIds.Length == 0)
                return Error(seq, "BADREQ", null, null);
            progIds = encodedProgIds.Split('\t');
        }
        else
        {
            progIds = OneCComConnector.KnownProgIds;
        }

        // Промежуточные диагнозы отправляем сразу: следующий ProgID может оборвать процесс,
        // и уже полученная причина иначе пропала бы вместе с ним.
        //
        // Писать в поток могут два потока сразу. По таймауту главный поток перестаёт ждать
        // COM-поток, но не убивает его: тот остался внутри COM-вызова, и прервать его нечем.
        // Значит он может дописать промежуточный кадр уже после того, как главный отправил
        // окончательный, — кадры перемешались бы, и родитель принял бы ответ за повреждённый.
        // Поэтому запись сериализуем, а после окончательного кадра промежуточные запрещаем.
        var writeLock = new object();
        var finished = false;

        void SendPartial(string progId, string text, string errCode)
        {
            lock (writeLock)
            {
                if (finished)
                    return;

                output.WriteLine(ResultPrefix + seq + "\tPARTIAL\tDBERR\t"
                    + Encode(errCode) + "\t" + Encode(progId + ": " + text));
            }
        }

        OneCConfigInfo? info;
        ComFailureKind kind;
        string? detail;
        string? code;
        string? usedProgId;
        try
        {
            info = ReadInProcess(
                connectString, timeoutMs, progIds, SendPartial, out kind, out detail, out code,
                out usedProgId);
        }
        finally
        {
            // Через finally: если чтение бросит, окончательный кадр запишет вызывающий,
            // и промежуточные к тому времени должны быть уже запрещены.
            lock (writeLock)
                finished = true;
        }

        // Имя и версия конфигурации приходят из Metadata и строку подключения содержать
        // не могут — их отдаём как есть.
        // В кадре успеха уезжает и фактически использованный ProgID (issue #175): родитель
        // показывает и логирует именно тот коннектор, который реально подключился.
        return info is null
            ? Error(seq, KindToToken(kind), code, detail, usedProgId)
            : ResultPrefix + seq + "\tOK\t" + Encode(info.Value.Name)
              + "\t" + Encode(info.Value.Version) + "\t" + Encode(usedProgId);
    }

    /// <summary>
    /// Ответ об ошибке: код и текст идут раздельными полями. Родитель может отбросить текст,
    /// если найдёт в нём пароль, и всё равно сказать пользователю что-то определённое по коду.
    /// <paramref name="lastProgId"/> — последний перебранный ProgID (issue #175): дописывается
    /// шестым полем, только когда известен, иначе формат старого кадра в 5 полей сохраняется.
    /// </summary>
    private static string Error(string seq, string token, string? code, string? text, string? lastProgId = null) =>
        ResultPrefix + seq + "\tERR\t" + token + "\t" + Encode(code) + "\t" + Encode(text)
        + (string.IsNullOrEmpty(lastProgId) ? string.Empty : "\t" + Encode(lastProgId));

    /// <summary>Самое глубокое вложенное исключение — настоящая причина, а не обёртка.</summary>
    private static Exception Deepest(Exception ex)
    {
        while (ex.InnerException is not null)
            ex = ex.InnerException;
        return ex;
    }

    /// <summary>
    /// Осмысленность разряда отказа. Диагноз с большим весом вытесняет меньший: ошибка
    /// самой базы полезнее, чем «коннектор не создался» от версии, которой тут и не место.
    /// Возвращает true, если новый разряд надо принять.
    /// <para>
    /// При равном весе удерживается уже принятый: выбирать между двумя одинаково осмысленными
    /// диагнозами не нужно. Единственный разряд, где такой выбор был бы содержательным, —
    /// ошибка самой базы, и там диагнозы копятся все до одного, а не вытесняют друг друга.
    /// </para>
    /// </summary>
    internal static bool Promote(ref int rank, ComFailureKind kind)
    {
        var weight = kind switch
        {
            ComFailureKind.DatabaseError => 4,
            ComFailureKind.NoConnection => 3,
            ComFailureKind.InstanceFailed => 2,
            _ => 1
        };

        if (weight <= rank)
            return false;

        rank = weight;
        return true;
    }

    /// <summary>
    /// Опознавательные сведения об исключении: имя типа и код. Пользовательских данных
    /// не содержат, поэтому отдаются всегда — даже когда текст ошибки пришлось скрыть.
    /// <para>
    /// Разворачиваем до самого глубокого вложенного исключения: <c>InvokeMember</c> оборачивает
    /// COM-ошибку в <c>TargetInvocationException</c>, а при двойной обёртке разворот на один
    /// уровень отдавал бы код промежуточной обёртки вместо настоящего. Нулевой код не показываем:
    /// «0x00000000» в качестве причины отказа бессмысленно.
    /// </para>
    /// </summary>
    private static string Hresult(Exception ex)
    {
        var inner = Deepest(ex);
        var name = inner.GetType().Name;
        return inner.HResult == 0
            ? name
            : name + " 0x" + inner.HResult.ToString("X8", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Единственная таблица соответствия разрядов и токенов протокола. Обе стороны выводятся
    /// из неё: пока таблица одна, добавить разряд в отправку и забыть в разборе невозможно,
    /// а раньше именно это расхождение позволяло свободному тексту 1С доехать до пользователя
    /// в обход решения о пароле.
    /// </summary>
    private static readonly (ComFailureKind Kind, string Token)[] TokenMap =
    {
        (ComFailureKind.NotRegistered, "NOTREG"),
        (ComFailureKind.InstanceFailed, "NOINST"),
        (ComFailureKind.Timeout, "TIMEOUT"),
        (ComFailureKind.NoConnection, "NOCONN"),
        (ComFailureKind.MetadataProperty, "METAPROP"),
        (ComFailureKind.MetadataRead, "METAREAD"),
        (ComFailureKind.DatabaseError, "DBERR"),
        (ComFailureKind.BadRequest, "BADREQ")
    };

    internal static string KindToToken(ComFailureKind kind)
    {
        foreach (var (k, token) in TokenMap)
        {
            if (k == kind)
                return token;
        }

        return "DBERR";
    }

    /// <summary>
    /// Допустима ли такая подробность у такого разряда.
    /// <para>
    /// Свободный текст 1С разрешён только разряду <see cref="ComFailureKind.DatabaseError"/> —
    /// единственному, который проходит через решение о показе пароля. Остальные разряды несут
    /// либо ничего, либо значение проверяемой формы, поэтому произвольный текст в кадре
    /// означает, что мы читаем не то, что думаем.
    /// </para>
    /// </summary>
    private static bool DetailAllowed(
        ComFailureKind kind, string code, string detail, IReadOnlyList<string> progIds) => kind switch
    {
        // Ошибка базы: свободный текст и код исключения. Дальше решает признак пароля.
        ComFailureKind.DatabaseError => true,

        // Число миллисекунд — больше в этом разряде сказать нечего.
        ComFailureKind.Timeout => code.Length == 0
            && int.TryParse(detail, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),

        // Имя ProgID из фактического списка перебора (включая кастомный из шаблона имени,
        // issue #175). Сообщение исключения сюда не кладём: оно приходит от сторонней
        // библиотеки и опознавательной ценности не добавляет, а разряд отказа и код уже
        // сказаны отдельно.
        ComFailureKind.InstanceFailed => IsKnownProgId(detail, progIds),
        ComFailureKind.NoConnection => code.Length == 0 && IsKnownProgId(detail, progIds),

        // Разряды без подробности.
        _ => code.Length == 0 && detail.Length == 0
    };

    private static bool IsKnownProgId(string value, IReadOnlyList<string> progIds)
    {
        foreach (var progId in progIds)
        {
            if (string.Equals(progId, value, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Обратное отображение. Возвращает false для неизвестного токена: разбирать такой кадр
    /// нельзя, потому что мы не знаем, что означают его поля.
    /// </summary>
    internal static bool TryMapToken(string token, out ComFailureKind kind)
    {
        foreach (var (k, t) in TokenMap)
        {
            if (string.Equals(t, token, StringComparison.Ordinal))
            {
                kind = k;
                return true;
            }
        }

        kind = ComFailureKind.Transport;
        return false;
    }

    /// <summary>
    /// Собственно COM-обращение. Живёт только в агенте: именно здесь возможен нативный обрыв,
    /// который мы и изолируем. ProgID перебираются по фактическому списку <paramref name="progIds"/>:
    /// стандартному <see cref="OneCComConnector.KnownProgIds"/> или кастомному из шаблона имени
    /// COM-коннектора (issue #175).
    /// </summary>
    private static OneCConfigInfo? ReadInProcess(
        string connectString, int timeoutMs, IReadOnlyList<string> progIds,
        Action<string, string, string>? onPartial,
        out ComFailureKind kind, out string? detail, out string? code, out string? usedProgId)
    {
        OneCConfigInfo? result = null;
        var localKind = ComFailureKind.NotRegistered;
        string? localDetail = null;
        string? localCode = null;
        string? localUsedProgId = null;

        // Диагноз держим самый осмысленный, а не первый попавшийся. Прежде было наоборот:
        // битая регистрация V83 закрепляла вердикт «не удалось создать экземпляр», и
        // настоящая причина от работоспособного V82 («неправильное имя или пароль»)
        // до пользователя не доходила.
        var rank = 0;

        // Ошибки самой базы не выбираем, а копим все. Выбор между двумя одинаково
        // осмысленными диагнозами проигрывает в любую сторону: оставишь ранний — для базы
        // 8.2 победит «несовместимая версия» от V83 вместо настоящей причины от V82;
        // оставишь поздний — то же самое зеркально произойдёт с базой 8.3. Теряется
        // диагностика в обоих случаях, поэтому не теряем ничего.
        var dbErrors = new List<(string ProgId, string Text, string Code)>();

        var thread = new Thread(() =>
        {
            foreach (var progId in progIds)
            {
                object? connector;

                // Получение типа и создание объекта — отдельно от подключения. Их отказ
                // означает «эта версия платформы непригодна», и надо пробовать следующую:
                // V83 нередко остаётся в реестре после сноса платформы, а рабочим оказывается V82.
                try
                {
                    var type = Type.GetTypeFromProgID(progId);
                    if (type is null)
                        continue;

                    connector = Activator.CreateInstance(type);
                    if (connector is null)
                    {
                        if (Promote(ref rank, ComFailureKind.InstanceFailed))
                        {
                            localKind = ComFailureKind.InstanceFailed;
                            localDetail = progId;
                            localCode = null;
                        }
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    // ProgID есть, но объект не создаётся: битая регистрация, несоответствие
                    // разрядности, DLL занята. Причину запоминаем — иначе отказ выдаётся
                    // за «коннектор не зарегистрирован», а этот вердикт глушит COM сразу.
                    if (Promote(ref rank, ComFailureKind.InstanceFailed))
                    {
                        localKind = ComFailureKind.InstanceFailed;
                        // Только имя ProgID: сообщение об отказе создания объекта приходит
                        // от сторонней библиотеки, проверить его форму нельзя, а всё
                        // опознавательное уже есть в коде.
                        localDetail = progId;
                        localCode = Hresult(ex);
                    }
                    continue;
                }

                // Фактически попробованный коннектор (issue #175): дошли до вызова Connect —
                // значит этот ProgID реально использовался, и о нём надо сообщить родителю,
                // даже если подключение завершится неудачей. Прогнутые мимо кандидаты
                // (не зарегистрированы, экземпляр не создался) в диагностику не попадают,
                // иначе последний из них вводил бы в заблуждение («V81» при пустом списке).
                localUsedProgId = progId;

                object? connection = null;
                object? metadata = null;
                try
                {
                    connection = connector.GetType().InvokeMember(
                        "Connect", BindingFlags.InvokeMethod, null, connector,
                        new object[] { connectString });
                    if (connection is null)
                    {
                        if (Promote(ref rank, ComFailureKind.NoConnection))
                        {
                            localKind = ComFailureKind.NoConnection;
                            localDetail = progId;
                            localCode = null;
                        }

                        continue;
                    }

                    metadata = InvokeGet(connection, "Metadata");
                    if (metadata is null)
                    {
                        // Подробность и код от предыдущего ProgID здесь не наши — чистим,
                        // иначе они уедут родителю в паре с чужим разрядом.
                        localKind = ComFailureKind.MetadataProperty;
                        localDetail = null;
                        localCode = null;
                        return;
                    }

                    var name = AsString(metadata, "Name") ?? AsString(metadata, "Synonym") ?? string.Empty;
                    var version = AsString(metadata, "Version") ?? string.Empty;

                    if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(version))
                    {
                        localKind = ComFailureKind.MetadataRead;
                        localDetail = null;
                        localCode = null;
                        return;
                    }

                    result = new OneCConfigInfo(name, version);
                    localKind = ComFailureKind.None;
                    // localUsedProgId уже равен текущему progId (установлен перед вызовом Connect).
                    return;
                }
                catch (Exception ex)
                {
                    // Текст ошибки 1С умеет цитировать строку подключения целиком, но решать,
                    // показывать его или нет, будет родитель: только у него есть пароль.
                    // Здесь отдаём и текст, и опознавательный код — родитель выберет.
                    // Текст берём с того же уровня, что и код: иначе пользователь получал бы
                    // «Exception has been thrown by the target of an invocation», а код
                    // рядом — от совсем другого исключения.
                    var text = Deepest(ex).Message;
                    var errCode = Hresult(ex);
                    dbErrors.Add((progId, text, errCode));
                    Promote(ref rank, ComFailureKind.DatabaseError);
                    localKind = ComFailureKind.DatabaseError;
                    try { onPartial?.Invoke(progId, text, errCode); } catch { /* канал закрыт */ }

                    // Перебор продолжаем на любой ошибке подключения — так же, как в исходном
                    // ConnectCore. Отсеивать по разряду отказа нельзя: база 8.2 через V83 даёт
                    // обычное исключение о несовместимости версий, коннектор при этом создаётся
                    // нормально, ветка InstanceFailed такой случай не ловит, а рабочий V82
                    // находится только следующей итерацией.
                }
                finally
                {
                    Release(metadata);
                    Release(connection);
                    Release(connector);
                }
            }

            // Перебор окончен без успеха. Если базу не пустил не один коннектор, а несколько,
            // отдаём все причины разом, помечая каждую своим ProgID. Единственная причина
            // отдаётся как раньше — без пометки, иначе привычное сообщение обросло бы шумом.
            if (localKind == ComFailureKind.DatabaseError && dbErrors.Count > 0)
            {
                if (dbErrors.Count == 1)
                {
                    localDetail = dbErrors[0].Text;
                    localCode = dbErrors[0].Code;
                }
                else
                {
                    localDetail = JoinDiagnoses(dbErrors, static e => e.Text);
                    localCode = JoinDiagnoses(dbErrors, static e => e.Code);
                }
            }
        })
        {
            IsBackground = true,
            Name = "1C-COM-ConfigRead"
        };

        // STA обязателен для COM-объектов 1С.
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!thread.Join(timeoutMs))
        {
            // Поток фоновый и завершению агента не мешает. Родитель на этот ответ агента
            // убьёт: внутри остался повисший COM-вызов, и переиспользовать процесс нельзя.
            kind = ComFailureKind.Timeout;
            detail = timeoutMs.ToString(CultureInfo.InvariantCulture);
            code = null;
            usedProgId = null;
            return null;
        }

        kind = localKind;
        detail = localDetail;
        code = localCode;
        usedProgId = localUsedProgId;
        return result;
    }

    /// <summary>
    /// Склеивает диагнозы нескольких ProgID в одну строку вида «V83: причина; V82: причина».
    /// Пометка обязательна: без неё две причины подряд читаются как одна бессвязная.
    /// </summary>
    internal static string JoinDiagnoses(
        List<(string ProgId, string Text, string Code)> items,
        Func<(string ProgId, string Text, string Code), string> select)
    {
        var sb = new StringBuilder();
        foreach (var item in items)
        {
            if (sb.Length > 0)
                sb.Append("; ");
            sb.Append(item.ProgId).Append(": ").Append(select(item));
        }

        return sb.ToString();
    }

    private static object? InvokeGet(object target, string property)
    {
        try
        {
            return target.GetType().InvokeMember(
                property, BindingFlags.GetProperty, null, target, null);
        }
        catch
        {
            return null;
        }
    }

    private static string? AsString(object target, string property) =>
        InvokeGet(target, property)?.ToString();

    private static void Release(object? comObject)
    {
        try
        {
            if (comObject is not null && Marshal.IsComObject(comObject))
                Marshal.ReleaseComObject(comObject);
        }
        catch
        {
            // Освобождение COM-объекта не должно ронять агента.
        }
    }
}
#endif
