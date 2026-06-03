using System.Collections.Generic;
using System.Threading.Tasks;
using StockPicker.Models;

namespace StockPicker.Services
{
    public interface IDayPickService
    {
        /// <summary>
        /// Score every stock in <paramref name="universe"/> using the requested
        /// <paramref name="strategy"/> and return the top picks sorted by score descending.
        /// </summary>
        /// <param name="universe">Stocks to evaluate (already capped to the desired size).</param>
        /// <param name="history">Merged daily OHLCV bars keyed by symbol.</param>
        /// <param name="summaries">Live quote data keyed by symbol.</param>
        /// <param name="nameLookup">Optional name/sector override map.</param>
        /// <param name="strategy">Scoring strategy to apply.</param>
        Task<IReadOnlyList<DayPick>> GenerateAsync(
            IReadOnlyList<Stock>                                       universe,
            IReadOnlyDictionary<string, IReadOnlyList<StockQuote>>     history,
            IReadOnlyDictionary<string, QuoteSummary>                  summaries,
            IReadOnlyDictionary<string, (string Name, string Sector)>? nameLookup,
            DayPickStrategy strategy = DayPickStrategy.Momentum);
    }
}
