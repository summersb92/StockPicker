using System.Globalization;
using Avalonia.Data.Converters;
using StockPicker.Models;

namespace StockPicker.Desktop.Converters
{
    /// <summary>
    /// Returns <c>true</c> when the bound <see cref="LayoutMode"/> matches the target mode
    /// passed as the converter parameter, otherwise <c>false</c>. Bind to <c>IsVisible</c>.
    /// </summary>
    /// <example>
    /// <code>
    /// IsVisible="{Binding LayoutMode,
    ///             Converter={StaticResource LayoutModeToBool},
    ///             ConverterParameter=Full}"
    /// </code>
    /// </example>
    /// <remarks>
    /// WPF-ADAPTATION: was <c>LayoutModeToVisibilityConverter</c> returning
    /// <c>System.Windows.Visibility</c>. Now returns <see cref="bool"/> for Avalonia's
    /// <c>IsVisible</c> (Visible → true, Collapsed → false) and is renamed accordingly.
    /// </remarks>
    public class LayoutModeToBoolConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is not LayoutMode current || parameter is null)
                return false;

            if (!Enum.TryParse<LayoutMode>(parameter.ToString(), out var target))
                return false;

            return current == target;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
