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

    /// <summary>Количество удалённых из приложения баз (есть в приложении, нет в файле).</summary>
    public int Removed { get; set; }

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
                // версию платформы и режим запуска. Логин/пароль из приложения сохраняем,
                // если в файле они пустые (ibases.v8i часто не хранит пароль).
                var imported = entry.ToInfobase();
                var prevUser = existing.Connection?.User ?? string.Empty;
                var prevPassword = existing.Connection?.Password ?? string.Empty;
                var prevAuth = existing.Connection?.AuthenticationMode ?? AuthenticationMode.Prompt;

                existing.Connection = imported.Connection;
                if (string.IsNullOrWhiteSpace(existing.Connection.User) && !string.IsNullOrWhiteSpace(prevUser))
                    existing.Connection.User = prevUser;
                if (string.IsNullOrWhiteSpace(existing.Connection.Password) && !string.IsNullOrWhiteSpace(prevPassword))
                    existing.Connection.Password = prevPassword;
                if (existing.Connection.AuthenticationMode == AuthenticationMode.Prompt
                    && prevAuth != AuthenticationMode.Prompt
                    && (!string.IsNullOrWhiteSpace(existing.Connection.User) || !string.IsNullOrWhiteSpace(existing.Connection.Password)))
                    existing.Connection.AuthenticationMode = prevAuth;

                if (!string.IsNullOrWhiteSpace(entry.Group))
                    existing.Group = NormalizeGroupPath(entry.Group);
                if (!string.IsNullOrWhiteSpace(entry.Id))
                    existing.Id = entry.Id;
                if (!string.IsNullOrWhiteSpace(imported.PlatformVersion))
                    existing.PlatformVersion = imported.PlatformVersion;
                // Разрядность переносим в отдельное поле базы, если в файле она была
                // явно указана суффиксом версии «(32)/(64)».
                if (imported.Architecture is "32" or "64")
                    existing.Architecture = imported.Architecture;
                if (!string.IsNullOrWhiteSpace(imported.LaunchMode))
                    existing.LaunchMode = imported.LaunchMode;
                if (!string.IsNullOrWhiteSpace(imported.LaunchParameters))
                    existing.LaunchParameters = imported.LaunchParameters;
                result.Updated++;
            }
        }

        // Удаляем из приложения базы, которых нет в файле:
        // — с заполненным ID 1С (синхронизировались со стартером);
        // — либо с тем же именем, что было в файле ранее и исчезло.
        // Локальные базы без ID, которых никогда не было в ibases.v8i, не трогаем.
        var fileNames = new HashSet<string>(
            entries.Where(e => !e.IsGroup && e.Enabled && !string.IsNullOrWhiteSpace(e.Name))
                   .Select(e => e.Name),
            StringComparer.OrdinalIgnoreCase);
        var fileIds = new HashSet<string>(
            entries.Where(e => !e.IsGroup && e.Enabled && !string.IsNullOrWhiteSpace(e.Id))
                   .Select(e => e.Id.Trim()),
            StringComparer.OrdinalIgnoreCase);

        for (var i = infobases.Count - 1; i >= 0; i--)
        {
            var b = infobases[i];
            var hasId = !string.IsNullOrWhiteSpace(b.Id);
            var nameInFile = !string.IsNullOrWhiteSpace(b.Name) && fileNames.Contains(b.Name);
            var idInFile = hasId && fileIds.Contains(b.Id.Trim());

            if (nameInFile || idInFile)
                continue;

            // Удаляем только базы, которые явно пришли из 1С (есть ID).
            if (!hasId)
                continue;

            infobases.RemoveAt(i);
            result.Removed++;
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

        // Собираем полные пути групп: из секций-групп (по Name и Folder) и из ссылок баз (по Folder).
        var groupPaths = new List<string>();
        foreach (var entry in entries)
        {
            if (!entry.Enabled)
                continue;

            if (entry.IsGroup)
            {
                // Секция-группа: Name — имя (или полный путь), Folder — путь родителя (\ или /).
                // Примеры 1С: Name=«Бухгалтерия», Folder=«Учёт»
                //            Name=«Учёт\Бухгалтерия», Folder пустой
                var folderPath = NormalizeGroupPath(entry.Group);
                var namePath = NormalizeGroupPath(entry.Name);
                string groupPath;
                if (string.IsNullOrWhiteSpace(folderPath))
                {
                    groupPath = namePath;
                }
                else if (string.IsNullOrWhiteSpace(namePath))
                {
                    groupPath = folderPath;
                }
                else if (namePath.StartsWith(folderPath + GroupHierarchyHelper.PathSeparator, StringComparison.OrdinalIgnoreCase)
                         || string.Equals(namePath, folderPath, StringComparison.OrdinalIgnoreCase))
                {
                    // Name уже содержит путь родителя — не дублируем.
                    groupPath = namePath;
                }
                else
                {
                    // Folder + листовое имя (или относительный путь Name).
                    var leaf = NormalizeGroupName(entry.Name);
                    groupPath = string.IsNullOrWhiteSpace(leaf)
                        ? folderPath
                        : folderPath + GroupHierarchyHelper.PathSeparator + leaf;
                }
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
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            var pathSoFar = string.Join(GroupHierarchyHelper.PathSeparator, segments.Take(i + 1));

            var existing = groups.FirstOrDefault(g =>
                string.Equals(g.Name, segment, StringComparison.OrdinalIgnoreCase)
                && IsParent(g, parentId));

            if (existing is null)
            {
                var id = ResolveGroupIdFromFile(groupEntries, pathSoFar, segment) ?? Guid.NewGuid().ToString();
                existing = new Group
                {
                    Name = segment,
                    Id = id,
                    ParentId = parentId ?? string.Empty
                };
                groups.Add(existing);
                result.GroupsCreated++;
            }
            else if (string.IsNullOrWhiteSpace(existing.ParentId) && !string.IsNullOrEmpty(parentId))
            {
                // Восстанавливаем родителя, если группа уже была создана без иерархии.
                existing.ParentId = parentId;
            }

            parentId = existing.Id;
        }
    }

    /// <summary>
    /// Ищет ID группы-секции в файле ibases.v8i по полному пути или имени.
    /// </summary>
    private static string? ResolveGroupIdFromFile(List<IbaseEntry> groupEntries, string fullPath, string leafName)
    {
        foreach (var e in groupEntries)
        {
            if (string.IsNullOrWhiteSpace(e.Id))
                continue;

            var eFull = BuildGroupEntryPath(e);
            if (string.Equals(eFull, fullPath, StringComparison.OrdinalIgnoreCase))
                return e.Id;
        }

        // Запасной вариант: единственная секция с таким именем.
        var byName = groupEntries
            .Where(e => string.Equals(NormalizeGroupName(e.Name), leafName, StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(e.Id))
            .ToList();
        return byName.Count == 1 ? byName[0].Id : null;
    }

    /// <summary>
    /// Строит полный путь группы-секции из Name и Folder.
    /// </summary>
    private static string BuildGroupEntryPath(IbaseEntry entry)
    {
        var folderPath = NormalizeGroupPath(entry.Group);
        var namePath = NormalizeGroupPath(entry.Name);
        if (string.IsNullOrWhiteSpace(folderPath))
            return namePath;
        if (string.IsNullOrWhiteSpace(namePath))
            return folderPath;
        if (namePath.StartsWith(folderPath + GroupHierarchyHelper.PathSeparator, StringComparison.OrdinalIgnoreCase)
            || string.Equals(namePath, folderPath, StringComparison.OrdinalIgnoreCase))
            return namePath;
        return folderPath + GroupHierarchyHelper.PathSeparator + NormalizeGroupName(entry.Name);
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

#if LINUX
        // Linux: платформа 1С хранит список баз в ~/.1C/1cestart/ibases.v8i
        // (проверено на 8.3.27). Прочие каталоги оставлены как запасные варианты
        // на случай других версий и дистрибутивов.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
        {
            candidates.Add(Path.Combine(home, ".1C", "1cestart", "ibases.v8i"));
            candidates.Add(Path.Combine(home, ".1cv8", "1CEStart", "ibases.v8i"));
            candidates.Add(Path.Combine(home, ".local", "share", "1cv8", "1CEStart", "ibases.v8i"));
            candidates.Add(Path.Combine(home, ".local", "share", "1C", "1CEStart", "ibases.v8i"));
        }

        // Учитываем явно заданный XDG_DATA_HOME.
        var xdgData = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrWhiteSpace(xdgData))
            candidates.Add(Path.Combine(xdgData, "1cv8", "1CEStart", "ibases.v8i"));
#else
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
#endif

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
        /// Версия очищается от суффикса разрядности «(32)/(64)», а разрядность
        /// сохраняется в отдельное поле Architecture.
        /// </summary>
        public Infobase ToInfobase()
        {
            var connection = ParseConnection(Connect);

            var version = Version;
            var architecture = "32-priority";
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