using System.Windows;

namespace StockPicker.Converters
{
    /// <summary>
    /// A <see cref="Freezable"/> proxy that forwards a DataContext value into
    /// elements that don't participate in the visual tree — most notably
    /// <see cref="System.Windows.Controls.DataGridColumn"/> children, which
    /// cannot reach the window's DataContext through normal inheritance.
    ///
    /// Usage in XAML:
    /// <code>
    ///   &lt;Window.Resources&gt;
    ///     &lt;conv:BindingProxy x:Key="VMProxy" Data="{Binding}" /&gt;
    ///   &lt;/Window.Resources&gt;
    ///
    ///   &lt;DataGridTextColumn
    ///       Visibility="{Binding Data.SomeToggle.IsVisible,
    ///                            Source={StaticResource VMProxy},
    ///                            Converter={StaticResource BoolToVisibility}}" /&gt;
    /// </code>
    /// </summary>
    public class BindingProxy : Freezable
    {
        public static readonly DependencyProperty DataProperty =
            DependencyProperty.Register(
                nameof(Data),
                typeof(object),
                typeof(BindingProxy),
                new UIPropertyMetadata(null));

        public object Data
        {
            get => GetValue(DataProperty);
            set => SetValue(DataProperty, value);
        }

        protected override Freezable CreateInstanceCore() => new BindingProxy();
    }
}
