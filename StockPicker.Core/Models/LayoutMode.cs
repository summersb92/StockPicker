namespace StockPicker.Models
{
    /// <summary>
    /// Describes the current UI layout mode. Driven by window width so the shell
    /// can adapt to being snapped to half a screen vs. maximized.
    /// </summary>
    public enum LayoutMode
    {
        /// <summary>
        /// Narrow layout (window snapped to half a monitor, tablet, etc.).
        /// Recommendations list and Details panel stack vertically.
        /// </summary>
        Compact,

        /// <summary>
        /// Wide layout (maximized on a normal monitor).
        /// Recommendations list and Details panel sit side-by-side.
        /// </summary>
        Full
    }
}
