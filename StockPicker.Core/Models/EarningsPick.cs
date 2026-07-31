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

        // ── Post-earnings (JustReported mode only) ────────────────────────────
        /// <summary>Which side of the earnings date this pick was found on.</summary>
        public EarningsScanMode Mode { get; set; } = EarningsScanMode.Upcoming;

        /// <summary>
        /// Calendar days since the announcement (0 = reported today). Only meaningful when
        /// <see cref="Mode"/> is <see cref="EarningsScanMode.JustReported"/>.
        /// </summary>
        public int DaysSinceEarnings { get; set; }

        /// <summary>
        /// Price change (%) from the close before the earnings date to the latest close —
        /// the market's verdict on the print. Large negative values are the "plummeted"
        /// signal the rebound screen hunts for.
        /// </summary>
        public double? PostEarningsMovePct { get; set; }

        /// <summary>Drop (%) from the highest close in the loaded history to the latest close.</summary>
        public double? DrawdownPct { get; set; }

        // ── Analyst target (enriched after the scan — one request per symbol) ──
        /// <summary>Mean 12-month analyst price target, when fetched.</summary>
        public decimal? TargetMeanPrice { get; set; }

        /// <summary>Upside (%) from the latest price to <see cref="TargetMeanPrice"/>.</summary>
        public double? TargetDeltaPct =>
            (TargetMeanPrice.HasValue && LastPrice.HasValue && LastPrice.Value != 0)
                ? (double)((TargetMeanPrice.Value - LastPrice.Value) / LastPrice.Value * 100m)
                : (double?)null;

        // ── EPS surprise (enriched after the scan) ────────────────────────────
        /// <summary>
        /// Reported-vs-expected EPS for the announcement, when a provider had it. Null means
        /// "not published yet" — Yahoo lags by a few days — NOT "did not beat".
        /// </summary>
        public EarningsSurprise? Surprise { get; set; }

        /// <summary>True when EPS beat the estimate; null when no provider has the figure.</summary>
        public bool? EpsBeat => Surprise?.Beat;

        /// <summary>
        /// Composite 0–100 rebound score for <see cref="EarningsScanMode.JustReported"/>:
        /// blends the size of the selloff, remaining analyst upside, and the EPS beat.
        /// Populated by the scan service; see its ScoreRebound method for the weights.
        /// </summary>
        public double OpportunityScore { get; set; }

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
        /// <summary>"3d ago" / "today" for a reported pick; empty in Upcoming mode.</summary>
        public string ReportedDisplay =>
            Mode != EarningsScanMode.JustReported ? ""
            : DaysSinceEarnings == 0 ? $"{NextEarningsDate:MMM d} (today)"
                                     : $"{NextEarningsDate:MMM d}  ({DaysSinceEarnings}d ago)";

        /// <summary>Signed post-earnings reaction, e.g. "-18.4%". Empty when unavailable.</summary>
        public string PostEarningsMoveDisplay =>
            PostEarningsMovePct.HasValue
                ? (PostEarningsMovePct.Value >= 0 ? $"+{PostEarningsMovePct.Value:F1}%"
                                                  : $"{PostEarningsMovePct.Value:F1}%")
                : "";

        /// <summary>Drawdown from the period high, e.g. "-23.1%". Empty when unavailable.</summary>
        public string DrawdownDisplay =>
            DrawdownPct.HasValue ? $"{DrawdownPct.Value:F1}%" : "";

        /// <summary>Mean 1-year target, e.g. "205.50". Empty when not fetched.</summary>
        public string TargetMeanDisplay =>
            TargetMeanPrice.HasValue ? $"{TargetMeanPrice.Value:F2}" : "";

        /// <summary>Signed upside to the mean target, e.g. "+34.2%". Empty when unavailable.</summary>
        public string TargetDeltaDisplay =>
            TargetDeltaPct.HasValue
                ? (TargetDeltaPct.Value >= 0 ? $"+{TargetDeltaPct.Value:F1}%"
                                             : $"{TargetDeltaPct.Value:F1}%")
                : "";

        /// <summary>"Beat +10.1%" / "Miss -3.4%" / "" when not published yet.</summary>
        public string EpsSurpriseDisplay =>
            Surprise is { HasVerdict: true }
                ? $"{Surprise.BeatDisplay} {Surprise.SurpriseDisplay}".Trim()
                : "";

        /// <summary>Rebound score, blank in Upcoming mode where it is not computed.</summary>
        public string OpportunityScoreDisplay =>
            Mode == EarningsScanMode.JustReported ? $"{OpportunityScore:F0}" : "";

        public string LeverageDisplay      => MarginApplied ? $"{Leverage:F1}×" : "";
        public string InterestCostDisplay  => MarginApplied ? $"{InterestCostPct:F2}%" : "";
        public string NetMarginReturnDisplay =>
            MarginApplied
                ? (NetMarginReturnPct >= 0 ? $"+{NetMarginReturnPct:F1}%" : $"{NetMarginReturnPct:F1}%")
                : "";
    }
}
