using System.Text.RegularExpressions;
using System.Windows.Controls;
using System.Windows.Input;

namespace LLPlayer.Controls.Settings;

public partial class SettingsVideo : UserControl
{
    public SettingsVideo()
    {
        InitializeComponent();
    }

    private void ValidationRatio(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !Regex.IsMatch(e.Text, @"^[0-9\.\,\/\:]+$");
    }
}
