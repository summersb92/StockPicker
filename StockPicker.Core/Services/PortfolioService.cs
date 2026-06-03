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
        private string        _dailyPicksDate  = string.Empty;
        private List<DayPick> _dailyPicks      = new();
        private List<MarketIndexSnapshot> _marketIndexCache = new();

        // ── Construction ─────────────────────────────────────────────────────

        public PortfolioService()
        {
            var data         = LoadFromDisk();
            _watch           = data.WatchList;
            _held            = data.Held;
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

            _held.Add(new HeldPosition
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
            });

            SaveAsync();
        }

        public void UpsertHeld(HeldPosition position)
        {
            if (position is null || string.IsNullOrWhiteSpace(position.Symbol)) return;

            int idx = _held.FindIndex(
                h => h.Symbol.Equals(position.Symbol, StringComparison.OrdinalIgnoreCase));

            if (idx >= 0) _held[idx] = position;   // update existing in place
            else          _held.Add(position);     // add new

            SaveAsync();
        }

        public void RemoveFromHeld(string symbol)
        {
            int removed = _held.RemoveAll(
                h => h.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase));

            if (removed > 0) SaveAsync();
        }

        // ── Persistence ───────────────────────────────────────────────────────

        /// <summary>
        /// Reads the portfolio file from disk.
        /// Returns an empty <see cref="PortfolioData"/> if the file is absent or corrupt.
        /// </summary>
        private static PortfolioData LoadFromDisk()
        {
            try
            {
                if (!File.Exists(_file)) return new PortfolioData();
                var json = File.ReadAllText(_file);
                return JsonSerializer.Deserialize<PortfolioData>(json, _jsonOptions)
                       ?? new PortfolioData();
            }
            catch
            {
                // Corrupt file — start fresh (don't crash the app).
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
            catch
            {
                // Best-effort — never crash the app because a save failed.
            }
        }
    }
}
