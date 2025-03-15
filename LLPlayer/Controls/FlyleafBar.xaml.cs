using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using FlyleafLib;
using FlyleafLib.MediaFramework.MediaDemuxer;
using FlyleafLib.MediaPlayer;
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

        FL.Config.PropertyChanged += (sender, args) =>
        {
            if (args.PropertyName == nameof(FL.Config.SeekBarShowOnlyMouseOver))
            {
                // Avoided a problem in which Opacity was set to 0 when switching settings and was not displayed.
                FL.Player.Activity.ForceFullActive();
                IsShowing = true;

                if (FL.Config.SeekBarShowOnlyMouseOver && !MyCard.IsMouseOver)
                {
                    IsShowing = false;
                }
            }
        };

        FL.Player.Activity.PropertyChanged += (sender, args) =>
        {
            switch (args.PropertyName)
            {
                case nameof(FL.Player.Activity.IsEnabled):
                    if (FL.Config.SeekBarShowOnlyMouseOver)
                    {
                        IsShowing = !FL.Player.Activity.IsEnabled || MyCard.IsMouseOver;
                    }
                    break;

                case nameof(FL.Player.Activity.Mode):
                    if (!FL.Config.SeekBarShowOnlyMouseOver)
                    {
                        IsShowing = FL.Player.Activity.Mode == ActivityMode.FullActive;
                    }
                    break;
            }
        };

        if (FL.Config.SeekBarShowOnlyMouseOver)
        {
            // start in hide
            MyCard.Opacity = 0.01;
        }
        else
        {
            // start in show
            IsShowing = true;
        }
    }

    public bool IsShowing
    {
        get;
        set
        {
            if (field == value)
                return;

            field = value;
            if (value)
            {
                // Fade In
                MyCard.BeginAnimation(OpacityProperty, new DoubleAnimation()
                {
                    BeginTime = TimeSpan.Zero,
                    To = 1,
                    Duration = new Duration(TimeSpan.FromMilliseconds(FL.Config.SeekBarFadeInTimeMs))
                });
            }
            else
            {
                // Fade Out
                MyCard.BeginAnimation(OpacityProperty, new DoubleAnimation()
                {
                    BeginTime = TimeSpan.Zero,
                    // TODO: L: needs to be almost transparent to receive MouseEnter events
                    To = FL.Config.SeekBarShowOnlyMouseOver ? 0.01 : 0,
                    Duration = new Duration(TimeSpan.FromMilliseconds(FL.Config.SeekBarFadeOutTimeMs))
                });
            }
        }
    }

    private void OnMouseLeave(object sender, RoutedEventArgs e)
    {
        if (FL.Config.SeekBarShowOnlyMouseOver && FL.Player.Activity.IsEnabled)
        {
            IsShowing = false;
        }
        else
        {
            SetActivity(true);
        }
    }

    private void OnMouseEnter(object sender, MouseEventArgs e)
    {
        if (FL.Config.SeekBarShowOnlyMouseOver && FL.Player.Activity.IsEnabled)
        {
            IsShowing = true;
        }
        else
        {
            SetActivity(false);
        }
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
            btn.ContextMenu.Opened += OnContextMenuOnOpened;
            btn.ContextMenu.Closed += OnContextMenuOnClosed;
            btn.ContextMenu.MouseMove += OnContextMenuOnMouseMove;
            btn.ContextMenu.PlacementTarget = btn;
        }
        btn.ContextMenu.IsOpen = true;
    }

    private void OnContextMenuOnOpened(object o, RoutedEventArgs args)
    {
        SetActivity(false);
    }

    private void OnContextMenuOnClosed(object o, RoutedEventArgs args)
    {
        SetActivity(true);
    }

    private void OnContextMenuOnMouseMove(object o, MouseEventArgs args)
    {
        // this is necessary to keep PopupMenu visible when opened in succession when SeekBarShowOnlyMouseOver
        SetActivity(false);
    }
}

public class SliderToolTipBehavior : Behavior<Slider>
{
    private const int PaddingSize = 5;
    private Popup _valuePopup;
    private TextBlock _tooltipText;
    private Border _tooltipBorder;
    private Track? _track;

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
        _track ??= (Track)AssociatedObject.Template.FindName("PART_Track", AssociatedObject);
        Point pos = e.GetPosition(_track);
        double value = _track.ValueFromPoint(pos);
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

        // Fix for high dpi because PointToScreen returns physical coords
        cursorPoint.X /= Utils.NativeMethods.DpiX;
        cursorPoint.Y /= Utils.NativeMethods.DpiY;

        sliderPoint.X /= Utils.NativeMethods.DpiX;
        sliderPoint.Y /= Utils.NativeMethods.DpiY;

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
