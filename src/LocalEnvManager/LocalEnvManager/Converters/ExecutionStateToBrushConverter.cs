using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using LocalEnvManager.Models;

namespace LocalEnvManager.Converters
{
	// Maps an ExecutionState to a status brush (gray / blue / green / red).
	public sealed class ExecutionStateToBrushConverter : IValueConverter
	{
		public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
		{
			var brush = value switch
			{
				ExecutionState.Running => (Brush)Application.Current.FindResource("PrimaryBrush"),
				ExecutionState.Success => (Brush)Application.Current.FindResource("SuccessBrush"),
				ExecutionState.Failed => (Brush)Application.Current.FindResource("DangerBrush"),
				_ => (Brush)Application.Current.FindResource("TextTertiaryBrush")
			};

			return brush;
		}

		public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
		{
			throw new NotSupportedException();
		}
	}
}
