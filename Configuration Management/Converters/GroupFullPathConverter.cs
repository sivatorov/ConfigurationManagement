using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Data;
using Configuration_Management.Models;

namespace Configuration_Management.Converters;

/// <summary>
/// Возвращает полный путь группы в иерархии (например, «Учёт / Бухгалтерия»).
/// Используется для отображения групп с учётом их родительских групп.
/// </summary>
public class GroupFullPathConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var group = values.Length > 0 ? values[0] as Group : null;
        var groups = values.Length > 1 ? values[1] as ObservableCollection<Group> : null;

        if (group is null)
            return string.Empty;

        return GroupHierarchyHelper.GetFullPath(group, groups);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}