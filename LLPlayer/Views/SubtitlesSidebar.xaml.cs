using System.Windows;
using System.Windows.Controls;
using LLPlayer.Controls;
using LLPlayer.ViewModels;

namespace LLPlayer.Views;

public partial class SubtitlesSidebar : UserControl
{
    private SubtitlesSidebarVM VM => (SubtitlesSidebarVM)DataContext;
    public SubtitlesSidebar()
    {
        InitializeComponent();

        DataContext = ((App)Application.Current).Container.Resolve<SubtitlesSidebarVM>();

        Loaded += (sender, args) =>
        {
            VM.RequestScrollToTop += OnRequestScrollToTop;
        };

        Unloaded += (sender, args) =>
        {
            VM.RequestScrollToTop -= OnRequestScrollToTop;
            VM.Dispose();
        };
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

    /// <summary>
    /// Scroll to the top subtitle
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="args"></param>
    private void OnRequestScrollToTop(object? sender, EventArgs args)
    {
        if (SubtitleListBox.Items.Count <= 0)
            return;

        var first = SubtitleListBox.Items[0];
        if (first != null)
        {
            SubtitleListBox.ScrollIntoView(first);
        }
    }

    private void SelectableTextBox_OnWordClicked(object? sender, WordClickedEventArgs e)
    {
        _ = WordPopupControl.OnWordClicked(e);
    }
}
