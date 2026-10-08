using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Configuration_Management.Models;

namespace Configuration_Management.Services;

/// <summary>
/// ЧИСТЫЙ статический парсер текстового вывода утилиты rac (Remote Administration Client,
/// поставляется с платформой 1С:Предприятие). Без I/O и UI — только строки.
/// Форматы вывода rac документированы на ИТС:
/// <list type="bullet">
/// <item>list-команды (cluster/process/session/connection/lock list) — таблица: первая строка —
/// заголовок колонок, поля строк разделены табуляцией <c>\t</c> (значения могут содержать пробелы);</item>
/// <item>info-команды (cluster info и т.п.) — строки «ключ: значение».</item>
/// </list>
/// Парсинг типизированных команд — позиционный по индексам колонок; лишние колонки справа
/// игнорируются. Строки с несовпадающим числом полей или невалидным идентификатором
/// пропускаются без исключения.
/// </summary>
public static class RacOutputParser
{
    /// <summary>
    /// Разделитель для fallback-разбора таблицы без табуляций: 2+ пробела подряд.
    /// Одиночный пробел внутри значения (имя кластера, строка подключения) остаётся
    /// частью поля (issue #324).
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex MultiSpaceSeparator =
        new(@" {2,}", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Разбивает табличный вывод rac на строки и поля (разделитель — табуляция).
    /// Fallback: если в строке нет ни одной табуляции, поля разделяются 2+ пробелами
    /// (вывод с выравниванием пробелами в некоторых окружениях/версиях — issue #324).
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> ParseTable(string output)
    {
        var rows = new List<IReadOnlyList<string>>();
        if (string.IsNullOrEmpty(output))
            return rows;

        var lines = SplitLines(output);
        if (lines.Count == 0)
            return rows;

        // Вывод, выровненный пробелами (некоторые версии/окружения rac не используют
        // табуляции; issue #324): позиции начала колонок берём из строки заголовка.
        // Позиционный разбор применяем ТОЛЬКО при единообразном выравнивании — когда
        // границы колонок в заголовке совпадают с границами в первой строке данных
        // (иначе индексы из заголовка резали бы значения, шире заголовка, и мы бы
        // сломали вывод с неравномерными отступами — см. существующие образцы тестов).
        IReadOnlyList<int>? columnStarts = null;
        if (lines[0].IndexOf('\t') < 0 && lines.Count > 1)
        {
            var headerStarts = TryGetColumnStarts(lines[0]);
            // Позиционный разбор корректен, если выравнивание ЕДИНООБРАЗНО: каждая
            // граница колонки из заголовка в строках данных указывает на начало
            // непробельного блока. Внутренние пробелы значения (имя кластера) дают
            // свои «переходы», но они не совпадают с границами заголовка — их
            // наличие не отключает позиционный разбор.
            if (headerStarts is not null &&
                headerStarts.Count > 1 &&
                StartsAlignWithRows(lines, headerStarts))
                columnStarts = headerStarts;
        }

        foreach (var line in lines)
        {
            IReadOnlyList<string> fields;
            if (line.IndexOf('\t') >= 0)
            {
                fields = line.Split('\t');
            }
            else if (columnStarts is not null && columnStarts.Count > 1)
            {
                fields = SplitByColumnStarts(line, columnStarts);
            }
            else
            {
                fields = MultiSpaceSeparator.Split(line.Trim());
            }
            rows.Add(fields);
        }

        return rows;
    }

    /// <summary>Делит вывод на непустые строки, убирая «\r» (CRLF из cmd/Windows).</summary>
    private static List<string> SplitLines(string output)
    {
        var lines = new List<string>();
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (!string.IsNullOrWhiteSpace(line))
                lines.Add(line);
        }
        return lines;
    }

    /// <summary>
    /// Определяет позиции начала колонок в строке заголовка: колонка начинается там,
    /// где последовательность пробелов сменяется непробельным символом (первая колонка —
    /// в начале строки). Возвращает null, если колонок меньше двух (заголовок из одного
    /// слова — позиционный разбор бессмыслен, оставляем fallback по 2+ пробелам).
    /// </summary>
    private static IReadOnlyList<int>? TryGetColumnStarts(string header)
    {
        var starts = new List<int>();
        var prevWasSpace = true;
        for (var i = 0; i < header.Length; i++)
        {
            var isSpace = header[i] == ' ' || header[i] == '\t';
            if (!isSpace && prevWasSpace)
                starts.Add(i);
            prevWasSpace = isSpace;
        }
        return starts.Count > 1 ? starts : null;
    }

    /// <summary>
    /// Проверяет единообразность выравнивания: во всех строках данных (кроме заголовка)
    /// каждая граница колонки из заголовка (кроме последней) указывает на начало
    /// непробельного блока. Если выравнивание неравномерное (границы из заголовка
    /// попадают внутрь пробельного «хвоста» или за конец строки) — позиционный разбор
    /// не применяется, остаётся fallback «2+ пробела».
    /// </summary>
    private static bool StartsAlignWithRows(List<string> lines, IReadOnlyList<int> starts)
    {
        for (var rowIndex = 1; rowIndex < lines.Count; rowIndex++)
        {
            var row = lines[rowIndex];
            for (var i = 1; i < starts.Count - 1; i++)
            {
                var pos = starts[i];
                if (pos >= row.Length || row[pos] == ' ' || row[pos] == '\t')
                    return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Разбивает строку данных по фиксированным позициям начала колонок (вычисленным
    /// из заголовка). Поля обрезаются; внутренние пробелы значения (имя кластера)
    /// сохраняются — в отличие от fallback «2+ пробела», который резал такие значения.
    /// </summary>
    private static IReadOnlyList<string> SplitByColumnStarts(string line, IReadOnlyList<int> starts)
    {
        var fields = new List<string>(starts.Count);
        for (var i = 0; i < starts.Count; i++)
        {
            var from = starts[i];
            var to = i + 1 < starts.Count ? starts[i + 1] : line.Length;
            if (from >= line.Length)
            {
                fields.Add(string.Empty);
                continue;
            }
            var end = Math.Min(to, line.Length);
            fields.Add(line.Substring(from, end - from).Trim());
        }
        return fields;
    }

    /// <summary>
    /// Разбирает вывод info-команды rac в словарь «ключ → значение».
    /// Ключ и значение обрезаются; разделитель — первый символ «:».
    /// Строки без разделителя и пустые строки пропускаются.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ParseInfo(string output)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(output))
            return result;

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var colon = line.IndexOf(':');
            if (colon <= 0)
                continue;

            var key = line.Substring(0, colon).Trim();
            if (key.Length == 0)
                continue;

            var value = line.Substring(colon + 1).Trim();
            result[key] = value;
        }

        return result;
    }

    /// <summary>
    /// Разбирает вывод «cluster list» в список кластеров.
    /// Поддерживаются два формата вывода (issue #324):
    /// <list type="bullet">
    /// <item>таблица: строка заголовка + строки данных, колонки cluster, name, port
    /// (разделитель \t или выравнивание пробелами; лишние колонки справа игнорируются);</item>
    /// <item>блоки «ключ : значение» (новые версии rac): каждый кластер — блок строк
    /// вида «cluster : GUID», «host : …», «port : …», «name : "…"».</item>
    /// </list>
    /// Если табличный разбор не дал ни одного кластера — пробуется формат key-value блоков.
    /// </summary>
    public static IReadOnlyList<RacCluster> ToClusters(string output)
    {
        const int minColumns = 3;
        var clusters = new List<RacCluster>();

        foreach (var row in ParseTable(output))
        {
            if (row.Count < minColumns)
                continue;

            var id = ParseGuid(row[0]);
            if (id == Guid.Empty)
                continue; // строка заголовка или мусор

            clusters.Add(new RacCluster
            {
                Id = id,
                Name = row[1].Trim(),
                Port = ParseInt(row[2])
            });
        }

        if (clusters.Count == 0)
        {
            // Формат новых версий rac: «cluster list» выводится блоками «ключ : значение»
            // вместо таблицы; блок начинается строкой «cluster : GUID» (issue #324).
            foreach (var block in TryParseKeyValueBlocks(output, "cluster", IsGuid))
            {
                var id = ParseGuid(Get(block, "cluster"));
                if (id == Guid.Empty)
                    continue; // блок без валидного идентификатора — пропускаем

                clusters.Add(new RacCluster
                {
                    Id = id,
                    // Имя кластера — по первому найденному ключу-алиасу: разные версии rac
                    // называют колонку по-разному (issue #324, «в поле ключ вместо имени»:
                    // у новых rac 8.5 возможны fullName/displayName вместо name).
                    Name = Unquote(GetAny(block, "name", "fullName", "displayName", "descr", "description")),
                    Port = ParseInt(Get(block, "port")),
                    // Хост кластера — для запасной подписи в списке, когда name пуст
                    // (issue #324: «ключ вместо имени» — пользователь должен видеть
                    // хотя бы осмысленный «host (порт)» вместо пустого плейсхолдера).
                    Host = Unquote(Get(block, "host"))
                });
            }
        }

        return clusters;
    }

    /// <summary>
    /// Разбирает вывод rac в блоки «ключ : значение» (формат новых версий rac для list-команд).
    /// Новый блок начинается со строки, где ключ равен <paramref name="blockStartKey"/> и значение
    /// проходит проверку <paramref name="blockStartValuePredicate"/> (например, является GUID).
    /// Внутри блока собираются пары «ключ → значение» (ключ обрезается; значения снимаются
    /// с кавычек — см. <see cref="Unquote"/>). Возвращает пустой список, если блоков не найдено.
    /// </summary>
    private static IReadOnlyList<IReadOnlyDictionary<string, string>> TryParseKeyValueBlocks(
        string output,
        string blockStartKey,
        Func<string, bool>? blockStartValuePredicate = null)
    {
        var blocks = new List<IReadOnlyDictionary<string, string>>();
        Dictionary<string, string>? current = null;

        foreach (var rawLine in (output ?? string.Empty).Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var colon = line.IndexOf(':');
            if (colon <= 0)
                continue;

            var key = line.Substring(0, colon).Trim();
            if (key.Length == 0)
                continue;

            var value = line.Substring(colon + 1).Trim();

            // Строка «blockStartKey : value» начинает новый блок (и завершает текущий).
            if (string.Equals(key, blockStartKey, StringComparison.OrdinalIgnoreCase) &&
                (blockStartValuePredicate is null || blockStartValuePredicate(value)))
            {
                if (current is not null)
                    blocks.Add(current);
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            if (current is null)
                continue;

            current[key] = value;
        }

        if (current is not null)
            blocks.Add(current);

        return blocks;
    }

    /// <summary>
    /// Разбирает вывод rac в блоки «ключ : значение» (формат НОВЫХ версий rac для list-команд,
    /// issue #324). Публичная обёртка над <see cref="TryParseKeyValueBlocks"/>: используется
    /// тестами и key-value fallback'ами остальных list-методов (<c>ToProcesses</c>,
    /// <c>ToSessions</c>, <c>ToConnections</c>, <c>ToLocks</c>, <c>ToInfobaseSummaries</c>,
    /// <c>ToJobs</c>). Новый блок начинается со строки, где ключ равен
    /// <paramref name="blockStartKey"/>. Возвращает пустой список, если блоков не найдено.
    /// </summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, string>> ParseKeyValueBlocks(
        string output, string blockStartKey)
        => TryParseKeyValueBlocks(output, blockStartKey);

    /// <summary>
    /// Признак вывода rac в формате блоков «ключ : значение» (новые версии, issue #324):
    /// первая непустая строка содержит разделитель «:» И не содержит табуляций (табличный
    /// вывод всегда с \t либо выровнен пробелами с заголовком-таблицей). Используется как
    /// условие включения key-value fallback в list-методах, чтобы не тратить попытку разбора
    /// впустую и не ломать табличные регрессии.
    /// </summary>
    internal static bool LooksLikeKeyValueOutput(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return false;
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line))
                continue;
            if (line.IndexOf('\t') >= 0)
                return false;
            return line.IndexOf(':') > 0;
        }
        return false;
    }

    /// <summary>Признак того, что значение является корректным GUID (для маркера блока кластера).</summary>
    private static bool IsGuid(string value) =>
        Guid.TryParse(value.Trim(), out _);

    /// <summary>
    /// Разбирает вывод «process list» в список рабочих процессов (rphost/rmngr).
    /// Колонки: cluster, process, host, pid, port, started-at, memory-size, memory-total,
    /// memory-available, memory-excess, threads, cpu, available-performances, running, infobases.
    /// Минимальный набор для парсинга — первые 5 колонок; отсутствующие справа — default.
    /// </summary>
    public static IReadOnlyList<RacProcessInfo> ToProcesses(string output)
    {
        const int minColumns = 5;
        var processes = new List<RacProcessInfo>();

        foreach (var row in ParseTable(output))
        {
            if (row.Count < minColumns)
                continue;

            var id = ParseGuid(row[1]);
            if (id == Guid.Empty)
                continue; // строка заголовка или мусор

            processes.Add(new RacProcessInfo
            {
                Id = id,
                Host = Col(row, 2).Trim(),
                Pid = ParseInt(Col(row, 3)),
                Port = ParseInt(Col(row, 4)),
                StartedAt = ParseDateTime(Col(row, 5)),
                MemorySize = ParseLong(Col(row, 6)),
                MemoryTotal = ParseLong(Col(row, 7)),
                MemoryAvailable = ParseLong(Col(row, 8)),
                MemoryExcess = ParseLong(Col(row, 9)),
                Threads = ParseInt(Col(row, 10)),
                Cpu = ParseDouble(Col(row, 11)),
                AvailablePerformances = ParseDouble(Col(row, 12)),
                Running = ParseBool(Col(row, 13)),
                Infobases = ParseInt(Col(row, 14))
            });
        }

        // issue #324: новые версии rac отдают «process list» блоками «ключ : значение».
        if (processes.Count == 0 && LooksLikeKeyValueOutput(output))
        {
            foreach (var block in TryParseKeyValueBlocks(output, "process"))
            {
                var id = ParseGuid(Get(block, "process"));
                if (id == Guid.Empty)
                    continue; // блок без валидного идентификатора — пропускаем

                processes.Add(new RacProcessInfo
                {
                    Id = id,
                    Host = Unquote(Get(block, "host")),
                    Pid = ParseInt(Get(block, "pid")),
                    Port = ParseInt(Get(block, "port")),
                    StartedAt = ParseDateTime(Get(block, "started-at")),
                    MemorySize = ParseLong(Get(block, "memory-size")),
                    MemoryTotal = ParseLong(Get(block, "memory-total")),
                    MemoryAvailable = ParseLong(Get(block, "memory-available")),
                    MemoryExcess = ParseLong(Get(block, "memory-excess")),
                    Threads = ParseInt(Get(block, "threads")),
                    Cpu = ParseDouble(Get(block, "cpu")),
                    AvailablePerformances = ParseDouble(Get(block, "available-performances")),
                    Running = ParseBool(Get(block, "running")),
                    Infobases = ParseInt(Get(block, "infobases"))
                });
            }
        }

        return processes;
    }

    /// <summary>
    /// Разбирает вывод «session list» в список сеансов.
    /// Колонки: cluster, session, infobase, user-name, host, app-id, started-at, last-active-at,
    /// blocked-by-ls, blocked-by-deadlock, db-proc-duration, duration-all, duration-current,
    /// duration-dbms, duration-cpu, duration-wait, memory, bytes, position, read, write,
    /// connection, hibernate, state. Минимальный набор — первые 3 колонки.
    /// </summary>
    public static IReadOnlyList<RacSessionInfo> ToSessions(string output)
    {
        const int minColumns = 3;
        var sessions = new List<RacSessionInfo>();

        foreach (var row in ParseTable(output))
        {
            if (row.Count < minColumns)
                continue;

            var id = ParseGuid(row[1]);
            if (id == Guid.Empty)
                continue; // строка заголовка или мусор

            sessions.Add(new RacSessionInfo
            {
                Id = id,
                InfobaseId = ParseNullableGuid(Col(row, 2)),
                User = Col(row, 3).Trim(),
                Host = Col(row, 4).Trim(),
                AppId = Col(row, 5).Trim(),
                StartedAt = ParseDateTime(Col(row, 6)),
                LastActiveAt = ParseDateTime(Col(row, 7)),
                BlockedByLs = ParseBool(Col(row, 8)),
                BlockedByDeadlock = ParseBool(Col(row, 9)),
                DbProcDuration = ParseLong(Col(row, 10)),
                DurationAll = ParseLong(Col(row, 11)),
                DurationCurrent = ParseLong(Col(row, 12)),
                DurationDbms = ParseLong(Col(row, 13)),
                DurationCpu = ParseLong(Col(row, 14)),
                DurationWait = ParseLong(Col(row, 15)),
                Memory = ParseLong(Col(row, 16)),
                Bytes = ParseLong(Col(row, 17)),
                Position = ParseLong(Col(row, 18)),
                Read = ParseLong(Col(row, 19)),
                Write = ParseLong(Col(row, 20)),
                ConnectionId = ParseGuid(Col(row, 21)),
                Hibernate = ParseBool(Col(row, 22)),
                State = Col(row, 23).Trim()
            });
        }

        // issue #324: новые версии rac отдают «session list» блоками «ключ : значение».
        if (sessions.Count == 0 && LooksLikeKeyValueOutput(output))
        {
            foreach (var block in TryParseKeyValueBlocks(output, "session"))
            {
                var id = ParseGuid(Get(block, "session"));
                if (id == Guid.Empty)
                    continue;

                sessions.Add(new RacSessionInfo
                {
                    Id = id,
                    InfobaseId = ParseNullableGuid(Get(block, "infobase")),
                    User = Unquote(Get(block, "user-name")),
                    Host = Unquote(Get(block, "host")),
                    AppId = Unquote(Get(block, "app-id")),
                    StartedAt = ParseDateTime(Get(block, "started-at")),
                    LastActiveAt = ParseDateTime(Get(block, "last-active-at")),
                    BlockedByLs = ParseBool(Get(block, "blocked-by-ls")),
                    BlockedByDeadlock = ParseBool(Get(block, "blocked-by-deadlock")),
                    DbProcDuration = ParseLong(Get(block, "db-proc-duration")),
                    DurationAll = ParseLong(Get(block, "duration-all")),
                    DurationCurrent = ParseLong(Get(block, "duration-current")),
                    DurationDbms = ParseLong(Get(block, "duration-dbms")),
                    DurationCpu = ParseLong(Get(block, "duration-cpu")),
                    DurationWait = ParseLong(Get(block, "duration-wait")),
                    Memory = ParseLong(Get(block, "memory")),
                    Bytes = ParseLong(Get(block, "bytes")),
                    Position = ParseLong(Get(block, "position")),
                    Read = ParseLong(Get(block, "read")),
                    Write = ParseLong(Get(block, "write")),
                    ConnectionId = ParseGuid(Get(block, "connection")),
                    Hibernate = ParseBool(Get(block, "hibernate")),
                    State = Unquote(Get(block, "state"))
                });
            }
        }

        return sessions;
    }

    /// <summary>
    /// Разбирает вывод «connection list» в список соединений.
    /// Колонки: cluster, connection, session, blocked, connector, process, host, port,
    /// established-at, last-connection-time, duration. Минимальный набор — первые 5 колонок.
    /// </summary>
    public static IReadOnlyList<RacConnectionInfo> ToConnections(string output)
    {
        const int minColumns = 5;
        var connections = new List<RacConnectionInfo>();

        foreach (var row in ParseTable(output))
        {
            if (row.Count < minColumns)
                continue;

            var id = ParseGuid(row[1]);
            if (id == Guid.Empty)
                continue; // строка заголовка или мусор

            connections.Add(new RacConnectionInfo
            {
                Id = id,
                SessionId = ParseGuid(Col(row, 2)),
                Blocked = ParseBool(Col(row, 3)),
                Connector = Col(row, 4).Trim(),
                ProcessId = ParseGuid(Col(row, 5)),
                Host = Col(row, 6).Trim(),
                Port = ParseInt(Col(row, 7)),
                EstablishedAt = ParseDateTime(Col(row, 8)),
                LastConnectionTime = ParseDateTime(Col(row, 9)),
                Duration = ParseLong(Col(row, 10))
            });
        }

        // issue #324: новые версии rac отдают «connection list» блоками «ключ : значение».
        // Схемы ключей различаются между версиями rac: прежняя — connection/session/
        // blocked/connector/process/host/port/established-at/last-connection-time/duration/
        // descr; 8.5.4.1878 (лог 7OH connection_list.log) — connection/conn-id/host/
        // process/infobase/application/connected-at/session-number/blocked-by-ls.
        // Читаем через GetAny-алиасы, чтобы обе схемы ложились в одну модель.
        if (connections.Count == 0 && LooksLikeKeyValueOutput(output))
        {
            foreach (var block in TryParseKeyValueBlocks(output, "connection"))
            {
                var id = ParseGuid(Get(block, "connection"));
                if (id == Guid.Empty)
                    continue;

                connections.Add(new RacConnectionInfo
                {
                    Id = id,
                    SessionId = ParseGuid(GetAny(block, "session", "session-number")),
                    Blocked = ParseBool(GetAny(block, "blocked-by-ls", "blocked")),
                    Connector = Unquote(GetAny(block, "connector", "application")),
                    ProcessId = ParseGuid(Get(block, "process")),
                    Host = Unquote(Get(block, "host")),
                    Port = ParseInt(Get(block, "port")),
                    EstablishedAt = ParseDateTime(GetAny(block, "established-at", "connected-at")),
                    LastConnectionTime = ParseDateTime(GetAny(block, "last-connection-time", "connected-at")),
                    Duration = ParseLong(Get(block, "duration")),
                    Descr = Unquote(GetAny(block, "descr", "application"))
                });
            }
        }

        return connections;
    }

    /// <summary>
    /// Разбирает вывод «lock list» в список блокировок объектов данных.
    /// Колонки: cluster, lock, session, infobase, connection, transaction, waiting, blocking,
    /// object. Минимальный набор — первые 5 колонок.
    /// </summary>
    public static IReadOnlyList<RacLockInfo> ToLocks(string output)
    {
        const int minColumns = 5;
        var locks = new List<RacLockInfo>();

        foreach (var row in ParseTable(output))
        {
            if (row.Count < minColumns)
                continue;

            var id = ParseGuid(row[1]);
            if (id == Guid.Empty)
                continue; // строка заголовка или мусор

            locks.Add(new RacLockInfo
            {
                Id = id,
                SessionId = ParseGuid(Col(row, 2)),
                InfobaseId = ParseGuid(Col(row, 3)),
                ConnectionId = ParseGuid(Col(row, 4)),
                TransactionId = ParseGuid(Col(row, 5)),
                Waiting = ParseBool(Col(row, 6)),
                Blocking = ParseBool(Col(row, 7)),
                Object = Col(row, 8).Trim()
            });
        }

        // issue #324: новые версии rac отдают «lock list» блоками «ключ : значение».
        // Схемы: прежняя — старт блока «lock», ключи session/infobase/connection/
        // transaction/waiting/blocking/object; 8.5.4.1878 (комментарий 7OH) — блоки
        // стартуют строкой «connection : GUID» (так же, как у connection list!), ключи
        // connection/session/object/locked/descr. Блоки блокировок отличаем от блоков
        // соединений по маркерным ключам object/locked (у соединений их нет).
        if (locks.Count == 0 && LooksLikeKeyValueOutput(output))
        {
            // Явный List: в 8.5.4 к блокам «lock» добавляются блоки со стартером
            // «connection» (фильтр по маркерным ключам ниже).
            var blocks = new List<IReadOnlyDictionary<string, string>>(TryParseKeyValueBlocks(output, "lock"));
            if (blocks.Count == 0)
            {
                foreach (var block in TryParseKeyValueBlocks(output, "connection"))
                {
                    if (!block.ContainsKey("object") && !block.ContainsKey("locked"))
                        continue; // это блоки connection list, а не блокировки
                    blocks.Add(block);
                }
            }

            foreach (var block in blocks)
            {
                var id = ParseGuid(Get(block, "lock"));
                if (id == Guid.Empty)
                    id = ParseGuid(Get(block, "connection")); // 8.5.4: uuid блокировки в выводе отсутствует

                locks.Add(new RacLockInfo
                {
                    Id = id,
                    SessionId = ParseGuid(Get(block, "session")),
                    InfobaseId = ParseGuid(Get(block, "infobase")),
                    ConnectionId = ParseGuid(Get(block, "connection")),
                    TransactionId = ParseGuid(Get(block, "transaction")),
                    Waiting = ParseBool(Get(block, "waiting")),
                    Blocking = ParseBool(Get(block, "blocking")),
                    // В схеме 8.5.4 «object» — GUID ссылки на объект (часто пустой), а
                    // человекочитаемое описание — «descr»: предпочитаем его.
                    Object = Unquote(GetAny(block, "descr", "object"))
                });
            }
        }

        return locks;
    }

    /// <summary>
    /// Разбирает вывод «infobase summary list» в список информационных баз кластера.
    /// Колонки: infobase, name, descr, dbms, db-server, db-name, db-user, locale,
    /// security-level, licensed. Минимальный набор — первые 2 колонки; отсутствующие
    /// справа — значения по умолчанию, лишние — игнорируются.
    /// </summary>
    public static IReadOnlyList<RacInfobaseSummary> ToInfobaseSummaries(string output)
    {
        const int minColumns = 2;
        var infobases = new List<RacInfobaseSummary>();

        foreach (var row in ParseTable(output))
        {
            if (row.Count < minColumns)
                continue;

            var id = ParseGuid(row[0]);
            if (id == Guid.Empty)
                continue; // строка заголовка или мусор

            infobases.Add(new RacInfobaseSummary
            {
                InfobaseId = id,
                Name = row[1].Trim(),
                Descr = Col(row, 2).Trim(),
                Dbms = Col(row, 3).Trim(),
                DbServer = Col(row, 4).Trim(),
                DbName = Col(row, 5).Trim(),
                DbUser = Col(row, 6).Trim(),
                Locale = Col(row, 7).Trim(),
                SecurityLevel = ParseInt(Col(row, 8)),
                Licensed = ParseBool(Col(row, 9))
            });
        }

        // issue #324: новые версии rac отдают «infobase summary list» блоками «ключ : значение».
        if (infobases.Count == 0 && LooksLikeKeyValueOutput(output))
        {
            foreach (var block in TryParseKeyValueBlocks(output, "infobase"))
            {
                var id = ParseGuid(Get(block, "infobase"));
                if (id == Guid.Empty)
                    continue;

                infobases.Add(new RacInfobaseSummary
                {
                    InfobaseId = id,
                    Name = Unquote(Get(block, "name")),
                    Descr = Unquote(Get(block, "descr")),
                    Dbms = Unquote(Get(block, "dbms")),
                    DbServer = Unquote(Get(block, "db-server")),
                    DbName = Unquote(Get(block, "db-name")),
                    DbUser = Unquote(Get(block, "db-user")),
                    Locale = Unquote(Get(block, "locale")),
                    SecurityLevel = ParseInt(Get(block, "security-level")),
                    Licensed = ParseBool(Get(block, "licensed"))
                });
            }
        }

        return infobases;
    }

    /// <summary>
    /// Разбирает вывод «job list» в список регламентных заданий кластера.
    /// Колонки: cluster, job, infobase, name, method-name, predefined, schedule, state,
    /// started-at, next-start, last-start, last-end, last-success, last-error,
    /// last-error-descr, process, …, result. Минимальный набор — первые 3 колонки
    /// (cluster, job, infobase); отсутствующие справа — значения по умолчанию, лишние —
    /// игнорируются. У строковых полей снимаются обрамляющие двойные кавычки (rac может
    /// заключать значения с пробелами — например cron-расписание — в кавычки).
    /// </summary>
    public static IReadOnlyList<RacJobInfo> ToJobs(string output)
    {
        const int minColumns = 3;
        var jobs = new List<RacJobInfo>();

        foreach (var row in ParseTable(output))
        {
            if (row.Count < minColumns)
                continue;

            var id = ParseGuid(row[1]);
            if (id == Guid.Empty)
                continue; // строка заголовка или мусор

            jobs.Add(new RacJobInfo
            {
                Id = id,
                InfobaseId = ParseNullableGuid(Col(row, 2)),
                Name = Unquote(Col(row, 3)),
                MethodName = Unquote(Col(row, 4)),
                Predefined = ParseBool(Col(row, 5)),
                Schedule = Unquote(Col(row, 6)),
                State = Unquote(Col(row, 7)),
                StartedAt = ParseDateTime(Col(row, 8)),
                NextStart = ParseDateTime(Col(row, 9)),
                LastStart = ParseDateTime(Col(row, 10)),
                LastEnd = ParseDateTime(Col(row, 11)),
                LastSuccess = ParseBool(Col(row, 12)),
                LastError = ParseBool(Col(row, 13)),
                LastErrorDescr = Unquote(Col(row, 14)),
                ProcessId = ParseGuid(Col(row, 15)),
                Result = Unquote(Col(row, 21))
            });
        }

        // issue #324: новые версии rac отдают «job list» блоками «ключ : значение».
        if (jobs.Count == 0 && LooksLikeKeyValueOutput(output))
        {
            foreach (var block in TryParseKeyValueBlocks(output, "job"))
            {
                var id = ParseGuid(Get(block, "job"));
                if (id == Guid.Empty)
                    continue;

                jobs.Add(new RacJobInfo
                {
                    Id = id,
                    InfobaseId = ParseNullableGuid(Get(block, "infobase")),
                    Name = Unquote(Get(block, "name")),
                    MethodName = Unquote(Get(block, "method-name")),
                    Predefined = ParseBool(Get(block, "predefined")),
                    Schedule = Unquote(Get(block, "schedule")),
                    State = Unquote(Get(block, "state")),
                    StartedAt = ParseDateTime(Get(block, "started-at")),
                    NextStart = ParseDateTime(Get(block, "next-start")),
                    LastStart = ParseDateTime(Get(block, "last-start")),
                    LastEnd = ParseDateTime(Get(block, "last-end")),
                    LastSuccess = ParseBool(Get(block, "last-success")),
                    LastError = ParseBool(Get(block, "last-error")),
                    LastErrorDescr = Unquote(Get(block, "last-error-descr")),
                    ProcessId = ParseGuid(Get(block, "process")),
                    Result = Unquote(Get(block, "result"))
                });
            }
        }

        return jobs;
    }

    /// <summary>
    /// Разбирает вывод «cluster info» (формат «ключ: значение») в <see cref="RacClusterInfo"/>:
    /// сохраняет полный словарь свойств и заполняет типизированные частые поля.
    /// </summary>
    public static RacClusterInfo ToClusterInfo(string output)
    {
        var properties = ParseInfo(output);

        return new RacClusterInfo
        {
            Properties = properties,
            // rac 8.5.4 заключает значения с пробелами в кавычки («name : "Локальный
            // кластер"», issue #324) — снимаем их, как это делает блок-парсер ToClusters.
            Name = Unquote(Get(properties, "name")),
            HostName = Unquote(Get(properties, "hostName")),
            Port = ParseInt(Get(properties, "port")),
            ExpirationTimeout = ParseLong(Get(properties, "expirationTimeout")),
            LifetimeLimit = ParseLong(Get(properties, "lifetimeLimit")),
            MaxMemorySize = ParseLong(Get(properties, "maxMemorySize")),
            MaxMemoryTimeLimit = ParseLong(Get(properties, "maxMemoryTimeLimit")),
            SecurityLevel = ParseInt(Get(properties, "securityLevel")),
            SessionIdleTimeout = ParseLong(Get(properties, "sessionIdleTimeout")),
            SessionMaxMemorySize = ParseLong(Get(properties, "sessionMaxMemorySize")),
            SessionMaxTimeLimit = ParseLong(Get(properties, "sessionMaxTimeLimit"))
        };
    }

    /// <summary>Значение по ключу без учёта регистра или пустая строка.</summary>
    private static string Get(IReadOnlyDictionary<string, string> properties, string key)
    {
        foreach (var pair in properties)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
                return pair.Value;
        }

        return string.Empty;
    }

    /// <summary>Значение по ПЕРВОМУ найденному ключу из списка алиасов (регистронезависимо)
    /// или пустая строка. Схемы вывода rac разных версий отличаются именами ключей
    /// (issue #324): новые 8.5.4.1878 (connected-at / blocked-by-ls / application) и
    /// прежние (established-at / blocked / connector / descr) читаются одним маппингом.</summary>
    private static string GetAny(IReadOnlyDictionary<string, string> properties, params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = Get(properties, key);
            if (value.Length > 0)
                return value;
        }

        return string.Empty;
    }

    /// <summary>Значение колонки с учётом «лишних колонок справа»: за пределами — пустая строка.</summary>
    private static string Col(IReadOnlyList<string> row, int index) =>
        index < row.Count ? row[index] : string.Empty;

    /// <summary>
    /// Снимает обрамляющие двойные кавычки со значения (rac может заключать поля
    /// с пробелами — например cron-расписание — в кавычки). Некавыченные значения
    /// возвращаются без изменений.
    /// </summary>
    private static string Unquote(string value)
    {
        var v = value.Trim();
        if (v.Length >= 2 && v[0] == '"' && v[v.Length - 1] == '"')
            return v.Substring(1, v.Length - 2).Trim();
        return v;
    }

    private static Guid ParseGuid(string value) =>
        Guid.TryParse(value.Trim(), out var guid) ? guid : Guid.Empty;

    private static Guid? ParseNullableGuid(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return Guid.TryParse(value.Trim(), out var guid) ? guid : null;
    }

    private static bool ParseBool(string value)
    {
        var v = value.Trim();
        if (v is "1" or "true" or "True" or "TRUE")
            return true;
        if (v is "0" or "false" or "False" or "FALSE")
            return false;
        return false;
    }

    private static int ParseInt(string value) =>
        int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : 0;

    private static long ParseLong(string value) =>
        long.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : 0L;

    private static double ParseDouble(string value) =>
        double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
            ? result
            : 0d;

    private static DateTime ParseDateTime(string value)
    {
        // rac отдаёт даты в фиксированном формате (например 2026-09-28T14:00:00),
        // но для устойчивости используем TryParse с инвариантной культурой.
        if (DateTime.TryParse(
                value.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return date;

        return default;
    }
}