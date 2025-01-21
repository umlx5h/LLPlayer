using System.Windows;
using System.Windows.Controls;
using LLPlayer.Controls;
using LLPlayer.ViewModels;

namespace LLPlayer.Views;

public partial class SubtitlesSidebar : UserControl
{
    public SubtitlesSidebar()
    {
        InitializeComponent();

        DataContext = ((App)Application.Current).Container.Resolve<SubtitlesSidebarVM>();
    }

    /// <summary>
    /// Scroll to the current subtitle
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void SubtitleListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && SubtitleListBox.SelectedItem != null)
        {
            SubtitleListBox.ScrollIntoView(SubtitleListBox.SelectedItem);
        }
    }

    private void SelectableTextBox_OnWordClicked(object? sender, WordClickedEventArgs e)
    {
        _ = WordPopupControl.OnWordClicked(e);
    }
}
