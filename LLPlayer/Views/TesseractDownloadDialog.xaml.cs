using System.Windows;
using System.Windows.Controls;
using LLPlayer.ViewModels;

namespace LLPlayer.Views;
public partial class TesseractDownloadDialog : UserControl
{
    public TesseractDownloadDialog()
    {
        InitializeComponent();

        DataContext = ((App)Application.Current).Container.Resolve<TesseractDownloadDialogVM>();
    }
}
