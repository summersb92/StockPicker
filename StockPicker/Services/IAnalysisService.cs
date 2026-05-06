using System.Collections.Generic;
using System.Threading.Tasks;
using StockPicker.Models;

namespace StockPicker.Services
{
    /// <summary>
    /// Converts raw price history into a scored <see cref="AnalysisResult"/>.
    /// This is where technical indicators, pattern recognition, or ML inference live.
    /// </summary>
    public interface IAnalysisService
    {
        /// <summary>
        /// Run analysis on a single symbol given its price history and the scan context.
        /// </summary>
        /// <param name="stock">The stock being analyzed.</param>
        /// <param name="history">Price history to feed the analysis.</param>
        /// <param name="context">
        /// Scan configuration — selected strategy, target profit margin, date window.
        /// Implementations should branch on <c>context.Strategy.Id</c> to produce
        /// strategy-appropriate signals.
        /// </param>
        Task<AnalysisResult> AnalyzeAsync(
            Stock stock,
            IReadOnlyList<StockQuote> history,
            ScanContext context);
    }
}
