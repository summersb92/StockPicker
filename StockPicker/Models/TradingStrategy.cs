namespace StockPicker.Models
{
    /// <summary>
    /// A named trading strategy that can be selected by the user.
    /// Drives which indicators the analysis layer computes, how the
    /// recommendation layer thresholds them, and — via <see cref="HoldingPeriod"/> —
    /// the buy/sell dates proposed on each recommendation.
    /// </summary>
    /// <remarks>
    /// STUB — currently just a label + holding-period tag. Real implementations should either:
    ///   (a) Add a <c>Type</c> discriminator and branch in the analysis/recommendation
    ///       services via a strategy-pattern switch, OR
    ///   (b) Split into concrete subclasses (MomentumStrategy, MeanReversionStrategy, etc.)
    ///       each with its own parameters (lookback period, overbought threshold, stop %, etc.).
    /// Option (b) scales better and is what the Researcher should recommend before the
    /// real analysis work begins.
    /// </remarks>
    public class TradingStrategy
    {
        /// <summary>Stable identifier used when persisting the user's selection.</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>Display name shown in the Strategy ComboBox.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Short description shown under the selection for context.</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// How long positions opened by this strategy are meant to be held.
        /// Used by the recommendation service to compute BuyDate / SellDate.
        /// </summary>
        public HoldingPeriod HoldingPeriod { get; set; } = HoldingPeriod.Unspecified;

        // TODO: add strategy-specific parameters when we move beyond the stub:
        //   public int LookbackDays { get; set; }
        //   public decimal EntryThreshold { get; set; }
        //   public decimal StopLossPercent { get; set; }
        //   public decimal TakeProfitPercent { get; set; }
        // Consider a parameter dictionary if you want it data-driven:
        //   public Dictionary<string, object> Parameters { get; } = new();

        public override string ToString() => Name;
    }
}
