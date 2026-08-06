using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace LocalEnvManager.Converters
{
    /// <summary>
    /// Maps a "has error" flag to the brush used for the latest-log snippet on the
    /// Execute tab. Red when true (the latest line is an error), the muted secondary
    /// gray otherwise.
    /// </summary>
    public sealed class ErrorTextBrushConverter : IValueConverter
    {
        private static readonly SolidColorBrush DangerBrush = new(Color.FromRgb(0xDC, 0x26, 0x26));
        private static readonly SolidColorBrush MutedBrush = new(Color.FromRgb(0x64, 0x74, 0x8B));

        static ErrorTextBrushConverter()
        {
            DangerBrush.Freeze();
            MutedBrush.Freeze();
        }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is true ? DangerBrush : MutedBrush;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return DependencyProperty.UnsetValue;
        }
    }
}