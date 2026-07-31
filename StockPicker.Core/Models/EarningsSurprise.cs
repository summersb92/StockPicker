using System;

namespace StockPicker.Models
{
    /// <summary>
    /// The reported-vs-expected EPS outcome for a single earnings announcement.
    ///
    /// Two sources fill this in and they do NOT agree on units, so both are normalised here:
    ///   • Finnhub /stock/earnings   → surprisePercent already a percent (10.12 = +10.12%)
    ///   • Yahoo earningsHistory     → surprisePercent a fraction (0.1012 = +10.12%)
    /// Everything on this class is a percent. Fetchers are responsible for converting.
    ///
    /// Yahoo's earningsHistory lags: the quarter a company reported yesterday typically is not
    /// in it yet, so for very recent announcements Finnhub is the only source likely to answer.
    /// That is why <see cref="Source"/> is recorded — a blank surprise on a fresh report means
    /// "not published yet", not "did not beat".
    /// </summary>
    public class EarningsSurprise
    {
        public string Symbol { get; set; } = string.Empty;

        /// <summary>
        /// End date of the fiscal period being reported (not the announcement date).
        /// Used to pick the most recent entry when a source returns several quarters.
        /// </summary>
        public DateTime PeriodEnd { get; set; }

        /// <summary>Reported earnings per share.</summary>
        public double? EpsActual { get; set; }

        /// <summary>Consensus analyst estimate for the same period.</summary>
        public double? EpsEstimate { get; set; }

        /// <summary>
        /// Beat/miss size as a percent of the estimate, normalised across sources.
        /// Positive means the company earned more than expected.
        /// </summary>
        public double? SurprisePercent { get; set; }

        /// <summary>Which provider supplied this record, for display and diagnostics.</summary>
        public DataSourceType Source { get; set; }

        // ── Computed helpers ──────────────────────────────────────────────────

        /// <summary>
        /// True when EPS came in above the estimate. Falls back to the surprise percent when
        /// the raw actual/estimate pair is missing but the surprise figure is present.
        /// Null when neither is available — unknown, not a miss.
        /// </summary>
        public bool? Beat
        {
            get
            {
                if (EpsActual.HasValue && EpsEstimate.HasValue)
                    return EpsActual.Value > EpsEstimate.Value;
                if (SurprisePercent.HasValue)
                    return SurprisePercent.Value > 0;
                return null;
            }
        }

        /// <summary>True when a usable beat/miss verdict exists.</summary>
        public bool HasVerdict => Beat.HasValue;

        // ── Display helpers ───────────────────────────────────────────────────

        /// <summary>Signed surprise, e.g. "+10.1%" / "-3.4%". Empty when unavailable.</summary>
        public string SurpriseDisplay =>
            SurprisePercent.HasValue
                ? (SurprisePercent.Value >= 0 ? $"+{SurprisePercent.Value:F1}%"
                                              : $"{SurprisePercent.Value:F1}%")
                : "";

        /// <summary>"Beat" / "Miss" / "" when unknown.</summary>
        public string BeatDisplay => Beat switch
        {
            true  => "Beat",
            false => "Miss",
            _     => "",
        };

        /// <summary>Actual vs estimate, e.g. "1.57 vs 1.43". Empty when either is missing.</summary>
        public string EpsDisplay =>
            (EpsActual.HasValue && EpsEstimate.HasValue)
                ? $"{EpsActual.Value:F2} vs {EpsEstimate.Value:F2}"
                : "";
    }
}
