using System.Windows;
using System.Windows.Controls;
using LLPlayer.Controls.Settings;
using LLPlayer.ViewModels;

namespace LLPlayer.Views;

public partial class SettingsDialog : UserControl
{
    public SettingsDialog()
    {
        InitializeComponent();

        DataContext = ((App)Application.Current).Container.Resolve<SettingsDialogVM>();
    }

    private void SettingsTreeView_OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (SettingsContent == null)
        {
            return;
        }

        if (SettingsTreeView.SelectedItem is TreeViewItem selectedItem)
        {
            string? tag = selectedItem.Tag as string;
            switch (tag)
            {
                case nameof(SettingsPlayer):
                    SettingsContent.Content = new SettingsPlayer();
                    break;

                case nameof(SettingsAudio):
                    SettingsContent.Content = new SettingsAudio();
                    break;

                case nameof(SettingsVideo):
                    SettingsContent.Content = new SettingsVideo();
                    break;

                case nameof(SettingsSubtitles):
                    SettingsContent.Content = new SettingsSubtitles();
                    break;

                case nameof(SettingsSubtitlesPS):
                    SettingsContent.Content = new SettingsSubtitlesPS();
                    break;

                case nameof(SettingsSubtitlesASR):
                    SettingsContent.Content = new SettingsSubtitlesASR();
                    break;

                case nameof(SettingsSubtitlesOCR):
                    SettingsContent.Content = new SettingsSubtitlesOCR();
                    break;

                case nameof(SettingsSubtitlesTrans):
                    SettingsContent.Content = new SettingsSubtitlesTrans();
                    break;

                case nameof(SettingsSubtitlesAction):
                    SettingsContent.Content = new SettingsSubtitlesAction();
                    break;

                case nameof(SettingsKeys):
                    SettingsContent.Content = new SettingsKeys();
                    break;

                case nameof(SettingsKeysOffset):
                    SettingsContent.Content = new SettingsKeysOffset();
                    break;

                case nameof(SettingsThemes):
                    SettingsContent.Content = new SettingsThemes();
                    break;

                case nameof(SettingsPlugins):
                    SettingsContent.Content = new SettingsPlugins();
                    break;

                case nameof(SettingsAbout):
                    SettingsContent.Content = new SettingsAbout();
                    break;
            }
        }
    }
}
