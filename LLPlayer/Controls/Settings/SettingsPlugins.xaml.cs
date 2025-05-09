using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using LLPlayer.ViewModels;

namespace LLPlayer.Controls.Settings;

public partial class SettingsPlugins : UserControl
{
    public SettingsPlugins()
    {
        InitializeComponent();
    }

    private void PluginValueChanged(object sender, RoutedEventArgs e)
    {
        string curPlugin = ((TextBlock)((Panel)((FrameworkElement)sender).Parent).Children[0]).Text;

        if (DataContext is SettingsDialogVM vm)
        {
            vm.FL.PlayerConfig.Plugins[cmbPlugins.Text][curPlugin] = ((TextBox)sender).Text;
        }
    }
}

public class GetDictionaryItemConverter : IMultiValueConverter
{
    public object? Convert(object[]? value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value == null)
            return null;
        if (value[0] is not IDictionary dictionary)
            return null;
        if (value[1] is not string key)
            return null;

        return dictionary[key];
    }
    public object[] ConvertBack(object value, Type[] targetType, object parameter, CultureInfo culture) { throw new NotImplementedException(); }
}
