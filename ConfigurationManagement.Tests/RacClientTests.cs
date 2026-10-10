using System.Text;
using Configuration_Management.Models;
using Configuration_Management.Services;
using Xunit;

namespace ConfigurationManagement.Tests;

/// <summary>
/// Тесты сборки аргументов командной строки rac (<see cref="RacClient.BuildArguments"/>)
/// и маскирования пароля (<see cref="SensitiveDataMasker.MaskRacPassword"/>) —
/// без запуска реального процесса rac.
/// Точка подключения собирается ЕДИНЫМ токеном «host:port» первым аргументом
/// (rac.exe host:port cluster list): раздельные --host=/--port= не разбираются
/// новыми версиями платформы (issue #324).
/// </summary>
public sealed class RacClientTests
{
    private static RacConnectionParams Params(
        string address = "localhost", int port = 1540,
        string user = "Admin", string password = "secret")
    {
        return new RacConnectionParams
        {
            Address = address,
            Port = port,
            User = user,
            Password = password
        };
    }

    [Fact]
    public void BuildArguments_IncludesConnectionParams_ThenCommand()
    {
        var args = RacClient.BuildArguments(Params(), "cluster", "list");

        Assert.Equal(new[]
        {
            "localhost:1540",
            "--user=Admin",
            "--password=secret",
            "cluster",
            "list"
        }, args);
    }

    [Fact]
    public void BuildArguments_HostAndPort_AreSingleToken()
    {
        // Ключевой формат issue #324: rac.exe localhost:1545 cluster list.
        var args = RacClient.BuildArguments(
            new RacConnectionParams { Address = "srv1", Port = 1545 }, "cluster", "list");

        Assert.Equal("srv1:1545", args[0]);
        Assert.DoesNotContain(args, a => a.StartsWith("--host="));
        Assert.DoesNotContain(args, a => a.StartsWith("--port="));
        Assert.Equal(new[] { "srv1:1545", "cluster", "list" }, args);
    }

    [Fact]
    public void BuildArguments_OmitsEmptyUserAndPassword()
    {
        var args = RacClient.BuildArguments(
            new RacConnectionParams { Address = "srv1", Port = 1545 }, "cluster", "list");

        Assert.Equal(new[] { "srv1:1545", "cluster", "list" }, args);
    }

    [Fact]
    public void BuildArguments_EmptyAddress_OmitsConnectionToken()
    {
        var args = RacClient.BuildArguments(
            new RacConnectionParams { Address = "   ", Port = 1540, User = "Admin" },
            "cluster", "list");

        Assert.Equal(new[] { "--user=Admin", "cluster", "list" }, args);
    }

    [Fact]
    public void BuildArguments_ZeroPort_AddressIsPlainHost()
    {
        // Порт ≤ 0: rac.exe host cluster list — без «:port».
        var args = RacClient.BuildArguments(Params(port: 0), "cluster", "list");

        Assert.Equal("localhost", args[0]);
        Assert.DoesNotContain(args, a => a.StartsWith("--port="));
        Assert.DoesNotContain(args, a => a.StartsWith("--host="));
        Assert.Equal(1540, IRacClient.DefaultPort);
    }

    [Fact]
    public void BuildArguments_ClusterList_HasNoClusterOption()
    {
        var args = RacClient.BuildArguments(Params(), "cluster", "list");

        Assert.DoesNotContain(args, a => a.StartsWith("--cluster="));
    }

    [Fact]
    public void BuildArguments_ProcessList_IncludesClusterOption()
    {
        var clusterId = Guid.NewGuid();
        var args = RacClient.BuildArguments(Params(), "process", "list", $"--cluster={clusterId}");

        Assert.Contains($"--cluster={clusterId}", args);
        Assert.Equal("--cluster=" + clusterId, args[^1]);
    }

    [Fact]
    public void BuildArguments_ClusterInfo_IncludesHostPortToken_AndClusterOption()
    {
        // Команда «cluster info» (данные кластера на вкладке «Информация о кластере»,
        // issue #324): точка подключения одним токеном host:port + --cluster=<uuid>.
        var clusterId = Guid.Parse("8b2f6f5e-6e3c-4c5a-8a9b-1c2d3e4f5a6b");
        var args = RacClient.BuildArguments(
            new RacConnectionParams { Address = "srv1", Port = 1540, User = "Admin", Password = "secret" },
            "cluster", "info", $"--cluster={clusterId}");

        Assert.Equal(new[]
        {
            "srv1:1540",
            "--user=Admin",
            "--password=secret",
            "cluster",
            "info",
            "--cluster=" + clusterId
        }, args);
        Assert.DoesNotContain(args, a => a.StartsWith("--host="));
        Assert.DoesNotContain(args, a => a.StartsWith("--port="));
    }

    [Fact]
    public void BuildArguments_KeepsValuesWithSpacesAsSingleToken()
    {
        // Адрес, пароль и логин с пробелами передаются одним токеном (ArgumentList без shell).
        var args = RacClient.BuildArguments(
            new RacConnectionParams
            {
                Address = "srv 1",
                Port = 1540,
                User = "Иванов",
                Password = "пароль с пробелами"
            },
            "session", "list");

        Assert.Contains("srv 1:1540", args);
        Assert.Contains("--user=Иванов", args);
        Assert.Contains("--password=пароль с пробелами", args);
        Assert.DoesNotContain(args, a => a.StartsWith("--host="));
    }

    [Fact]
    public void MaskRacPassword_HidesPasswordValue()
    {
        // Формат журнала после перехода на единый токен «host:port»: пароль по-прежнему
        // передаётся как --password=... и маскируется целиком.
        const string line = "localhost:1540 --user=Admin --password=secret cluster list";
        var masked = SensitiveDataMasker.MaskRacPassword(line);

        Assert.DoesNotContain("secret", masked);
        Assert.Contains("--password=***", masked);
        // Точка подключения host:port и остальные аргументы не затрагиваются.
        Assert.Contains("localhost:1540", masked);
        Assert.Contains("--user=Admin", masked);
    }

    [Fact]
    public void MaskRacPassword_LeavesOtherArguments_AndNull()
    {
        const string line = "srv1:1540 cluster list";

        Assert.Equal(line, SensitiveDataMasker.MaskRacPassword(line));
        Assert.Null(SensitiveDataMasker.MaskRacPassword(null!));
    }

    [Fact]
    public void BuildArguments_JobList_IncludesClusterOption()
    {
        var clusterId = Guid.NewGuid();
        var args = RacClient.BuildArguments(Params(), "job", "list", $"--cluster={clusterId}");

        Assert.Contains($"--cluster={clusterId}", args);
        Assert.Equal("localhost:1540", args[0]);
        Assert.Equal("--cluster=" + clusterId, args[^1]);
    }

    [Theory]
    [InlineData("pause")]
    [InlineData("resume")]
    [InlineData("disable")]
    [InlineData("enable")]
    public void BuildArguments_SetJobState_IncludesCommandAndOptions(string expectedSubcommand)
    {
        var clusterId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var args = RacClient.BuildArguments(
            Params(), "job", expectedSubcommand, $"--cluster={clusterId}", $"--job={jobId}");

        Assert.Equal(new[]
        {
            "localhost:1540",
            "--user=Admin",
            "--password=secret",
            "job",
            expectedSubcommand,
            "--cluster=" + clusterId,
            "--job=" + jobId
        }, args);
    }

    // ---------- DecodeRacOutput (кодировка вывода rac, issue #324) ----------

    [Fact]
    public void DecodeRacOutput_Utf8_Passthrough()
    {
        // Linux/Avalonia: rac выводит UTF-8 — строгий UTF-8 декодируется как есть.
        var bytes = Encoding.UTF8.GetBytes("cluster\tname\n111-222\tЛокальный кластер\n");

        Assert.Equal("cluster\tname\n111-222\tЛокальный кластер\n",
            RacClient.DecodeRacOutput(bytes));
    }

    [Fact]
    public void DecodeRacOutput_Cp866_RussianText_IsDecoded()
    {
        // Windows: rac пишет в OEM-кодовой странице (cp866) — байты невалидны как UTF-8,
        // fallback должен декодировать их без символов замены. Провайдер кодовых страниц
        // регистрируется здесь, чтобы тест не зависел от порядка выполнения.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var bytes = Encoding.GetEncoding(866).GetBytes(
            "cluster\tname\n111-222\tЛокальный кластер\n");

        var decoded = RacClient.DecodeRacOutput(bytes);

        Assert.Contains("Локальный кластер", decoded);
        Assert.DoesNotContain("\uFFFD", decoded);
    }

    [Fact]
    public void DecodeRacOutput_SingleByteWindowsText_DecodesWithoutReplacementChars()
    {
        // Однобайтовые кодовые страницы (cp866 приоритетно, cp1251 резерв) не дают
        // символов замены U+FFFD и сохраняют ASCII-часть (цифры, GUID, ключи колонок —
        // то, что парсеру нужно). Приоритет cp866 соответствует OEM-кодировке консоли
        // Windows; точное различие 866/1251 без образца вывода недостижимо, поэтому
        // тест фиксирует именно гарантию отсутствия потерь при fallback (issue #324).
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var bytes = Encoding.GetEncoding(1251).GetBytes("Имя: Тестовый кластер 1С");

        var decoded = RacClient.DecodeRacOutput(bytes);

        Assert.DoesNotContain("\uFFFD", decoded);
        // ASCII-фрагмент (« 1» перед «С») сохраняется в любой однобайтовой кодировке.
        Assert.Contains(" 1", decoded);
    }

    [Fact]
    public void DecodeRacOutput_EmptyAndNull_ReturnEmpty()
    {
        Assert.Equal(string.Empty, RacClient.DecodeRacOutput(Array.Empty<byte>()));
        Assert.Equal(string.Empty, RacClient.DecodeRacOutput(null!));
    }

    // ---------- EnsureParsedOrThrow (issue #324: непустой нераспознанный вывод = ошибка) ----------

    [Fact]
    public void EnsureParsedOrThrow_NonEmptyUnparsedOutput_ThrowsRacOutputParseException()
    {
        // rac завершился с кодом 0 и вернул НЕпустой вывод, но ни табличный разбор, ни блоки
        // «ключ : значение» не дали ни одной строки данных — новая версия формата. Монитор
        // должен показать понятную ошибку и ОСТАНОВИТЬ автообновление (без повторов каждые 5 с).
        var ex = Assert.Throws<RacOutputParseException>(
            () => RacClient.EnsureParsedOrThrow("some future format output\n", 0, "process list"));

        Assert.Contains("process list", ex.Message);
    }

    [Fact]
    public void EnsureParsedOrThrow_EmptyOutput_DoesNotThrow()
    {
        // Пустой вывод — легитимный случай «данных нет» (регресс): исключения быть не должно.
        RacClient.EnsureParsedOrThrow(string.Empty, 0, "process list");
        RacClient.EnsureParsedOrThrow(null!, 0, "process list");
        RacClient.EnsureParsedOrThrow("   \n\t ", 0, "process list");
    }

    [Fact]
    public void EnsureParsedOrThrow_ParsedRows_DoesNotThrow()
    {
        // Разбор дал строки данных — правило не срабатывает даже при непустом выводе.
        RacClient.EnsureParsedOrThrow("cluster\tname\n", 1, "process list");
    }

    // ---------- job list: форматы и ключ кэша (issue #324) ----------

    [Fact]
    public void JobListArgs_Format0_UsesEqualsToken()
    {
        var clusterId = Guid.Parse("cbc95ef0-99c9-4b1a-909f-cff4c8de61d9");

        Assert.Equal(new[] { "job", "list", $"--cluster={clusterId}" }, RacClient.JobListArgs(0, clusterId));
    }

    [Fact]
    public void JobListArgs_Format1_UsesTwoTokens()
    {
        // rac 8.5.4.1878 отклоняет формат 0 (код -1, «Ошибка разбора параметра»);
        // формат 1 — два токена «--cluster <uuid>» (issue #324).
        var clusterId = Guid.Parse("cbc95ef0-99c9-4b1a-909f-cff4c8de61d9");

        Assert.Equal(new[] { "job", "list", "--cluster", clusterId.ToString() }, RacClient.JobListArgs(1, clusterId));
    }

    [Fact]
    public void JobListArgs_Format2_UsesPositionalUuid()
    {
        // По логам 2026-10-07/08 rac 8.5.4.1878 отклоняет ОБА варианта «--cluster»
        // («Ошибка разбора параметра: --cluster») — добавлен формат 2: позиционный
        // uuid без имени параметра (issue #324, 0.3.9.330).
        var clusterId = Guid.Parse("cbc95ef0-99c9-4b1a-909f-cff4c8de61d9");

        Assert.Equal(new[] { "job", "list", clusterId.ToString() }, RacClient.JobListArgs(2, clusterId));
    }

    [Fact]
    public void JobListFormatCount_CoversAllFormats()
    {
        Assert.Equal(3, RacClient.JobListFormatCount);
        // Имена форматов — для журнала; должны отличаться между собой.
        Assert.Equal(
            new[] { "--cluster=<uuid>", "--cluster <uuid>", "<uuid>" },
            new[]
            {
                RacClient.JobListFormatName(0),
                RacClient.JobListFormatName(1),
                RacClient.JobListFormatName(2)
            });
    }

    [Fact]
    public void JobListFormatKey_IncludesConnectionAndCluster_NotPassword()
    {
        // Ключ кэша формата: точка подключения + учётная запись + кластер; пароль
        // не должен попадать в ключ (секреты не хранятся в словаре/логах).
        var clusterId = Guid.Parse("cbc95ef0-99c9-4b1a-909f-cff4c8de61d9");
        var parameters = new RacConnectionParams
        {
            Address = "srv1",
            Port = 1545,
            User = "Admin",
            Password = "secret-password"
        };

        var key = RacClient.JobListFormatKey(parameters, clusterId);

        Assert.Contains("srv1:1545", key);
        Assert.Contains("Admin", key);
        Assert.Contains(clusterId.ToString(), key);
        Assert.DoesNotContain("secret-password", key);
    }

    // ============ cluster info из кэша cluster list (issue #324) ============

    private const string TwoClustersKvOutput = """
        cluster : cbc95ef0-99c9-4b1a-909f-cff4c8de61d9
        host : ALF
        port : 27541
        name : "Локальный кластер"
        expiration-timeout : 60

        cluster : 2a4b1e0c-1111-4b1a-909f-cff4c8de61d9
        host : SRV2
        port : 1741
        name : "Второй кластер"
        expiration-timeout : 120
        """;

    [Fact]
    public void ExtractClusterBlock_FirstCluster_ReturnsBlockLines()
    {
        var block = RacClient.ExtractClusterBlock(
            TwoClustersKvOutput, Guid.Parse("cbc95ef0-99c9-4b1a-909f-cff4c8de61d9"));

        Assert.NotNull(block);
        Assert.Contains("Локальный кластер", block);
        Assert.Contains("expiration-timeout : 60", block);
        Assert.DoesNotContain("Второй кластер", block);
    }

    [Fact]
    public void ExtractClusterBlock_SecondCluster_ReturnsOnlySecondBlock()
    {
        var block = RacClient.ExtractClusterBlock(
            TwoClustersKvOutput, Guid.Parse("2a4b1e0c-1111-4b1a-909f-cff4c8de61d9"));

        Assert.NotNull(block);
        Assert.Contains("Второй кластер", block);
        Assert.Contains("expiration-timeout : 120", block);
        Assert.DoesNotContain("Локальный кластер", block);
    }

    [Fact]
    public void ExtractClusterBlock_ClusterNotFound_ReturnsNull()
    {
        Assert.Null(RacClient.ExtractClusterBlock(
            TwoClustersKvOutput, Guid.Parse("00000000-0000-0000-0000-000000000001")));
    }

    [Fact]
    public void ExtractClusterBlock_EmptyOrInvalid_ReturnsNull()
    {
        var id = Guid.Parse("cbc95ef0-99c9-4b1a-909f-cff4c8de61d9");
        Assert.Null(RacClient.ExtractClusterBlock(null, id));
        Assert.Null(RacClient.ExtractClusterBlock(string.Empty, id));
        Assert.Null(RacClient.ExtractClusterBlock("port : 1541", id));
    }

    // ---------- Диагностика нераспознанного вывода (issue #324, лог 7OH от 2026-10-08):
    // при exit=0, stdout>0 и 0 распознанных записей в журнал пишется WARN с командой,
    // объёмом вывода и его началом — по журналу видно, какая из шести параллельных
    // list-команд не распознана и в каком виде пришёл вывод. ----------

    [Fact]
    public void EnsureParsedOrThrow_WithLogger_WarnsWithCommandAndOutputPreview()
    {
        var logger = new CapturingLogger();
        const string output = "some future format line 1\nsome future format line 2\n";

        Assert.Throws<RacOutputParseException>(
            () => RacClient.EnsureParsedOrThrow(output, 0, "session list", logger));

        var warn = Assert.Single(logger.Warnings);
        Assert.Contains("session list", warn);                       // какая команда
        Assert.Contains($"stdout={output.Length}", warn);            // объём вывода
        Assert.Contains("some future format line 1", warn);          // начало вывода
        Assert.DoesNotContain("\n", warn);                           // одна строка журнала
    }

    [Fact]
    public void EnsureParsedOrThrow_WithoutLogger_StillThrows()
    {
        // Логгер опционален: старые вызовы (3 аргумента) продолжают работать.
        Assert.Throws<RacOutputParseException>(
            () => RacClient.EnsureParsedOrThrow("future format", 0, "process list"));
    }

    [Fact]
    public void Preview_FlattensLines_AndTruncatesTo200Chars()
    {
        var longOutput = string.Concat(Enumerable.Repeat("абвгд ", 60)); // 360 символов

        var preview = RacClient.Preview(longOutput);

        Assert.True(preview.Length <= 201); // 200 символов + «…»
        Assert.EndsWith("…", preview);
        Assert.DoesNotContain("\n", RacClient.Preview("a\r\nb\tc"));
        Assert.Contains("a b c", RacClient.Preview("a\r\nb\tc"));
    }

    /// <summary>Логгер-заглушка: собирает сообщения для проверки диагностики.</summary>
    private sealed class CapturingLogger : IAppLogger
    {
        public List<string> Warnings { get; } = new();

        public void Info(string message) { }
        public void Warn(string message) => Warnings.Add(message);
        public void Error(string message, Exception? exception = null) { }
    }

    [Fact]
    public void ToClusterInfo_FromExtractedBlock_FillsNameAndPort()
    {
        // Блок из cluster list должен разбираться так же, как вывод cluster info:
        // это основание для переиспользования кэша вместо запуска rac (issue #324).
        var block = RacClient.ExtractClusterBlock(
            TwoClustersKvOutput, Guid.Parse("cbc95ef0-99c9-4b1a-909f-cff4c8de61d9"))!;

        var info = RacOutputParser.ToClusterInfo(block);

        Assert.Equal("Локальный кластер", info.Name);
        Assert.Equal(27541, info.Port);
    }

    // ---------- LooksLikeUsageHelp (issue #324, 0.3.10.3: справка вместо списка заданий) ----------

    [Fact]
    public void LooksLikeUsageHelp_RussianHelp_IsDetected()
    {
        // Фактический случай 8.5.4.1878: «job list <uuid>» завершается с кодом 0,
        // но rac вернул справку об использовании (stdout=1833 симв.) — 0 записей.
        const string output =
            "Использование: rac [режим] [команда] [параметры]\n" +
            "Команды: cluster, infobase, session, connection, process, lock, job\n" +
            "Пример: rac localhost:1540 cluster list";
        Assert.True(RacClient.LooksLikeUsageHelp(output));
    }

    [Fact]
    public void LooksLikeUsageHelp_EnglishHelp_IsDetected()
    {
        Assert.True(RacClient.LooksLikeUsageHelp("Usage: rac [mode] [command] [options]\n"));
    }

    [Fact]
    public void LooksLikeUsageHelp_LeadingBlankLine_StillDetected()
    {
        Assert.True(RacClient.LooksLikeUsageHelp("\r\n\r\nИспользование: rac [режим] [команда]\n"));
    }

    [Fact]
    public void LooksLikeUsageHelp_DataOutput_IsNotHelp()
    {
        // Табличный и key-value вывод данных справкой не считаются.
        Assert.False(RacClient.LooksLikeUsageHelp("cluster\tjob\tinfobase\tname\n"));
        Assert.False(RacClient.LooksLikeUsageHelp(
            "job                                 : cbc95ef0-99c9-4b1a-909f-cff4c8de61d9\n" +
            "name                                : \"Обновление информационной базы\"\n"));
        Assert.False(RacClient.LooksLikeUsageHelp(null));
        Assert.False(RacClient.LooksLikeUsageHelp(string.Empty));
        Assert.False(RacClient.LooksLikeUsageHelp("   \n  "));
    }

    [Fact]
    public void LooksLikeUsageHelp_HelpMentionedInsideData_IsNotHelp()
    {
        // Справка распознаётся только по ПЕРВОЙ непустой строке: упоминание слова
        // «Использование» в данных (например, в имени задания) не сигнализирует.
        Assert.False(RacClient.LooksLikeUsageHelp(
            "job\tinfobase\tname\n" +
            "guid\tguid\tИспользование: rac — так называется задание\n"));
    }

    // ---------- LooksLikeUsageHelp (issue #324, 0.3.11: справка 8.5.4.1878 с баннером) ----------

    [Fact]
    public void LooksLikeUsageHelp_RussianHelpWithBanner_8_5_4_1878_IsDetected()
    {
        // Реальная структура вывода rac 8.5.4.1878 (issue #324, 0.3.11): сначала
        // многострочный баннер, «Использование:» — дальше первой строки.
        const string output =
            "1C:Enterprise 8.5 Remote Administrative Client Utility © 1C-Soft LLC 1996-2026\n" +
            "Утилита административной консоли 1С:Предприятия\n" +
            "\n" +
            "Использование: rac [режим] [команда] [параметры]\n" +
            "Пример: rac localhost:1540 cluster list\n";
        Assert.True(RacClient.LooksLikeUsageHelp(output));
    }

    [Fact]
    public void LooksLikeUsageHelp_EnglishHelpWithBanner_IsDetected()
    {
        const string output =
            "1C:Enterprise 8.5 Remote Administrative Client Utility © 1C-Soft LLC 1996-2026\n" +
            "\n" +
            "Usage: rac [mode] [command] [options]\n" +
            "Example: rac localhost:1540 cluster list\n";
        Assert.True(RacClient.LooksLikeUsageHelp(output));
    }

    [Fact]
    public void LooksLikeUsageHelp_CommandTemplateMarkerWithoutUsagePrefix_IsDetected()
    {
        // Шаблон команды «rac [режим]» в первых строках — признак справки, даже если
        // строка не начинается с «Использование:»/«Usage:».
        Assert.True(RacClient.LooksLikeUsageHelp(
            "Утилита 1С:Предприятия\n rac [режим] [команда]\n"));
    }

    [Fact]
    public void LooksLikeUsageHelp_BannerBeyondScanWindow_IsNotHelp()
    {
        // «Использование:» за пределами окна 15 строк — data-вывод с поздним
        // упоминанием (в имени задания) справкой не считается.
        var lines = new System.Collections.Generic.List<string> { "job-id : 1" };
        for (var i = 0; i < RacClient.UsageHelpScanLines; i++)
            lines.Add("name : «Использование режима» — имя задания");
        lines.Add("name : Использование: rac — так называется задание");
        Assert.False(RacClient.LooksLikeUsageHelp(string.Join("\n", lines)));
    }

    [Fact]
    public void LooksLikeUsageHelp_RealJobDataOutput_IsNotHelp()
    {
        // Реальный data-вывод «job list» (блоки «ключ : значение») — не справка.
        const string output =
            "job                                 : cbc95ef0-99c9-4b1a-909f-cff4c8de61d9\n" +
            "name                                : \"Обновление информационной базы\"\n" +
            "start                               : 2026-10-07T10:00:00\n" +
            "state                               : StartInfobase\n";
        Assert.False(RacClient.LooksLikeUsageHelp(output));
    }

    // ---------- ClusterUpdateArgs (issue #324, C3: rac «cluster update») ----------

    [Fact]
    public void ClusterUpdateArgs_EmptyChanges_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => RacClient.ClusterUpdateArgs(Guid.NewGuid(), new RacClusterUpdate()));
        Assert.Throws<ArgumentException>(() => RacClient.ClusterUpdateArgs(Guid.NewGuid(), null));
    }

    [Fact]
    public void ClusterUpdateArgs_OnlyChangedFields_AreIncluded()
    {
        // В командную строку попадают ТОЛЬКО изменённые свойства (null — не трогаем),
        // числа — в инвариантной записи.
        var clusterId = Guid.Parse("cbc95ef0-99c9-4b1a-909f-cff4c8de61d9");
        var args = RacClient.ClusterUpdateArgs(clusterId, new RacClusterUpdate
        {
            Name = "Локальный кластер",
            ExpirationTimeout = 120,
            SecurityLevel = 1
        });

        Assert.Equal(new[]
        {
            "cluster", "update", $"--cluster={clusterId}",
            "--name=Локальный кластер",
            "--expiration-timeout=120",
            "--security-level=1"
        }, args);
    }

    [Fact]
    public void ClusterUpdateArgs_AllKnownFields_AreSupported()
    {
        var clusterId = Guid.NewGuid();
        var args = RacClient.ClusterUpdateArgs(clusterId, new RacClusterUpdate
        {
            Name = "кл",
            ExpirationTimeout = 60,
            LifetimeLimit = 0,
            MaxMemorySize = 900000,
            MaxMemoryTimeLimit = 120,
            SecurityLevel = 0,
            PingPeriod = 1000,
            PingTimeout = 5000,
            MaxAuthAttempts = 10,
            AuthLockDuration = 900
        });

        Assert.Equal(new[]
        {
            "cluster", "update", $"--cluster={clusterId}",
            "--name=кл",
            "--expiration-timeout=60",
            "--lifetime-limit=0",
            "--max-memory-size=900000",
            "--max-memory-time-limit=120",
            "--security-level=0",
            "--ping-period=1000",
            "--ping-timeout=5000",
            "--max-auth-attempts=10",
            "--auth-lock-duration=900"
        }, args);
    }

    [Fact]
    public void ClusterUpdateArgs_NameWithSpaces_IsSingleToken()
    {
        // Значение с пробелами остаётся одним токеном (аргументы передаются через
        // ArgumentList без shell — как у остальной сборки аргументов rac).
        var args = RacClient.ClusterUpdateArgs(
            Guid.NewGuid(), new RacClusterUpdate { Name = "Имя с пробелами" });

        Assert.Contains("--name=Имя с пробелами", args);
    }
}