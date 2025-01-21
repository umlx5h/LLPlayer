using System.Collections;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Xaml.Behaviors;

namespace LLPlayer.Extensions;

/// <summary>
/// Behavior to add the ability to swap rows in a DataGrid by drag and drop
/// </summary>
public class DataGridRowOrderBehavior<T> : Behavior<DataGrid>
    where T : class
{
    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(nameof(ItemsSource), typeof(IList), typeof (DataGridRowOrderBehavior<T>), new PropertyMetadata(null));

    /// <summary>
    /// ObservableCollection to be reordered bound to DataGrid
    /// </summary>
    public ObservableCollection<T> ItemsSource
    {
        get => (ObservableCollection<T>)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public static readonly DependencyProperty DragTargetNameProperty =
        DependencyProperty.Register(nameof(DragTargetName), typeof(string), typeof (DataGridRowOrderBehavior<T>), new PropertyMetadata(null));

    /// <summary>
    /// Control name of the element to be dragged
    /// </summary>
    public string DragTargetName
    {
        get => (string)GetValue(DragTargetNameProperty);
        set => SetValue(DragTargetNameProperty, value);
    }

    private T? _draggedItem; // Item in row to be dragged

    protected override void OnAttached()
    {
        base.OnAttached();

        if (AssociatedObject == null)
        {
            return;
        }

        AssociatedObject.AllowDrop = true;

        AssociatedObject.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
        AssociatedObject.DragOver += OnDragOver;
        AssociatedObject.Drop += OnDrop;
    }

    protected override void OnDetaching()
    {
        base.OnDetaching();

        if (AssociatedObject == null)
        {
            return;
        }

        AssociatedObject.AllowDrop = false;

        AssociatedObject.PreviewMouseLeftButtonDown -= OnPreviewMouseLeftButtonDown;
        AssociatedObject.DragOver -= OnDragOver;
        AssociatedObject.Drop -= OnDrop;
    }

    private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ItemsSource == null)
        {
            return;
        }

        // 1: Determines if it is on a "drag handle".
        if (!IsDragHandle(e.OriginalSource))
        {
            return;
        }

        // 2: Identify Row
        DataGridRow? row = UIHelper.FindParent<DataGridRow>((DependencyObject)e.OriginalSource);
        if (row == null)
        {
            return;
        }

        // Select the row
        AssociatedObject.UnselectAll();
        row.IsSelected = true;
        row.Focus();

        _draggedItem = row.Item as T;

        if (_draggedItem != null)
        {
            // Start dragging
            DragDrop.DoDragDrop(AssociatedObject, _draggedItem, DragDropEffects.Move);
        }
    }

    /// <summary>
    /// Dragging (while the mouse is moving)
    /// </summary>
    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (_draggedItem == null)
        {
            return;
        }

        e.Handled = true;

        // Find the line where the mouse is now.
        DataGridRow? row = UIHelper.FindParent<DataGridRow>((DependencyObject)e.OriginalSource);
        if (row == null)
        {
            return;
        }

        var targetItem = row.Item;
        if (targetItem == null || targetItem == _draggedItem)
        {
            // If it's the same line, nothing.
            return;
        }

        T? targetRow = targetItem as T;
        if (targetRow == null)
        {
            return;
        }

        // Get the index of each row to be replaced
        int oldIndex = ItemsSource.IndexOf(_draggedItem);
        int newIndex = ItemsSource.IndexOf(targetRow);

        if (oldIndex < 0 || newIndex < 0 || oldIndex == newIndex)
        {
            return;
        }

        // Swap lines
        ItemsSource.Move(oldIndex, newIndex);
    }

    /// <summary>
    /// When dropped
    /// </summary>
    private void OnDrop(object sender, DragEventArgs e)
    {
        // Clear state as it is being reordered during drag.
        _draggedItem = null;
    }

    /// <summary>
    /// Judges whether an element is ready to start dragging.
    /// </summary>
    private bool IsDragHandle(object originalSource)
    {
        return UIHelper.FindParentWithName(originalSource as DependencyObject, DragTargetName);
    }
}
