using System;
using System.Linq;
using StockPicker.Services;
using Xunit;

namespace StockPicker.Core.Tests
{
    /// <summary>
    /// Verifies the shared Wilder RSI/ATR math against documented guards and a
    /// canonical reference series. See <see cref="Indicators"/>.
    /// </summary>
    public class IndicatorsTests
    {
        // ── RSI ────────────────────────────────────────────────────────────────

        [Fact]
        public void RsiWilder_ReturnsNeutral50_WhenFewerThanPeriodPlusOneBars()
        {
            // period + 1 = 15 required; supply only 10.
            var closes = Enumerable.Range(0, 10).Select(i => 100.0 + i).ToArray();
            Assert.Equal(50.0, Indicators.RsiWilder(closes, 14), 10);
        }

        [Fact]
        public void RsiWilder_Returns100_OnMonotonicGains_WhenAvgLossIsZero()
        {
            // Strictly increasing closes → no losses → avgLoss == 0 → RSI 100.
            var closes = Enumerable.Range(0, 30).Select(i => 100.0 + i).ToArray();
            Assert.Equal(100.0, Indicators.RsiWilder(closes, 14), 10);
        }

        [Fact]
        public void RsiWilder_MatchesCanonicalWilderSeed_At14Periods()
        {
            // Canonical StockCharts/Wilder closing series. With exactly 15 closes the
            // result is the seed (SMA of the first 14 changes); published RSI ≈ 70.53.
            var closes = CanonicalCloses.Take(15).ToArray();
            var rsi = Indicators.RsiWilder(closes, 14);
            Assert.InRange(rsi, 70.0, 71.0);
        }

        [Fact]
        public void RsiWilder_MatchesCanonicalWilderSeries_ThroughSmoothing()
        {
            // Full canonical series (33 closes). Published 14-period RSI ≈ 37.77
            // after Wilder smoothing across the tail.
            var rsi = Indicators.RsiWilder(CanonicalCloses, 14);
            Assert.InRange(rsi, 37.0, 38.5);
        }

        // ── ATR ────────────────────────────────────────────────────────────────

        [Fact]
        public void AtrWilder_ReturnsZero_WithFewerThanTwoBars()
        {
            var closes = new[] { 100.0 };
            var highs  = new[] { 101.0 };
            var lows   = new[] { 99.0 };
            Assert.Equal(0.0, Indicators.AtrWilder(closes, highs, lows, 14), 10);
        }

        [Fact]
        public void AtrWilder_TrueRangeAccountsForGaps()
        {
            // Bar 1 gaps up hard from the prior close: intraday high-low is only 1.0,
            // but the gap from prevClose (100) to the low (104) is 4.0 and to the high
            // (105) is 5.0. True range must pick the 5.0 gap term, not the 1.0 range.
            var closes = new[] { 100.0, 104.5 };
            var highs  = new[] { 100.0, 105.0 };
            var lows   = new[] { 100.0, 104.0 };

            var atr = Indicators.AtrWilder(closes, highs, lows, 14);

            // seedLen == 1 → ATR == the single true range == 5.0 (the gap term).
            Assert.Equal(5.0, atr, 6);
            Assert.True(atr > (highs[1] - lows[1]),
                "ATR must exceed the naive high-low range when a gap dominates.");
        }

        /// <summary>
        /// Canonical 14-period Wilder RSI closing prices (StockCharts reference set).
        /// </summary>
        private static readonly double[] CanonicalCloses =
        {
            44.3389, 44.0902, 44.1497, 43.6124, 44.3278, 44.8264, 45.0955, 45.4245,
            45.8433, 46.0826, 45.8931, 46.0328, 45.6140, 46.2820, 46.2820, 46.0028,
            46.0328, 46.4116, 46.2222, 45.6439, 46.2122, 46.2521, 45.7137, 46.4515,
            45.7835, 45.3548, 44.0288, 44.1783, 44.2181, 44.5672, 43.4205, 42.6628,
            43.1314,
        };
    }
}
