using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace WpfColorFontDialog
{
	public class FontColor
	{
		public SolidColorBrush Brush
		{
			get;
			set;
		}

		public string Name
		{
			get;
			set;
		}

		public FontColor(string name, SolidColorBrush brush)
		{
			this.Name = name;
			this.Brush = brush;
		}

		public override bool Equals(object obj)
		{
			if (obj == null)
			{
				return false;
			}
			FontColor p = obj as FontColor;
			if (p == null)
			{
				return false;
			}
			if (this.Name != p.Name)
			{
				return false;
			}
			return this.Brush.Equals(p.Brush);
		}

		public bool Equals(FontColor p)
		{
			if (p == null)
			{
				return false;
			}
			if (this.Name != p.Name)
			{
				return false;
			}
			return this.Brush.Equals(p.Brush);
		}

		public override int GetHashCode()
		{
			return base.GetHashCode();
		}

		public override string ToString()
		{
			string[] name = new string[] { "FontColor [Color=", this.Name, ", ", this.Brush.ToString(), "]" };
			return string.Concat(name);
		}
	}
}