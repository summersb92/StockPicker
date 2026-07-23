namespace StockPicker.Desktop.ViewModels
{
    /// <summary>
    /// Represents a single toggleable DataGrid column (used by the Settings "Columns" tab).
    /// </summary>
    /// <remarks>
    /// WPF-ADAPTATION: copied verbatim from <c>StockPicker/ViewModels/ColumnToggle.cs</c>; only
    /// the namespace changed (<c>StockPicker.ViewModels</c> → <c>StockPicker.Desktop.ViewModels</c>).
    /// The WPF original bound <see cref="IsVisible"/> to a column's <c>Visibility</c> via a
    /// BindingProxy + BooleanToVisibilityConverter; in Avalonia the column visibility is a plain
    /// bool, so the same <see cref="IsVisible"/> bool binds directly (no converter/proxy).
    /// </remarks>
    public class ColumnToggle : ViewModelBase
    {
        public ColumnToggle(string header, bool isVisible = true)
        {
            Header     = header;
            _isVisible = isVisible;
        }

        /// <summary>Column header text — shown in the column picker UI.</summary>
        public string Header { get; }

        private bool _isVisible;
        public bool IsVisible
        {
            get => _isVisible;
            set => SetProperty(ref _isVisible, value);
        }
    }
}
