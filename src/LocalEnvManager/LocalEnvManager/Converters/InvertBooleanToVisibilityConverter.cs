using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace LocalEnvManager.Converters
{
    /// <summary>
    /// Converts true to Collapsed and false to Visible. Used to show/hide elements
    /// opposite to a boolean flag (e.g. show the Cancel button when IsRunning is true).
    /// </summary>
    public sealed class InvertBooleanToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is true ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is Visibility v && v != Visibility.Visible;
        }
    }
}
