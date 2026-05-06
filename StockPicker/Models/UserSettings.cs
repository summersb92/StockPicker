using System.Collections.Generic;

namespace StockPicker.Models
{
    /// <summary>
    /// User preferences persisted across sessions.
    /// Serialised to %LOCALAPPDATA%\StockPicker\user_settings.json.
    /// </summary>
    public class UserSettings
    {
        // ── Column visibility ──────────────────────────────────────────────────
        /// <summary>
        /// Maps each column's Header string to its visibility state.
        /// Columns absent from the dictionary keep their compiled default.
        /// </summary>
        public Dictionary<string, bool> ColumnVisibility { get; set; } = new();

        // ── Column order ───────────────────────────────────────────────────────
        /// <summary>
        /// Maps each column's Header string to its saved DisplayIndex.
        /// Columns absent from the dictionary keep their XAML-defined default position.
        /// </summary>
        public Dictionary<string, int> ColumnOrder { get; set; } = new();

        // ── Sort state ─────────────────────────────────────────────────────────
        /// <summary>SortMemberPath of the last active sort column, or empty if none.</summary>
        public string SortColumn    { get; set; } = string.Empty;

        /// <summary>"Ascending" or "Descending".</summary>
        public string SortDirection { get; set; } = "Ascending";

        // ── Data sources ───────────────────────────────────────────────────────
        /// <summary>Enum names of enabled data sources. Defaults to Yahoo Finance only.</summary>
        public List<string> EnabledDataSources { get; set; } = new() { nameof(DataSourceType.YahooFinance) };

        /// <summary>API keys keyed by DataSourceType enum name.</summary>
        public Dictionary<string, string> ApiKeys { get; set; } = new();

        // ── Universe index ─────────────────────────────────────────────────────
        /// <summary>
        /// The selected stock index used as the scan universe.
        /// Stored as the enum name (e.g. "SP500") for forward-compatible JSON serialisation.
        /// </summary>
        public string SelectedIndex { get; set; } = nameof(IndexUniverse.SP500);

        // ── Analysis settings ──────────────────────────────────────────────────
        /// <summary>
        /// Minimum weekly return % the user considers a viable pick. Default 2.0%.
        /// </summary>
        public decimal TargetProfitMarginPercent { get; set; } = 2.0m;

        // ── Strategy persistence ───────────────────────────────────────────────
        /// <summary>
        /// The Name of the last selected strategy. Restored on next launch.
        /// Empty string means use the provider's default.
        /// </summary>
        public string LastStrategyName { get; set; } = string.Empty;
    }
}
