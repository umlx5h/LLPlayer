using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace WpfColorFontDialog
{
    internal static class I18NUtil
    {
        private const string DefaultLanguage = @"en-US";

        public static string CurrentLanguage;

        public static readonly Dictionary<string, string> SupportLanguage = new Dictionary<string, string>
        {
            {@"简体中文", @"zh-CN"},
            {@"English (United States)", @"en-US"},
        };

        public static string GetCurrentLanguage()
        {
            return System.Globalization.CultureInfo.CurrentCulture.Name;
        }

        public static string GetLanguage()
        {
            var name = GetCurrentLanguage();
            return SupportLanguage.Any(s => name == s.Value) ? name : DefaultLanguage;
        }

        public static string GetWindowStringValue(Window window, string key)
        {
            if (window.Resources.MergedDictionaries[0][key] is string str)
            {
                return str;
            }
            return null;
        }

        public static void SetLanguage(ResourceDictionary resources, string langName = @"")
        {
            if (string.IsNullOrEmpty(langName))
            {
                langName = GetLanguage();
            }
            CurrentLanguage = langName;
            if (resources.MergedDictionaries.Count > 0)
            {
                resources.MergedDictionaries[0].Source = new Uri($@"I18n/{langName}.xaml", UriKind.Relative);
            }
        }
    }
}
