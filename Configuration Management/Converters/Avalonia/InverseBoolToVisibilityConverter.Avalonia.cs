#if LINUX
using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace Configuration_Management.Converters
{
    /// <summary>
    /// Avalonia-версия: true → скрыт, false → показан (обратный BooleanToVisibility).
    /// В Avalonia видимостью управляет булево IsVisible, перечисления Visibility нет.
    /// </summary>
    public class InverseBoolToVisibilityConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var flag = value is bool b && b;
            return !flag;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is bool v && !v;
    }
}
#endif