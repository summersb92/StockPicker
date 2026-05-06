using System.Collections.Generic;

namespace StockPicker.Models
{
    /// <summary>
    /// JSON payload for the portfolio persistence file.
    /// Serialised to %LOCALAPPDATA%\StockPicker\portfolio.json on every mutation
    /// and loaded on application startup.
    /// </summary>
    public class PortfolioData
    {
        /// <summary>Stocks the user is tracking but does not own.</summary>
        public List<Recommendation> WatchList { get; set; } = new();

        /// <summary>Stocks the user currently holds as open positions.</summary>
        public List<HeldPosition> Held { get; set; } = new();
    }
}
