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
        /// <param name="mode">
        /// <see cref="EarningsScanMode.Upcoming"/> (default) keeps the forward-looking behaviour
        /// described above. <see cref="EarningsScanMode.JustReported"/> instead returns stocks
        /// that reported within <paramref name="lookbackDays"/>, scored as rebound candidates on
        /// selloff size, analyst upside, and EPS beat. Margin figures are not computed in that
        /// mode — there is no pending event to hold a leveraged position into.
        /// </param>
        /// <param name="lookbackDays">
        /// How many days back to look when <paramref name="mode"/> is
        /// <see cref="EarningsScanMode.JustReported"/>. Ignored otherwise.
        /// </param>
        /// <remarks>
        /// The last two parameters are optional so existing callers compile unchanged and keep
        /// their current forward-looking behaviour.
        /// </remarks>
        Task<IReadOnlyList<EarningsPick>> GenerateAsync(
            IReadOnlyList<Stock>                                       universe,
            IReadOnlyDictionary<string, IReadOnlyList<StockQuote>>     history,
            IReadOnlyDictionary<string, QuoteSummary>                  summaries,
            IReadOnlyDictionary<string, (string Name, string Sector)>? nameLookup,
            int     windowDays,
            decimal targetUpPercent,
            bool    useMargin,
            decimal marginPercent,
            decimal marginRatePercent,
            EarningsScanMode mode = EarningsScanMode.Upcoming,
            int lookbackDays = 5);
    }
}
