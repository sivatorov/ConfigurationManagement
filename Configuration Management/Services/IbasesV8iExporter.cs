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
        var entries = File.Exists(filePath) ? IbaseEntry.Parse(filePath) : new List<IbaseEntry>();

        // Существующие базы по имени и по ID 1С (для обновления на месте). Матчинг по ID —
        // основной (issue #278): база может быть переименована в приложении, а в файле
        // (после восстановления) храниться под старым именем с тем же ID. По имени — fallback.
        var existingByName = new Dictionary<string, IbaseEntry>(StringComparer.OrdinalIgnoreCase);
        var existingById = new Dictionary<string, IbaseEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (entry.IsGroup || string.IsNullOrWhiteSpace(entry.Name))
                continue;
            existingByName[entry.Name] = entry;
            if (!string.IsNullOrWhiteSpace(entry.Id))
                existingById[entry.Id.Trim()] = entry;
        }

        // Записываем базы приложения.
        foreach (var infobase in infobaseList)
        {
            if (string.IsNullOrWhiteSpace(infobase.Name))
                continue;

            var entry = ToEntry(infobase, groupList);

            var existing = FindMatchingEntry(entry, existingByName, existingById);
            if (existing is not null)
            {
                // Обновляем существующую запись файла, сохраняя её позицию и прочие ключи.
                ApplyEntryUpdate(existing, entry, existingByName);
                result.Updated++;
            }
            else
            {
                existingByName[entry.Name] = entry;
                if (!string.IsNullOrWhiteSpace(entry.Id))
                    existingById[entry.Id.Trim()] = entry;
                entries.Add(entry);
                result.Added++;
            }
        }

        // Удаляем из файла базы, которых больше нет в приложении (двусторонняя синхронизация).
        // Секции-группы в файле не трогаем — их удаление может ломать иерархию 1С.
        var appNames = new HashSet<string>(
            infobaseList.Where(b => !string.IsNullOrWhiteSpace(b.Name)).Select(b => b.Name),
            StringComparer.OrdinalIgnoreCase);
        // ID 1С баз приложения — дополнительный критерий сохранения записи: секцию файла,
        // чей ID присутствует в приложении, НЕ удаляем, даже если её имя не совпадает ни с
        // одним именем базы приложения (база могла быть переименована в приложении, а запись
        // в файле после ручного восстановления остаться под старым именем с тем же ID,
        // issue #278). Запись, обновлённая по ID на предыдущем шаге, уже переименована.
        var appIds = new HashSet<string>(
            infobaseList.Where(b => !string.IsNullOrWhiteSpace(b.Id)).Select(b => b.Id.Trim()),
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
            if (!string.IsNullOrWhiteSpace(entry.Id) && appIds.Contains(entry.Id.Trim()))
            {
                // Запись совпала с базой приложения по ID — сохраняем (имя может отличаться).
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

        // Сериализуем полный список записей, сохраняя порядок секций файла.
        // Пустая строка-разделитель между секциями не добавляется (issue #277):
        // тело каждой секции заканчивается переводом строки, поэтому следующая
        // секция начинается сразу после последней строки предыдущей.
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
        var entries = File.Exists(filePath) ? IbaseEntry.Parse(filePath) : new List<IbaseEntry>();

        // Существующие базы по имени и по ID 1С (для обновления на месте). Матчинг по ID —
        // основной (issue #278), по имени — fallback.
        var existingByName = new Dictionary<string, IbaseEntry>(StringComparer.OrdinalIgnoreCase);
        var existingById = new Dictionary<string, IbaseEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (entry.IsGroup || string.IsNullOrWhiteSpace(entry.Name))
                continue;
            existingByName[entry.Name] = entry;
            if (!string.IsNullOrWhiteSpace(entry.Id))
                existingById[entry.Id.Trim()] = entry;
        }

        var written = 0;
        foreach (var infobase in infobaseList)
        {
            if (string.IsNullOrWhiteSpace(infobase.Name))
                continue;

            var entry = ToEntry(infobase, groupList);

            var existing = FindMatchingEntry(entry, existingByName, existingById);
            if (existing is not null)
            {
                // Обновляем существующую запись файла, сохраняя её позицию.
                ApplyEntryUpdate(existing, entry, existingByName);
            }
            else
            {
                existingByName[entry.Name] = entry;
                if (!string.IsNullOrWhiteSpace(entry.Id))
                    existingById[entry.Id.Trim()] = entry;
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
    /// Ищет существующую запись файла для обновления. Основной матчинг — по ID 1С
    /// (регистронезависимо, см. issue #278): база может быть переименована в приложении,
    /// а в файле (после восстановления) храниться под старым именем с тем же ID. Если ID
    /// не заполнен или совпадение по ID не найдено, используется fallback по имени.
    /// </summary>
    private static IbaseEntry? FindMatchingEntry(
        IbaseEntry entry,
        Dictionary<string, IbaseEntry> existingByName,
        Dictionary<string, IbaseEntry> existingById)
    {
        if (!string.IsNullOrWhiteSpace(entry.Id)
            && existingById.TryGetValue(entry.Id.Trim(), out var byId))
            return byId;
        if (existingByName.TryGetValue(entry.Name, out var byName))
            return byName;
        return null;
    }

    /// <summary>
    /// Переносит значения из новой записи в существующую, сохраняя позицию секции и
    /// исходный порядок строк (включая пустые строки и неизвестные ключи, хранящиеся в
    /// <see cref="IbaseEntry.Lines"/>) — обновление значений происходит при записи на своих
    /// местах (issue #277). Connect сохраняет состав параметров исходной строки файла
    /// (<see cref="IbaseEntry.MergeConnect"/>): Usr/Pwd дописываются только если они были
    /// в исходном Connect; полная пересборка Connect — только при изменении цели подключения.
    /// Ключ режима запуска <c>App=Auto</c> из файла сохраняется как есть (issue #277): у базы,
    /// которую пользователь не трогал, режим запуска в приложении может быть производным от
    /// DefaultApp (например, в файле App=Auto + DefaultApp=ThickClient, а приложение видит
    /// «Толстый клиент»), поэтому перезапись App=Auto на App=ThickClient — нежелательная потеря
    /// данных. Не-нейтральные App и DefaultApp продолжают синхронизироваться с приложением
    /// (явно заданный режим запуска). Новые базы получают App/DefaultApp из <see cref="ToEntry"/>.
    /// При смене имени базы (матчинг по ID, issue #278) переименовывает секцию в файле
    /// и обновляет индекс по имени, чтобы не создавался дубль со старым именем.
    /// </summary>
    private static void ApplyEntryUpdate(
        IbaseEntry existing,
        IbaseEntry entry,
        Dictionary<string, IbaseEntry> existingByName)
    {
        existing.Connect = IbaseEntry.MergeConnect(existing.OriginalConnect, entry.Connect);
        existing.Group = entry.Group;
        existing.Id = entry.Id;
        existing.Version = entry.Version;
        existing.AdditionalParameters = entry.AdditionalParameters;
        // App=Auto (режим по умолчанию) в файле сохраняем — не даём приложению перезаписать
        // его производным от DefaultApp значением (issue #277). Остальные App и DefaultApp
        // синхронизируем с приложением.
        existing.App = string.Equals(existing.App, "Auto", StringComparison.OrdinalIgnoreCase)
            ? existing.App
            : entry.App;
        existing.DefaultApp = entry.DefaultApp;
        existing.Enabled = true;

        if (string.Equals(existing.Name, entry.Name, StringComparison.OrdinalIgnoreCase))
            return;

        if (!string.IsNullOrWhiteSpace(existing.Name))
            existingByName.Remove(existing.Name);
        existing.Name = entry.Name;
        existingByName[entry.Name] = existing;
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
    /// Приводит секции-группы к формату штатного стартера: Name содержит имя самой
    /// группы, а Folder — абсолютный путь родителя с ведущим «/» и прямыми слешами
    /// на всех ОС. Для корневой группы используется «/». Обратный слеш в Folder
    /// стартер под Windows воспринимает как буквальную часть имени (issue #165).
    /// Уже накопившиеся секции с полным путём сопоставляются с моделью по ID/пути и
    /// переписываются обратно в корректную пару Name + Folder.
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
        var appGroupsById = groups
            .Where(g => !string.IsNullOrWhiteSpace(g.Id))
            .GroupBy(g => g.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            if (!entry.IsGroup)
            {
                result.Add(entry);
                continue;
            }

            Group? appGroup = null;
            if (!string.IsNullOrWhiteSpace(entry.Id))
                appGroupsById.TryGetValue(entry.Id, out appGroup);

            var parsedPath = BuildGroupPath(entry);
            if (appGroup is null && !string.IsNullOrWhiteSpace(parsedPath))
                appGroupsByPath.TryGetValue(parsedPath, out appGroup);

            var canonicalPath = appGroup is null
                ? parsedPath
                : NormalizeGroupPath(GroupHierarchyHelper.GetFullPath(appGroup, groups));
            if (string.IsNullOrWhiteSpace(canonicalPath))
            {
                // Секция-группа без пути — сохраняем как есть.
                result.Add(entry);
                continue;
            }

            if (byPath.ContainsKey(canonicalPath))
                continue; // Дубликат папки — пропускаем.

            if (appGroup is not null)
            {
                entry.Name = appGroup.Name;
                var parent = groups.FirstOrDefault(g =>
                    string.Equals(g.Id, appGroup.ParentId, StringComparison.OrdinalIgnoreCase));
                entry.Group = parent is null
                    ? "/"
                    : ToStarterFolderPath(GroupHierarchyHelper.GetFullPath(parent, groups));
                if (!string.IsNullOrWhiteSpace(appGroup.Id))
                    entry.Id = appGroup.Id;
            }
            else
            {
                var (leaf, parentPath) = SplitLeafAndParent(canonicalPath);
                entry.Name = leaf;
                entry.Group = string.IsNullOrWhiteSpace(parentPath)
                    ? "/"
                    : ToStarterFolderPath(parentPath);
            }
            byPath[canonicalPath] = entry;
            result.Add(entry);
        }

        return result;
    }

    private static (string Leaf, string ParentPath) SplitLeafAndParent(string canonicalPath)
    {
        var idx = canonicalPath.LastIndexOf(GroupHierarchyHelper.PathSeparator, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return (canonicalPath.Trim(), string.Empty);
        return (
            canonicalPath.Substring(idx + GroupHierarchyHelper.PathSeparator.Length).Trim(),
            canonicalPath.Substring(0, idx).Trim());
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
                groupPath = GroupHierarchyHelper.GetFullPath(group, groups);
            }

            // Формат стартера — абсолютный путь с ведущим «/» и прямыми слешами
            // независимо от ОС: «/НАН/Весь кобошоп».
            groupPath = ToStarterFolderPath(groupPath);
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

    private static string ToStarterFolderPath(string fullPath)
    {
        var segments = SplitGroupPath(fullPath);
        return segments.Count == 0 ? "/" : "/" + string.Join("/", segments);
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
    /// Записывает запись в StringBuilder в формате секции ibases.v8i, сохраняя исходный
    /// порядок строк секции: значения управляемых ключей обновляются на своих местах,
    /// отсутствующие добавляются в каноническом порядке в конец, а пустые строки и
    /// неизвестные ключи переносятся дословно (<see cref="IbaseEntry.WriteBodyTo"/>,
    /// issue #277). Пустая строка-разделитель между секциями НЕ вставляется (issue #277):
    /// тело секции заканчивается переводом строки, поэтому следующая секция начинается
    /// сразу после последней строки предыдущей — как это делает стартер 1С.
    /// </summary>
    /// <param name="sb">Приёмник текста.</param>
    /// <param name="entry">Запись файла.</param>
    private static void WriteEntry(StringBuilder sb, IbaseEntry entry)
    {
        sb.Append('[').Append(entry.Name).AppendLine("]");
        entry.WriteBodyTo(sb);
    }
}
