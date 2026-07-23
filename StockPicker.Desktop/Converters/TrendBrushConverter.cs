using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using StockPicker.Models;

namespace StockPicker.Desktop.Converters
{
    /// <summary>
    /// Maps an "up/down" signal to a green / red (/ grey) foreground brush, reproducing the
    /// WPF <c>DataTrigger</c>s that recoloured change figures in the market-index bar and the
    /// portfolio performance strip.
    /// </summary>
    /// <remarks>
    /// WPF-ADAPTATION: WPF expressed this as per-<c>TextBlock</c> <c>Style.Triggers</c>
    /// (<c>IsPositive</c>/<c>IsUp</c>/<c>HasData</c> → colour). Avalonia styles cannot trigger
    /// off arbitrary data values, so the same effect is expressed as a value converter bound to
    /// the element's <c>Foreground</c>. It accepts either:
    /// <list type="bullet">
    ///   <item>a <see cref="bool"/> (e.g. <c>IsPositive</c> / <c>IsUp</c>): true → green,
    ///     false → red; or</item>
    ///   <item>a <see cref="PerformancePeriod"/> item (the trailing-window cards, which have a
    ///     third "no data" state): <c>!HasData</c> → grey, else <c>IsUp</c> → green/red.</item>
    /// </list>
    /// Colours match the WPF MainWindow originals (#388E3C / #D32F2F / #AAA).
    /// </remarks>
    public class TrendBrushConverter : IValueConverter
    {
        private static readonly IBrush Green = new ImmutableSolidColorBrush(Color.FromRgb(0x38, 0x8E, 0x3C));
        private static readonly IBrush Red   = new ImmutableSolidColorBrush(Color.FromRgb(0xD3, 0x2F, 0x2F));
        private static readonly IBrush Grey  = new ImmutableSolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA));

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            switch (value)
            {
                case PerformancePeriod p:
                    return !p.HasData ? Grey : (p.IsUp ? Green : Red);
                case bool b:
                    return b ? Green : Red;
                default:
                    return Grey;
            }
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
