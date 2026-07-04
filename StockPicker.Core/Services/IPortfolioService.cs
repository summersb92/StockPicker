using System;
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
        /// <summary>
        /// Raised when saving the portfolio file fails at runtime (disk full,
        /// permissions). The string is a short human-readable message.
        /// Subscribers should surface it — a silent persistence failure means the
        /// user believes a trade was recorded when it wasn't.
        /// </summary>
        event Action<string>? PersistenceError;

        /// <summary>
        /// Non-null when the portfolio file existed at startup but could not be
        /// parsed. The corrupt file is backed up alongside the original (never
        /// silently discarded); this message says where.
        /// </summary>
        string? StartupLoadError { get; }

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

        /// <summary>
        /// Adds a manually-entered position, or updates the existing position with the
        /// same symbol (case-insensitive). Used by the Add/Edit Position dialog.
        /// </summary>
        void UpsertHeld(HeldPosition position);

        /// <summary>Remove a position from the held list (sold / closed out).</summary>
        void RemoveFromHeld(string symbol);

        /// <summary>
        /// Sells a held position (full or partial) at <paramref name="sellPrice"/>: credits the
        /// net proceeds (gross less any repaid margin loan + accrued interest) to cash, records a
        /// Sell transaction with realized gain, and removes or reduces the position. Returns the
        /// recorded transaction, or null if the symbol isn't held.
        /// </summary>
        Transaction? SellHeld(string symbol, decimal sellPrice, int shares, System.DateTime date);

        // --- Cash ---

        /// <summary>Un-invested cash on hand, counted toward total portfolio value.</summary>
        decimal GetCash();

        /// <summary>Sets the cash balance directly (negative values clamped to zero). Does NOT log a transaction.</summary>
        void SetCash(decimal amount);

        /// <summary>Adds cash and records a Deposit transaction.</summary>
        void DepositCash(decimal amount, System.DateTime date, string note);

        /// <summary>Removes cash (clamped to the available balance) and records a Withdrawal transaction.</summary>
        void WithdrawCash(decimal amount, System.DateTime date, string note);

        // --- Ledger ---

        /// <summary>Full transaction ledger (chronological): buys, sells, deposits, withdrawals.</summary>
        IReadOnlyList<Transaction> GetTransactions();

        // --- Daily picks cache ---

        /// <summary>
        /// Returns the cached daily picks if they were generated for
        /// <paramref name="targetDate"/>, otherwise returns null.
        /// </summary>
        System.Collections.Generic.IReadOnlyList<DayPick>? GetCachedDayPicks(System.DateTime targetDate);

        /// <summary>Persists daily picks for <paramref name="targetDate"/> to disk.</summary>
        void SaveDayPicksCache(System.DateTime targetDate, System.Collections.Generic.IReadOnlyList<DayPick> picks);

        // --- Market index cache ---

        /// <summary>Returns the last-saved market index snapshots, or an empty list if none.</summary>
        IReadOnlyList<MarketIndexSnapshot> GetCachedMarketIndices();

        /// <summary>Persists fresh market index data so it shows instantly on next startup.</summary>
        void SaveMarketIndicesCache(IReadOnlyList<MarketIndexSnapshot> snapshots);
    }
}
