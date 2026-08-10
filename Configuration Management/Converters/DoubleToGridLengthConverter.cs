using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Configuration_Management.Converters;

/// <summary>
/// Конвертер ширины колонки из числа (double) в тип GridLength.
/// Используется для привязки ширины колонок строк списка баз
/// к значениям, хранящимся в настройках приложения.
/// </summary>
public class DoubleToGridLengthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is double width && width > 0)
        {
            return new GridLength(width);
        }

        // Значение по умолчанию можно передать параметром (например "120").
        if (double.TryParse(parameter as string, NumberStyles.Any, culture, out var fallback) && fallback > 0)
        {
            return new GridLength(fallback);
        }

        return new GridLength(1, GridUnitType.Auto);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is GridLength length ? length.Value : 0d;
    }
}