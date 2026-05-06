namespace StockPicker.ViewModels
{
    /// <summary>
    /// Represents a single toggleable DataGrid column.
    /// Bind <see cref="IsVisible"/> (via a BindingProxy) to the column's
    /// <c>Visibility</c> property using <c>BooleanToVisibilityConverter</c>.
    /// </summary>
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
