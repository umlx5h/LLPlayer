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
    /// Interaction logic for ColorPicker.xaml
    /// </summary>
    public partial class ColorPicker : UserControl
    {
        private ColorPickerViewModel viewModel;

        public readonly static RoutedEvent ColorChangedEvent;

        public readonly static DependencyProperty SelectedColorProperty;

        public FontColor SelectedColor
        {
            get
            {
                FontColor fc = (FontColor)base.GetValue(ColorPicker.SelectedColorProperty) ?? AvailableColors.GetFontColor("Black");
                return fc;
            }
            set
            {
                this.viewModel.SelectedFontColor = value;
                base.SetValue(ColorPicker.SelectedColorProperty, value);
            }
        }

        static ColorPicker()
        {
            ColorPicker.ColorChangedEvent = EventManager.RegisterRoutedEvent("ColorChanged", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(ColorPicker));
            ColorPicker.SelectedColorProperty = DependencyProperty.Register("SelectedColor", typeof(FontColor), typeof(ColorPicker), new UIPropertyMetadata(null));
        }
        public ColorPicker()
        {
            InitializeComponent();
            this.viewModel = new ColorPickerViewModel();
            base.DataContext = this.viewModel;
        }
        private void RaiseColorChangedEvent()
        {
            base.RaiseEvent(new RoutedEventArgs(ColorPicker.ColorChangedEvent));
        }

        private void superCombo_DropDownClosed(object sender, EventArgs e)
        {
            base.SetValue(ColorPicker.SelectedColorProperty, this.viewModel.SelectedFontColor);
            this.RaiseColorChangedEvent();
        }

        private void superCombo_Loaded(object sender, RoutedEventArgs e)
        {
            base.SetValue(ColorPicker.SelectedColorProperty, this.viewModel.SelectedFontColor);
        }

        public event RoutedEventHandler ColorChanged
        {
            add
            {
                base.AddHandler(ColorPicker.ColorChangedEvent, value);
            }
            remove
            {
                base.RemoveHandler(ColorPicker.ColorChangedEvent, value);
            }
        }
    }
}
