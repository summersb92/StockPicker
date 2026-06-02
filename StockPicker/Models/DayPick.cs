using System;

namespace StockPicker.Models
{
    /// <summary>Direction bias for an intraday pick.</summary>
    public enum DayPickDirection { Long, Short }

    /// <summary>
    /// A single intraday stock-of-the-day recommendation.
    /// These picks are intended to be opened and closed within the same trading session.
    /// </summary>
    public class DayPick
    {
        // ── Identity ──────────────────────────────────────────────────────────
        public string Symbol      { get; set; } = string.Empty;
        public string CompanyName { get; set; } = string.Empty;
        public string Sector      { get; set; } = string.Empty;

        // ── Direction ─────────────────────────────────────────────────────────
        public DayPickDirection Direction { get; set; } = DayPickDirection.Long;

        // ── Trade levels ──────────────────────────────────────────────────────
        /// <summary>Suggested intraday entry price (current last price at signal time).</summary>
        public decimal? EntryPrice { get; set; }
        /// <summary>Intraday stop-loss — 1.5× ATR below entry for Long, above for Short.</summary>
        public decimal? StopLoss   { get; set; }
        /// <summary>Intraday target — 2.5× ATR above entry for Long, below for Short.</summary>
        public decimal? Target     { get; set; }

        // ── Scoring signals ───────────────────────────────────────────────────
        /// <summary>Composite intraday score (higher = stronger pick).</summary>
        public double  IntraDayScore { get; set; }
        /// <summary>Normalized confidence derived from the composite intraday score.</summary>
        public double  Confidence    { get; set; }
        /// <summary>Today's volume / 3-month average daily volume.</summary>
        public double  VolumeRatio   { get; set; }
        /// <summary>Opening gap % vs previous session close. Negative = gap-down.</summary>
        public double  GapPct        { get; set; }
        /// <summary>ATR-14 as a percentage of price — proxy for intraday range.</summary>
        public double  AtrPct        { get; set; }
        /// <summary>14-period RSI at the time of signal generation.</summary>
        public double? RSI14         { get; set; }

        // ── Live data ─────────────────────────────────────────────────────────
        public decimal? DayOpen      { get; set; }
        public decimal? LastPrice    { get; set; }
        public double?  DayChangePct { get; set; }

        // ── Narrative ─────────────────────────────────────────────────────────
        /// <summary>Comma-separated list of signals that triggered this pick.</summary>
        public string TriggerReason { get; set; } = string.Empty;

        // ── Metadata ──────────────────────────────────────────────────────────
        public DateTime GeneratedAt { get; set; } = DateTime.Now;

        // ── Computed risk/reward ──────────────────────────────────────────────
        /// <summary>
        /// Ratio of potential profit to potential loss (Target − Entry) / (Entry − Stop).
        /// Returns null when trade levels are unavailable.
        /// </summary>
        public double? RiskRewardRatio =>
            (EntryPrice.HasValue && StopLoss.HasValue && Target.HasValue &&
             EntryPrice.Value != StopLoss.Value)
                ? Math.Abs((double)(Target.Value  - EntryPrice.Value)) /
                  Math.Abs((double)(EntryPrice.Value - StopLoss.Value))
                : null;

        // ── Display helpers ───────────────────────────────────────────────────
        public string DirectionDisplay   => Direction == DayPickDirection.Long ? "▲ Long" : "▼ Short";
        public string GapPctDisplay      => GapPct >= 0 ? $"+{GapPct:F2}%" : $"{GapPct:F2}%";
        public string DayChangePctDisplay =>
            DayChangePct.HasValue
                ? (DayChangePct >= 0 ? $"+{DayChangePct:F2}%" : $"{DayChangePct:F2}%")
                : "";
        public string VolumeRatioDisplay  => $"{VolumeRatio:F1}×";
        public string AtrPctDisplay       => $"{AtrPct:F2}%";
        public string RiskRewardDisplay   =>
            RiskRewardRatio.HasValue ? $"{RiskRewardRatio.Value:F1}:1" : "";
        public string ScoreDisplay        => $"{IntraDayScore:F1}";
        public string ConfidenceDisplay   => $"{Confidence:P0}";
    }
}
