using System.Windows;
using System.Windows.Controls;
using FlyleafLib.MediaPlayer;
using LLPlayer.Services;

namespace LLPlayer.Controls;

public partial class SubtitlesControl : UserControl
{
    public FlyleafManager FL { get; }

    public SubtitlesControl()
    {
        InitializeComponent();

        FL = ((App)Application.Current).Container.Resolve<FlyleafManager>();

        DataContext = this;
    }

    private void SubtitlePanel_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.HeightChanged)
        {
            // Sometimes there is a very small difference in decimal points when the subtitles are switched, and this event fires.
            // If you update the margin of the Sub at this time, the window will go wrong, so do it only when the difference is above a certain level.
            double heightDiff = Math.Abs(e.NewSize.Height - e.PreviousSize.Height);
            if (heightDiff >= 1.0)
            {
                FL.Config.Subs.SubsPanelSize = e.NewSize;
            }
        }
    }

    private async void SelectableSubtitleText_OnWordClicked(object sender, WordClickedEventArgs e)
    {
        await WordPopupControl.OnWordClicked(e);
    }

    private void SelectableSubtitleText_OnWordClickedDown(object? sender, EventArgs e)
    {
        // Assume drag and stop playback.
        if (FL.Player.Status == Status.Playing)
        {
            FL.Player.Pause();
        }
    }
}
