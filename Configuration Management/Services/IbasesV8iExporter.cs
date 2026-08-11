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
    /// Выгружает базы и группы приложения в файл ibases.v8i, добавляя новые записи
    /// и обновляя существующие (по совпадению имени базы). Группы записываются
    /// как секции без строки подключения.
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

        // Существующие записи файла (базы), ключ — имя базы.
        var existingByName = new Dictionary<string, IbaseEntry>(StringComparer.OrdinalIgnoreCase);

        var sb = new StringBuilder();

        // Читаем существующий файл, чтобы не затирать данные, которых нет в приложении.
        if (File.Exists(filePath))
        {
            foreach (var entry in Parse(filePath))
            {
                // Сохраняем существующие базы и группы как есть.
                WriteEntry(sb, entry);
                if (!entry.IsGroup && !string.IsNullOrWhiteSpace(entry.Name))
                {
                    existingByName[entry.Name] = entry;
                }
            }
        }

        // Собираем секции-группы из иерархии групп приложения.
        foreach (var group in groupList)
        {
            var path = GroupHierarchyHelper.GetFullPath(group, groupList);
            if (string.IsNullOrWhiteSpace(path))
                continue;

            var entry = new IbaseEntry
            {
                Name = path,
                Id = group.Id,
                Enabled = true
            };

            // Группа уже записана — пропускаем (это может быть база с таким же именем,
            // но группа — это секция без Connect, поэтому при совпадении не перезаписываем базу).
            if (HasEntry(sb, entry.Name, isGroup: true))
                continue;

            WriteEntry(sb, entry);
            result.GroupsCreated++;
        }

        // Записываем базы приложения.
        foreach (var infobase in infobaseList)
        {
            var existing = existingByName.TryGetValue(infobase.Name, out var e) ? e : null;

            var entry = ToEntry(infobase, groupList);

            if (existing is null)
            {
                WriteEntry(sb, entry);
                result.Added++;
            }
            else
            {
                // Обновляем существующую запись файла, сохраняя её позицию.
                var existingText = SerializeEntry(existing);
                var newText = SerializeEntry(entry);
                if (existingText != newText)
                {
                    sb.Replace(existingText, newText);
                    result.Updated++;
                }
            }
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
    /// Сериализует запись в строку для сравнения (используется при обновлении существующей базы).
    /// </summary>
    private static string SerializeEntry(IbaseEntry entry)
    {
        var sb = new StringBuilder();
        WriteEntry(sb, entry);
        return sb.ToString();
    }

    /// <summary>
    /// Проверяет, присутствует ли в строке записи уже секция с указанным именем.
    /// </summary>
    private static bool HasEntry(StringBuilder sb, string name, bool isGroup)
    {
        // Для проверки наличия группы достаточно поискать заголовок секции [Имя].
        var marker = "[" + name + "]";
        return sb.ToString().Contains(marker, StringComparison.OrdinalIgnoreCase);
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