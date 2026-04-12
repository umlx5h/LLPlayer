using System.Windows;

namespace LLPlayer.Resources;

public partial class Slider : ResourceDictionary
{
    public Slider()
    {
        InitializeComponent();
    }
}

public static class SliderLayout
{
    public static readonly DependencyProperty BarHeightProperty =
        DependencyProperty.RegisterAttached(
            "BarHeight",
            typeof(double),
            typeof(SliderLayout),
            new FrameworkPropertyMetadata(5d, FrameworkPropertyMetadataOptions.Inherits));

    public static void SetBarHeight(DependencyObject element, double value)
    {
        element.SetValue(BarHeightProperty, value);
    }

    public static double GetBarHeight(DependencyObject element)
    {
        return (double)element.GetValue(BarHeightProperty);
    }

    public static readonly DependencyProperty TrackHeightProperty =
        DependencyProperty.RegisterAttached(
            "TrackHeight",
            typeof(double),
            typeof(SliderLayout),
            new FrameworkPropertyMetadata(10d, FrameworkPropertyMetadataOptions.Inherits));

    public static void SetTrackHeight(DependencyObject element, double value)
    {
        element.SetValue(TrackHeightProperty, value);
    }

    public static double GetTrackHeight(DependencyObject element)
    {
        return (double)element.GetValue(TrackHeightProperty);
    }

    public static readonly DependencyProperty ThumbHeightProperty =
        DependencyProperty.RegisterAttached(
            "ThumbHeight",
            typeof(double),
            typeof(SliderLayout),
            new FrameworkPropertyMetadata(10d, FrameworkPropertyMetadataOptions.Inherits));

    public static void SetThumbHeight(DependencyObject element, double value)
    {
        element.SetValue(ThumbHeightProperty, value);
    }

    public static double GetThumbHeight(DependencyObject element)
    {
        return (double)element.GetValue(ThumbHeightProperty);
    }
}
