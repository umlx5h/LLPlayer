using LLPlayer.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LLPlayer.Views;

public partial class SelectLanguageDialog : UserControl
{
    public SelectLanguageDialog()
    {
        InitializeComponent();

        DataContext = ((App)Application.Current).Container.Resolve<SelectLanguageDialogVM>();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Window? window = Window.GetWindow(this);
        window!.CommandBindings.Add(new CommandBinding(ApplicationCommands.Find, OnFindExecuted));
    }

    private void OnFindExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
        e.Handled = true;
    }
}
