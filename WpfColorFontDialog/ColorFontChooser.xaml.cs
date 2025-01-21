using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace WpfColorFontDialog
{
    /// <summary>
    /// Interaction logic for ColorFontChooser.xaml
    /// </summary>
    public partial class ColorFontChooser : UserControl
    {
        public FontInfo SelectedFont
        {
            get
            {
                return new FontInfo(this.txtSampleText.FontFamily, this.txtSampleText.FontSize, this.txtSampleText.FontStyle, this.txtSampleText.FontStretch, this.txtSampleText.FontWeight, this.colorPicker.SelectedColor.Brush);
            }
        }



        public bool ShowColorPicker
        {
            get { return (bool)GetValue(ShowColorPickerProperty); }
            set { SetValue(ShowColorPickerProperty, value); }
        }

        // Using a DependencyProperty as the backing store for ShowColorPicker.  This enables animation, styling, binding, etc...
        public static readonly DependencyProperty ShowColorPickerProperty =
            DependencyProperty.Register("ShowColorPicker", typeof(bool), typeof(ColorFontChooser), new PropertyMetadata(true, ShowColorPickerPropertyCallback));


        public bool AllowArbitraryFontSizes
        {
            get { return (bool)GetValue(AllowArbitraryFontSizesProperty); }
            set { SetValue(AllowArbitraryFontSizesProperty, value); }
        }

        // Using a DependencyProperty as the backing store for AllowArbitraryFontSizes.  This enables animation, styling, binding, etc...
        public static readonly DependencyProperty AllowArbitraryFontSizesProperty =
            DependencyProperty.Register("AllowArbitraryFontSizes", typeof(bool), typeof(ColorFontChooser), new PropertyMetadata(true, AllowArbitraryFontSizesPropertyCallback));


        public bool PreviewFontInFontList
        {
            get { return (bool)GetValue(PreviewFontInFontListProperty); }
            set { SetValue(PreviewFontInFontListProperty, value); }
        }

        // Using a DependencyProperty as the backing store for PreviewFontInFontList.  This enables animation, styling, binding, etc...
        public static readonly DependencyProperty PreviewFontInFontListProperty =
            DependencyProperty.Register("PreviewFontInFontList", typeof(bool), typeof(ColorFontChooser), new PropertyMetadata(true, PreviewFontInFontListPropertyCallback));


        public ColorFontChooser()
        {
            InitializeComponent();
            this.groupBoxColorPicker.Visibility = ShowColorPicker ? Visibility.Visible : Visibility.Collapsed;
            this.tbFontSize.IsEnabled = AllowArbitraryFontSizes;
            lstFamily.ItemTemplate = PreviewFontInFontList ? (DataTemplate)Resources["fontFamilyData"] : (DataTemplate)Resources["fontFamilyDataWithoutPreview"];
        }
        private static void PreviewFontInFontListPropertyCallback(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ColorFontChooser chooser = d as ColorFontChooser;
            if (e.NewValue == null)
                return;
            if ((bool)e.NewValue == true)
                chooser.lstFamily.ItemTemplate = chooser.Resources["fontFamilyData"] as DataTemplate;
            else
                chooser.lstFamily.ItemTemplate = chooser.Resources["fontFamilyDataWithoutPreview"] as DataTemplate;
        }
        private static void AllowArbitraryFontSizesPropertyCallback(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ColorFontChooser chooser = d as ColorFontChooser;
            if (e.NewValue == null)
                return;

            chooser.tbFontSize.IsEnabled = (bool)e.NewValue;

        }
        private static void ShowColorPickerPropertyCallback(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ColorFontChooser chooser = d as ColorFontChooser;
            if (e.NewValue == null)
                return;
            if ((bool)e.NewValue == true)
                chooser.groupBoxColorPicker.Visibility = Visibility.Visible;
            else
                chooser.groupBoxColorPicker.Visibility = Visibility.Collapsed;
        }

        private void colorPicker_ColorChanged(object sender, RoutedEventArgs e)
        {
            this.txtSampleText.Foreground = this.colorPicker.SelectedColor.Brush;
        }

        private void lstFontSizes_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (this.tbFontSize != null && this.lstFontSizes.SelectedItem != null)
            {
                this.tbFontSize.Text = this.lstFontSizes.SelectedItem.ToString();
            }
        }

        private void tbFontSize_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            e.Handled = !TextBoxTextAllowed(e.Text);
        }

        private void tbFontSize_Pasting(object sender, DataObjectPastingEventArgs e)
        {
            if (e.DataObject.GetDataPresent(typeof(String)))
            {
                String Text1 = (String)e.DataObject.GetData(typeof(String));
                if (!TextBoxTextAllowed(Text1)) e.CancelCommand();
            }
            else
            {
                e.CancelCommand();
            }
        }
        private Boolean TextBoxTextAllowed(String Text2)
        {
            return Array.TrueForAll<Char>(Text2.ToCharArray(),
                delegate (Char c) { return Char.IsDigit(c) || Char.IsControl(c); });
        }

        private void tbFontSize_LostFocus(object sender, RoutedEventArgs e)
        {
            if (tbFontSize.Text.Length == 0)
            {
                if (this.lstFontSizes.SelectedItem == null)
                {
                    lstFontSizes.SelectedIndex = 0;
                }
                tbFontSize.Text = this.lstFontSizes.SelectedItem.ToString();

            }
        }

        private void tbFontSize_TextChanged(object sender, TextChangedEventArgs e)
        {
            foreach (int size in this.lstFontSizes.Items)
            {
                if (size.ToString() == tbFontSize.Text)
                {
                    lstFontSizes.SelectedItem = size;
                    lstFontSizes.ScrollIntoView(size);
                    return;
                }
            }
            this.lstFontSizes.SelectedItem = null;
        }
    }
}
