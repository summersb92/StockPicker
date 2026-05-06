using System.Collections.Generic;
using System.Threading.Tasks;
using StockPicker.Models;

namespace StockPicker.Services
{
    /// <summary>
    /// Generates a ranked list of intraday "Stock of the Day" picks from cached
    /// price history and live quote data.  No network calls are made — all scoring
    /// is performed against the data already fetched by the main scan pipeline.
    /// </summary>
    public interface IDayPickService
    {
        /// <summary>
        /// Score every stock in <paramref name="universe"/> using intraday signals
        /// derived from <paramref name="history"/> and <paramref name="summaries"/>,
        /// then return the top picks (usually 5–10) sorted by score descending.
        /// </summary>
        /// <param name="universe">Full list of stocks in the current scan universe.</param>
        /// <param name="history">Merged daily OHLCV bars keyed by symbol (90-day window).</param>
        /// <param name="summaries">Live quote data keyed by symbol.</param>
        /// <param name="nameLookup">Optional name/sector override map from the universe.</param>
        Task<IReadOnlyList<DayPick>> GenerateAsync(
            IReadOnlyList<Stock>                                     universe,
            IReadOnlyDictionary<string, IReadOnlyList<StockQuote>>   history,
            IReadOnlyDictionary<string, QuoteSummary>                summaries,
            IReadOnlyDictionary<string, (string Name, string Sector)>? nameLookup);
    }
}
