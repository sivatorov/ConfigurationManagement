namespace Configuration_Management.Models;

/// <summary>
/// Представляет группу информационных баз.
/// </summary>
public class Group
{
    /// <summary>
    /// Идентификатор группы 1С (GUID из файла ibases.v8i, ключ ID).
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Наименование группы.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Идентификатор родительской группы (Id). Пустая строка — корневая группа.
    /// Позволяет строить иерархию групп, как в типовом списке баз 1С.
    /// </summary>
    public string ParentId { get; set; } = string.Empty;

    /// <summary>Описание группы.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Цвет группы (в формате #RRGGBB).</summary>
    public string Color { get; set; } = "#2D6CDF";

    /// <summary>
    /// Возвращает строковое представление группы.
    /// </summary>
    public override string ToString() => Name;
}