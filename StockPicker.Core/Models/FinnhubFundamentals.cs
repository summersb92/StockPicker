namespace StockPicker.Models
{
    /// <summary>
    /// Fundamental ratio data fetched from the Finnhub /stock/metric endpoint.
    ///
    /// All fields are nullable — free-tier access may not return every metric,
    /// and some tickers have no coverage at all.
    ///
    /// UNIT NOTE: Finnhub ratio units are unverified (no live key at design time).
    /// On the first successful API call, raw values are logged via Debug.WriteLine
    /// so units can be confirmed. Display helpers in <see cref="StockPicker.Models.Recommendation"/>
    /// are intentionally kept in one place so the format string can be flipped without
    /// touching any other file.
    /// </summary>
    public class FinnhubFundamentals
    {
        /// <summary>
        /// Total debt to equity ratio from Finnhub series.annual.totalDebtToEquity.
        /// Units unverified — assumed to be a ratio (e.g. 1.5 = 150 % debt/equity).
        /// </summary>
        public double? DebtToEquity     { get; set; }

        /// <summary>
        /// Net debt to total equity from Finnhub series.annual.netDebtToTotalEquity.
        /// Negative values indicate net cash position.
        /// </summary>
        public double? NetDebtToEquity  { get; set; }

        /// <summary>
        /// Return on equity from Finnhub series.annual.roe.
        /// Units unverified — assumed to be a fraction (e.g. 0.15 = 15 % ROE).
        /// Stored raw; display helper multiplies by 100.
        /// </summary>
        public double? ReturnOnEquity   { get; set; }

        /// <summary>
        /// Current ratio (current assets / current liabilities).
        /// Available but not currently surfaced in the grid — kept for future use.
        /// </summary>
        public double? CurrentRatio     { get; set; }
    }
}
