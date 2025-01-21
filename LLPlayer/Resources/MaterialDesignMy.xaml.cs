using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LLPlayer.Resources;

public partial class MaterialDesignMy : ResourceDictionary
{
    public MaterialDesignMy()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Do not close the menu when right-clicking or CTRL+left-clicking on a context menu
    /// This is achieved by dynamically setting StaysOpenOnClick
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void MenuItem_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is MenuItem menuItem)
        {
            if ((Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl)) ||
                Mouse.RightButton == MouseButtonState.Pressed)
            {
                menuItem.StaysOpenOnClick = true;
            }
            else if (menuItem.StaysOpenOnClick)
            {
                menuItem.StaysOpenOnClick = false;
            }
        }
    }
}
