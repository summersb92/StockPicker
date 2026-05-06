using System;

namespace StockPicker.Models
{
    /// <summary>
    /// The runtime configuration for a single weekly scan. Passed end-to-end through
    /// the pipeline (data fetch → analysis → recommendation) so every stage has
    /// the same view of what the user is asking for.
    /// </summary>
    /// <remarks>
    /// Design intent: as new tunables are added (position sizing, risk caps,
    /// time horizon, etc.), extend this class rather than adding parameters to
    /// service methods. Keeps signatures stable and makes it easy to persist
    /// a "scan preset" later.
    /// </remarks>
    public class ScanContext
    {
        /// <summary>Strategy the user selected for this scan.</summary>
        public TradingStrategy Strategy { get; set; } = new();

        /// <summary>
        /// Target weekly profit margin as a percent (e.g. 2.5 means 2.5%).
        /// The recommendation layer uses this to size positions or to filter
        /// picks that are unlikely to hit the target.
        /// </summary>
        public decimal TargetProfitMarginPercent { get; set; }

        /// <summary>Start of the week-of-interest window (inclusive).</summary>
        public DateTime WeekStart { get; set; }

        /// <summary>End of the week-of-interest window (inclusive).</summary>
        public DateTime WeekEnd { get; set; }

        // TODO: future tunables to consider:
        //   public decimal MaxRiskPercentPerTrade { get; set; }
        //   public int MaxPicksPerScan { get; set; }
        //   public bool DiversifyBySector { get; set; }
        //   public decimal AccountSize { get; set; }   // for position sizing
    }
}
