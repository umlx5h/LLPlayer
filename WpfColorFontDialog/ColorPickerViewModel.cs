using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading;
using System.Windows.Media;

namespace WpfColorFontDialog
{
	internal class ColorPickerViewModel : INotifyPropertyChanged
	{
		private ReadOnlyCollection<FontColor> roFontColors;

		private FontColor selectedFontColor;

		public ReadOnlyCollection<FontColor> FontColors
		{
			get
			{
				return this.roFontColors;
			}
		}

		public FontColor SelectedFontColor
		{
			get
			{
				return this.selectedFontColor;
			}
			set
			{
				if (this.selectedFontColor == value)
				{
					return;
				}
				this.selectedFontColor = value;
				this.OnPropertyChanged("SelectedFontColor");
			}
		}

		public ColorPickerViewModel()
		{
			this.selectedFontColor = AvailableColors.GetFontColor(Colors.Black);
			this.roFontColors = new ReadOnlyCollection<FontColor>(new AvailableColors());
		}

		private void OnPropertyChanged(string propertyName)
		{
			if (this.PropertyChanged != null)
			{
				this.PropertyChanged(this, new PropertyChangedEventArgs(propertyName));
			}
		}

		public event PropertyChangedEventHandler PropertyChanged;
	}
}