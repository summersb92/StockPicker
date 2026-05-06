using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using StockPicker.Models;

namespace StockPicker.Services
{
    /// <summary>
    /// STUB implementation — returns a small hard-coded universe and empty history.
    /// Replace with a real provider (Yahoo Finance, Alpha Vantage, Polygon, IEX Cloud, etc.).
    /// </summary>
    /// <remarks>
    /// TODO Integration notes:
    ///   - Pick a data provider. Alpha Vantage has a free tier but is rate-limited.
    ///     Polygon and IEX are paid but much faster. Yahoo via an unofficial client
    ///     works for prototyping only.
    ///   - Add an HttpClient (inject via constructor) and pull an API key from config.
    ///   - Consider caching: weekly bars don't change intraday, so a simple
    ///     file/memory cache keyed by (symbol, from, to) will slash API calls.
    /// </remarks>
    public class StockDataService : IStockDataService
    {
        /// <inheritdoc />
        public DataSourceType SourceType => DataSourceType.YahooFinance;

        public Task<IReadOnlyList<Stock>> GetUniverseAsync()
        {
            // TODO: replace with a real universe source (file, API, watchlist, index constituents)
            IReadOnlyList<Stock> stubUniverse = new List<Stock>
            {
                new() { Symbol = "AAPL", Name = "Apple Inc.",        Exchange = "NASDAQ", Sector = "Technology" },
                new() { Symbol = "MSFT", Name = "Microsoft Corp.",   Exchange = "NASDAQ", Sector = "Technology" },
                new() { Symbol = "NVDA", Name = "NVIDIA Corp.",      Exchange = "NASDAQ", Sector = "Technology" },
                new() { Symbol = "GOOGL",Name = "Alphabet Inc.",     Exchange = "NASDAQ", Sector = "Communication" },
                new() { Symbol = "AMZN", Name = "Amazon.com Inc.",   Exchange = "NASDAQ", Sector = "Consumer Disc." },
                new() { Symbol = "JPM",  Name = "JPMorgan Chase",    Exchange = "NYSE",   Sector = "Financials" },
                new() { Symbol = "XOM",  Name = "Exxon Mobil",       Exchange = "NYSE",   Sector = "Energy" },
                new() { Symbol = "JNJ",  Name = "Johnson & Johnson", Exchange = "NYSE",   Sector = "Healthcare" },
            };
            return Task.FromResult(stubUniverse);
        }

        public Task<IReadOnlyList<StockQuote>> GetHistoryAsync(string symbol, DateTime from, DateTime to)
        {
            // TODO: call the data provider and map the response to StockQuote bars.
            // For the stub we just return an empty list so downstream code has something to iterate.
            IReadOnlyList<StockQuote> empty = new List<StockQuote>();
            return Task.FromResult(empty);
        }

        public Task<StockQuote?> GetLatestQuoteAsync(string symbol)
        {
            // TODO: fetch the real latest quote from the provider.
            return Task.FromResult<StockQuote?>(null);
        }

        public Task<Dictionary<string, QuoteSummary>> GetQuoteSummariesAsync(IEnumerable<string> symbols)
        {
            // Stub — returns empty dictionary; no live data in this implementation.
            return Task.FromResult(new Dictionary<string, QuoteSummary>());
        }
    }
}
