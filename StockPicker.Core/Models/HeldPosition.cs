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

        // ── Margin (persisted) ───────────────────────────────────────────────

        /// <summary>True when the shares were bought on margin (borrowed money).</summary>
        public bool BoughtOnMargin { get; set; }

        /// <summary>
        /// Initial margin requirement as a percent of the position funded with the
        /// investor's own equity. 50% → 2× leverage; 100% → cash. Ignored when
        /// <see cref="BoughtOnMargin"/> is false.
        /// </summary>
        public decimal MarginPercent { get; set; } = 50m;

        /// <summary>Annual interest rate (%) charged on the borrowed portion.</summary>
        public decimal MarginInterestRatePercent { get; set; }

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

        /// <summary>Gross gain on the shares, before any borrowing cost (price move × shares).</summary>
        [JsonIgnore]
        public decimal? UnrealizedGain =>
            ShareCount > 0 && LastPrice.HasValue ? (LastPrice.Value - EntryPrice) * ShareCount : null;

        /// <summary>Gross price return (%), ignoring leverage and interest.</summary>
        [JsonIgnore]
        public double? UnrealizedGainPct =>
            EntryPrice > 0 && LastPrice.HasValue
                ? (double)((LastPrice.Value - EntryPrice) / EntryPrice * 100m)
                : null;

        // ── Margin math ──────────────────────────────────────────────────────
        // For a cash position (BoughtOnMargin == false) every figure below reduces
        // to the plain values: leverage 1×, equity == cost basis, zero interest.

        /// <summary>Full position cost at entry (entry price × shares).</summary>
        [JsonIgnore]
        public decimal CostBasis => EntryPrice * ShareCount;

        /// <summary>Leverage multiple. 50% margin → 2×; cash → 1×.</summary>
        [JsonIgnore]
        public decimal Leverage =>
            BoughtOnMargin && MarginPercent > 0 ? Math.Round(100m / MarginPercent, 2) : 1m;

        /// <summary>The investor's own money in the position (cost basis for cash).</summary>
        [JsonIgnore]
        public decimal EquityInvested =>
            BoughtOnMargin ? CostBasis * (MarginPercent / 100m) : CostBasis;

        /// <summary>Amount borrowed from the broker (0 for cash positions).</summary>
        [JsonIgnore]
        public decimal BorrowedAmount =>
            BoughtOnMargin ? CostBasis - EquityInvested : 0m;

        /// <summary>Calendar days the position has been open.</summary>
        [JsonIgnore]
        public int DaysHeld =>
            EntryDate == default ? 0 : Math.Max(0, (DateTime.Today - EntryDate.Date).Days);

        /// <summary>Interest accrued on the borrowed amount so far (0 for cash positions).</summary>
        [JsonIgnore]
        public decimal InterestAccrued =>
            BoughtOnMargin
                ? BorrowedAmount * (MarginInterestRatePercent / 100m) * (DaysHeld / 365m)
                : 0m;

        /// <summary>Gross price gain net of accrued borrowing cost.</summary>
        [JsonIgnore]
        public decimal? NetUnrealizedGain =>
            UnrealizedGain.HasValue ? UnrealizedGain.Value - InterestAccrued : (decimal?)null;

        /// <summary>Leveraged, interest-net return on the equity actually invested (%).</summary>
        [JsonIgnore]
        public double? ReturnOnEquityPct =>
            EquityInvested > 0 && NetUnrealizedGain.HasValue
                ? (double)(NetUnrealizedGain.Value / EquityInvested * 100m)
                : (double?)null;

        /// <summary>The gain figure the UI headlines: leveraged &amp; interest-net on margin, else plain.</summary>
        [JsonIgnore]
        public decimal? EffectiveGain => BoughtOnMargin ? NetUnrealizedGain : UnrealizedGain;

        /// <summary>The gain % the UI headlines: return on equity on margin, else price return.</summary>
        [JsonIgnore]
        public double? EffectiveGainPct => BoughtOnMargin ? ReturnOnEquityPct : UnrealizedGainPct;

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

        private static string Money(decimal v) =>
            v >= 0 ? $"+${v:N2}" : $"-${Math.Abs(v):N2}";

        /// <summary>Headline gain ($) — leveraged &amp; interest-net on margin, plain otherwise.</summary>
        [JsonIgnore]
        public string EffectiveGainDisplay =>
            EffectiveGain.HasValue ? Money(EffectiveGain.Value) : "";

        /// <summary>Headline gain (%) — return on equity on margin, price return otherwise.</summary>
        [JsonIgnore]
        public string EffectiveGainPctDisplay =>
            EffectiveGainPct.HasValue
                ? (EffectiveGainPct.Value >= 0 ? $"+{EffectiveGainPct.Value:F2}%" : $"{EffectiveGainPct.Value:F2}%")
                : "";

        /// <summary>Leverage badge for the grid, e.g. "2×" — or "Cash" for an unlevered position.</summary>
        [JsonIgnore]
        public string LeverageDisplay => BoughtOnMargin ? $"{Leverage:0.#}×" : "Cash";

        [JsonIgnore] public string EquityInvestedDisplay  => $"${EquityInvested:N2}";
        [JsonIgnore] public string BorrowedDisplay        => $"${BorrowedAmount:N2}";
        [JsonIgnore] public string InterestAccruedDisplay => $"${InterestAccrued:N2}";

        /// <summary>One-line margin summary for the details pane, e.g. "2× · 50% margin · 12.5%/yr".</summary>
        [JsonIgnore]
        public string MarginSummaryDisplay =>
            BoughtOnMargin
                ? $"{Leverage:0.#}× · {MarginPercent:0.#}% margin · {MarginInterestRatePercent:0.#}%/yr"
                : "";

        /// <summary>True when there is margin to show — drives visibility of the margin section.</summary>
        [JsonIgnore]
        public bool HasMargin => BoughtOnMargin;

        /// <summary>
        /// True when position has a live price and the (leveraged, interest-net) return is
        /// positive; null when no price.
        /// </summary>
        [JsonIgnore]
        public bool? IsProfit =>
            EffectiveGainPct.HasValue ? EffectiveGainPct.Value >= 0 : (bool?)null;
    }
}
