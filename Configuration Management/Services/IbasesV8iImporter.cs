using System.IO;
using System.Text;
using Configuration_Management.Models;

namespace Configuration_Management.Services;

/// <summary>
/// Результат импорта списка баз из файла ibases.v8i.
/// </summary>
public class IbasesImportResult
{
    /// <summary>Количество добавленных новых баз.</summary>
    public int Added { get; set; }

    /// <summary>Количество обновлённых существующих баз.</summary>
    public int Updated { get; set; }

    /// <summary>Количество пропущенных (отключённых) баз.</summary>
    public int Skipped { get; set; }

    /// <summary>Количество созданных новых групп.</summary>
    public int GroupsCreated { get; set; }
}

/// <summary>
/// Сервис импорта списка информационных баз из стандартного файла 1С ibases.v8i.
/// </summary>
public static class IbasesV8iImporter
{
    /// <summary>
    /// Считывает список баз из файла ibases.v8i, добавляет новые базы в коллекцию,
    /// обновляет существующие (по совпадению имени) и создаёт недостающие группы.
    /// </summary>
    /// <param name="filePath">Путь к файлу ibases.v8i.</param>
    /// <param name="infobases">Коллекция баз, в которую выполняется импорт.</param>
    /// <param name="groups">Коллекция групп, в которую добавляются недостающие группы.</param>
    /// <returns>Результат импорта.</returns>
    public static IbasesImportResult Import(string filePath, IList<Infobase> infobases, IList<Group> groups)
    {
        var result = new IbasesImportResult();

        if (!File.Exists(filePath))
            return result;

        var entries = Parse(filePath);

        // Создаём недостающие группы из импортируемых баз.
        EnsureGroups(entries, groups, result);

        foreach (var entry in entries)
        {
            // Пропускаем группы (секции без строки подключения) — они не являются базами.
            if (entry.IsGroup)
                continue;

            // Пропускаем отключённые базы.
            if (!entry.Enabled)
            {
                result.Skipped++;
                continue;
            }

            var existing = infobases.FirstOrDefault(b =>
                string.Equals(b.Name, entry.Name, StringComparison.OrdinalIgnoreCase));

            if (existing is null)
            {
                // Новая база — добавляем.
                infobases.Add(entry.ToInfobase());
                result.Added++;
            }
            else
            {
                // Существующая база — обновляем настройки подключения, группу, ID базы 1С,
                // версию платформы и режим запуска.
                var imported = entry.ToInfobase();
                existing.Connection = imported.Connection;
                if (!string.IsNullOrWhiteSpace(entry.Group))
                    existing.Group = NormalizeGroupPath(entry.Group);
                if (!string.IsNullOrWhiteSpace(entry.Id))
                    existing.Id = entry.Id;
                if (!string.IsNullOrWhiteSpace(imported.PlatformVersion))
                    existing.PlatformVersion = imported.PlatformVersion;
                if (!string.IsNullOrWhiteSpace(imported.LaunchMode))
                    existing.LaunchMode = imported.LaunchMode;
                if (!string.IsNullOrWhiteSpace(imported.LaunchParameters))
                    existing.LaunchParameters = imported.LaunchParameters;
                result.Updated++;
            }
        }

        return result;
    }

    /// <summary>
    /// Определяет уникальные группы из импортируемых записей и добавляет
    /// недостающие группы в коллекцию.
    /// </summary>
    private static void EnsureGroups(List<IbaseEntry> entries, IList<Group> groups, IbasesImportResult result)
    {
        // Группы из файла ibases.v8i — это секции без строки подключения (Connect),
        // у которых есть собственный ID. Также учитываем группы, на которые
        // ссылаются базы через ключ Folder.
        var groupEntries = entries
            .Where(e => e.IsGroup && e.Enabled)
            .ToList();

        // Собираем полные пути групп: из секций-групп (по Name) и из ссылок баз (по Folder).
        var groupPaths = new List<string>();
        foreach (var entry in entries)
        {
            if (!entry.Enabled)
                continue;

            if (entry.IsGroup)
            {
                // Путь группы-секции.
                var groupPath = NormalizeGroupPath(entry.Name);
                if (!string.IsNullOrWhiteSpace(groupPath))
                    groupPaths.Add(groupPath);
            }
            else
            {
                // Группа, на которую ссылается база через Folder (путь «Родитель\Дочерняя»).
                var groupPath = NormalizeGroupPath(entry.Group);
                if (!string.IsNullOrWhiteSpace(groupPath))
                    groupPaths.Add(groupPath);
            }
        }

        // Создаём группы для каждого уникального пути, выстраивая иерархию.
        foreach (var groupPath in groupPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            CreateGroupWithParents(groupPath, groupEntries, groups, result);
        }
    }

    /// <summary>
    /// Создаёт группу по полному пути (например, «Учёт\Бухгалтерия»),
    /// автоматически создавая недостающие родительские группы и выставляя ParentId.
    /// </summary>
    private static void CreateGroupWithParents(
        string groupPath,
        List<IbaseEntry> groupEntries,
        IList<Group> groups,
        IbasesImportResult result)
    {
        var segments = SplitGroupPath(groupPath);
        if (segments.Count == 0)
            return;

        string? parentId = null;
        foreach (var segment in segments)
        {
            var existing = groups.FirstOrDefault(g =>
                string.Equals(g.Name, segment, StringComparison.OrdinalIgnoreCase)
                && IsParent(g, parentId));

            if (existing is null)
            {
                // Ищем ID группы-секции из файла, соответствующей текущему сегменту.
                var groupEntry = groupEntries.FirstOrDefault(e =>
                    string.Equals(NormalizeGroupName(e.Name), segment, StringComparison.OrdinalIgnoreCase));

                existing = new Group
                {
                    Name = segment,
                    // ID группы-секции из файла, либо новый GUID. Учитываем и null, и пустую строку:
                    // при пустом Id связь родитель-потомок по ParentId теряется, иерархия групп ломается.
                    Id = !string.IsNullOrWhiteSpace(groupEntry?.Id) ? groupEntry.Id : Guid.NewGuid().ToString(),
                    ParentId = parentId ?? string.Empty
                };
                groups.Add(existing);
                result.GroupsCreated++;
            }

            parentId = existing.Id;
        }
    }

    /// <summary>
    /// Проверяет, что группа <paramref name="group"/> имеет указанного родителя.
    /// </summary>
    private static bool IsParent(Group group, string? parentId)
    {
        return string.Equals(group.ParentId ?? string.Empty, parentId ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Разбивает путь группы на сегменты по разделителям "/" и "\".
    /// </summary>
    private static List<string> SplitGroupPath(string path)
    {
        return path
            .Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();
    }

    /// <summary>
    /// Ищет файл ibases.v8i в стандартных местах хранения списка баз 1С.
    /// Возвращает путь к найденному файлу или null, если файл не найден.
    /// </summary>
    public static string? FindDefaultPath()
    {
        var candidates = new List<string>();

        // Основной путь: %APPDATA%\1C\1CEStart\ibases.v8i
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrEmpty(appData))
        {
            candidates.Add(Path.Combine(appData, "1C", "1CEStart", "ibases.v8i"));
        }

        // Дополнительные возможные пути.
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localAppData))
        {
            candidates.Add(Path.Combine(localAppData, "1C", "1CEStart", "ibases.v8i"));
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Ищет ID базы 1С (GUID) в файле ibases.v8i по имени базы или строке подключения.
    /// Сначала выполняется поиск по имени (без учёта регистра), затем — по строке подключения.
    /// Используется для подстановки ID базы 1С при ручном создании базы в приложении.
    /// </summary>
    /// <param name="name">Наименование базы.</param>
    /// <param name="connectionString">Строка подключения (File=... или Srvr=...;Ref=...).</param>
    /// <returns>ID базы 1С или null, если база не найдена.</returns>
    public static string? FindId(string? name, string? connectionString)
    {
        var filePath = FindDefaultPath();
        if (filePath is null)
            return null;

        var entries = Parse(filePath);

        // 1. Поиск по имени базы.
        if (!string.IsNullOrWhiteSpace(name))
        {
            var byName = entries.FirstOrDefault(e =>
                string.Equals(e.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (byName != null && !string.IsNullOrWhiteSpace(byName.Id))
                return byName.Id;
        }

        // 2. Поиск по строке подключения.
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            var normalized = NormalizeConnectionString(connectionString);
            if (!string.IsNullOrEmpty(normalized))
            {
                var byConnect = entries.FirstOrDefault(e =>
                    !string.IsNullOrWhiteSpace(e.Connect) &&
                    NormalizeConnectionString(e.Connect).StartsWith(normalized, StringComparison.OrdinalIgnoreCase));
                if (byConnect != null && !string.IsNullOrWhiteSpace(byConnect.Id))
                    return byConnect.Id;
            }
        }

        return null;
    }

    /// <summary>
    /// Нормализует строку подключения для сравнения: убирает пробелы и кавычки,
    /// приводит к нижнему регистру. Позволяет сопоставлять строки подключения
    /// с разным порядком/наличием дополнительных параметров (Usr, Pwd и др.).
    /// </summary>
    private static string NormalizeConnectionString(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (ch == '"' || ch == ' ')
                continue;
            sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Разбирает файл ibases.v8i на список записей баз.
    /// </summary>
    private static List<IbaseEntry> Parse(string filePath)
    {
        var entries = new List<IbaseEntry>();
        IbaseEntry? current = null;

        foreach (var rawLine in File.ReadAllLines(filePath, Encoding.Default))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;

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
                continue;

            var key = line.Substring(0, eqIndex).Trim();
            var value = line.Substring(eqIndex + 1).Trim();

            switch (key)
            {
                case "Connect":
                    current.Connect = value;
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
                case "Locale":
                    current.Locale = value;
                    break;
                case "External":
                    current.External = ParseBool(value);
                    break;
                case "ClientConnectionSpeed":
                    current.ClientConnectionSpeed = value;
                    break;
                case "AdditionalParameters":
                    current.AdditionalParameters = value;
                    break;
            }
        }

        return entries;
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
    /// Нормализует путь группы: обрезает пробелы вокруг разделителей "/" и "\",
    /// но сохраняет иерархию пути.
    /// </summary>
    private static string NormalizeGroupPath(string group)
    {
        var segments = SplitGroupPath(group);
        return string.Join(GroupHierarchyHelper.PathSeparator, segments);
    }

    /// <summary>
    /// Нормализует одиночное имя группы (без учёта пути): убирает разделители.
    /// Используется для сопоставления имён секций-групп.
    /// </summary>
    private static string NormalizeGroupName(string name)
    {
        var segments = SplitGroupPath(name);
        return segments.Count > 0 ? segments[^1] : string.Empty;
    }

    /// <summary>
    /// Внутреннее представление записи базы из файла ibases.v8i.
    /// </summary>
    private sealed class IbaseEntry
    {
        public string Name { get; set; } = string.Empty;
        public string Connect { get; set; } = string.Empty;
        public string Group { get; set; } = string.Empty;
        public bool Enabled { get; set; } = true;
        public string Id { get; set; } = string.Empty;

        /// <summary>Режим запуска из файла ibases.v8i (Auto, ThinClient, ThickClient, WebClient).</summary>
        public string App { get; set; } = string.Empty;

        /// <summary>Режим запуска по умолчанию из файла ibases.v8i (DefaultApp).</summary>
        public string DefaultApp { get; set; } = string.Empty;

        /// <summary>Версия платформы 1С.</summary>
        public string Version { get; set; } = string.Empty;

        /// <summary>Локаль базы.</summary>
        public string Locale { get; set; } = string.Empty;

        /// <summary>Признак внешней базы.</summary>
        public bool External { get; set; }

        /// <summary>Скорость соединения клиента (Normal, Fast, Slow).</summary>
        public string ClientConnectionSpeed { get; set; } = string.Empty;

        /// <summary>Дополнительные параметры подключения (AdditionalParameters).</summary>
        public string AdditionalParameters { get; set; } = string.Empty;

        /// <summary>
        /// Признак того, что запись является группой, а не базой.
        /// Группа — это секция без строки подключения (Connect).
        /// </summary>
        public bool IsGroup => string.IsNullOrWhiteSpace(Connect);

        /// <summary>
        /// Преобразует запись в модель Infobase, разбирая строку подключения.
        /// </summary>
        public Infobase ToInfobase()
        {
            var connection = ParseConnection(Connect);

            return new Infobase
            {
                Name = Name,
                Group = NormalizeGroupPath(Group),
                Connection = connection,
                PlatformVersion = Version,
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
            var normalizedApp = app.Trim().ToLowerInvariant();

            // Явно заданный режим запуска имеет приоритет.
            switch (normalizedApp)
            {
                case "thinclient":
                    return "Тонкий клиент";
                case "thickclient":
                    return "Толстый клиент";
                case "webclient":
                    return "Веб-клиент";
            }

            // App не задан или равен Auto — используем режим запуска по умолчанию (DefaultApp).
            var normalizedDefault = defaultApp.Trim().ToLowerInvariant();
            switch (normalizedDefault)
            {
                case "thinclient":
                    return "Тонкий клиент";
                case "thickclient":
                    return "Толстый клиент";
                case "webclient":
                    return "Веб-клиент";
            }

            return "Автоматический";
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

            // Клиент-серверный режим.
            settings.Type = ConnectionType.ClientServer;
            settings.Server = ExtractQuoted(connect, "Srvr") ?? string.Empty;
            settings.DatabaseName = ExtractQuoted(connect, "Ref") ?? string.Empty;
            settings.User = ExtractQuoted(connect, "Usr") ?? string.Empty;
            settings.Password = ExtractQuoted(connect, "Pwd") ?? string.Empty;
            settings.UseOsAuthentication = string.IsNullOrEmpty(settings.User);

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

            // Значение в кавычках.
            if (source[start] == '"')
            {
                var end = start + 1;
                while (end < source.Length && source[end] != '"')
                    end++;

                if (end >= source.Length)
                    return null;

                return source.Substring(start + 1, end - start - 1);
            }

            // Значение без кавычек — до точки с запятой или конца строки.
            var valueEnd = source.IndexOf(';', start);
            if (valueEnd < 0)
                valueEnd = source.Length;

            return source.Substring(start, valueEnd - start).Trim();
        }
    }
}