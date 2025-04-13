using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MaterialDesignThemes.Wpf;

namespace LLPlayer.Controls.Settings.Controls;

public partial class ColorPicker : UserControl
{
    public ColorPicker()
    {
        InitializeComponent();

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // save initial color for cancellation
        _initialColor = PickerColor;

        MyNamedColors.SelectedItem = null;
    }

    private Color? _initialColor = null;

    public Color PickerColor
    {
        get => (Color)GetValue(PickerColorProperty);
        set => SetValue(PickerColorProperty, value);
    }

    public static readonly DependencyProperty PickerColorProperty =
        DependencyProperty.Register(nameof(PickerColor), typeof(Color), typeof(ColorPicker));

    public List<KeyValuePair<string, Color>> NamedColors { get; } = GetColors();

    private static List<KeyValuePair<string, Color>> GetColors()
    {
        return typeof(Colors)
            .GetProperties()
            .Where(prop =>
                typeof(Color).IsAssignableFrom(prop.PropertyType))
            .Select(prop =>
                new KeyValuePair<string, Color>(prop.Name, (Color)prop.GetValue(null)))
            .ToList();
    }

    private void NamedColors_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MyNamedColors.SelectedItem != null)
        {
            PickerColor = ((KeyValuePair<string, Color>)MyNamedColors.SelectedItem).Value;
        }
    }

    private void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        DialogHost.CloseDialogCommand.Execute("apply", this);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_initialColor.HasValue)
        {
            PickerColor = _initialColor.Value;
        }
        DialogHost.CloseDialogCommand.Execute("cancel", this);
    }
}
