using System.Globalization;
using System.Windows.Data;

namespace FlyleafLib.Controls.WPF;

[ValueConversion(typeof(long), typeof(TimeSpan))]
public class TicksToTimeSpanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)     => new TimeSpan((long)value);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => ((TimeSpan)value).Ticks;
}

[ValueConversion(typeof(long), typeof(string))]
public class TicksToTimeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)     => new TimeSpan((long)value).ToString(@"hh\:mm\:ss");
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

[ValueConversion(typeof(long), typeof(double))]
public class TicksToSecondsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)     => (long)value / 10000000.0;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => (long)((double)value * 10000000);
}

[ValueConversion(typeof(long), typeof(int))]
public class TicksToMilliSecondsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)     => (int)((long)value / 10000);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => long.Parse(value.ToString()) * 10000;
}

[ValueConversion(typeof(AspectRatio), typeof(string))]
public class StringToRationalConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)     => value.ToString();

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => new AspectRatio(value.ToString());
}
