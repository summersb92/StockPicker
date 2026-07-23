using System;
using System.Collections.Generic;
using System.Linq;

namespace StockPicker.Reference
{
    /// <summary>
    /// The single canonical glossary for the app. Every field emitted by the
    /// whitelist export DTOs (<c>ContextProjections</c>), the technical indicators on
    /// <c>AnalysisResult</c>, and the strategy ids from <c>StrategyProvider</c> has an
    /// entry here. This same table feeds UI tooltips, the glossary panel,
    /// <c>glossary.json</c> in the context bundle, and the MCP <c>get_glossary</c> /
    /// <c>explain_term</c> tools.
    /// </summary>
    /// <remarks>
    /// A drift test (<c>GlossaryDriftTests</c>) asserts that every public property of
    /// every <c>*Export</c> record resolves to an entry here, so a new exported field
    /// without a definition fails the build rather than shipping undocumented.
    ///
    /// GUARDRAIL: entries are educational/definitional ONLY — they describe what a
    /// metric is and how it is computed, never what a user should do with it.
    /// </remarks>
    public static class Glossary
    {
        // Ordered so the glossary reads naturally (identity → signal → indicators →
        // risk → portfolio → earnings → strategies). Order is preserved in All.
        private static readonly IReadOnlyList<TermDefinition> _all = new List<TermDefinition>
        {
            // ── Identity / common ───────────────────────────────────────────────
            new("Symbol", "Ticker symbol", TermCategory.General,
                "The exchange ticker that identifies the stock (e.g. AAPL).",
                "The exchange ticker symbol identifying the security, e.g. AAPL for Apple."),
            new("CompanyName", "Company name", TermCategory.General,
                "Full company name behind the ticker.",
                "The full legal/common name of the company the ticker represents."),
            new("Sector", "Sector", TermCategory.General,
                "The market sector the company is classified in.",
                "The market sector the company is classified under (e.g. Technology, Energy)."),
            new("LastPrice", "Last price", TermCategory.Indicator,
                "Most recent traded price for the symbol.",
                "The most recent traded (or last-fetched) price for the symbol.",
                Range: "≥ 0"),
            new("Date", "Date", TermCategory.General,
                "Calendar date the row refers to.",
                "The calendar date the row refers to — e.g. when a transaction was recorded."),
            new("Type", "Transaction type", TermCategory.General,
                "Kind of ledger entry: Buy, Sell, Deposit, or Withdrawal.",
                "The kind of ledger entry: Buy, Sell, Deposit, or Withdrawal."),
            new("Note", "Note", TermCategory.General,
                "Free-text annotation on a transaction.",
                "A free-text annotation attached to a ledger transaction."),
            new("HasData", "Has data", TermCategory.General,
                "Whether this period has enough history to compute a return.",
                "True when the trailing window has enough price history to compute a return; false leaves the period blank rather than showing a misleading zero."),

            // ── Signal / recommendation ─────────────────────────────────────────
            new("Action", "Recommendation action", TermCategory.Signal,
                "Strong Buy / Buy / Hold / Sell classification from the strategy scan.",
                "The classification the strategy scan assigns to a symbol — Strong Buy, Buy, Hold, or Sell — derived from its analysis score."),
            new("Confidence", "Confidence", TermCategory.Signal,
                "0–1 model confidence in the recommendation.",
                "A 0–1 measure of how strongly the analysis supports the recommendation; higher means the underlying signals agreed more.",
                Range: "0–1"),
            new("Reasoning", "Reasoning", TermCategory.Signal,
                "Plain-language explanation of why the signal fired.",
                "A plain-language summary of the indicator readings that produced this recommendation."),
            new("TargetPrice", "Target price", TermCategory.Signal,
                "Price the strategy projects for the holding period.",
                "The price the strategy projects the symbol could reach over its intended holding period, based on the configured profit target."),
            new("BuyDate", "Buy date", TermCategory.Signal,
                "Date the strategy would open the position.",
                "The date the strategy would open the position (for period-based strategies such as the Monday-open momentum scan)."),
            new("SellDate", "Sell date", TermCategory.Signal,
                "Date the strategy would close the position.",
                "The date the strategy would close the position, given its holding-period rule."),
            new("Direction", "Direction", TermCategory.Signal,
                "Long or short bias for the day pick.",
                "Whether the intraday pick is biased Long (expecting a rise) or Short (expecting a fall)."),
            new("IntraDayScore", "Intraday score", TermCategory.Signal,
                "Day-pick ranking strength for a same-session trade.",
                "A ranking score for a same-session (intraday) pick; larger magnitudes indicate a stronger setup under the chosen day-pick strategy."),
            new("Target", "Target level", TermCategory.Signal,
                "Projected price objective for an intraday pick.",
                "The projected price objective for an intraday pick, used with the entry and stop to frame the trade."),
            new("TriggerReason", "Trigger reason", TermCategory.Signal,
                "Why the intraday pick was flagged.",
                "The specific condition that flagged the intraday pick (e.g. a momentum or breakout trigger)."),
            new("TargetHitProbability", "Target-hit probability", TermCategory.Signal,
                "Historical odds of reaching the target within the window.",
                "The empirical share of historical setups similar to the current one that reached the configured target gain within the strategy's holding window.",
                Range: "0–1"),
            new("ExpectedDaysToTarget", "Expected days to target", TermCategory.Signal,
                "Average trading days winning analogs took to hit target.",
                "The average number of trading days it took historically-similar winning setups to reach the target gain."),
            new("MedianDaysToTarget", "Median days to target", TermCategory.Signal,
                "Median trading days winning analogs took to hit target.",
                "The median number of trading days it took historically-similar winning setups to reach the target gain."),
            new("TargetHitSampleSize", "Target-hit sample size", TermCategory.Signal,
                "Count of historical analogs behind the target stats.",
                "The number of historical analog setups used to estimate the target-hit probability and timing stats; larger samples are more reliable."),

            // ── Technical indicators ────────────────────────────────────────────
            new("Score", "Analysis score", TermCategory.Indicator,
                "Composite bullish/bearish score from the analysis.",
                "A composite score from the analysis where, by convention, higher is more bullish and lower is more bearish; the exact scale depends on the analysis implementation."),
            new("Signals", "Signals", TermCategory.Indicator,
                "Human-readable indicator flags feeding the score.",
                "The list of human-readable indicator observations the analysis flagged (e.g. \"Above 50-day SMA\") that together produced the score."),
            new("RSI", "RSI", TermCategory.Indicator,
                "Relative Strength Index momentum oscillator, 0–100.",
                "The Relative Strength Index, a momentum oscillator from 0 to 100 comparing the size of recent gains to recent losses. Conventionally >70 is called overbought and <30 oversold.",
                Range: "0–100"),
            new("RSI14", "RSI (14-day)", TermCategory.Indicator,
                "14-day Relative Strength Index, 0–100.",
                "The Relative Strength Index computed over a 14-session lookback: a 0–100 momentum oscillator comparing recent gains to recent losses.",
                Range: "0–100"),
            new("SMA20", "SMA (20-day)", TermCategory.Indicator,
                "Average closing price over the last 20 sessions.",
                "The simple moving average of the closing price over the trailing 20 trading sessions.",
                Formula: "sum(close, 20) / 20"),
            new("SMA50", "SMA (50-day)", TermCategory.Indicator,
                "Average closing price over the last 50 sessions.",
                "The simple moving average of the closing price over the trailing 50 trading sessions.",
                Formula: "sum(close, 50) / 50"),
            new("DayChangePct", "Day change", TermCategory.Indicator,
                "Percent price change since the prior close.",
                "The percentage change in price since the previous session's close.",
                Formula: "(price − priorClose) / priorClose × 100", Range: "%"),
            new("WeekReturnPct", "1-week return", TermCategory.Indicator,
                "Percent price change over the trailing week.",
                "The percentage change in price over the trailing one-week window.",
                Range: "%"),

            // ── Risk ────────────────────────────────────────────────────────────
            new("StopLoss", "Stop loss", TermCategory.Risk,
                "Exit level that caps the loss on a pick.",
                "A pre-defined exit price that bounds the loss on a pick if the trade moves against the intended direction."),
            new("RiskRewardRatio", "Risk : reward", TermCategory.Risk,
                "(Target − entry) ÷ (entry − stop). Higher is better.",
                "The ratio of projected gain to projected loss for a pick: the distance from entry to target divided by the distance from entry to stop.",
                Formula: "(target − entry) / (entry − stop)"),
            new("Leverage", "Leverage", TermCategory.Risk,
                "Position size ÷ your own equity in it.",
                "How much larger the position is than the equity you funded it with; 1× means no borrowing, 2× means half the position is borrowed.",
                Formula: "positionValue / equityInvested", Range: "≥ 1"),

            // ── Portfolio / holdings ────────────────────────────────────────────
            new("EntryPrice", "Entry price", TermCategory.Portfolio,
                "Price per share paid to open the position.",
                "The per-share price paid when the position was opened."),
            new("ShareCount", "Share count", TermCategory.Portfolio,
                "Number of shares currently held.",
                "The number of shares currently held in the position."),
            new("Shares", "Shares", TermCategory.Portfolio,
                "Number of shares in the transaction.",
                "The number of shares bought or sold in a ledger transaction."),
            new("Price", "Price", TermCategory.Portfolio,
                "Per-share price of the transaction.",
                "The per-share price at which a ledger transaction executed."),
            new("EntryDate", "Entry date", TermCategory.Portfolio,
                "Date the position was opened.",
                "The date the position was opened."),
            new("PlannedSellDate", "Planned sell date", TermCategory.Portfolio,
                "Date the position is intended to be closed.",
                "The date the position is intended to be closed, per its holding-period plan (may be empty for open-ended holds)."),
            new("UnrealizedGainPct", "Unrealized P&L %", TermCategory.Portfolio,
                "Percent change vs. entry on an open position.",
                "The percentage change in value of an open position relative to its entry price; \"unrealized\" because the position has not been sold.",
                Formula: "(lastPrice − entryPrice) / entryPrice × 100", Range: "%"),
            new("BoughtOnMargin", "On margin", TermCategory.Portfolio,
                "Position partly funded with borrowed money.",
                "True when the position was partly funded with borrowed money (margin) rather than entirely with your own cash."),
            new("MarginPercent", "Margin percent", TermCategory.Portfolio,
                "Share of the position funded by borrowing.",
                "The percentage of the position's cost that was funded by borrowing rather than your own equity.",
                Range: "0–100%"),
            new("MarginInterestRatePercent", "Margin interest rate", TermCategory.Portfolio,
                "Annual interest rate charged on borrowed funds.",
                "The annual interest rate charged on the borrowed (margin) portion of the position.",
                Range: "%"),
            new("EquityInvested", "Equity invested", TermCategory.Portfolio,
                "Your own cash in the position (excl. borrowing).",
                "The amount of your own cash committed to the position, excluding any borrowed (margin) funds."),
            new("InterestAccrued", "Interest accrued", TermCategory.Portfolio,
                "Margin interest owed to date on the position.",
                "The margin interest that has accumulated on the borrowed portion of the position since it was opened."),
            new("ReturnOnEquityPct", "Return on equity", TermCategory.Portfolio,
                "Gain measured against your equity, not position size.",
                "The position's gain measured against the equity you invested rather than the full position value; margin magnifies this relative to the unlevered return.",
                Range: "%"),
            new("GrossAmount", "Gross amount", TermCategory.Portfolio,
                "Shares × price before cash-flow direction.",
                "The gross value of a transaction — shares multiplied by price — before accounting for the direction of cash flow.",
                Formula: "shares × price"),
            new("CashDelta", "Cash change", TermCategory.Portfolio,
                "Signed change to cash from the transaction.",
                "The signed change to your cash balance from a transaction: negative for buys and withdrawals, positive for sells and deposits."),
            new("RealizedGain", "Realized gain", TermCategory.Portfolio,
                "Profit/loss locked in by a completed sale.",
                "The profit or loss locked in by a completed sale, measured against the cost basis of the shares sold; empty for non-sale transactions."),
            new("OnMargin", "On margin", TermCategory.Portfolio,
                "Whether the transaction used borrowed funds.",
                "True when the transaction involved borrowed (margin) funds."),
            new("CostBasis", "Cost basis", TermCategory.Portfolio,
                "Total amount paid for current holdings.",
                "The total amount paid to acquire the current holdings, used as the baseline for gain calculations."),
            new("MarketValue", "Market value", TermCategory.Portfolio,
                "Current worth of holdings at live prices.",
                "The current worth of all holdings valued at the latest available prices."),
            new("CashBalance", "Cash balance", TermCategory.Portfolio,
                "Un-invested cash on hand.",
                "The amount of un-invested cash currently in the account."),
            new("TotalValue", "Total value", TermCategory.Portfolio,
                "Market value of holdings plus cash.",
                "The total account value: the market value of holdings plus the cash balance.",
                Formula: "marketValue + cashBalance"),
            new("TotalGain", "Total gain", TermCategory.Portfolio,
                "Overall profit/loss across the portfolio.",
                "The overall profit or loss across the portfolio measured against cost basis (in currency)."),
            new("TotalGainPct", "Total gain %", TermCategory.Portfolio,
                "Overall portfolio profit/loss as a percent.",
                "The overall portfolio profit or loss expressed as a percentage of cost basis.",
                Range: "%"),
            new("PositionCount", "Position count", TermCategory.Portfolio,
                "Number of open positions.",
                "The number of distinct open positions in the portfolio."),
            new("AsOf", "As of", TermCategory.Portfolio,
                "Timestamp the performance snapshot was computed.",
                "The timestamp at which the performance snapshot was computed."),
            new("Periods", "Trailing periods", TermCategory.Portfolio,
                "Week/month/quarter/year return breakdown.",
                "The set of trailing-window returns (e.g. week, month, quarter, year) that make up the performance breakdown."),
            new("Label", "Period label", TermCategory.Portfolio,
                "Name of a trailing period (e.g. \"1 Month\").",
                "The name of a trailing-window period in the performance breakdown, e.g. \"1 Week\" or \"1 Year\"."),
            new("StartDate", "Period start date", TermCategory.Portfolio,
                "Start date of a trailing period.",
                "The date a trailing performance period begins."),
            new("StartValue", "Period start value", TermCategory.Portfolio,
                "Portfolio value at the start of the period.",
                "The portfolio value at the start of a trailing performance period."),
            new("CurrentValue", "Period current value", TermCategory.Portfolio,
                "Portfolio value at the end of the period.",
                "The portfolio value at the end of a trailing performance period (usually now)."),
            new("ChangePct", "Period change %", TermCategory.Portfolio,
                "Percent change over the trailing period.",
                "The percentage change in portfolio value over a trailing period.",
                Formula: "(currentValue − startValue) / startValue × 100", Range: "%"),

            // ── Earnings ────────────────────────────────────────────────────────
            new("NextEarningsDate", "Next earnings date", TermCategory.Earnings,
                "Date of the company's next scheduled earnings report.",
                "The date the company is next scheduled to report earnings."),
            new("DaysUntilEarnings", "Days to earnings", TermCategory.Earnings,
                "Sessions until the next reported earnings date.",
                "The number of days until the company's next scheduled earnings report."),
            new("LikelihoodScore", "Earnings likelihood", TermCategory.Earnings,
                "0–100 rank of an upcoming-earnings candidate.",
                "A 0–100 score ranking how well an upcoming-earnings candidate matches the scanner's criteria; higher ranks appear first.",
                Range: "0–100"),
            new("MeetsThreshold", "Meets threshold", TermCategory.Earnings,
                "Whether the pick clears the configured upside threshold.",
                "True when the earnings candidate's expected upside clears the configured target-up threshold for the scan."),
            new("ExpectedMovePct", "Expected move", TermCategory.Earnings,
                "Implied one-move size around the earnings date.",
                "The size of the price move the market implies around the earnings date, expressed as a percentage.",
                Range: "%"),
            new("MomentumPct", "Momentum", TermCategory.Earnings,
                "Recent trend strength feeding the earnings rank.",
                "A percentage measure of the stock's recent price trend strength, one of the inputs to the earnings likelihood score.",
                Range: "%"),
            new("HoldingPeriod", "Holding period", TermCategory.Strategy,
                "Intended time in the trade (Quick / Short / Long).",
                "The intended time the strategy holds a position: Quick (days), Short (weeks to months), or Long (years)."),

            // ── Strategies (ids from StrategyProvider) ──────────────────────────
            new("momentum", "Momentum (Quick)", TermCategory.Strategy,
                "Buys recent outperformers; opens Monday, closes Friday.",
                "A strategy that favors stocks which have outperformed over a recent lookback window, holding only within the week to avoid weekend exposure."),
            new("mean-reversion", "Mean Reversion (Short)", TermCategory.Strategy,
                "Favors names that sold off far from their average.",
                "A strategy that favors stocks which have sold off unusually far from their average price, on the premise that price tends to revert toward the mean over weeks to months."),
            new("breakout", "Breakout (Short)", TermCategory.Strategy,
                "Favors moves above recent resistance on volume.",
                "A strategy that favors stocks breaking above recent resistance on above-average volume, held while the trend persists."),
            new("value", "Value (Long)", TermCategory.Strategy,
                "Favors statistically cheap names (low P/E, P/B).",
                "A fundamental strategy that favors statistically cheap stocks — low price-to-earnings and price-to-book with positive earnings. It uses today's fundamentals, so it is excluded from point-in-time backtests."),
            new("52w-high", "52-Week High (Short)", TermCategory.Strategy,
                "Favors strength near the 52-week high.",
                "A strategy based on the documented momentum anomaly that stocks trading within a few percent of their 52-week high tend to keep outperforming (George & Hwang 2004)."),
            new("buy-and-hold", "Buy & Hold (Long)", TermCategory.Strategy,
                "Accumulate strong names and hold long-term.",
                "A strategy that accumulates fundamentally strong names and holds them for years, exiting only when the original thesis breaks."),
        };

        /// <summary>All glossary entries, in canonical display order.</summary>
        public static IReadOnlyList<TermDefinition> All => _all;

        // Case-insensitive index for fast lookup by key.
        private static readonly IReadOnlyDictionary<string, TermDefinition> _byKey =
            _all.ToDictionary(d => d.Key, StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Looks up a definition by <paramref name="key"/> (case-insensitive).
        /// Returns false and a null <paramref name="def"/> when no entry exists.
        /// </summary>
        public static bool TryGet(string key, out TermDefinition? def)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                def = null;
                return false;
            }
            return _byKey.TryGetValue(key, out def);
        }

        /// <summary>Returns all entries in the given <paramref name="c"/> category, in canonical order.</summary>
        public static IEnumerable<TermDefinition> ByCategory(TermCategory c) =>
            _all.Where(d => d.Category == c);
    }
}
