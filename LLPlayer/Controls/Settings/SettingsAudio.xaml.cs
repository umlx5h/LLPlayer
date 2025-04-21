using System.Windows;
using System.Windows.Controls;
using FlyleafLib;
using LLPlayer.Extensions;
using LLPlayer.Services;
using LLPlayer.Views;

namespace LLPlayer.Controls.Settings;

public partial class SettingsAudio : UserControl
{
    public SettingsAudio()
    {
        InitializeComponent();

        DataContext = ((App)Application.Current).Container.Resolve<SettingsAudioVM>();
    }
}

public class SettingsAudioVM : Bindable
{
    private readonly IDialogService _dialogService;
    public FlyleafManager FL { get; }

    public SettingsAudioVM(FlyleafManager fl, IDialogService dialogService)
    {
        _dialogService = dialogService;
        FL = fl;
    }

    // ReSharper disable NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract
    public DelegateCommand CmdConfigureLanguage => field ??= new(() =>
    {
        DialogParameters p = new()
        {
            { "languages", FL.PlayerConfig.Audio.Languages }
        };

        _dialogService.ShowDialog(nameof(SelectLanguageDialog), p, result =>
        {
            List<Language> updated = result.Parameters.GetValue<List<Language>>("languages");

            if (!FL.PlayerConfig.Audio.Languages.SequenceEqual(updated))
            {
                FL.PlayerConfig.Audio.Languages = updated;
            }
        });
    });
    // ReSharper restore NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract
}
