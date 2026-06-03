using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StockPicker.Models;

namespace StockPicker.Services
{
    /// <summary>
    /// The cached inputs for the analysis/recommendation pipeline:
    /// the universe plus its price history, live quote summaries, and a name/sector map.
    /// </summary>
    public sealed class ScanData
    {
        public IReadOnlyList<Stock> Universe { get; init; } = Array.Empty<Stock>();
        public Dictionary<string, IReadOnlyList<StockQuote>> History { get; init; } =
            new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, QuoteSummary> Summaries { get; init; } =
            new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, (string Name, string Sector)> NameLookup { get; init; } =
            new(StringComparer.OrdinalIgnoreCase);
        public DateTime WeekStart { get; init; }
        public DateTime WeekEnd   { get; init; }
    }

    /// <summary>
    /// WPF-free orchestration of the scan pipeline (fetch → analyze → recommend),
    /// shared by the desktop app and the CLI. The desktop app layers its own
    /// multi-source fetch and UI state on top; the CLI uses these helpers directly.
    /// </summary>
    public static class ScanEngine
    {
        /// <summary>Returns the universe for an index — built-in lists, or a live fetch for the S&amp;P 500.</summary>
        public static async Task<IReadOnlyList<Stock>> GetUniverseAsync(IStockDataService data, IndexUniverse index)
            => index switch
            {
                IndexUniverse.Dow30     => BuiltInUniverses.Dow30,
                IndexUniverse.SP100     => BuiltInUniverses.SP100,
                IndexUniverse.Nasdaq100 => BuiltInUniverses.Nasdaq100,
                _                       => await data.GetUniverseAsync(),
            };

        /// <summary>
        /// Fetches daily price history (last <paramref name="lookbackDays"/> days) and live quote
        /// summaries for <paramref name="universe"/> from a single data source.
        /// </summary>
        public static async Task<ScanData> FetchAsync(
            IStockDataService data,
            IReadOnlyList<Stock> universe,
            int lookbackDays = 90,
            int maxConcurrency = 15,
            IProgress<string>? progress = null,
            CancellationToken ct = default)
        {
            var today     = DateTime.Today;
            var weekStart = today.AddDays(-lookbackDays);
            var weekEnd   = today;

            var nameLookup = universe.ToDictionary(
                s => s.Symbol, s => (s.Name, s.Sector), StringComparer.OrdinalIgnoreCase);

            progress?.Report($"Fetching price history for {universe.Count} symbols…");

            var bag = new ConcurrentDictionary<string, IReadOnlyList<StockQuote>>(StringComparer.OrdinalIgnoreCase);
            using var sem = new SemaphoreSlim(maxConcurrency);

            var tasks = universe.Select(async stock =>
            {
                await sem.WaitAsync(ct);
                try
                {
                    bag[stock.Symbol] = await data.GetHistoryAsync(stock.Symbol, weekStart, weekEnd);
                }
                catch
                {
                    // Missing history for one symbol must not abort the whole scan.
                }
                finally { sem.Release(); }
            });
            await Task.WhenAll(tasks);

            progress?.Report("Fetching live quote summaries…");
            var summaries = await data.GetQuoteSummariesAsync(universe.Select(s => s.Symbol));

            return new ScanData
            {
                Universe   = universe,
                History    = new Dictionary<string, IReadOnlyList<StockQuote>>(bag, StringComparer.OrdinalIgnoreCase),
                Summaries  = new Dictionary<string, QuoteSummary>(summaries, StringComparer.OrdinalIgnoreCase),
                NameLookup = nameLookup,
                WeekStart  = weekStart,
                WeekEnd    = weekEnd,
            };
        }

        /// <summary>
        /// Runs analysis + recommendation for a single strategy and enriches each
        /// recommendation with company name, sector, and live market data.
        /// </summary>
        public static async Task<IReadOnlyList<Recommendation>> AnalyzeAndRecommendAsync(
            ScanData data,
            TradingStrategy strategy,
            decimal targetProfitPercent,
            IAnalysisService analysis,
            IRecommendationService recommendation)
        {
            var ctx = BuildContext(data, strategy, targetProfitPercent);

            var analyses = await Task.Run(() => RunAnalyses(data, ctx, analysis));
            var recs = (await recommendation.GenerateAsync(analyses, ctx)).ToList();

            foreach (var rec in recs)
                Enrich(rec, data);

            return recs;
        }

        /// <summary>
        /// Scans the universe through every supplied strategy, keeps the highest-confidence
        /// Buy/StrongBuy read per symbol, and returns the top <paramref name="topCount"/>
        /// (enriched) ranked by confidence.
        /// </summary>
        public static async Task<List<BestPick>> BestAcrossStrategiesAsync(
            ScanData data,
            IReadOnlyList<TradingStrategy> strategies,
            decimal targetProfitPercent,
            IAnalysisService analysis,
            IRecommendationService recommendation,
            int topCount)
        {
            var ranked = await Task.Run(() =>
            {
                var bySymbol = new Dictionary<string, (Recommendation rec, string strat)>(StringComparer.OrdinalIgnoreCase);

                foreach (var strat in strategies)
                {
                    var ctx      = BuildContext(data, strat, targetProfitPercent);
                    var analyses = RunAnalyses(data, ctx, analysis);
                    var recs     = recommendation.GenerateAsync(analyses, ctx).Result;

                    foreach (var r in recs)
                    {
                        if (r.Action != RecommendationAction.Buy && r.Action != RecommendationAction.StrongBuy)
                            continue;
                        if (!bySymbol.TryGetValue(r.Symbol, out var existing) || r.Confidence > existing.rec.Confidence)
                            bySymbol[r.Symbol] = (r, strat.Name);
                    }
                }

                return bySymbol.Values
                    .OrderByDescending(x => x.rec.Confidence)
                    .ThenBy(x => x.rec.ActionSortOrder)
                    .ThenBy(x => x.rec.Symbol)
                    .Take(topCount)
                    .ToList();
            });

            foreach (var (rec, _) in ranked)
                Enrich(rec, data);

            return ranked.Select(x => new BestPick(x.rec, x.strat)).ToList();
        }

        // ── Internals ──────────────────────────────────────────────────────────

        private static ScanContext BuildContext(ScanData data, TradingStrategy strategy, decimal target)
            => new()
            {
                Strategy                  = strategy,
                TargetProfitMarginPercent = target,
                WeekStart                 = data.WeekStart,
                WeekEnd                   = data.WeekEnd,
            };

        private static List<AnalysisResult> RunAnalyses(ScanData data, ScanContext ctx, IAnalysisService analysis)
        {
            var list = new List<AnalysisResult>(data.Universe.Count);
            foreach (var stock in data.Universe)
            {
                var history = data.History.TryGetValue(stock.Symbol, out var h)
                    ? h : Array.Empty<StockQuote>();
                list.Add(analysis.AnalyzeAsync(stock, history, ctx).Result);
            }
            return list;
        }

        /// <summary>Fills company name, sector, and live market data onto a recommendation.</summary>
        public static void Enrich(Recommendation rec, ScanData data)
        {
            if (data.Summaries.TryGetValue(rec.Symbol, out var qs))
            {
                rec.CompanyName = qs.LongName ?? qs.ShortName ?? rec.Symbol;
                if (string.IsNullOrEmpty(rec.Sector))
                    rec.Sector = qs.Sector ?? "";

                rec.LastPrice        = qs.Price;
                rec.DayChange        = qs.DayChange;
                rec.DayChangePct     = qs.DayChangePct;
                rec.Volume           = qs.Volume;
                rec.AvgVolume        = qs.AvgVolume;
                rec.MarketCap        = qs.MarketCap;
                rec.PERatio          = qs.PERatio;
                rec.ForwardPE        = qs.ForwardPE;
                rec.EPS              = qs.EPS;
                rec.PriceToBook      = qs.PriceToBook;
                rec.Week52High       = qs.Week52High;
                rec.Week52Low        = qs.Week52Low;
                rec.Beta             = qs.Beta;
                rec.DividendYieldPct = qs.DividendYieldPct;
                rec.ShortRatio       = qs.ShortRatio;
                rec.ImpliedVolatility = qs.ImpliedVolatility;
                rec.Theta             = qs.Theta;
            }
            else if (data.NameLookup.TryGetValue(rec.Symbol, out var info))
            {
                if (string.IsNullOrEmpty(rec.CompanyName)) rec.CompanyName = info.Name;
                if (string.IsNullOrEmpty(rec.Sector))      rec.Sector      = info.Sector;
            }
        }
    }
}
