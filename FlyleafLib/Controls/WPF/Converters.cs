using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace FlyleafLib.Controls.WPF;

public class TicksToTimeSpanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)     => new TimeSpan((long)value);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => ((TimeSpan)value).Ticks;
}

public class TicksToTimeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)     => new TimeSpan((long)value).ToString(@"hh\:mm\:ss");
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class TicksToSecondsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)     => (long)value / 10000000.0;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => (long)((double)value * 10000000);
}

public class TicksToMilliSecondsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)     => (int)((long)value / 10000);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => long.Parse(value.ToString()) * 10000;
}

public class StringToRationalConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)     => value.ToString();

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => new AspectRatio(value.ToString());
}

public class BooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)     => (bool)value ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}
