using System;

namespace StockPicker.Models
{
    /// <summary>
    /// A single stock with an upcoming earnings announcement inside the user's scan window.
    /// Carries a blended 0–100 likelihood score that the stock rises by the user's target %,
    /// plus optional margin-adjusted return figures.
    ///
    /// NOTE: the likelihood score is a heuristic estimate built from option-implied volatility,
    /// momentum, and recent drift — it is NOT a prediction and must not be treated as advice.
    /// </summary>
    public class EarningsPick
    {
        // ── Identity ──────────────────────────────────────────────────────────
        public string Symbol      { get; set; } = string.Empty;
        public string CompanyName { get; set; } = string.Empty;
        public string Sector      { get; set; } = string.Empty;

        // ── Earnings ──────────────────────────────────────────────────────────
        /// <summary>Next scheduled earnings announcement date.</summary>
        public DateTime NextEarningsDate { get; set; }
        /// <summary>Calendar days from today until the announcement (>= 0).</summary>
        public int DaysUntilEarnings { get; set; }

        // ── Market data ───────────────────────────────────────────────────────
        public decimal? LastPrice    { get; set; }
        public double?  DayChangePct { get; set; }
        /// <summary>At-the-money implied volatility (fraction, e.g. 0.30 = 30%) when available.</summary>
        public double?  ImpliedVolatility { get; set; }

        // ── Signals ───────────────────────────────────────────────────────────
        /// <summary>One-sigma expected move (%) between now and the earnings date.</summary>
        public double ExpectedMovePct { get; set; }
        /// <summary>20-day price momentum return (%).</summary>
        public double MomentumPct { get; set; }
        /// <summary>Short-term drift: last close vs SMA20 (%).</summary>
        public double DriftPct { get; set; }
        /// <summary>Composite 0–100 likelihood that the stock rises by the target % (estimate).</summary>
        public double LikelihoodScore { get; set; }
        /// <summary>True when the blended expected upside meets or exceeds the user's target %.</summary>
        public bool MeetsThreshold { get; set; }
        /// <summary>The target % this pick was evaluated against (for display).</summary>
        public decimal TargetUpPercent { get; set; }
        /// <summary>Comma-separated signals that produced the score.</summary>
        public string TriggerReason { get; set; } = string.Empty;

        // ── Margin (populated only when the margin toggle is on) ────────────────
        public bool    MarginApplied           { get; set; }
        /// <summary>Buying-power multiple = 100 / margin%. 50% → 2×.</summary>
        public double  Leverage                { get; set; }
        /// <summary>Interest cost over the holding window, as a % of equity.</summary>
        public double  InterestCostPct         { get; set; }
        /// <summary>Leveraged return on equity if the target move is realized, before interest (%).</summary>
        public double  GrossLeveragedReturnPct { get; set; }
        /// <summary>Net return on equity after subtracting margin interest (%).</summary>
        public double  NetMarginReturnPct      { get; set; }
        /// <summary>Underlying move (%) needed just to cover the margin interest.</summary>
        public double  BreakevenMovePct        { get; set; }

        // ── Metadata ──────────────────────────────────────────────────────────
        public DateTime GeneratedAt { get; set; } = DateTime.Now;

        // ── Display helpers ─────────────────────────────────────────────────────
        public string EarningsDateDisplay =>
            DaysUntilEarnings == 0 ? $"{NextEarningsDate:MMM d} (today)"
                                   : $"{NextEarningsDate:MMM d}  ({DaysUntilEarnings}d)";
        public string DaysUntilDisplay     => $"{DaysUntilEarnings}d";
        public string ExpectedMoveDisplay  => $"±{ExpectedMovePct:F1}%";
        public string ScoreDisplay         => $"{LikelihoodScore:F0}";
        public string MomentumDisplay       =>
            MomentumPct >= 0 ? $"+{MomentumPct:F1}%" : $"{MomentumPct:F1}%";
        public string FlagDisplay          =>
            MeetsThreshold ? $"✅ ≥{TargetUpPercent:0.#}%" : "—";
        public string DayChangePctDisplay  =>
            DayChangePct.HasValue
                ? (DayChangePct >= 0 ? $"+{DayChangePct:F2}%" : $"{DayChangePct:F2}%")
                : "";
        public string LeverageDisplay      => MarginApplied ? $"{Leverage:F1}×" : "";
        public string InterestCostDisplay  => MarginApplied ? $"{InterestCostPct:F2}%" : "";
        public string NetMarginReturnDisplay =>
            MarginApplied
                ? (NetMarginReturnPct >= 0 ? $"+{NetMarginReturnPct:F1}%" : $"{NetMarginReturnPct:F1}%")
                : "";
    }
}
