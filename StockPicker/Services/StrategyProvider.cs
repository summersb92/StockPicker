using System.Collections.Generic;
using StockPicker.Models;

namespace StockPicker.Services
{
    /// <summary>
    /// STUB implementation — returns a hard-coded list of four strategies with
    /// their intended holding periods assigned.
    /// </summary>
    /// <remarks>
    /// TODO when filling this in:
    ///   - Decide on the real strategy taxonomy. Four stubs here are placeholders
    ///     chosen to represent the three holding-period classes plus a free slot.
    ///   - Once strategies have parameters, load them from
    ///     %AppData%/StockPicker/strategies.json so users can edit without rebuilding.
    ///   - If strategies become pluggable, replace this stub with a loader that
    ///     reflects over a "Strategies" folder and discovers implementations.
    /// </remarks>
    public class StrategyProvider : IStrategyProvider
    {
        private static readonly IReadOnlyList<TradingStrategy> _strategies = new List<TradingStrategy>
        {
            new()
            {
                Id = "momentum",
                Name = "Momentum (Quick)",
                Description = "Buys stocks that have outperformed over a recent lookback " +
                              "window. Opens Monday, closes Friday — no weekend exposure.",
                HoldingPeriod = HoldingPeriod.Quick
            },
            new()
            {
                Id = "mean-reversion",
                Name = "Mean Reversion (Short)",
                Description = "Buys stocks that have sold off unusually far from their " +
                              "average, betting on a snap-back over weeks to months.",
                HoldingPeriod = HoldingPeriod.Short
            },
            new()
            {
                Id = "breakout",
                Name = "Breakout (Short)",
                Description = "Buys stocks that break above recent resistance on " +
                              "above-average volume. Hold until the trend weakens.",
                HoldingPeriod = HoldingPeriod.Short
            },
            new()
            {
                Id = "buy-and-hold",
                Name = "Buy & Hold (Long)",
                Description = "Accumulate fundamentally strong names and hold for " +
                              "years — exit only on thesis break.",
                HoldingPeriod = HoldingPeriod.Long
            }
        };

        public IReadOnlyList<TradingStrategy> GetStrategies() => _strategies;

        /// <summary>
        /// Default to Mean Reversion — conservative and a reasonable starting point.
        /// TODO: revisit when real strategies are implemented; the default should be
        /// whichever has the best risk-adjusted backtest.
        /// </summary>
        public TradingStrategy GetDefault() => _strategies[1];
    }
}
