using System.IO;
using System.Text;
using Configuration_Management.Models;

namespace Configuration_Management.Services;

/// <summary>
/// Строка тела секции файла ibases.v8i в исходном виде (без заголовка «[Имя]»).
/// Для строк вида «Ключ=Значение» дополнительно хранится разобранный ключ, чтобы
/// запись могла обновлять значения управляемых ключей НА СВОИХ МЕСТАХ. Пустые строки
/// и строки без «=» (комментарии и т.п.) сохраняются дословно (<see cref="Key"/> == null).
/// </summary>
internal sealed class IbaseSectionLine
{
    /// <summary>Исходный текст строки без перевода строки.</summary>
    public string Raw { get; init; } = string.Empty;

    /// <summary>Имя ключа (до «=») с обрезанными краевыми пробелами; null для пустых/непарных строк.</summary>
    public string? Key { get; init; }

    /// <summary>Значение (после «=») с обрезанными краевыми пробелами; null для строк без «=».</summary>
    public string? Value { get; init; }
}

/// <summary>
/// Внутреннее представление записи базы (или группы-секции) из файла ibases.v8i.
/// Общая реализация для экспортёра и импортёра, чтобы оба пути работали одинаково
/// и не теряли ключи при пересохранении (issue #277).
/// Ключи секции хранятся в <see cref="Lines"/> в ИСХОДНОМ ПОРЯДКЕ (включая пустые
/// строки и неизвестные/пользовательские ключи). При записи значения управляемых
/// ключей (ID, Enable, Folder, Connect, App, DefaultApp, Version, AdditionalParameters)
/// обновляются на своих местах, а отсутствующие добавляются в каноническом порядке
/// в конец секции. Все прочие строки (Locale, External, ClientConnectionSpeed,
/// OrderInList/OrderInTree, WA, DisableLocalSpeechToText, пользовательские) переносятся
/// дословно, без потерь и без изменения порядка.
/// </summary>
internal sealed class IbaseEntry
{
    /// <summary>Имя секции (заголовок «[Имя]»).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Строка подключения (Connect).</summary>
    public string Connect { get; set; } = string.Empty;

    /// <summary>
    /// Оригинальная строка подключения Connect из файла (до любых изменений). Заполняется
    /// при разборе (<see cref="Parse"/>) и используется при обновлении записи, чтобы сохранить
    /// состав параметров исходного Connect: Usr/Pwd пишутся только если они БЫЛИ в исходном
    /// файле (issue #277).
    /// </summary>
    public string OriginalConnect { get; set; } = string.Empty;

    /// <summary>Путь родительской группы в формате стартера (Folder).</summary>
    public string Group { get; set; } = string.Empty;

    /// <summary>Признак включённой записи (Enable).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>ID базы 1С (GUID).</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Режим запуска из файла ibases.v8i (Auto, ThinClient, ThickClient, WebClient).</summary>
    public string App { get; set; } = string.Empty;

    /// <summary>Режим запуска по умолчанию из файла ibases.v8i (DefaultApp).</summary>
    public string DefaultApp { get; set; } = string.Empty;

    /// <summary>Версия платформы 1С.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>Дополнительные параметры подключения (AdditionalParameters).</summary>
    public string AdditionalParameters { get; set; } = string.Empty;

    /// <summary>Локаль базы (сохраняется дословно, не участвует в обновлении).</summary>
    public string Locale { get; set; } = string.Empty;

    /// <summary>Признак внешней базы (сохраняется дословно, не участвует в обновлении).</summary>
    public bool External { get; set; }

    /// <summary>Скорость соединения клиента (сохраняется дословно, не участвует в обновлении).</summary>
    public string ClientConnectionSpeed { get; set; } = string.Empty;

    /// <summary>
    /// Строки тела секции в исходном порядке (без заголовка «[Имя]»), включая пустые
    /// строки и неизвестные ключи. Хвостовые пустые строки (межсекционные разделители)
    /// при разборе удаляются — при записи между секциями вставляется ровно одна пустая
    /// строка.
    /// </summary>
    public List<IbaseSectionLine> Lines { get; } = new();

    /// <summary>
    /// Признак того, что запись является группой, а не базой.
    /// Группа — это секция без строки подключения (Connect).
    /// </summary>
    public bool IsGroup => string.IsNullOrWhiteSpace(Connect);

    /// <summary>
    /// Возвращает true, если в секции есть строка с указанным ключом (регистронезависимо).
    /// Используется для правил «минимальных изменений» файла: нейтральные ключи
    /// App/DefaultApp не дописываются, если их не было в секции (issue #277).
    /// </summary>
    public bool HasKey(string key)
    {
        return TryGetLine(key) is not null;
    }

    /// <summary>
    /// Возвращает строку секции с указанным ключом (регистронезависимо) или null,
    /// если такого ключа в секции нет.
    /// </summary>
    public IbaseSectionLine? TryGetLine(string key)
    {
        foreach (var line in Lines)
        {
            if (line.Key is not null
                && string.Equals(line.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return line;
            }
        }
        return null;
    }

    /// <summary>
    /// Определяет кодировку файла ibases.v8i по байтовой метке порядка (BOM) — issue #277:
    /// исходный файл стартера 1С может быть сохранён как «UTF-8 (BOM)», и при перезаписи
    /// нужно сохранить ту же кодировку/BOM, а не переписывать файл в кодировку по умолчанию
    /// («UTF-8» без BOM). Методы проверяются от более длинных к более коротким, чтобы
    /// UTF-32 LE (FF FE 00 00) не принимался за UTF-16 LE (FF FE). Без BOM возвращается
    /// кодировка по умолчанию (ANSI). Для нового файла — UTF-8 с BOM (нативная кодировка,
    /// в которой стартер 1С создаёт ibases.v8i).
    /// </summary>
    public static Encoding DetectEncoding(string filePath)
    {
        if (!File.Exists(filePath))
            return new UTF8Encoding(true);

        var bytes = File.ReadAllBytes(filePath);

        if (bytes.Length >= 4
            && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)
            return new UTF32Encoding(false, true); // UTF-32 LE
        if (bytes.Length >= 4
            && bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF)
            return new UTF32Encoding(true, true);  // UTF-32 BE
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return new UTF8Encoding(true);         // UTF-8 с BOM
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return new UnicodeEncoding(false, true); // UTF-16 LE
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return new UnicodeEncoding(true, true);  // UTF-16 BE

        return Encoding.Default;
    }

    /// <summary>
    /// Разбирает файл ibases.v8i на список записей. Используется и экспортёром
    /// (для чтения существующего файла перед перезаписью), и импортёром — единая
    /// реализация гарантирует, что ни один из путей не теряет ключи (issue #277).
    /// Кодировка определяется по BOM (<see cref="DetectEncoding"/>): исходный файл
    /// в «UTF-8 (BOM)» читается и впоследствии переписывается без потери кодировки.
    /// </summary>
    public static List<IbaseEntry> Parse(string filePath)
    {
        var entries = new List<IbaseEntry>();
        IbaseEntry? current = null;

        foreach (var rawLine in File.ReadAllLines(filePath, DetectEncoding(filePath)))
        {
            var line = rawLine.Trim();

            if (line.Length == 0)
            {
                // Пустая строка внутри секции сохраняется как есть; пустые строки
                // между секциями при записи не восстанавливаются (разделитель не
                // добавляется, issue #277).
                if (current is not null)
                    current.Lines.Add(new IbaseSectionLine { Raw = rawLine });
                continue;
            }

            // Секция базы: [Имя базы]
            if (line.StartsWith("[") && line.EndsWith("]"))
            {
                current = new IbaseEntry { Name = line.Substring(1, line.Length - 2).Trim() };
                entries.Add(current);
                continue;
            }

            if (current is null)
                continue;

            var eqIndex = line.IndexOf('=');
            if (eqIndex < 0)
            {
                // Строка без «=» (комментарий и т.п.) — сохраняем дословно.
                current.Lines.Add(new IbaseSectionLine { Raw = rawLine });
                continue;
            }

            var key = line.Substring(0, eqIndex).Trim();
            var value = line.Substring(eqIndex + 1).Trim();

            switch (key)
            {
                case "Connect":
                    current.Connect = value;
                    current.OriginalConnect = value;
                    break;
                case "Folder":
                    current.Group = value;
                    break;
                case "Enable":
                    current.Enabled = ParseBool(value);
                    break;
                case "ID":
                    current.Id = value;
                    break;
                case "App":
                    current.App = value;
                    break;
                case "DefaultApp":
                    current.DefaultApp = value;
                    break;
                case "Version":
                    current.Version = value;
                    break;
                case "AdditionalParameters":
                    current.AdditionalParameters = value;
                    break;
                case "Locale":
                    current.Locale = value;
                    break;
                case "External":
                    current.External = ParseBool(value);
                    break;
                case "ClientConnectionSpeed":
                    current.ClientConnectionSpeed = value;
                    break;
            }

            current.Lines.Add(new IbaseSectionLine { Raw = rawLine, Key = key, Value = value });
        }

        // Хвостовые пустые строки секции — это межсекционные разделители; при записи
        // разделитель между секциями не добавляется (issue #277), поэтому убираем их,
        // чтобы не появлялись лишние пустые строки. Непустые строки без «=»
        // (комментарии в конце секции) не трогаем.
        foreach (var entry in entries)
        {
            while (entry.Lines.Count > 0
                   && entry.Lines[^1].Key is null
                   && string.IsNullOrWhiteSpace(entry.Lines[^1].Raw))
            {
                entry.Lines.RemoveAt(entry.Lines.Count - 1);
            }
        }

        return entries;
    }

    /// <summary>
    /// Записывает тело секции (без заголовка «[Имя]») в StringBuilder, сохраняя исходный
    /// порядок строк: значения управляемых ключей обновляются на своих местах, удаляются
    /// строки ключей, чьи значения стали пустыми (и Enable при включённой записи),
    /// отсутствующие управляемые ключи добавляются в каноническом порядке в конец.
    /// Нейтральный режим запуска (App/DefaultApp=Auto) не дописывается, если ключа
    /// не было в секции (issue #277). Все прочие строки (пустые, неизвестные,
    /// пользовательские ключи) переносятся дословно.
    /// </summary>
    public void WriteBodyTo(StringBuilder sb)
    {
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Проход 1: переписываем существующие строки на месте, сохраняя порядок.
        foreach (var line in Lines)
        {
            if (line.Key is null)
            {
                sb.AppendLine(line.Raw);
                continue;
            }

            if (!TryGetManagedValue(line.Key, out var value, out var omit))
            {
                // Неуправляемый ключ (в т.ч. Locale/External/ClientConnectionSpeed и
                // пользовательские ключи) — сохраняем строку дословно.
                sb.AppendLine(line.Raw);
                continue;
            }

            present.Add(line.Key);

            if (omit)
                continue; // Значение очищено (или Enable при включённой записи) — строку удаляем.

            sb.Append(line.Key).Append('=').AppendLine(value);
        }

        // Проход 2: добавляем отсутствующие управляемые ключи в каноническом порядке —
        // для новых записей и ключей, которых не было в исходной секции.
        foreach (var key in CanonicalManagedKeys)
        {
            if (present.Contains(key))
                continue;
            if (!TryGetManagedValue(key, out var value, out var omit) || omit || string.IsNullOrEmpty(value))
                continue;
            // Нейтральный режим запуска «Auto» не дописывается, если ключа App/DefaultApp
            // не было в секции (issue #277): файл сохраняется с минимальными изменениями.
            // Если ключ был — его значение обновляется на своём месте в первом проходе
            // (даже на «Auto»).
            if (IsNeutralLaunchKey(key, value))
                continue;
            sb.Append(key).Append('=').AppendLine(value);
        }
    }

    /// <summary>
    /// Возвращает true, если ключ App/DefaultApp имеет нейтральное значение «Auto»
    /// (режим запуска по умолчанию). Такой ключ НЕ дописывается в секцию, если его там
    /// не было (issue #277); если ключ был — его значение обновляется в любом случае.
    /// </summary>
    private static bool IsNeutralLaunchKey(string key, string value)
    {
        if (!string.Equals(key, "App", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(key, "DefaultApp", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return string.Equals(value, "Auto", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Объединяет новую строку подключения с составом параметров исходной (issue #277):
    /// если ЦЕЛЬ подключения не изменилась (Srvr+Ref / File / WS — регистронезависимо),
    /// то Usr/Pwd включаются ТОЛЬКО если они БЫЛИ в исходном Connect (значения — актуальные
    /// из новой строки), а прочие параметры пересобираются в каноническом виде. Если цель
    /// изменилась (сервер/база/файл/веб-адрес) или исходного Connect нет — возвращается
    /// новая строка целиком (с Usr/Pwd при наличии в приложении).
    /// </summary>
    internal static string MergeConnect(string originalConnect, string newConnect)
    {
        if (string.IsNullOrWhiteSpace(originalConnect))
            return newConnect;

        if (!SameConnectionTarget(originalConnect, newConnect))
            return newConnect;

        // Цель не изменилась — сохраняем присутствие Usr/Pwd из исходного Connect.
        var hadUsr = IndexOfParameter(originalConnect, "Usr") >= 0;
        var hadPwd = IndexOfParameter(originalConnect, "Pwd") >= 0;
        if (hadUsr && hadPwd)
            return newConnect;

        var result = newConnect;
        if (!hadPwd)
            result = RemoveParameter(result, "Pwd");
        if (!hadUsr)
            result = RemoveParameter(result, "Usr");
        return result;
    }

    /// <summary>
    /// Сравнивает «цель» двух строк подключения без учёта Usr/Pwd и прочих параметров:
    /// для файлового режима — путь File, для веб-режима — адрес WS, для клиент-серверного —
    /// сервер (host:port) и имя базы Ref. Регистр не учитывается.
    /// </summary>
    private static bool SameConnectionTarget(string a, string b)
    {
        var fileA = ExtractQuoted(a, "File");
        var fileB = ExtractQuoted(b, "File");
        if (fileA != null || fileB != null)
        {
            return fileA != null && fileB != null
                && string.Equals(fileA.Trim(), fileB.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        var wsA = ExtractQuoted(a, "WS");
        var wsB = ExtractQuoted(b, "WS");
        if (wsA != null || wsB != null)
        {
            return wsA != null && wsB != null
                && string.Equals(wsA.Trim(), wsB.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        // Клиент-серверный режим: Srvr (host:port) + Ref.
        var serverA = new ConnectionSettings();
        ConnectionSettings.ParseServerAndPort(ExtractQuoted(a, "Srvr"), serverA);
        var serverB = new ConnectionSettings();
        ConnectionSettings.ParseServerAndPort(ExtractQuoted(b, "Srvr"), serverB);

        if (!string.Equals(
                (serverA.Server ?? string.Empty).Trim(),
                (serverB.Server ?? string.Empty).Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (serverA.Port != serverB.Port)
            return false;
        return string.Equals(
            (ExtractQuoted(a, "Ref") ?? string.Empty).Trim(),
            (ExtractQuoted(b, "Ref") ?? string.Empty).Trim(),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Удаляет параметр с указанным ключом из строки подключения (регистронезависимо),
    /// сохраняя корректные разделители «;». Если параметра нет — возвращает строку как есть.
    /// </summary>
    private static string RemoveParameter(string connect, string key)
    {
        var idx = IndexOfParameter(connect, key);
        if (idx < 0)
            return connect;

        var start = idx;
        if (start > 0 && connect[start - 1] == ';')
            start--; // включаем предшествующий разделитель

        var end = connect.IndexOf(';', idx);
        if (end < 0)
        {
            end = connect.Length;
        }
        else if (start == idx)
        {
            end++; // параметр первый в строке — убираем и его завершающий разделитель
        }

        return connect.Remove(start, end - start);
    }

    /// <summary>
    /// Ищет позицию начала параметра «Key=» в строке подключения (регистронезависимо).
    /// Параметр должен начинаться с начала строки или сразу после «;».
    /// </summary>
    private static int IndexOfParameter(string connect, string key)
    {
        var marker = key + "=";
        var idx = connect.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        while (idx >= 0)
        {
            if (idx == 0 || connect[idx - 1] == ';')
                return idx;
            idx = connect.IndexOf(marker, idx + marker.Length, StringComparison.OrdinalIgnoreCase);
        }
        return -1;
    }

    /// <summary>Канонический порядок управляемых ключей для новых записей.</summary>
    private static readonly string[] CanonicalManagedKeys =
    {
        "ID", "Enable", "Folder", "Connect", "App", "DefaultApp", "Version", "AdditionalParameters"
    };

    /// <summary>
    /// Возвращает текущее значение управляемого ключа секции (регистронезависимо) и признак
    /// того, что строку ключа нужно удалить. Для неуправляемых ключей возвращает false.
    /// Ключ Enable опускается при включённой записи (отсутствие Enable = запись включена),
    /// а при отключённой — всегда выводится как Enable=0.
    /// </summary>
    private bool TryGetManagedValue(string key, out string? value, out bool omit)
    {
        switch (key.ToLowerInvariant())
        {
            case "id":
                value = Id ?? string.Empty;
                omit = string.IsNullOrEmpty(value);
                return true;
            case "enable":
                value = Enabled ? "1" : "0";
                omit = Enabled;
                return true;
            case "folder":
                value = Group ?? string.Empty;
                omit = string.IsNullOrEmpty(value);
                return true;
            case "connect":
                value = Connect ?? string.Empty;
                omit = string.IsNullOrEmpty(value);
                return true;
            case "app":
                value = App ?? string.Empty;
                omit = string.IsNullOrEmpty(value);
                return true;
            case "defaultapp":
                value = DefaultApp ?? string.Empty;
                omit = string.IsNullOrEmpty(value);
                return true;
            case "version":
                value = Version ?? string.Empty;
                omit = string.IsNullOrEmpty(value);
                return true;
            case "additionalparameters":
                value = AdditionalParameters ?? string.Empty;
                omit = string.IsNullOrEmpty(value);
                return true;
            default:
                value = null;
                omit = false;
                return false;
        }
    }

    private static bool ParseBool(string value)
    {
        return value.Trim() switch
        {
            "1" => true,
            "0" => false,
            _ => bool.TryParse(value, out var b) && b
        };
    }

    /// <summary>
    /// Преобразует запись в модель Infobase, разбирая строку подключения.
    /// Версия очищается от суффикса разрядности «(32)/(64)», а разрядность
    /// сохраняется в отдельное поле Architecture.
    /// </summary>
    public Infobase ToInfobase()
    {
        var connection = ParseConnection(Connect);

        var version = Version;
        var architecture = string.Empty;
        var end = Version.LastIndexOf(')');
        var start = Version.LastIndexOf('(');
        if (end >= 0 && start >= 0 && start < end)
        {
            var arch = Version.Substring(start + 1, end - start - 1).Trim();
            if (arch == "32" || arch == "64")
            {
                architecture = arch;
                var clean = Version.Substring(0, start).Trim();
                if (!string.IsNullOrWhiteSpace(clean))
                    version = clean;
            }
        }

        return new Infobase
        {
            Name = Name,
            Group = NormalizeGroupPath(Group),
            Connection = connection,
            PlatformVersion = version,
            Architecture = architecture,
            LaunchMode = MapLaunchMode(App, DefaultApp),
            LaunchParameters = AdditionalParameters,
            Description = string.Empty,
            Id = Id
        };
    }

    /// <summary>
    /// Преобразует значения ключей App и DefaultApp из ibases.v8i в режим запуска приложения.
    /// Приоритет отдаётся явно заданному значению App. Если App не задан или равен Auto,
    /// используется режим запуска по умолчанию (DefaultApp). Признак WA (доступность
    /// веб-клиента) не влияет на режим запуска.
    /// </summary>
    private static string MapLaunchMode(string app, string defaultApp)
    {
        // Явно заданный режим запуска имеет приоритет.
        var mapped = MapSingleLaunchMode(app);
        if (mapped != null)
            return mapped;

        // App не задан или равен Auto — используем режим запуска по умолчанию (DefaultApp).
        mapped = MapSingleLaunchMode(defaultApp);
        if (mapped != null)
            return mapped;

        return "Автоматический";
    }

    /// <summary>
    /// Сопоставляет одно значение ключа App/DefaultApp из ibases.v8i каноническому
    /// русскому режиму запуска. Возвращает null, если значение не распознано
    /// (пусто, Auto или иное) — в этом случае применяется режим по умолчанию.
    /// Канонические значения используются для хранения и сравнения и НЕ локализуются.
    /// </summary>
    private static string? MapSingleLaunchMode(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "thinclient" => "Тонкий клиент",
            "thickclient" => "Толстый клиент",
            "webclient" => "Веб-клиент",
            _ => null
        };
    }

    /// <summary>
    /// Разбирает строку подключения 1С вида:
    /// File="C:\path"  или  Srvr="server";Ref="base";Usr="user";Pwd="pass"
    /// </summary>
    private static ConnectionSettings ParseConnection(string connect)
    {
        var settings = new ConnectionSettings();

        if (string.IsNullOrWhiteSpace(connect))
            return settings;

        // Файловый режим.
        var fileMatch = ExtractQuoted(connect, "File");
        if (fileMatch != null)
        {
            settings.Type = ConnectionType.File;
            settings.FilePath = fileMatch;
            return settings;
        }

        // Клиент-серверный / веб-режим.
        var wsMatch = ExtractQuoted(connect, "WS");
        if (wsMatch != null)
        {
            settings.Type = ConnectionType.WebServer;
            settings.WebUrl = wsMatch;
            return settings;
        }

        settings.Type = ConnectionType.ClientServer;
        // Srvr может быть «host» или «host:port» — порт выносим в отдельное поле.
        ConnectionSettings.ParseServerAndPort(ExtractQuoted(connect, "Srvr"), settings);
        settings.DatabaseName = ExtractQuoted(connect, "Ref") ?? string.Empty;
        settings.User = ExtractQuoted(connect, "Usr") ?? string.Empty;
        settings.Password = ExtractQuoted(connect, "Pwd") ?? string.Empty;
        // Не сбрасываем режим аутентификации в Windows только из-за пустого Usr:
        // в ibases.v8i логин часто отсутствует, а вход запрашивается платформой.
        if (!string.IsNullOrEmpty(settings.User))
            settings.AuthenticationMode = AuthenticationMode.Credentials;
        else
            settings.AuthenticationMode = AuthenticationMode.Prompt;

        return settings;
    }

    /// <summary>
    /// Извлекает значение параметра из строки подключения.
    /// Например, для "Srvr=\"server\"" вернёт "server".
    /// Поддерживает пробелы вокруг знака "=" и значения без кавычек.
    /// </summary>
    private static string? ExtractQuoted(string source, string key)
    {
        // Ищем ключ с возможными пробелами вокруг знака "=".
        var marker = key + "=";
        var idx = source.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            // Пробуем вариант с пробелом перед "=" (например, "Srv = \"server\"").
            var spacedMarker = key + " =";
            idx = source.IndexOf(spacedMarker, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                return null;
            idx += spacedMarker.Length - 1; // указываем на "="
        }
        else
        {
            idx += marker.Length - 1; // указываем на "="
        }

        var start = idx + 1; // сразу после "="
        if (start >= source.Length)
            return null;

        // Пропускаем пробелы.
        while (start < source.Length && source[start] == ' ')
            start++;

        if (start >= source.Length)
            return null;

        // Значение в кавычках. Удвоенная кавычка («""») внутри значения — это экранированная
        // кавычка (симметрично записи экспортёром); одиночная кавычка закрывает значение.
        if (source[start] == '"')
        {
            var sb = new System.Text.StringBuilder();
            var i = start + 1;
            while (i < source.Length)
            {
                if (source[i] == '"')
                {
                    // Удвоенная кавычка — экранированная кавычка внутри значения.
                    if (i + 1 < source.Length && source[i + 1] == '"')
                    {
                        sb.Append('"');
                        i += 2;
                        continue;
                    }
                    // Одиночная кавычка закрывает значение.
                    break;
                }
                sb.Append(source[i]);
                i++;
            }

            // Дошли до конца строки, не встретив закрывающей кавычки.
            if (i >= source.Length)
                return null;

            return sb.ToString();
        }

        // Значение без кавычек — до точки с запятой или конца строки.
        var valueEnd = source.IndexOf(';', start);
        if (valueEnd < 0)
            valueEnd = source.Length;

        return source.Substring(start, valueEnd - start).Trim();
    }

    /// <summary>Нормализует путь группы: разделители «/» и «\» → внутренний « / », пробелы убираются.</summary>
    private static string NormalizeGroupPath(string group)
    {
        var segments = SplitGroupPath(group);
        return string.Join(GroupHierarchyHelper.PathSeparator, segments);
    }

    /// <summary>Разбивает путь группы на сегменты по разделителям "/" и "\".</summary>
    private static List<string> SplitGroupPath(string path)
    {
        return path
            .Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();
    }
}