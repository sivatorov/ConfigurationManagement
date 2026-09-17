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

    /// <summary>Количество удалённых из файла баз (есть в файле, нет в приложении).</summary>
    public int Removed { get; set; }
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
        foreach (var entry in entries)
        {
            if (!entry.IsGroup && !string.IsNullOrWhiteSpace(entry.Name))
            {
                existingByName[entry.Name] = entry;
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

        // Удаляем из файла базы, которых больше нет в приложении (двусторонняя синхронизация).
        // Секции-группы в файле не трогаем — их удаление может ломать иерархию 1С.
        var appNames = new HashSet<string>(
            infobaseList.Where(b => !string.IsNullOrWhiteSpace(b.Name)).Select(b => b.Name),
            StringComparer.OrdinalIgnoreCase);
        var kept = new List<IbaseEntry>(entries.Count);
        foreach (var entry in entries)
        {
            if (entry.IsGroup || string.IsNullOrWhiteSpace(entry.Name))
            {
                kept.Add(entry);
                continue;
            }
            if (appNames.Contains(entry.Name))
            {
                kept.Add(entry);
                continue;
            }
            result.Removed++;
        }
        entries = kept;

        // Устраняем дубликаты секций с одинаковым именем. Имя секции в файле 1С
        // уникально (это ключ базы). Дубли появляются, например, когда имя группы
        // приложения совпадает с именем базы (в файл попадали две секции с одним
        // именем — группа и база), либо когда в файле уже были повторные записи.
        var entriesBeforeDedup = entries.Count;
        entries = Deduplicate(entries);

        // Канонизируем и дедуплицируем секции-группы по ПОЛНОМУ пути папки (issue #165).
        // Файл, переписанный штатным стартером 1С, может содержать одну и ту же
        // вложенную папку в двух представлениях: с именем-листом
        // (Name=«Бухгалтерия», Folder=«Учёт») и с полным путём в заголовке секции
        // (Name=«Учёт\Бухгалтерия»). Сопоставление по одному имени (см. Deduplicate)
        // такие пары НЕ склеивает, поэтому обе секции попадали в файл, и стартер под
        // Windows показывал их как две отдельные папки. Здесь каждая секция приводится
        // к нативному виду стартера (Name — полный путь, Folder=/), а совпавшие по
        // полному пути устраняются.
        var groupSectionsBefore = entries.Count(e => e.IsGroup);
        entries = NormalizeAndDedupeGroupSections(entries, groupList);
        var groupDupesRemoved = groupSectionsBefore - entries.Count(e => e.IsGroup);

        // Лог экспорта (issue #165): количество записей, баз, групп и устранённых
        // дубликатов секций и секций-групп, а также канонические пути папок — по
        // журналу видно, на каком шаге возникало дублирование вложенных папок под
        // Windows при повторной синхронизации со штатным стартером.
        var canonicalGroupPaths = entries
            .Where(e => e.IsGroup)
            .Select(BuildGroupPath)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
        LogInfo(
            $"Экспорт ibases.v8i: записей стало {entries.Count} " +
            $"(баз добавлено {result.Added}, обновлено {result.Updated}, удалено {result.Removed}, " +
            $"групп создано {result.GroupsCreated}), " +
            $"устранено дубликатов секций {entriesBeforeDedup - entries.Count}, " +
            $"устранено дубликатов групп-секций {groupDupesRemoved}, " +
            $"групп-секций в файле: [{string.Join("; ", canonicalGroupPaths)}]");

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
    /// Дописывает переданные базы в файл ibases.v8i, добавляя отсутствующие записи и
    /// обновляя уже существующие (по совпадению имени, без учёта регистра). В отличие от
    /// полного <see cref="Export"/>, чужие записи (которых нет в переданном списке) из
    /// файла НЕ удаляются — только дописываются/обновляются выбранные базы.
    /// </summary>
    /// <param name="filePath">Путь к файлу ibases.v8i.</param>
    /// <param name="infobases">Список баз, которые нужно записать в файл.</param>
    /// <param name="groups">Список групп приложения (для разрешения пути группы базы).</param>
    /// <returns>Количество записанных в файл баз (добавленных или обновлённых).</returns>
    public static int AddInfobasesToFile(string filePath, IEnumerable<Infobase> infobases, IEnumerable<Group> groups)
    {
        var infobaseList = infobases.ToList();
        var groupList = groups.ToList();

        // Существующие записи файла. Если файла нет — начинаем с пустого списка,
        // чтобы добавить только выбранные базы и не затирать потенциально чужие данные.
        var entries = File.Exists(filePath) ? Parse(filePath) : new List<IbaseEntry>();

        // Существующие базы по имени (для обновления на месте).
        var existingByName = new Dictionary<string, IbaseEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (!entry.IsGroup && !string.IsNullOrWhiteSpace(entry.Name))
                existingByName[entry.Name] = entry;
        }

        var written = 0;
        foreach (var infobase in infobaseList)
        {
            if (string.IsNullOrWhiteSpace(infobase.Name))
                continue;

            var entry = ToEntry(infobase, groupList);

            if (existingByName.TryGetValue(infobase.Name, out var existing))
            {
                // Обновляем существующую запись файла, сохраняя её позицию.
                existing.Connect = entry.Connect;
                existing.Group = entry.Group;
                existing.Id = entry.Id;
                existing.Version = entry.Version;
                existing.AdditionalParameters = entry.AdditionalParameters;
                existing.App = entry.App;
                existing.DefaultApp = entry.DefaultApp;
                existing.Enabled = true;
            }
            else
            {
                existingByName[infobase.Name] = entry;
                entries.Add(entry);
            }

            written++;
        }

        // Устраняем дубликаты секций с одинаковым именем и приводим секции групп к
        // нативному формату стартера (как в полном Export).
        entries = Deduplicate(entries);
        entries = NormalizeAndDedupeGroupSections(entries, groupList);

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
        return written;
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
    /// Приводит секции-группы файла ibases.v8i к единому каноническому виду и устраняет
    /// дубликаты по ПОЛНОМУ пути папки. Файл, переписанный штатным стартером 1С, может
    /// содержать одну и ту же вложенную папку в двух представлениях: с именем-листом
    /// (Name=«Бухгалтерия», Folder=«Учёт») и в нативном формате стартера — с полным
    /// путём в заголовке секции (Name=«Учёт\Бухгалтерия», Folder=«/»). Сопоставление
    /// по одному имени (см. <see cref="Deduplicate"/>) такие пары не склеивает.
    ///
    /// Важно не только удалить дубликат, но и записать оставшуюся секцию в нативном
    /// формате. Если снова сохранить Name=«Бухгалтерия», Folder=«Учёт», штатный стартер
    /// при следующем запуске добавит рядом Name=«Учёт\Бухгалтерия», Folder=«/», и дубль
    /// вернётся. Именно этот цикл воспроизводился на пользовательском файле issue #165.
    /// Идентификатор секции синхронизируется с группой приложения по полному пути.
    /// </summary>
    private static List<IbaseEntry> NormalizeAndDedupeGroupSections(
        List<IbaseEntry> entries,
        List<Group> groups)
    {
        var byPath = new Dictionary<string, IbaseEntry>(StringComparer.OrdinalIgnoreCase);
        var result = new List<IbaseEntry>(entries.Count);
        var appGroupsByPath = groups
            .Where(g => !string.IsNullOrWhiteSpace(g.Name))
            .GroupBy(
                g => NormalizeGroupPath(GroupHierarchyHelper.GetFullPath(g, groups)),
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            if (!entry.IsGroup)
            {
                result.Add(entry);
                continue;
            }

            var canonicalPath = BuildGroupPath(entry);
            if (string.IsNullOrWhiteSpace(canonicalPath))
            {
                // Секция-группа без пути — сохраняем как есть.
                result.Add(entry);
                continue;
            }

            if (byPath.ContainsKey(canonicalPath))
                continue; // Дубликат папки — пропускаем.

            // Штатный стартер хранит путь группы целиком в имени секции, а Folder=/
            // обозначает корень дерева. Это относится и к корневым, и к вложенным группам.
            entry.Name = ToFolderPath(canonicalPath);
            entry.Group = "/";
            if (appGroupsByPath.TryGetValue(canonicalPath, out var appGroup)
                && !string.IsNullOrWhiteSpace(appGroup.Id))
            {
                entry.Id = appGroup.Id;
            }
            byPath[canonicalPath] = entry;
            result.Add(entry);
        }

        return result;
    }

    /// <summary>
    /// Строит канонический полный путь группы-секции из Name и Folder.
    /// </summary>
    private static string BuildGroupPath(IbaseEntry entry)
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
    /// Нормализует путь группы: разделители «/» и «\» → внутренний « / », пробелы убираются.
    /// </summary>
    private static string NormalizeGroupPath(string group)
    {
        var segments = SplitGroupPath(group);
        return string.Join(GroupHierarchyHelper.PathSeparator, segments);
    }

    /// <summary>
    /// Возвращает одиночное имя (последний сегмент пути) группы.
    /// </summary>
    private static string NormalizeGroupName(string name)
    {
        var segments = SplitGroupPath(name);
        return segments.Count > 0 ? segments[^1] : string.Empty;
    }

    /// <summary>
    /// Разбивает путь группы на сегменты по разделителям «/» и «\».
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

            // Важно (issue #165): ключ Folder в файле ibases.v8i использует НАТИВНЫЙ
            // разделитель платформы («\» на Windows, «/» на Linux), а не внутренний
            // « / » приложения. Если оставить внутренний разделитель, то для вложенных
            // папок (путь из нескольких сегментов) 1С-стартер увидит
            // Folder=«Учёт / Бухгалтерия» как имя ОДНОЙ литеральной папки и создаст
            // её дубликат рядом с правильно вложенной «Бухгалтерия». Для плоских папок
            // (один сегмент, без разделителя) бага не видна — потому проблема проявлялась
            // только у вложенных папок. Всегда приводим Folder к нативному виду (для
            // Linux это именно «/»: захардкоженный «\» давал дубль с обратным слешем).
            groupPath = ToFolderPath(groupPath);
        }

        // Версия записывается без суффикса разрядности «(32)/(64)»: разрядность хранится
        // в отдельном поле базы (Architecture) и в ibases.v8i не должна попадать в Version.
        var platformVersion = CleanPlatformVersion(infobase.PlatformVersion);

        return new IbaseEntry
        {
            Name = infobase.Name,
            Connect = BuildConnectionString(infobase),
            Group = groupPath,
            Enabled = true,
            Id = infobase.Id,
            Version = platformVersion,
            AdditionalParameters = infobase.LaunchParameters,
            App = MapLaunchModeBack(infobase.LaunchMode),
            DefaultApp = MapLaunchModeBack(infobase.LaunchMode)
        };
    }

    /// <summary>
    /// Убирает суффикс разрядности из строки версии платформы:
    /// «8.3.27.1688 (64)» → «8.3.27.1688». Версия без суффикса возвращается без изменений.
    /// </summary>
    private static string CleanPlatformVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return version;

        PlatformVersionService.ParseVariant(version, out var cleanVersion, out _);
        return string.IsNullOrWhiteSpace(cleanVersion) ? version : cleanVersion;
    }

    /// <summary>
    /// Преобразует полный путь группы из внутреннего представления приложения
    /// (разделитель « / ») в формат ключа Folder файла ibases.v8i с НАТИВНЫМ
    /// разделителем платформы: «\» на Windows и «/» на Linux (порождён через
    /// <see cref="Path.DirectorySeparatorChar"/>). Штатный стартер 1С строит иерархию
    /// вложенных папок из ключа Folder, используя разделитель той ОС, на которой
    /// работает платформа. Жёстко зашитый «\» на Linux заставлял стартер видеть
    /// Folder=«Учёт\Бухгалтерия» как имя одной литеральной папки и создавать её
    /// дубликат рядом с правильно вложенной «Бухгалтерией» (issue #165).
    /// </summary>
    private static string ToFolderPath(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
            return string.Empty;

        var separator = Path.DirectorySeparatorChar.ToString();
        var segments = fullPath.Split(
            new[] { GroupHierarchyHelper.PathSeparator, "/", "\\" },
            StringSplitOptions.RemoveEmptyEntries);
        return string.Join(separator, segments.Select(s => s.Trim()));
    }

    /// <summary>Пишет информационное сообщение экспорта в файловый лог (issue #165).</summary>
    private static void LogInfo(string message)
    {
        try
        {
            AppServices.GetRequiredService<IAppLogger>().Info(message);
        }
        catch
        {
            // Логирование не должно ломать экспорт.
        }
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
            sb.Append("File=\"").Append(EscapeConnectValue(connection.FilePath)).Append('"');
        }
        else if (connection.Type == ConnectionType.WebServer)
        {
            sb.Append("WS=\"").Append(EscapeConnectValue(connection.WebUrl)).Append('"');
        }
        else
        {
            // Порт включаем в Srvr, если он нестандартный (как принято в 1С: host:port).
            sb.Append("Srvr=\"").Append(EscapeConnectValue(connection.GetServerWithPort())).Append("\";");
            sb.Append("Ref=\"").Append(EscapeConnectValue(connection.DatabaseName)).Append('"');
            if (!string.IsNullOrWhiteSpace(connection.User))
            {
                sb.Append(";Usr=\"").Append(EscapeConnectValue(connection.User)).Append('"');
                if (!string.IsNullOrWhiteSpace(connection.Password))
                {
                    sb.Append(";Pwd=\"").Append(EscapeConnectValue(connection.Password)).Append('"');
                }
            }
            // Примечание (issue #94): блокировка фоновых заданий не пишется в ibases.v8i —
            // документированный параметр SchJobDn действует только при создании базы и не влияет
            // на уже созданную ИБ, поэтому включать его в список баз не имеет смысла.
        }

        // Завершающая точка с запятой обязательна: EDT (и ряд других средств 1С)
        // не может открыть базу, если строка подключения не заканчивается знаком «;».
        var result = sb.ToString().TrimEnd();
        if (!string.IsNullOrEmpty(result) && !result.EndsWith(";"))
            result += ";";

        return result;
    }

    /// <summary>
    /// Экранирует значение строки подключения 1С: кавычка внутри значения удваивается.
    /// При импорте ibases.v8i удвоение разворачивается обратно разборщиком строки подключения.
    /// </summary>
    private static string EscapeConnectValue(string value) => value.Replace("\"", "\"\"");

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
