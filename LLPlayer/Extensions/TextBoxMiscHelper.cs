using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LLPlayer.Extensions;

public static class TextBoxMiscHelper
{
    public static bool GetIsHexValidationEnabled(DependencyObject obj)
    {
        return (bool)obj.GetValue(IsHexValidationEnabledProperty);
    }

    public static void SetIsHexValidationEnabled(DependencyObject obj, bool value)
    {
        obj.SetValue(IsHexValidationEnabledProperty, value);
    }

    public static readonly DependencyProperty IsHexValidationEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsHexValidationEnabled",
            typeof(bool),
            typeof(TextBoxMiscHelper),
            new UIPropertyMetadata(false, OnIsHexValidationEnabledChanged));

    private static void OnIsHexValidationEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TextBox textBox)
        {
            if ((bool)e.NewValue)
            {
                textBox.PreviewTextInput += OnPreviewTextInput;
            }
            else
            {
                textBox.PreviewTextInput -= OnPreviewTextInput;
            }
        }
    }

    private static void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !Regex.IsMatch(e.Text, "^[0-9a-f]+$", RegexOptions.IgnoreCase);
    }
}
