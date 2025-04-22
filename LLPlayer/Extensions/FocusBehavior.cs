using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace LLPlayer.Extensions;

public static class FocusBehavior
{
    public static readonly DependencyProperty IsFocusedProperty =
        DependencyProperty.RegisterAttached(
            "IsFocused",
            typeof(bool),
            typeof(FocusBehavior),
            new UIPropertyMetadata(false, OnIsFocusedChanged));

    public static bool GetIsFocused(DependencyObject obj) =>
        (bool)obj.GetValue(IsFocusedProperty);

    public static void SetIsFocused(DependencyObject obj, bool value) =>
        obj.SetValue(IsFocusedProperty, value);

    private static void OnIsFocusedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (!(d is UIElement element) || !(e.NewValue is bool isFocused) || !isFocused)
            return;

        // Set focus to element
        element.Dispatcher.BeginInvoke(() =>
        {
            element.Focus();
            if (element is TextBox tb)
            {
                // if TextBox, then select text
                tb.SelectAll();
                //tb.CaretIndex = tb.Text.Length;
            }
        }, DispatcherPriority.Input);
    }
}
