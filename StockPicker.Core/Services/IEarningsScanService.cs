using System.Collections.Generic;
using System.Threading.Tasks;
using StockPicker.Models;

namespace StockPicker.Services
{
    public interface IEarningsScanService
    {
        /// <summary>
        /// Find every stock in <paramref name="universe"/> whose next earnings date falls within
        /// <paramref name="windowDays"/> days from today, score each for the likelihood of rising
        /// by <paramref name="targetUpPercent"/>, and (optionally) compute margin-adjusted returns.
        /// Results are sorted by likelihood descending, then by soonest earnings date.
        /// </summary>
        /// <param name="universe">Stocks to evaluate (already capped to the desired size).</param>
        /// <param name="history">Merged daily OHLCV bars keyed by symbol.</param>
        /// <param name="summaries">Live quote data keyed by symbol (must carry NextEarningsDate).</param>
        /// <param name="nameLookup">Optional name/sector override map.</param>
        /// <param name="windowDays">Only include earnings within this many days from today.</param>
        /// <param name="targetUpPercent">Target upside % the likelihood flag is measured against.</param>
        /// <param name="useMargin">When true, populate the margin-adjusted return fields.</param>
        /// <param name="marginPercent">Equity margin requirement % (leverage = 100 / marginPercent).</param>
        /// <param name="marginRatePercent">Annualized margin interest rate %.</param>
        Task<IReadOnlyList<EarningsPick>> GenerateAsync(
            IReadOnlyList<Stock>                                       universe,
            IReadOnlyDictionary<string, IReadOnlyList<StockQuote>>     history,
            IReadOnlyDictionary<string, QuoteSummary>                  summaries,
            IReadOnlyDictionary<string, (string Name, string Sector)>? nameLookup,
            int     windowDays,
            decimal targetUpPercent,
            bool    useMargin,
            decimal marginPercent,
            decimal marginRatePercent);
    }
}
