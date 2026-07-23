using System;
using StockPicker.Services;
using Xunit;

namespace StockPicker.Core.Tests
{
    /// <summary>
    /// Offline tests for <see cref="YahooFinanceStockDataService.ParseAnalystRatings"/>
    /// against canned quoteSummary fixtures (no network). Verifies the happy path,
    /// the "0m" trend-bucket selection, wrapped-"raw" numeric extraction, and that
    /// every malformed/partial payload degrades to nulls instead of throwing.
    /// </summary>
    public class AnalystRatingsParseTests
    {
        private static readonly DateTime FetchedAt = new(2026, 7, 23, 12, 0, 0, DateTimeKind.Utc);

        // Shape mirrors a live v10/finance/quoteSummary response with
        // modules=recommendationTrend,financialData (values are synthetic).
        private const string FullFixture = """
        {
          "quoteSummary": {
            "result": [
              {
                "recommendationTrend": {
                  "trend": [
                    { "period": "0m",  "strongBuy": 6, "buy": 23, "hold": 14, "sell": 2, "strongSell": 2 },
                    { "period": "-1m", "strongBuy": 5, "buy": 20, "hold": 16, "sell": 3, "strongSell": 1 },
                    { "period": "-2m", "strongBuy": 4, "buy": 19, "hold": 17, "sell": 3, "strongSell": 1 }
                  ],
                  "maxAge": 86400
                },
                "financialData": {
                  "maxAge": 86400,
                  "recommendationMean": { "raw": 2.0, "fmt": "2.00" },
                  "recommendationKey": "buy",
                  "numberOfAnalystOpinions": { "raw": 43, "fmt": "43", "longFmt": "43" },
                  "targetMeanPrice": { "raw": 205.5, "fmt": "205.50" },
                  "targetMedianPrice": { "raw": 207.0, "fmt": "207.00" },
                  "targetHighPrice": { "raw": 250.0, "fmt": "250.00" },
                  "targetLowPrice": { "raw": 180.0, "fmt": "180.00" }
                }
              }
            ],
            "error": null
          }
        }
        """;

        [Fact]
        public void FullPayload_ParsesAllFields()
        {
            var r = YahooFinanceStockDataService.ParseAnalystRatings("AAPL", FullFixture, FetchedAt);

            Assert.NotNull(r);
            Assert.Equal("AAPL", r!.Symbol);
            Assert.Equal(FetchedAt, r.FetchedAtUtc);

            // Counts must come from the "0m" bucket, not "-1m"/"-2m".
            Assert.Equal(6,  r.StrongBuy);
            Assert.Equal(23, r.Buy);
            Assert.Equal(14, r.Hold);
            Assert.Equal(2,  r.Sell);
            Assert.Equal(2,  r.StrongSell);
            Assert.Equal(47, r.TotalRatings);

            Assert.Equal(2.0, r.RecommendationMean);
            Assert.Equal("buy", r.RecommendationKey);
            Assert.Equal(43, r.NumberOfAnalystOpinions);
            Assert.Equal(205.5m, r.TargetMeanPrice);
            Assert.Equal(207.0m, r.TargetMedianPrice);
            Assert.Equal(250.0m, r.TargetHighPrice);
            Assert.Equal(180.0m, r.TargetLowPrice);
        }

        [Fact]
        public void FullPayload_DisplayHelpers()
        {
            var r = YahooFinanceStockDataService.ParseAnalystRatings("AAPL", FullFixture, FetchedAt)!;

            Assert.Equal("6 SB · 23 B · 14 H · 2 S · 2 SS", r.CountsDisplay);
            Assert.Equal("2.0 · Buy · 43 analysts", r.ConsensusDisplay);
            Assert.Equal("Low $180.00 · Mean $205.50 · High $250.00", r.TargetRangeDisplay);
            Assert.True(r.HasCounts);
            Assert.True(r.HasTargets);

            // Upside is only computed once the VM injects a current price.
            Assert.Equal("", r.TargetUpsideDisplay);
            r.CurrentPrice = 183.00m;
            Assert.Equal("+12.3% vs $183.00", r.TargetUpsideDisplay);
        }

        [Fact]
        public void SnakeCaseKey_IsTitleCasedForDisplay()
        {
            const string json = """
            { "quoteSummary": { "result": [ { "financialData": { "recommendationKey": "strong_buy" } } ], "error": null } }
            """;
            var r = YahooFinanceStockDataService.ParseAnalystRatings("NVDA", json, FetchedAt);
            Assert.NotNull(r);
            Assert.Equal("Strong Buy", r!.RecommendationKeyDisplay);
        }

        [Fact]
        public void MissingTrendModule_StillReturnsFinancialData()
        {
            const string json = """
            {
              "quoteSummary": {
                "result": [
                  {
                    "financialData": {
                      "recommendationMean": { "raw": 1.5 },
                      "recommendationKey": "strong_buy",
                      "numberOfAnalystOpinions": { "raw": 12 }
                    }
                  }
                ],
                "error": null
              }
            }
            """;
            var r = YahooFinanceStockDataService.ParseAnalystRatings("MSFT", json, FetchedAt);

            Assert.NotNull(r);
            Assert.Equal(0, r!.TotalRatings);
            Assert.False(r.HasCounts);
            Assert.Equal("", r.CountsDisplay);
            Assert.Equal(1.5, r.RecommendationMean);
            Assert.Equal(12, r.NumberOfAnalystOpinions);
            Assert.Null(r.TargetMeanPrice);
            Assert.False(r.HasTargets);
        }

        [Fact]
        public void MissingFinancialData_StillReturnsCounts()
        {
            const string json = """
            {
              "quoteSummary": {
                "result": [
                  {
                    "recommendationTrend": {
                      "trend": [ { "period": "0m", "strongBuy": 1, "buy": 2, "hold": 3, "sell": 0, "strongSell": 0 } ]
                    }
                  }
                ],
                "error": null
              }
            }
            """;
            var r = YahooFinanceStockDataService.ParseAnalystRatings("KO", json, FetchedAt);

            Assert.NotNull(r);
            Assert.Equal(6, r!.TotalRatings);
            Assert.Null(r.RecommendationMean);
            Assert.Equal("", r.RecommendationKey);
            Assert.Equal("", r.TargetRangeDisplay);
        }

        [Theory]
        [InlineData("not json at all")]
        [InlineData("{}")]
        [InlineData("""{ "quoteSummary": { "result": [], "error": null } }""")]
        [InlineData("""{ "quoteSummary": { "result": null, "error": { "code": "Not Found" } } }""")]
        [InlineData("""{ "quoteSummary": { "result": [ {} ], "error": null } }""")]
        public void MalformedOrEmptyPayload_ReturnsNull(string json)
        {
            var r = YahooFinanceStockDataService.ParseAnalystRatings("XXXX", json, FetchedAt);
            Assert.Null(r);
        }

        [Fact]
        public void ZeroCountsAndEmptyFinancialData_ReturnsNull()
        {
            // A row exists but carries nothing usable — treat as "no analyst data".
            const string json = """
            {
              "quoteSummary": {
                "result": [
                  {
                    "recommendationTrend": { "trend": [ { "period": "0m", "strongBuy": 0, "buy": 0, "hold": 0, "sell": 0, "strongSell": 0 } ] },
                    "financialData": { "maxAge": 86400 }
                  }
                ],
                "error": null
              }
            }
            """;
            var r = YahooFinanceStockDataService.ParseAnalystRatings("ZZZZ", json, FetchedAt);
            Assert.Null(r);
        }
    }
}
