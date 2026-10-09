using Configuration_Management.Models;
using Configuration_Management.Services;
using Xunit;

namespace ConfigurationManagement.Tests;

/// <summary>
/// Тесты чистого парсера вывода rac (<see cref="RacOutputParser"/>): таблицы
/// (ParseTable), «ключ: значение» (ParseInfo) и типизированные парсеры
/// кластеров/процессов/сеансов/соединений/блокировок/информации о кластере.
/// Образцы вывода соответствуют документированному на ИТС формату rac:
/// list-команды — таблица с разделителем \t и строкой заголовка; info — «ключ: значение».
/// </summary>
public sealed class RacOutputParserTests
{
    // ---------- ParseTable ----------

    [Fact]
    public void ParseTable_SplitsRowsAndColumns()
    {
        var table = RacOutputParser.ParseTable("a\tb\tc\n1\t2\t3\n");

        Assert.Equal(2, table.Count);
        Assert.Equal(new[] { "a", "b", "c" }, table[0]);
        Assert.Equal(new[] { "1", "2", "3" }, table[1]);
    }

    [Fact]
    public void ParseTable_SkipsEmptyLines_AndHandlesCrLf()
    {
        var table = RacOutputParser.ParseTable("a\tb\r\n\r\n1\t2\r\n");

        Assert.Equal(2, table.Count);
        Assert.Equal("b", table[0][1]); // без «\r»
        Assert.Equal("2", table[1][1]);
    }

    [Fact]
    public void ParseTable_PreservesSpacesInValues()
    {
        var table = RacOutputParser.ParseTable("cluster\tname\n111-222\tЛокальный кластер\n");

        Assert.Equal("Локальный кластер", table[1][1]);
    }

    [Fact]
    public void ParseTable_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(RacOutputParser.ParseTable(string.Empty));
        Assert.Empty(RacOutputParser.ParseTable("\n\n  \n"));
    }

    [Fact]
    public void ParseTable_FallsBackToSpaceSeparation_WhenNoTabs()
    {
        // Fallback issue #324: некоторые версии/окружения выводят таблицу с выравниванием
        // пробелами вместо табуляции. Разделитель — 2+ пробелов подряд, одиночный пробел
        // внутри значения (имя кластера) остаётся частью поля.
        const string output =
            "cluster                                name             port\n" +
            "8b2f6f5e-6e3c-4c5a-8a9b-1c2d3e4f5a6b     Локальный кластер  1541\n";

        var table = RacOutputParser.ParseTable(output);

        Assert.Equal(2, table.Count);
        Assert.Equal("8b2f6f5e-6e3c-4c5a-8a9b-1c2d3e4f5a6b", table[1][0]);
        Assert.Equal("Локальный кластер", table[1][1]);
        Assert.Equal("1541", table[1][2]);
    }

    [Fact]
    public void ParseTable_WithTabs_KeepsSingleSpacesInsideValues()
    {
        // Если в строке есть табуляция — разделение строго по ней: одиночные пробелы
        // внутри значения не разрывают поле даже при выравнивании пробелами.
        const string output =
            "cluster\tname\tport\n" +
            "8b2f6f5e-6e3c-4c5a-8a9b-1c2d3e4f5a6b\tЛокальный  кластер\t1541\n";

        var table = RacOutputParser.ParseTable(output);

        Assert.Equal("Локальный  кластер", table[1][1]); // двойной пробел сохраняется
        Assert.Equal(3, table[1].Count);
    }

    [Fact]
    public void ParseTable_PositionalSplit_KeepsInnerMultiSpaces()
    {
        // Вывод с ЕДИНООБРАЗНЫМ выравниванием пробелами без табуляций (issue #324):
        // границы колонок заголовка совпадают с границами строк данных, поэтому имя
        // с НЕСКОЛЬКИМИ пробелами подряд не режется (прежний fallback «2+ пробела»
        // терял такие имена).
        const string guid = "8b2f6f5e-6e3c-4c5a-8a9b-1c2d3e4f5a6b";
        var output =
            "cluster".PadRight(38) + "name".PadRight(20) + "port\n" +
            guid.PadRight(38) + "Локальный  кластер".PadRight(20) + "1541\n";

        var table = RacOutputParser.ParseTable(output);

        Assert.Equal(2, table.Count);
        Assert.Equal("cluster", table[0][0]);
        Assert.Equal("port", table[0][2]);
        Assert.Equal(guid, table[1][0]);
        Assert.Equal("Локальный  кластер", table[1][1]); // двойной пробел внутри имени
        Assert.Equal("1541", table[1][2]);
    }

    [Fact]
    public void ParseTable_PositionalSplit_HandlesShorterValues()
    {
        // Значение короче ширины колонки — остаток поля обрезается, без смещения
        // последующих колонок (единообразное выравнивание — позиционный разбор активен).
        const string guid = "8b2f6f5e-6e3c-4c5a-8a9b-1c2d3e4f5a6b";
        var output =
            "cluster".PadRight(38) + "name".PadRight(20) + "port\n" +
            guid.PadRight(38) + "Тест".PadRight(20) + "1541\n";

        var table = RacOutputParser.ParseTable(output);

        Assert.Equal("Тест", table[1][1]);
        Assert.Equal("1541", table[1][2]);
    }

    [Fact]
    public void ParseTable_PositionalSplit_HandlesCrLf()
    {
        // CRLF-вывод (cmd на Windows): «\r» удаляется до позиционного разбора,
        // единообразное выравнивание сохраняется.
        const string guid = "8b2f6f5e-6e3c-4c5a-8a9b-1c2d3e4f5a6b";
        var output =
            "cluster".PadRight(38) + "name".PadRight(20) + "port\r\n" +
            guid.PadRight(38) + "Локальный кластер".PadRight(20) + "1541\r\n";

        var table = RacOutputParser.ParseTable(output);

        Assert.Equal(2, table.Count);
        Assert.Equal("Локальный кластер", table[1][1]);
        Assert.Equal("1541", table[1][2]);
    }

    [Fact]
    public void ParseTable_HeaderWithTabs_FallsBackToSpacesInDataRows()
    {
        // Смешанный вывод: заголовок через табуляции, строки данных — выравнивание
        // пробелами. Заголовок с табами отключает позиционный разбор (иначе индексы
        // колонок были бы неверными), строки без табов делятся по 2+ пробелам.
        const string output =
            "cluster\tname\tport\n" +
            "8b2f6f5e-6e3c-4c5a-8a9b-1c2d3e4f5a6b     Локальный кластер    1541\n";

        var table = RacOutputParser.ParseTable(output);

        Assert.Equal("cluster", table[0][0]); // заголовок — по табам
        Assert.Equal("8b2f6f5e-6e3c-4c5a-8a9b-1c2d3e4f5a6b", table[1][0]);
        Assert.Equal("Локальный кластер", table[1][1]);
        Assert.Equal("1541", table[1][2]);
    }

    // ---------- ParseInfo ----------

    [Fact]
    public void ParseInfo_ParsesKeyValue()
    {
        var info = RacOutputParser.ParseInfo("name: Локальный кластер\nport: 1541\n");

        Assert.Equal("Локальный кластер", info["name"]);
        Assert.Equal("1541", info["port"]);
    }

    [Fact]
    public void ParseInfo_TrimsKeyWithPadding()
    {
        // Реальный rac выравнивает ключи пробелами перед «:».
        var info = RacOutputParser.ParseInfo(
            "cluster        : 8b2f6f5e-6e3c-4c5a-8a9b-1c2d3e4f5a6b\n");

        Assert.Equal("8b2f6f5e-6e3c-4c5a-8a9b-1c2d3e4f5a6b", info["cluster"]);
    }

    [Fact]
    public void ParseInfo_SplitsOnFirstColon()
    {
        var info = RacOutputParser.ParseInfo("desc: значение: с двоеточием\n");

        Assert.Equal("значение: с двоеточием", info["desc"]);
    }

    [Fact]
    public void ParseInfo_SkipsLinesWithoutColon_AndEmpty()
    {
        var info = RacOutputParser.ParseInfo("no colon here\n\nname: Тест\n");

        var pair = Assert.Single(info);
        Assert.Equal("name", pair.Key);
        Assert.Equal("Тест", pair.Value);
    }

    [Fact]
    public void ParseInfo_HandlesValueWithSpacesAndCyrillic()
    {
        var info = RacOutputParser.ParseInfo("hostName : Сервер Управления 1С\n");

        Assert.Equal("Сервер Управления 1С", info["hostName"]);
    }

    // ---------- ToClusters ----------

    [Fact]
    public void ToClusters_ParsesSample()
    {
        const string output =
            "cluster\tname\tport\n" +
            "8b2f6f5e-6e3c-4c5a-8a9b-1c2d3e4f5a6b\tЛокальный кластер\t1541\n" +
            "a3f1d2b4-1111-2222-3333-444455556666\tРабочий кластер\t2541\n";

        var clusters = RacOutputParser.ToClusters(output);

        Assert.Equal(2, clusters.Count);
        Assert.Equal(Guid.Parse("8b2f6f5e-6e3c-4c5a-8a9b-1c2d3e4f5a6b"), clusters[0].Id);
        Assert.Equal("Локальный кластер", clusters[0].Name);
        Assert.Equal(1541, clusters[0].Port);
        Assert.Equal("Рабочий кластер", clusters[1].Name);
        Assert.Equal(2541, clusters[1].Port);
    }

    [Fact]
    public void ToClusters_IgnoresExtraColumns()
    {
        const string output =
            "cluster\tname\tport\textra\n" +
            "8b2f6f5e-6e3c-4c5a-8a9b-1c2d3e4f5a6b\tЛокальный кластер\t1541\tосновной\n";

        var cluster = Assert.Single(RacOutputParser.ToClusters(output));

        Assert.Equal("Локальный кластер", cluster.Name);
        Assert.Equal(1541, cluster.Port);
    }

    [Fact]
    public void ToClusters_SkipsBadUuid_AndShortRow()
    {
        const string output =
            "cluster\tname\tport\n" +
            "не-uuid\tКривой идентификатор\t1541\n" +     // кривой uuid — строка пропускается
            "8b2f6f5e-6e3c-4c5a-8a9b-1c2d3e4f5a6b\n";     // меньше полей — строка пропускается

        Assert.Empty(RacOutputParser.ToClusters(output));
    }

    [Fact]
    public void ToClusters_EmptyOutput_ReturnsEmpty()
    {
        Assert.Empty(RacOutputParser.ToClusters(string.Empty));
        Assert.Empty(RacOutputParser.ToClusters("cluster\tname\tport\n"));
    }

    [Fact]
    public void ToClusters_ParsesRealWorldSample()
    {
        // Фактический формат rac «cluster list»: таблица с табуляцией, первая строка —
        // заголовок колонок; у кластера своё имя и ПОРТ КЛАСТЕРА (обычно 1541), отличный
        // от порта агента (1540), к которому подключается сам rac (issue #324).
        const string output =
            "cluster\tname\tport\n" +
            "8b2f6f5e-6e3c-4c5a-8a9b-1c2d3e4f5a6b\tЛокальный кластер\t1541\n" +
            "a3f1d2b4-1111-2222-3333-444455556666\tБухгалтерия предприятия\t1542\n";

        var clusters = RacOutputParser.ToClusters(output);

        Assert.Equal(2, clusters.Count);
        Assert.Equal("Локальный кластер", clusters[0].Name);
        Assert.Equal(1541, clusters[0].Port);
        Assert.Equal("Бухгалтерия предприятия", clusters[1].Name);
        Assert.Equal(1542, clusters[1].Port);
    }

    [Fact]
    public void ToClusters_ParsesSpaceAlignedOutput()
    {
        // Пробельный fallback (issue #324) доходит до типизированного парсера: колонка
        // порта кластера читается как int, GUID кластера распознаётся.
        const string output =
            "cluster                                name             port\n" +
            "8b2f6f5e-6e3c-4c5a-8a9b-1c2d3e4f5a6b     Локальный кластер  1541\n";

        var cluster = Assert.Single(RacOutputParser.ToClusters(output));

        Assert.Equal(Guid.Parse("8b2f6f5e-6e3c-4c5a-8a9b-1c2d3e4f5a6b"), cluster.Id);
        Assert.Equal("Локальный кластер", cluster.Name);
        Assert.Equal(1541, cluster.Port);
    }

    [Fact]
    public void ToClusters_ParsesNonStandardPortsSample()
    {
        // Сценарий из обращения (issue #324): у пользователя нестандартные порты —
        // агент ragent слушает 27545 (в приложение вводится он), кластер — 27541.
        // Вывод «cluster list» с нестандартным портом кластера разбирается корректно:
        // колонка port — это порт КЛАСТЕРА, а не точка подключения rac.
        const string output =
            "cluster                                name             port\n" +
            "cbc95ef0-1234-5678-9abc-def012345678     Сервер бухгалтерии  27541\n";

        var cluster = Assert.Single(RacOutputParser.ToClusters(output));

        Assert.Equal(Guid.Parse("cbc95ef0-1234-5678-9abc-def012345678"), cluster.Id);
        Assert.Equal("Сервер бухгалтерии", cluster.Name);
        Assert.Equal(27541, cluster.Port);
    }

    [Fact]
    public void ToClusters_ParsesKeyValueBlocks()
    {
        // Реальный вывод из issue #324: новые версии rac отдают «cluster list»
        // блоками «ключ : значение» с выравниванием пробелами и двоеточием
        // (не таблицей). Парсер должен извлечь кластер из такого вывода.
        const string output =
            "cluster                                   : cbc95ef0-99c9-4b1a-909f-cff4c8de61d9\n" +
            "host                                      : ALF\n" +
            "port                                      : 27541\n" +
            "name                                      : \"Локальный кластер\"\n";

        var cluster = Assert.Single(RacOutputParser.ToClusters(output));

        Assert.Equal(Guid.Parse("cbc95ef0-99c9-4b1a-909f-cff4c8de61d9"), cluster.Id);
        Assert.Equal("Локальный кластер", cluster.Name);
        Assert.Equal(27541, cluster.Port);
    }

    [Fact]
    public void ToClusters_ParsesMultipleKeyValueBlocks()
    {
        // Несколько кластеров подряд — каждый блок начинается строкой «cluster : GUID».
        const string output =
            "cluster                                   : cbc95ef0-99c9-4b1a-909f-cff4c8de61d9\n" +
            "host                                      : ALF\n" +
            "port                                      : 27541\n" +
            "name                                      : \"Локальный кластер\"\n" +
            "cluster                                   : a3f1d2b4-1111-2222-3333-444455556666\n" +
            "host                                      : ALF\n" +
            "port                                      : 27542\n" +
            "name                                      : \"Бухгалтерия предприятия\"\n";

        var clusters = RacOutputParser.ToClusters(output);

        Assert.Equal(2, clusters.Count);
        Assert.Equal(Guid.Parse("cbc95ef0-99c9-4b1a-909f-cff4c8de61d9"), clusters[0].Id);
        Assert.Equal("Локальный кластер", clusters[0].Name);
        Assert.Equal(27541, clusters[0].Port);
        Assert.Equal(Guid.Parse("a3f1d2b4-1111-2222-3333-444455556666"), clusters[1].Id);
        Assert.Equal("Бухгалтерия предприятия", clusters[1].Name);
        Assert.Equal(27542, clusters[1].Port);
    }

    [Fact]
    public void ToClusters_KeyValueWithQuotedName()
    {
        // Имя кластера в кавычках (rac заключает значения с пробелами) — кавычки снимаются.
        const string output =
            "cluster : cbc95ef0-99c9-4b1a-909f-cff4c8de61d9\n" +
            "name    : \"Узлы ЭДО (основной)\"\n" +
            "port    : 2541\n";

        var cluster = Assert.Single(RacOutputParser.ToClusters(output));

        Assert.Equal("Узлы ЭДО (основной)", cluster.Name);
        Assert.Equal(2541, cluster.Port);
    }

    [Fact]
    public void ToClusters_TableFormatStillWorks()
    {
        // Регрессия: табличный формат (заголовок + строки данных) не должен ломаться
        // при добавлении разбора key-value блоков.
        const string output =
            "cluster\tname\tport\n" +
            "8b2f6f5e-6e3c-4c5a-8a9b-1c2d3e4f5a6b\tЛокальный кластер\t1541\n";

        var cluster = Assert.Single(RacOutputParser.ToClusters(output));

        Assert.Equal(Guid.Parse("8b2f6f5e-6e3c-4c5a-8a9b-1c2d3e4f5a6b"), cluster.Id);
        Assert.Equal("Локальный кластер", cluster.Name);
        Assert.Equal(1541, cluster.Port);
    }

    [Fact]
    public void ToClusters_KeyValueWithoutValidClusterMarker_ReturnsEmpty()
    {
        // Строка «cluster» без GUID-значения не считается маркером блока — вывод
        // не должен превращаться в ложные кластеры.
        const string output =
            "cluster : не-guid\n" +
            "name    : Мусор\n" +
            "port    : 1541\n";

        Assert.Empty(RacOutputParser.ToClusters(output));
    }

    // ---------- ToProcesses ----------

    [Fact]
    public void ToProcesses_ParsesSample()
    {
        const string output =
            "cluster\tprocess\thost\tpid\tport\tstarted-at\tmemory-size\tmemory-total\tmemory-available\tmemory-excess\tthreads\tcpu\tavailable-performances\trunning\tinfobases\n" +
            "8b2f6f5e-6e3c-4c5a-8a9b-1c2d3e4f5a6b\tc3d4e5f6-aaaa-bbbb-cccc-ddddeeeeffff\tserver-01\t2048\t1560\t2026-09-28T10:00:00\t104857600\t536870912\t4294967296\t0\t32\t2.5\t100\t1\t3\n";

        var process = Assert.Single(RacOutputParser.ToProcesses(output));

        Assert.Equal(Guid.Parse("c3d4e5f6-aaaa-bbbb-cccc-ddddeeeeffff"), process.Id);
        Assert.Equal("server-01", process.Host);
        Assert.Equal(2048, process.Pid);
        Assert.Equal(1560, process.Port);
        Assert.Equal(new DateTime(2026, 9, 28, 10, 0, 0), process.StartedAt);
        Assert.Equal(104857600L, process.MemorySize);
        Assert.Equal(536870912L, process.MemoryTotal);
        Assert.Equal(4294967296L, process.MemoryAvailable);
        Assert.Equal(0L, process.MemoryExcess);
        Assert.Equal(32, process.Threads);
        Assert.Equal(2.5, process.Cpu);
        Assert.Equal(100d, process.AvailablePerformances);
        Assert.True(process.Running);
        Assert.Equal(3, process.Infobases);
    }

    [Fact]
    public void ToProcesses_ParsesMinimalColumns_WithDefaults()
    {
        const string output =
            "cluster\tprocess\thost\tpid\tport\n" +
            "8b2f6f5e-6e3c-4c5a-8a9b-1c2d3e4f5a6b\tc3d4e5f6-aaaa-bbbb-cccc-ddddeeeeffff\tserver-01\t2048\t1560\n";

        var process = Assert.Single(RacOutputParser.ToProcesses(output));

        Assert.Equal(2048, process.Pid);
        Assert.Equal(0L, process.MemorySize); // отсутствующие справа колонки — default
        Assert.Equal(0d, process.Cpu);
        Assert.False(process.Running);
        Assert.Equal(0, process.Infobases);
    }

    [Fact]
    public void ToProcesses_SkipsHeader_AndShortRow()
    {
        const string output =
            "cluster\tprocess\thost\tpid\tport\n" +
            "8b2f6f5e-6e3c-4c5a-8a9b-1c2d3e4f5a6b\tc3d4e5f6-aaaa-bbbb-cccc-ddddeeeeffff\tserver-01\n";

        Assert.Empty(RacOutputParser.ToProcesses(output));
    }

    // ---------- ToSessions ----------

    [Fact]
    public void ToSessions_ParsesSample()
    {
        const string output =
            "cluster\tsession\tinfobase\tuser-name\thost\tapp-id\tstarted-at\tlast-active-at\tblocked-by-ls\tblocked-by-deadlock\tdb-proc-duration\tduration-all\tduration-current\tduration-dbms\tduration-cpu\tduration-wait\tmemory\tbytes\tposition\tread\twrite\tconnection\thibernate\tstate\n" +
            "8b2f6f5e-6e3c-4c5a-8a9b-1c2d3e4f5a6b\t11111111-2222-3333-4444-555555555555\t99999999-8888-7777-6666-555555555555\tИванов Иван\tWORKSTATION-01\t1CV8\t2026-09-28T09:30:00\t2026-09-28T09:45:12\t0\t1\t0\t1250\t350\t900\t200\t150\t104857600\t12345678\t0\t1024\t2048\tbbbbbbbb-0000-1111-2222-333344445555\t0\tactive\n";

        var session = Assert.Single(RacOutputParser.ToSessions(output));

        Assert.Equal(Guid.Parse("11111111-2222-3333-4444-555555555555"), session.Id);
        Assert.Equal(Guid.Parse("99999999-8888-7777-6666-555555555555"), session.InfobaseId);
        Assert.Equal("Иванов Иван", session.User);
        Assert.Equal("WORKSTATION-01", session.Host);
        Assert.Equal("1CV8", session.AppId);
        Assert.Equal(new DateTime(2026, 9, 28, 9, 30, 0), session.StartedAt);
        Assert.Equal(new DateTime(2026, 9, 28, 9, 45, 12), session.LastActiveAt);
        Assert.False(session.BlockedByLs);
        Assert.True(session.BlockedByDeadlock);
        Assert.Equal(0L, session.DbProcDuration);
        Assert.Equal(1250L, session.DurationAll);
        Assert.Equal(350L, session.DurationCurrent);
        Assert.Equal(900L, session.DurationDbms);
        Assert.Equal(200L, session.DurationCpu);
        Assert.Equal(150L, session.DurationWait);
        Assert.Equal(104857600L, session.Memory);
        Assert.Equal(12345678L, session.Bytes);
        Assert.Equal(0L, session.Position);
        Assert.Equal(1024L, session.Read);
        Assert.Equal(2048L, session.Write);
        Assert.Equal(Guid.Parse("bbbbbbbb-0000-1111-2222-333344445555"), session.ConnectionId);
        Assert.False(session.Hibernate);
        Assert.Equal("active", session.State);
    }

    [Fact]
    public void ToSessions_EmptyInfobase_IsNull()
    {
        const string output =
            "cluster\tsession\tinfobase\tuser-name\n" +
            "8b2f6f5e-6e3c-4c5a-8a9b-1c2d3e4f5a6b\t11111111-2222-3333-4444-555555555555\t\tСлужебный\n";

        var session = Assert.Single(RacOutputParser.ToSessions(output));

        Assert.Null(session.InfobaseId);
        Assert.Equal("Служебный", session.User);
        Assert.Equal(string.Empty, session.Host);
    }

    [Fact]
    public void ToSessions_SkipsShortRow_WithoutThrowing()
    {
        const string output =
            "cluster\tsession\n" +
            "8b2f6f5e-6e3c-4c5a-8a9b-1c2d3e4f5a6b\t11111111-2222-3333-4444-555555555555\n";

        Assert.Empty(RacOutputParser.ToSessions(output));
    }

    // ---------- ToConnections ----------

    [Fact]
    public void ToConnections_ParsesSample()
    {
        const string output =
            "cluster\tconnection\tsession\tblocked\tconnector\tprocess\thost\tport\testablished-at\tlast-connection-time\tduration\n" +
            "8b2f6f5e-6e3c-4c5a-8a9b-1c2d3e4f5a6b\t11112222-3333-4444-5555-666677778888\t11111111-2222-3333-4444-555555555555\t1\t1CV8\tc3d4e5f6-aaaa-bbbb-cccc-ddddeeeeffff\tWORKSTATION-01\t1560\t2026-09-28T09:30:00\t2026-09-28T09:45:12\t1250\n";

        var connection = Assert.Single(RacOutputParser.ToConnections(output));

        Assert.Equal(Guid.Parse("11112222-3333-4444-5555-666677778888"), connection.Id);
        Assert.Equal(Guid.Parse("11111111-2222-3333-4444-555555555555"), connection.SessionId);
        Assert.True(connection.Blocked);
        Assert.Equal("1CV8", connection.Connector);
        Assert.Equal(Guid.Parse("c3d4e5f6-aaaa-bbbb-cccc-ddddeeeeffff"), connection.ProcessId);
        Assert.Equal("WORKSTATION-01", connection.Host);
        Assert.Equal(1560, connection.Port);
        Assert.Equal(new DateTime(2026, 9, 28, 9, 30, 0), connection.EstablishedAt);
        Assert.Equal(new DateTime(2026, 9, 28, 9, 45, 12), connection.LastConnectionTime);
        Assert.Equal(1250L, connection.Duration);
    }

    // ---------- ToLocks ----------

    [Fact]
    public void ToLocks_ParsesSample()
    {
        const string output =
            "cluster\tlock\tsession\tinfobase\tconnection\ttransaction\twaiting\tblocking\tobject\n" +
            "8b2f6f5e-6e3c-4c5a-8a9b-1c2d3e4f5a6b\t11111111-aaaa-bbbb-cccc-dddddddddddd\t11111111-2222-3333-4444-555555555555\t99999999-8888-7777-6666-555555555555\t11112222-3333-4444-5555-666677778888\taaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee\t0\t1\tСправочник.Номенклатура.Ссылка\n";

        var lockInfo = Assert.Single(RacOutputParser.ToLocks(output));

        Assert.Equal(Guid.Parse("11111111-aaaa-bbbb-cccc-dddddddddddd"), lockInfo.Id);
        Assert.Equal(Guid.Parse("11111111-2222-3333-4444-555555555555"), lockInfo.SessionId);
        Assert.Equal(Guid.Parse("99999999-8888-7777-6666-555555555555"), lockInfo.InfobaseId);
        Assert.Equal(Guid.Parse("11112222-3333-4444-5555-666677778888"), lockInfo.ConnectionId);
        Assert.Equal(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"), lockInfo.TransactionId);
        Assert.False(lockInfo.Waiting);
        Assert.True(lockInfo.Blocking);
        Assert.Equal("Справочник.Номенклатура.Ссылка", lockInfo.Object);
    }

    // ---------- ToClusterInfo ----------

    [Fact]
    public void ToClusterInfo_ParsesSample()
    {
        const string output =
            "cluster        : 8b2f6f5e-6e3c-4c5a-8a9b-1c2d3e4f5a6b\n" +
            "name           : Локальный кластер\n" +
            "hostName       : localhost\n" +
            "port           : 1541\n" +
            "expirationTimeout : 360\n" +
            "lifetimeLimit  : 1440\n" +
            "maxMemorySize  : 0\n" +
            "maxMemoryTimeLimit : 0\n" +
            "securityLevel  : 0\n" +
            "sessionIdleTimeout : 0\n" +
            "sessionMaxMemorySize : 0\n" +
            "sessionMaxTimeLimit : 0\n";

        var info = RacOutputParser.ToClusterInfo(output);

        Assert.Equal("Локальный кластер", info.Name);
        Assert.Equal("localhost", info.HostName);
        Assert.Equal(1541, info.Port);
        Assert.Equal(360L, info.ExpirationTimeout);
        Assert.Equal(1440L, info.LifetimeLimit);
        Assert.Equal(0L, info.MaxMemorySize);
        Assert.Equal(0L, info.MaxMemoryTimeLimit);
        Assert.Equal(0, info.SecurityLevel);
        Assert.Equal(0L, info.SessionIdleTimeout);
        Assert.Equal(0L, info.SessionMaxMemorySize);
        Assert.Equal(0L, info.SessionMaxTimeLimit);
        Assert.Equal("8b2f6f5e-6e3c-4c5a-8a9b-1c2d3e4f5a6b", info.Properties["cluster"]);
        Assert.Equal(12, info.Properties.Count);
    }

    [Fact]
    public void ToClusterInfo_EmptyOutput_ReturnsEmptyInfo()
    {
        var info = RacOutputParser.ToClusterInfo(string.Empty);

        Assert.Empty(info.Properties);
        Assert.Equal(string.Empty, info.Name);
        Assert.Equal(0, info.Port);
    }

    // ---------- ToInfobaseSummaries ----------

    [Fact]
    public void ToInfobaseSummaries_ParsesSample()
    {
        const string output =
            "infobase\tname\tdescr\tdbms\tdb-server\tdb-name\tdb-user\tlocale\tsecurity-level\tlicensed\n" +
            "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee\tБухгалтерия предприятия\tОсновная база с пробелами\tMSSQLServer\tsql-01\tbuho\tsa\tru\t0\t0\n" +
            "11111111-2222-3333-4444-555555555555\tЗарплата и кадры\t\tPostgreSQL\tpg-01\tzpkor\tpostgres\tru_RU\t1\t1\n";

        var infobases = RacOutputParser.ToInfobaseSummaries(output);

        Assert.Equal(2, infobases.Count);
        Assert.Equal(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"), infobases[0].InfobaseId);
        Assert.Equal("Бухгалтерия предприятия", infobases[0].Name);
        Assert.Equal("Основная база с пробелами", infobases[0].Descr); // кириллица и пробелы
        Assert.Equal("MSSQLServer", infobases[0].Dbms);
        Assert.Equal("sql-01", infobases[0].DbServer);
        Assert.Equal("buho", infobases[0].DbName);
        Assert.Equal("sa", infobases[0].DbUser);
        Assert.Equal("ru", infobases[0].Locale);
        Assert.Equal(0, infobases[0].SecurityLevel);
        Assert.False(infobases[0].Licensed);
        Assert.Equal(Guid.Parse("11111111-2222-3333-4444-555555555555"), infobases[1].InfobaseId);
        Assert.Equal("Зарплата и кадры", infobases[1].Name);
        Assert.Equal(string.Empty, infobases[1].Descr);
        Assert.Equal("PostgreSQL", infobases[1].Dbms);
        Assert.Equal(1, infobases[1].SecurityLevel);
        Assert.True(infobases[1].Licensed);
    }

    [Fact]
    public void ToInfobaseSummaries_ParsesPartialColumns_WithDefaults()
    {
        const string output =
            "infobase\tname\tdescr\n" +
            "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee\tБухгалтерия\tОписание\n";

        var infobase = Assert.Single(RacOutputParser.ToInfobaseSummaries(output));

        Assert.Equal("Бухгалтерия", infobase.Name);
        Assert.Equal("Описание", infobase.Descr);
        Assert.Equal(string.Empty, infobase.Dbms);          // отсутствующие справа — default
        Assert.Equal(string.Empty, infobase.DbServer);
        Assert.Equal(string.Empty, infobase.DbName);
        Assert.Equal(string.Empty, infobase.DbUser);
        Assert.Equal(string.Empty, infobase.Locale);
        Assert.Equal(0, infobase.SecurityLevel);
        Assert.False(infobase.Licensed);
    }

    [Fact]
    public void ToInfobaseSummaries_TwoColumns_MinimalSet()
    {
        const string output =
            "infobase\tname\n" +
            "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee\tБухгалтерия\n";

        var infobase = Assert.Single(RacOutputParser.ToInfobaseSummaries(output));

        Assert.Equal("Бухгалтерия", infobase.Name);
        Assert.Equal(string.Empty, infobase.Descr);
    }

    [Fact]
    public void ToInfobaseSummaries_SkipsHeader_InvalidUuid_AndShortRow()
    {
        const string output =
            "infobase\tname\tdescr\n" +                    // строка заголовка — пропускается
            "не-uuid\tКривой идентификатор\tописание\n" +   // невалидный GUID — пропускается
            "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee\n";       // меньше 2 колонок — пропускается

        Assert.Empty(RacOutputParser.ToInfobaseSummaries(output));
    }

    [Fact]
    public void ToInfobaseSummaries_EmptyOutput_ReturnsEmpty()
    {
        Assert.Empty(RacOutputParser.ToInfobaseSummaries(string.Empty));
        Assert.Empty(RacOutputParser.ToInfobaseSummaries("infobase\tname\tdescr\n"));
    }

    // ---------- ToJobs ----------

    [Fact]
    public void ToJobs_ParsesSample()
    {
        const string output =
            "cluster\tjob\tinfobase\tname\tmethod-name\tpredefined\tschedule\tstate\tstarted-at\tnext-start\tlast-start\tlast-end\tlast-success\tlast-error\tlast-error-descr\tprocess\treplication\tuse-lifetime\tlifetime-period\tlifetime-interval\tlifetime-percentage\tresult\n" +
            "8b2f6f5e-6e3c-4c5a-8a9b-1c2d3e4f5a6b\taaaaaaaa-1111-2222-3333-444455556666\tcccccccc-1111-2222-3333-444455556666\tОбмен данными\tВыполнитьОбмен\t1\t\"0 0 3 * * ? *\"\tscheduled\t2026-09-30T03:00:00\t2026-10-01T03:00:00\t2026-09-30T03:00:00\t2026-09-30T03:10:00\t1\t0\t\t3f2b5d5e-aaaa-bbbb-cccc-ddddeeeeffff\t0\t0\t0\t0\t0\tЗавершено успешно\n";

        var job = Assert.Single(RacOutputParser.ToJobs(output));

        Assert.Equal(Guid.Parse("aaaaaaaa-1111-2222-3333-444455556666"), job.Id);
        Assert.Equal(Guid.Parse("cccccccc-1111-2222-3333-444455556666"), job.InfobaseId);
        Assert.Equal("Обмен данными", job.Name);
        Assert.Equal("ВыполнитьОбмен", job.MethodName);
        Assert.True(job.Predefined);
        Assert.Equal("0 0 3 * * ? *", job.Schedule); // обрамляющие кавычки сняты
        Assert.Equal("scheduled", job.State);
        Assert.Equal(new DateTime(2026, 9, 30, 3, 0, 0), job.StartedAt);
        Assert.Equal(new DateTime(2026, 10, 1, 3, 0, 0), job.NextStart);
        Assert.Equal(new DateTime(2026, 9, 30, 3, 0, 0), job.LastStart);
        Assert.Equal(new DateTime(2026, 9, 30, 3, 10, 0), job.LastEnd);
        Assert.True(job.LastSuccess);
        Assert.False(job.LastError);
        Assert.Equal(string.Empty, job.LastErrorDescr);
        Assert.Equal(Guid.Parse("3f2b5d5e-aaaa-bbbb-cccc-ddddeeeeffff"), job.ProcessId);
        Assert.Equal("Завершено успешно", job.Result);
    }

    [Fact]
    public void ToJobs_UnquotesScheduleAndErrorDescr()
    {
        const string output =
            "cluster\tjob\tinfobase\tname\tmethod-name\tpredefined\tschedule\tstate\tstarted-at\tnext-start\tlast-start\tlast-end\tlast-success\tlast-error\tlast-error-descr\n" +
            "8b2f6f5e-6e3c-4c5a-8a9b-1c2d3e4f5a6b\taaaaaaaa-1111-2222-3333-444455556666\t\ttest\tМетод\t0\t\"0 0 1 * * ? *\"\tpaused\t\t\t\t\t0\t1\t\"Недостаточно прав\"\n";

        var job = Assert.Single(RacOutputParser.ToJobs(output));

        Assert.Equal("0 0 1 * * ? *", job.Schedule);
        Assert.Equal("Недостаточно прав", job.LastErrorDescr);
        Assert.True(job.LastError);
        Assert.False(job.LastSuccess);
    }

    [Fact]
    public void ToJobs_ParsesMinimalColumns_WithDefaults()
    {
        const string output =
            "cluster\tjob\tinfobase\tname\n" +
            "8b2f6f5e-6e3c-4c5a-8a9b-1c2d3e4f5a6b\taaaaaaaa-1111-2222-3333-444455556666\tcccccccc-1111-2222-3333-444455556666\tЗадание\n";

        var job = Assert.Single(RacOutputParser.ToJobs(output));

        Assert.Equal("Задание", job.Name);
        Assert.Equal(string.Empty, job.MethodName); // отсутствующие справа колонки — default
        Assert.False(job.Predefined);
        Assert.Equal(string.Empty, job.Schedule);
        Assert.Equal(string.Empty, job.State);
        Assert.Equal(default, job.NextStart);
        Assert.Equal(default, job.ProcessId);
    }

    [Fact]
    public void ToJobs_EmptyInfobase_IsNull()
    {
        const string output =
            "cluster\tjob\tinfobase\tname\n" +
            "8b2f6f5e-6e3c-4c5a-8a9b-1c2d3e4f5a6b\taaaaaaaa-1111-2222-3333-444455556666\t\tСлужебное задание\n";

        var job = Assert.Single(RacOutputParser.ToJobs(output));

        Assert.Null(job.InfobaseId);
        Assert.Equal("Служебное задание", job.Name);
    }

    [Fact]
    public void ToJobs_SkipsHeader_InvalidUuid_AndShortRow()
    {
        const string output =
            "cluster\tjob\tinfobase\tname\n" +                 // строка заголовка — пропускается
            "не-uuid\tКривой идентификатор\t\tЗадание\n" +      // невалидный GUID — пропускается
            "8b2f6f5e-6e3c-4c5a-8a9b-1c2d3e4f5a6b\n";          // меньше 3 колонок — пропускается

        Assert.Empty(RacOutputParser.ToJobs(output));
    }

    [Fact]
    public void ToJobs_EmptyOutput_ReturnsEmpty()
    {
        Assert.Empty(RacOutputParser.ToJobs(string.Empty));
        Assert.Empty(RacOutputParser.ToJobs("cluster\tjob\tinfobase\tname\n"));
    }

    // ---------- Устойчивость к «кривым» данным ----------

    [Fact]
    public void TypedParsers_NeverThrow_OnGarbageInput()
    {
        // Строки без табуляций, пустые значения и мусор не должны ронять парсеры.
        Assert.Empty(RacOutputParser.ToClusters("garbage\n\n"));
        Assert.Empty(RacOutputParser.ToProcesses("\t\t\t\n"));
        Assert.Empty(RacOutputParser.ToSessions("111-222\t333-444\t\n"));
        Assert.Empty(RacOutputParser.ToConnections("111-222\n"));
        Assert.Empty(RacOutputParser.ToLocks(null!));
        Assert.Empty(RacOutputParser.ToInfobaseSummaries("мусор без табуляций\n"));
        Assert.Empty(RacOutputParser.ToJobs(null!));

        Assert.NotNull(RacOutputParser.ToClusterInfo("только текст без двоеточия\n"));
    }

    // ---------- Key-value блоки (issue #324, новые версии rac) ----------

    [Fact]
    public void ToConnections_ParsesKeyValueBlocks_WithDescr()
    {
        // Реальный вывод 7OH (комментарий 16/18): новые версии rac отдают «connection list»
        // блоками «ключ : значение» — connection/session/blocked/connector/process/host/port/
        // established-at/last-connection-time/duration/descr. «Ключ вместо значения» больше
        // не появляется: descr извлекается в отдельное поле и показывается колонкой.
        const string output =
            "connection                                : 97ec4a09-12a1-4b3c-8f2e-9c0d1e2f3a4b\n" +
            "session                                   : 5b4c3d2e-1111-2222-3333-444455556666\n" +
            "blocked                                   : 0\n" +
            "connector                                 : 1CV8\n" +
            "process                                   : a1b2c3d4-aaaa-bbbb-cccc-ddddeeeeffff\n" +
            "host                                      : WS-USER-01\n" +
            "port                                      : 37105\n" +
            "established-at                            : 2026-10-04T10:15:00\n" +
            "last-connection-time                      : 2026-10-04T10:15:30\n" +
            "duration                                  : 30000\n" +
            "descr                                     : \"1CV8 8.3.27.2214 (клиент, толстый)\"\n";

        var connection = Assert.Single(RacOutputParser.ToConnections(output));

        Assert.Equal(Guid.Parse("97ec4a09-12a1-4b3c-8f2e-9c0d1e2f3a4b"), connection.Id);
        Assert.Equal(Guid.Parse("5b4c3d2e-1111-2222-3333-444455556666"), connection.SessionId);
        Assert.False(connection.Blocked);
        Assert.Equal("1CV8", connection.Connector);
        Assert.Equal("WS-USER-01", connection.Host);
        Assert.Equal(37105, connection.Port);
        Assert.Equal("1CV8 8.3.27.2214 (клиент, толстый)", connection.Descr);
    }

    [Fact]
    public void ToProcesses_ParsesKeyValueBlocks()
    {
        const string output =
            "process                       : 11111111-2222-3333-4444-555566667777\n" +
            "host                          : SRV-1C-01\n" +
            "pid                           : 12345\n" +
            "port                          : 1560\n" +
            "started-at                    : 2026-10-04T06:00:00\n" +
            "memory-size                   : 1048576\n" +
            "memory-total                  : 2097152\n" +
            "memory-available              : 3145728\n" +
            "memory-excess                 : 0\n" +
            "threads                       : 12\n" +
            "cpu                           : 3.5\n" +
            "available-performances        : 100\n" +
            "running                       : 1\n" +
            "infobases                     : 4\n";

        var process = Assert.Single(RacOutputParser.ToProcesses(output));

        Assert.Equal(Guid.Parse("11111111-2222-3333-4444-555566667777"), process.Id);
        Assert.Equal("SRV-1C-01", process.Host);
        Assert.Equal(12345, process.Pid);
        Assert.Equal(1560, process.Port);
        Assert.Equal(12, process.Threads);
        Assert.Equal(3.5, process.Cpu, precision: 3);
        Assert.True(process.Running);
        Assert.Equal(4, process.Infobases);
    }

    [Fact]
    public void ToSessions_ParsesKeyValueBlocks()
    {
        const string output =
            "session           : 22222222-3333-4444-5555-666677778888\n" +
            "infobase          : 99999999-aaaa-bbbb-cccc-ddddeeeeffff\n" +
            "user-name         : \"Иванов Иван\"\n" +
            "host              : WS-USER-01\n" +
            "app-id            : 1CV8\n" +
            "started-at        : 2026-10-04T08:00:00\n" +
            "last-active-at    : 2026-10-04T09:00:00\n" +
            "blocked-by-ls     : 0\n" +
            "blocked-by-deadlock : 0\n" +
            "duration-all      : 3600000\n" +
            "memory            : 268435456\n" +
            "connection        : 97ec4a09-12a1-4b3c-8f2e-9c0d1e2f3a4b\n" +
            "hibernate         : 0\n" +
            "state             : active\n";

        var session = Assert.Single(RacOutputParser.ToSessions(output));

        Assert.Equal(Guid.Parse("22222222-3333-4444-5555-666677778888"), session.Id);
        Assert.Equal(Guid.Parse("99999999-aaaa-bbbb-cccc-ddddeeeeffff"), session.InfobaseId);
        Assert.Equal("Иванов Иван", session.User);
        Assert.Equal("1CV8", session.AppId);
        Assert.Equal("active", session.State);
        Assert.Equal(3_600_000, session.DurationAll);
    }

    [Fact]
    public void ToLocks_ParsesKeyValueBlocks()
    {
        const string output =
            "lock              : 33333333-4444-5555-6666-777788889999\n" +
            "session           : 22222222-3333-4444-5555-666677778888\n" +
            "infobase          : 99999999-aaaa-bbbb-cccc-ddddeeeeffff\n" +
            "connection        : 97ec4a09-12a1-4b3c-8f2e-9c0d1e2f3a4b\n" +
            "transaction       : aaaabbbb-cccc-dddd-eeee-ffff00001111\n" +
            "waiting           : 0\n" +
            "blocking          : 1\n" +
            "object            : \"Справочник.Контрагенты\"\n";

        var lockRow = Assert.Single(RacOutputParser.ToLocks(output));

        Assert.Equal(Guid.Parse("33333333-4444-5555-6666-777788889999"), lockRow.Id);
        Assert.True(lockRow.Blocking);
        Assert.False(lockRow.Waiting);
        Assert.Equal("Справочник.Контрагенты", lockRow.Object);
    }

    [Fact]
    public void ToInfobaseSummaries_ParsesKeyValueBlocks()
    {
        const string output =
            "infobase           : 99999999-aaaa-bbbb-cccc-ddddeeeeffff\n" +
            "name               : \"Бухгалтерия предприятия\"\n" +
            "descr              : \"Рабочая база\"\n" +
            "dbms               : MSSQLServer\n" +
            "db-server          : SQL-SRV-01\n" +
            "db-name            : base_1c\n" +
            "db-user            : 1c_user\n" +
            "locale             : ru_RU\n" +
            "security-level     : 0\n" +
            "licensed           : 1\n";

        var infobase = Assert.Single(RacOutputParser.ToInfobaseSummaries(output));

        Assert.Equal(Guid.Parse("99999999-aaaa-bbbb-cccc-ddddeeeeffff"), infobase.InfobaseId);
        Assert.Equal("Бухгалтерия предприятия", infobase.Name);
        Assert.Equal("Рабочая база", infobase.Descr);
        Assert.Equal("MSSQLServer", infobase.Dbms);
        Assert.True(infobase.Licensed);
    }

    [Fact]
    public void ToJobs_ParsesKeyValueBlocks()
    {
        const string output =
            "job                  : 44444444-5555-6666-7777-88889999aaaa\n" +
            "infobase             : 99999999-aaaa-bbbb-cccc-ddddeeeeffff\n" +
            "name                 : \"Обмен с банком\"\n" +
            "method-name          : ОбменСБанком.Выполнить\n" +
            "predefined           : 1\n" +
            "schedule             : \"Повторяющийся день в течение дня\"\n" +
            "state                : scheduled\n" +
            "next-start           : 2026-10-04T12:00:00\n" +
            "last-success         : 1\n" +
            "last-error           : 0\n" +
            "process              : 11111111-2222-3333-4444-555566667777\n";

        var job = Assert.Single(RacOutputParser.ToJobs(output));

        Assert.Equal(Guid.Parse("44444444-5555-6666-7777-88889999aaaa"), job.Id);
        Assert.Equal(Guid.Parse("99999999-aaaa-bbbb-cccc-ddddeeeeffff"), job.InfobaseId);
        Assert.Equal("Обмен с банком", job.Name);
        Assert.Equal("scheduled", job.State);
        Assert.True(job.Predefined);
        Assert.True(job.LastSuccess);
    }

    [Fact]
    public void ParseKeyValueBlocks_PublicWrapper_ReturnsBlocks()
    {
        const string output =
            "connection : aaaa\n" +
            "descr      : bbbb\n" +
            "connection : cccc\n" +
            "descr      : dddd\n";

        var blocks = RacOutputParser.ParseKeyValueBlocks(output, "connection");

        Assert.Equal(2, blocks.Count);
        Assert.Equal("aaaa", blocks[0]["connection"]);
        Assert.Equal("bbbb", blocks[0]["descr"]);
        Assert.Equal("cccc", blocks[1]["connection"]);
    }

    [Fact]
    public void ToConnections_ParsesRac854Blocks()
    {
        // Реальный вывод rac 8.5.4.1878 (issue #324, connection_list.log 7OH):
        // блоки «ключ : значение» со схемой connection/conn-id/host/process/infobase/
        // application/connected-at/session-number/blocked-by-ls — ключи НЕ совпадают с
        // прежней схемой (session/blocked/connector/established-at), читаются алиасами.
        const string output =
            "connection     : eb58bad9-537c-4bf5-b6b7-86969badb834\n" +
            "conn-id        : 0\n" +
            "host           : ALF\n" +
            "process        : e8fd61c0-2028-41fc-8b9c-0b33199fa8ba\n" +
            "infobase       : 00000000-0000-0000-0000-000000000000\n" +
            "application    : \"JobScheduler\"\n" +
            "connected-at   : 2026-10-07T12:03:36\n" +
            "session-number : 0\n" +
            "blocked-by-ls  : 0\n" +
            "\n" +
            "connection     : 583c6b7c-dac3-4613-a225-52e8aec45352\n" +
            "conn-id        : 0\n" +
            "host           : ALF\n" +
            "process        : e8fd61c0-2028-41fc-8b9c-0b33199fa8ba\n" +
            "infobase       : 00000000-0000-0000-0000-000000000000\n" +
            "application    : \"AgentStandardCall\"\n" +
            "connected-at   : 2026-10-07T12:03:39\n" +
            "session-number : 0\n" +
            "blocked-by-ls  : 0\n";

        var connections = RacOutputParser.ToConnections(output);

        Assert.Equal(2, connections.Count);

        var first = connections[0];
        Assert.Equal(Guid.Parse("eb58bad9-537c-4bf5-b6b7-86969badb834"), first.Id);
        Assert.Equal("ALF", first.Host);
        Assert.Equal(Guid.Parse("e8fd61c0-2028-41fc-8b9c-0b33199fa8ba"), first.ProcessId);
        Assert.False(first.Blocked);              // blocked-by-ls = 0
        Assert.Equal("JobScheduler", first.Connector);  // application → алиас
        Assert.Equal("JobScheduler", first.Descr);
        Assert.Equal(new DateTime(2026, 10, 7, 12, 3, 36), first.EstablishedAt); // connected-at → алиас
        Assert.Equal(Guid.Empty, first.SessionId); // session-number не GUID
        Assert.Equal(0, first.Port);

        Assert.Equal("AgentStandardCall", connections[1].Connector);
    }

    [Fact]
    public void ToLocks_ParsesRac854BlocksStartingWithConnection()
    {
        // Реальный вывод rac 8.5.4.1878 (issue #324, комментарий 7OH): блоки lock list
        // стартуют строкой «connection : GUID» (как у connection list), ключи
        // connection/session/object/locked/descr.
        const string output =
            "connection : 00000000-0000-0000-0000-000000000000\n" +
            "session    : 00000000-0000-0000-0000-000000000000\n" +
            "object     : 00000000-0000-0000-0000-000000000000\n" +
            "locked     : 2026-10-04T11:48:12\n" +
            "descr      : \"Менеджер кластера(ALF,27541,0)\"\n" +
            "\n" +
            "connection : 098cd8e9-28c1-47b0-bf54-c09f66b7e093\n" +
            "session    : 00000000-0000-0000-0000-000000000000\n" +
            "object     : 00000000-0000-0000-0000-000000000000\n" +
            "locked     : 2026-10-04T11:48:16\n" +
            "descr      : \"Соединение(ServerJobExecutorContext,ALF,JobScheduler)\"\n";

        var locks = RacOutputParser.ToLocks(output);

        Assert.Equal(2, locks.Count);
        Assert.Equal(Guid.Empty, locks[0].Id); // uuid блокировки в выводе 8.5.4 отсутствует
        Assert.Equal("Менеджер кластера(ALF,27541,0)", locks[0].Object);
        Assert.Equal(Guid.Parse("098cd8e9-28c1-47b0-bf54-c09f66b7e093"), locks[1].Id);
        Assert.Equal("Соединение(ServerJobExecutorContext,ALF,JobScheduler)", locks[1].Object);
        Assert.Equal(Guid.Empty, locks[0].SessionId);
    }

    [Fact]
    public void ToLocks_IgnoresConnectionListBlocks()
    {
        // Блоки connection list (connection/conn-id/host/application/...) не содержат
        // маркерных ключей object/locked — в блокировки они не попадают.
        const string output =
            "connection     : eb58bad9-537c-4bf5-b6b7-86969badb834\n" +
            "conn-id        : 0\n" +
            "host           : ALF\n" +
            "process        : e8fd61c0-2028-41fc-8b9c-0b33199fa8ba\n" +
            "application    : \"JobScheduler\"\n";

        Assert.Empty(RacOutputParser.ToLocks(output));
    }

    [Fact]
    public void ToClusters_ParsesExactCommentOutput()
    {
        // Точный вывод «rac.exe localhost:27545 cluster list» из комментария 7OH
        // (0.3.9.300): key-value с пустыми полями (restart-schedule/security-profile-name)
        // и именем кластера в кавычках — «ключ вместо значения» (issue #324, 0.3.9.319).
        const string output =
            "cluster                                   : cbc95ef0-99c9-4b1a-909f-cff4c8de61d9\n" +
            "host                                      : ALF\n" +
            "port                                      : 27541\n" +
            "name                                      : \"Локальный кластер\"\n" +
            "expiration-timeout                        : 60\n" +
            "lifetime-limit                            : 0\n" +
            "max-memory-size                           : 0\n" +
            "max-memory-time-limit                     : 0\n" +
            "security-level                            : 0\n" +
            "session-fault-tolerance-level             : 0\n" +
            "load-balancing-mode                       : performance\n" +
            "errors-count-threshold                    : 0\n" +
            "kill-problem-processes                    : 1\n" +
            "kill-by-memory-with-dump                  : 0\n" +
            "allow-access-right-audit-events-recording : 0\n" +
            "ping-period                               : 0\n" +
            "ping-timeout                              : 0\n" +
            "restart-schedule                          :\n" +
            "security-profile-name                     :\n" +
            "max-auth-attempts                         : 10\n" +
            "auth-lock-duration                        : 900\n";

        var cluster = Assert.Single(RacOutputParser.ToClusters(output));

        Assert.Equal(Guid.Parse("cbc95ef0-99c9-4b1a-909f-cff4c8de61d9"), cluster.Id);
        Assert.Equal("Локальный кластер", cluster.Name);
        Assert.Equal(27541, cluster.Port);
    }

    [Fact]
    public void LooksLikeKeyValueOutput_DetectsBlocksVsTable()
    {
        // Строка вида «ключ : значение» без табуляций — это блоки.
        Assert.True(RacOutputParser.LooksLikeKeyValueOutput("connection : aaaa\n"));
        // Табличный вывод с табуляцией — НЕ блоки.
        Assert.False(RacOutputParser.LooksLikeKeyValueOutput("cluster\tname\tport\n"));
        Assert.False(RacOutputParser.LooksLikeKeyValueOutput(null));
        Assert.False(RacOutputParser.LooksLikeKeyValueOutput(string.Empty));
    }

    [Fact]
    public void ToClusters_EmptyName_DoesNotExposeKeyInsteadOfValue()
    {
        // issue #324 («ключ вместо названия»): в key-value блоке rac может вернуть
        // ключ «name» без значения — парсер не должен подставлять сам ключ в Name;
        // за отображение пустого имени отвечает RacClusterRow.DisplayText (fallback).
        const string output =
            "cluster                                   : cbc95ef0-99c9-4b1a-909f-cff4c8de61d9\n" +
            "host                                      : ALF\n" +
            "port                                      : 27541\n" +
            "name                                      :\n";

        var cluster = Assert.Single(RacOutputParser.ToClusters(output));

        Assert.Equal(Guid.Parse("cbc95ef0-99c9-4b1a-909f-cff4c8de61d9"), cluster.Id);
        Assert.Equal(27541, cluster.Port);
        Assert.Equal(string.Empty, cluster.Name);
    }

    [Fact]
    public void ToJobs_ParsesKeyValueBlocks_ForRac854StyleOutput()
    {
        // Фактический вывод «job list --cluster <uuid>» на rac 8.5.4 (issue #324)
        // приходит блоками «ключ : значение» со стартером «job : GUID»; имя задания —
        // в «name», ключ-инфобейза — в «infobase». Разбор не должен давать 0 строк
        // (иначе EnsureParsedOrThrow остановит автообновление).
        const string output =
            "job                                       : 1a2b3c4d-0000-0000-0000-000000000001\n" +
            "name                                      : \"Обновление информационной базы\"\n" +
            "infobase                                  : 1a2b3c4d-0000-0000-0000-0000000000ab\n" +
            "method-name                               : \"ОбновлениеИнформационнойБазы\"\n" +
            "predefined                                : 0\n" +
            "schedule                                  : \"{\\\"frequency\\\":\\\"daily\\\"}\"\n" +
            "state                                     : \"scheduled\"\n";

        var job = Assert.Single(RacOutputParser.ToJobs(output));

        Assert.Equal(Guid.Parse("1a2b3c4d-0000-0000-0000-000000000001"), job.Id);
        Assert.Equal(Guid.Parse("1a2b3c4d-0000-0000-0000-0000000000ab"), job.InfobaseId);
        Assert.Equal("Обновление информационной базы", job.Name);
        Assert.Equal("ОбновлениеИнформационнойБазы", job.MethodName);
    }

    // ---------- issue #324 (лог 7OH от 2026-10-08 23:26): exit=0, stdout>0, вкладки пусты ----------
    // rac 8.5.4.1878 переименовывает ключи схем list-команд (у connection list — «conn-id»
    // вместо «session» и т.п.); если стартер блока не «process»/«session»/«infobase»/«job»,
    // а их «-id»-вариант, парсер раньше давал 0 строк и EnsureParsedOrThrow опустошал ВСЕ
    // вкладки. Тесты фиксируют разбор схем со стартерами-алиасами и выравниванием пробелами.

    [Fact]
    public void ToProcesses_ParsesKeyValueBlocks_WithProcessIdStarter()
    {
        // Схема с выравниванием пробелами (как в реальных логах 8.5.4.1878) и стартером
        // «process-id : GUID» вместо «process : GUID» — раньше 0 строк → пустая вкладка.
        const string output =
            "process-id                             : 6d29b3a6-95a1-4a3e-9c34-cf2f830a4b47\n" +
            "host                                   : ALF\n" +
            "port                                   : 27560\n" +
            "pid                                    : 8412\n" +
            "started-at                             : 2026-10-08T21:50:11\n" +
            "memory-size                            : 205127680\n" +
            "memory-total                           : 3089715200\n" +
            "memory-available                       : 2884587520\n" +
            "memory-excess                          : 0\n" +
            "threads                                : 60\n" +
            "cpu                                    : 0\n" +
            "available-performances                 : 1000\n" +
            "running                                : 1\n" +
            "infobases                              : 2\n";

        var process = Assert.Single(RacOutputParser.ToProcesses(output));

        Assert.Equal(Guid.Parse("6d29b3a6-95a1-4a3e-9c34-cf2f830a4b47"), process.Id);
        Assert.Equal("ALF", process.Host);
        Assert.Equal(27560, process.Port);
        Assert.Equal(8412, process.Pid);
        Assert.Equal(60, process.Threads);
        Assert.True(process.Running);
        Assert.Equal(2, process.Infobases);
    }

    [Fact]
    public void ToProcesses_NumericProcessIdInsideBlock_DoesNotSplitBlock()
    {
        // Обратный вариант: стартер «process», а «process-id : 1234» (число) — ВНУТРИ
        // блока. Числовой process-id не должен разрывать блок и должен читаться как pid.
        const string output =
            "process                                : 6d29b3a6-95a1-4a3e-9c34-cf2f830a4b47\n" +
            "process-id                             : 8412\n" +
            "host                                   : ALF\n" +
            "port                                   : 27560\n" +
            "running                                : 1\n" +
            "\n" +
            "process                                : 7d29b3a6-95a1-4a3e-9c34-cf2f830a4b48\n" +
            "process-id                             : 8413\n" +
            "host                                   : ALF\n" +
            "port                                   : 27561\n" +
            "running                                : 0\n";

        var processes = RacOutputParser.ToProcesses(output);

        Assert.Equal(2, processes.Count);
        Assert.Equal(Guid.Parse("6d29b3a6-95a1-4a3e-9c34-cf2f830a4b47"), processes[0].Id);
        Assert.Equal(8412, processes[0].Pid);
        Assert.Equal(27560, processes[0].Port);
        Assert.True(processes[0].Running);
        Assert.Equal(Guid.Parse("7d29b3a6-95a1-4a3e-9c34-cf2f830a4b48"), processes[1].Id);
        Assert.Equal(8413, processes[1].Pid);
        Assert.False(processes[1].Running);
    }

    [Fact]
    public void ToSessions_ParsesKeyValueBlocks_WithSessionIdStarter()
    {
        // Схема «session-id : GUID» со стартером-алиасом (как «conn-id» у connection
        // list 8.5.4.1878) — раньше 0 строк → пустая вкладка сеансов.
        const string output =
            "session-id                             : 9a2b3c4d-0000-0000-0000-0000000000aa\n" +
            "infobase                               : 1a2b3c4d-0000-0000-0000-0000000000ab\n" +
            "user-name                              : \"Иванов\"\n" +
            "host                                   : ALF\n" +
            "app-id                                 : \"Designer\"\n" +
            "started-at                             : 2026-10-08T23:20:00\n" +
            "last-active-at                         : 2026-10-08T23:26:00\n" +
            "blocked-by-ls                          : 0\n" +
            "hibernate                              : 0\n" +
            "state                                  : \"Normal\"\n";

        var session = Assert.Single(RacOutputParser.ToSessions(output));

        Assert.Equal(Guid.Parse("9a2b3c4d-0000-0000-0000-0000000000aa"), session.Id);
        Assert.Equal(Guid.Parse("1a2b3c4d-0000-0000-0000-0000000000ab"), session.InfobaseId);
        Assert.Equal("Иванов", session.User);
        Assert.Equal("Designer", session.AppId);
        Assert.Equal("Normal", session.State);
    }

    [Fact]
    public void ToInfobaseSummaries_ParsesKeyValueBlocks_WithInfobaseIdStarter()
    {
        const string output =
            "infobase-id                            : 1a2b3c4d-0000-0000-0000-0000000000ab\n" +
            "name                                   : \"BUH3\"\n" +
            "descr                                  : \"Бухгалтерия\"\n" +
            "dbms                                   : MSSQLServer\n" +
            "locale                                 : ru_RU\n" +
            "security-level                         : 0\n" +
            "licensed                               : 0\n";

        var infobase = Assert.Single(RacOutputParser.ToInfobaseSummaries(output));

        Assert.Equal(Guid.Parse("1a2b3c4d-0000-0000-0000-0000000000ab"), infobase.InfobaseId);
        Assert.Equal("BUH3", infobase.Name);
        Assert.Equal("Бухгалтерия", infobase.Descr);
        Assert.Equal("MSSQLServer", infobase.Dbms);
    }

    [Fact]
    public void ToJobs_ParsesKeyValueBlocks_WithJobIdStarter()
    {
        const string output =
            "job-id                                 : 1a2b3c4d-0000-0000-0000-000000000001\n" +
            "name                                   : \"Обновление информационной базы\"\n" +
            "infobase                               : 1a2b3c4d-0000-0000-0000-0000000000ab\n" +
            "state                                  : \"scheduled\"\n";

        var job = Assert.Single(RacOutputParser.ToJobs(output));

        Assert.Equal(Guid.Parse("1a2b3c4d-0000-0000-0000-000000000001"), job.Id);
        Assert.Equal("Обновление информационной базы", job.Name);
        Assert.Equal("scheduled", job.State);
    }

    [Fact]
    public void ToJobs_UsageHelpOutput_ReturnsEmpty()
    {
        // issue #324 (0.3.10.3): rac 8.5.4.1878 на «job list <uuid>» может вернуть
        // справку об использовании («Использование: rac …» вместо данных). Парсер
        // не должен извлечь из неё ни одной строки заданий — сигналом смены формата
        // занимается RacClient.LooksLikeUsageHelp (повтор с альтернативным синтаксисом).
        const string help =
            "Использование: rac [режим] [команда] [параметры]\n" +
            "Команды: cluster, infobase, session, connection, process, lock, job\n" +
            "Пример: rac localhost:1540 job list --cluster=<uuid>\n";

        Assert.Empty(RacOutputParser.ToJobs(help));
    }

    [Fact]
    public void ToJobs_Rac85KeyValueSchema_InfobaseNameAndMethodAreRead()
    {
        // «Новый формат job list 8.5»: блоки «ключ : значение» со схемой 8.5
        // (name/method-name в кавычках, пустой infobase) ложатся в модель.
        const string output =
            "job        : 1a2b3c4d-0000-0000-0000-000000000002\n" +
            "infobase   : \n" +
            "name       : \"Полный индексно-пересчетный расчет\"\n" +
            "method-name: \"Common.ИндексацияСсылок\"\n" +
            "predefined : 1\n" +
            "schedule   : \"* * * * *\"\n" +
            "state      : scheduled\n" +
            "next-start : 2026-10-09T21:00:00\n";

        var job = Assert.Single(RacOutputParser.ToJobs(output));

        Assert.Equal(Guid.Parse("1a2b3c4d-0000-0000-0000-000000000002"), job.Id);
        Assert.Null(job.InfobaseId);
        Assert.Equal("Полный индексно-пересчетный расчет", job.Name);
        Assert.Equal("Common.ИндексацияСсылок", job.MethodName);
        Assert.True(job.Predefined);
        Assert.Equal("scheduled", job.State);
        Assert.Equal(new DateTime(2026, 10, 9, 21, 0, 0), job.NextStart);
    }
}