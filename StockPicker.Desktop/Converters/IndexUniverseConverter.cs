using System.Globalization;
using Avalonia.Data.Converters;
using StockPicker.Models;

namespace StockPicker.Desktop.Converters
{
    /// <summary>
    /// Converts an <see cref="IndexUniverse"/> enum value to its human-readable display name
    /// for use in the Settings window ComboBox ItemTemplate.
    /// </summary>
    /// <remarks>
    /// WPF-ADAPTATION: implements <c>Avalonia.Data.Converters.IValueConverter</c> instead of
    /// <c>System.Windows.Data.IValueConverter</c>. Parameters are now nullable
    /// (<c>object?</c>). The <see cref="IndexUniverse.DisplayName"/> helper lives in
    /// StockPicker.Core and is unchanged.
    /// </remarks>
    public class IndexUniverseConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is IndexUniverse u ? u.DisplayName() : value?.ToString() ?? string.Empty;

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
