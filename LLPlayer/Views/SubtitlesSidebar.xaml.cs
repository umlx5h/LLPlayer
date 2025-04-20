using System.Windows;
using System.Windows.Controls;
using LLPlayer.Controls;
using LLPlayer.ViewModels;

namespace LLPlayer.Views;

public partial class SubtitlesSidebar : UserControl
{
    private SubtitlesSidebarVM? VM => DataContext as SubtitlesSidebarVM;

    public SubtitlesSidebar()
    {
        InitializeComponent();

        DataContext = ((App)Application.Current).Container.Resolve<SubtitlesSidebarVM>();

        Loaded += (s, e) =>
        {
            // Listen for Ctrl+F
            Window.GetWindow(this)!.PreviewKeyDown += OnPreviewKeyDown;
        };
        Unloaded += (sender, args) =>
        {
            if (DataContext is SubtitlesSidebarVM vm)
            {
                vm.Dispose();
            }
            var win = Window.GetWindow(this);
            if (win != null) win.PreviewKeyDown -= OnPreviewKeyDown;
        };
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // Activate search with Ctrl+F
        if (e.Key == System.Windows.Input.Key.F && (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == System.Windows.Input.ModifierKeys.Control)
        {
            VM?.CmdShowSearchInput.Execute();
            e.Handled = true;
            // Focus search box after UI updates
            Dispatcher.BeginInvoke(new Action(() => SidebarSearchTextBox.Focus()));
        }
        // If search is active, Escape closes it
        else if (e.Key == System.Windows.Input.Key.Escape && VM?.IsSearchActive == true)
        {
            VM.CmdClearSearch.Execute();
            e.Handled = true;
        }
    }

    private void SearchTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // Escape clears and closes search
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            VM?.CmdClearSearch.Execute();
            // Move focus away
            SubtitleListBox.Focus();
            e.Handled = true;
        }
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
