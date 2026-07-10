using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using StockPicker.Core.Tests.TestDoubles;
using StockPicker.Models;
using StockPicker.Services;
using Xunit;

namespace StockPicker.Core.Tests
{
    /// <summary>
    /// Reconstructed trailing-window performance from an in-memory
    /// <see cref="FakeStockDataService"/>. Every run pins <c>asOf</c> so the
    /// window boundaries are deterministic.
    /// </summary>
    public class PerformanceServiceTests
    {
        private static readonly DateTime AsOf = new(2026, 6, 1);

        [Fact]
        public async Task ComputeAsync_NoPositions_ReturnsCashOnly()
        {
            var data = new FakeStockDataService();

            var perf = await PerformanceService.ComputeAsync(
                Array.Empty<HeldPosition>(), data, cash: 1234.56m, asOf: AsOf);

            Assert.Equal(0, perf.PositionCount);
            Assert.Equal(1234.56m, perf.CashBalance);
            Assert.Equal(0m, perf.MarketValue);
            Assert.Equal(1234.56m, perf.TotalValue);
            Assert.Empty(perf.Periods);
        }

        [Fact]
        public async Task ComputeAsync_ZeroShareCountsOnly_TreatedAsNoPositions()
        {
            var data = new FakeStockDataService();
            var held = new[] { new HeldPosition { Symbol = "AAPL", ShareCount = 0, EntryPrice = 10m } };

            var perf = await PerformanceService.ComputeAsync(held, data, cash: 500m, asOf: AsOf);

            Assert.Equal(0, perf.PositionCount);
            Assert.Equal(500m, perf.CashBalance);
            Assert.Empty(perf.Periods);
        }

        [Fact]
        public async Task ComputeAsync_ShortHistory_ClampsAllWindowsToEarliestClose()
        {
            // Only ~5 days of history — shorter than every trailing window. Each window
            // start precedes the earliest bar, so all windows clamp to the earliest close
            // (100) and therefore report identical start/current values.
            var data = new FakeStockDataService()
                .WithHistory("AAPL",
                    (AsOf.AddDays(-5), 100m),
                    (AsOf,             110m));

            var held = new[]
            {
                new HeldPosition { Symbol = "AAPL", ShareCount = 10, EntryPrice = 100m },
            };

            var perf = await PerformanceService.ComputeAsync(held, data, cash: 0m, asOf: AsOf);

            Assert.Equal(4, perf.Periods.Count);
            Assert.All(perf.Periods, p => Assert.True(p.HasData));

            // Every window clamps to the earliest bar (100) at the start and the latest (110) now.
            Assert.All(perf.Periods, p => Assert.Equal(1000m, p.StartValue));
            Assert.All(perf.Periods, p => Assert.Equal(1100m, p.CurrentValue));

            // Week == Month == Quarter == Year because they all clamped to the same close.
            var distinctStart = perf.Periods.Select(p => p.StartValue).Distinct().Count();
            Assert.Equal(1, distinctStart);
        }

        [Fact]
        public async Task ComputeAsync_MarginPosition_SubtractsLoanFromBothWindowEnds()
        {
            // 50% margin → 2× leverage. Interest rate 0 so only the loan (not carry)
            // moves the numbers, keeping the assertion exact.
            var data = new FakeStockDataService()
                .WithHistory("MSFT",
                    (AsOf.AddYears(-1).AddDays(-5), 100m), // earliest bar, before every window
                    (AsOf,                           120m)); // current

            var held = new[]
            {
                new HeldPosition
                {
                    Symbol = "MSFT",
                    ShareCount = 10,
                    EntryPrice = 100m,
                    EntryDate = AsOf.AddYears(-1).AddDays(-5), // before every window start
                    BoughtOnMargin = true,
                    MarginPercent = 50m,                        // borrowed = 50% of cost basis
                    MarginInterestRatePercent = 0m,
                },
            };

            var perf = await PerformanceService.ComputeAsync(held, data, cash: 0m, asOf: AsOf);

            // Cost basis = investor equity = 1000 * 0.5 = 500.
            Assert.Equal(500m, perf.CostBasis);

            var week = perf.Periods.Single(p => p.Label == "Week");
            // Borrowed = 500. start = 100*10 - 500 = 500; current = 120*10 - 500 = 700.
            Assert.Equal(500m, week.StartValue);
            Assert.Equal(700m, week.CurrentValue);
        }

        [Fact]
        public async Task ComputeAsync_MarginInterest_ReducesCurrentWindowEquity()
        {
            HeldPosition Position(decimal rate) => new()
            {
                Symbol = "MSFT",
                ShareCount = 10,
                EntryPrice = 100m,
                EntryDate = AsOf.AddYears(-1).AddDays(-5),
                BoughtOnMargin = true,
                MarginPercent = 50m,
                MarginInterestRatePercent = rate,
            };

            (DateTime, decimal)[] bars =
            {
                (AsOf.AddYears(-1).AddDays(-5), 100m),
                (AsOf,                           120m),
            };

            var noInterest = await PerformanceService.ComputeAsync(
                new[] { Position(0m) },
                new FakeStockDataService().WithHistory("MSFT", bars),
                asOf: AsOf);

            var withInterest = await PerformanceService.ComputeAsync(
                new[] { Position(12m) },
                new FakeStockDataService().WithHistory("MSFT", bars),
                asOf: AsOf);

            var weekNo   = noInterest.Periods.Single(p => p.Label == "Week");
            var weekYes  = withInterest.Periods.Single(p => p.Label == "Week");

            // Accrued interest over the window is subtracted from the current end only,
            // so the current value drops while the start value is unchanged.
            Assert.Equal(weekNo.StartValue, weekYes.StartValue);
            Assert.True(weekYes.CurrentValue < weekNo.CurrentValue,
                $"expected interest to reduce current equity: {weekYes.CurrentValue} < {weekNo.CurrentValue}");
        }
    }
}
