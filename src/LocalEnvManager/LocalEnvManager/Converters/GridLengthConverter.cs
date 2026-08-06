using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace LocalEnvManager.Converters
{
    /// <summary>
    /// Converts a string like "5*", "*", "12" or "Auto" into a <see cref="GridLength"/>.
    /// Used so the Execute tab's two-column widths can be driven from view-model
    /// properties (ColumnDefinition.Width is a plain GridLength struct, not a
    /// DependencyProperty, so a string → GridLength conversion is the cleanest way
    /// to bind it).
    /// </summary>
    public sealed class GridLengthConverter : IValueConverter
    {
        // System.Windows.GridLengthConverter is sealed; reuse its parsing logic
        // instead of rolling our own (it handles "5*", "*", "12", "Auto", etc.).
        private static readonly System.Windows.GridLengthConverter Parser = new();

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is string s && !string.IsNullOrWhiteSpace(s))
            {
                var result = Parser.ConvertFromString(s);
                if (result is GridLength length) return length;
            }
            return GridLength.Auto;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => Binding.DoNothing;
    }
}