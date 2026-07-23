using Avalonia;

namespace StockPicker.Desktop.Converters
{
    /// <summary>
    /// A proxy object that forwards a DataContext value into elements that don't
    /// participate in the visual tree — most notably <c>DataGridColumn</c> children,
    /// which cannot reach the window's DataContext through normal inheritance.
    /// </summary>
    /// <remarks>
    /// WPF-ADAPTATION: the WPF version derived from <c>System.Windows.Freezable</c>
    /// (which Avalonia does not have). This version is a plain <see cref="AvaloniaObject"/>
    /// with a <see cref="StyledProperty{T}"/>, which is the idiomatic Avalonia replacement.
    ///
    /// NOTE: not consumed anywhere this phase (the DataGrid-bearing MainWindow is a later
    /// phase). It is ported now so the pattern is ready. In many cases Avalonia's compiled
    /// bindings can reach the DataContext directly (<c>$parent</c>, <c>#ElementName</c>)
    /// without a proxy — revisit whether it is still needed when MainWindow is ported.
    /// </remarks>
    public class BindingProxy : AvaloniaObject
    {
        public static readonly StyledProperty<object?> DataProperty =
            AvaloniaProperty.Register<BindingProxy, object?>(nameof(Data));

        public object? Data
        {
            get => GetValue(DataProperty);
            set => SetValue(DataProperty, value);
        }
    }
}
