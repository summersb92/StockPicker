using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using StockPicker.Models;

namespace StockPicker.Services
{
    /// <summary>
    /// Fetches stock identity, historical price data, and live quote summaries.
    /// Implementations call Yahoo Finance, Alpha Vantage, Polygon, IEX, etc.
    /// </summary>
    public interface IStockDataService
    {
        /// <summary>Identifies which data source this implementation represents.</summary>
        DataSourceType SourceType { get; }

        /// <summary>
        /// Return the universe of stocks the app should consider for this week's scan.
        /// </summary>
        Task<IReadOnlyList<Stock>> GetUniverseAsync();

        /// <summary>
        /// Fetch historical OHLCV bars for a symbol between the two dates (inclusive).
        /// </summary>
        Task<IReadOnlyList<StockQuote>> GetHistoryAsync(string symbol, DateTime from, DateTime to);

        /// <summary>Fetch the most recent quote for a symbol.</summary>
        Task<StockQuote?> GetLatestQuoteAsync(string symbol);

        /// <summary>
        /// Batch-fetch live market data (price, volume, valuation, 52W range, etc.)
        /// for one or more symbols in a single round-trip.
        /// Returns a dictionary keyed by symbol (upper-case).
        /// Missing or errored symbols are silently omitted.
        /// </summary>
        Task<Dictionary<string, QuoteSummary>> GetQuoteSummariesAsync(IEnumerable<string> symbols);

        /// <summary>
        /// Fetch weekly OHLCV bars for a symbol for the past <paramref name="weeks"/> weeks.
        /// Returns an empty list if data is unavailable.
        /// </summary>
        Task<IReadOnlyList<WeeklyBar>> GetWeeklyBarsAsync(string symbol, ChartRange range = ChartRange.Year, System.Threading.CancellationToken ct = default);

        /// <summary>
        /// Fetches implied volatility and Black-Scholes theta for the near-term ATM option.
        /// Returns (null, null) if options data is unavailable or not supported.
        /// </summary>
        Task<(double? IV, double? Theta)> GetNearTermOptionsAsync(string symbol, System.Threading.CancellationToken ct = default);
    }
}
