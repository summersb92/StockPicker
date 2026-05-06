using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using StockPicker.Models;

namespace StockPicker.Converters
{
    /// <summary>
    /// Converts a <see cref="LayoutMode"/> value to <see cref="Visibility"/>.
    /// Pass the target LayoutMode as the ConverterParameter — the element is
    /// Visible when the current mode matches, Collapsed otherwise.
    /// </summary>
    /// <example>
    /// <code>
    /// Visibility="{Binding LayoutMode,
    ///              Converter={StaticResource LayoutModeToVisibility},
    ///              ConverterParameter=Full}"
    /// </code>
    /// </example>
    public class LayoutModeToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not LayoutMode current || parameter is null)
                return Visibility.Collapsed;

            if (!Enum.TryParse<LayoutMode>(parameter.ToString(), out var target))
                return Visibility.Collapsed;

            return current == target ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
