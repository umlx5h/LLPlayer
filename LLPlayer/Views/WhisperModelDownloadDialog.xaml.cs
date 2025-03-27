using System.Windows;
using System.Windows.Controls;
using LLPlayer.ViewModels;

namespace LLPlayer.Views;

public partial class WhisperModelDownloadDialog : UserControl
{
    public WhisperModelDownloadDialog()
    {
        InitializeComponent();

        DataContext = ((App)Application.Current).Container.Resolve<WhisperModelDownloadDialogVM>();
    }
}
