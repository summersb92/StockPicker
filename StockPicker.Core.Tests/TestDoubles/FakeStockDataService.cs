using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StockPicker.Models;
using StockPicker.Services;

namespace StockPicker.Core.Tests.TestDoubles
{
    /// <summary>
    /// Minimal in-memory <see cref="IStockDataService"/> for unit tests. Canned
    /// data is set per-test via <see cref="Histories"/>; the network-bound members
    /// return empty/neutral results. Only <see cref="GetHistoryAsync"/> is exercised
    /// by the code under test (PerformanceService), but every interface member is
    /// implemented so the double compiles against the real interface.
    /// </summary>
    public sealed class FakeStockDataService : IStockDataService
    {
        /// <summary>Canned daily bars keyed by symbol (case-insensitive).</summary>
        public Dictionary<string, IReadOnlyList<StockQuote>> Histories { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public DataSourceType SourceType { get; set; } = DataSourceType.YahooFinance;

        /// <summary>Register a canned history for a symbol from (timestamp, close) pairs.</summary>
        public FakeStockDataService WithHistory(string symbol, params (DateTime Date, decimal Close)[] bars)
        {
            Histories[symbol] = bars
                .Select(b => new StockQuote
                {
                    Symbol    = symbol,
                    Timestamp = b.Date,
                    Open      = b.Close,
                    High      = b.Close,
                    Low       = b.Close,
                    Close     = b.Close,
                })
                .ToList();
            return this;
        }

        public Task<IReadOnlyList<Stock>> GetUniverseAsync()
            => Task.FromResult<IReadOnlyList<Stock>>(Array.Empty<Stock>());

        public Task<IReadOnlyList<StockQuote>> GetHistoryAsync(string symbol, DateTime from, DateTime to)
        {
            if (Histories.TryGetValue(symbol, out var bars))
            {
                // Mirror a real source: bars within the requested (inclusive) window.
                var window = bars
                    .Where(b => b.Timestamp.Date >= from.Date && b.Timestamp.Date <= to.Date)
                    .ToList();
                return Task.FromResult<IReadOnlyList<StockQuote>>(window);
            }
            return Task.FromResult<IReadOnlyList<StockQuote>>(Array.Empty<StockQuote>());
        }

        public Task<StockQuote?> GetLatestQuoteAsync(string symbol)
        {
            if (Histories.TryGetValue(symbol, out var bars) && bars.Count > 0)
                return Task.FromResult<StockQuote?>(bars[^1]);
            return Task.FromResult<StockQuote?>(null);
        }

        public Task<Dictionary<string, QuoteSummary>> GetQuoteSummariesAsync(IEnumerable<string> symbols)
            => Task.FromResult(new Dictionary<string, QuoteSummary>(StringComparer.OrdinalIgnoreCase));

        public Task<IReadOnlyList<WeeklyBar>> GetWeeklyBarsAsync(
            string symbol, ChartRange range = ChartRange.Year, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<WeeklyBar>>(Array.Empty<WeeklyBar>());

        public Task<(double? IV, double? Theta)> GetNearTermOptionsAsync(
            string symbol, CancellationToken ct = default)
            => Task.FromResult<(double?, double?)>((null, null));
    }
}
