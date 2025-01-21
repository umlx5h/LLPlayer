using System.Windows;
using System.Windows.Controls;
using LLPlayer.ViewModels;

namespace LLPlayer.Views;

public partial class FlyleafOverlay : UserControl
{
    private FlyleafOverlayVM VM => (FlyleafOverlayVM)DataContext;

    public FlyleafOverlay()
    {
        InitializeComponent();

        DataContext = ((App)Application.Current).Container.Resolve<FlyleafOverlayVM>();
    }

    private void FlyleafOverlay_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.HeightChanged)
        {
            // The height of MainWindow cannot be used because it includes the title bar,
            // so the height is obtained here and passed on.
            double heightDiff = Math.Abs(e.NewSize.Height - e.PreviousSize.Height);

            if (heightDiff >= 1.0)
            {
                VM.FL.Config.ScreenWidth = e.NewSize.Width;
                VM.FL.Config.ScreenHeight = e.NewSize.Height;
            }
        }
    }
}
