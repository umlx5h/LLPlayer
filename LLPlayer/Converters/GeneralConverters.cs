using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace LLPlayer.Converters;

[ValueConversion(typeof(bool), typeof(Visibility))]
public class BooleanToVisibilityMiscConverter : IValueConverter
{
    public Visibility FalseVisibility { get; set; } = Visibility.Collapsed;
    public bool Invert { get; set; } = false;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (Invert)
        {
            return (bool)value ? FalseVisibility : Visibility.Visible;
        }

        return (bool)value ? Visibility.Visible : FalseVisibility;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

[ValueConversion(typeof(Color), typeof(Brush))]
public class ColorToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is Color color)
        {
            return new SolidColorBrush(color);
        }
        return Binding.DoNothing;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is SolidColorBrush brush)
        {
            return brush.Color;
        }
        return default(Color);
    }
}
