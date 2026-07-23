using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StockPicker.Models;
using StockPicker.Services;

namespace StockPicker.Core.Tests
{
    /// <summary>
    /// Deterministic synthetic price-history builders shared by the service tests.
    /// No network, no randomness — every bar is computed from the parameters.
    /// </summary>
    internal static class TestBars
    {
        /// <summary>
        /// One daily bar per calendar day from <paramref name="start"/> to
        /// <paramref name="end"/> (inclusive), with the close supplied per date.
        /// High/Low bracket the close by ±1% so ATR/high-based logic stays sane.
        /// </summary>
        public static List<StockQuote> Daily(
            string symbol, DateTime start, DateTime end,
            Func<DateTime, decimal> close, long volume = 1_000_000)
        {
            var bars = new List<StockQuote>();
            for (var d = start.Date; d <= end.Date; d = d.AddDays(1))
            {
                var c = close(d);
                bars.Add(new StockQuote
                {
                    Symbol    = symbol,
                    Timestamp = d,
                    Open      = c,
                    High      = c * 1.01m,
                    Low       = c * 0.99m,
                    Close     = c,
                    Volume    = volume,
                });
            }
            return bars;
        }

        /// <summary>
        /// <paramref name="count"/> consecutive daily bars ending today, with the
        /// close (and optionally volume) computed from the bar index 0..count-1.
        /// </summary>
        public static List<StockQuote> Indexed(
            string symbol, int count,
            Func<int, decimal> close, Func<int, long>? volume = null)
        {
            var start = DateTime.Today.AddDays(-(count - 1));
            var bars = new List<StockQuote>(count);
            for (int i = 0; i < count; i++)
            {
                var c = close(i);
                bars.Add(new StockQuote
                {
                    Symbol    = symbol,
                    Timestamp = start.AddDays(i),
                    Open      = c,
                    High      = c * 1.01m,
                    Low       = c * 0.99m,
                    Close     = c,
                    Volume    = volume?.Invoke(i) ?? 1_000_000,
                });
            }
            return bars;
        }
    }

    /// <summary>
    /// In-memory <see cref="IStockDataService"/>: serves pre-canned histories keyed
    /// by symbol and nothing else. Symbols without a canned history throw, which the
    /// consumer under test (PerformanceService) is expected to swallow per symbol.
    /// </summary>
    internal sealed class FakeStockDataService : IStockDataService
    {
        public Dictionary<string, IReadOnlyList<StockQuote>> Histories { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public DataSourceType SourceType => default;

        public Task<IReadOnlyList<StockQuote>> GetHistoryAsync(string symbol, DateTime from, DateTime to)
        {
            if (!Histories.TryGetValue(symbol, out var bars))
                throw new InvalidOperationException($"No canned history for '{symbol}'.");

            IReadOnlyList<StockQuote> windowed = bars
                .Where(b => b.Timestamp.Date >= from.Date && b.Timestamp.Date <= to.Date)
                .ToList();
            return Task.FromResult(windowed);
        }

        public Task<IReadOnlyList<Stock>> GetUniverseAsync()
            => Task.FromResult<IReadOnlyList<Stock>>(Array.Empty<Stock>());

        public Task<StockQuote?> GetLatestQuoteAsync(string symbol)
            => Task.FromResult<StockQuote?>(null);

        public Task<Dictionary<string, QuoteSummary>> GetQuoteSummariesAsync(IEnumerable<string> symbols)
            => Task.FromResult(new Dictionary<string, QuoteSummary>());

        public Task<IReadOnlyList<WeeklyBar>> GetWeeklyBarsAsync(
            string symbol, ChartRange range = ChartRange.Year, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<WeeklyBar>>(Array.Empty<WeeklyBar>());

        public Task<(double? IV, double? Theta)> GetNearTermOptionsAsync(
            string symbol, CancellationToken ct = default)
            => Task.FromResult<(double?, double?)>((null, null));
    }
}
