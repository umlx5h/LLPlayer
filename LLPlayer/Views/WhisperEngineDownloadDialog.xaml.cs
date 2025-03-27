using System.Windows;
using System.Windows.Controls;
using LLPlayer.ViewModels;

namespace LLPlayer.Views;

public partial class WhisperEngineDownloadDialog : UserControl
{
    public WhisperEngineDownloadDialog()
    {
        InitializeComponent();

        DataContext = ((App)Application.Current).Container.Resolve<WhisperEngineDownloadDialogVM>();
    }
}
