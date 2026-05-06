using System;

namespace StockPicker.Models
{
    /// <summary>
    /// A single OHLCV (Open/High/Low/Close/Volume) bar for a stock at a point in time.
    /// Can represent a daily, weekly, or intraday bar depending on the source.
    /// </summary>
    public class StockQuote
    {
        public string Symbol { get; set; } = string.Empty;

        public DateTime Timestamp { get; set; }

        public decimal Open { get; set; }
        public decimal High { get; set; }
        public decimal Low { get; set; }
        public decimal Close { get; set; }
        public long Volume { get; set; }

        /// <summary>Convenience: close - open.</summary>
        public decimal Change => Close - Open;

        /// <summary>Convenience: percent change from open to close.</summary>
        public decimal ChangePercent => Open == 0 ? 0 : (Change / Open) * 100m;
    }
}
