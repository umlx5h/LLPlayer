using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using FlyleafLib;
using LLPlayer.Extensions;
using NMeCab.Specialized;

namespace LLPlayer.Controls;

public partial class SelectableSubtitleText : UserControl
{
    private readonly Binding _bindFontSize;
    private readonly Binding _bindFontWeight;
    private readonly Binding _bindFontFamily;
    private readonly Binding _bindFontStyle;
    private readonly Binding _bindFill;
    private readonly Binding _bindStroke;
    private readonly Binding _bindStrokeThicknessInitial;

    private OutlinedTextBlock? _wordStart;

    public SelectableSubtitleText()
    {
        InitializeComponent();

        // DataContext is set in WrapPanel, so there is no need to use ElementName.
        //_bindFontSize = new Binding(nameof(FontSize))
        //{
        //    //ElementName = nameof(Root),
        //    Mode = BindingMode.OneWay
        //};
        _bindFontSize = new Binding(nameof(FontSize));
        _bindFontWeight = new Binding(nameof(FontWeight));
        _bindFontFamily = new Binding(nameof(FontFamily));
        _bindFontStyle = new Binding(nameof(FontStyle));
        _bindFill = new Binding(nameof(Fill));
        _bindStroke = new Binding(nameof(Stroke));
        _bindStrokeThicknessInitial = new Binding(nameof(StrokeThicknessInitial));
    }

    public static readonly RoutedEvent WordClickedEvent =
        EventManager.RegisterRoutedEvent(nameof(WordClicked), RoutingStrategy.Bubble, typeof(WordClickedEventHandler), typeof(SelectableSubtitleText));

    public event WordClickedEventHandler WordClicked
    {
        add => AddHandler(WordClickedEvent, value);
        remove => RemoveHandler(WordClickedEvent, value);
    }

    public event EventHandler? WordClickedDown;

    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(SelectableSubtitleText), new FrameworkPropertyMetadata(string.Empty, OnTextChanged));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    private string _textFix;

    public static readonly DependencyProperty IsTranslatedProperty =
        DependencyProperty.Register(nameof(IsTranslated), typeof(bool), typeof(SelectableSubtitleText), new FrameworkPropertyMetadata(false));

    public bool IsTranslated
    {
        get => (bool)GetValue(IsTranslatedProperty);
        set => SetValue(IsTranslatedProperty, value);
    }

    public static readonly DependencyProperty LanguageProperty =
        DependencyProperty.Register(nameof(Language), typeof(Language), typeof(SelectableSubtitleText), new FrameworkPropertyMetadata(null, OnLanguageChanged));

    public Language Language
    {
        get => (Language)GetValue(LanguageProperty);
        set => SetValue(LanguageProperty, value);
    }

    public static readonly DependencyProperty SubIndexProperty =
        DependencyProperty.Register(nameof(SubIndex), typeof(int), typeof(SelectableSubtitleText), new FrameworkPropertyMetadata(0));

    public int SubIndex
    {
        get => (int)GetValue(SubIndexProperty);
        set => SetValue(SubIndexProperty, value);
    }

    public static readonly DependencyProperty FillProperty =
        OutlinedTextBlock.FillProperty.AddOwner(typeof(SelectableSubtitleText));

    public Brush Fill
    {
        get => (Brush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public static readonly DependencyProperty StrokeProperty =
        OutlinedTextBlock.StrokeProperty.AddOwner(typeof(SelectableSubtitleText));

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public static readonly DependencyProperty WidthPercentageProperty =
        DependencyProperty.Register(nameof(WidthPercentage), typeof(double), typeof(SelectableSubtitleText), new FrameworkPropertyMetadata(60.0, OnWidthPercentageChanged));

    public double WidthPercentage
    {
        get => (double)GetValue(WidthPercentageProperty);
        set => SetValue(WidthPercentageProperty, value);
    }

    public static readonly DependencyProperty WidthPercentageFixProperty =
        DependencyProperty.Register(nameof(WidthPercentageFix), typeof(double), typeof(SelectableSubtitleText), new FrameworkPropertyMetadata(60.0));

    public double WidthPercentageFix
    {
        get => (double)GetValue(WidthPercentageFixProperty);
        set => SetValue(WidthPercentageFixProperty, value);
    }

    public static readonly DependencyProperty IgnoreLineBreakProperty =
        DependencyProperty.Register(nameof(IgnoreLineBreak), typeof(bool), typeof(SelectableSubtitleText), new FrameworkPropertyMetadata(false));

    public bool IgnoreLineBreak
    {
        get => (bool)GetValue(IgnoreLineBreakProperty);
        set => SetValue(IgnoreLineBreakProperty, value);
    }

    public static readonly DependencyProperty StrokeThicknessInitialProperty =
        DependencyProperty.Register(nameof(StrokeThicknessInitial), typeof(double), typeof(SelectableSubtitleText), new FrameworkPropertyMetadata(3.0));

    public double StrokeThicknessInitial
    {
        get => (double)GetValue(StrokeThicknessInitialProperty);
        set => SetValue(StrokeThicknessInitialProperty, value);
    }

    private static void OnWidthPercentageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ctl = (SelectableSubtitleText)d;
        ctl.WidthPercentageFix = (double)e.NewValue;
    }

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ctl = (SelectableSubtitleText)d;
        ctl.SetText((string)e.NewValue);
    }

    private static void OnLanguageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ctl = (SelectableSubtitleText)d;
        if (ctl.Language != null && ctl.Language.IsRTL)
        {
            ctl.wrapPanel.FlowDirection = FlowDirection.RightToLeft;
        }
        else
        {
            ctl.wrapPanel.FlowDirection = FlowDirection.LeftToRight;
        }
    }

    private static readonly Lazy<MeCabIpaDicTagger> MeCabTagger = new(() => MeCabIpaDicTagger.Create(), true);

    private void SetText(string text)
    {
        if (text == null)
        {
            return;
        }

        _textFix = text.ReplaceLineEndings(" ");

        if (IgnoreLineBreak)
        {
            text = _textFix;
        }

        bool containLineBreak = text.Contains('\n');

        // TODO: L: If each line break has a leading "-", I want to respect it.

        // If it contains line feeds, expand them to the full screen width (respecting the formatting in the SRT subtitle)
        WidthPercentageFix = containLineBreak ? 100.0 : WidthPercentage;

        wrapPanel.Children.Clear();
        _wordStart = null;

        string[] lines = text.SplitToLines().ToArray();

        var wordOffset = 0;

        //  SelectableTextBox uses char.IsPunctuation(), so use a regular expression for it.
        // TODO: L: Sharing the code with TextBox
        string splitPattern = @"((?:[^\P{P}'-]+|\s))";

        // Use an OutlinedTextBlock for each word to display the border Text and enclose it in a WrapPanel
        for (int i = 0; i < lines.Length; i++)
        {
            List<string> words;

            if (Language != null && Language.ISO6391 == "ja")
            {
                // word segmentation for Japanese
                words = new List<string>();

                // TODO: L: Also do word segmentation in sidebar
                var nodes = MeCabTagger.Value.Parse(lines[i]);
                foreach (var node in nodes)
                {
                    // If there are space-separated characters, such as English, add them manually since they are not on the Surface
                    if (char.IsWhiteSpace(lines[i][node.BPos]))
                    {
                        words.Add(" ");
                    }
                    words.Add(node.Surface);
                }
            }
            else
            {
                words = Regex.Split(lines[i], splitPattern)
                    .Where(w => w != "").ToList();
            }

            foreach (string word in words)
            {
                if (string.IsNullOrWhiteSpace(word))
                {
                    // Blanks are inserted with TextBlock.
                    TextBlock space = new()
                    {
                        Text = word,
                        // Created a click judgment to prevent playback toggling when clicking between words.
                        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
                    };
                    space.SetBinding(TextBlock.FontSizeProperty, _bindFontSize);
                    space.SetBinding(TextBlock.FontWeightProperty, _bindFontWeight);
                    space.SetBinding(TextBlock.FontStyleProperty, _bindFontStyle);
                    space.SetBinding(TextBlock.FontFamilyProperty, _bindFontFamily);
                    wrapPanel.Children.Add(space);
                    wordOffset += word.Length;
                    continue;
                }

                bool isSplitChar = Regex.IsMatch(word, splitPattern);

                OutlinedTextBlock textBlock = new()
                {
                    Text = word,
                    ClipToBounds = false,
                    TextWrapping = TextWrapping.Wrap,
                    StrokePosition = StrokePosition.Outside,
                    IsHitTestVisible = false,
                    WordOffset = wordOffset,
                    // Fixed because the word itself is inverted
                    FlowDirection = FlowDirection.LeftToRight
                };

                wordOffset += word.Length;

                textBlock.SetBinding(OutlinedTextBlock.FontSizeProperty, _bindFontSize);
                textBlock.SetBinding(OutlinedTextBlock.FontWeightProperty, _bindFontWeight);
                textBlock.SetBinding(OutlinedTextBlock.FontStyleProperty, _bindFontStyle);
                textBlock.SetBinding(OutlinedTextBlock.FontFamilyProperty, _bindFontFamily);
                textBlock.SetBinding(OutlinedTextBlock.FillProperty, _bindFill);
                textBlock.SetBinding(OutlinedTextBlock.StrokeProperty, _bindStroke);
                textBlock.SetBinding(OutlinedTextBlock.StrokeThicknessInitialProperty, _bindStrokeThicknessInitial);

                if (isSplitChar)
                {
                    wrapPanel.Children.Add(textBlock);
                }
                else
                {
                    Border border = new()
                    {
                        // Set brush to Border because OutlinedTextBlock's character click judgment is only on the character.
                        //ref: https://stackoverflow.com/questions/50653308/hit-testing-a-transparent-element-in-a-transparent-window
                        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
                        BorderThickness = new Thickness(1),
                        IsHitTestVisible = true,
                        Child = textBlock,
                        Cursor = Cursors.Hand,
                    };

                    border.MouseLeftButtonDown += WordMouseLeftButtonDown;
                    border.MouseLeftButtonUp += WordMouseLeftButtonUp;
                    border.MouseRightButtonUp += WordMouseRightButtonUp;
                    border.MouseUp += WordMouseMiddleButtonUp;

                    // Change background color on mouse over
                    border.MouseEnter += (_, _) =>
                    {
                        border.BorderBrush = SubIndex == 0 ? Brushes.Yellow : Brushes.Green;
                        border.Background = new SolidColorBrush(Color.FromArgb(80, 127, 127, 127));
                    };
                    border.MouseLeave += (_, _) =>
                    {
                        border.BorderBrush = null;
                        border.Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
                    };

                    wrapPanel.Children.Add(border);
                }
            }

            if (containLineBreak && i != lines.Length - 1)
            {
                // Add line breaks except at the end when there are two or more lines
                wrapPanel.Children.Add(new NewLine());
            }
        }
    }

    private void WordMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border { Child: OutlinedTextBlock word })
        {
            _wordStart = word;
            WordClickedDown?.Invoke(this, EventArgs.Empty);

            e.Handled = true;
        }
    }

    private void WordMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border { Child: OutlinedTextBlock word })
        {
            if (_wordStart == word)
            {
                // word clicked
                Point wordPoint = word.TranslatePoint(default, Root);

                WordClickedEventArgs args = new(WordClickedEvent)
                {
                    Mouse = MouseClick.Left,
                    Words = word.Text,
                    IsWord = true,
                    Text = _textFix,
                    IsTranslated = IsTranslated,
                    SubIndex = SubIndex,
                    WordOffset = word.WordOffset,
                    WordsX = wordPoint.X,
                    WordsWidth = word.ActualWidth
                };
                RaiseEvent(args);
            }
            else if (_wordStart != null)
            {
                // phrase selected
                var wordStart = _wordStart!;
                var wordEnd = word;

                // support right to left drag
                if (wordStart.WordOffset > wordEnd.WordOffset)
                {
                    (wordStart, wordEnd) = (wordEnd, wordStart);
                }

                List<FrameworkElement> elements = wrapPanel.Children.OfType<FrameworkElement>().ToList();

                int startIndex = elements.IndexOf((FrameworkElement)wordStart.Parent);
                int endIndex = elements.IndexOf((FrameworkElement)wordEnd.Parent);

                if (startIndex == -1 || endIndex == -1)
                {
                    Debug.Assert(startIndex >= 0);
                    Debug.Assert(endIndex >= 0);
                    return;
                }

                var selectedElements = elements[startIndex..(endIndex + 1)];
                var selectedWords = selectedElements.Select(fe =>
                {
                    switch (fe)
                    {
                        case Border { Child: OutlinedTextBlock word }:
                            return word.Text;
                        case OutlinedTextBlock splitter:
                            return splitter.Text;
                        case TextBlock space:
                            return space.Text;
                        case NewLine:
                            // convert to space
                            return " ";
                        default:
                            throw new InvalidOperationException();
                    }
                }).ToList();
                string selectedText = string.Join(string.Empty, selectedWords);

                Point startPoint = wordStart.TranslatePoint(default, Root);
                Point endPoint = wordEnd.TranslatePoint(default, Root);

                // if different Y axis, then line break or text wrapping
                double wordsX = 0;
                double wordsWidth = wrapPanel.ActualWidth;

                if (Math.Abs(startPoint.Y - endPoint.Y) < 1e-6)
                {
                    // selection in one line

                    if (wrapPanel.FlowDirection == FlowDirection.LeftToRight)
                    {
                        wordsX = startPoint.X;
                        wordsWidth = endPoint.X + wordEnd.ActualWidth - startPoint.X;
                    }
                    else
                    {
                        wordsX = endPoint.X;
                        wordsWidth = startPoint.X + wordStart.ActualWidth - endPoint.X;
                    }
                }

                WordClickedEventArgs args = new(WordClickedEvent)
                {
                    Mouse = MouseClick.Left,
                    Words = selectedText,
                    IsWord = false,
                    Text = _textFix,
                    IsTranslated = IsTranslated,
                    SubIndex = SubIndex,
                    WordOffset = wordStart.WordOffset,
                    WordsX = wordsX,
                    WordsWidth = wordsWidth
                };
                RaiseEvent(args);
            }

            _wordStart = null;
            e.Handled = true;
        }
    }

    private void WordMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border { Child: OutlinedTextBlock word })
        {
            Point wordPoint = word.TranslatePoint(default, Root);

            WordClickedEventArgs args = new(WordClickedEvent)
            {
                Mouse = MouseClick.Right,
                Words = word.Text,
                IsWord = true,
                Text = _textFix,
                IsTranslated = IsTranslated,
                SubIndex = SubIndex,
                WordOffset = word.WordOffset,
                WordsX = wordPoint.X,
                WordsWidth = word.ActualWidth
            };
            RaiseEvent(args);
            e.Handled = true;
        }
    }

    private void WordMouseMiddleButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle)
        {
            WordClickedEventArgs args = new(WordClickedEvent)
            {
                Mouse = MouseClick.Middle,
                Words = _textFix,
                IsWord = false,
                Text = _textFix,
                IsTranslated = IsTranslated,
                SubIndex = SubIndex,
                WordOffset = 0,
                WordsX = 0,
                WordsWidth = wrapPanel.ActualWidth
            };
            RaiseEvent(args);
            e.Handled = true;
        }
    }
}

public class NewLine : FrameworkElement
{
    public NewLine()
    {
        Height = 0;
        var binding = new Binding
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(AlignableWrapPanel), 1),
            Path = new PropertyPath("ActualWidth")
        };
        BindingOperations.SetBinding(this, WidthProperty, binding);
    }
}
