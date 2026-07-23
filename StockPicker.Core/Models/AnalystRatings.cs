using System;

namespace StockPicker.Models
{
    /// <summary>
    /// Wall Street analyst consensus data for a single symbol, fetched on demand from
    /// Yahoo Finance's quoteSummary endpoint (recommendationTrend + financialData
    /// modules). Refreshed at most once per day per symbol — the upstream data itself
    /// carries a 24-hour max-age.
    /// </summary>
    public class AnalystRatings
    {
        public string Symbol { get; set; } = string.Empty;

        // ── Rating counts (current-month "0m" trend bucket) ───────────────────
        public int StrongBuy  { get; set; }
        public int Buy        { get; set; }
        public int Hold       { get; set; }
        public int Sell       { get; set; }
        public int StrongSell { get; set; }

        // ── Consensus ─────────────────────────────────────────────────────────
        /// <summary>Mean recommendation on a 1–5 scale; lower is stronger (1 = Strong Buy).</summary>
        public double? RecommendationMean { get; set; }

        /// <summary>Yahoo's consensus label, e.g. "buy", "strong_buy", "hold".</summary>
        public string RecommendationKey { get; set; } = string.Empty;

        /// <summary>Number of analysts contributing to the consensus.</summary>
        public int? NumberOfAnalystOpinions { get; set; }

        // ── Price targets ─────────────────────────────────────────────────────
        public decimal? TargetMeanPrice   { get; set; }
        public decimal? TargetMedianPrice { get; set; }
        public decimal? TargetHighPrice   { get; set; }
        public decimal? TargetLowPrice    { get; set; }

        /// <summary>When the data was fetched (UTC) — drives the 24h cache TTL.</summary>
        public DateTime FetchedAtUtc { get; set; }

        /// <summary>
        /// Latest market price, injected by the ViewModel from its quote cache so the
        /// target displays can show upside/downside context. Never fetched here.
        /// </summary>
        public decimal? CurrentPrice { get; set; }

        // ── Computed helpers ──────────────────────────────────────────────────

        /// <summary>Total number of individual ratings in the counts row.</summary>
        public int TotalRatings => StrongBuy + Buy + Hold + Sell + StrongSell;

        /// <summary>True when the trend module returned at least one rating.</summary>
        public bool HasCounts => TotalRatings > 0;

        /// <summary>True when any price target is available.</summary>
        public bool HasTargets =>
            TargetMeanPrice.HasValue || TargetHighPrice.HasValue || TargetLowPrice.HasValue;

        // ── Display helpers (follow the HeldPosition *Display pattern) ────────

        /// <summary>Compact counts row, e.g. "6 SB · 23 B · 14 H · 2 S · 2 SS".</summary>
        public string CountsDisplay =>
            HasCounts ? $"{StrongBuy} SB · {Buy} B · {Hold} H · {Sell} S · {StrongSell} SS" : "";

        /// <summary>"strong_buy" → "Strong Buy", "buy" → "Buy", etc.</summary>
        public string RecommendationKeyDisplay
        {
            get
            {
                if (string.IsNullOrWhiteSpace(RecommendationKey)) return "";
                var parts = RecommendationKey.Split('_', StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < parts.Length; i++)
                    parts[i] = char.ToUpperInvariant(parts[i][0]) + parts[i][1..];
                return string.Join(" ", parts);
            }
        }

        /// <summary>One-line consensus summary, e.g. "2.0 · Buy · 43 analysts".</summary>
        public string ConsensusDisplay
        {
            get
            {
                var parts = new System.Collections.Generic.List<string>(3);
                if (RecommendationMean.HasValue) parts.Add($"{RecommendationMean.Value:F1}");
                if (!string.IsNullOrEmpty(RecommendationKeyDisplay)) parts.Add(RecommendationKeyDisplay);
                if (NumberOfAnalystOpinions is > 0) parts.Add($"{NumberOfAnalystOpinions} analysts");
                return string.Join(" · ", parts);
            }
        }

        /// <summary>Target range line, e.g. "Low $180.00 · Mean $205.50 · High $250.00".</summary>
        public string TargetRangeDisplay
        {
            get
            {
                var parts = new System.Collections.Generic.List<string>(3);
                if (TargetLowPrice.HasValue)  parts.Add($"Low ${TargetLowPrice.Value:F2}");
                if (TargetMeanPrice.HasValue) parts.Add($"Mean ${TargetMeanPrice.Value:F2}");
                if (TargetHighPrice.HasValue) parts.Add($"High ${TargetHighPrice.Value:F2}");
                return string.Join(" · ", parts);
            }
        }

        /// <summary>
        /// Mean target vs. the current price, e.g. "+12.3% vs $183.00" — empty when
        /// either price is missing.
        /// </summary>
        public string TargetUpsideDisplay
        {
            get
            {
                if (!TargetMeanPrice.HasValue || !CurrentPrice.HasValue || CurrentPrice.Value == 0)
                    return "";
                var pct = (double)((TargetMeanPrice.Value - CurrentPrice.Value) / CurrentPrice.Value * 100m);
                var sign = pct >= 0 ? "+" : "";
                return $"{sign}{pct:F1}% vs ${CurrentPrice.Value:F2}";
            }
        }
    }
}
