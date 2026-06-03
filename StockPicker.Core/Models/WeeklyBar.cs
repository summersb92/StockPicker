using System;

namespace StockPicker.Models
{
    /// <summary>One week of OHLCV price data, used for chart rendering in the Details pane.</summary>
    public class WeeklyBar
    {
        public DateTime WeekStart  { get; set; }
        public decimal  Open       { get; set; }
        public decimal  High       { get; set; }
        public decimal  Low        { get; set; }
        public decimal  Close      { get; set; }
        public long     Volume     { get; set; }
    }
}
