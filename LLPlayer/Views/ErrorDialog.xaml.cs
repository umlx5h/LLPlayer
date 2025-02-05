using System.Windows;
using LLPlayer.ViewModels;
using System.Windows.Controls;
using System.Windows.Input;

namespace LLPlayer.Views;

public partial class ErrorDialog : UserControl
{
    public ErrorDialog()
    {
        InitializeComponent();

        DataContext = ((App)Application.Current).Container.Resolve<ErrorDialogVM>();
    }

    private void FrameworkElement_OnLoaded(object sender, RoutedEventArgs e)
    {
        Keyboard.Focus(sender as IInputElement);
    }

    private void ErrorDialog_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        Keyboard.Focus(sender as IInputElement);
    }

    // Topmost dialog, so it should be draggable
    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Window window)
            return;

        if (e.ChangedButton == MouseButton.Left)
        {
            window.DragMove();
        }
    }
}
