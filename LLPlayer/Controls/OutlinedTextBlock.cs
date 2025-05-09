using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;

namespace LLPlayer.Controls;

[ContentProperty("Text")]
public class OutlinedTextBlock : FrameworkElement
{
    public OutlinedTextBlock()
    {
        UpdatePen();
        TextDecorations = new TextDecorationCollection();
    }

    #region dependency properties
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
            nameof(Fill),
            typeof(Brush),
            typeof(OutlinedTextBlock),
            new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
            nameof(Stroke),
            typeof(Brush),
            typeof(OutlinedTextBlock),
            new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
            nameof(StrokeThickness),
            typeof(double),
            typeof(OutlinedTextBlock),
            new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontFamilyProperty = TextElement.FontFamilyProperty.AddOwner(
            typeof(OutlinedTextBlock),
            new FrameworkPropertyMetadata(OnFormattedTextUpdated));

    public static readonly DependencyProperty FontSizeProperty = TextElement.FontSizeProperty.AddOwner(
            typeof(OutlinedTextBlock),
            new FrameworkPropertyMetadata(OnFormattedTextUpdated));

    public static readonly DependencyProperty FontSizeInitialProperty = DependencyProperty.Register(
            nameof(FontSizeInitial),
            typeof(double),
            typeof(OutlinedTextBlock),
            new FrameworkPropertyMetadata(OnFormattedTextUpdated));

    public static readonly DependencyProperty FontSizeAutoProperty = DependencyProperty.Register(
        nameof(FontSizeAuto),
        typeof(bool),
        typeof(OutlinedTextBlock),
        new FrameworkPropertyMetadata(OnFormattedTextUpdated));

    public static readonly DependencyProperty FontStretchProperty = TextElement.FontStretchProperty.AddOwner(
            typeof(OutlinedTextBlock),
            new FrameworkPropertyMetadata(OnFormattedTextUpdated));

    public static readonly DependencyProperty FontStyleProperty = TextElement.FontStyleProperty.AddOwner(
            typeof(OutlinedTextBlock),
            new FrameworkPropertyMetadata(OnFormattedTextUpdated));

    public static readonly DependencyProperty FontWeightProperty = TextElement.FontWeightProperty.AddOwner(
            typeof(OutlinedTextBlock),
            new FrameworkPropertyMetadata(OnFormattedTextUpdated));

    public static readonly DependencyProperty StrokePositionProperty = DependencyProperty.Register(
            nameof(StrokePosition),
            typeof(StrokePosition),
            typeof(OutlinedTextBlock),
            new FrameworkPropertyMetadata(StrokePosition.Outside, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessInitialProperty = DependencyProperty.Register(
            nameof(StrokeThicknessInitial),
            typeof(double),
            typeof(OutlinedTextBlock),
            new FrameworkPropertyMetadata(OnFormattedTextUpdated));

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
            nameof(Text),
            typeof(string),
            typeof(OutlinedTextBlock),
            new FrameworkPropertyMetadata(OnFormattedTextInvalidated));

    public static readonly DependencyProperty TextAlignmentProperty = DependencyProperty.Register(
            nameof(TextAlignment),
            typeof(TextAlignment),
            typeof(OutlinedTextBlock),
            new FrameworkPropertyMetadata(OnFormattedTextUpdated));

    public static readonly DependencyProperty TextDecorationsProperty = DependencyProperty.Register(
            nameof(TextDecorations),
            typeof(TextDecorationCollection),
            typeof(OutlinedTextBlock),
            new FrameworkPropertyMetadata(OnFormattedTextUpdated));

    public static readonly DependencyProperty TextTrimmingProperty = DependencyProperty.Register(
            nameof(TextTrimming),
            typeof(TextTrimming),
            typeof(OutlinedTextBlock),
            new FrameworkPropertyMetadata(OnFormattedTextUpdated));

    public static readonly DependencyProperty TextWrappingProperty = DependencyProperty.Register(
            nameof(TextWrapping),
            typeof(TextWrapping),
            typeof(OutlinedTextBlock),
            new FrameworkPropertyMetadata(TextWrapping.NoWrap, OnFormattedTextUpdated));

    private FormattedText? _FormattedText;
    private Geometry? _TextGeometry;
    private Pen? _Pen;
    private PathGeometry? _clipGeometry;

    public Brush Fill
    {
        get => (Brush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public FontFamily FontFamily
    {
        get => (FontFamily)GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    [TypeConverter(typeof(FontSizeConverter))]
    public double FontSize
    {
        get => (double)GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    [TypeConverter(typeof(FontSizeConverter))]
    public double FontSizeInitial
    {
        get => (double)GetValue(FontSizeInitialProperty);
        set => SetValue(FontSizeInitialProperty, value);
    }

    public bool FontSizeAuto
    {
        get => (bool)GetValue(FontSizeAutoProperty);
        set => SetValue(FontSizeAutoProperty, value);
    }

    public FontStretch FontStretch
    {
        get => (FontStretch)GetValue(FontStretchProperty);
        set => SetValue(FontStretchProperty, value);
    }

    public FontStyle FontStyle
    {
        get => (FontStyle)GetValue(FontStyleProperty);
        set => SetValue(FontStyleProperty, value);
    }

    public FontWeight FontWeight
    {
        get => (FontWeight)GetValue(FontWeightProperty);
        set => SetValue(FontWeightProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public StrokePosition StrokePosition
    {
        get => (StrokePosition)GetValue(StrokePositionProperty);
        set => SetValue(StrokePositionProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public double StrokeThicknessInitial
    {
        get => (double)GetValue(StrokeThicknessInitialProperty);
        set => SetValue(StrokeThicknessInitialProperty, value);
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public TextAlignment TextAlignment
    {
        get => (TextAlignment)GetValue(TextAlignmentProperty);
        set => SetValue(TextAlignmentProperty, value);
    }

    public TextDecorationCollection TextDecorations
    {
        get => (TextDecorationCollection)GetValue(TextDecorationsProperty);
        set => SetValue(TextDecorationsProperty, value);
    }

    public TextTrimming TextTrimming
    {
        get => (TextTrimming)GetValue(TextTrimmingProperty);
        set => SetValue(TextTrimmingProperty, value);
    }

    public TextWrapping TextWrapping
    {
        get => (TextWrapping)GetValue(TextWrappingProperty);
        set => SetValue(TextWrappingProperty, value);
    }

    #endregion

    #region PDIC

    public int WordOffset { get; set; }

    #endregion

    private void UpdatePen()
    {
        _Pen = new Pen(Stroke, StrokeThickness)
        {
            DashCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round
        };

        if (StrokePosition == StrokePosition.Outside || StrokePosition == StrokePosition.Inside)
            _Pen.Thickness = StrokeThickness * 2;

        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        EnsureGeometry();

        drawingContext.DrawGeometry(Fill, null, _TextGeometry);

        if (StrokePosition == StrokePosition.Outside)
            drawingContext.PushClip(_clipGeometry);
        else if (StrokePosition == StrokePosition.Inside)
            drawingContext.PushClip(_TextGeometry);

        drawingContext.DrawGeometry(null, _Pen, _TextGeometry);

        if (StrokePosition == StrokePosition.Outside || StrokePosition == StrokePosition.Inside)
            drawingContext.Pop();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        EnsureFormattedText();

        // constrain the formatted text according to the available size
        double w = availableSize.Width;
        double h = availableSize.Height;

        if (FontSizeAuto)
        {
            if (FontSizeInitial > 0)
            {
                double r = w / 1920; // FontSizeInitial should be based on fixed Screen Width (eg. Full HD 1920)
                FontSize = FontSizeInitial * (r + ((1 - r) * 0.20f)); // TBR: Weight/Percentage for how much it will be affected by the change (possible dependency property / config)
                _FormattedText!.SetFontSize(FontSize);
            }
        }

        if (StrokeThicknessInitial > 0)
        {
            double r = FontSize / 48; // StrokeThicknessInitial should be based on fixed FontSize (eg. 48)
            StrokeThickness = Math.Max(1, StrokeThicknessInitial * r);
            UpdatePen();
        }

        // the Math.Min call is important - without this constraint (which seems arbitrary, but is the maximum allowable text width), things blow up when availableSize is infinite in both directions
        // the Math.Max call is to ensure we don't hit zero, which will cause MaxTextHeight to throw
        _FormattedText!.MaxTextWidth = Math.Min(3579139, w);
        _FormattedText!.MaxTextHeight = Math.Max(0.0001d, h);

        // return the desired size
        return new Size(Math.Ceiling(_FormattedText.Width), Math.Ceiling(_FormattedText.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        EnsureFormattedText();

        // update the formatted text with the final size
        _FormattedText!.MaxTextWidth = finalSize.Width;
        _FormattedText!.MaxTextHeight = Math.Max(0.0001d, finalSize.Height);

        // need to re-generate the geometry now that the dimensions have changed
        _TextGeometry = null;
        UpdatePen();

        return finalSize;
    }

    private static void OnFormattedTextInvalidated(DependencyObject dependencyObject,
      DependencyPropertyChangedEventArgs e)
    {
        var outlinedTextBlock = (OutlinedTextBlock)dependencyObject;
        outlinedTextBlock._FormattedText = null;
        outlinedTextBlock._TextGeometry = null;

        outlinedTextBlock.InvalidateMeasure();
        outlinedTextBlock.InvalidateVisual();
    }

    private static void OnFormattedTextUpdated(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        var outlinedTextBlock = (OutlinedTextBlock)dependencyObject;
        if (outlinedTextBlock._FormattedText != null)
            outlinedTextBlock.UpdateFormattedText();
        outlinedTextBlock._TextGeometry = null;

        outlinedTextBlock.InvalidateMeasure();
        outlinedTextBlock.InvalidateVisual();
    }

    private void EnsureFormattedText()
    {
        if (_FormattedText != null)
            return;

        _FormattedText = new FormattedText(
          Text ?? "",
          CultureInfo.CurrentUICulture,
          FlowDirection,
          new Typeface(FontFamily, FontStyle, FontWeight, FontStretch),
          FontSize,
          Brushes.Black,
          VisualTreeHelper.GetDpi(this).PixelsPerDip);

        UpdateFormattedText();
    }

    private void UpdateFormattedText()
    {
        _FormattedText!.MaxLineCount = TextWrapping == TextWrapping.NoWrap ? 1 : int.MaxValue;
        _FormattedText.TextAlignment = TextAlignment;
        _FormattedText.Trimming = TextTrimming;
        _FormattedText.SetFontSize(FontSize);
        _FormattedText.SetFontStyle(FontStyle);
        _FormattedText.SetFontWeight(FontWeight);
        _FormattedText.SetFontFamily(FontFamily);
        _FormattedText.SetFontStretch(FontStretch);
        _FormattedText.SetTextDecorations(TextDecorations);
    }

    private void EnsureGeometry()
    {
        if (_TextGeometry != null)
            return;

        EnsureFormattedText();
        _TextGeometry = _FormattedText!.BuildGeometry(new Point(0, 0));

        if (StrokePosition == StrokePosition.Outside)
        {
            var boundsGeo = new RectangleGeometry(new Rect(-(2 * StrokeThickness),
                    -(2 * StrokeThickness), ActualWidth + (4 * StrokeThickness), ActualHeight + (4 * StrokeThickness)));
            _clipGeometry = Geometry.Combine(boundsGeo, _TextGeometry, GeometryCombineMode.Exclude, null);
        }
    }
}

public enum StrokePosition
{
    Center,
    Outside,
    Inside
}
