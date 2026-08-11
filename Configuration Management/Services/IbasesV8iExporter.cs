using System.IO;
using System.Text;
using Configuration_Management.Models;

namespace Configuration_Management.Services;

/// <summary>
/// Результат экспорта списка баз в файл ibases.v8i.
/// </summary>
public class IbasesExportResult
{
    /// <summary>Количество добавленных в файл новых баз.</summary>
    public int Added { get; set; }

    /// <summary>Количество обновлённых в файле существующих баз.</summary>
    public int Updated { get; set; }

    /// <summary>Количество созданных новых групп.</summary>
    public int GroupsCreated { get; set; }
}

/// <summary>
/// Сервис экспорта списка информационных баз приложения в стандартный файл 1С ibases.v8i.
/// </summary>
public static class IbasesV8iExporter
{
    /// <summary>
    /// Выгружает базы приложения в файл ibases.v8i, добавляя новые записи и обновляя
    /// существующие (по совпадению имени базы). Группы приложения не создаются в файле
    /// как новые секции (чтобы не появлялись лишние папки); существующие секции-группы
    /// только обновляются (имя и иерархия).
    /// </summary>
    /// <param name="filePath">Путь к файлу ibases.v8i.</param>
    /// <param name="infobases">Список информационных баз приложения.</param>
    /// <param name="groups">Список групп приложения.</param>
    /// <returns>Результат экспорта.</returns>
    public static IbasesExportResult Export(string filePath, IEnumerable<Infobase> infobases, IEnumerable<Group> groups)
    {
        var result = new IbasesExportResult();

        var infobaseList = infobases.ToList();
        var groupList = groups.ToList();

        // Существующие записи файла. Читаем файл, чтобы не затирать данные,
        // которых нет в приложении.
        var entries = File.Exists(filePath) ? Parse(filePath) : new List<IbaseEntry>();

        // Существующие базы по имени (для обновления на месте).
        var existingByName = new Dictionary<string, IbaseEntry>(StringComparer.OrdinalIgnoreCase);
        // Существующие секции-группы по имени.
        var groupSectionByName = new Dictionary<string, IbaseEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            if (entry.IsGroup)
            {
                if (!string.IsNullOrWhiteSpace(entry.Name))
                    groupSectionByName[entry.Name] = entry;
            }
            else if (!string.IsNullOrWhiteSpace(entry.Name))
            {
                existingByName[entry.Name] = entry;
            }
        }

        // Обновляем секции-группы, уже существующие в файле. Новые секции-группы при
        // выгрузке не создаются: папки в 1С отображаются по Folder-ссылкам баз, поэтому
        // добавление явных секций приводило бы к появлению лишних групп, которых не было
        // в исходном файле (например, групп, возникших из Folder-ссылок при импорте).
        foreach (var group in groupList)
        {
            if (string.IsNullOrWhiteSpace(group.Name))
                continue;

            var entry = ToGroupEntry(group, groupList);

            if (groupSectionByName.TryGetValue(group.Name, out var existingGroup))
            {
                // Обновляем существующую секцию-группу (имя и иерархия).
                existingGroup.Id = entry.Id;
                existingGroup.Group = entry.Group;
            }
        }

        // Записываем базы приложения.
        foreach (var infobase in infobaseList)
        {
            if (string.IsNullOrWhiteSpace(infobase.Name))
                continue;

            var entry = ToEntry(infobase, groupList);

            if (existingByName.TryGetValue(infobase.Name, out var existing))
            {
                // Обновляем существующую запись файла, сохраняя её позицию и прочие ключи.
                existing.Connect = entry.Connect;
                existing.Group = entry.Group;
                existing.Id = entry.Id;
                existing.Version = entry.Version;
                existing.AdditionalParameters = entry.AdditionalParameters;
                existing.App = entry.App;
                existing.DefaultApp = entry.DefaultApp;
                existing.Enabled = true;
                result.Updated++;
            }
            else
            {
                existingByName[infobase.Name] = entry;
                entries.Add(entry);
                result.Added++;
            }
        }

        // Устраняем дубликаты секций с одинаковым именем. Имя секции в файле 1С
        // уникально (это ключ базы). Дубли появляются, например, когда имя группы
        // приложения совпадает с именем базы (в файл попадали две секции с одним
        // именем — группа и база), либо когда в файле уже были повторные записи.
        entries = Deduplicate(entries);

        // Сериализуем полный список записей.
        var sb = new StringBuilder();
        foreach (var entry in entries)
        {
            WriteEntry(sb, entry);
        }

        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(filePath, sb.ToString(), Encoding.Default);
        return result;
    }

    /// <summary>
    /// Устраняет дубликаты секций с одинаковым именем. Имя секции в файле 1С уникально.
    /// При конфликте записи-группы (без строки подключения) и записи-базы (с Connect)
    /// приоритет сохраняется за базой; для одинаковых по типу записей — за первой
    /// встреченной. Порядок записей сохраняется.
    /// </summary>
    private static List<IbaseEntry> Deduplicate(List<IbaseEntry> entries)
    {
        var byName = new Dictionary<string, IbaseEntry>(StringComparer.OrdinalIgnoreCase);
        var result = new List<IbaseEntry>(entries.Count);

        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Name))
            {
                // Записи без имени не участвуют в сопоставлении и сохраняются как есть.
                result.Add(entry);
                continue;
            }

            if (byName.TryGetValue(entry.Name, out var existing))
            {
                // Если уже хранится запись-группа (без Connect), а новая — база (с Connect),
                // заменяем группу базой.
                if (existing.IsGroup && !entry.IsGroup)
                {
                    var idx = result.IndexOf(existing);
                    result[idx] = entry;
                    byName[entry.Name] = entry;
                }
                // В остальных случаях (дубликат базы, дубликат группы) первую сохраняем,
                // последующие пропускаем.
            }
            else
            {
                byName[entry.Name] = entry;
                result.Add(entry);
            }
        }

        return result;
    }

    /// <summary>
    /// Преобразует базу приложения в запись ibases.v8i.
    /// </summary>
    private static IbaseEntry ToEntry(Infobase infobase, List<Group> groups)
    {
        var groupPath = infobase.Group;
        if (!string.IsNullOrWhiteSpace(groupPath))
        {
            var group = groups.FirstOrDefault(g =>
                string.Equals(GroupHierarchyHelper.GetFullPath(g, groups), groupPath, StringComparison.OrdinalIgnoreCase));
            if (group is not null)
            {
                // Используем полный путь группы для Folder.
                groupPath = GroupHierarchyHelper.GetFullPath(group, groups);
            }
        }

        return new IbaseEntry
        {
            Name = infobase.Name,
            Connect = BuildConnectionString(infobase),
            Group = groupPath,
            Enabled = true,
            Id = infobase.Id,
            Version = infobase.PlatformVersion,
            AdditionalParameters = infobase.LaunchParameters,
            App = MapLaunchModeBack(infobase.LaunchMode),
            DefaultApp = MapLaunchModeBack(infobase.LaunchMode)
        };
    }

    /// <summary>
    /// Преобразует группу приложения в запись ibases.v8i (секцию-группу без строки подключения).
    /// Имя секции — одиночное наименование группы, вложенность задаётся ключом Folder
    /// (полный путь с разделителем «\»), как в типовом файле 1С.
    /// </summary>
    private static IbaseEntry ToGroupEntry(Group group, List<Group> groups)
    {
        var entry = new IbaseEntry
        {
            Name = group.Name,
            Id = group.Id,
            Enabled = true
        };

        if (!string.IsNullOrWhiteSpace(group.ParentId))
        {
            var parent = groups.FirstOrDefault(g =>
                string.Equals(g.Id, group.ParentId, StringComparison.OrdinalIgnoreCase));
            if (parent is not null)
            {
                entry.Group = ToFolderPath(GroupHierarchyHelper.GetFullPath(parent, groups));
            }
        }

        return entry;
    }

    /// <summary>
    /// Преобразует полный путь группы из внутреннего представления приложения
    /// (разделитель « / ») в формат ключа Folder файла ibases.v8i (разделитель «\»).
    /// </summary>
    private static string ToFolderPath(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
            return string.Empty;

        var segments = fullPath.Split(
            new[] { GroupHierarchyHelper.PathSeparator, "/", "\\" },
            StringSplitOptions.RemoveEmptyEntries);
        return string.Join("\\", segments.Select(s => s.Trim()));
    }

    /// <summary>
    /// Строит строку подключения из настроек базы.
    /// </summary>
    private static string BuildConnectionString(Infobase infobase)
    {
        var connection = infobase.Connection;
        var sb = new StringBuilder();

        if (connection.Type == ConnectionType.File)
        {
            sb.Append("File=\"").Append(connection.FilePath).Append('"');
        }
        else
        {
            sb.Append("Srvr=\"").Append(connection.Server).Append("\";");
            sb.Append("Ref=\"").Append(connection.DatabaseName).Append('"');
            if (!string.IsNullOrWhiteSpace(connection.User))
            {
                sb.Append(";Usr=\"").Append(connection.User).Append('"');
                if (!string.IsNullOrWhiteSpace(connection.Password))
                {
                    sb.Append(";Pwd=\"").Append(connection.Password).Append('"');
                }
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Преобразует режим запуска приложения в значение ключей App/DefaultApp файла ibases.v8i.
    /// </summary>
    private static string MapLaunchModeBack(string launchMode)
    {
        switch ((launchMode ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "тонкий клиент":
            case "thinclient":
                return "ThinClient";
            case "толстый клиент":
            case "thickclient":
                return "ThickClient";
            case "веб-клиент":
            case "webclient":
                return "WebClient";
            default:
                return "Auto";
        }
    }

    /// <summary>
    /// Записывает запись в StringBuilder в формате секции ibases.v8i.
    /// </summary>
    private static void WriteEntry(StringBuilder sb, IbaseEntry entry)
    {
        if (sb.Length > 0)
        {
            sb.AppendLine();
        }

        sb.Append('[').Append(entry.Name).AppendLine("]");

        if (!string.IsNullOrWhiteSpace(entry.Id))
        {
            sb.Append("ID=").AppendLine(entry.Id);
        }
        if (!entry.Enabled)
        {
            sb.Append("Enable=0").AppendLine();
        }
        if (!string.IsNullOrWhiteSpace(entry.Group))
        {
            sb.Append("Folder=").AppendLine(entry.Group);
        }
        if (!string.IsNullOrWhiteSpace(entry.Connect))
        {
            sb.Append("Connect=").AppendLine(entry.Connect);
        }
        if (!string.IsNullOrWhiteSpace(entry.App))
        {
            sb.Append("App=").AppendLine(entry.App);
        }
        if (!string.IsNullOrWhiteSpace(entry.DefaultApp))
        {
            sb.Append("DefaultApp=").AppendLine(entry.DefaultApp);
        }
        if (!string.IsNullOrWhiteSpace(entry.Version))
        {
            sb.Append("Version=").AppendLine(entry.Version);
        }
        if (!string.IsNullOrWhiteSpace(entry.AdditionalParameters))
        {
            sb.Append("AdditionalParameters=").AppendLine(entry.AdditionalParameters);
        }
    }

    /// <summary>
    /// Разбирает файл ibases.v8i на список записей (используется для чтения существующего файла).
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
                    current.Enabled = value.Trim() != "0";
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
            }
        }

        return entries;
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
        public string App { get; set; } = string.Empty;
        public string DefaultApp { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string AdditionalParameters { get; set; } = string.Empty;

        public bool IsGroup => string.IsNullOrWhiteSpace(Connect);
    }
}