using System.Collections.Generic;
using StockPicker.Models;

namespace StockPicker.Services
{
    /// <summary>
    /// Supplies the list of strategies the user can pick from in the UI.
    /// </summary>
    /// <remarks>
    /// Decoupling this from the view model lets us swap the source later —
    /// hard-coded list now, a config file or plugin loader later.
    /// </remarks>
    public interface IStrategyProvider
    {
        /// <summary>Return all strategies available for selection.</summary>
        IReadOnlyList<TradingStrategy> GetStrategies();

        /// <summary>
        /// Return the strategy that should be pre-selected on first launch.
        /// Typically the safest/most-conservative option.
        /// </summary>
        TradingStrategy GetDefault();
    }
}
