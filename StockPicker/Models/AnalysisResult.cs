using System.Collections.Generic;

namespace StockPicker.Models
{
    /// <summary>
    /// The output of running analysis against a stock's price history.
    /// A numeric score combined with human-readable signals that explain the score.
    /// </summary>
    public class AnalysisResult
    {
        public string Symbol { get; set; } = string.Empty;

        /// <summary>
        /// Composite score. Convention: higher = more bullish, lower = more bearish.
        /// Range is up to the analysis implementation (e.g. -100 to +100, or 0 to 1).
        /// </summary>
        public double Score { get; set; }

        /// <summary>Individual indicator readings keyed by name (e.g. "RSI", "SMA50").</summary>
        public Dictionary<string, double> Indicators { get; } = new();

        /// <summary>Human-readable signals the analysis flagged (e.g. "Above 50-day SMA").</summary>
        public List<string> Signals { get; } = new();

        /// <summary>
        /// Empirical probability that a setup similar to the current one reached the
        /// configured target gain within the strategy's holding window.
        /// </summary>
        public double? TargetHitProbability { get; set; }

        /// <summary>
        /// Average number of trading days it took winning analogs to reach the target.
        /// </summary>
        public double? ExpectedDaysToTarget { get; set; }

        /// <summary>
        /// Median number of trading days it took winning analogs to reach the target.
        /// </summary>
        public double? MedianDaysToTarget { get; set; }

        /// <summary>
        /// Number of historical analog setups used to estimate the target stats.
        /// </summary>
        public int? TargetHitSampleSize { get; set; }
    }
}
