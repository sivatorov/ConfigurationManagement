using System;
using System.Collections.Generic;

namespace Configuration_Management.Models;

/// <summary>
/// Кластер серверов 1С:Предприятие — строка вывода команды rac «cluster list».
/// Поля соответствуют колонкам вывода (формат документирован на ИТС):
/// <c>cluster</c>, <c>name</c>, <c>port</c>. Лишние колонки справа игнорируются парсером.
/// </summary>
public sealed class RacCluster
{
    /// <summary>Идентификатор кластера (колонка «cluster»).</summary>
    public Guid Id { get; set; }

    /// <summary>Имя кластера (колонка «name», может содержать пробелы и кириллицу).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Порт кластера (колонка «port»).</summary>
    public int Port { get; set; }

    /// <summary>Имя компьютера кластера (ключ «host» в блоке key-value; issue #324:
    /// используется как запасной подпись в списке кластеров, когда rac не отдал name).</summary>
    public string Host { get; set; } = string.Empty;
}

/// <summary>
/// Рабочий процесс сервера 1С (rphost/rmngr) — строка вывода команды rac «process list».
/// Память — в байтах, длительности/таймауты — в секундах (как отдаёт rac).
/// </summary>
public sealed class RacProcessInfo
{
    /// <summary>Идентификатор процесса (колонка «process»).</summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Тип процесса (rphost/rmngr). В выводе «process list» колонки типа нет —
    /// заполняется из других источников (например «process info»), по умолчанию пусто.
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Имя компьютера, на котором работает процесс (колонка «host»).</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>PID процесса ОС (колонка «pid»).</summary>
    public int Pid { get; set; }

    /// <summary>Порт процесса (колонка «port»).</summary>
    public int Port { get; set; }

    /// <summary>Время старта процесса (колонка «started-at»).</summary>
    public DateTime StartedAt { get; set; }

    /// <summary>Текущий объём памяти процесса, байт (колонка «memory-size»).</summary>
    public long MemorySize { get; set; }

    /// <summary>Объём памяти, доступный для выделения процессу, байт (колонка «memory-total»).</summary>
    public long MemoryTotal { get; set; }

    /// <summary>Объём свободной памяти, доступный процессу, байт (колонка «memory-available»).</summary>
    public long MemoryAvailable { get; set; }

    /// <summary>Превышение допустимого объёма памяти процесса, байт (колонка «memory-excess»).</summary>
    public long MemoryExcess { get; set; }

    /// <summary>Количество потоков процесса (колонка «threads»).</summary>
    public int Threads { get; set; }

    /// <summary>Загрузка CPU процесса, % (колонка «cpu»).</summary>
    public double Cpu { get; set; }

    /// <summary>Доступная производительность, % (колонка «available-performances»).</summary>
    public double AvailablePerformances { get; set; }

    /// <summary>Признак запущенного процесса (колонка «running», «0»/«1»).</summary>
    public bool Running { get; set; }

    /// <summary>Количество информационных баз, обслуживаемых процессом (колонка «infobases»).</summary>
    public int Infobases { get; set; }
}

/// <summary>
/// Сеанс пользователя сервера 1С — строка вывода команды rac «session list».
/// Длительности — в миллисекундах, память и объёмы — в байтах (как отдаёт rac).
/// </summary>
public sealed class RacSessionInfo
{
    /// <summary>Идентификатор сеанса (колонка «session»).</summary>
    public Guid Id { get; set; }

    /// <summary>Идентификатор информационной базы сеанса (колонка «infobase»); null, если база не указана.</summary>
    public Guid? InfobaseId { get; set; }

    /// <summary>Имя пользователя сеанса (колонка «user-name», может содержать пробелы и кириллицу).</summary>
    public string User { get; set; } = string.Empty;

    /// <summary>Имя компьютера клиента (колонка «host»).</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>Идентификатор клиентского приложения (колонка «app-id», например «1CV8»).</summary>
    public string AppId { get; set; } = string.Empty;

    /// <summary>Время старта сеанса (колонка «started-at»).</summary>
    public DateTime StartedAt { get; set; }

    /// <summary>Время последней активности сеанса (колонка «last-active-at»).</summary>
    public DateTime LastActiveAt { get; set; }

    /// <summary>Заблокирован ли сеанс служебными заданиями (колонка «blocked-by-ls», «0»/«1»).</summary>
    public bool BlockedByLs { get; set; }

    /// <summary>Заблокирован ли сеанс взаимоблокировкой (колонка «blocked-by-deadlock», «0»/«1»).</summary>
    public bool BlockedByDeadlock { get; set; }

    /// <summary>Длительность работы процесса СУБД для сеанса, мс (колонка «db-proc-duration»).</summary>
    public long DbProcDuration { get; set; }

    /// <summary>Общая длительность сеанса, мс (колонка «duration-all»).</summary>
    public long DurationAll { get; set; }

    /// <summary>Длительность текущего вызова, мс (колонка «duration-current»).</summary>
    public long DurationCurrent { get; set; }

    /// <summary>Длительность работы СУБД, мс (колонка «duration-dbms»).</summary>
    public long DurationDbms { get; set; }

    /// <summary>Длительность работы CPU, мс (колонка «duration-cpu»).</summary>
    public long DurationCpu { get; set; }

    /// <summary>Длительность ожидания, мс (колонка «duration-wait»).</summary>
    public long DurationWait { get; set; }

    /// <summary>Объём памяти сеанса, байт (колонка «memory»).</summary>
    public long Memory { get; set; }

    /// <summary>Объём данных, переданных сеансу, байт (колонка «bytes»).</summary>
    public long Bytes { get; set; }

    /// <summary>Позиция последнего прочитанного фрагмента данных (колонка «position»).</summary>
    public long Position { get; set; }

    /// <summary>Объём прочитанных данных, байт (колонка «read»).</summary>
    public long Read { get; set; }

    /// <summary>Объём записанных данных, байт (колонка «write»).</summary>
    public long Write { get; set; }

    /// <summary>Идентификатор соединения сеанса (колонка «connection»).</summary>
    public Guid ConnectionId { get; set; }

    /// <summary>Признак спящего (hibernate) сеанса (колонка «hibernate», «0»/«1»).</summary>
    public bool Hibernate { get; set; }

    /// <summary>Состояние сеанса (колонка «state», например «active», «sleep», «dead»).</summary>
    public string State { get; set; } = string.Empty;
}

/// <summary>
/// Соединение клиента с сервером 1С — строка вывода команды rac «connection list».
/// </summary>
public sealed class RacConnectionInfo
{
    /// <summary>Идентификатор соединения (колонка «connection»).</summary>
    public Guid Id { get; set; }

    /// <summary>Идентификатор сеанса соединения (колонка «session»).</summary>
    public Guid SessionId { get; set; }

    /// <summary>Признак блокировки соединения (колонка «blocked», «0»/«1»).</summary>
    public bool Blocked { get; set; }

    /// <summary>Тип коннектора (колонка «connector», например «1CV8»).</summary>
    public string Connector { get; set; } = string.Empty;

    /// <summary>Идентификатор рабочего процесса, обслуживающего соединение (колонка «process»).</summary>
    public Guid ProcessId { get; set; }

    /// <summary>Имя компьютера клиента (колонка «host»).</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>Порт клиента (колонка «port»).</summary>
    public int Port { get; set; }

    /// <summary>Время установления соединения (колонка «established-at»).</summary>
    public DateTime EstablishedAt { get; set; }

    /// <summary>Время последнего обращения по соединению (колонка «last-connection-time»).</summary>
    public DateTime LastConnectionTime { get; set; }

    /// <summary>Длительность соединения, мс (колонка «duration»).</summary>
    public long Duration { get; set; }

    /// <summary>
    /// Описание соединения (колонка «descr» в новом формате вывода rac «ключ : значение»,
    /// issue #324). В табличном выводе старых версий колонки может не быть — пусто.
    /// </summary>
    public string Descr { get; set; } = string.Empty;
}

/// <summary>
/// Блокировка объекта данных — строка вывода команды rac «lock list».
/// </summary>
public sealed class RacLockInfo
{
    /// <summary>Идентификатор блокировки (колонка «lock»).</summary>
    public Guid Id { get; set; }

    /// <summary>Идентификатор сеанса, удерживающего блокировку (колонка «session»).</summary>
    public Guid SessionId { get; set; }

    /// <summary>Идентификатор информационной базы (колонка «infobase»).</summary>
    public Guid InfobaseId { get; set; }

    /// <summary>Идентификатор соединения, удерживающего блокировку (колонка «connection»).</summary>
    public Guid ConnectionId { get; set; }

    /// <summary>Идентификатор транзакции, в которой удерживается блокировка (колонка «transaction»).</summary>
    public Guid TransactionId { get; set; }

    /// <summary>Ожидает ли блокировку другой сеанс (колонка «waiting», «0»/«1»).</summary>
    public bool Waiting { get; set; }

    /// <summary>Блокирует ли объект другой сеанс (колонка «blocking», «0»/«1»).</summary>
    public bool Blocking { get; set; }

    /// <summary>Описание объекта блокировки (колонка «object», например ссылка на объект метаданных).</summary>
    public string Object { get; set; } = string.Empty;
}

/// <summary>
/// Информация о кластере — вывод команды rac «cluster info» (формат «ключ: значение»).
/// Хранит полный словарь свойств кластера (для отображения «как есть») и типизированные
/// частые поля для использования в UI.
/// </summary>
public sealed class RacClusterInfo
{
    /// <summary>Все свойства кластера из вывода rac: ключ — имя свойства (без обрезки), значение — строка.</summary>
    public IReadOnlyDictionary<string, string> Properties { get; set; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Имя кластера (свойство «name»).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Имя компьютера-сервера (свойство «hostName»).</summary>
    public string HostName { get; set; } = string.Empty;

    /// <summary>Порт кластера (свойство «port»).</summary>
    public int Port { get; set; }

    /// <summary>Таймаут снятия блокировок (свойство «expirationTimeout»).</summary>
    public long ExpirationTimeout { get; set; }

    /// <summary>Лимит времени жизни сеансов (свойство «lifetimeLimit»).</summary>
    public long LifetimeLimit { get; set; }

    /// <summary>Максимальный объём памяти рабочего процесса, байт (свойство «maxMemorySize»).</summary>
    public long MaxMemorySize { get; set; }

    /// <summary>Интервал контроля превышения памяти (свойство «maxMemoryTimeLimit»).</summary>
    public long MaxMemoryTimeLimit { get; set; }

    /// <summary>Уровень безопасности кластера (свойство «securityLevel»).</summary>
    public int SecurityLevel { get; set; }

    /// <summary>Таймаут бездействия сеанса (свойство «sessionIdleTimeout»).</summary>
    public long SessionIdleTimeout { get; set; }

    /// <summary>Максимальный объём памяти сеанса, байт (свойство «sessionMaxMemorySize»).</summary>
    public long SessionMaxMemorySize { get; set; }

    /// <summary>Максимальная длительность существования сеанса (свойство «sessionMaxTimeLimit»).</summary>
    public long SessionMaxTimeLimit { get; set; }
}

/// <summary>
/// Информационная база кластера серверов 1С:Предприятие — строка вывода команды rac
/// «infobase summary list». Поля соответствуют колонкам вывода (формат документирован на ИТС):
/// <c>infobase</c>, <c>name</c>, <c>descr</c>, <c>dbms</c>, <c>db-server</c>, <c>db-name</c>,
/// <c>db-user</c>, <c>locale</c>, <c>security-level</c>, <c>licensed</c>. Лишние колонки справа
/// игнорируются парсером, отсутствующие — принимают значения по умолчанию.
/// </summary>
public sealed class RacInfobaseSummary
{
    /// <summary>Идентификатор информационной базы в кластере (колонка «infobase»).</summary>
    public Guid InfobaseId { get; set; }

    /// <summary>Имя информационной базы (колонка «name») — значение Ref строки подключения.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Описание информационной базы (колонка «descr», может содержать пробелы и кириллицу).</summary>
    public string Descr { get; set; } = string.Empty;

    /// <summary>Тип СУБД (колонка «dbms»); пусто для файловой информационной базы кластера.</summary>
    public string Dbms { get; set; } = string.Empty;

    /// <summary>Имя сервера СУБД (колонка «db-server»).</summary>
    public string DbServer { get; set; } = string.Empty;

    /// <summary>Имя базы данных в СУБД (колонка «db-name»).</summary>
    public string DbName { get; set; } = string.Empty;

    /// <summary>Пользователь СУБД (колонка «db-user»).</summary>
    public string DbUser { get; set; } = string.Empty;

    /// <summary>Локаль информационной базы (колонка «locale»).</summary>
    public string Locale { get; set; } = string.Empty;

    /// <summary>Уровень безопасности информационной базы (колонка «security-level»).</summary>
    public int SecurityLevel { get; set; }

    /// <summary>Признак лицензированной информационной базы (колонка «licensed», «0»/«1»).</summary>
    public bool Licensed { get; set; }
}

/// <summary>
/// Операция управления состоянием регламентного задания кластера (команды rac
/// «job pause / resume / disable / enable»).
/// </summary>
public enum RacJobAction
{
    /// <summary>Приостановить выполнение задания («job pause»).</summary>
    Pause,

    /// <summary>Возобновить выполнение задания («job resume»).</summary>
    Resume,

    /// <summary>Снять задание с расписания («job disable»).</summary>
    Disable,

    /// <summary>Вернуть задание на расписание («job enable»).</summary>
    Enable
}

/// <summary>
/// Регламентное задание кластера серверов 1С:Предприятие — строка вывода команды rac
/// «job list». Поля соответствуют колонкам вывода (формат документирован на ИТС;
/// состав колонок может отличаться между версиями платформы): <c>cluster</c>, <c>job</c>,
/// <c>infobase</c>, <c>name</c>, <c>method-name</c>, <c>predefined</c>, <c>schedule</c>,
/// <c>state</c>, <c>started-at</c>, <c>next-start</c>, <c>last-start</c>, <c>last-end</c>,
/// <c>last-success</c>, <c>last-error</c>, <c>last-error-descr</c>, <c>process</c>, …,
/// <c>result</c>. Лишние колонки справа игнорируются парсером, отсутствующие —
/// принимают значения по умолчанию.
/// </summary>
public sealed class RacJobInfo
{
    /// <summary>Идентификатор задания (колонка «job»).</summary>
    public Guid Id { get; set; }

    /// <summary>Идентификатор информационной базы-владельца (колонка «infobase»); null — задание без ИБ.</summary>
    public Guid? InfobaseId { get; set; }

    /// <summary>Имя задания (колонка «name», может содержать пробелы и кириллицу).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Имя метода, выполняемого заданием (колонка «method-name»).</summary>
    public string MethodName { get; set; } = string.Empty;

    /// <summary>Признак предопределённого задания (колонка «predefined», «0»/«1»).</summary>
    public bool Predefined { get; set; }

    /// <summary>Расписание задания в виде cron-подобной строки (колонка «schedule»).</summary>
    public string Schedule { get; set; } = string.Empty;

    /// <summary>
    /// Состояние задания (колонка «state»): «running» (выполняется), «scheduled»
    /// (запланировано), «paused» (приостановлено), «disabled» (снято с расписания),
    /// «interrupted» (прервано).
    /// </summary>
    public string State { get; set; } = string.Empty;

    /// <summary>Время фактического старта текущего выполнения (колонка «started-at»).</summary>
    public DateTime StartedAt { get; set; }

    /// <summary>Ближайшее время запуска по расписанию (колонка «next-start»).</summary>
    public DateTime NextStart { get; set; }

    /// <summary>Время последнего запуска (колонка «last-start»).</summary>
    public DateTime LastStart { get; set; }

    /// <summary>Время окончания последнего запуска (колонка «last-end»).</summary>
    public DateTime LastEnd { get; set; }

    /// <summary>Признак успешности последнего запуска (колонка «last-success», «0»/«1»).</summary>
    public bool LastSuccess { get; set; }

    /// <summary>Признак ошибки последнего запуска (колонка «last-error», «0»/«1»).</summary>
    public bool LastError { get; set; }

    /// <summary>Описание ошибки последнего запуска (колонка «last-error-descr»).</summary>
    public string LastErrorDescr { get; set; } = string.Empty;

    /// <summary>Идентификатор рабочего процесса, обслуживающего задание (колонка «process»).</summary>
    public Guid ProcessId { get; set; }

    /// <summary>Результат последнего запуска (колонка «result», текст).</summary>
    public string Result { get; set; } = string.Empty;
}