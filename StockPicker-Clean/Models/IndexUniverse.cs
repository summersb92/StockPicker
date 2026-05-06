namespace StockPicker.Models
{
    /// <summary>
    /// The stock index (or watchlist) used as the scan universe.
    /// Determines which symbols the app considers each week.
    /// </summary>
    public enum IndexUniverse
    {
        /// <summary>Dow Jones Industrial Average — 30 large blue-chip US companies.</summary>
        Dow30,

        /// <summary>S&amp;P 100 — 100 largest S&amp;P 500 companies by market capitalisation.</summary>
        SP100,

        /// <summary>NASDAQ-100 — 100 largest non-financial companies listed on the NASDAQ.</summary>
        Nasdaq100,

        /// <summary>S&amp;P 500 — ~503 large-cap US companies (fetched live from a public dataset).</summary>
        SP500,
    }

    public static class IndexUniverseExtensions
    {
        /// <summary>Human-readable name shown in the Settings combo-box.</summary>
        public static string DisplayName(this IndexUniverse u) => u switch
        {
            IndexUniverse.Dow30    => "Dow Jones 30",
            IndexUniverse.SP100    => "S&P 100",
            IndexUniverse.Nasdaq100 => "NASDAQ 100",
            IndexUniverse.SP500    => "S&P 500",
            _                      => u.ToString()
        };

        /// <summary>Short description shown below the combo-box.</summary>
        public static string Description(this IndexUniverse u) => u switch
        {
            IndexUniverse.Dow30    => "30 blue-chip US companies — the most watched index.",
            IndexUniverse.SP100    => "100 largest S&P 500 companies by market cap — mega-caps only.",
            IndexUniverse.Nasdaq100 => "100 largest non-financial NASDAQ companies — tech-heavy.",
            IndexUniverse.SP500    => "~503 large-cap US companies — broadest coverage (fetched live).",
            _                      => string.Empty
        };

        /// <summary>
        /// Natural size of the index — the actual cap on how many stocks can be scanned.
        /// Used to validate / clamp the universe-size slider.
        /// </summary>
        public static int MaxSize(this IndexUniverse u) => u switch
        {
            IndexUniverse.Dow30     => 30,
            IndexUniverse.SP100     => 100,
            IndexUniverse.Nasdaq100 => 100,
            IndexUniverse.SP500     => 503,
            _                       => 503
        };
    }
}
