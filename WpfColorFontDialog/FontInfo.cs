using System;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WpfColorFontDialog
{
	public class FontInfo
	{
		public SolidColorBrush BrushColor
		{
			get;
			set;
		}

		public FontColor Color
		{
			get
			{
				return AvailableColors.GetFontColor(this.BrushColor);
			}
		}

		public FontFamily Family
		{
			get;
			set;
		}

		public double Size
		{
			get;
			set;
		}

		public FontStretch Stretch
		{
			get;
			set;
		}

		public FontStyle Style
		{
			get;
			set;
		}

		public FamilyTypeface Typeface
		{
			get
			{
				FamilyTypeface ftf = new FamilyTypeface()
				{
					Stretch = this.Stretch,
					Weight = this.Weight,
					Style = this.Style
				};
				return ftf;
			}
		}

		public FontWeight Weight
		{
			get;
			set;
		}

		public FontInfo()
		{
		}

		public FontInfo(FontFamily fam, double sz, FontStyle style, FontStretch strc, FontWeight weight, SolidColorBrush c)
		{
			this.Family = fam;
			this.Size = sz;
			this.Style = style;
			this.Stretch = strc;
			this.Weight = weight;
			this.BrushColor = c;
		}

		public static void ApplyFont(Control control, FontInfo font)
		{
			control.FontFamily = font.Family;
			control.FontSize = font.Size;
			control.FontStyle = font.Style;
			control.FontStretch = font.Stretch;
			control.FontWeight = font.Weight;
			control.Foreground = font.BrushColor;
		}

		public static FontInfo GetControlFont(Control control)
		{
			FontInfo font = new FontInfo()
			{
				Family = control.FontFamily,
				Size = control.FontSize,
				Style = control.FontStyle,
				Stretch = control.FontStretch,
				Weight = control.FontWeight,
				BrushColor = (SolidColorBrush)control.Foreground
			};
			return font;
		}

		public static string TypefaceToString(FamilyTypeface ttf)
		{
			StringBuilder sb = new StringBuilder(ttf.Stretch.ToString());
			sb.Append("-");
			sb.Append(ttf.Weight.ToString());
			sb.Append("-");
			sb.Append(ttf.Style.ToString());
			return sb.ToString();
		}
	}
}