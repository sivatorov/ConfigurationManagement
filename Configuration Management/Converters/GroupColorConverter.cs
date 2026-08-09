using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Configuration_Management.Models;

namespace Configuration_Management.Converters;

/// <summary>
/// Возвращает кисть цвета группы по её имени и списку групп.
/// Используется для окрашивания фона заголовка группы в списке.
/// </summary>
public class GroupColorConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var groupPath = values[0]?.ToString() ?? string.Empty;

        if (values.Length > 1 && values[1] is ObservableCollection<Group> groups)
        {
            // Ищем группу по полному пути в иерархии (например, «Учёт / Бухгалтерия»).
            var group = groups.FirstOrDefault(g =>
                string.Equals(GroupHierarchyHelper.GetFullPath(g, groups), groupPath, StringComparison.OrdinalIgnoreCase));
            if (group is not null)
            {
                return CreateBrush(group.Color);
            }
        }

        return CreateBrush("#2D6CDF");
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static SolidColorBrush CreateBrush(string hex)
    {
        try
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        }
        catch
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2D6CDF"));
        }
    }
}