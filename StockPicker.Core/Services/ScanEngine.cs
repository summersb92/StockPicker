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
        /// Scans the universe through every supplied strategy, keeps the highest-scoring
        /// Buy/StrongBuy read per symbol, and returns the top <paramref name="topCount"/>
        /// (enriched) ranked by RAW SCORE — never by confidence, which saturates at 1.0
        /// once |score| ≥ 3 and would collapse the ranking to alphabetical order.
        /// Delegates to <see cref="CrossStrategyAsync"/>.
        /// </summary>
        public static async Task<List<BestPick>> BestAcrossStrategiesAsync(
            ScanData data,
            IReadOnlyList<TradingStrategy> strategies,
            decimal targetProfitPercent,
            IAnalysisService analysis,
            IRecommendationService recommendation,
            int topCount)
            => (await CrossStrategyAsync(data, strategies, targetProfitPercent,
                                         analysis, recommendation, topCount)).Best;

        /// <summary>
        /// One pass over every strategy that produces BOTH cross-strategy views:
        /// <list type="bullet">
        ///   <item><b>Best</b> — top Buy/StrongBuy per symbol across all strategies, ranked by
        ///   raw score (desc), then by how many strategies agree, then symbol. Each pick
        ///   carries its consensus count ("Buy on N of M strategies").</item>
        ///   <item><b>PerStrategy</b> — the top <paramref name="perStrategyTop"/> Buy-rated
        ///   picks for EACH strategy, so a briefing can mix strategies side by side.</item>
        /// </list>
        /// All returned recommendations are enriched with live quote data.
        /// </summary>
        public static async Task<CrossStrategyResult> CrossStrategyAsync(
            ScanData data,
            IReadOnlyList<TradingStrategy> strategies,
            decimal targetProfitPercent,
            IAnalysisService analysis,
            IRecommendationService recommendation,
            int topCount,
            int perStrategyTop = 3)
        {
            var (ranked, perStrategy) = await Task.Run(() =>
            {
                var bySymbol = new Dictionary<string, (Recommendation rec, string strat, int buyCount)>(
                                   StringComparer.OrdinalIgnoreCase);
                var sections = new List<StrategyTopPicks>(strategies.Count);

                foreach (var strat in strategies)
                {
                    var ctx      = BuildContext(data, strat, targetProfitPercent);
                    var analyses = RunAnalyses(data, ctx, analysis);
                    var recs     = recommendation.GenerateAsync(analyses, ctx).Result;

                    var stratBuys = new List<Recommendation>();
                    foreach (var r in recs)
                    {
                        if (r.Action != RecommendationAction.Buy && r.Action != RecommendationAction.StrongBuy)
                            continue;

                        stratBuys.Add(r);

                        // Keep the highest-SCORING read per symbol; count strategy agreement.
                        if (bySymbol.TryGetValue(r.Symbol, out var existing))
                        {
                            bySymbol[r.Symbol] = r.Score > existing.rec.Score
                                ? (r, strat.Name, existing.buyCount + 1)
                                : (existing.rec, existing.strat, existing.buyCount + 1);
                        }
                        else
                        {
                            bySymbol[r.Symbol] = (r, strat.Name, 1);
                        }
                    }

                    sections.Add(new StrategyTopPicks(
                        strat.Name,
                        strat.HoldingPeriod.ToString(),
                        stratBuys.OrderByDescending(r => r.Score)
                                 .ThenBy(r => r.Symbol)
                                 .Take(perStrategyTop)
                                 .ToList()));
                }

                var best = bySymbol.Values
                    .OrderByDescending(x => x.rec.Score)
                    .ThenByDescending(x => x.buyCount)
                    .ThenBy(x => x.rec.Symbol)
                    .Take(topCount)
                    .ToList();

                return (best, sections);
            });

            int strategyCount = strategies.Count;

            // Enrich every distinct recommendation instance surfaced by either view.
            var enriched = new HashSet<Recommendation>();
            foreach (var (rec, _, _) in ranked)
                if (enriched.Add(rec)) Enrich(rec, data);
            foreach (var section in perStrategy)
                foreach (var rec in section.Picks)
                    if (enriched.Add(rec)) Enrich(rec, data);

            return new CrossStrategyResult
            {
                Best = ranked.Select(x => new BestPick(x.rec, x.strat, x.buyCount, strategyCount)).ToList(),
                PerStrategy = perStrategy,
            };
        }

        // ── Internals ──────────────────────────────────────────────────────────

        private static ScanContext BuildContext(ScanData data, TradingStrategy strategy, decimal target)
            => new()
            {
                Strategy                  = strategy,
                TargetProfitMarginPercent = target,
                WeekStart                 = data.WeekStart,
                WeekEnd                   = data.WeekEnd,
                Summaries                 = data.Summaries,
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
                rec.NextEarningsDate = qs.NextEarningsDate;
                rec.DividendYieldPct = qs.DividendYieldPct;
                rec.ShortRatio       = qs.ShortRatio;
                rec.ImpliedVolatility = qs.ImpliedVolatility;
                rec.Theta             = qs.Theta;
                rec.TotalCash         = qs.TotalCash;
            }

            // Always backfill name/sector from the universe lookup — Yahoo's quote
            // endpoint returns no sector, so without this the sector rollup reads
            // "Unknown" even though the universe list knows every constituent's sector.
            if (data.NameLookup.TryGetValue(rec.Symbol, out var info))
            {
                if (string.IsNullOrEmpty(rec.CompanyName)) rec.CompanyName = info.Name;
                if (string.IsNullOrEmpty(rec.Sector))      rec.Sector      = info.Sector;
            }
        }
    }
}
