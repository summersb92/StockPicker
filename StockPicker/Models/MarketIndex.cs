namespace StockPicker.Models
{
    /// <summary>
    /// Represents a live snapshot of a broad market index (e.g. DOW, S&amp;P 500, NASDAQ).
    /// Bound to the market index bar at the top of the main window.
    /// </summary>
    public class MarketIndex
    {
        /// <summary>Yahoo Finance symbol, e.g. "^DJI".</summary>
        public string Symbol { get; set; } = "";

        /// <summary>Display name shown in the UI, e.g. "DOW".</summary>
        public string Name { get; set; } = "";

        public decimal? Price       { get; set; }
        public decimal? DayChange   { get; set; }
        public double?  DayChangePct { get; set; }

        /// <summary>True when the index is flat or positive on the day.</summary>
        public bool IsPositive => DayChangePct.HasValue && DayChangePct.Value >= 0;

        /// <summary>Formatted current level with thousands separator.</summary>
        public string PriceDisplay =>
            Price.HasValue ? Price.Value.ToString("N2") : "—";

        /// <summary>
        /// Combined change string: "▲ +123.45 (+0.29%)" or "▼ -55.12 (-0.31%)"
        /// Returns "—" if no data is available yet.
        /// </summary>
        public string ChangeDisplay
        {
            get
            {
                if (!DayChange.HasValue || !DayChangePct.HasValue) return "—";

                var arrow = DayChange.Value >= 0 ? "▲" : "▼";
                var pts   = DayChange.Value >= 0
                    ? $"+{DayChange.Value:N2}"
                    : $"{DayChange.Value:N2}";
                var pct   = DayChangePct.Value >= 0
                    ? $"+{DayChangePct.Value:F2}%"
                    : $"{DayChangePct.Value:F2}%";

                return $"{arrow} {pts} ({pct})";
            }
        }
    }
}
