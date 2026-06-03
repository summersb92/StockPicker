using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StockPicker.Models;

namespace StockPicker.Services
{
    /// <summary>
    /// Reconstructs trailing-window portfolio performance (week / month / quarter / year)
    /// from each held symbol's historical prices.
    ///
    /// This is a PRICE-performance view of <em>today's</em> holdings: it assumes the current
    /// share counts were held across each window and values them at the window-start price.
    /// It is not a money-weighted return — the app keeps no record of past buys/sells — so a
    /// position entered mid-window is still valued from the window start. Cost basis / total
    /// gain, by contrast, use each position's actual entry price.
    /// </summary>
    public static class PerformanceService
    {
        private static readonly (string Label, Func<DateTime, DateTime> Start)[] Windows =
        {
            ("Week",    d => d.AddDays(-7)),
            ("Month",   d => d.AddMonths(-1)),
            ("Quarter", d => d.AddMonths(-3)),
            ("Year",    d => d.AddYears(-1)),
        };

        /// <summary>
        /// Computes aggregate + trailing-window performance for the held positions, fetching
        /// ~1 year of daily history per symbol from <paramref name="data"/>.
        /// </summary>
        public static async Task<PortfolioPerformance> ComputeAsync(
            IReadOnlyList<HeldPosition> held,
            IStockDataService data,
            DateTime? asOf = null,
            int maxConcurrency = 8,
            CancellationToken ct = default)
        {
            var today     = (asOf ?? DateTime.Today).Date;
            var positions = held.Where(h => h.ShareCount > 0).ToList();
            if (positions.Count == 0)
                return PortfolioPerformance.Empty;

            // Two-week margin so we can resolve a close on/before each window start.
            var from = today.AddYears(-1).AddDays(-14);

            // Fetch history for each distinct held symbol (concurrency-limited).
            var histories = new Dictionary<string, IReadOnlyList<StockQuote>>(StringComparer.OrdinalIgnoreCase);
            using (var sem = new SemaphoreSlim(maxConcurrency))
            {
                var symbols = positions.Select(p => p.Symbol)
                                       .Distinct(StringComparer.OrdinalIgnoreCase)
                                       .ToList();
                var tasks = symbols.Select(async sym =>
                {
                    await sem.WaitAsync(ct);
                    try
                    {
                        var bars = (await data.GetHistoryAsync(sym, from, today))
                                   .OrderBy(b => b.Timestamp).ToList();
                        lock (histories) { histories[sym] = bars; }
                    }
                    catch { /* a missing symbol just drops out of the windows below */ }
                    finally { sem.Release(); }
                });
                await Task.WhenAll(tasks);
            }

            // Cost basis (actual entry) and current market value (latest close, fallback to entry).
            decimal costBasis = positions.Sum(p => p.EntryPrice * p.ShareCount);
            decimal marketValue = positions.Sum(p =>
                (LatestClose(histories, p.Symbol) ?? p.LastPrice ?? p.EntryPrice) * p.ShareCount);

            var periods = new List<PerformancePeriod>(Windows.Length);
            foreach (var (label, startFn) in Windows)
            {
                var ds = startFn(today);
                decimal startVal = 0m, curVal = 0m;
                int covered = 0;

                foreach (var p in positions)
                {
                    var startClose = CloseOnOrBefore(histories, p.Symbol, ds);
                    var curClose   = LatestClose(histories, p.Symbol);
                    if (startClose is decimal s && curClose is decimal c)
                    {
                        startVal += s * p.ShareCount;
                        curVal   += c * p.ShareCount;
                        covered++;
                    }
                }

                periods.Add(new PerformancePeriod
                {
                    Label            = label,
                    StartDate        = ds,
                    StartValue       = startVal,
                    CurrentValue     = curVal,
                    PositionsCovered = covered,
                    HasData          = covered > 0,
                });
            }

            return new PortfolioPerformance
            {
                AsOf          = DateTime.Now,
                PositionCount = positions.Count,
                CostBasis     = costBasis,
                MarketValue   = marketValue,
                Periods       = periods,
            };
        }

        private static decimal? LatestClose(
            Dictionary<string, IReadOnlyList<StockQuote>> histories, string symbol)
            => histories.TryGetValue(symbol, out var bars) && bars.Count > 0
                ? bars[^1].Close : (decimal?)null;

        /// <summary>Most recent close at or before <paramref name="date"/>; null if none. Bars are ascending.</summary>
        private static decimal? CloseOnOrBefore(
            Dictionary<string, IReadOnlyList<StockQuote>> histories, string symbol, DateTime date)
        {
            if (!histories.TryGetValue(symbol, out var bars) || bars.Count == 0)
                return null;

            decimal? best = null;
            foreach (var b in bars)
            {
                if (b.Timestamp.Date <= date.Date) best = b.Close;
                else break;
            }
            return best;
        }
    }
}
