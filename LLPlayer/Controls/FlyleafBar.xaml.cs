using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using FlyleafLib.MediaFramework.MediaDemuxer;
using LLPlayer.Services;
using Microsoft.Xaml.Behaviors;

namespace LLPlayer.Controls;

public partial class FlyleafBar : UserControl
{
    public FlyleafManager FL { get; }

    public FlyleafBar()
    {
        InitializeComponent();

        FL = ((App)Application.Current).Container.Resolve<FlyleafManager>();

        DataContext = this;

        // Do not hide the cursor when it is on the seek bar
        MouseEnter += OnMouseEnter;
        LostFocus += OnMouseLeave;
        MouseLeave += OnMouseLeave;
    }

    private void OnMouseLeave(object sender, RoutedEventArgs e)
    {
        SetActivity(true);
    }

    private void OnMouseEnter(object sender, MouseEventArgs e)
    {
        SetActivity(false);
    }

    private void SetActivity(bool isActive)
    {
        FL.Player.Activity.IsEnabled = isActive;
    }

    // Left-click on button to open context menu
    private void ButtonBase_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn)
        {
            return;
        }

        if (btn.ContextMenu == null)
        {
            return;
        }

        if (btn.ContextMenu.PlacementTarget == null)
        {
            // Do not hide seek bar when context menu is displayed (register once)
            btn.ContextMenu.Opened += (o, args) =>
            {
                SetActivity(false);
            };

            btn.ContextMenu.Closed += (o, args) =>
            {
                SetActivity(true);
            };

            btn.ContextMenu.PlacementTarget = btn;
        }
        btn.ContextMenu.IsOpen = true;
    }
}

public class SliderToolTipBehavior : Behavior<Slider>
{
    private const int PaddingSize = 5;
    private Popup _valuePopup;
    private TextBlock _tooltipText;
    private Border _tooltipBorder;

    public static readonly DependencyProperty ChaptersProperty =
        DependencyProperty.Register(nameof(Chapters), typeof(IList<Demuxer.Chapter>), typeof(SliderToolTipBehavior), new PropertyMetadata(null));

    public IList<Demuxer.Chapter> Chapters
    {
        get => (IList<Demuxer.Chapter>)GetValue(ChaptersProperty);
        set => SetValue(ChaptersProperty, value);
    }

    protected override void OnAttached()
    {
        base.OnAttached();

        _tooltipText = new TextBlock
        {
            Foreground = Brushes.White,
            FontSize = 12,
            TextAlignment = TextAlignment.Center
        };

        _tooltipBorder = new Border
        {
            Background = new SolidColorBrush(Colors.Black) { Opacity = 0.7 },
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(PaddingSize),
            Child = _tooltipText
        };

        _valuePopup = new Popup
        {
            AllowsTransparency = true,
            Placement = PlacementMode.Absolute,
            PlacementTarget = AssociatedObject,
            Child = _tooltipBorder
        };

        AssociatedObject.MouseMove += Slider_MouseMove;
        AssociatedObject.MouseLeave += Slider_MouseLeave;
    }

    protected override void OnDetaching()
    {
        base.OnDetaching();

        AssociatedObject.MouseMove -= Slider_MouseMove;
        AssociatedObject.MouseLeave -= Slider_MouseLeave;
    }

    private void Slider_MouseMove(object sender, MouseEventArgs e)
    {
        Point position = e.GetPosition(AssociatedObject);
        double value = CalculateSliderValue(position);

        TimeSpan hoverTime = TimeSpan.FromSeconds(value);

        string dateFormat = hoverTime.Hours >= 1 ? @"hh\:mm\:ss" : @"mm\:ss";
        string timestamp = hoverTime.ToString(dateFormat);

        // TODO: L: Allow customizable chapter display functionality
        string? chapterTitle = GetChapterTitleAtTime(hoverTime);

        _tooltipText.Inlines.Clear();
        if (chapterTitle != null)
        {
            _tooltipText.Inlines.Add(chapterTitle);
            _tooltipText.Inlines.Add(new LineBreak());
        }

        _tooltipText.Inlines.Add(timestamp);

        Window window = Window.GetWindow(AssociatedObject);
        Point cursorPoint = window!.PointToScreen(e.GetPosition(window));

        Point sliderPoint = AssociatedObject.PointToScreen(default);

        // Display on top of slider near mouse
        _valuePopup.HorizontalOffset = cursorPoint.X - (_tooltipText.ActualWidth + PaddingSize * 2) / 2;
        _valuePopup.VerticalOffset = sliderPoint.Y - _tooltipBorder.ActualHeight - 5;

        // display popup
        _valuePopup.IsOpen = true;
    }

    private void Slider_MouseLeave(object sender, MouseEventArgs e)
    {
        _valuePopup.IsOpen = false;
    }

    private double CalculateSliderValue(Point mousePosition)
    {
        double percentage = mousePosition.X / AssociatedObject.ActualWidth;
        return AssociatedObject.Minimum + (percentage * (AssociatedObject.Maximum - AssociatedObject.Minimum));
    }

    private string? GetChapterTitleAtTime(TimeSpan time)
    {
        if (Chapters == null || Chapters.Count <= 1)
        {
            return null;
        }

        var chapter = Chapters.FirstOrDefault(c => c.StartTime <= time.Ticks && time.Ticks <= c.EndTime);
        return chapter?.Title;
    }
}
