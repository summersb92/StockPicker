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
    ///
    /// Each window is valued from the latest close at or before the window start. When we hold
    /// LESS history than a window's length, that window CLAMPS to the earliest available close
    /// instead of dropping out — so, e.g., with only a month of price history the Quarter and
    /// Year returns both equal the Month return rather than showing "n/a".
    ///
    /// Margin positions are valued on EQUITY: each window subtracts the margin loan from both
    /// ends and the interest accrued during the window from the current end, so the return is
    /// leveraged and carry-net (a 2× position that moved +30% shows ~+60% less carry). Cash
    /// positions have no loan or interest, so they are unaffected.
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
            decimal cash = 0m,
            DateTime? asOf = null,
            int maxConcurrency = 8,
            CancellationToken ct = default)
        {
            var today     = (asOf ?? DateTime.Today).Date;
            var positions = held.Where(h => h.ShareCount > 0).ToList();
            if (positions.Count == 0)
                return new PortfolioPerformance { AsOf = DateTime.Now, CashBalance = cash };

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

            // Cost basis = the investor's own equity (== full cost for cash positions).
            // Market value = current equity value: holdings marked to market, less any
            // outstanding margin loan and the interest accrued on it. For cash positions
            // BorrowedAmount and InterestAccrued are zero, so this is just price × shares.
            decimal costBasis = positions.Sum(p => p.EquityInvested);
            decimal marketValue = positions.Sum(p =>
                (LatestClose(histories, p.Symbol) ?? p.LastPrice ?? p.EntryPrice) * p.ShareCount
                - p.BorrowedAmount - p.InterestAccrued);

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
                        // Value each window on the EQUITY in the position, not the gross holding:
                        // subtract the (constant) margin loan from both ends and the interest that
                        // accrued during the window from the current end. For a cash position the
                        // loan and interest are zero, so this is just price × shares — unchanged.
                        decimal borrowed = p.BorrowedAmount;
                        startVal += s * p.ShareCount - borrowed;
                        curVal   += c * p.ShareCount - borrowed - InterestOverWindow(p, ds, today);
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
                CashBalance   = cash,
                Periods       = periods,
            };
        }

        private static decimal? LatestClose(
            Dictionary<string, IReadOnlyList<StockQuote>> histories, string symbol)
            => histories.TryGetValue(symbol, out var bars) && bars.Count > 0
                ? bars[^1].Close : (decimal?)null;

        /// <summary>
        /// Margin interest accrued on a position during a trailing window, prorated to the days
        /// the position was actually held inside that window (from the later of the window start
        /// and the entry date, through today). Zero for cash positions.
        /// </summary>
        private static decimal InterestOverWindow(HeldPosition p, DateTime windowStart, DateTime today)
        {
            if (!p.BoughtOnMargin || p.MarginInterestRatePercent <= 0m || p.BorrowedAmount <= 0m)
                return 0m;

            var entry = p.EntryDate == default ? today.Date : p.EntryDate.Date;
            var from  = entry > windowStart.Date ? entry : windowStart.Date;
            double days = (today.Date - from).TotalDays;
            if (days <= 0) return 0m;

            return p.BorrowedAmount * (p.MarginInterestRatePercent / 100m) * (decimal)(days / 365.0);
        }

        /// <summary>
        /// Close to value a window from. Normally the most recent close at or before
        /// <paramref name="date"/>. If the window starts before our earliest available bar
        /// (i.e. we hold less history than the window length), it CLAMPS to the earliest
        /// close so the window still produces a return — e.g. with only a month of history,
        /// the Quarter and Year windows clamp to that month's start and therefore equal the
        /// Month return rather than dropping out. Returns null only when there are no bars.
        /// Bars are ascending by timestamp.
        /// </summary>
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

            // No bar on/before the window start → clamp to the earliest bar we have.
            return best ?? bars[0].Close;
        }
    }
}
