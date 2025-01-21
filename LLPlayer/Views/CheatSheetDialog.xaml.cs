using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using LLPlayer.ViewModels;

namespace LLPlayer.Views;

public partial class CheatSheetDialog : UserControl
{
    public CheatSheetDialog()
    {
        InitializeComponent();

        DataContext = ((App)Application.Current).Container.Resolve<CheatSheetDialogVM>();
    }
}

[ValueConversion(typeof(int), typeof(Visibility))]
public class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (int.TryParse(value.ToString(), out var count))
        {
            return count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        return Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
