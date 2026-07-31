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
        /// <summary>
        /// Raw strategy score the action was derived from. Unlike <see cref="Confidence"/>
        /// (which saturates at 1.0 once |score| ≥ 3), the raw score keeps discriminating
        /// between strong picks — use it for ranking, never the saturated confidence.
        /// </summary>
        public double               Score      { get; set; }
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
        public double?  TargetHitProbability { get; set; }
        public double?  ExpectedDaysToTarget { get; set; }
        public double?  MedianDaysToTarget   { get; set; }
        public int?     TargetHitSampleSize  { get; set; }

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
        public decimal? DayOpen         { get; set; }   // session open price
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

        // ── Cash / fundamental data (Yahoo + Finnhub two-pass) ───────────────
        /// <summary>Total cash and equivalents on the balance sheet (from Yahoo Finance totalCash).</summary>
        public decimal? TotalCash         { get; set; }

        /// <summary>
        /// Total debt to equity ratio (from Finnhub series.annual.totalDebtToEquity).
        /// Units unverified — assumed ratio (e.g. 1.5 = 150 % D/E).
        /// Populated in the background Finnhub two-pass for the top 20 recommendations only.
        /// </summary>
        public double?  DebtToEquity      { get; set; }

        /// <summary>
        /// Net debt to total equity (from Finnhub series.annual.netDebtToTotalEquity).
        /// Negative = net cash position.  Same two-pass population caveat as DebtToEquity.
        /// </summary>
        public double?  NetDebtToEquity   { get; set; }

        /// <summary>
        /// Return on equity stored as a raw Finnhub fraction (e.g. 0.15 = 15 % ROE).
        /// Display helper multiplies by 100 so the format string is in one place.
        /// Same two-pass population caveat as DebtToEquity.
        /// </summary>
        public double?  ReturnOnEquityPct { get; set; }

        // ── Analyst price target (Yahoo quoteSummary two-pass) ───────────────
        /// <summary>
        /// Mean 12-month analyst price target (Yahoo financialData.targetMeanPrice).
        ///
        /// Populated only for the top recommendations by the background enrichment pass:
        /// quoteSummary accepts one symbol per request, so it cannot be batched the way the
        /// main quote call is. Rows outside that set leave this null and display blank.
        /// </summary>
        public decimal? TargetMeanPrice { get; set; }

        /// <summary>How many analysts contribute to <see cref="TargetMeanPrice"/>.</summary>
        public int? NumberOfAnalystOpinions { get; set; }

        /// <summary>
        /// Upside/downside from the current price to the mean 1-year target, in percent.
        /// Positive means analysts see the stock higher than it trades today.
        /// Null when either price is missing.
        /// </summary>
        public double? TargetDeltaPct =>
            (TargetMeanPrice.HasValue && LastPrice.HasValue && LastPrice.Value != 0)
                ? (double)((TargetMeanPrice.Value - LastPrice.Value) / LastPrice.Value * 100m)
                : (double?)null;

        /// <summary>
        /// Cash as a percentage of market cap.  Null when either TotalCash or MarketCap
        /// is unavailable.  Used by <see cref="StockPicker.Services.FundamentalScreen"/>.
        /// </summary>
        public double? CashToMktCapPct =>
            (TotalCash.HasValue && MarketCap.HasValue && MarketCap.Value != 0)
                ? (double)TotalCash.Value / MarketCap.Value * 100.0
                : (double?)null;

        // ── Options Greeks ────────────────────────────────────────────────────
        /// <summary>Implied volatility of the near-term ATM option (fraction, e.g. 0.30 = 30%).</summary>
        public double?  ImpliedVolatility { get; set; }
        /// <summary>Theta — time-decay cost per day in $ for the near-term ATM option.</summary>
        public double?  Theta             { get; set; }

        // ── Upcoming catalyst ─────────────────────────────────────────────────
        /// <summary>Next scheduled earnings date, when the data source reports one.</summary>
        public DateTime? NextEarningsDate { get; set; }

        /// <summary>Calendar days until the next earnings report; null when unknown or past.</summary>
        public int? DaysToEarnings =>
            NextEarningsDate.HasValue && NextEarningsDate.Value.Date >= DateTime.Today
                ? (NextEarningsDate.Value.Date - DateTime.Today).Days
                : (int?)null;

        /// <summary>
        /// Position of the current price within the 52-week range (0 = at the low,
        /// 100 = at the high). Null when the range or price is unavailable.
        /// </summary>
        public double? Week52PositionPct =>
            (LastPrice.HasValue && Week52High.HasValue && Week52Low.HasValue &&
             Week52High.Value > Week52Low.Value)
                ? (double)((LastPrice.Value - Week52Low.Value) /
                           (Week52High.Value - Week52Low.Value)) * 100.0
                : null;

        /// <summary>Implied volatility formatted as a percentage string, e.g. "32.5%".</summary>
        public string ImpliedVolatilityPctDisplay =>
            ImpliedVolatility.HasValue ? $"{ImpliedVolatility.Value * 100.0:F1}%" : "";

        /// <summary>Theta formatted with two decimal places, e.g. "-0.08".</summary>
        public string ThetaDisplay =>
            Theta.HasValue ? $"{Theta.Value:F2}" : "";

        // ── Fundamental display helpers ────────────────────────────────────────
        // Finnhub units verified 2026-06-25 against a live key (see FinnhubFundamentals).
        // All formatting is kept in one place so a future unit change is a one-line edit.

        /// <summary>
        /// True when this stock passes the cash-heavy &amp; low-debt screen. Exposed as a
        /// property (not just the <see cref="StockPicker.Services.FundamentalScreen"/> method)
        /// so the grid can SORT on it — a sortable column groups qualifying stocks together
        /// without hiding everything else the way the filter toggle does.
        ///
        /// Thresholds deliberately stay in FundamentalScreen; this only delegates, so there is
        /// still exactly one place to change them.
        /// </summary>
        public bool IsCashHeavyLowDebt =>
            StockPicker.Services.FundamentalScreen.IsCashHeavyLowDebt(this);

        /// <summary>Tick for stocks passing the cash-heavy &amp; low-debt screen, else empty.</summary>
        public string CashHeavyLowDebtDisplay => IsCashHeavyLowDebt ? "✓" : "";

        /// <summary>Mean 1-year analyst target, e.g. "205.50". Empty when unavailable.</summary>
        public string TargetMeanDisplay =>
            TargetMeanPrice.HasValue ? $"{TargetMeanPrice.Value:F2}" : "";

        /// <summary>
        /// Signed upside to the mean target, e.g. "+18.3%" or "-4.2%". Empty when unavailable.
        /// </summary>
        public string TargetDeltaDisplay =>
            TargetDeltaPct.HasValue
                ? (TargetDeltaPct.Value >= 0 ? $"+{TargetDeltaPct.Value:F1}%" : $"{TargetDeltaPct.Value:F1}%")
                : "";

        /// <summary>Cash/MktCap as a percentage string, e.g. "18.3%". Empty when null.</summary>
        public string CashToMktCapDisplay =>
            CashToMktCapPct.HasValue ? $"{CashToMktCapPct.Value:F1}%" : "";

        /// <summary>
        /// Debt-to-equity formatted as a ratio with two decimal places, e.g. "1.35".
        /// Verified unit: raw ratio (e.g. 1.35 = 135 % D/E). Empty when null.
        /// </summary>
        public string DebtToEquityDisplay =>
            DebtToEquity.HasValue ? $"{DebtToEquity.Value:F2}" : "";

        /// <summary>
        /// Net debt-to-equity formatted as a ratio with two decimal places, e.g. "-0.20".
        /// Negative values indicate a net cash position. Empty when null.
        /// </summary>
        public string NetDebtToEquityDisplay =>
            NetDebtToEquity.HasValue ? $"{NetDebtToEquity.Value:F2}" : "";

        /// <summary>
        /// Return on equity as a percentage string, e.g. "15.4%".
        /// Verified: Finnhub roe is a fraction (0.41 = 41 %); multiplied by 100 here.
        /// Empty when null.
        /// </summary>
        public string RoeDisplay =>
            ReturnOnEquityPct.HasValue ? $"{ReturnOnEquityPct.Value * 100.0:F1}%" : "";

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
        public string TargetHitProbabilityDisplay =>
            TargetHitProbability.HasValue ? $"{TargetHitProbability.Value * 100:F0}%" : "";
        public string ExpectedDaysToTargetDisplay =>
            ExpectedDaysToTarget.HasValue ? $"{ExpectedDaysToTarget.Value:F1} d" : "";
        public string MedianDaysToTargetDisplay =>
            MedianDaysToTarget.HasValue ? $"{MedianDaysToTarget.Value:F1} d" : "";
        public string TargetHitSampleSizeDisplay =>
            TargetHitSampleSize.HasValue && TargetHitSampleSize.Value > 0 ? TargetHitSampleSize.Value.ToString() : "";

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
