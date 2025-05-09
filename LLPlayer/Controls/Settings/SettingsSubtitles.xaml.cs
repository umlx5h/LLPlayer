using System.Windows;
using System.Windows.Controls;
using FlyleafLib;
using FlyleafLib.MediaPlayer.Translation;
using LLPlayer.Extensions;
using LLPlayer.Services;
using LLPlayer.Views;

namespace LLPlayer.Controls.Settings;

public partial class SettingsSubtitles : UserControl
{
    public SettingsSubtitles()
    {
        InitializeComponent();

        DataContext = ((App)Application.Current).Container.Resolve<SettingsSubtitlesVM>();
    }
}

public class SettingsSubtitlesVM : Bindable
{
    public FlyleafManager FL { get; }
    private readonly IDialogService _dialogService;

    public SettingsSubtitlesVM(FlyleafManager fl, IDialogService dialogService)
    {
        _dialogService = dialogService;
        FL = fl;
        Languages = TranslateLanguage.Langs.Values.ToList();

        if (FL.PlayerConfig.Subtitles.LanguageFallbackPrimary != null)
        {
            SelectedPrimaryLanguage = Languages.FirstOrDefault(l => l.ISO6391 == FL.PlayerConfig.Subtitles.LanguageFallbackPrimary.ISO6391);
        }

        if (FL.PlayerConfig.Subtitles.LanguageFallbackSecondary != null)
        {
            SelectedSecondaryLanguage = Languages.FirstOrDefault(l => l.ISO6391 == FL.PlayerConfig.Subtitles.LanguageFallbackSecondary.ISO6391);
        }
    }

    public List<TranslateLanguage> Languages { get; set; }

    public TranslateLanguage? SelectedPrimaryLanguage
    {
        get;
        set
        {
            if (Set(ref field, value))
            {
                if (value == null)
                {
                    FL.PlayerConfig.Subtitles.LanguageFallbackPrimary = null;
                }
                else
                {
                    FL.PlayerConfig.Subtitles.LanguageFallbackPrimary = Language.Get(value.ISO6391);
                }

                if (FL.PlayerConfig.Subtitles.LanguageFallbackSecondarySame)
                {
                    FL.PlayerConfig.Subtitles.LanguageFallbackSecondary = FL.PlayerConfig.Subtitles.LanguageFallbackPrimary;

                    SelectedSecondaryLanguage = SelectedPrimaryLanguage;
                }
            }
        }
    }

    public TranslateLanguage? SelectedSecondaryLanguage
    {
        get;
        set
        {
            if (Set(ref field, value))
            {
                if (value == null)
                {
                    FL.PlayerConfig.Subtitles.LanguageFallbackSecondary = null;
                }
                else
                {
                    FL.PlayerConfig.Subtitles.LanguageFallbackSecondary = Language.Get(value.ISO6391);
                }
            }
        }
    }

    public DelegateCommand? CmdConfigureLanguage => field ??= new(() =>
    {
        DialogParameters p = new()
        {
            { "languages", FL.PlayerConfig.Subtitles.Languages }
        };

        _dialogService.ShowDialog(nameof(SelectLanguageDialog), p, result =>
        {
            List<Language> updated = result.Parameters.GetValue<List<Language>>("languages");

            if (!FL.PlayerConfig.Subtitles.Languages.SequenceEqual(updated))
            {
                FL.PlayerConfig.Subtitles.Languages = updated;
            }
        });
    });
}
