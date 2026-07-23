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
    /// Strategy scoring on deterministic synthetic histories. Assertions are
    /// directional (uptrend outscores downtrend, oversold outscores extended, …)
    /// and range-based (RSI in [0,100]) — never exact floats, so indicator
    /// refinements don't turn into false failures.
    /// </summary>
    public class AnalysisServiceTests
    {
        private readonly AnalysisService _service = new();
        private static readonly IStrategyProvider Strategies = new StrategyProvider();

        private static ScanContext Context(string strategyId) => new()
        {
            Strategy = Strategies.GetStrategies().Single(s => s.Id == strategyId),
            TargetProfitMarginPercent = 2.5m,
            // The historical-analog target estimate is O(n²) and orthogonal to the
            // scoring under test — skip it, exactly as the backtest engine does.
            SkipTargetEstimate = true,
        };

        private static Stock StockFor(string symbol) => new() { Symbol = symbol, Name = symbol };

        private Task<AnalysisResult> Analyze(string strategyId, IReadOnlyList<StockQuote> bars)
            => _service.AnalyzeAsync(StockFor(bars.Count > 0 ? bars[0].Symbol : "EMPTY"), bars, Context(strategyId));

        // ── Shared synthetic histories (60 bars, constant volume) ─────────────

        private static List<StockQuote> Uptrend()   => TestBars.Indexed("UP",   60, i => 100m * (decimal)Math.Pow(1.01, i));
        private static List<StockQuote> Downtrend() => TestBars.Indexed("DOWN", 60, i => 100m * (decimal)Math.Pow(0.99, i));
        private static List<StockQuote> Flat()      => TestBars.Indexed("FLAT", 60, _ => 100m);

        // ── Momentum ──────────────────────────────────────────────────────────

        [Fact]
        public async Task Momentum_UptrendOutscoresDowntrend()
        {
            var up   = await Analyze("momentum", Uptrend());
            var down = await Analyze("momentum", Downtrend());

            Assert.True(up.Score > down.Score,
                $"uptrend ({up.Score}) should outscore downtrend ({down.Score})");
            Assert.True(up.Score > 0,   $"steady uptrend should score bullish, got {up.Score}");
            Assert.True(down.Score < 0, $"steady downtrend should score bearish, got {down.Score}");
        }

        [Fact]
        public async Task Momentum_FlatSitsBetweenUptrendAndDowntrend()
        {
            var up   = await Analyze("momentum", Uptrend());
            var flat = await Analyze("momentum", Flat());
            var down = await Analyze("momentum", Downtrend());

            Assert.True(down.Score < flat.Score && flat.Score < up.Score,
                $"expected {down.Score} < {flat.Score} < {up.Score}");
        }

        // ── Indicator sanity ──────────────────────────────────────────────────

        [Fact]
        public async Task Rsi14_IsAlwaysWithinZeroToHundred()
        {
            foreach (var bars in new[] { Uptrend(), Downtrend(), Flat() })
            {
                var result = await Analyze("momentum", bars);
                var rsi = Assert.Contains("RSI14", (IDictionary<string, double>)result.Indicators);
                Assert.InRange(rsi, 0.0, 100.0);
            }
        }

        [Fact]
        public async Task Indicators_IncludeCoreReadingsWhenHistorySuffices()
        {
            var result = await Analyze("momentum", Uptrend());

            Assert.Contains("SMA20",       result.Indicators.Keys);
            Assert.Contains("SMA50",       result.Indicators.Keys);
            Assert.Contains("WeekReturn%", result.Indicators.Keys);
            Assert.Contains("VolumeTrend", result.Indicators.Keys);
            Assert.Contains("LastClose",   result.Indicators.Keys);
            Assert.NotEmpty(result.Signals);
        }

        // ── Mean reversion ────────────────────────────────────────────────────

        [Fact]
        public async Task MeanReversion_SharpDropOutscoresSharpRise()
        {
            // Flat at 100 for 55 bars, then 5 bars sliding to 85 (well below SMA20)
            // versus 5 bars jumping to 115 (well above it).
            var dropped = TestBars.Indexed("DROP",  60, i => i < 55 ? 100m : 100m - 3m * (i - 54));
            var surged  = TestBars.Indexed("SURGE", 60, i => i < 55 ? 100m : 100m + 3m * (i - 54));

            var buyCandidate  = await Analyze("mean-reversion", dropped);
            var fadeCandidate = await Analyze("mean-reversion", surged);

            Assert.True(buyCandidate.Score > fadeCandidate.Score,
                $"oversold ({buyCandidate.Score}) should outscore extended ({fadeCandidate.Score})");
            Assert.True(buyCandidate.Score > 0,
                $"a deep pullback below the mean should score positive, got {buyCandidate.Score}");
            Assert.True(fadeCandidate.Score < 0,
                $"an extended spike above the mean should score negative, got {fadeCandidate.Score}");
        }

        // ── Breakout ──────────────────────────────────────────────────────────

        [Fact]
        public async Task Breakout_NewHighOnVolumeOutscoresStockFarBelowItsHigh()
        {
            // Fresh high with a 2× volume surge on the last 5 bars…
            var breakingOut = TestBars.Indexed("BRK", 60,
                close:  i => 100m + 0.5m * i,
                volume: i => i >= 55 ? 2_000_000L : 1_000_000L);
            // …versus a name still ~25% below the high it set early in the window.
            var brokenDown = TestBars.Indexed("DUD", 60,
                close: i => i < 10 ? 100m : 75m);

            var breakout = await Analyze("breakout", breakingOut);
            var dud      = await Analyze("breakout", brokenDown);

            Assert.True(breakout.Score > dud.Score,
                $"breakout ({breakout.Score}) should outscore broken-down ({dud.Score})");
            Assert.True(breakout.Score > 0, $"a fresh high on volume should score positive, got {breakout.Score}");
        }

        // ── Empty history ─────────────────────────────────────────────────────

        [Fact]
        public async Task EmptyHistory_ScoresZeroWithAnExplanatorySignal()
        {
            var result = await Analyze("momentum", new List<StockQuote>());

            Assert.Equal(0.0, result.Score);
            Assert.Empty(result.Indicators);
            Assert.NotEmpty(result.Signals);   // "No price history available…"
        }
    }
}
