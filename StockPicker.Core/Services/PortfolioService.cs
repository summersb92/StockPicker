using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using StockPicker.Models;

namespace StockPicker.Services
{
    /// <summary>
    /// Persistent implementation of <see cref="IPortfolioService"/>.
    ///
    /// Data is stored in <c>%LOCALAPPDATA%\StockPicker\portfolio.json</c>.
    /// On startup the file is read synchronously (it's tiny, typically &lt; 50 KB).
    /// After every mutation the file is saved asynchronously using a tmp→rename
    /// pattern so a crash mid-write never corrupts the saved data.
    /// </summary>
    public class PortfolioService : IPortfolioService
    {
        // ── File paths ────────────────────────────────────────────────────────

        private static readonly string _folder =
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "StockPicker");

        private static readonly string _file =
            Path.Combine(_folder, "portfolio.json");

        // ── JSON options ──────────────────────────────────────────────────────
        // Enums as strings make the saved file human-readable and survive
        // reorderings of the enum members across app updates.

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented               = true,
            PropertyNameCaseInsensitive = true,
            Converters                  = { new JsonStringEnumConverter() },
        };

        // ── In-memory state ───────────────────────────────────────────────────

        private readonly List<Recommendation> _watch;
        private readonly List<HeldPosition>   _held;
        private decimal       _cash            = 0m;
        private readonly List<Transaction>    _transactions;
        private string        _dailyPicksDate  = string.Empty;
        private List<DayPick> _dailyPicks      = new();
        private List<MarketIndexSnapshot> _marketIndexCache = new();

        // ── Construction ─────────────────────────────────────────────────────

        public PortfolioService()
        {
            var data         = LoadFromDisk();
            _watch           = data.WatchList;
            _held            = data.Held;
            _cash            = data.CashBalance;
            _transactions    = data.Transactions ?? new List<Transaction>();
            _dailyPicksDate  = data.DailyPicksDate  ?? string.Empty;
            _dailyPicks      = data.DailyPicks      ?? new List<DayPick>();
            _marketIndexCache = data.MarketIndexCache ?? new List<MarketIndexSnapshot>();
        }

        // ── IPortfolioService — Watch ─────────────────────────────────────────

        public IReadOnlyList<Recommendation> GetWatchList() => _watch.ToList();

        public void AddToWatch(Recommendation rec)
        {
            if (_watch.Any(r => r.Symbol.Equals(rec.Symbol, StringComparison.OrdinalIgnoreCase)))
                return;

            _watch.Add(rec);
            SaveAsync();
        }

        public void RemoveFromWatch(string symbol)
        {
            int removed = _watch.RemoveAll(
                r => r.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase));

            if (removed > 0) SaveAsync();
        }

        // ── IPortfolioService — Held ──────────────────────────────────────────

        public IReadOnlyList<HeldPosition> GetHeld() => _held.ToList();

        public void AddToHeld(Recommendation rec)
        {
            if (_held.Any(h => h.Symbol.Equals(rec.Symbol, StringComparison.OrdinalIgnoreCase)))
                return;

            var pos = new HeldPosition
            {
                Symbol          = rec.Symbol,
                CompanyName     = rec.CompanyName,
                SourceTag       = rec.SourceTag,
                EntryPrice      = rec.LastPrice ?? rec.TargetPrice ?? 0m,
                EntryDate       = DateTime.Today,
                ShareCount      = 0,
                PlannedSellDate = rec.SellDate,
                HoldingPeriod   = rec.HoldingPeriod,
                Notes           = rec.Reasoning,
            };
            _held.Add(pos);
            LogBuy(pos, debitCash: true);   // ShareCount is 0 here, so outlay is 0 until edited

            SaveAsync();
        }

        public void UpsertHeld(HeldPosition position)
        {
            if (position is null || string.IsNullOrWhiteSpace(position.Symbol)) return;

            int idx = _held.FindIndex(
                h => h.Symbol.Equals(position.Symbol, StringComparison.OrdinalIgnoreCase));

            if (idx >= 0)
            {
                // Editing changes the investor's own money in the trade — move only the
                // difference in equity in/out of cash, then sync the ledger line.
                decimal equityDelta = position.EquityInvested - _held[idx].EquityInvested;
                if (equityDelta != 0m) _cash -= equityDelta;
                _held[idx] = position;
                SyncOpenBuy(position);
            }
            else
            {
                _held.Add(position);            // add new
                LogBuy(position, debitCash: true);
            }

            SaveAsync();
        }

        public void RemoveFromHeld(string symbol)
        {
            int idx = _held.FindIndex(h => h.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) return;

            _held.RemoveAt(idx);

            // A raw delete is "undo the mistaken entry": drop the open Buy line and refund
            // exactly the cash it pulled (its CashDelta), so cash and ledger stay consistent.
            for (int i = _transactions.Count - 1; i >= 0; i--)
            {
                var t = _transactions[i];
                if (t.Type == TransactionType.Buy &&
                    t.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase))
                {
                    _cash -= t.CashDelta;   // CashDelta is negative (an outlay) → refunds it
                    _transactions.RemoveAt(i);
                    break;
                }
            }

            SaveAsync();
        }

        public Transaction? SellHeld(string symbol, decimal sellPrice, int shares, DateTime date)
        {
            int idx = _held.FindIndex(h => h.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) return null;

            var pos        = _held[idx];
            int sellShares = shares <= 0 ? pos.ShareCount : Math.Min(shares, pos.ShareCount);
            if (sellShares <= 0) return null;

            decimal frac          = pos.ShareCount > 0 ? (decimal)sellShares / pos.ShareCount : 1m;
            decimal gross         = sellPrice * sellShares;
            decimal loanRepaid    = pos.BorrowedAmount  * frac;   // 0 for cash positions
            decimal interestPaid  = pos.InterestAccrued * frac;   // 0 for cash positions
            decimal netProceeds   = gross - loanRepaid - interestPaid;
            decimal equityCost    = pos.EquityInvested  * frac;
            decimal realizedGain  = netProceeds - equityCost;

            // Credit the investor's net proceeds to cash (may be negative for a position sold
            // below its loan + interest; the cash balance is allowed to go negative).
            _cash += netProceeds;

            // Reduce or close out the position.
            if (sellShares >= pos.ShareCount) _held.RemoveAt(idx);
            else                              pos.ShareCount -= sellShares;

            var txn = new Transaction
            {
                Date         = date.Date,
                Type         = TransactionType.Sell,
                Symbol       = pos.Symbol,
                CompanyName  = pos.CompanyName,
                Shares       = sellShares,
                Price        = sellPrice,
                CashDelta    = netProceeds,
                RealizedGain = realizedGain,
                OnMargin     = pos.BoughtOnMargin,
            };
            _transactions.Add(txn);

            SaveAsync();
            return txn;
        }

        // ── IPortfolioService — Cash ──────────────────────────────────────────

        public decimal GetCash() => _cash;

        public void SetCash(decimal amount)
        {
            var clamped = amount < 0m ? 0m : amount;
            if (_cash == clamped) return;
            _cash = clamped;
            SaveAsync();
        }

        public void DepositCash(decimal amount, DateTime date, string note)
        {
            if (amount <= 0m) return;
            _cash += amount;
            _transactions.Add(new Transaction
            {
                Date = date.Date, Type = TransactionType.Deposit,
                CashDelta = amount, Note = note ?? string.Empty,
            });
            SaveAsync();
        }

        public void WithdrawCash(decimal amount, DateTime date, string note)
        {
            if (amount <= 0m) return;
            decimal actual = Math.Min(amount, _cash);
            if (actual <= 0m) return;
            _cash -= actual;
            _transactions.Add(new Transaction
            {
                Date = date.Date, Type = TransactionType.Withdrawal,
                CashDelta = -actual, Note = note ?? string.Empty,
            });
            SaveAsync();
        }

        // ── IPortfolioService — Ledger ────────────────────────────────────────

        public IReadOnlyList<Transaction> GetTransactions() => _transactions.ToList();

        /// <summary>
        /// Forces an immediate write of the current state, bypassing the debounce. Used by the
        /// CLI to guarantee a mutation is persisted before the process exits.
        /// </summary>
        public Task FlushAsync() => SaveInternalAsync();

        /// <summary>
        /// Syncs the open (most-recent) Buy ledger line for a symbol to an edited position, so
        /// editing shares / entry price / date / margin updates the history line it's on. If no
        /// Buy line exists yet (e.g. a position predating the ledger), one is created.
        /// </summary>
        private void SyncOpenBuy(HeldPosition pos)
        {
            for (int i = _transactions.Count - 1; i >= 0; i--)
            {
                var t = _transactions[i];
                if (t.Type == TransactionType.Buy &&
                    t.Symbol.Equals(pos.Symbol, StringComparison.OrdinalIgnoreCase))
                {
                    t.CompanyName = pos.CompanyName;
                    t.Shares      = pos.ShareCount;
                    t.Price       = pos.EntryPrice;
                    t.Date        = pos.EntryDate == default ? t.Date : pos.EntryDate.Date;
                    t.OnMargin    = pos.BoughtOnMargin;
                    t.CashDelta   = -pos.EquityInvested;   // reflect the current cash outlay
                    return;
                }
            }
            LogBuy(pos, debitCash: false);   // backfill a line for a pre-existing position (no cash move)
        }

        /// <summary>
        /// Appends a Buy ledger entry for an opened position. When <paramref name="debitCash"/>
        /// is true the investor's own money in the trade (<see cref="HeldPosition.EquityInvested"/>
        /// — the full cost for a cash buy, the margin down payment for a margin buy) is pulled
        /// from the cash balance (which may go negative). Backfilled buys for pre-existing
        /// positions pass false so they don't retroactively charge cash.
        /// </summary>
        private void LogBuy(HeldPosition pos, bool debitCash)
        {
            decimal outlay = debitCash ? pos.EquityInvested : 0m;
            if (outlay != 0m) _cash -= outlay;

            _transactions.Add(new Transaction
            {
                Date        = pos.EntryDate == default ? DateTime.Today : pos.EntryDate.Date,
                Type        = TransactionType.Buy,
                Symbol      = pos.Symbol,
                CompanyName = pos.CompanyName,
                Shares      = pos.ShareCount,
                Price       = pos.EntryPrice,
                CashDelta   = -outlay,
                OnMargin    = pos.BoughtOnMargin,
            });
        }

        // ── Persistence ───────────────────────────────────────────────────────

        /// <inheritdoc/>
        public event Action<string>? PersistenceError;

        /// <inheritdoc/>
        public string? StartupLoadError { get; private set; }

        /// <summary>
        /// Reads the portfolio file from disk.
        /// Returns an empty <see cref="PortfolioData"/> if the file is absent.
        /// A corrupt file is BACKED UP (never silently discarded) and reported
        /// via <see cref="StartupLoadError"/> so the user can recover it.
        /// </summary>
        private PortfolioData LoadFromDisk()
        {
            try
            {
                if (!File.Exists(_file)) return new PortfolioData();
                var json = File.ReadAllText(_file);
                return JsonSerializer.Deserialize<PortfolioData>(json, _jsonOptions)
                       ?? new PortfolioData();
            }
            catch (Exception ex)
            {
                // Corrupt or unreadable — preserve the evidence, then start fresh.
                var backup = _file + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                try { File.Move(_file, backup); } catch { backup = "(backup failed)"; }

                StartupLoadError =
                    $"Portfolio file could not be read ({ex.GetType().Name}); " +
                    $"backed up to {backup} and started fresh.";
                System.Diagnostics.Debug.WriteLine($"[PortfolioService] {StartupLoadError}");
                return new PortfolioData();
            }
        }

        // ── IPortfolioService — Market index cache ───────────────────────────────

        public IReadOnlyList<MarketIndexSnapshot> GetCachedMarketIndices()
            => _marketIndexCache.ToList();

        public void SaveMarketIndicesCache(IReadOnlyList<MarketIndexSnapshot> snapshots)
        {
            _marketIndexCache = snapshots.ToList();
            SaveAsync();
        }

        // ── IPortfolioService — Daily picks cache ────────────────────────────────

        public IReadOnlyList<DayPick>? GetCachedDayPicks(DateTime targetDate)
        {
            var key = targetDate.ToString("yyyy-MM-dd");
            return key == _dailyPicksDate && _dailyPicks.Count > 0
                ? _dailyPicks.ToList()
                : null;
        }

        public void SaveDayPicksCache(DateTime targetDate, IReadOnlyList<DayPick> picks)
        {
            _dailyPicksDate = targetDate.ToString("yyyy-MM-dd");
            _dailyPicks     = picks.ToList();
            SaveAsync();
        }

        /// <summary>
        /// Schedules a debounced save: cancels any pending write and schedules a fresh
        /// one 250 ms from now.  Rapid successive mutations (e.g. batch multi-select adds)
        /// all coalesce into a single file write that fires after the last mutation.
        /// </summary>
        private CancellationTokenSource _saveCts = new();

        private void SaveAsync()
        {
            // Cancel the previously scheduled save (if any) and start a new countdown.
            _saveCts.Cancel();
            _saveCts = new CancellationTokenSource();
            var token = _saveCts.Token;

            _ = Task.Delay(250, token)
                    .ContinueWith(
                        _ => SaveInternalAsync(),
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnRanToCompletion,
                        TaskScheduler.Default)
                    .Unwrap();
        }

        private async Task SaveInternalAsync()
        {
            try
            {
                Directory.CreateDirectory(_folder);

                // Snapshot the in-memory lists so the async write is safe
                // even if a mutation arrives while we're serialising.
                var snapshot = new PortfolioData
                {
                    WatchList        = _watch.ToList(),
                    Held             = _held.ToList(),
                    CashBalance      = _cash,
                    Transactions     = _transactions.ToList(),
                    DailyPicksDate   = _dailyPicksDate,
                    DailyPicks       = _dailyPicks.ToList(),
                    MarketIndexCache = _marketIndexCache.ToList(),
                };

                var tmp = _file + ".tmp";

                await using (var fs = new FileStream(
                    tmp, FileMode.Create, FileAccess.Write,
                    FileShare.None, bufferSize: 4096, useAsync: true))
                {
                    await JsonSerializer.SerializeAsync(fs, snapshot, _jsonOptions);
                    await fs.FlushAsync();
                }

                File.Move(tmp, _file, overwrite: true);
            }
            catch (Exception ex)
            {
                // Don't crash the app — but never fail silently either: a lost save
                // means a trade the user believes was recorded will vanish on restart.
                var msg = $"⚠ Portfolio save failed ({ex.GetType().Name}: {ex.Message}) — recent changes may be lost on exit.";
                System.Diagnostics.Debug.WriteLine($"[PortfolioService] {msg}");
                try { PersistenceError?.Invoke(msg); } catch { /* subscriber threw — ignore */ }
            }
        }
    }
}
