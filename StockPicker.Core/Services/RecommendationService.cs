using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using StockPicker.Models;

namespace StockPicker.Services
{
    /// <summary>
    /// STUB implementation — naive threshold mapping from <see cref="AnalysisResult.Score"/>
    /// to an action bucket, plus date computation based on the strategy's holding period.
    /// </summary>
    /// <remarks>
    /// INTENT — what this stub will become:
    ///   The final stage of the pipeline. Takes the raw per-stock analyses and turns them
    ///   into a prioritized list of trades, using <see cref="ScanContext"/> for guardrails.
    ///
    /// IMPLEMENTATION NOTES:
    ///   • Score → Action thresholds should be calibrated against backtested data,
    ///     not hard-coded numbers like below.
    ///   • Use <see cref="ScanContext.TargetProfitMarginPercent"/> to filter or re-rank:
    ///       - Drop picks whose expected move (based on ATR or recent volatility) is
    ///         smaller than the target.
    ///       - Or compute TargetPrice as (currentPrice * (1 + targetProfit/100)) and
    ///         only return the pick if a plausible path to that target exists.
    ///   • Diversify: don't return N picks from the same sector.
    ///   • Cap the result set (e.g. top 5 Buys, top 2 Sells) so the user isn't overwhelmed.
    ///   • Buy/sell dates below are CALENDAR-BASED PLACEHOLDERS. Real strategies should
    ///     replace the Short/Long sell dates with signal-driven exits (trailing stop,
    ///     target hit, indicator cross).
    /// </remarks>
    public class RecommendationService : IRecommendationService
    {
        private readonly ITradingCalendar _calendar;

        public RecommendationService() : this(new TradingCalendar()) { }

        public RecommendationService(ITradingCalendar calendar)
        {
            _calendar = calendar;
        }

        public Task<IReadOnlyList<Recommendation>> GenerateAsync(
            IReadOnlyList<AnalysisResult> analyses,
            ScanContext context)
        {
            var today = DateTime.Today;

            var recs = analyses
                .Select(a => ToRecommendation(a, context, today))
                .OrderByDescending(r => r.Confidence)    // highest conviction first (100 % → 0 %)
                .ThenBy(r => ActionSortKey(r.Action))    // within same confidence: StrongBuy → StrongSell
                .ThenBy(r => r.Symbol)                   // tiebreak alphabetically
                .ToList();

            return Task.FromResult<IReadOnlyList<Recommendation>>(recs);
        }

        private Recommendation ToRecommendation(AnalysisResult a, ScanContext context, DateTime today)
        {
            // TODO: replace with real thresholds calibrated against backtest data.
            var action = a.Score switch
            {
                >=  2.0 => RecommendationAction.StrongBuy,
                >=  0.5 => RecommendationAction.Buy,
                <= -2.0 => RecommendationAction.StrongSell,
                <= -0.5 => RecommendationAction.Sell,
                _       => RecommendationAction.Hold
            };

            // TODO: use context.TargetProfitMarginPercent to:
            //   1. Set a realistic TargetPrice based on current price and target %.
            //   2. Downgrade picks whose expected move < target.
            var reasoning = a.Signals.Count > 0
                ? string.Join("; ", a.Signals)
                : "[stub] No signals generated";

            reasoning += $"  (strategy: {context.Strategy.Name}, target: {context.TargetProfitMarginPercent}%)";

            var (buy, sell) = ComputeTradeDates(context.Strategy.HoldingPeriod, today);

            // Pull indicator readings from AnalysisResult into flat Recommendation fields
            // so the DataGrid can bind to them directly.
            a.Indicators.TryGetValue("RSI14",       out var rsi14);
            a.Indicators.TryGetValue("WeekReturn%",  out var weekRet);
            a.Indicators.TryGetValue("SMA20",        out var sma20);
            a.Indicators.TryGetValue("SMA50",        out var sma50);
            a.Indicators.TryGetValue("VolumeTrend",  out var volTrend);
            a.Indicators.TryGetValue("LastClose",    out var lastClose);

            return new Recommendation
            {
                Symbol        = a.Symbol,
                Action        = action,
                Confidence    = Math.Min(1.0, Math.Abs(a.Score) / 3.0),
                Reasoning     = reasoning,
                BuyDate       = buy,
                SellDate      = sell,
                HoldingPeriod = context.Strategy.HoldingPeriod,

                // Analysis indicators
                RSI14         = rsi14    != 0 ? rsi14    : null,
                WeekReturnPct = weekRet  != 0 ? weekRet  : null,
                SMA20         = sma20    != 0 ? sma20    : null,
                SMA50         = sma50    != 0 ? sma50    : null,
                VolumeTrend   = volTrend != 0 ? volTrend : null,
                LastPrice     = lastClose != 0 ? (decimal?)lastClose : null,
            };
        }

        /// <summary>
        /// Compute suggested buy/sell dates based on the strategy's holding period.
        /// </summary>
        /// <remarks>
        /// STUB DATE RULES:
        ///   • Quick: buy upcoming Monday, sell that Friday. If today IS Monday,
        ///     use today; otherwise jump to next Monday so we never propose a
        ///     trade that's already underway.
        ///   • Short: buy next trading day, sell ~6 months later. Placeholder —
        ///     real strategies exit on signal, not calendar.
        ///   • Long: buy next trading day, sell ~2 years later. Same caveat.
        ///   • Unspecified: leave both null and let the caller decide.
        ///
        /// TODO: move the Short/Long horizons onto the strategy itself
        /// (e.g. TradingStrategy.TargetHoldDays) once strategies have parameters.
        /// </remarks>
        private (DateTime? buy, DateTime? sell) ComputeTradeDates(HoldingPeriod period, DateTime today)
        {
            switch (period)
            {
                case HoldingPeriod.Quick:
                {
                    var monday = _calendar.NextWeekStart(today);
                    var friday = _calendar.WeekEndFor(monday);
                    return (monday, friday);
                }

                case HoldingPeriod.Short:
                {
                    var buy = _calendar.NextTradingDay(today);
                    // Placeholder: ~6 months. Real exit is signal-driven.
                    return (buy, buy.AddMonths(6));
                }

                case HoldingPeriod.Long:
                {
                    var buy = _calendar.NextTradingDay(today);
                    // Placeholder: ~2 years. Real exit is signal-driven.
                    return (buy, buy.AddYears(2));
                }

                default:
                    return (null, null);
            }
        }

        private static int ActionSortKey(RecommendationAction action) => action switch
        {
            RecommendationAction.StrongBuy  => 0,
            RecommendationAction.Buy        => 1,
            RecommendationAction.Hold       => 2,
            RecommendationAction.Sell       => 3,
            RecommendationAction.StrongSell => 4,
            _                               => 5,
        };
    }
}
