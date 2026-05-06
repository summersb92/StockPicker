namespace StockPicker.Models
{
    /// <summary>
    /// Classifies how long a strategy intends to hold a position.
    /// Drives the BuyDate / SellDate computed on each recommendation.
    /// </summary>
    public enum HoldingPeriod
    {
        /// <summary>No specific holding period defined.</summary>
        Unspecified,

        /// <summary>Intraday — opened and closed within the same session.</summary>
        Intraday,

        /// <summary>Quick swing — Monday open to Friday close of the same week.</summary>
        Quick,

        /// <summary>Short-term — weeks to months, less than one year.</summary>
        Short,

        /// <summary>Long-term — more than one year, buy-and-hold.</summary>
        Long,
    }
}
