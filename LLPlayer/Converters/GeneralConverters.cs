using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using LLPlayer.Services;

namespace LLPlayer.Converters;
[ValueConversion(typeof(bool), typeof(bool))]
public class InvertBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
        {
            return !boolValue;
        }
        return false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
        {
            return !boolValue;
        }
        return false;
    }
}

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

[ValueConversion(typeof(Enum), typeof(string))]
public class EnumToStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string str;
        try
        {
            str = Enum.GetName(value.GetType(), value)!;
            return str;
        }
        catch
        {
            return string.Empty;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

[ValueConversion(typeof(Enum), typeof(string))]
public class EnumToDescriptionConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is Enum enumValue)
        {
            return enumValue.GetDescription();
        }
        return value.ToString() ?? "";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

[ValueConversion(typeof(Enum), typeof(bool))]
public class EnumToBooleanConverter : IValueConverter
{
    // value, parameter = Enum
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not Enum enumValue || parameter is not Enum enumTarget)
            return false;

        return enumValue.Equals(enumTarget);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return parameter;
    }
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

/// <summary>
/// Converts from System.Windows.Input.Key to human readable string
/// </summary>
[ValueConversion(typeof(Key), typeof(string))]
public class KeyToStringConverter : IValueConverter
{
    public static readonly Dictionary<Key, string> KeyMappings = new()
    {
        { Key.D0, "0" },
        { Key.D1, "1" },
        { Key.D2, "2" },
        { Key.D3, "3" },
        { Key.D4, "4" },
        { Key.D5, "5" },
        { Key.D6, "6" },
        { Key.D7, "7" },
        { Key.D8, "8" },
        { Key.D9, "9" },
        { Key.Prior, "PageUp" },
        { Key.Next, "PageDown" },
        { Key.Return, "Enter" },
        { Key.Oem1, ";" },
        { Key.Oem2, "/" },
        { Key.Oem3, "`" },
        { Key.Oem4, "[" },
        { Key.Oem5, "\\" },
        { Key.Oem6, "]" },
        { Key.Oem7, "'" },
        { Key.OemPlus, "Plus" },
        { Key.OemMinus, "Minus" },
        { Key.OemComma, "," },
        { Key.OemPeriod, "." }
    };

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is Key key)
        {
            if (KeyMappings.TryGetValue(key, out var mappedValue))
            {
                return mappedValue;
            }

            return key.ToString();
        }

        return Binding.DoNothing;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

[ValueConversion(typeof(long), typeof(string))]
public class FileSizeHumanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is long size)
        {
            return FormatBytes(size);
        }

        return Binding.DoNothing;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }

    private static string FormatBytes(long bytes)
    {
        string[] sizes = ["B", "KB", "MB", "GB"];
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }
}

[ValueConversion(typeof(int?), typeof(string))]
public class NullableIntConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        int? nullableInt = value as int?;
        return nullableInt.HasValue ? nullableInt.Value.ToString() : string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string str = value as string;
        if (string.IsNullOrWhiteSpace(str))
            return null;

        if (int.TryParse(str, out int result))
            return result;

        return null;
    }
}
