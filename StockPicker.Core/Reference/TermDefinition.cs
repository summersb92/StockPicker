namespace StockPicker.Reference
{
    /// <summary>
    /// Broad grouping a <see cref="TermDefinition"/> belongs to, used to organize the
    /// glossary panel and to let callers filter definitions by area.
    /// </summary>
    public enum TermCategory
    {
        /// <summary>Trade signal / recommendation fields (action, confidence, targets).</summary>
        Signal,

        /// <summary>Technical indicators computed from price history (RSI, moving averages, …).</summary>
        Indicator,

        /// <summary>Risk-framing measures (stop loss, leverage, risk-reward).</summary>
        Risk,

        /// <summary>Portfolio / holdings accounting fields (cost basis, market value, P&amp;L).</summary>
        Portfolio,

        /// <summary>Named trading strategies the scanner can run.</summary>
        Strategy,

        /// <summary>Upcoming-earnings scan fields.</summary>
        Earnings,

        /// <summary>General terms that do not fit a more specific category.</summary>
        General,
    }

    /// <summary>
    /// One glossary entry — the single source of truth for what a term/field means in
    /// this app. <see cref="Tooltip"/> is the one-liner shown on hover; <see cref="Explanation"/>
    /// is the fuller paragraph used by the glossary panel and by any LLM reading the
    /// exported context bundle.
    /// </summary>
    /// <remarks>
    /// DEFINITIONAL ONLY. Every entry describes what a term <i>is</i> and how the app
    /// <i>computes</i> it — never what action to take. The app does not give investment
    /// advice, so no definition is prescriptive ("buy when…", "sell if…").
    /// </remarks>
    /// <param name="Key">Stable lookup key; matches the DTO field / UI label, e.g. <c>"RSI14"</c>.</param>
    /// <param name="Term">Human-readable display name, e.g. <c>"RSI (14-day)"</c>.</param>
    /// <param name="Category">Which <see cref="TermCategory"/> this entry belongs to.</param>
    /// <param name="Tooltip">Short one-liner (≤120 chars) shown on hover.</param>
    /// <param name="Explanation">One to three sentences for the panel and the LLM.</param>
    /// <param name="Formula">Optional formula / computation note, when a term is derived.</param>
    /// <param name="Range">Optional typical value range, e.g. <c>"0–100"</c> or <c>"0–1"</c>.</param>
    public sealed record TermDefinition(
        string       Key,
        string       Term,
        TermCategory Category,
        string       Tooltip,
        string       Explanation,
        string?      Formula = null,
        string?      Range   = null);
}
