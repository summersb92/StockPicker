using System.Collections.Generic;
using System.Threading.Tasks;
using StockPicker.Models;

namespace StockPicker.Services
{
    /// <summary>
    /// Converts a batch of <see cref="AnalysisResult"/>s into a ranked list of
    /// actionable <see cref="Recommendation"/>s. This is where ranking, filtering,
    /// position sizing, and action-thresholding happen.
    /// </summary>
    public interface IRecommendationService
    {
        /// <summary>
        /// Generate the week's Buy / Sell / Hold recommendations from a batch of analyses.
        /// Implementations are expected to sort the output most-actionable-first.
        /// </summary>
        /// <param name="analyses">The per-stock analysis results produced upstream.</param>
        /// <param name="context">
        /// Scan configuration. The target profit margin gates/shapes the output —
        /// low-potential picks should be downgraded or filtered out.
        /// </param>
        Task<IReadOnlyList<Recommendation>> GenerateAsync(
            IReadOnlyList<AnalysisResult> analyses,
            ScanContext context);
    }
}
