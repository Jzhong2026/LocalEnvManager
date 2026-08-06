using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace LocalEnvManager.Converters
{
    /// <summary>
    /// Inverts a boolean binding. Used so a checkbox / button can be disabled while
    /// the inverse flag (e.g. IsRunning, IsRefreshingBranches) is true.
    /// </summary>
    public sealed class NotConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is bool b ? !b : true;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is bool b ? !b : DependencyProperty.UnsetValue;
        }
    }
}