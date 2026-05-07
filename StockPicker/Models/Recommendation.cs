using System;
using System.Collections.Generic;

namespace StockPicker.Models
{
    /// <summary>
    /// A single actionable recommendation — the terminal output of the pipeline.
    /// Carries both the strategy signal (Action, Confidence, Reasoning) and all
    /// enriched market data fetched from Yahoo Finance so the DataGrid has one
    /// flat object to bind against.
    /// </summary>
    public class Recommendation
    {
        // ── Identity ──────────────────────────────────────────────────────────
        public string Symbol      { get; set; } = string.Empty;
        /// <summary>Full company name, e.g. "Apple Inc."</summary>
        public string CompanyName { get; set; } = string.Empty;
        /// <summary>GICS sector, e.g. "Technology".</summary>
        public string Sector      { get; set; } = string.Empty;

        // ── Signal ────────────────────────────────────────────────────────────
        public RecommendationAction Action     { get; set; }
        public double               Confidence { get; set; }   // 0.0–1.0
        public string               Reasoning  { get; set; } = string.Empty;

        /// <summary>
        /// Numeric sort key for <see cref="Action"/> so WPF's DataGrid can sort the
        /// Action column correctly (enum names would sort alphabetically otherwise).
        /// StrongBuy = 0, Buy = 1, Hold = 2, Sell = 3, StrongSell = 4.
        /// </summary>
        public int ActionSortOrder => Action switch
        {
            RecommendationAction.StrongBuy  => 0,
            RecommendationAction.Buy        => 1,
            RecommendationAction.Hold       => 2,
            RecommendationAction.Sell       => 3,
            RecommendationAction.StrongSell => 4,
            _                               => 5,
        };

        // ── Trade dates ───────────────────────────────────────────────────────
        public DateTime?      BuyDate       { get; set; }
        public DateTime?      SellDate      { get; set; }
        public HoldingPeriod  HoldingPeriod { get; set; } = HoldingPeriod.Unspecified;

        // ── Data source provenance ────────────────────────────────────────────
        /// <summary>Which data sources contributed to this recommendation.</summary>
        public List<DataSourceType> ContributingSources { get; set; } = new();

        /// <summary>Short display string for the Source column in the grid.</summary>
        public string SourceDisplay =>
            ContributingSources.Count == 0 ? "" :
            ContributingSources.Count == 1 ? ContributingSources[0].ShortName() :
            $"Multi ({ContributingSources.Count})";

        // ── Strategy target ───────────────────────────────────────────────────
        public decimal? TargetPrice   { get; set; }
        public DateTime GeneratedAt   { get; set; } = DateTime.Now;

        // ── Analysis indicators (from AnalysisService) ────────────────────────
        public double?  RSI14          { get; set; }
        public double?  WeekReturnPct  { get; set; }
        public double?  SMA20          { get; set; }
        public double?  SMA50          { get; set; }
        public double?  VolumeTrend    { get; set; }
        public double?  VolumeRatio    { get; set; }   // current vol / avg vol
        public double?  GapPct         { get; set; }   // overnight gap %
        public double?  AtrPct         { get; set; }   // ATR as % of price
        public decimal? StopLoss       { get; set; }   // suggested stop-loss level

        // ── Live market data (from Yahoo Finance quote) ───────────────────────
        public decimal? LastPrice       { get; set; }   // regularMarketPrice
        public decimal? DayChange       { get; set; }   // $ change today
        public double?  DayChangePct    { get; set; }   // % change today
        public long?    Volume          { get; set; }   // today's volume
        public long?    AvgVolume       { get; set; }   // 3-month avg volume
        public long?    MarketCap       { get; set; }   // market capitalisation
        public double?  PERatio         { get; set; }   // trailing P/E
        public double?  ForwardPE       { get; set; }   // forward P/E
        public double?  EPS             { get; set; }   // trailing EPS
        public double?  PriceToBook     { get; set; }   // P/B ratio
        public decimal? Week52High      { get; set; }
        public decimal? Week52Low       { get; set; }
        public double?  Beta            { get; set; }
        public double?  DividendYieldPct { get; set; }  // already in %
        public double?  ShortRatio      { get; set; }

        // ── Options Greeks ────────────────────────────────────────────────────
        /// <summary>Implied volatility of the near-term ATM option (fraction, e.g. 0.30 = 30%).</summary>
        public double?  ImpliedVolatility { get; set; }
        /// <summary>Theta — time-decay cost per day in $ for the near-term ATM option.</summary>
        public double?  Theta             { get; set; }

        /// <summary>Implied volatility formatted as a percentage string, e.g. "32.5%".</summary>
        public string ImpliedVolatilityPctDisplay =>
            ImpliedVolatility.HasValue ? $"{ImpliedVolatility.Value * 100.0:F1}%" : "";

        /// <summary>Theta formatted with two decimal places, e.g. "-0.08".</summary>
        public string ThetaDisplay =>
            Theta.HasValue ? $"{Theta.Value:F2}" : "";

        // ── HeldPosition compatibility (used by Details pane shared template) ─
        public decimal? EntryPrice      { get; set; }
        public int?     ShareCount      { get; set; }
        public DateTime? EntryDate      { get; set; }
        public DateTime? PlannedSellDate { get; set; }
        public string   Notes           { get; set; } = string.Empty;

        // ── Origin tag ───────────────────────────────────────────────────────
        /// <summary>
        /// Records where this item was added from, e.g. "Momentum Swing",
        /// "Daily Pick", or "Value Strategy".  Shown in the Watch and Positions grids.
        /// </summary>
        public string SourceTag { get; set; } = string.Empty;

        // ── Watch-list tracking ───────────────────────────────────────────────
        /// <summary>
        /// The price at the moment the user clicked "Add to Watch".
        /// Stays fixed while LastPrice is updated on every scan refresh.
        /// </summary>
        public decimal?  WatchedPrice { get; set; }

        /// <summary>When the stock was added to the watch list.</summary>
        public DateTime? WatchedAt    { get; set; }

        /// <summary>
        /// % change from <see cref="WatchedPrice"/> to the current <see cref="LastPrice"/>.
        /// Null if either price is missing or zero.
        /// </summary>
        public double? WatchChangePct =>
            (WatchedPrice.HasValue && WatchedPrice.Value != 0 && LastPrice.HasValue)
                ? (double)((LastPrice.Value - WatchedPrice.Value) / WatchedPrice.Value * 100m)
                : null;

        public string WatchedPriceDisplay =>
            WatchedPrice.HasValue ? $"${WatchedPrice.Value:F2}" : "";

        public string WatchedAtDisplay =>
            WatchedAt.HasValue ? WatchedAt.Value.ToString("MMM d") : "";

        public string WatchChangePctDisplay =>
            WatchChangePct.HasValue
                ? (WatchChangePct >= 0 ? $"+{WatchChangePct:F2}%" : $"{WatchChangePct:F2}%")
                : "";

        /// <summary>
        /// True when price has risen since added to watch; false if fallen; null if no data.
        /// Drives the green/red row tint on the Watch tab.
        /// </summary>
        public bool? WatchIsUp =>
            WatchChangePct.HasValue ? WatchChangePct > 0 : (bool?)null;

        // ── Display-formatted helpers ─────────────────────────────────────────
        // These avoid XAML converter complexity for signed/scaled numbers.

        public string DayChangeDisplay =>
            DayChange.HasValue
                ? (DayChange >= 0 ? $"+${DayChange:F2}" : $"-${Math.Abs((double)DayChange.Value):F2}")
                : "";

        public string DayChangePctDisplay =>
            DayChangePct.HasValue
                ? (DayChangePct >= 0 ? $"+{DayChangePct:F2}%" : $"{DayChangePct:F2}%")
                : "";

        public string WeekReturnDisplay =>
            WeekReturnPct.HasValue
                ? (WeekReturnPct >= 0 ? $"+{WeekReturnPct:F2}%" : $"{WeekReturnPct:F2}%")
                : "";

        public string VolumeRatioDisplay => VolumeRatio.HasValue ? $"{VolumeRatio.Value:F2}×" : "";
        public string GapPctDisplay     => GapPct.HasValue    ? (GapPct >= 0 ? $"+{GapPct:F2}%" : $"{GapPct:F2}%") : "";
        public string AtrPctDisplay     => AtrPct.HasValue    ? $"{AtrPct.Value:F2}%"  : "";

        public string VolumeDisplay    => FormatLarge(Volume);
        public string AvgVolumeDisplay => FormatLarge(AvgVolume);
        public string MarketCapDisplay => FormatMarketCap(MarketCap);

        private static string FormatLarge(long? value)
        {
            if (!value.HasValue || value.Value == 0) return "";
            var v = value.Value;
            if (v >= 1_000_000_000) return $"{v / 1_000_000_000.0:F2}B";
            if (v >= 1_000_000)     return $"{v / 1_000_000.0:F1}M";
            if (v >= 1_000)         return $"{v / 1_000.0:F0}K";
            return v.ToString();
        }

        private static string FormatMarketCap(long? value)
        {
            if (!value.HasValue || value.Value == 0) return "";
            var v = value.Value;
            if (v >= 1_000_000_000_000) return $"${v / 1_000_000_000_000.0:F2}T";
            if (v >= 1_000_000_000)     return $"${v / 1_000_000_000.0:F1}B";
            if (v >= 1_000_000)         return $"${v / 1_000_000.0:F0}M";
            return $"${v:N0}";
        }
    }
}
