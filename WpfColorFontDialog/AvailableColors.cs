using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows.Media;

namespace WpfColorFontDialog
{
	internal class AvailableColors : List<FontColor>
	{
		public AvailableColors()
		{
			this.Init();
		}

		public static FontColor GetFontColor(SolidColorBrush b)
		{
			return (new AvailableColors()).GetFontColorByBrush(b);
		}

		public static FontColor GetFontColor(string name)
		{
			return (new AvailableColors()).GetFontColorByName(name);
		}

		public static FontColor GetFontColor(Color c)
		{
			return AvailableColors.GetFontColor(new SolidColorBrush(c));
		}

		public FontColor GetFontColorByBrush(SolidColorBrush b)
		{
			FontColor found = null;
			foreach (FontColor brush in this)
			{
				if (!brush.Brush.Color.Equals(b.Color))
				{
					continue;
				}
				found = brush;
				break;
			}
			return found;
		}

		public FontColor GetFontColorByName(string name)
		{
			FontColor found = null;
			foreach (FontColor b in this)
			{
				if (b.Name != name)
				{
					continue;
				}
				found = b;
				break;
			}
			return found;
		}

		public static int GetFontColorIndex(FontColor c)
		{
			AvailableColors brushList = new AvailableColors();
			int idx = 0;
			SolidColorBrush colorBrush = c.Brush;
			foreach (FontColor brush in brushList)
			{
				if (brush.Brush.Color.Equals(colorBrush.Color))
				{
					break;
				}
				idx++;
			}
			return idx;
		}

		private void Init()
		{
			PropertyInfo[] properties = typeof(Colors).GetProperties(BindingFlags.Static | BindingFlags.Public);
			for (int i = 0; i < (int)properties.Length; i++)
			{
				PropertyInfo prop = properties[i];
				string name = prop.Name;
				SolidColorBrush brush = new SolidColorBrush((Color)prop.GetValue(null, null));
				base.Add(new FontColor(name, brush));
			}
		}
	}
}