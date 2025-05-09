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

    public ITranslateSettings? SelectedServiceSettings { get; set => Set(ref field, value); }

    public DelegateCommand? CmdSetDefaultPromptKeepContext => field ??= new(() =>
    {
        FL.PlayerConfig.Subtitles.TranslateChatConfig.PromptKeepContext = TranslateChatConfig.DefaultPromptKeepContext.ReplaceLineEndings("\n");
    });

    public DelegateCommand? CmdSetDefaultPromptOneByOne => field ??= new(() =>
    {
        FL.PlayerConfig.Subtitles.TranslateChatConfig.PromptOneByOne = TranslateChatConfig.DefaultPromptOneByOne.ReplaceLineEndings("\n");
    });
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

[ValueConversion(typeof(TranslateServiceType), typeof(string))]
internal class TranslateServiceTypeEnumToStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is TranslateServiceType enumValue)
        {
            string displayName = enumValue.GetDescription();

            if (enumValue.IsLLM())
            {
                return $"{displayName} (LLM)";
            }

            return $"{displayName}";
        }

        return value?.ToString() ?? string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

[ValueConversion(typeof(TranslateServiceType), typeof(string))]
internal class TranslateServiceTypeEnumToUrlConverter : IValueConverter
{
    private const string BaseUrl = "https://github.com/umlx5h/LLPlayer/wiki/Translation-Engine";
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is TranslateServiceType enumValue)
        {
            string displayName = enumValue.GetDescription();

            return $"{BaseUrl}#{displayName.ToLower().Replace(' ', '-')}";
        }
        return BaseUrl;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

[ValueConversion(typeof(TargetLanguage), typeof(string))]
internal class TargetLanguageEnumToNoSupportedTranslateServiceConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is TargetLanguage enumValue)
        {
            TranslateServiceType supported = enumValue.SupportedServiceType();

            // DeepL = DeepLX
            List<TranslateServiceType> notSupported =
               Enum.GetValues<TranslateServiceType>()
                    .Where(t => t != TranslateServiceType.DeepLX)
                    .Where(t => !supported.HasFlag(t))
                    .ToList();

            if (notSupported.Count == 0)
            {
                return "[All supported]";
            }

            return string.Join(',', notSupported.Select(t => t.ToString()));
        }
        return value?.ToString() ?? string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
