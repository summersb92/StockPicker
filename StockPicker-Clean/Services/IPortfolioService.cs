using System.Collections.Generic;
using StockPicker.Models;

namespace StockPicker.Services
{
    /// <summary>
    /// Manages the user's Watch list (tracked but not owned) and Held list
    /// (currently-owned positions).
    /// </summary>
    /// <remarks>
    /// Designed so the in-memory stub and a persistent JSON-file implementation
    /// share the same contract. The ViewModel never knows which it's talking to.
    /// </remarks>
    public interface IPortfolioService
    {
        // --- Watch list ---

        /// <summary>Snapshot of the current watch list.</summary>
        IReadOnlyList<Recommendation> GetWatchList();

        /// <summary>
        /// Add a recommendation to the watch list. No-op if the symbol is already watched.
        /// </summary>
        void AddToWatch(Recommendation rec);

        /// <summary>Remove a symbol from the watch list.</summary>
        void RemoveFromWatch(string symbol);

        // --- Held positions ---

        /// <summary>Snapshot of currently-held positions.</summary>
        IReadOnlyList<HeldPosition> GetHeld();

        /// <summary>
        /// Mark a recommendation as held. In the stub the EntryPrice defaults to
        /// the recommendation's TargetPrice (or 0 if none) and the EntryDate to today —
        /// the UI will want to offer an entry dialog later so the user can enter
        /// real fills.
        /// </summary>
        void AddToHeld(Recommendation rec);

        /// <summary>Remove a position from the held list (sold / closed out).</summary>
        void RemoveFromHeld(string symbol);
    }
}
