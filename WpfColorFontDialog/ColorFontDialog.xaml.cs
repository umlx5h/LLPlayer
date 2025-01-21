using System.Collections;
using System.Windows;
using System.Windows.Media;

namespace WpfColorFontDialog
{
    /// <summary>
    /// Interaction logic for ColorFontDialog.xaml
    /// </summary>
    public partial class ColorFontDialog : Window
    {
        private FontInfo _selectedFont;

        public FontInfo Font
        {
            get
            {
                return _selectedFont;
            }
            set
            {
                _selectedFont = value;
            }
        }

        private int[] _defaultFontSizes = { 8, 9, 10, 11, 12, 14, 16, 18, 20, 22, 24, 26, 28, 36, 48, 72, 96 };
        private int[] _fontSizes = null;
        public int[] FontSizes
        {
            get
            {
                return _fontSizes ?? _defaultFontSizes;
            }
            set
            {
                _fontSizes = value;
            }
        }
        public ColorFontDialog(bool previewFontInFontList = true, bool allowArbitraryFontSizes = true, bool showColorPicker = true)
        {
            InitializeComponent();
            I18NUtil.SetLanguage(Resources);
            this.colorFontChooser.PreviewFontInFontList = previewFontInFontList;
            this.colorFontChooser.AllowArbitraryFontSizes = allowArbitraryFontSizes;
            this.colorFontChooser.ShowColorPicker = showColorPicker;
        }

        private void btnOk_Click(object sender, RoutedEventArgs e)
        {
            this.Font = this.colorFontChooser.SelectedFont;
            base.DialogResult = new bool?(true);
        }

        private void SyncFontColor()
        {
            int colorIdx = AvailableColors.GetFontColorIndex(this.Font.Color);
            this.colorFontChooser.colorPicker.superCombo.SelectedIndex = colorIdx;
            this.colorFontChooser.txtSampleText.Foreground = this.Font.Color.Brush;
            this.colorFontChooser.colorPicker.superCombo.BringIntoView();
        }

        private void SyncFontName()
        {
            string fontFamilyName = this._selectedFont.Family.Source;
            bool foundMatch = false;
            int idx = 0;
            foreach (object item in (IEnumerable)this.colorFontChooser.lstFamily.Items)
            {
                if (fontFamilyName == item.ToString())
                {
                    foundMatch = true;
                    break;
                }
                idx++;
            }
            if (!foundMatch)
            {
                idx = 0;
            }
            this.colorFontChooser.lstFamily.SelectedIndex = idx;
            this.colorFontChooser.lstFamily.ScrollIntoView(this.colorFontChooser.lstFamily.Items[idx]);
        }

        private void SyncFontSize()
        {
            double fontSize = this._selectedFont.Size;
            this.colorFontChooser.lstFontSizes.ItemsSource = FontSizes;
            this.colorFontChooser.tbFontSize.Text = fontSize.ToString();
        }

        private void SyncFontTypeface()
        {
            string fontTypeFaceSb = FontInfo.TypefaceToString(this._selectedFont.Typeface);
            int idx = 0;
            foreach (object item in (IEnumerable)this.colorFontChooser.lstTypefaces.Items)
            {
                if (fontTypeFaceSb == FontInfo.TypefaceToString(item as FamilyTypeface))
                {
                    break;
                }
                idx++;
            }
            this.colorFontChooser.lstTypefaces.SelectedIndex = idx;
            this.colorFontChooser.lstTypefaces.ScrollIntoView(this.colorFontChooser.lstTypefaces.SelectedItem);
        }

        private void Window_Loaded_1(object sender, RoutedEventArgs e)
        {
            this.SyncFontColor();
            this.SyncFontName();
            this.SyncFontSize();
            this.SyncFontTypeface();
        }
    }
}
