using System.Diagnostics;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Navigation;

namespace LLPlayer.Extensions;

public static class HyperlinkHelper
{
    public static readonly DependencyProperty OpenInBrowserProperty =
        DependencyProperty.RegisterAttached(
            "OpenInBrowser",
            typeof(bool),
            typeof(HyperlinkHelper),
            new PropertyMetadata(false, OnOpenInBrowserChanged));

    public static bool GetOpenInBrowser(DependencyObject obj)
    {
        return (bool)obj.GetValue(OpenInBrowserProperty);
    }

    public static void SetOpenInBrowser(DependencyObject obj, bool value)
    {
        obj.SetValue(OpenInBrowserProperty, value);
    }

    private static void OnOpenInBrowserChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is Hyperlink hyperlink)
        {
            bool newValue = (bool)e.NewValue;
            if (newValue)
            {
                hyperlink.RequestNavigate -= OnRequestNavigate;
                hyperlink.RequestNavigate += OnRequestNavigate;
            }
            else
            {
                hyperlink.RequestNavigate -= OnRequestNavigate;
            }
        }
    }

    private static void OnRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        OpenUrlInBrowser(e.Uri.AbsoluteUri);
        e.Handled = true;
    }

    public static void OpenUrlInBrowser(string url)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });
    }
}
