using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LLPlayer.Services;
using MaterialDesignThemes.Wpf;

namespace LLPlayer.Controls.Settings;

// TODO: L: separate vm from usercontrol
public partial class SettingsThemes : UserControl, INotifyPropertyChanged
{
    public FlyleafManager FL { get; }

    public SettingsThemes()
    {
        InitializeComponent();

        FL = ((App)Application.Current).Container.Resolve<FlyleafManager>();

        DataContext = this;
    }

    private ColorScheme _activeSchema;
    private Color _prevColor;

    public Color PickerColor
    {
        get;
        set
        {
            if (Set(ref field, value))
            {
                switch (_activeSchema)
                {
                    case ColorScheme.Primary:
                        FL.Config.Theme.PrimaryColor = value;
                        break;

                    case ColorScheme.Secondary:
                        FL.Config.Theme.SecondaryColor = value;
                        break;
                }
            }
        }
    }

    private void ValidationHex(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !Regex.IsMatch(e.Text, @"^[0-9a-f]+$", RegexOptions.IgnoreCase);
    }

    private void ColorPickerDialog_Primary_OnDialogOpened(object sender, DialogOpenedEventArgs eventArgs)
    {
        _activeSchema = ColorScheme.Primary;
        PickerColor = _prevColor = FL.Config.Theme.PrimaryColor;
    }

    private void ColorPickerDialog_Secondary_OnDialogOpened(object sender, DialogOpenedEventArgs eventArgs)
    {
        _activeSchema = ColorScheme.Secondary;
        PickerColor = _prevColor = FL.Config.Theme.SecondaryColor;
    }

    private void ColorPickerDialog_OnDialogClosed(object sender, DialogClosedEventArgs eventArgs)
    {
        if ((string)eventArgs.Parameter! == "cancel")
        {
            // If cancelled, return to original color.
            PickerColor = _prevColor;
        }
    }

    #region INotifyPropertyChanged
    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
    #endregion
}

public enum ColorScheme
{
    Primary,
    Secondary,
    // TODO: L: Allow manual setting of text color
    //PrimaryForeground,
    //SecondaryForeground
    // TODO: L: Allow setting of background color and video background color
}

public class ColorHexRule : ValidationRule
{
    public override ValidationResult Validate(object value, CultureInfo cultureInfo)
    {
        if (value != null && Regex.IsMatch(value.ToString(), @"^[0-9a-f]{6}$", RegexOptions.IgnoreCase))
        {
            return new ValidationResult(true, null);
        }

        return new ValidationResult(false, "Invalid");
    }
}
