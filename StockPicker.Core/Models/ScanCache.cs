using System;
using System.Collections.Generic;

namespace StockPicker.Models
{
    /// <summary>
    /// Persisted snapshot of a completed scan — everything needed to reconstruct the
    /// in-memory cache so the app can show recommendations instantly on restart
    /// without waiting for a fresh network fetch.
    ///
    /// Serialised to JSON at %LOCALAPPDATA%\StockPicker\scan_cache.json by
    /// <see cref="StockPicker.Services.ScanCacheService"/>.
    /// </summary>
    public class ScanCache
    {
        /// <summary>When the originating network scan completed.</summary>
        public DateTime FetchTime { get; set; }

        /// <summary>History window used for technical indicators (start of 90-day lookback).</summary>
        public DateTime WeekStart { get; set; }

        /// <summary>History window end (typically today at the time of the scan).</summary>
        public DateTime WeekEnd   { get; set; }

        /// <summary>The stock universe scanned.</summary>
        public List<Stock> Universe { get; set; } = new();

        /// <summary>
        /// OHLCV history per symbol.
        /// Uses <c>List&lt;StockQuote&gt;</c> (not IReadOnlyList) so
        /// System.Text.Json can round-trip it without custom converters.
        /// </summary>
        public Dictionary<string, List<StockQuote>> History { get; set; } = new();

        /// <summary>Live quote data per symbol at the time of the scan.</summary>
        public Dictionary<string, QuoteSummary> Summaries { get; set; } = new();

        /// <summary>
        /// Tracks which data sources provided history data for each symbol.
        /// Used to restore source tags on recommendations after a cache load.
        /// Keyed by symbol; values are DataSourceType enum names.
        /// </summary>
        public Dictionary<string, List<string>> SourcesBySymbol { get; set; } = new();

        /// <summary>
        /// Enabled and fully configured data sources at the time this cache was built.
        /// Used to invalidate the cache when provider settings change.
        /// </summary>
        public List<string> EnabledSources { get; set; } = new();

        /// <summary>
        /// Primary quote-summary source used when the cache was built.
        /// </summary>
        public string PrimaryQuoteSource { get; set; } = string.Empty;
    }
}
