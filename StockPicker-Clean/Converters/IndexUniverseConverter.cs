using System;
using System.Globalization;
using System.Windows.Data;
using StockPicker.Models;

namespace StockPicker.Converters
{
    /// <summary>
    /// Converts an <see cref="IndexUniverse"/> enum value to its human-readable display name
    /// for use in the Settings window ComboBox ItemTemplate.
    /// </summary>
    public class IndexUniverseConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is IndexUniverse u ? u.DisplayName() : value?.ToString() ?? string.Empty;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
