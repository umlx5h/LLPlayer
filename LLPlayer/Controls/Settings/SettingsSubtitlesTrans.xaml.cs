using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using FlyleafLib.MediaPlayer.Translation;
using FlyleafLib.MediaPlayer.Translation.Services;
using LLPlayer.Extensions;
using LLPlayer.Services;

namespace LLPlayer.Controls.Settings;

public partial class SettingsSubtitlesTrans : UserControl
{
    public SettingsSubtitlesTrans()
    {
        InitializeComponent();

        DataContext = ((App)Application.Current).Container.Resolve<SettingsSubtitlesTransVM>();
    }
}

public class SettingsSubtitlesTransVM : Bindable
{
    public FlyleafManager FL { get; }

    public SettingsSubtitlesTransVM(FlyleafManager fl)
    {
        FL = fl;

        SelectedTranslateServiceType = FL.PlayerConfig.Subtitles.TranslateServiceType;
    }

    public TranslateServiceType SelectedTranslateServiceType
    {
        get;
        set
        {
            Set(ref field, value);

            FL.PlayerConfig.Subtitles.TranslateServiceType = value;

            if (FL.PlayerConfig.Subtitles.TranslateServiceSettings.TryGetValue(value, out var settings))
            {
                // It points to an instance of the same class, so change this will be reflected in the config.
                SelectedServiceSettings = settings;
            }
            else
            {
                ITranslateSettings? defaultSettings = value.DefaultSettings();
                FL.PlayerConfig.Subtitles.TranslateServiceSettings.Add(value, defaultSettings);

                SelectedServiceSettings = defaultSettings;
            }
        }
    }

    public ITranslateSettings SelectedServiceSettings { get; set => Set(ref field, value); }
}

[ValueConversion(typeof(TargetLanguage), typeof(string))]
internal class TargetLanguageEnumToStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is TargetLanguage enumValue)
        {
            return enumValue.DisplayName();
        }
        return value?.ToString() ?? string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

[ValueConversion(typeof(TargetLanguage), typeof(string))]
internal class TargetLanguageEnumToSupportedTranslateServiceConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is TargetLanguage enumValue)
        {
            TranslateServiceType supported = enumValue.SupportedServiceType();

            var types = Enum.GetValues<TranslateServiceType>().Where(t => t != TranslateServiceType.DeepLX).ToList();

            string text = "";

            foreach (var (index, type) in types.Index())
            {
                string status = "NG";
                if (supported.HasFlag(type))
                {
                    status = "OK";
                }

                text += $"{status}: {type.ToString()}";

                if (index < types.Count - 1)
                {
                    text += ", ";
                }
            }

            return $"[{text}]";
        }
        return value?.ToString() ?? string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
