using System.Windows;

namespace LLPlayer.Controls;

public class WordClickedEventArgs(RoutedEvent args) : RoutedEventArgs(args)
{
    public required MouseClick Mouse { get; init; }
    public required string Words { get; init; }
    public required bool IsWord { get; init; }
    public required string Text { get; init; }
    public required int WordOffset { get; init; }

    // For screen subtitles
    public double WordsX { get; init; }
    public double WordsWidth { get; init; }

    // For sidebar subtitles
    public FrameworkElement? Sender { get; init; }
}

public enum MouseClick
{
    Left, Right, Middle
}

public delegate void WordClickedEventHandler(object sender, WordClickedEventArgs e);
