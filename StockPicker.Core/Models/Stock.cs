namespace StockPicker.Models
{
    /// <summary>
    /// Represents a tradeable security — the identity of a stock, not its price data.
    /// Price history lives on <see cref="StockQuote"/>.
    /// </summary>
    public class Stock
    {
        /// <summary>Ticker symbol, e.g. "AAPL".</summary>
        public string Symbol { get; set; } = string.Empty;

        /// <summary>Company name, e.g. "Apple Inc.".</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Exchange, e.g. "NASDAQ" or "NYSE".</summary>
        public string Exchange { get; set; } = string.Empty;

        /// <summary>Sector classification, e.g. "Technology".</summary>
        public string Sector { get; set; } = string.Empty;
    }
}
