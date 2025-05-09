using System.Globalization;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using FlyleafLib;
using static FlyleafLib.MediaFramework.MediaDemuxer.Demuxer;

namespace LLPlayer.Converters;

[ValueConversion(typeof(int), typeof(Qualities))]
public class QualityToLevelsConverter : IValueConverter
{
    public enum Qualities
    {
        None,
        Low,
        Med,
        High,
        _4k
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        int videoHeight = (int)value;

        if (videoHeight > 1080)
            return Qualities._4k;
        if (videoHeight > 720)
            return Qualities.High;
        if (videoHeight == 720)
            return Qualities.Med;

        return Qualities.Low;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) { throw new NotImplementedException(); }
}

[ValueConversion(typeof(int), typeof(Volumes))]
public class VolumeToLevelsConverter : IValueConverter
{
    public enum Volumes
    {
        Mute,
        Low,
        Med,
        High
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        int volume = (int)value;

        if (volume == 0)
            return Volumes.Mute;
        if (volume > 99)
            return Volumes.High;
        if (volume > 49)
            return Volumes.Med;
        return Volumes.Low;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) { throw new NotImplementedException(); }
}

public class SumConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        double sum = 0;

        if (values == null)
            return sum;

        foreach (object value in values)
        {
            if (value == DependencyProperty.UnsetValue)
                continue;
            sum += (double)value;
        }

        return sum;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class PlaylistItemsConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        return $"Playlist ({values[0]}/{values[1]})";
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

[ValueConversion(typeof(Color), typeof(string))]
public class ColorToHexConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null)
            return null;

        string UpperHexString(int i) => i.ToString("X2").ToUpper();
        Color color = (Color)value;
        string hex = UpperHexString(color.R) +
                      UpperHexString(color.G) +
                      UpperHexString(color.B);
        return hex;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        try
        {
            return ColorConverter.ConvertFromString("#" + value.ToString());
        }
        catch (Exception)
        {
            // ignored
        }

        return Binding.DoNothing;
    }
}

public class Tuple3Converter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        return (values[0], values[1], values[2]);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class SubIndexToIsEnabledConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Any(x => x == DependencyProperty.UnsetValue))
            return DependencyProperty.UnsetValue;

        // values[0] = Tag (index)
        // values[1] = IsPrimaryEnabled
        // values[2] = IsSecondaryEnabled
        int index = int.Parse((string)values[0]);
        bool isPrimaryEnabled = (bool)values[1];
        bool isSecondaryEnabled = (bool)values[2];

        return index == 0 ? isPrimaryEnabled : isSecondaryEnabled;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

[ValueConversion(typeof(IEnumerable<Chapter>), typeof(DoubleCollection))]
public class ChaptersToTicksConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is IEnumerable<Chapter> chapters)
        {
            // Convert tick count to seconds
            List<double> secs = chapters.Select(c => c.StartTime / 10000000.0).ToList();
            if (secs.Count <= 1)
            {
                return Binding.DoNothing;
            }

            return new DoubleCollection(secs);
        }

        return Binding.DoNothing;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

[ValueConversion(typeof(IEnumerable<Chapter>), typeof(TickPlacement))]
public class ChaptersToTickPlacementConverter : IValueConverter
{
    public TickPlacement TickPlacement { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is IEnumerable<Chapter> chapters && chapters.Count() >= 2)
        {
            return TickPlacement;
        }

        return TickPlacement.None;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class AspectRatioIsCheckedConverter : IMultiValueConverter
{
    // values[0]: SelectedAspectRatio, values[1]: current AspectRatio
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2)
        {
            return false;
        }

        if (values[0] is AspectRatio selected && values[1] is AspectRatio current)
        {
            return selected.Equals(current);
        }
        return false;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
