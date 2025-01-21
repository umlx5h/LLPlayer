using System.Windows;
using System.Windows.Controls;
using LLPlayer.ViewModels;

namespace LLPlayer.Views;

public partial class SubtitlesDownloaderDialog : UserControl
{
    public SubtitlesDownloaderDialog()
    {
        InitializeComponent();

        DataContext = ((App)Application.Current).Container.Resolve<SubtitlesDownloaderDialogVM>();
    }
}
