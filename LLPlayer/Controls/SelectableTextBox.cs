using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LLPlayer.Controls;

public class SelectableTextBox : TextBox
{
    static SelectableTextBox()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(SelectableTextBox), new FrameworkPropertyMetadata(typeof(SelectableTextBox)));
    }

    public static readonly RoutedEvent WordClickedEvent =
        EventManager.RegisterRoutedEvent(nameof(WordClicked), RoutingStrategy.Bubble, typeof(WordClickedEventHandler), typeof(SelectableTextBox));

    public event WordClickedEventHandler WordClicked
    {
        add => AddHandler(WordClickedEvent, value);
        remove => RemoveHandler(WordClickedEvent, value);
    }

    private bool _isDragging;
    private int _dragStartIndex = -1;
    private int _dragEndIndex = -1;

    private Point _mouseDownPosition;

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);

        _isDragging = true;
        _mouseDownPosition = e.GetPosition(this);
        _dragStartIndex = GetCharacterIndexFromPoint(_mouseDownPosition, true);
        _dragEndIndex = _dragStartIndex;

        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        base.OnPreviewMouseMove(e);

        if (_isDragging)
        {
            Point currentPosition = e.GetPosition(this);

            if (currentPosition != _mouseDownPosition)
            {
                int currentIndex = GetCharacterIndexFromPoint(currentPosition, true);
                if (currentIndex != -1 && currentIndex != _dragEndIndex)
                {
                    _dragEndIndex = currentIndex;
                }
            }
        }
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonUp(e);

        if (_isDragging)
        {
            _isDragging = false;
            ReleaseMouseCapture();
            e.Handled = true;

            if (_dragStartIndex == _dragEndIndex)
            {
                // Click
                HandleClick(_mouseDownPosition, MouseClick.Left);
            }
            else
            {
                // Drag
                HandleDragSelection();
            }
        }
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);

        Point clickPosition = e.GetPosition(this);

        HandleClick(clickPosition, MouseClick.Right);

        // Right clicks other than words are currently disabled, consider creating another one
        e.Handled = true;
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        // Middle click: Sentence Lookup
        if (e.ChangedButton == MouseButton.Middle)
        {
            if (string.IsNullOrEmpty(Text))
            {
                return;
            }

            // Change line breaks to spaces to improve translation accuracy.
            string fixText = Text.ReplaceLineEndings(" ");

            WordClickedEventArgs args = new(WordClickedEvent)
            {
                Mouse = MouseClick.Middle,
                Words = fixText,
                IsWord = false,
                Text = fixText,
                WordOffset = 0,
                Sender = this
            };

            RaiseEvent(args);
        }
    }

    private void HandleClick(Point point, MouseClick mouse)
    {
        // false because it fires only above the word
        int charIndex = GetCharacterIndexFromPoint(point, false);

        if (charIndex == -1 || string.IsNullOrEmpty(Text))
        {
            return;
        }

        int start = FindWordStart(charIndex);
        int end = FindWordEnd(charIndex);

        // get word
        string word = Text.Substring(start, end - start).Trim();

        if (string.IsNullOrEmpty(word))
        {
            return;
        }

        WordClickedEventArgs args = new(WordClickedEvent)
        {
            Mouse = mouse,
            Words = word,
            IsWord = true,
            Text = Text.ReplaceLineEndings(" "),
            WordOffset = start,
            Sender = this
        };
        RaiseEvent(args);
    }

    private void HandleDragSelection()
    {
        // Support right to left drag
        int rawStart = Math.Min(_dragStartIndex, _dragEndIndex);
        int rawEnd = Math.Max(_dragStartIndex, _dragEndIndex);

        // Adjust to word boundaries
        int adjustedStart = FindWordStart(rawStart);
        int adjustedEnd = FindWordEnd(rawEnd);

        // Extract the substring within the adjusted selection
        // TODO: L: If there is only a delimiter after the end, I want to include it in the selection (should improve translation accuracy)
        string selectedText = Text.Substring(adjustedStart, adjustedEnd - adjustedStart);

        WordClickedEventArgs args = new(WordClickedEvent)
        {
            Mouse = MouseClick.Left,
            // Change line breaks to spaces to improve translation accuracy.
            Words = selectedText.ReplaceLineEndings(" "),
            IsWord = false,
            Text = Text.ReplaceLineEndings(" "),
            WordOffset = adjustedStart,
            Sender = this
        };

        RaiseEvent(args);
    }

    private int FindWordStart(int index)
    {
        if (index < 0 || index > Text.Length)
        {
            return 0;
        }

        while (index > 0 && !IsWordSeparator(Text[index - 1]))
        {
            index--;
        }

        return index;
    }

    private int FindWordEnd(int index)
    {
        if (index < 0 || index > Text.Length)
        {
            return Text.Length;
        }

        while (index < Text.Length && !IsWordSeparator(Text[index]))
        {
            index++;
        }

        return index;
    }

    private static readonly HashSet<char> ExcludedSeparators = ['\'', '-'];

    private static bool IsWordSeparator(char c)
    {
        if (ExcludedSeparators.Contains(c))
        {
            return false;
        }

        return char.IsWhiteSpace(c) || char.IsPunctuation(c);
    }

    #region Cursor Hand
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        // Change cursor only over word text
        int charIndex = GetCharacterIndexFromPoint(e.GetPosition(this), false);

        Cursor = charIndex != -1 ? Cursors.Hand : Cursors.Arrow;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);

        if (Cursor == Cursors.Hand)
        {
            Cursor = Cursors.Arrow;
        }
    }
    #endregion
}
