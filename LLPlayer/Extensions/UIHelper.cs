using System.Windows;
using System.Windows.Media;

namespace LLPlayer.Extensions;

public static class UIHelper
{
    // ref: https://www.infragistics.com/community/blogs/b/blagunas/posts/find-the-parent-control-of-a-specific-type-in-wpf-and-silverlight
    public static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        //get parent item
        DependencyObject? parentObject = VisualTreeHelper.GetParent(child);

        //we've reached the end of the tree
        if (parentObject == null)
            return null;

        //check if the parent matches the type we're looking for
        if (parentObject is T parent)
            return parent;

        return FindParent<T>(parentObject);
    }

    /// <summary>
    /// Traverses the visual tree upward from the current element to determine if an element with the specified name exists.j
    /// </summary>
    /// <param name="element">Element to start with (current element)</param>
    /// <param name="name">Name of the element</param>
    /// <returns>True if the element with the specified name exists, false otherwise.</returns>
    public static bool FindParentWithName(DependencyObject? element, string name)
    {
        if (element == null)
        {
            return false;
        }

        DependencyObject? current = element;

        while (current != null)
        {
            if (current is FrameworkElement fe && fe.Name == name)
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }
}
