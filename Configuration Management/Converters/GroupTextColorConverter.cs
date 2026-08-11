using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Configuration_Management.Models;

namespace Configuration_Management.Converters;

/// <summary>
/// Возвращает контрастный цвет текста (чёрный или белый) для заголовка группы
/// на основе цвета фона группы. Используется для обеспечения читаемости названия
/// группы на светлом или тёмном фоне.
/// </summary>
public class GroupTextColorConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var groupPath = values[0]?.ToString() ?? string.Empty;

        Color color;
        if (values.Length > 1 && values[1] is ObservableCollection<Group> groups)
        {
            // Ищем группу по полному пути в иерархии (например, «Учёт / Бухгалтерия»).
            var group = groups.FirstOrDefault(g =>
                string.Equals(GroupHierarchyHelper.GetFullPath(g, groups), groupPath, StringComparison.OrdinalIgnoreCase));
            color = group is not null
                ? ParseColor(group.Color)
                : ParseColor("#2D6CDF");
        }
        else
        {
            color = ParseColor("#2D6CDF");
        }

        return new SolidColorBrush(IsLight(color) ? Colors.Black : Colors.White);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static Color ParseColor(string hex)
    {
        try
        {
            return (Color)ColorConverter.ConvertFromString(hex);
        }
        catch
        {
            return (Color)ColorConverter.ConvertFromString("#2D6CDF");
        }
    }

    /// <summary>
    /// Определяет, является ли цвет светлым (по воспринимаемой яркости).
    /// </summary>
    private static bool IsLight(Color color)
    {
        // Воспринимаемая яркость по формуле (Rec. 709).
        var luminance = 0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B;
        return luminance > 150;
    }
}