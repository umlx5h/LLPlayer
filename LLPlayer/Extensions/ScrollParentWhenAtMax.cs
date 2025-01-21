using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Xaml.Behaviors;

namespace LLPlayer.Extensions;

/// <summary>
/// Behavior to disable scrolling in ListView
/// ref: https://stackoverflow.com/questions/1585462/bubbling-scroll-events-from-a-listview-to-its-parent
/// </summary>
public class ScrollParentWhenAtMax : Behavior<FrameworkElement>
{
    protected override void OnAttached()
    {
        base.OnAttached();
        AssociatedObject.PreviewMouseWheel += PreviewMouseWheel;
    }

    protected override void OnDetaching()
    {
        AssociatedObject.PreviewMouseWheel -= PreviewMouseWheel;
        base.OnDetaching();
    }

    private void PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var scrollViewer = GetVisualChild<ScrollViewer>(AssociatedObject);
        var scrollPos = scrollViewer.ContentVerticalOffset;
        if ((scrollPos == scrollViewer.ScrollableHeight && e.Delta < 0)
            || (scrollPos == 0 && e.Delta > 0))
        {
            e.Handled = true;
            MouseWheelEventArgs e2 = new(e.MouseDevice, e.Timestamp, e.Delta);
            e2.RoutedEvent = UIElement.MouseWheelEvent;
            AssociatedObject.RaiseEvent(e2);
        }
    }

    private static T GetVisualChild<T>(DependencyObject parent) where T : Visual
    {
        T child = default(T);

        int numVisuals = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < numVisuals; i++)
        {
            Visual v = (Visual)VisualTreeHelper.GetChild(parent, i);
            child = v as T;
            if (child == null)
            {
                child = GetVisualChild<T>(v);
            }

            if (child != null)
            {
                break;
            }
        }

        return child;
    }
}
