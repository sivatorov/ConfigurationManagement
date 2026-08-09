using System.Text;

namespace Configuration_Management.Models;

/// <summary>
/// Вспомогательные методы для работы с иерархией групп информационных баз.
/// Позволяет строить полные пути групп, дерево подгрупп и проверять корректность иерархии.
/// </summary>
public static class GroupHierarchyHelper
{
    /// <summary>
    /// Разделитель пути между родительской и дочерней группой.
    /// Используется для хранения в свойстве <see cref="Infobase.Group"/> полного пути группы.
    /// </summary>
    public const string PathSeparator = " / ";

    /// <summary>
    /// Возвращает полный путь группы в иерархии (например, «Учёт / Бухгалтерия»).
    /// Для корневой группы возвращается просто её наименование.
    /// </summary>
    /// <param name="group">Группа, для которой строится путь.</param>
    /// <param name="allGroups">Полный список групп для поиска родителей.</param>
    public static string GetFullPath(Group? group, IEnumerable<Group>? allGroups)
    {
        if (group is null || string.IsNullOrWhiteSpace(group.Name))
            return string.Empty;

        var list = allGroups?.ToList() ?? new List<Group>();
        var parts = new List<string> { group.Name };

        var current = group;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        visited.Add(current.Id);

        // Поднимаемся по цепочке родителей, защищаясь от циклических ссылок.
        while (!string.IsNullOrEmpty(current.ParentId))
        {
            if (!visited.Add(current.ParentId))
                break; // Обнаружен цикл — прекращаем построение пути.

            var parent = list.FirstOrDefault(g =>
                string.Equals(g.Id, current.ParentId, StringComparison.OrdinalIgnoreCase));
            if (parent is null || string.IsNullOrWhiteSpace(parent.Name))
                break;

            parts.Add(parent.Name);
            current = parent;
        }

        parts.Reverse();
        return string.Join(PathSeparator, parts);
    }

    /// <summary>
    /// Возвращает наименование группы (без пути родителя).
    /// </summary>
    public static string GetDisplayName(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
            return string.Empty;

        var separatorIndex = fullPath.LastIndexOf(PathSeparator, StringComparison.OrdinalIgnoreCase);
        return separatorIndex >= 0
            ? fullPath.Substring(separatorIndex + PathSeparator.Length)
            : fullPath;
    }

    /// <summary>
    /// Находит группу по полному пути (например, «Учёт / Бухгалтерия»).
    /// </summary>
    public static Group? FindByFullPath(string fullPath, IEnumerable<Group>? allGroups)
    {
        if (allGroups is null || string.IsNullOrWhiteSpace(fullPath))
            return null;

        foreach (var group in allGroups)
        {
            if (string.Equals(GetFullPath(group, allGroups), fullPath, StringComparison.OrdinalIgnoreCase))
                return group;
        }

        return null;
    }

    /// <summary>
    /// Проверяет, является ли группа <paramref name="candidateId"/> предком группы
    /// <paramref name="groupId"/> в иерархии (включая саму группу). Используется для
    /// предотвращения создания циклических ссылок при назначении родителя.
    /// </summary>
    public static bool IsAncestorOrSelf(string? groupId, string? candidateId, IEnumerable<Group>? allGroups)
    {
        if (string.IsNullOrEmpty(groupId) || string.IsNullOrEmpty(candidateId))
            return false;
        if (string.Equals(groupId, candidateId, StringComparison.OrdinalIgnoreCase))
            return true;

        var list = allGroups?.ToList() ?? new List<Group>();
        var current = list.FirstOrDefault(g =>
            string.Equals(g.Id, groupId, StringComparison.OrdinalIgnoreCase));

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (current is not null && !string.IsNullOrEmpty(current.ParentId))
        {
            if (!visited.Add(current.ParentId))
                return false;

            if (string.Equals(current.ParentId, candidateId, StringComparison.OrdinalIgnoreCase))
                return true;

            current = list.FirstOrDefault(g =>
                string.Equals(g.Id, current.ParentId, StringComparison.OrdinalIgnoreCase));
        }

        return false;
    }

    /// <summary>
    /// Проверяет, что у группы не установлена циклическая ссылка на собственных потомков.
    /// </summary>
    public static bool IsValidParent(Group group, Group? parent, IEnumerable<Group>? allGroups)
    {
        // Нельзя назначить родителем саму группу или её собственного потомка.
        if (parent is null)
            return true;

        if (string.Equals(group.Id, parent.Id, StringComparison.OrdinalIgnoreCase))
            return false;

        return !IsAncestorOrSelf(parent.Id, group.Id, allGroups);
    }
}