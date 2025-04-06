using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using FlyleafLib.MediaPlayer;
using LLPlayer.Services;

namespace LLPlayer.Converters;

public class WidthPercentageMultiConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values[0] is double actualWidth && values[1] is double percentage)
        {
            return actualWidth * (percentage / 100.0);
        }
        return 0;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class SubTextMaskConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length != 6)
            return DependencyProperty.UnsetValue;

        if (values[0] is int index &&          // Index of sub to be processed
            values[1] is string displayText && // Sub text to be processed (may be translated)
            values[2] is string text &&        // Sub text to be processed
            values[3] is int selectedIndex &&  // Index of the selected ListItem
            values[4] is bool isEnabled &&     // whether to enable this feature
            values[5] is bool showOriginal)    // whether to show original text
        {
            string show = showOriginal ? text : displayText;

            if (isEnabled && index > selectedIndex && !string.IsNullOrEmpty(show))
            {
                return new string('_', show.Length);
            }

            return show;
        }
        return DependencyProperty.UnsetValue;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

public class SubTextFlowDirectionConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length != 3)
        {
            return DependencyProperty.UnsetValue;
        }

        if (values[0] is bool isTranslated &&
            values[1] is int subIndex &&
            values[2] is FlyleafManager fl)
        {
            var language = isTranslated ? fl.PlayerConfig.Subtitles.TranslateLanguage : fl.Player.SubtitlesManager[subIndex].Language;
            if (language != null && language.IsRTL)
            {
                return FlowDirection.RightToLeft;
            }

            return FlowDirection.LeftToRight;
        }

        return DependencyProperty.UnsetValue;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

public class ListItemIndexVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length != 3)
        {
            return DependencyProperty.UnsetValue;
        }

        if (values[0] is int itemIndex &&     // Index of sub to be processed
            values[1] is int selectedIndex && // Index of the selected ListItem
            values[2] is bool isEnabled)      // whether to enable this feature
        {
            if (isEnabled && itemIndex > selectedIndex)
            {
                return Visibility.Collapsed;
            }

            return Visibility.Visible;
        }
        return DependencyProperty.UnsetValue;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

[ValueConversion(typeof(TimeSpan), typeof(string))]
public class TimeSpanToStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is TimeSpan ts)
        {
            if (ts.TotalHours >= 1)
            {
                return ts.ToString(@"hh\:mm\:ss");
            }
            else
            {
                return ts.ToString(@"mm\:ss");
            }
        }

        return DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

[ValueConversion(typeof(SubtitleData), typeof(WriteableBitmap))]
public class SubBitmapImageSourceConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not SubtitleData item)
        {
            return null;
        }

        if (!item.IsBitmap || item.Bitmap == null || !string.IsNullOrEmpty(item.Text))
        {
            return null;
        }

        WriteableBitmap wb = item.Bitmap.SubToWritableBitmap(false);

        return wb;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

[ValueConversion(typeof(int), typeof(string))]
public class SubIndexToDisplayStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int subIndex)
        {
            return subIndex == 0 ? "Primary" : "Secondary";
        }
        return DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string str)
        {
            return str == "Primary" ? 0 : 1;
        }
        return DependencyProperty.UnsetValue;
    }
}
