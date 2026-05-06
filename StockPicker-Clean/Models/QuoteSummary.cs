namespace StockPicker.Models
{
    /// <summary>
    /// Live market data for a single ticker, fetched from Yahoo Finance's
    /// v7/finance/quote endpoint.  All fields are nullable — Yahoo does not
    /// guarantee every field is present for every symbol.
    /// </summary>
    public class QuoteSummary
    {
        public string Symbol   { get; set; } = string.Empty;

        // ── Identity ──────────────────────────────────────────────────────────
        /// <summary>Full company name returned by the data source (e.g. "Apple Inc.").</summary>
        public string? LongName  { get; set; }
        /// <summary>Short display name (e.g. "Apple").</summary>
        public string? ShortName { get; set; }
        /// <summary>Industry/sector string returned by the data source (e.g. "Technology").</summary>
        public string? Sector    { get; set; }

        // ── Price ─────────────────────────────────────────────────────────────
        public decimal? Price             { get; set; }   // regularMarketPrice
        public decimal? PrevClose         { get; set; }   // regularMarketPreviousClose
        public decimal? DayOpen           { get; set; }   // regularMarketOpen
        public decimal? DayHigh           { get; set; }   // regularMarketDayHigh
        public decimal? DayLow            { get; set; }   // regularMarketDayLow
        public decimal? DayChange         { get; set; }   // regularMarketChange
        public double?  DayChangePct      { get; set; }   // regularMarketChangePercent (already a %)

        // ── Volume ────────────────────────────────────────────────────────────
        public long?    Volume            { get; set; }   // regularMarketVolume
        public long?    AvgVolume         { get; set; }   // averageVolume (3-month)

        // ── Valuation ─────────────────────────────────────────────────────────
        public long?    MarketCap         { get; set; }   // marketCap
        public double?  PERatio           { get; set; }   // trailingPE
        public double?  ForwardPE         { get; set; }   // forwardPE
        public double?  EPS               { get; set; }   // epsTrailingTwelveMonths
        public double?  PriceToBook       { get; set; }   // priceToBook

        // ── Range ─────────────────────────────────────────────────────────────
        public decimal? Week52High        { get; set; }   // fiftyTwoWeekHigh
        public decimal? Week52Low         { get; set; }   // fiftyTwoWeekLow
        public double?  Week52ChangePct   { get; set; }   // 52WeekChange (already a fraction → ×100)

        // ── Risk / Income ─────────────────────────────────────────────────────
        public double?  Beta              { get; set; }   // beta
        public double?  DividendYieldPct  { get; set; }   // trailingAnnualDividendYield ×100
        public double?  ShortRatio        { get; set; }   // shortRatio
    }
}
