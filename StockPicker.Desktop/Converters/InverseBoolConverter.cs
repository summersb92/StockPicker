using System.Globalization;
using Avalonia.Data.Converters;

namespace StockPicker.Desktop.Converters
{
    /// <summary>
    /// Inverts a boolean: <c>true → false</c>, <c>false → true</c>.
    /// Bind to an element's <c>IsVisible</c> to show a placeholder when a
    /// condition (e.g. "has items") is false and hide it when true.
    /// </summary>
    /// <remarks>
    /// WPF-ADAPTATION: was <c>InverseBoolToVisibilityConverter</c> returning the
    /// <c>System.Windows.Visibility</c> enum (<c>true → Collapsed</c>, <c>false → Visible</c>).
    /// Avalonia has no <c>Visibility</c> enum — visibility is the boolean <c>IsVisible</c>
    /// property — so this now returns a <see cref="bool"/> and is renamed accordingly.
    /// Mapping preserved: the value that used to yield <c>Visible</c> now yields <c>true</c>.
    /// </remarks>
    public class InverseBoolConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => !(value is bool b && b);

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => !(value is bool b && b);
    }
}
