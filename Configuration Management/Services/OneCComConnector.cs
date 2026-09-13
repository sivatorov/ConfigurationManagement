using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Configuration_Management.Localization;
using Configuration_Management.Models;
using Microsoft.Win32;

namespace Configuration_Management.Services;

/// <summary>
/// Реализация подключения к информационным базам 1С через COM-коннектор.
/// Все COM-операции выполняются в фоновом STA-потоке (требование 1С) с ограничением
/// по времени, чтобы не блокировать UI и не «зависать» навсегда при недоступной базе.
/// </summary>
public sealed class OneCComConnector : IOneCComConnector
{
    private readonly IAppLogger _logger;
    private readonly IInfobaseRepository _repository;

    // Шаблон имени COM-коннектора читается из настроек лениво и кэшируется: повторное
    // чтение settings.json для каждой базы при переборе списка было бы лишним дисковым
    // вводом. Значение приходит через ApplyTemplate — при загрузке настроек профиля
    // и при правке настройки, — а ленивое чтение настроек остаётся лишь страховкой
    // на случай обращения до загрузки. Кэш без обновления не годился: шаблон, заданный
    // после первого COM-чтения в сессии, не действовал бы до перезапуска приложения,
    // а ленивое чтение вместо явного делало поведение зависящим от того, случилось ли
    // COM-чтение до смены активного профиля (issue #175). Кэш статический: сервис живёт как singleton (AppServices),
    // и хранить значение в экземпляре означало бы, что сбрасывать нужно именно тот экземпляр.
    // Оговорка: кэш общий для всех экземпляров класса, а настройки читает репозиторий
    // экземпляра. Пока экземпляр один, это одно и то же; если понадобится второй коннектор
    // со своим репозиторием, кэш придётся унести в экземпляр.
    private static readonly object TemplateLock = new();
    private static string? _templateCache;
    private static bool _templateLoaded;

    /// <summary>
    /// ProgID COM-коннекторов 1С в порядке приоритета (от новых версий к старым).
    /// <para>
    /// V85 добавлен: у платформы 8.5 собственный ProgID и собственный CLSID, прежний список
    /// её не находил вовсе. Порядок важен вдвойне — некоторые сборки коннектора 8.3.27
    /// (проверены 8.3.27.2130 и 8.3.27.2214) обрывают процесс нативным fast-fail на вызове
    /// Connect, поэтому исправный V85 должен опрашиваться раньше них. Замерено, что коннектор
    /// 8.5 читает базы 8.3 с тем же результатом, что и исправная сборка 8.3.
    /// </para>
    /// </summary>
    public static readonly string[] KnownProgIds =
    {
        "V85.COMConnector",
        "V83.COMConnector",
        "V82.COMConnector",
        "V81.COMConnector"
    };

    /// <summary>
    /// Шаблон имени COM-коннектора из настроек (issue #175). Пустая строка означает
    /// стандартные ProgID (<see cref="KnownProgIds"/>). Разворачивается по версии платформы
    /// конкретной базы через <see cref="BuildProgIdCandidates"/>.
    /// </summary>
    private string? ComConnectorNameTemplate
    {
        get
        {
            lock (TemplateLock)
            {
                if (_templateLoaded)
                    return _templateCache;

                try
                {
                    _templateCache = _repository.LoadSettings()?.ComConnectorNameTemplate ?? "";
                }
                catch
                {
                    // Настройки недоступны — остаёмся на стандартных ProgID.
                    _templateCache = "";
                }

                _templateLoaded = true;
                return _templateCache;
            }
        }
    }

    /// <summary>
    /// true, если задан непустой шаблон имени COM-коннектора (issue #175). От этого признака
    /// зависит, допустим ли быстрый отказ по «кэшу доступности»: тот проверяет только
    /// стандартные ProgID (<see cref="KnownProgIds"/>), а при кастомном шаблоне коннектор
    /// может быть зарегистрирован под именем вне этого списка, поэтому путь кэша нельзя
    /// применять даже тогда, когда версию платформы развернуть не удалось (иначе шаблон
    /// не подхватывался бы и до перебора кандидатов не доходило).
    /// </summary>
    private bool HasTemplate => !string.IsNullOrWhiteSpace(ComConnectorNameTemplate);

    /// <summary>
    /// Список ProgID для перебора при подключении к заданной базе (issue #175).
    /// Если задан шаблон, разворачивает его по версии платформы базы и ставит первым,
    /// дополняя стандартный список (без дублей). При пустом шаблоне возвращает сам
    /// <see cref="KnownProgIds"/>, чтобы поведение по умолчанию не менялось.
    /// </summary>
    private IReadOnlyList<string> GetProgIds(Infobase infobase)
        => BuildProgIdCandidates(ComConnectorNameTemplate, infobase?.PlatformVersion);

    /// <summary>
    /// Строит список ProgID-кандидатов для перебора. <paramref name="template"/> — шаблон
    /// имени COM-коннектора, <paramref name="platformVersion"/> — версия платформы базы
    /// (например «8.3.27.1644»). Если шаблон пуст или версию развернуть нельзя, возвращается
    /// стандартный <see cref="KnownProgIds"/>.
    /// </summary>
    internal static IReadOnlyList<string> BuildProgIdCandidates(string? template, string? platformVersion)
    {
        var expanded = ExpandTemplate(template, platformVersion);
        if (expanded is null)
            return KnownProgIds;

        var list = new List<string>(KnownProgIds.Length + 1) { expanded };
        foreach (var progId in KnownProgIds)
        {
            if (!string.Equals(progId, expanded, StringComparison.OrdinalIgnoreCase))
                list.Add(progId);
        }

        return list;
    }

    /// <summary>
    /// Разворачивает шаблон имени COM-коннектора по версии платформы (issue #175).
    /// Делегирует общему помощнику <see cref="ComConnectorTemplate.Expand"/>, чтобы
    /// поведение при подключении совпадало с интерактивным предпросмотром в окне
    /// настроек (включая обрезку разделителей перед пустыми сегментами версии).
    /// Возвращает null, если шаблон пуст, а также если шаблон содержит плейсхолдеры,
    /// но версии нет, её нельзя разобрать или имя выходит пустым (тогда разворачивать
    /// нечего и используется стандартный список). Готовое имя без плейсхолдеров версии
    /// не требует (issue #175).
    /// </summary>
    internal static string? ExpandTemplate(string? template, string? platformVersion)
        => ComConnectorTemplate.Expand(template, platformVersion);

    /// <summary>
    /// Возвращает первый ProgID из списка кандидатов, который реально зарегистрирован
    /// в системе (issue #174). Стандартный список начинается с V85 независимо от версии
    /// базы; если V85 не установлен (или не подходит по разрядности), показывать его как
    /// «использованный» было бы вводящей в заблуждение диагностикой. Возвращает null,
    /// если ни один из кандидатов не зарегистрирован.
    /// </summary>
    internal static string? FirstRegisteredProgId(IReadOnlyList<string> progIds)
    {
        foreach (var progId in progIds)
        {
            try
            {
                if (Type.GetTypeFromProgID(progId) is not null)
                    return progId;
            }
            catch
            {
                // Не зарегистрирован / несоответствие разрядности — пробуем следующий.
            }
        }
        return null;
    }

    // -- Кэш доступности COM-коннекторов 1С ----------------------------------
    // Реестр COM не меняется в течение сеанса (кроме ручной регистрации),
    // поэтому результат проверки кэшируется, чтобы не повторять её для каждой базы.
    private static readonly object AvailabilityLock = new();
    private static bool? _connectorsAvailable;
    private static string? _cachedAvailabilityStatus;

    /// <summary>
    /// Проверяет (с кэшированием), зарегистрирован ли хотя бы один COM-коннектор 1С,
    /// фактически доступный из текущего процесса (учитывает разрядность через GetTypeFromProgID).
    /// Если коннектор недоступен, вызывающие методы быстро прерываются, не плодя потоки и исключения.
    /// </summary>
    public static bool IsComConnectorAvailable()
    {
        lock (AvailabilityLock)
        {
            if (_connectorsAvailable is { } cached)
                return cached;

            var available = false;
            foreach (var progId in KnownProgIds)
            {
                try
                {
                    if (Type.GetTypeFromProgID(progId) is not null)
                    {
                        available = true;
                        break;
                    }
                }
                catch
                {
                    // Не зарегистрирован / несоответствие разрядности — пробуем следующий.
                }
            }

            _connectorsAvailable = available;
            _cachedAvailabilityStatus = DescribeProgIdStatus();
            return available;
        }
    }

    /// <summary>
    /// Сбрасывает кэш доступности (вызывается после ручной регистрации COM-коннектора).
    /// </summary>
    public static void ResetAvailabilityCache()
    {
        lock (AvailabilityLock)
        {
            _connectorsAvailable = null;
            _cachedAvailabilityStatus = null;
        }
    }

    /// <summary>Кэшированное описание состояния ProgID (реестр).</summary>
    private static string AvailabilityStatusText
    {
        get
        {
            lock (AvailabilityLock)
                return _cachedAvailabilityStatus ?? DescribeProgIdStatus();
        }
    }

    /// <summary>
    /// Заполняет LastError и пишет в лог понятное сообщение о том, что COM-коннектор
    /// не зарегистрирован (быстрый отказ без создания потока).
    /// </summary>
    private void SetConnectorUnavailableError(string baseName)
    {
        var status = AvailabilityStatusText;
        LastError = LocalizationManager.T("Com.NotFound") + " " +
                    status + " " +
                    LocalizationManager.T("Com.NotFoundInstallHint");
        _logger.Warn($"COM-коннектор 1С не зарегистрирован в системе для базы «{baseName}». {status}");
    }

    /// <summary>
    /// Текст последней ошибки COM-подключения (для вывода в UI при диагностике).
    /// Сбрасывается перед каждой новой успешной попыткой.
    /// </summary>
    public string? LastError { get; private set; }

    /// <inheritdoc />
    public string? LastUsedProgId { get; private set; }

    /// <inheritdoc />
    public string? LastUsedPlatformVersion { get; private set; }

    public OneCComConnector(IAppLogger logger, IInfobaseRepository repository)
    {
        _logger = logger;
        _repository = repository;
    }

    /// <inheritdoc />
    public string BuildConnectString(Infobase infobase) => BuildComConnectString(infobase);

    /// <inheritdoc />
    /// <remarks>
    /// ОПАСНО: выполняет COM-вызов в текущем процессе, а под CoreCLR это обрывает приложение
    /// нативным fast-fail 0xC0000409 без управляемого исключения. Вызывающих в приложении нет.
    /// Атрибут продублирован здесь намеренно: на интерфейсе он не предупреждает того, кто
    /// возьмёт конкретный тип.
    /// </remarks>
    [Obsolete("Выполняет COM-вызов в текущем процессе: под CoreCLR это обрывает приложение "
              + "нативным fast-fail 0xC0000409 без управляемого исключения. "
              + "Используйте путь через процесс-агент (ComReadHost).")]
    public OneCComConnection? Connect(Infobase infobase, int timeoutMs = 8000)
    {
        if (infobase is null) return null;

        // Список ProgID для перебора с учётом шаблона имени (issue #175). Кэш доступности
        // ускоряет только стандартный путь: при кастомном шаблоне коннектор может быть
        // зарегистрирован под именем вне KnownProgIds, и преждевременный отказ по кэшу
        // съедал бы его. Проверку в этом случае оставляем перебору ConnectCore.
        var progIds = GetProgIds(infobase);

        // Быстрый отказ по кэшу доступности допустим только без кастомного шаблона: он
        // проверяет лишь стандартные ProgID, а при заданном шаблоне (даже если версию
        // платформы развернуть не удалось) нужно дойти до перебора кандидатов и зафиксировать
        // это в журнале. Гейт держим на самом признаке шаблона, а не на ReferenceEquals:
        // при неудавшемся разворачивании BuildProgIdCandidates возвращает KnownProgIds,
        // и сравнение ссылок ложно считало бы шаблон отсутствующим.
        if (!HasTemplate && !IsComConnectorAvailable())
        {
            SetConnectorUnavailableError(infobase.Name);
            return null;
        }

        OneCComConnection? result = null;
        Exception? error = null;

        var thread = new Thread(() =>
        {
            try
            {
                result = ConnectCore(infobase, progIds);
            }
            catch (Exception ex)
            {
                error = ex;
                LastError = ex.Message;
            }
        })
        {
            IsBackground = true,
            Name = "1C-COM-Connector"
        };

        // STA обязателен для COM-объектов 1С.
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!thread.Join(timeoutMs))
        {
            LastError ??= string.Format(LocalizationManager.T("Com.TimeoutConnectFormat"), timeoutMs);
            _logger.Error($"Превышен таймаут COM-подключения к базе «{DisplayName(infobase)}».");
            return null;
        }
        if (error is not null)
            return null;

        return result;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Чтение выполняется не здесь, а в дочернем процессе (<see cref="ComReadHost"/>).
    /// Под CoreCLR вызов <c>Connect</c> у comcntr.dll обрывает процесс нативным fast-fail
    /// (0xC0000409) без управляемого исключения — перехватить его в этом процессе нельзя,
    /// поэтому COM изолирован. Подробности и история — в комментарии к ComReadHost.
    /// </remarks>
    /// <summary>
    /// Строит текст этапа «создание COM-подключения» для диалога прогресса (issue #174):
    /// указывает фактический ProgID (например, «V83.COMConnector»), которым идёт подключение,
    /// и версию платформы базы. ProgID может быть null (COM недоступен, Linux-сборка),
    /// тогда текст деградирует до варианта без него.
    /// </summary>
    private static string BuildDetectConnectStageMessage(string? platformVersion, string? progId)
    {
        var hasProgId = !string.IsNullOrWhiteSpace(progId);
        var hasVersion = !string.IsNullOrWhiteSpace(platformVersion);

        if (hasProgId && hasVersion)
            return string.Format(LocalizationManager.T("Connection.DetectStageConnectWithProgIdFormat"), progId, platformVersion);
        if (hasProgId)
            return string.Format(LocalizationManager.T("Connection.DetectStageConnectWithProgIdNoVersion"), progId);
        if (hasVersion)
            return string.Format(LocalizationManager.T("Connection.DetectStageConnectFormat"), platformVersion);
        return LocalizationManager.T("Connection.DetectStageConnectNoVersion");
    }

    public OneCConfigInfo? ReadConfigurationInfo(Infobase infobase, int timeoutMs = 8000, Action<string>? onStage = null)
    {
        if (infobase is null) return null;
        // Параметр объявлен ненулевым, а защита выше уже вернула бы раньше. Локальная
        // ссылка снимает для анализатора сомнение «может быть null» на последующих
        // обращениях к базе внутри метода (CS8602/CS8604).
        var ib = infobase!;

        // Поколение сброса снимаем раньше всего: пока запрос готовится и работает, настройку
        // могли сменить, и тогда сброс поколения обязан снять у этого запроса право защёлкнуть
        // недоступность COM — иначе отказ по прежнему списку имён погасил бы COM для нового
        // имени, и до перезапуска оно бы не проверилось (issue #175).
        var epoch = ComReadHost.CurrentEpoch();

        // Снимок шаблона берём до построения кандидатов и дальше пользуемся только им:
        // значение может измениться посреди чтения (сеттер настройки работает в потоке
        // интерфейса), и тогда кандидаты относились бы к одному шаблону, а признак,
        // быстрый отказ и запись в журнал — к другому (issue #175).
        var template = ComConnectorNameTemplate;
        var hasTemplate = !string.IsNullOrWhiteSpace(template);

        // Список ProgID с учётом шаблона имени (issue #175). Как и в Connect, кэш доступности
        // обходим при кастомном шаблоне: он проверяет только KnownProgIds, а перечень кандидатов
        // агент получит явно.
        var progIds = BuildProgIdCandidates(template, ib.PlatformVersion);

        // Запоминаем фактически использованный ProgID и версию платформы для диагностики
        // в UI (issue #174/#175). При кастомном шаблоне первым кандидатом всегда идёт
        // развёрнутое по версии имя (например, V83.COMConnector_27 для базы 8.3.27): его и
        // показываем в предпросмотре и при неуспехе, даже если оно не зарегистрировано —
        // пользователю важно увидеть, что именно дал его шаблон. При пустом шаблоне поведение
        // прежнее: берём первый реально зарегистрированный стандартный коннектор, иначе
        // диагностика объявляла бы «использованным» V85, которого на машине нет.
        LastUsedProgId = ReferenceEquals(progIds, KnownProgIds)
            ? FirstRegisteredProgId(progIds)
            : progIds[0];
        // ib гарантированно ненулевой (защита выше + null-forgiving), поэтому ?. здесь
        // избыточен и вдобавок сбивает анализ состояния потока для последующих обращений.
        LastUsedPlatformVersion = ib.PlatformVersion;

        // Быстрый отказ по кэшу доступности — только без кастомного шаблона (см. комментарий
        // в Connect): при заданном шаблоне даже неудавшееся разворачивание версии не должно
        // блокировать перебор кандидатов и запись попыток в журнал. Признак берём из снимка
        // выше, чтобы он относился к тому же шаблону, по которому построены кандидаты.
        if (!hasTemplate && !IsComConnectorAvailable())
        {
            SetConnectorUnavailableError(ib.Name);
            return null;
        }

        // Признак пароля берём у того, кто строку собирает: он единственный знает наверняка,
        // положил ли туда Pwd. Разбирать уже собранную строку обратно — лишний источник
        // расхождений: список секретных параметров пришлось бы держать синхронным в двух местах.
        var connectString = BuildComConnectString(ib, out var hasSecret);
        if (string.IsNullOrWhiteSpace(connectString))
        {
            LastError = LocalizationManager.T("Com.ConnStringBuildFailed");
            return null;
        }

        // Запоминаем состояние до вызова: если COM был отключён ещё раньше, писать об этом
        // в журнал незачем — на списке из десятков баз это дало бы десятки одинаковых строк
        // подряд на каждом старте. Снимок один на обе записи (предупреждение о шаблоне ниже
        // и сообщение об ошибке чтения), иначе они подавлялись бы по разным состояниям
        // и предупреждение могло сообщить о переборе, которого не было (issue #175).
        var alreadyDisabled = ComReadHost.ComUnavailable;

        // Шаблон задан, но развернуть его по версии платформы базы не удалось: имя из шаблона
        // в переборе не участвует, и в журнале это должно быть сказано отдельно (issue #175).
        // Иначе причина неотличима от «коннектор не зарегистрирован», а именно так выглядел
        // отказ у пользователя: строка про кандидатов есть, а его имени в ней нет. Запись
        // идёт по базе, как и сообщение об ошибке ниже, и подавляется в тех же случаях:
        // COM уже погашен на сессию или приложение закрывается — тогда отказ вызван не
        // состоянием шаблона, и строка на каждую оставшуюся базу была бы шумом.
        if (hasTemplate && ReferenceEquals(progIds, KnownProgIds)
            && !alreadyDisabled && !ComReadHost.IsShuttingDown)
        {
            // Либо версии платформы у базы нет вовсе, либо она есть, но применить её
            // к шаблону не удалось: имя выходит пустым или версия не разбирается
            // (в первых двух сегментах нет цифр). Последние два случая в сообщении
            // не разделяем: устраняются они одинаково.
            var reason = string.IsNullOrWhiteSpace(ib.PlatformVersion)
                ? "у базы не указана версия платформы 1С"
                : $"версию платформы базы «{ib.PlatformVersion}» не удалось применить "
                  + "к шаблону: имя выходит пустым или версия не разбирается";
            _logger.Warn(
                $"Шаблон имени COM-коннектора «{template}» не развёрнут "
                + $"для базы «{DisplayName(ib)}»: {reason}. "
                + $"Перебираются стандартные коннекторы: {string.Join(", ", progIds)}.");
        }

        // Сообщаем этапы в диалог прогресса кнопки «Определить» (issue #174): сначала —
        // создание COM-подключения с фактическим ProgID и версией платформы базы,
        // затем — чтение свойств.
        onStage?.Invoke(BuildDetectConnectStageMessage(ib.PlatformVersion, LastUsedProgId));

        var result = ComReadHost.Read(connectString, timeoutMs, progIds, epoch);
        onStage?.Invoke(LocalizationManager.T("Connection.DetectStageRead"));
        if (result.Failure == ComFailureKind.None && result.Info is not null)
        {
            LastError = null;

            // Агент сообщает фактически подключившийся ProgID (issue #175): родительская
            // оценка FirstRegisteredProgId лишь предсказывала, какой кандидат зарегистрирован,
            // а тут — тот, который реально установил соединение. Обновляем диагностику
            // и пишем в журнал, чтобы было видно, какой именно COM-коннектор использовался.
            if (!string.IsNullOrWhiteSpace(result.UsedProgId))
                LastUsedProgId = result.UsedProgId;

            _logger.Info(
                $"Подключение к базе «{DisplayName(ib)}» через COM-коннектор "
                + $"{LastUsedProgId ?? "(не определён)"} успешно."
                + $" Версия платформы: {LastUsedPlatformVersion ?? "(не указана)"}.");
            return result.Info;
        }

        // При неуспехе агент сообщает последний перебранный ProgID (issue #175): даже если
        // соединение не установилось, диагностика показывает, до какого коннектора дошёл
        // перебор (например, имя из кастомного шаблона, которое не зарегистрировано в системе).
        if (!string.IsNullOrWhiteSpace(result.UsedProgId))
            LastUsedProgId = result.UsedProgId;

        // Решение о тексте ошибки принимаем здесь, и принимаем его по тому, что сами
        // положили в строку подключения, а не по тексту ответа: если пароля в строке нет,
        // то 1С его не видела и процитировать не могла — текст безопасен по построению.
        // Если есть — текст не выпускаем вовсе. Никаких догадок о том, что процитировала 1С.
        LastError = MaskCredentials(DescribeFailure(result, hasSecret));

        // На выходе из приложения писать в журнал нечего: отказ вызван нашим же закрытием,
        // а не состоянием COM. Без этой проверки закрытие окна во время фонового дочитывания
        // добавляло бы в журнал по строке на каждую оставшуюся базу — защёлка такие отказы
        // намеренно не считает и поток записей не останавливает.
        if (!alreadyDisabled && !ComReadHost.IsShuttingDown)
        {
            // Внутреннее имя шага в журнал добавляем, а в сообщение пользователю — нет.
            var trace = result.Failure == ComFailureKind.AgentStart && !string.IsNullOrEmpty(result.Detail)
                ? $" ({result.Detail})"
                : string.Empty;
            // В журнал пишем целиком строку подключения (issue #174): по одной лишь фразе о таймауте
            // трудно понять, какая именно база/сервер/файл подставлялись и не потерялось ли что-то
            // при сборке строки. Пароль маскируем тем же правилом, что и для ошибок от 1С.
            _logger.Error(
                $"Не удалось прочитать сведения о конфигурации базы «{DisplayName(ib)}»: {LastError}{trace}."
                + $" Использованный COM-коннектор: {LastUsedProgId ?? "(не определён)"}."
                + $" Строка подключения: {MaskCredentials(connectString)}. Таймаут: {timeoutMs} мс."
                + $" Кандидаты COM-коннекторов (в порядке перебора): {string.Join(", ", progIds)}.");
        }

        return null;
    }

    /// <summary>
    /// Переводит разряд отказа в текст для пользователя. Тексты подбираются здесь, а не в агенте:
    /// в агенте локализация не поднята (он выходит до инициализации приложения), да и передавать
    /// по каналу код надёжнее, чем готовую строку.
    /// </summary>
    private static string DescribeFailure(ComReadResult result, bool hasSecret) => result.Failure switch
    {
        ComFailureKind.Disabled => LocalizationManager.T("Com.DisabledForSession"),
        // Подробность здесь — внутреннее имя шага («Process.Start», «dotnet host»). Оно
        // помогает разбирать отказ по журналу, но пользователю в диалоге не говорит ничего,
        // и переводу не подлежит, поэтому в сообщение не идёт: в журнал его пишут отдельно.
        ComFailureKind.AgentStart => LocalizationManager.T("Com.AgentStartFailed"),
        // Код возврата известен не всегда: если процесс придерживает Windows Error
        // Reporting, снять его не удаётся. Показывать «(код возврата )» нельзя.
        ComFailureKind.AgentCrashed => string.IsNullOrEmpty(result.Detail)
            ? LocalizationManager.T("Com.AgentCrashedUnknownCode")
            : string.Format(LocalizationManager.T("Com.AgentCrashedFormat"), result.Detail),
        ComFailureKind.Timeout => string.Format(
            LocalizationManager.T("Com.TimeoutReadFormat"), result.Detail ?? string.Empty),
        // Тот же текст, что и у быстрого отказа по кэшу (SetConnectorUnavailableError):
        // одно состояние — одна подсказка, включая совет установить платформу.
        ComFailureKind.NotRegistered => LocalizationManager.T("Com.NotFound")
                                        + " " + DescribeProgIdStatus()
                                        + " " + LocalizationManager.T("Com.NotFoundInstallHint"),
        // Коннектор в реестре есть, но не создаётся. Говорить «не найден» здесь нельзя:
        // текст противоречил бы и реестру, и быстрой проверке доступности.
        ComFailureKind.InstanceFailed => string.Format(
            LocalizationManager.T("Com.ProgIdInstanceFailedFormat"), result.Detail ?? string.Empty),
        ComFailureKind.NoConnection => string.Format(
            LocalizationManager.T("Com.ProgIdNoConnectionFormat"), result.Detail ?? string.Empty),
        // Агент не разобрал запрос. Разряд заведён именно ради того, чтобы отличать этот
        // случай от молчания агента, поэтому и текст у него собственный. Подробности
        // у разряда нет по построению: разбор её не пропускает.
        ComFailureKind.BadRequest => LocalizationManager.T("Com.AgentBadRequest"),
        ComFailureKind.MetadataProperty => LocalizationManager.T("Com.MetadataPropertyFailed"),
        ComFailureKind.MetadataRead => LocalizationManager.T("Com.MetadataReadFailed"),
        // Единственный разряд, где подробность — свободный текст от 1С. Показываем его,
        // только если в нём нет пароля; иначе остаётся опознавательный код.
        ComFailureKind.DatabaseError => DescribeDatabaseError(result, hasSecret),
        // Сбой обмена: подробность (текст исключения канала) полезнее общей фразы.
        ComFailureKind.Transport => string.IsNullOrWhiteSpace(result.Detail)
            ? LocalizationManager.T("Com.AgentNoResult")
            : result.Detail,
        _ => LocalizationManager.T("Com.AgentNoResult")
    };

    /// <summary>
    /// Решает, показывать ли свободный текст ошибки 1С.
    /// <para>
    /// Решение принимается по тому, что положено в строку подключения, а не по содержимому
    /// текста. Искать пароль в тексте бесполезно: строка не экранирована, сообщение может
    /// прийти усечённым посреди значения, и проверка на начало пароля пропускает почти любой
    /// процитированный фрагмент, а проверка на короткий пароль глушит диагностику без повода.
    /// Здесь работает простое правило: пароля в строке нет — значит 1С его не видела
    /// и процитировать не могла.
    /// </para>
    /// </summary>
    internal static string DescribeDatabaseError(ComReadResult result, bool hasSecret)
    {
        var code = string.IsNullOrWhiteSpace(result.Code)
            ? LocalizationManager.T("Com.UnknownCode")
            : result.Code;

        if (hasSecret)
            return string.Format(LocalizationManager.T("Com.DbErrorHiddenFormat"), code);

        return string.IsNullOrWhiteSpace(result.Detail)
            ? string.Format(LocalizationManager.T("Com.DbErrorCodeOnlyFormat"), code)
            : result.Detail;
    }

    /// <summary>
    /// Принимает значение настройки «имя COM-коннектора» напрямую (issue #175).
    /// Вызывается там, где значение прочитано из настроек или изменено пользователем:
    /// при загрузке настроек профиля и из сеттера настройки. Значение кладётся в кэш,
    /// а не сбрасывается: обратное чтение settings.json оставляло бы промежуток между
    /// правкой и записью файла, в который фоновое COM-чтение успевало закэшировать
    /// прежнее значение — и сброса больше не было бы. Явное заполнение вместо ленивого
    /// делает поведение однозначным: иначе результат зависел бы от того, случилось ли
    /// COM-чтение до смены профиля. Прежние вердикты о недоступности COM снимаются:
    /// они получены для другого набора имён (<see cref="ResetComVerdicts"/>).
    /// </summary>
    public static void ApplyTemplate(string? template)
    {
        lock (TemplateLock)
        {
            _templateCache = template ?? string.Empty;
            _templateLoaded = true;
        }

        ResetComVerdicts();
    }

    /// <summary>
    /// Сбрасывает оба вердикта о недоступности COM: кэш реестра этого класса и сессионную
    /// защёлку процесса-агента. Их два, и снимать надо оба — иначе после установки платформы
    /// команда обновления по-прежнему молча откажет по устаревшему кэшу.
    /// </summary>
    public static void ResetComVerdicts()
    {
        ResetAvailabilityCache();
        ComReadHost.ResetAvailability();
    }

    /// <summary>
    /// Устанавливает подключение в текущем (STA) потоке, перебирая список ProgID.
    /// При успехе возвращает владеющее COM-объектами соединение.
    /// </summary>
    private OneCComConnection? ConnectCore(Infobase infobase, IReadOnlyList<string> progIds)
    {
        var connectString = BuildComConnectString(infobase);
        if (string.IsNullOrWhiteSpace(connectString))
        {
            LastError = LocalizationManager.T("Com.ConnStringBuildFailed");
            return null;
        }

        // true, если хотя бы один COM-тип 1С удалось получить (зарегистрирован).
        var anyRegistered = false;

        foreach (var progId in progIds)
        {
            Type? type;
            try
            {
                type = Type.GetTypeFromProgID(progId);
            }
            catch (Exception ex)
            {
                LastError = string.Format(LocalizationManager.T("Com.ProgIdTypeFailedFormat"), progId, ex.Message);
                continue;
            }

            if (type is null)
            {
                LastError = string.Format(LocalizationManager.T("Com.ProgIdNotRegisteredFormat"), progId);
                continue;
            }

            anyRegistered = true;

            object? connector = null;
            object? connection = null;
            try
            {
                connector = Activator.CreateInstance(type);
                if (connector is null)
                {
                    LastError = string.Format(LocalizationManager.T("Com.ProgIdInstanceFailedFormat"), progId);
                    continue;
                }

                connection = type.InvokeMember(
                    "Connect",
                    BindingFlags.InvokeMethod,
                    null,
                    connector,
                    new object[] { connectString });

                if (connection is null)
                {
                    LastError = string.Format(LocalizationManager.T("Com.ProgIdNoConnectionFormat"), progId);
                    TryComRelease(connector);
                    continue;
                }

                // Соединение установлено — оборачиваем и передаём владение вызывающему.
                LastError = null;
                return new OneCComConnection(connector, connection, progId, connectString);
            }
            catch (Exception ex)
            {
                // Ни строки подключения, ни текста ошибки 1С здесь не выпускаем — только
                // опознавательные сведения об исключении. Текст 1С умеет процитировать строку
                // подключения целиком, а строка не экранирована, поэтому маскировка на ней
                // принципиально ненадёжна: пароль с кавычкой или разделителем пробивает любое
                // правило разбора. Исключение нельзя и просто передать логгеру — тот дописывает
                // Message как есть.
                var reason = ex.GetType().Name + (ex.HResult == 0
                    ? string.Empty
                    : " 0x" + ex.HResult.ToString("X8", CultureInfo.InvariantCulture));
                LastError = string.Format(LocalizationManager.T("Com.ConnectFailedFormat"), progId, reason);
                _logger.Error($"COM-подключение через {progId} не удалось: {reason}");
                // Пробуем следующий ProgID.
                TryComRelease(connection);
                TryComRelease(connector);
            }
        }

        // Ни один COM-коннектор 1С не зарегистрирован — понятное итоговое сообщение.
        if (!anyRegistered)
        {
            LastError = LocalizationManager.T("Com.NotFound") + " " +
                        DescribeProgIdStatus() + " " +
                        LocalizationManager.T("Com.NotFoundInstallHint");
            _logger.Warn($"COM-коннектор 1С не зарегистрирован в системе для базы «{infobase.Name}». {DescribeProgIdStatus()}");
        }

        return null;
    }

    /// <summary>
    /// Возвращает описание наличия ProgID COM-коннектора 1С в реестре (64-битное и 32-битное
    /// представления), чтобы отличить «платформа не установлена» от «несоответствие разрядности».
    /// Проверяется весь список известных ProgID: у платформы 8.5 он свой, и проверка по одному
    /// V83 объявляла бы установленную 8.5 отсутствующей.
    /// </summary>
    private static string DescribeProgIdStatus()
    {
        // Сравнивать надо один и тот же ProgID в обеих ветвях реестра. Проверка «есть хоть
        // какой-нибудь из списка» ломает единственное, ради чего эта функция существует:
        // при 64-разрядной 8.5 и 32-разрядной 8.3 она отвечала бы «зарегистрирован и там,
        // и там», хотя ни один коннектор в обеих ветвях не присутствует, и подсказка
        // о несоответствии разрядности не появилась бы.
        var in64 = false;
        var in32 = false;
        foreach (var progId in KnownProgIds)
        {
            var here64 = IsProgIdRegistered(progId, RegistryView.Registry64);
            var here32 = IsProgIdRegistered(progId, RegistryView.Registry32);

            if (here64 && here32)
            {
                in64 = in32 = true;
                break;
            }

            in64 |= here64;
            in32 |= here32;
        }

        if (in64 && in32)
            return LocalizationManager.T("Com.ProgIdStatusBothVisible");
        if (in64)
            return LocalizationManager.T("Com.ProgIdStatusOnly64");
        if (in32)
            return LocalizationManager.T("Com.ProgIdStatusOnly32");
        return LocalizationManager.T("Com.ProgIdStatusAbsent");
    }

    private static bool IsProgIdRegistered(string progId, RegistryView view)
    {
        try
        {
            using var classesRoot = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, view);
            using var progIdKey = classesRoot?.OpenSubKey(progId);
            var clsid = progIdKey?.GetValue("CLSID")?.ToString();
            if (string.IsNullOrWhiteSpace(clsid)) return false;

            using var clsidKey = classesRoot?.OpenSubKey($"CLSID\\{clsid}");
            return clsidKey is not null;
        }
        catch
        {
            return false;
        }
    }

    private static void TryComRelease(object? com)
    {
        if (com is null) return;
        try
        {
            if (Marshal.IsComObject(com))
                Marshal.FinalReleaseComObject(com);
        }
        catch
        {
            // Объект мог быть уже освобождён.
        }
    }

    /// <summary>
    /// Имена параметров, значение которых нельзя показывать. Имя пользователя (Usr)
    /// намеренно не маскируется: это логин, а не секрет, и он нужен для разбора отказов
    /// аутентификации. Так же вела себя и прежняя реализация.
    /// </summary>
    private static readonly string[] SecretParameters = { "Pwd", "Password" };

    /// <summary>
    /// Скрывает пароль перед записью в журнал или показом пользователю.
    /// <para>
    /// Применяется не только к самой строке подключения, но и к произвольному тексту:
    /// сообщения об ошибках, приходящие от 1С, умеют цитировать строку подключения целиком.
    /// </para>
    /// <para>
    /// Значение считается идущим до ближайшего <c>;</c> или конца строки — это грамматика
    /// строки подключения. Разбирать баланс кавычек нельзя: <see cref="BuildComConnectString"/>
    /// кавычку внутри пароля не экранирует, поэтому маскировка по кавычкам пропускала хвост
    /// пароля наружу. Правило по разделителю устойчиво ко всем формам записи — в кавычках,
    /// без кавычек, с удвоенными кавычками, с кавычкой внутри значения и с незакрытой кавычкой —
    /// и при этом сохраняет диагностику, идущую после разделителя.
    /// </para>
    /// </summary>
    internal static string MaskCredentials(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var sb = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var name = MatchSecretParameter(text, i);
            if (name is null)
            {
                sb.Append(text[i]);
                i++;
                continue;
            }

            var afterEq = SkipSpaces(text, i + name.Length) + 1; // за знаком равенства
            var quoteAt = SkipSpaces(text, afterEq);
            int end;

            if (quoteAt < text.Length && text[quoteAt] == '"')
            {
                // Закавыченное значение. Берём первую кавычку, за которой идёт разделитель
                // или конец строки, и за пределы строки не выходим.
                // Правило намеренно узкое. Оно не покрывает пароль, внутри которого стоит
                // «";», — но такой текст сюда и не попадает: при наличии пароля агент
                // свободный текст ошибки не отдаёт вовсе. Прежнее широкое правило «до
                // последней кавычки во всём остатке» закрывало этот случай, зато съедало
                // диагностику: из многострочного сообщения 1С исчезали строки с настоящей
                // причиной сбоя.
                var close = FindClosingQuote(text, quoteAt + 1);
                end = close >= 0 ? close + 1 : FindLineEnd(text, quoteAt);
            }
            else
            {
                // Незакавыченную форму не трогаем. Пароль в строку подключения всегда кладётся
                // в кавычках, поэтому здесь маскировать нечего, а вреда достаточно: путь вида
                // «D:\Базы\Pwd=1\buh» — допустимое имя каталога, и правило «до пробела или
                // разделителя» уничтожало бы хвост пути вместе с диагностикой у базы, у которой
                // пароля нет вовсе.
                sb.Append(text[i]);
                i++;
                continue;
            }

            sb.Append(name).Append("=***");
            i = end;
        }

        return sb.ToString();
    }

    /// <summary>
    /// Возвращает имя секретного параметра, если в позиции <paramref name="i"/> начинается
    /// именно он и за ним следует присваивание. Проверяются обе границы слова, иначе
    /// под маскировку попадали бы посторонние параметры вроде <c>NotPwd</c> и <c>PwdHint</c>.
    /// </summary>
    private static string? MatchSecretParameter(string text, int i)
    {
        if (i > 0)
        {
            var prev = text[i - 1];
            if (char.IsLetterOrDigit(prev) || prev == '_')
                return null;
        }

        foreach (var name in SecretParameters)
        {
            if (i + name.Length > text.Length)
                continue;
            if (string.Compare(text, i, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) != 0)
                continue;

            var k = SkipSpaces(text, i + name.Length);
            if (k < text.Length && text[k] == '=')
                return text.Substring(i, name.Length);
        }

        return null;
    }

    private static int SkipSpaces(string text, int i)
    {
        while (i < text.Length && (text[i] == ' ' || text[i] == '\t'))
            i++;
        return i;
    }

    /// <summary>
    /// Первая кавычка в пределах строки, за которой значение заканчивается.
    /// −1, если такой кавычки в строке нет (значение не закрыто).
    /// </summary>
    private static int FindClosingQuote(string text, int from)
    {
        for (var i = from; i < text.Length; i++)
        {
            if (text[i] == '\r' || text[i] == '\n')
                return -1;
            if (text[i] != '"')
                continue;

            // Закрывающей считаем кавычку перед любым несловообразующим символом, а не только
            // перед «;». Прежнее узкое условие теряло остаток строки, если после значения шла
            // запятая или пробел: «Pwd="p", причина: …» превращалось в «Pwd=***».
            if (i + 1 >= text.Length || !char.IsLetterOrDigit(text[i + 1]))
                return i;
        }
        return -1;
    }

    /// <summary>Конец текущей строки или конец текста.</summary>
    private static int FindLineEnd(string text, int from)
    {
        for (var i = from; i < text.Length; i++)
            if (text[i] == '\r' || text[i] == '\n')
                return i;
        return text.Length;
    }

    /// <summary>
    /// Строка подключения для COM: File=...; или Srvr=...;Ref=...; (+ Usr/Pwd при наличии).
    /// Для веб-публикации COM-подключение обычно неприменимо.
    /// </summary>
    private static string BuildComConnectString(Infobase infobase)
        => BuildComConnectString(infobase, out _);

    /// <summary>
    /// То же, но дополнительно сообщает, попал ли в строку пароль. Признак отдаёт именно
    /// сборщик: он единственный знает это точно, тогда как обратный разбор готовой строки
    /// зависит от списка имён секретных параметров и от экранирования, которого здесь нет.
    /// </summary>
    internal static string BuildComConnectString(Infobase infobase, out bool hasSecret)
    {
        hasSecret = false;

        var c = infobase.Connection;
        if (c is null) return string.Empty;

        var sb = new StringBuilder();
        switch (c.Type)
        {
            case ConnectionType.File:
                if (string.IsNullOrWhiteSpace(c.FilePath)) return string.Empty;
                AppendParameter(sb, "File", c.FilePath.Trim().TrimEnd('\\', '/'));
                break;

            case ConnectionType.ClientServer:
                if (string.IsNullOrWhiteSpace(c.Server) || string.IsNullOrWhiteSpace(c.DatabaseName))
                    return string.Empty;
                AppendParameter(sb, "Srvr", c.GetServerWithPort());
                AppendParameter(sb, "Ref", c.DatabaseName);
                break;

            default:
                // Веб — COM обычно не подходит.
                return string.Empty;
        }

        if (c.AuthenticationMode == AuthenticationMode.Credentials
            && !string.IsNullOrWhiteSpace(c.User))
        {
            AppendParameter(sb, "Usr", c.User);
            if (!string.IsNullOrWhiteSpace(c.Password))
            {
                AppendParameter(sb, "Pwd", c.Password);
                hasSecret = true;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Добавляет параметр строки подключения, экранируя значение.
    /// <para>
    /// Кавычка внутри значения удваивается — это правило самой грамматики строки подключения 1С.
    /// Без экранирования значение может закрыть себя и дописать любой другой параметр: путь вида
    /// <c>C:\Base";Pwd=…;x="</c> добавлял в строку пароль, о котором вызывающий не знал. От этого
    /// ломались сразу две вещи: подключение к базе, в пароле которой есть кавычка, и признак
    /// наличия пароля, на котором держится решение о показе текста ошибки 1С.
    /// </para>
    /// </summary>
    private static void AppendParameter(StringBuilder sb, string name, string value)
    {
        sb.Append(name).Append("=\"").Append(value.Replace("\"", "\"\"")).Append("\";");
    }

    /// <summary>
    /// Имя базы для сообщений об ошибках (issue #174): вместо пустого имени «» подставляет
    /// осмысленное значение — заданное наименование, затем Ref (DatabaseName), затем путь
    /// файловой базы, а иначе явный маркер. Закрывает случай, когда имя не заполнено у баз
    /// списка (например, после импорта) и диагностика по журналу теряет привязку к базе.
    /// </summary>
    private static string DisplayName(Infobase ib)
    {
        if (ib is null) return "<без имени>";
        if (!string.IsNullOrWhiteSpace(ib.Name))
            return ib.Name;
        var conn = ib.Connection;
        if (conn is not null && !string.IsNullOrWhiteSpace(conn.DatabaseName))
            return conn.DatabaseName;
        if (conn is not null && !string.IsNullOrWhiteSpace(conn.FilePath))
            return System.IO.Path.GetFileName(conn.FilePath.Trim().Trim('"').TrimEnd('\\', '/'));
        return "<без имени>";
    }
}