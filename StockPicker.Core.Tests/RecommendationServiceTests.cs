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
    /// RecommendationService: score → action bucketing, confidence bounds, target
    /// price direction, ordering, and holding-period trade dates. Inputs are
    /// hand-built AnalysisResults so no analysis/network is involved.
    /// </summary>
    public class RecommendationServiceTests
    {
        private static readonly IStrategyProvider Strategies = new StrategyProvider();
        private readonly RecommendationService _service = new(new TradingCalendar());

        private static ScanContext Context(string strategyId) => new()
        {
            Strategy = Strategies.GetStrategies().Single(s => s.Id == strategyId),
            TargetProfitMarginPercent = 5m,
        };

        private static AnalysisResult Result(string symbol, double score, double lastClose = 100.0)
        {
            var r = new AnalysisResult { Symbol = symbol, Score = score };
            r.Indicators["LastClose"] = lastClose;
            r.Signals.Add("synthetic signal");
            return r;
        }

        private async Task<Recommendation> Single(double score, string strategyId = "momentum")
        {
            var recs = await _service.GenerateAsync(new[] { Result("TEST", score) }, Context(strategyId));
            return Assert.Single(recs);
        }

        // ── Score → Action mapping (documented thresholds incl. boundaries) ───

        [Theory]
        [InlineData( 2.5, RecommendationAction.StrongBuy)]
        [InlineData( 2.0, RecommendationAction.StrongBuy)]   // boundary
        [InlineData( 1.0, RecommendationAction.Buy)]
        [InlineData( 0.5, RecommendationAction.Buy)]          // boundary
        [InlineData( 0.0, RecommendationAction.Hold)]
        [InlineData( 0.4, RecommendationAction.Hold)]
        [InlineData(-0.4, RecommendationAction.Hold)]
        [InlineData(-0.5, RecommendationAction.Sell)]         // boundary
        [InlineData(-1.0, RecommendationAction.Sell)]
        [InlineData(-2.0, RecommendationAction.StrongSell)]   // boundary
        [InlineData(-2.5, RecommendationAction.StrongSell)]
        public async Task Score_MapsToExpectedAction(double score, RecommendationAction expected)
        {
            var rec = await Single(score);
            Assert.Equal(expected, rec.Action);
            Assert.True(Enum.IsDefined(rec.Action));
        }

        // ── Confidence ────────────────────────────────────────────────────────

        [Theory]
        [InlineData( 0.0)]
        [InlineData( 1.5)]
        [InlineData(-1.5)]
        [InlineData( 4.5)]   // |score|/3 would be 1.5 — must cap at 1.0
        [InlineData(-9.0)]
        public async Task Confidence_IsAlwaysWithinZeroToOne(double score)
        {
            var rec = await Single(score);
            Assert.InRange(rec.Confidence, 0.0, 1.0);
        }

        // ── Target price direction ────────────────────────────────────────────

        [Fact]
        public async Task TargetPrice_IsAboveLastPriceForBuys_BelowForSells()
        {
            var buy  = await Single( 1.0);   // LastClose 100, target 5%
            var sell = await Single(-1.0);
            var hold = await Single( 0.0);

            Assert.NotNull(buy.TargetPrice);
            Assert.True(buy.TargetPrice > buy.LastPrice,
                $"buy target {buy.TargetPrice} should be above last {buy.LastPrice}");

            Assert.NotNull(sell.TargetPrice);
            Assert.True(sell.TargetPrice < sell.LastPrice,
                $"sell target {sell.TargetPrice} should be below last {sell.LastPrice}");

            Assert.Null(hold.TargetPrice);   // no directional target on Hold
        }

        // ── Ordering ──────────────────────────────────────────────────────────

        [Fact]
        public async Task Results_AreOrderedByConfidenceDescending()
        {
            var analyses = new[]
            {
                Result("MILD",  0.6),
                Result("HOT",   2.9),
                Result("FLAT",  0.0),
                Result("UGLY", -2.4),
            };

            var recs = await _service.GenerateAsync(analyses, Context("momentum"));

            var confidences = recs.Select(r => r.Confidence).ToList();
            Assert.Equal(confidences.OrderByDescending(c => c), confidences);
            Assert.Equal("HOT", recs[0].Symbol);   // highest conviction first
        }

        // ── Trade dates per holding period ────────────────────────────────────

        [Fact]
        public async Task QuickStrategy_ProposesMondayToFridayWeek()
        {
            var rec = await Single(1.0, "momentum");   // Momentum is HoldingPeriod.Quick

            Assert.NotNull(rec.BuyDate);
            Assert.NotNull(rec.SellDate);
            Assert.Equal(DayOfWeek.Monday, rec.BuyDate!.Value.DayOfWeek);
            Assert.Equal(DayOfWeek.Friday, rec.SellDate!.Value.DayOfWeek);
            Assert.Equal(rec.BuyDate.Value.AddDays(4), rec.SellDate.Value);
            Assert.True(rec.BuyDate.Value >= DateTime.Today, "never propose a trade already underway");
        }

        [Fact]
        public async Task ShortStrategy_BuysNextTradingDayAndSellsSixMonthsOut()
        {
            var rec = await Single(1.0, "mean-reversion");   // HoldingPeriod.Short

            Assert.NotNull(rec.BuyDate);
            var buy = rec.BuyDate!.Value;
            Assert.True(buy > DateTime.Today, "buy date must be a future trading day");
            Assert.NotEqual(DayOfWeek.Saturday, buy.DayOfWeek);
            Assert.NotEqual(DayOfWeek.Sunday,   buy.DayOfWeek);
            Assert.Equal(buy.AddMonths(6), rec.SellDate);
        }

        [Fact]
        public async Task LongStrategy_SellsTwoYearsAfterBuy()
        {
            var rec = await Single(1.0, "buy-and-hold");   // HoldingPeriod.Long

            Assert.NotNull(rec.BuyDate);
            Assert.Equal(rec.BuyDate!.Value.AddYears(2), rec.SellDate);
        }

        // ── Reasoning carries the analysis signals ────────────────────────────

        [Fact]
        public async Task Reasoning_IncludesSignalsAndStrategyName()
        {
            var rec = await Single(1.0, "momentum");

            Assert.Contains("synthetic signal", rec.Reasoning);
            Assert.Contains("Momentum", rec.Reasoning);
        }
    }
}
