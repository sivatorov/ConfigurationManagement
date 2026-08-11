using System.Collections.ObjectModel;
using Configuration_Management.Models;

namespace Configuration_Management.ViewModels;

/// <summary>
/// Представляет узел дерева групп информационных баз.
/// Содержит модель группы, коллекцию подгрупп и коллекцию баз, размещённых в этой группе.
/// Также содержит единую коллекцию <see cref="Items"/>, объединяющую подгруппы и базы,
/// для отображения дерева «группа в группе».
/// </summary>
public class GroupNodeViewModel : ViewModelBase
{
    private bool _isExpanded = true;
    private bool _isSelected;

    /// <summary>
    /// Создаёт узел дерева для указанной группы.
    /// </summary>
    /// <param name="group">Модель группы. Может быть null для специального узла («Закреплённые», «Без группы»).</param>
    /// <param name="parent">Родительский узел. Null для корневого узла.</param>
    /// <param name="displayName">Имя для отображения (для специальных узлов).</param>
    public GroupNodeViewModel(Group? group, GroupNodeViewModel? parent = null, string? displayName = null)
    {
        Group = group;
        Parent = parent;
        DisplayName = displayName ?? group?.Name ?? "Без группы";
        Children = new ObservableCollection<GroupNodeViewModel>();
        Infobases = new ObservableCollection<Infobase>();
        Items = new ObservableCollection<object>();
    }

    /// <summary>Модель группы. Null для специальных узлов («Закреплённые», «Без группы»).</summary>
    public Group? Group { get; }

    /// <summary>Родительский узел. Null для корневого узла.</summary>
    public GroupNodeViewModel? Parent { get; }

    /// <summary>Имя группы для отображения (без пути).</summary>
    public string DisplayName { get; }

    /// <summary>Полный путь группы в иерархии.</summary>
    public string FullPath
    {
        get
        {
            if (Group is null)
                return string.Empty;

            var parts = new List<string>();
            for (var node = this; node is not null && node.Group is not null; node = node.Parent)
            {
                parts.Add(node.Group.Name);
            }
            parts.Reverse();
            return string.Join(GroupHierarchyHelper.PathSeparator, parts);
        }
    }

    /// <summary>Цвет группы.</summary>
    public string Color => Group?.Color ?? "#2D6CDF";

    /// <summary>Подгруппы текущего узла.</summary>
    public ObservableCollection<GroupNodeViewModel> Children { get; }

    /// <summary>Базы, размещённые непосредственно в этой группе.</summary>
    public ObservableCollection<Infobase> Infobases { get; }

    /// <summary>
    /// Единая коллекция для отображения в дереве: содержит подгруппы (с базами) и базы текущей группы.
    /// Заполняется методом <see cref="PopulateItems"/>.
    /// </summary>
    public ObservableCollection<object> Items { get; }

    /// <summary>Признак наличия подгрупп.</summary>
    public bool HasChildren => Children.Count > 0;

    /// <summary>Признак наличия баз в группе.</summary>
    public bool HasInfobases => Infobases.Count > 0;

    /// <summary>Признак наличия баз в группе или её подгруппах.</summary>
    public bool ContainsInfobases => Infobases.Count > 0 || Children.Any(c => c.ContainsInfobases);

    /// <summary>Общее количество баз в группе и всех её подгруппах.</summary>
    public int TotalInfobaseCount => Infobases.Count + Children.Sum(c => c.TotalInfobaseCount);

    /// <summary>Состояние развёрнутости узла в дереве.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    /// <summary>Состояние выделенности узла в дереве.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>
    /// Заполняет коллекцию <see cref="Items"/>: сначала подгруппы, содержащие базы (рекурсивно),
    /// затем базы текущей группы. Пустые группы (без баз) в дерево не попадают.
    /// </summary>
    public void PopulateItems()
    {
        foreach (var child in Children)
        {
            child.PopulateItems();
        }

        Items.Clear();
        foreach (var child in Children)
        {
            if (child.ContainsInfobases)
            {
                Items.Add(child);
            }
        }
        foreach (var infobase in Infobases)
        {
            Items.Add(infobase);
        }
    }

    /// <summary>
    /// Возвращает строковое представление узла (полный путь группы).
    /// </summary>
    public override string ToString() => string.IsNullOrEmpty(FullPath) ? DisplayName : FullPath;

    /// <summary>
    /// Строит дерево групп из плоского списка с учётом свойства <see cref="Group.ParentId"/>.
    /// Возвращает список корневых узлов. Группы с несуществующим родителем становятся корневыми.
    /// Группы, участвующие в циклической ссылке родителя (A→B→A), делаются корневыми,
    /// чтобы в дереве не образовывалась бесконечная вложенность (иначе рекурсивный обход
    /// дерева приводит к StackOverflowException).
    /// </summary>
    public static List<GroupNodeViewModel> BuildTree(IEnumerable<Group> groups)
    {
        var list = groups.ToList();
        var nodes = new Dictionary<string, GroupNodeViewModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in list)
        {
            nodes[group.Id] = new GroupNodeViewModel(group);
        }

        // Определяем группы, участвующие в цикле родительских ссылок.
        var inCycle = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in list)
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var current = group;
            while (current is not null && !string.IsNullOrEmpty(current.ParentId))
            {
                // Встретили уже посещённый узел — цепочка замкнулась в цикл.
                if (!visited.Add(current.Id))
                {
                    inCycle.Add(current.Id);
                    foreach (var id in visited)
                        inCycle.Add(id);
                    break;
                }

                if (!nodes.TryGetValue(current.ParentId, out var parentNode))
                    break; // Родитель не найден — достигли корня.

                current = parentNode.Group;
            }
        }

        var roots = new List<GroupNodeViewModel>();
        foreach (var group in list)
        {
            // Группа с циклом, без родителя или с несуществующим родителем — корневая.
            if (inCycle.Contains(group.Id) ||
                string.IsNullOrEmpty(group.ParentId) ||
                !nodes.ContainsKey(group.ParentId))
            {
                roots.Add(nodes[group.Id]);
                continue;
            }

            nodes[group.ParentId].Children.Add(nodes[group.Id]);
        }

        return roots;
    }
}