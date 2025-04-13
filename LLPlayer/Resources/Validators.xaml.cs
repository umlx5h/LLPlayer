using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;

namespace LLPlayer.Resources;

public partial class Validators : ResourceDictionary
{
    public Validators()
    {
        InitializeComponent();
    }
}

public class ColorHexRule : ValidationRule
{
    public override ValidationResult Validate(object value, CultureInfo cultureInfo)
    {
        if (value != null && Regex.IsMatch(value.ToString(), "^[0-9a-f]{6}$", RegexOptions.IgnoreCase))
        {
            return new ValidationResult(true, null);
        }

        return new ValidationResult(false, "Invalid");
    }
}
