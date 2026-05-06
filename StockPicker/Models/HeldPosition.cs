using System;
using System.Text.Json.Serialization;

namespace StockPicker.Models
{
    /// <summary>
    /// A position the user currently owns. Distinct from a <see cref="Recommendation"/>
    /// because it carries actual execution data (entry price, entry date, share count)
    /// rather than the algorithm's suggested values.
    /// </summary>
    public class HeldPosition
    {
        public string Symbol { get; set; } = string.Empty;

        /// <summary>Records where the position originated — strategy name or "Daily Pick".</summary>
        public string SourceTag { get; set; } = string.Empty;

        /// <summary>Full company name, populated when position is added and persisted.</summary>
        public string CompanyName { get; set; } = string.Empty;

        /// <summary>Actual entry price the user paid (not the algorithm's target).</summary>
        public decimal EntryPrice { get; set; }

        /// <summary>Date the position was opened.</summary>
        public DateTime EntryDate { get; set; }

        /// <summary>Number of shares held.</summary>
        public int ShareCount { get; set; }

        /// <summary>
        /// Planned exit date copied from the originating recommendation.
        /// NULL for strategies without a calendar-based exit.
        /// </summary>
        public DateTime? PlannedSellDate { get; set; }

        /// <summary>
        /// Holding period the strategy intended — preserved so the UI can flag
        /// a Quick trade that's bled past Friday.
        /// </summary>
        public HoldingPeriod HoldingPeriod { get; set; } = HoldingPeriod.Unspecified;

        /// <summary>Free-form notes the user can attach to the position.</summary>
        public string Notes { get; set; } = string.Empty;

        // ── Live market data — populated by ViewModel, NOT persisted ─────────

        /// <summary>
        /// Latest market price injected by the ViewModel after each scan.
        /// Not serialised — always fetched fresh from the cache on startup.
        /// </summary>
        [JsonIgnore]
        public decimal? LastPrice { get; set; }

        // ── Computed P&L ─────────────────────────────────────────────────────

        [JsonIgnore]
        public decimal? CurrentValue =>
            ShareCount > 0 && LastPrice.HasValue ? LastPrice.Value * ShareCount : null;

        [JsonIgnore]
        public decimal? UnrealizedGain =>
            ShareCount > 0 && LastPrice.HasValue ? (LastPrice.Value - EntryPrice) * ShareCount : null;

        [JsonIgnore]
        public double? UnrealizedGainPct =>
            EntryPrice > 0 && LastPrice.HasValue
                ? (double)((LastPrice.Value - EntryPrice) / EntryPrice * 100m)
                : null;

        // ── Display helpers ───────────────────────────────────────────────────

        [JsonIgnore]
        public string CurrentValueDisplay =>
            CurrentValue.HasValue ? $"${CurrentValue.Value:N2}" : "";

        [JsonIgnore]
        public string UnrealizedGainDisplay
        {
            get
            {
                if (!UnrealizedGain.HasValue) return "";
                return UnrealizedGain.Value >= 0
                    ? $"+${UnrealizedGain.Value:N2}"
                    : $"-${Math.Abs((double)UnrealizedGain.Value):N2}";
            }
        }

        [JsonIgnore]
        public string UnrealizedGainPctDisplay =>
            UnrealizedGainPct.HasValue
                ? (UnrealizedGainPct.Value >= 0
                    ? $"+{UnrealizedGainPct.Value:F2}%"
                    : $"{UnrealizedGainPct.Value:F2}%")
                : "";

        /// <summary>True when position has a live price and is in profit; null when no price.</summary>
        [JsonIgnore]
        public bool? IsProfit =>
            UnrealizedGainPct.HasValue ? UnrealizedGainPct.Value >= 0 : (bool?)null;
    }
}
