using System.Windows;
using System.Windows.Controls;
using LLPlayer.ViewModels;

namespace LLPlayer.Views;

public partial class WhisperDownloadDialog : UserControl
{
    public WhisperDownloadDialog()
    {
        InitializeComponent();

        DataContext = ((App)Application.Current).Container.Resolve<WhisperDownloadDialogVM>();
    }
}
