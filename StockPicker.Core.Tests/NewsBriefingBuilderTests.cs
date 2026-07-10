using System;
using System.Collections.Generic;
using System.Linq;
using StockPicker.Models;
using StockPicker.Services;
using Xunit;

namespace StockPicker.Core.Tests
{
    /// <summary>
    /// Behaviour of the pure markdown briefing builder: action formatting,
    /// section content, and per-position hold/sell advice.
    /// </summary>
    public class NewsBriefingBuilderTests
    {
        [Fact]
        public void FormatAction_MapsEveryEnumValue_ToNonEmptyText()
        {
            // Iterating the enum means a newly added action with no mapping fails here.
            foreach (RecommendationAction action in Enum.GetValues<RecommendationAction>())
            {
                var text = NewsBriefingBuilder.FormatAction(action);
                Assert.False(string.IsNullOrWhiteSpace(text),
                    $"FormatAction returned empty text for {action}");
                // FormatAction's default arm is `_ => action.ToString()`, so a non-empty
                // check alone is tautological. Require an explicit human-readable mapping
                // that differs from the raw enum name — a new unmapped value fails here.
                Assert.NotEqual(action.ToString(), text);
            }
        }

        [Fact]
        public void Build_ProducesSections_ContainingTheFedInSymbols()
        {
            var input = new BriefingInput
            {
                StrategyName = "Momentum",
                Recommendations = new[]
                {
                    new Recommendation { Symbol = "AAPL", CompanyName = "Apple Inc.",     Action = RecommendationAction.Buy,      Score = 2.5, Sector = "Technology" },
                    new Recommendation { Symbol = "MSFT", CompanyName = "Microsoft Corp", Action = RecommendationAction.StrongBuy, Score = 3.1, Sector = "Technology" },
                },
                Positions = new[]
                {
                    new HeldPosition { Symbol = "AAPL", CompanyName = "Apple Inc.", EntryPrice = 150m, ShareCount = 5 },
                },
            };

            var md = NewsBriefingBuilder.Build(input);

            Assert.Contains("# StockPicker Market Briefing", md);
            Assert.Contains("AAPL", md);
            Assert.Contains("MSFT", md);
            Assert.Contains("Your positions", md);
            Assert.Contains("Top picks", md);
        }

        [Fact]
        public void AdvisePosition_Hold_ProducesNonEmptyVerdictRationaleAndExit()
        {
            var pos = new HeldPosition
            {
                Symbol = "AAPL",
                EntryPrice = 100m,
                ShareCount = 10,
                LastPrice = 102m, // small gain, below target, no sell signal
            };

            var (verdict, rationale, exit) =
                NewsBriefingBuilder.AdvisePosition(pos, signal: null, targetMonthlyPercent: 8m);

            Assert.Equal("HOLD", verdict);
            Assert.False(string.IsNullOrWhiteSpace(rationale));
            Assert.False(string.IsNullOrWhiteSpace(exit));
        }

        [Fact]
        public void AdvisePosition_PlannedExitReached_AdvisesSell()
        {
            var pos = new HeldPosition
            {
                Symbol = "AAPL",
                EntryPrice = 100m,
                ShareCount = 10,
                LastPrice = 101m,
                PlannedSellDate = new DateTime(2020, 1, 1), // fixed past date — no wall-clock read
            };

            var (verdict, rationale, exit) =
                NewsBriefingBuilder.AdvisePosition(pos, signal: null, targetMonthlyPercent: 8m);

            Assert.Equal("SELL", verdict);
            Assert.False(string.IsNullOrWhiteSpace(rationale));
            Assert.False(string.IsNullOrWhiteSpace(exit));
        }

        [Fact]
        public void AdvisePosition_BearishSignal_AdvisesSell()
        {
            var pos = new HeldPosition
            {
                Symbol = "AAPL",
                EntryPrice = 100m,
                ShareCount = 10,
                LastPrice = 101m,
            };
            var signal = new Recommendation { Symbol = "AAPL", Action = RecommendationAction.StrongSell, Confidence = 0.9 };

            var (verdict, rationale, exit) =
                NewsBriefingBuilder.AdvisePosition(pos, signal, targetMonthlyPercent: 8m);

            Assert.Equal("SELL", verdict);
            Assert.Contains("STRONG SELL", rationale);
            Assert.False(string.IsNullOrWhiteSpace(exit));
        }
    }
}
