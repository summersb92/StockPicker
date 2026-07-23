using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using StockPicker.Models;
using StockPicker.Services;
using Xunit;

namespace StockPicker.Core.Tests
{
    /// <summary>
    /// PerformanceService math against fully synthetic histories: cost basis,
    /// market value, total gain, trailing-window returns (including the documented
    /// clamp-to-earliest-bar behavior), margin equity valuation, and the empty /
    /// missing-history edge cases. All values are exact decimals by construction.
    /// </summary>
    public class PerformanceServiceTests
    {
        // Fixed as-of date so window boundaries never depend on the day the tests run.
        private static readonly DateTime AsOf = new(2025, 6, 10);   // a Tuesday

        /// <summary>
        /// A year+ of daily bars with a three-tier close schedule:
        ///   ≤ 2025-05-10 (month-start and earlier) → 100
        ///   2025-05-11 .. 2025-06-03 (week-start)  → 150
        ///   after 2025-06-03                       → 200
        /// So: week window starts at 150, month/quarter/year windows start at 100,
        /// and the latest close is 200.
        /// </summary>
        private static List<StockQuote> TieredYearHistory(string symbol) =>
            TestBars.Daily(symbol,
                start: AsOf.AddYears(-1).AddDays(-14),
                end:   AsOf,
                close: d => d <= new DateTime(2025, 5, 10) ? 100m
                          : d <= new DateTime(2025, 6, 3)  ? 150m
                          : 200m);

        private static HeldPosition CashPosition(
            string symbol, decimal entryPrice, int shares) => new()
        {
            Symbol     = symbol,
            EntryPrice = entryPrice,
            ShareCount = shares,
            EntryDate  = new DateTime(2024, 7, 1),
        };

        [Fact]
        public async Task CashPosition_CostBasisMarketValueAndTotalGain()
        {
            var data = new FakeStockDataService();
            data.Histories["ACME"] = TieredYearHistory("ACME");
            var held = new[] { CashPosition("ACME", entryPrice: 120m, shares: 10) };

            var perf = await PerformanceService.ComputeAsync(held, data, cash: 500m, asOf: AsOf);

            Assert.Equal(1, perf.PositionCount);
            Assert.Equal(1200m, perf.CostBasis);        // 120 × 10 (equity == full cost for cash)
            Assert.Equal(2000m, perf.MarketValue);      // latest close 200 × 10
            Assert.Equal(800m,  perf.TotalGain);        // 2000 − 1200
            Assert.Equal(500m,  perf.CashBalance);
            Assert.Equal(2500m, perf.TotalValue);       // holdings + cash
        }

        [Fact]
        public async Task TrailingWindows_ValueFromCloseOnOrBeforeWindowStart()
        {
            var data = new FakeStockDataService();
            data.Histories["ACME"] = TieredYearHistory("ACME");
            var held = new[] { CashPosition("ACME", entryPrice: 120m, shares: 10) };

            var perf = await PerformanceService.ComputeAsync(held, data, asOf: AsOf);

            var byLabel = perf.Periods.ToDictionary(p => p.Label);
            Assert.Equal(new[] { "Week", "Month", "Quarter", "Year" }, perf.Periods.Select(p => p.Label));
            Assert.All(perf.Periods, p =>
            {
                Assert.True(p.HasData);
                Assert.Equal(1, p.PositionsCovered);
                Assert.Equal(2000m, p.CurrentValue);    // 200 × 10 at every window's current end
            });

            Assert.Equal(1500m, byLabel["Week"].StartValue);     // close on 2025-06-03 = 150
            Assert.Equal(1000m, byLabel["Month"].StartValue);    // close on 2025-05-10 = 100
            Assert.Equal(1000m, byLabel["Quarter"].StartValue);
            Assert.Equal(1000m, byLabel["Year"].StartValue);
        }

        [Fact]
        public async Task ShortHistory_ClampsLongerWindowsToEarliestClose()
        {
            // Only ~3 weeks of bars: 80 → 100 → 200 tiers. Month/Quarter/Year windows
            // all start before the first bar, so they clamp to the earliest close (80)
            // instead of dropping out.
            var data = new FakeStockDataService();
            data.Histories["NEWCO"] = TestBars.Daily("NEWCO",
                start: new DateTime(2025, 5, 20),
                end:   AsOf,
                close: d => d <= new DateTime(2025, 5, 31) ? 80m
                          : d <= new DateTime(2025, 6, 3)  ? 100m
                          : 200m);
            var held = new[] { CashPosition("NEWCO", entryPrice: 90m, shares: 5) };

            var perf = await PerformanceService.ComputeAsync(held, data, asOf: AsOf);

            var byLabel = perf.Periods.ToDictionary(p => p.Label);
            Assert.Equal(500m, byLabel["Week"].StartValue);      // close on 2025-06-03 = 100 × 5
            Assert.Equal(400m, byLabel["Month"].StartValue);     // clamped to earliest close 80 × 5
            Assert.Equal(400m, byLabel["Quarter"].StartValue);   // same clamp
            Assert.Equal(400m, byLabel["Year"].StartValue);      // same clamp
            Assert.All(perf.Periods, p => Assert.True(p.HasData));
        }

        [Fact]
        public async Task MarginPosition_IsValuedOnEquityNotGrossHolding()
        {
            // 50% margin, zero interest rate (keeps the math independent of the real
            // clock — InterestAccrued is 0 regardless of DaysHeld): 10 shares @ 100
            // → cost 1000, equity 500, borrowed 500.
            var data = new FakeStockDataService();
            data.Histories["LEV"] = TestBars.Daily("LEV",
                start: AsOf.AddYears(-1).AddDays(-14),
                end:   AsOf,
                close: d => d <= new DateTime(2025, 6, 3) ? 100m : 120m);

            var held = new[]
            {
                new HeldPosition
                {
                    Symbol                    = "LEV",
                    EntryPrice                = 100m,
                    ShareCount                = 10,
                    EntryDate                 = new DateTime(2024, 7, 1),
                    BoughtOnMargin            = true,
                    MarginPercent             = 50m,
                    MarginInterestRatePercent = 0m,
                },
            };

            var perf = await PerformanceService.ComputeAsync(held, data, asOf: AsOf);

            Assert.Equal(500m, perf.CostBasis);      // equity invested, not gross cost
            Assert.Equal(700m, perf.MarketValue);    // 120×10 − 500 loan − 0 interest

            // A +20% price move is +40% on equity at 2× leverage.
            var week = perf.Periods.Single(p => p.Label == "Week");
            Assert.Equal(500m, week.StartValue);     // 100×10 − 500
            Assert.Equal(700m, week.CurrentValue);   // 120×10 − 500
            Assert.Equal(40.0, week.ChangePct, precision: 6);
        }

        [Fact]
        public async Task NoPositions_ReturnsEmptyPerformanceWithCash()
        {
            var perf = await PerformanceService.ComputeAsync(
                Array.Empty<HeldPosition>(), new FakeStockDataService(), cash: 750m, asOf: AsOf);

            Assert.Equal(0, perf.PositionCount);
            Assert.Equal(750m, perf.CashBalance);
            Assert.Equal(750m, perf.TotalValue);
            Assert.Empty(perf.Periods);
            Assert.False(perf.HasPositions);
        }

        [Fact]
        public async Task ZeroShareAndMissingHistoryPositions_AreHandledGracefully()
        {
            // "PLAN" has 0 shares → filtered out entirely. "GONE" has shares but its
            // history fetch throws → it drops out of every window (HasData false) and
            // is valued at its entry price in MarketValue.
            var data = new FakeStockDataService();   // no canned history at all
            var held = new[]
            {
                CashPosition("PLAN", entryPrice: 50m, shares: 0),
                CashPosition("GONE", entryPrice: 40m, shares: 10),
            };

            var perf = await PerformanceService.ComputeAsync(held, data, asOf: AsOf);

            Assert.Equal(1, perf.PositionCount);     // only GONE counts
            Assert.Equal(400m, perf.CostBasis);
            Assert.Equal(400m, perf.MarketValue);    // falls back to EntryPrice × shares
            Assert.All(perf.Periods, p =>
            {
                Assert.False(p.HasData);
                Assert.Equal(0, p.PositionsCovered);
            });
        }
    }
}
