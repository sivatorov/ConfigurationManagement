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
    /// обновляет существующие (по совпадению ID 1С, иначе — по имени) и создаёт
    /// недостающие группы.
    /// </summary>
    /// <param name="filePath">Путь к файлу ibases.v8i.</param>
    /// <param name="infobases">Коллекция баз, в которую выполняется импорт.</param>
    /// <param name="groups">Коллекция групп, в которую добавляются недостающие группы.</param>
    /// <param name="deletedEmptyGroupPaths">Полные пути пустых групп, удалённых пользователем
    /// вручную: при импорте такие группы не пересоздаются, пока под путём нет баз из файла
    /// (issue #327). Может быть null — тогда все группы файла создаются как раньше.</param>
    /// <returns>Результат импорта.</returns>
    public static IbasesImportResult Import(
        string filePath,
        IList<Infobase> infobases,
        IList<Group> groups,
        IReadOnlyCollection<string>? deletedEmptyGroupPaths = null)
    {
        var result = new IbasesImportResult();

        if (!File.Exists(filePath))
            return result;

        var entries = IbaseEntry.Parse(filePath);

        // В штатном ibases.v8i Folder — абсолютный путь с ведущим «/» и прямыми
        // слешами. Старые ошибочные варианты и ссылки по имени тоже нормализуем.
        ResolveFolderReferences(entries);

        // Пути удалённых пользователем пустых групп приводим к каноническому виду
        // (регистронезависимо), чтобы сравнивать их с путями, строящимися при импорте.
        ISet<string>? deletedPaths = null;
        if (deletedEmptyGroupPaths is not null)
        {
            deletedPaths = deletedEmptyGroupPaths
                .Select(NormalizeGroupPath)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        // Диагностика импорта групп (issue #351): состояние коллекции и фильтра
        // удалённых пользователем пустых групп до применения изменений. Это помогает
        // понять, почему папки «пропадают»: были ли группы в коллекции изначально,
        // не вернулись ли они из deleted_groups.json и сколько баз осталось без группы.
        LogInfo(
            $"Импорт ibases.v8i: групп в коллекции {groups.Count}, удалённых путей " +
            $"(deleted_groups.json): [{(deletedPaths is null or { Count: 0 }
                ? "-"
                : string.Join("; ", deletedPaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase)))}]");

        // Создаём недостающие группы из импортируемых баз.
        var groupsBefore = groups.Count;
        EnsureGroups(entries, groups, result, deletedPaths);

        // Канонические пути групп после импорта — для диагностики дублирования
        // вложенных папок при синхронизации со штатным стартером (issue #165).
        var groupPathsAfter = groups
            .Select(g => NormalizeGroupPath(GroupHierarchyHelper.GetFullPath(g, groups)))
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var basesWithoutGroup = infobases.Count(b => string.IsNullOrWhiteSpace(b.Group));
        LogInfo(
            $"Импорт ibases.v8i: групп было {groupsBefore}, стало {groups.Count}, " +
            $"создано {result.GroupsCreated}, удалено дубликатов {Math.Max(0, result.GroupsCreated + groupsBefore - groups.Count)}, " +
            $"баз без группы {basesWithoutGroup}, " +
            $"пути групп: [{string.Join("; ", groupPathsAfter)}]");

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

            // Сначала сопоставляем по ID 1С (issue #278): в приложении база может быть
            // переименована, а в файле (после восстановления) храниться под старым именем
            // с тем же ID. По имени — только как fallback.
            Infobase? existing = null;
            if (!string.IsNullOrWhiteSpace(entry.Id))
            {
                existing = infobases.FirstOrDefault(b =>
                    !string.IsNullOrWhiteSpace(b.Id)
                    && string.Equals(b.Id.Trim(), entry.Id.Trim(), StringComparison.OrdinalIgnoreCase));
            }
            existing ??= infobases.FirstOrDefault(b =>
                string.Equals(b.Name, entry.Name, StringComparison.OrdinalIgnoreCase));

            if (existing is null)
            {
                // Новая база — добавляем.
                infobases.Add(entry.ToInfobase());
                result.Added++;
            }
            else
            {
                // Существующая база — обновляем настройки подключения, группу, имя, ID базы 1С,
                // версию платформы и режим запуска. Логин/пароль из приложения сохраняем,
                // если в файле они пустые (ibases.v8i часто не хранит пароль).
                var imported = entry.ToInfobase();
                var prevUser = existing.Connection?.User ?? string.Empty;
                var prevPassword = existing.Connection?.Password ?? string.Empty;
                var prevAuth = existing.Connection?.AuthenticationMode ?? AuthenticationMode.Prompt;

                // Имя возвращаем из файла (issue #278): после ручного восстановления ibases.v8i
                // база в приложении должна снова называться как в файле — «информация приезжает
                // обратно». База уже сопоставлена по ID выше, поэтому переименование не создаёт дубль.
                existing.Name = entry.Name;

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
    /// Разворачивает Folder из формата штатного стартера в полные внутренние пути.
    /// Например, [Весь кобошоп] Folder=/НАН и база с
    /// Folder=/НАН/Весь кобошоп преобразуются в «НАН / Весь кобошоп».
    /// Также понимает ошибочный старый вид [НАН\Весь кобошоп] Folder=/, чтобы один
    /// экспорт новой версией мог восстановить файл пользователя.
    /// </summary>
    private static void ResolveFolderReferences(List<IbaseEntry> entries)
    {
        var groupEntries = entries
            .Where(e => e.IsGroup && e.Enabled && !string.IsNullOrWhiteSpace(e.Name))
            .ToList();
        var groupsBySectionName = groupEntries
            .GroupBy(e => e.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var pathByEntry = new Dictionary<IbaseEntry, string>();
        var resolving = new HashSet<IbaseEntry>();

        string ResolvePath(IbaseEntry entry)
        {
            if (pathByEntry.TryGetValue(entry, out var cached))
                return cached;

            var namePath = NormalizeGroupPath(entry.Name);
            if (!resolving.Add(entry))
                return namePath;

            var folderReference = entry.Group.Trim();
            string fullPath;
            if (string.IsNullOrWhiteSpace(NormalizeGroupPath(folderReference)))
            {
                fullPath = namePath;
            }
            else if (groupsBySectionName.TryGetValue(folderReference, out var parent)
                     && !ReferenceEquals(parent, entry))
            {
                var parentPath = ResolvePath(parent);
                var leaf = NormalizeGroupName(entry.Name);
                fullPath = string.IsNullOrWhiteSpace(parentPath)
                    ? leaf
                    : parentPath + GroupHierarchyHelper.PathSeparator + leaf;
            }
            else
            {
                // Совместимость со старыми файлами, где Folder ошибочно содержал путь.
                var parentPath = NormalizeGroupPath(folderReference);
                var leaf = NormalizeGroupName(entry.Name);
                fullPath = string.IsNullOrWhiteSpace(parentPath)
                    ? namePath
                    : parentPath + GroupHierarchyHelper.PathSeparator + leaf;
            }

            resolving.Remove(entry);
            pathByEntry[entry] = fullPath;
            return fullPath;
        }

        foreach (var groupEntry in groupEntries)
            ResolvePath(groupEntry);

        // Сначала переводим ссылки баз, пока имена секций ещё не канонизированы.
        foreach (var entry in entries.Where(e => !e.IsGroup))
        {
            var folderReference = entry.Group.Trim();
            if (groupsBySectionName.TryGetValue(folderReference, out var groupEntry)
                && pathByEntry.TryGetValue(groupEntry, out var groupPath))
            {
                entry.Group = groupPath;
            }
            else
            {
                entry.Group = NormalizeGroupPath(folderReference);
            }
        }

        // Дальнейшая логика импортёра работает с каноническими Name + полным путём родителя.
        foreach (var groupEntry in groupEntries)
        {
            var (leaf, parentPath) = SplitLeafAndParent(pathByEntry[groupEntry]);
            groupEntry.Name = leaf;
            groupEntry.Group = parentPath;
        }
    }

    /// <summary>
    /// Определяет уникальные группы из импортируемых записей и добавляет
    /// недостающие группы в коллекцию.
    /// </summary>
    /// <param name="deletedGroupPaths">Канонические полные пути удалённых пользователем
    /// пустых групп (регистронезависимо) или null, если такой фильтр не задан.</param>
    private static void EnsureGroups(
        List<IbaseEntry> entries,
        IList<Group> groups,
        IbasesImportResult result,
        ISet<string>? deletedGroupPaths = null)
    {
        // Группы из файла ibases.v8i — это секции без строки подключения (Connect),
        // у которых есть собственный ID. Также учитываем группы, на которые
        // ссылаются базы через ключ Folder.
        var groupEntries = entries
            .Where(e => e.IsGroup && e.Enabled)
            .ToList();

        // Канонизируем и дедуплицируем секции-группы по ПОЛНОМУ пути (issue #165).
        // Файл ibases.v8i, переписанный штатным стартером 1С, может содержать одну и ту же
        // вложенную папку в двух представлениях: с именем-листом (Name=«Бухгалтерия»,
        // Folder=«Учёт») и с полным путём в заголовке секции (Name=«Учёт\Бухгалтерия»).
        // Сопоставление по одному имени такие пары не склеивает, поэтому без дедупликации
        // по полному пути папка при импорте могла бы создаваться несколько раз, а штатный
        // стартер показывал бы дубли. Здесь каждая секция приводится к единому каноническому
        // виду (Name — имя листа, Folder — путь родителя), а совпавшие по полному пути —
        // устраняются (сохраняется первая встреченная вместе с её ID). Тот же канонический
        // список используется для подбора идентификаторов групп из файла
        // (<see cref="ResolveGroupIdFromFile"/>), поэтому на каждую папку приходится ровно
        // один источник пути и ID.
        groupEntries = NormalizeAndDedupeGroupSections(groupEntries);

        // Полные пути групп, под которыми в файле есть БАЗЫ: они нужны, чтобы
        // удалённую пустую группу (issue #327) не возвращать, если под ней в файле
        // снова появились базы — тогда группа пересоздаётся, иначе базам некуда падать.
        var basePaths = entries
            .Where(e => !e.IsGroup && e.Enabled)
            .Select(e => NormalizeGroupPath(e.Group))
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Собираем полные пути групп: из ссылок баз (по Folder) и из канонизированных
        // секций-групп (по Name и Folder).
        var groupPaths = new List<string>();

        // Группы, на которые ссылаются базы через Folder (путь «Родитель\Дочерняя»).
        foreach (var entry in entries)
        {
            if (entry.IsGroup || !entry.Enabled)
                continue;
            var groupPath = NormalizeGroupPath(entry.Group);
            if (!string.IsNullOrWhiteSpace(groupPath))
                groupPaths.Add(groupPath);
        }

        // Группы-секции из канонизированного списка.
        foreach (var groupEntry in groupEntries)
        {
            var groupPath = BuildGroupEntryPath(groupEntry);
            if (!string.IsNullOrWhiteSpace(groupPath))
                groupPaths.Add(groupPath);
        }

        // Создаём группы для каждого уникального пути, выстраивая иерархию.
        foreach (var groupPath in groupPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            CreateGroupWithParents(groupPath, groupEntries, groups, result, deletedGroupPaths, basePaths);
        }

        // Устраняем уже накопившиеся дубликаты групп по полному пути (например, созданные
        // предыдущими версиями приложения). При совпадении пути оставляем первую группу;
        // базы привязаны к группам по полному пути, поэтому ссылки не ломаются.
        RemoveDuplicateGroupsByPath(groups);
    }

    /// <summary>
    /// Удаляет из коллекции настоящие дубликаты групп с одинаковым полным путём
    /// (регистронезависимо), сохраняя одну из них. Используется для очистки
    /// «унаследованных» дубликатов папок, возникавших при повторных синхронизациях
    /// со штатным стартером (issue #165).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Совпадения одного лишь канонического полного пути НЕ достаточно для удаления:
    /// у разных живых групп он может совпадать (например, одноимённая корневая группа
    /// и «осиротевшая» группа того же имени, чей родитель ещё не существует, или группы
    /// с одинаковым именем, выстроенные разными родительскими цепочками). Удаление по
    /// одному пути стирало такие группы — «пропадали все папки» (issue #351). Дубликатом
    /// группа считается только при дополнительном совпадении: одинаковый ID группы ИЛИ
    /// одинаковые имя листа и родитель. Группы с разными Id и одинаковым путём без этих
    /// признаков сохраняются все (в журнал пишется предупреждение для диагностики).
    /// </para>
    /// <para>
    /// При конфликте сохраняется группа с содержимым (дочерними группами): удаление
    /// родителя вместе с детьми — худший исход, поэтому победителем становится группа,
    /// на которую ссылаются дети, а пустой дубликат удаляется.
    /// </para>
    /// <para>
    /// После дедупликации родитель дочерних групп, ссылавшихся на удалённый дубликат,
    /// переназначается на сохранённую группу (прямо по Id; если Id отсутствует — по
    /// запомненному полному пути). Без этого дети «осиротевали» бы: их полный путь
    /// переставал строиться, и следующая синхронизация плодила бы новые дубликаты
    /// вложенных папок (issue #165).
    /// </para>
    /// </remarks>
    private static void RemoveDuplicateGroupsByPath(IList<Group> groups)
    {
        if (groups.Count == 0)
            return;

        // Полный путь каждой группы вычисляем по исходному списку (до удаления),
        // чтобы дубликаты с одним и тем же путём корректно сопоставились, даже если
        // их родитель сам является дубликатом. Пути запоминаются и используются для
        // восстановления родителя осиротевших детей после дедупликации (fallback по пути).
        var originalPaths = new Dictionary<Group, string>(groups.Count);
        var byPath = new Dictionary<string, List<Group>>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            var path = NormalizeGroupPath(GroupHierarchyHelper.GetFullPath(group, groups));
            if (string.IsNullOrWhiteSpace(path))
                continue; // Группы без пути (без имени) не участвуют в дедупликации.
            originalPaths[group] = path;
            if (!byPath.TryGetValue(path, out var bucket))
                byPath[path] = bucket = new List<Group>();
            bucket.Add(group);
        }

        var keep = new List<Group>(groups.Count);
        // Пары «удаляемый дубликат → сохранённая группа» для переназначения детей.
        var removedPairs = new List<(Group Removed, Group Kept)>();

        foreach (var pair in byPath)
        {
            var path = pair.Key;
            var bucket = pair.Value;
            if (bucket.Count == 1)
            {
                keep.Add(bucket[0]);
                continue;
            }

            var winner = bucket[0];
            var candidates = new List<Group>();
            foreach (var candidate in bucket.Skip(1))
            {
                if (IsSameGroupDuplicate(candidate, winner))
                {
                    candidates.Add(candidate);
                }
                else
                {
                    // Разные живые группы с одним путём (сирота и корневая одноимённая,
                    // группы с разными родителями): НЕ удаляем — это была бы потеря папок
                    // (issue #351). Пишем предупреждение для диагностики.
                    LogInfo(
                        $"Дедупликация групп: путь \"{path}\": группы с Id \"{winner.Id}\" " +
                        $"и \"{candidate.Id}\" разные, оставлены обе");
                    keep.Add(candidate);
                }
            }

            if (candidates.Count == 0)
            {
                keep.Add(winner);
                continue;
            }

            // Сохраняем группу с содержимым: если у кандидата есть дочерние группы,
            // а у текущего победителя нет — победителем становится кандидат, иначе
            // папка потеряла бы своих детей вместе с удалённым дубликатом (issue #351).
            foreach (var candidate in candidates.ToList())
            {
                if (HasChildGroups(candidate, groups) && !HasChildGroups(winner, groups))
                {
                    candidates.Remove(candidate);
                    candidates.Add(winner);
                    winner = candidate;
                }
            }

            keep.Add(winner);
            foreach (var removed in candidates)
            {
                removedPairs.Add((removed, winner));
                LogInfo(
                    $"Дедупликация групп: путь \"{path}\", сохранена Id=\"{winner.Id}\", " +
                    $"удалена Id=\"{removed.Id}\"");
            }
        }

        if (keep.Count == groups.Count && removedPairs.Count == 0)
            return;

        // Перенаправляем ParentId дочерних групп, ссылавшихся на удалённые дубликаты,
        // на сохранённые группы. Это сохраняет иерархию и предотвращает повторное
        // создание дубликатов вложенных папок при следующих синхронизациях (issue #165).
        var redirected = 0;
        foreach (var (removed, kept) in removedPairs)
        {
            if (string.IsNullOrWhiteSpace(removed.Id) || string.IsNullOrWhiteSpace(kept.Id))
                continue;
            foreach (var group in keep)
            {
                if (string.Equals(group.ParentId ?? string.Empty, removed.Id, StringComparison.OrdinalIgnoreCase))
                {
                    group.ParentId = kept.Id;
                    redirected++;
                }
            }
        }

        // Инвариант (issue #351): группы, чей родитель пропал после дедупликации
        // (например, у удалённого дубликата или сохранённой группы не было Id, поэтому
        // прямое переназначение выше не сработало), перевешиваем на группу с полным
        // путём родителя, запомненным ДО удаления. Так иерархия не разрушается, а папки
        // не становятся корневыми («не пропадают») из-за потери родителя.
        foreach (var group in keep)
        {
            if (string.IsNullOrWhiteSpace(group.ParentId))
                continue;
            if (GroupExistsById(keep, group.ParentId))
                continue;

            if (!originalPaths.TryGetValue(group, out var ownPath))
                continue;
            var (_, parentPath) = SplitLeafAndParent(ownPath);
            if (string.IsNullOrWhiteSpace(parentPath))
                continue;

            var parent = FindGroupByFullPath(keep, parentPath, string.Empty, null);
            if (parent is not null && !string.Equals(parent.Id, group.Id, StringComparison.OrdinalIgnoreCase))
            {
                group.ParentId = parent.Id;
                redirected++;
            }
        }

        groups.Clear();
        foreach (var group in keep)
            groups.Add(group);

        LogInfo($"Дедупликация групп: удалено {removedPairs.Count} дубликатов, переназначено детей {redirected}");
    }

    /// <summary>
    /// Является ли группа <paramref name="candidate"/> настоящим дубликатом группы
    /// <paramref name="kept"/>: совпадает ID группы ИЛИ совпадают имя листа и родитель.
    /// Одного совпадения канонического полного пути недостаточно — «осиротевшая» группа
    /// с тем же именем, что и корневая, не является её дубликатом и удалению не подлежит
    /// (issue #351).
    /// </summary>
    private static bool IsSameGroupDuplicate(Group candidate, Group kept)
    {
        if (!string.IsNullOrWhiteSpace(candidate.Id)
            && !string.IsNullOrWhiteSpace(kept.Id)
            && string.Equals(candidate.Id, kept.Id, StringComparison.OrdinalIgnoreCase))
            return true;

        var sameName = string.Equals(
            NormalizeGroupName(candidate.Name),
            NormalizeGroupName(kept.Name),
            StringComparison.OrdinalIgnoreCase);
        var sameParent = string.Equals(
            candidate.ParentId ?? string.Empty,
            kept.ParentId ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
        return sameName && sameParent;
    }

    /// <summary>Есть ли у группы дочерние группы в коллекции (по <see cref="Group.ParentId"/>).</summary>
    private static bool HasChildGroups(Group group, IList<Group> groups)
        => groups.Any(g => !string.IsNullOrWhiteSpace(g.ParentId)
                           && string.Equals(g.ParentId, group.Id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Создаёт группу по полному пути (например, «Учёт\Бухгалтерия»),
    /// автоматически создавая недостающие родительские группы и выставляя ParentId.
    /// </summary>
    /// <param name="deletedGroupPaths">Канонические полные пути удалённых пользователем
    /// пустых групп (регистронезависимо) или null. Пути из этого списка не создаются,
    /// пока под ними нет баз из файла (<paramref name="basePaths"/>) — issue #327.</param>
    /// <param name="basePaths">Полные пути групп, под которыми в файле есть базы.</param>
    private static void CreateGroupWithParents(
        string groupPath,
        List<IbaseEntry> groupEntries,
        IList<Group> groups,
        IbasesImportResult result,
        ISet<string>? deletedGroupPaths = null,
        ISet<string>? basePaths = null)
    {
        var segments = SplitGroupPath(groupPath);
        if (segments.Count == 0)
            return;

        string? parentId = null;
        var pathSegments = new List<string>(segments.Count);
        // Ветка «мертва»: родительская группа — удалённая пользователем пустая группа
        // без баз в файле, её не восстановили. Потомков под ней тоже не создаём, иначе
        // они остались бы без родителя («осиротели»).
        var branchSuppressed = false;

        for (var i = 0; i < segments.Count; i++)
        {
            if (branchSuppressed)
                continue;

            var segment = segments[i];
            pathSegments.Add(segment);
            var pathSoFar = string.Join(GroupHierarchyHelper.PathSeparator, pathSegments);

            // Идемпотентность: сначала ищем существующую группу по точному полному пути
            // (регистронезависимо). Это не даёт создавать дубликаты папок при повторных
            // синхронизациях, когда одна и та же вложенная папка встречается в файле
            // в нескольких представлениях (секция-группа и Folder-ссылки баз).
            var existing = FindGroupByFullPath(groups, pathSoFar, segment, parentId);

            if (existing is null)
            {
                // Путь удалённой пользователем пустой группы (issue #327): не возвращаем её,
                // если под путём в файле нет баз. Если базы появились — группа нужна, и она
                // создаётся как обычно.
                if (deletedGroupPaths is not null
                    && deletedGroupPaths.Contains(NormalizeGroupPath(pathSoFar))
                    && !HasBasesUnderPath(basePaths, NormalizeGroupPath(pathSoFar)))
                {
                    branchSuppressed = true;
                    continue;
                }

                var id = ResolveGroupIdFromFile(groupEntries, pathSoFar, segment) ?? Guid.NewGuid().ToString();
                // Идентификатор из файла может совпасть с Id уже существующей группы коллекции,
                // живущей под другим полным путём (поиск по пути её не нашёл). Дубль Id ломает
                // построение дерева (узлы группируются по Id через словарь) и привязку потомков,
                // поэтому при коллизии назначаем свежий Guid вместо повторного использования Id
                // (issue #280).
                if (GroupExistsById(groups, id))
                    id = Guid.NewGuid().ToString();
                existing = new Group
                {
                    Name = segment,
                    Id = id,
                    ParentId = parentId ?? string.Empty
                };
                groups.Add(existing);
                result.GroupsCreated++;
            }
            else if (!string.IsNullOrEmpty(parentId)
                     && (string.IsNullOrWhiteSpace(existing.ParentId) || !GroupExistsById(groups, existing.ParentId)))
            {
                // Восстанавливаем/перевешиваем родителя: группа либо была создана без
                // иерархии, либо её родитель отсутствует («осиротела» — например, после
                // удаления дубликата-родителя прежними версиями приложения, когда ParentId
                // указывал на удалённую группу). Без перевешивания такая группа не находилась
                // бы по полному пути на следующей синхронизации, и импорт плодил бы новый
                // дубликат вложенной папки (issue #165). Здесь корректно восстанавливаем
                // связь с текущим родителем из пути.
                existing.ParentId = parentId;
            }

            parentId = existing.Id;
        }
    }

    /// <summary>
    /// Находит существующую группу, соответствующую сегменту пути. Приоритет — точное
    /// совпадение по полному пути группы (через <see cref="GroupHierarchyHelper.GetFullPath"/>),
    /// что обеспечивает идемпотентность импорта и предотвращает дублирование папок
    /// (в т.ч. у вложенных папок). Если группа по полному пути не найдена, используется
    /// прежнее сопоставление по имени листа и родителю.
    /// </summary>
    private static Group? FindGroupByFullPath(
        IList<Group> groups,
        string fullPath,
        string leafName,
        string? parentId)
    {
        if (groups.Count == 0)
            return null;

        foreach (var group in groups)
        {
            // Сравниваем канонизированные пути (нормализованные разделители и пробелы),
            // чтобы существующая группа всегда находилась по полному пути независимо от
            // того, как именно построен путь (через GetFullPath или через создание сегментами).
            // Это гарантирует идемпотентность импорта вложенных папок и не даёт повторным
            // синхронизациям плодить дубликаты.
            if (string.Equals(
                    NormalizeGroupPath(GroupHierarchyHelper.GetFullPath(group, groups)),
                    fullPath,
                    StringComparison.OrdinalIgnoreCase))
                return group;
        }

        // Запасной вариант — по имени листа: существующая группа считается подходящей,
        // если её родитель совпадает с текущим, ЛИБО группа «осиротела» (родитель задан,
        // но отсутствует в списке). Осиротевшую группу <see cref="CreateGroupWithParents"/>
        // перевешивает на актуального родителя, поэтому она не теряется и не порождает
        // новый дубликат при следующей синхронизации (issue #165).
        return groups.FirstOrDefault(g =>
            string.Equals(g.Name, leafName, StringComparison.OrdinalIgnoreCase)
            && (IsParent(g, parentId) || IsOrphanedParent(g, groups)));
    }

    /// <summary>
    /// Есть ли в файле базы с полным путём <paramref name="path"/> или в любой
    /// подгруппе под ним (по префиксу пути). Используется для решения, нужна ли
    /// удалённая пользователем пустая группа снова (issue #327).
    /// </summary>
    private static bool HasBasesUnderPath(ISet<string>? basePaths, string path)
    {
        if (basePaths is null || basePaths.Count == 0)
            return false;
        if (basePaths.Contains(path))
            return true;

        var prefix = path + GroupHierarchyHelper.PathSeparator;
        foreach (var p in basePaths)
        {
            if (p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Проверяет, что группа <paramref name="group"/> «осиротела»: у неё задан
    /// <see cref="Group.ParentId"/>, но соответствующей группы нет в коллекции.
    /// Такие группы возникают после удаления родителя-дубликата прежними версиями
    /// приложения и должны перевешиваться на актуального родителя по имени.
    /// </summary>
    private static bool IsOrphanedParent(Group group, IList<Group> groups)
    {
        return !string.IsNullOrWhiteSpace(group.ParentId)
               && !GroupExistsById(groups, group.ParentId);
    }

    /// <summary>
    /// Проверяет наличие группы с указанным идентификатором в коллекции.
    /// </summary>
    private static bool GroupExistsById(IList<Group> groups, string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return false;
        return groups.Any(g => string.Equals(g.Id, id, StringComparison.OrdinalIgnoreCase));
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
    /// Приводит секции-группы файла ibases.v8i к единому каноническому виду и устраняет
    /// дубликаты по ПОЛНОМУ пути папки. Файл, переписанный штатным стартером 1С, может
    /// содержать одну и ту же вложенную папку в двух представлениях: с именем-листом
    /// (Name=«Бухгалтерия», Folder=«Учёт») и с полным путём в заголовке секции
    /// (Name=«Учёт\Бухгалтерия»). Сопоставление по одному имени (см. <see cref="Parse"/>)
    /// такие пары не склеивает, из-за чего папка могла бы создаваться при импорте дважды,
    /// а штатный стартер показывал бы дубли (issue #165). Здесь каждая секция приводится
    /// к каноническому виду (Name — имя листа, Folder — путь родителя), а совпавшие по
    /// полному пути устраняются; сохраняется первая встреченная секция и её ID.
    /// </summary>
    private static List<IbaseEntry> NormalizeAndDedupeGroupSections(List<IbaseEntry> groupEntries)
    {
        var byPath = new Dictionary<string, IbaseEntry>(StringComparer.OrdinalIgnoreCase);
        var result = new List<IbaseEntry>(groupEntries.Count);

        foreach (var entry in groupEntries)
        {
            var canonicalPath = BuildGroupEntryPath(entry);
            if (string.IsNullOrWhiteSpace(canonicalPath))
            {
                // Секция-группа без пути — сохраняем как есть.
                result.Add(entry);
                continue;
            }

            if (byPath.ContainsKey(canonicalPath))
                continue; // Дубликат папки по полному пути — пропускаем.

            var (leaf, parentPath) = SplitLeafAndParent(canonicalPath);
            // Приводим к каноническому виду: имя — лист, Folder — путь родителя.
            entry.Name = leaf;
            entry.Group = string.IsNullOrWhiteSpace(parentPath) ? string.Empty : parentPath;
            byPath[canonicalPath] = entry;
            result.Add(entry);
        }

        return result;
    }

    /// <summary>
    /// Разделяет канонический путь группы на имя листа и путь родителя.
    /// </summary>
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
    /// Создаёт в коллекции недостающие группы по файлу ibases.v8i: тем же разбором,
    /// что и обычный импорт списка баз, включая перенос идентификаторов групп из файла
    /// и уборку дубликатов (issue #165). Используется импортом из StartManager, где
    /// группы приходят из этого же файла.
    /// </summary>
    /// <param name="filePath">Путь к файлу ibases.v8i.</param>
    /// <param name="groups">Коллекция групп приложения.</param>
    /// <returns>Количество созданных групп.</returns>
    public static int EnsureGroupsFromFile(string filePath, IList<Group> groups)
    {
        if (!File.Exists(filePath))
            return 0;

        var result = new IbasesImportResult();
        EnsureGroups(IbaseEntry.Parse(filePath), groups, result);
        return result.GroupsCreated;
    }

    /// <summary>
    /// Читает записи баз из файла ibases.v8i как модели <see cref="Infobase"/>,
    /// не изменяя коллекции приложения. Группы (секции без строки подключения)
    /// и отключённые записи пропускаются. Используется импортом из StartManager,
    /// где строка подключения берётся отсюда, а надстройки — из v8config.smc.
    /// </summary>
    /// <param name="filePath">Путь к файлу ibases.v8i.</param>
    /// <returns>Список баз файла; пустой список, если файла нет.</returns>
    public static List<Infobase> ReadInfobases(string filePath)
    {
        if (!File.Exists(filePath))
            return new List<Infobase>();

        return IbaseEntry.Parse(filePath)
            .Where(e => !e.IsGroup && e.Enabled)
            .Select(e => e.ToInfobase())
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

        var entries = IbaseEntry.Parse(filePath);

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
    /// Нормализует путь группы: обрезает пробелы вокруг разделителей "/" и "\",
    /// но сохраняет иерархию пути.
    /// </summary>
    /// <remarks>
    /// Разбор файла ibases.v8i выполняется общей реализацией <see cref="IbaseEntry.Parse"/>,
    /// используемой и экспортёром, и импортёром, — ни один из путей не теряет ключи
    /// и исходный порядок строк секций (issue #277).
    /// </remarks>
    private static string NormalizeGroupPath(string group)
    {
        var segments = SplitGroupPath(group);
        return string.Join(GroupHierarchyHelper.PathSeparator, segments);
    }

    /// <summary>Нормализует одиночное имя группы (без учёта пути): убирает разделители.</summary>
    private static string NormalizeGroupName(string name)
    {
        var segments = SplitGroupPath(name);
        return segments.Count > 0 ? segments[^1] : string.Empty;
    }

    /// <summary>Пишет информационное сообщение импорта в файловый лог (issue #165).</summary>
    private static void LogInfo(string message)
    {
        try
        {
            AppServices.GetRequiredService<IAppLogger>().Info(message);
        }
        catch
        {
            // Логирование не должно ломать импорт.
        }
    }

}
