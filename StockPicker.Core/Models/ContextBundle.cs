using System;
using System.Collections.Generic;
using StockPicker.Services;

namespace StockPicker.Models
{
    /// <summary>
    /// A snapshot of everything the app currently knows, handed to
    /// <see cref="Services.ContextExportService"/> to be written as an
    /// LLM-consumable context bundle.
    ///
    /// This is a pure carrier: callers own (and have already computed) all of the
    /// data — the export service does NO fetching, scanning, or recalculation.
    ///
    /// IMMUTABILITY: every list carries the whitelist DTO records from
    /// <see cref="Services.ContextProjections"/>, projected by the CALLER at
    /// bundle-construction time (on the UI thread in the desktop app). Nothing on
    /// this type aliases a live domain object, so a deferred (debounced) export can
    /// never observe a torn, half-mutated snapshot.
    ///
    /// SECURITY: this type deliberately carries no <see cref="UserSettings"/>
    /// reference. Only the whitelisted values below (e.g. the enabled-source
    /// names, copied into a fresh list) ever reach the exporter, so API keys
    /// can never leak into an exported file.
    /// </summary>
    public class ContextBundle
    {
        /// <summary>Current strategy recommendations shown in the main grid (whitelist-projected).</summary>
        public List<RecommendationExport> Recommendations { get; set; } = new();

        /// <summary>Upcoming-earnings candidates from the earnings scanner (whitelist-projected).</summary>
        public List<EarningsExport> Earnings { get; set; } = new();

        /// <summary>Intraday stock-of-the-day picks (whitelist-projected).</summary>
        public List<DayPickExport> DayPicks { get; set; } = new();

        /// <summary>Positions the user currently holds (whitelist-projected).</summary>
        public List<PositionExport> Positions { get; set; } = new();

        /// <summary>Full transaction ledger (buys, sells, deposits, withdrawals; whitelist-projected).</summary>
        public List<TransactionExport> Transactions { get; set; } = new();

        /// <summary>Un-invested cash on hand.</summary>
        public decimal CashBalance { get; set; }

        /// <summary>
        /// Latest computed portfolio performance (whitelist-projected), or null when the
        /// caller has not computed real performance yet — null makes the exporter skip
        /// (and remove) performance.json instead of writing misleading $0 values.
        /// </summary>
        public PerformanceExport? Performance { get; set; }

        /// <summary>The markdown News briefing, exported verbatim.</summary>
        public string NewsBriefingMarkdown { get; set; } = string.Empty;

        /// <summary>When the underlying market data was last fetched (null before the first scan).</summary>
        public DateTime? DataFetchTime { get; set; }

        /// <summary>
        /// Names of the enabled data sources. Callers must pass a COPY of the list —
        /// never a live reference into <see cref="UserSettings"/>.
        /// </summary>
        public List<string> EnabledSources { get; set; } = new();

        /// <summary>Human-readable description of the scan universe (e.g. "S&amp;P 500 (~503 stocks)").</summary>
        public string UniverseDescription { get; set; } = string.Empty;

        /// <summary>Name of the strategy the recommendations were generated with.</summary>
        public string StrategyName { get; set; } = string.Empty;

        /// <summary>When this bundle was assembled (local time).</summary>
        public DateTime GeneratedAt { get; set; } = DateTime.Now;

        // ── App-state snapshot ("what's going on right now") ──────────────────
        // These describe the user's current focus/attention rather than the data
        // itself, and are written to app-state.json so an LLM can answer
        // "what am I looking at / why is this highlighted?" (see LLM-CONTEXT doc §3.3).

        /// <summary>Id of the strategy currently selected (e.g. "momentum").</summary>
        public string ActiveStrategy { get; set; } = string.Empty;

        /// <summary>Display name of the currently selected strategy (e.g. "Momentum (Quick)").</summary>
        public string ActiveStrategyName { get; set; } = string.Empty;

        /// <summary>Human-readable scan universe (e.g. "Dow 30 (~30 stocks)").</summary>
        public string Universe { get; set; } = string.Empty;

        /// <summary>Symbol of the row/chart the user has focused, or null when nothing is selected.</summary>
        public string? SelectedSymbol { get; set; }

        /// <summary>Which tab/pane is currently showing (e.g. "Recommendations").</summary>
        public string ActiveView { get; set; } = string.Empty;

        /// <summary>The active sort of the primary grid, or null when unknown.</summary>
        public SortState? Sort { get; set; }

        /// <summary>When the last scan/data fetch completed (UTC), or null before the first scan.</summary>
        public DateTime? LastScanUtc { get; set; }

        /// <summary>Hours since the last scan at bundle-assembly time, or null before the first scan.</summary>
        public double? StalenessHours { get; set; }
    }

    /// <summary>A grid sort: the column being sorted on and whether it is descending.</summary>
    /// <param name="Column">The column/field the grid is sorted by.</param>
    /// <param name="Descending">True when the sort is descending (largest/latest first).</param>
    public sealed record SortState(string Column, bool Descending);
}
