using System;
using System.Collections.Generic;
using StockPicker.Models;

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

        // ── Daily-picks cache ─────────────────────────────────────────────────

        /// <summary>
        /// The trading day these cached picks were generated for, in ISO format (yyyy-MM-dd).
        /// Empty string means no valid cache.
        /// </summary>
        public string DailyPicksDate { get; set; } = string.Empty;

        /// <summary>Cached daily picks for <see cref="DailyPicksDate"/>.</summary>
        public List<DayPick> DailyPicks { get; set; } = new();

        // ── Market index cache ────────────────────────────────────────────────

        /// <summary>Last-fetched market index snapshots for instant display on startup.</summary>
        public List<MarketIndexSnapshot> MarketIndexCache { get; set; } = new();
    }

    /// <summary>
    /// Persisted snapshot of one market index (DOW, S&amp;P 500, etc.).
    /// </summary>
    public class MarketIndexSnapshot
    {
        public string   Symbol       { get; set; } = string.Empty;
        public decimal? Price        { get; set; }
        public decimal? DayChange    { get; set; }
        public double?  DayChangePct { get; set; }
        public DateTime FetchedAt    { get; set; }
    }
}
