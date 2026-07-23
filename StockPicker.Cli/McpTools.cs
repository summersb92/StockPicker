using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using StockPicker.Cli;
using StockPicker.Models;
using StockPicker.Reference;
using StockPicker.Services;

// =====================================================================================
// `stockpicker mcp` — a READ-ONLY Model Context Protocol server over stdio.
//
// stdout is the JSON-RPC channel and NOTHING else may write to it: all logging is
// routed to stderr via LogToStandardErrorThreshold, and every tool returns its result
// as a string (one MCP text content block). The server exposes no mutating tools —
// deposit/withdraw/sell remain CLI-only — and every payload is built exclusively from
// StockPicker.Core's whitelisted ContextProjections DTOs, so UserSettings / ApiKeys
// can never reach any output.
// =====================================================================================

internal partial class Program
{
    /// <summary>
    /// Hosts the MCP stdio server. Blocks until the client closes stdin
    /// (the normal MCP shutdown signal), then returns to the dispatcher.
    /// </summary>
    private static async Task RunMcpServer()
    {
        // Deliberately NOT forwarding the CLI argv into the host: "mcp" (and any
        // stray flags) mean nothing to Host configuration.
        var builder = Host.CreateApplicationBuilder(Array.Empty<string>());

        // ── stdout purity ── stdout carries JSON-RPC frames only; push every log
        // line (Trace..Critical) to stderr.
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

        // Registered so the SDK resolves McpDataProvider tool parameters from DI
        // (and therefore hides them from the client-facing tool schemas).
        builder.Services.AddSingleton<McpDataProvider>();

        builder.Services
            .AddMcpServer(o => o.ServerInfo = new() { Name = "stockpicker", Version = "1.0.0" })
            .WithStdioServerTransport()
            .WithToolsFromAssembly();

        await builder.Build().RunAsync();
    }
}

namespace StockPicker.Cli
{
    /// <summary>
    /// Singleton data hub for the MCP tools: owns the Core services and memoizes the
    /// expensive ScanEngine universe+fetch result in-memory per index with a 15-minute
    /// TTL, so consecutive tool calls don't re-download 90 days of history each time.
    /// Concurrent callers for the same index share one in-flight fetch task; a faulted
    /// or expired entry is refetched on the next call.
    /// </summary>
    public sealed class McpDataProvider
    {
        private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(15);

        private readonly object _gate = new();
        private readonly Dictionary<IndexUniverse, (DateTime FetchedAtUtc, Task<ScanData> Data)> _cache = new();

        public ILogger Logger { get; }

        // Single data source, no API key required — mirrors the CLI's service set.
        public YahooFinanceStockDataService DataService { get; } = new();
        public AnalysisService              Analysis    { get; } = new();
        public RecommendationService        Recommender { get; } = new();
        public StrategyProvider             Strategies  { get; } = new();
        public EarningsScanService          EarningsScanner { get; } = new();
        public DayPickService               DayPicker   { get; } = new();

        public McpDataProvider(ILogger<McpDataProvider> logger) => Logger = logger;

        /// <summary>
        /// Returns the (possibly cached) universe + 90-day history + live summaries
        /// for <paramref name="index"/>. First call per index fetches (progress goes
        /// to stderr); later calls within the TTL reuse the same ScanData.
        /// </summary>
        public Task<ScanData> GetScanDataAsync(IndexUniverse index)
        {
            lock (_gate)
            {
                if (_cache.TryGetValue(index, out var entry)
                    && DateTime.UtcNow - entry.FetchedAtUtc < CacheTtl
                    && !entry.Data.IsFaulted
                    && !entry.Data.IsCanceled)
                {
                    Logger.LogDebug("ScanData cache hit for {Index} (age {Age:m\\:ss}).",
                        index, DateTime.UtcNow - entry.FetchedAtUtc);
                    return entry.Data;
                }

                // Shared task, deliberately not tied to any one caller's CancellationToken —
                // a canceled client request must not poison the cache for the next caller.
                var task = FetchAsync(index);
                _cache[index] = (DateTime.UtcNow, task);
                return task;
            }
        }

        private async Task<ScanData> FetchAsync(IndexUniverse index)
        {
            Logger.LogInformation("ScanData cache miss for {Index} — fetching universe + history…", index);
            var universe = await ScanEngine.GetUniverseAsync(DataService, index);
            var data = await ScanEngine.FetchAsync(
                DataService, universe,
                progress: new Progress<string>(m => Logger.LogInformation("{Message}", m)));
            Logger.LogInformation("Cached {Count} symbols for {Index} (TTL {Ttl} min).",
                data.Universe.Count, index, CacheTtl.TotalMinutes);
            return data;
        }

        // ── Argument resolvers (strict: unknown values throw so the client can self-correct) ──

        public static IndexUniverse ResolveIndex(string? key)
        {
            var k = (key ?? "dow30").Replace("&", "").Replace("-", "").Replace(" ", "").ToLowerInvariant();
            return k switch
            {
                "dow" or "dow30" or "djia"       => IndexUniverse.Dow30,
                "sp100" or "s&p100" or "100"     => IndexUniverse.SP100,
                "nasdaq" or "nasdaq100" or "ndx" => IndexUniverse.Nasdaq100,
                "sp500" or "500" or ""           => IndexUniverse.SP500,
                _ => throw new ArgumentException(
                         $"Unknown index '{key}'. Valid values: dow30, sp100, nasdaq100, sp500."),
            };
        }

        public TradingStrategy ResolveStrategy(string? key)
        {
            var all = Strategies.GetStrategies();
            if (string.IsNullOrWhiteSpace(key)) return Strategies.GetDefault();

            var norm = key.Replace("-", "").Replace(" ", "").ToLowerInvariant();
            return all.FirstOrDefault(s => s.Id.Replace("-", "").Equals(norm, StringComparison.OrdinalIgnoreCase))
                ?? all.FirstOrDefault(s => s.Name.Replace(" ", "").Contains(norm, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException(
                       $"Unknown strategy '{key}'. Valid ids: {string.Join(", ", all.Select(s => s.Id))}.");
        }

        public static DayPickStrategy ResolveDayPickStrategy(string? key)
        {
            var k = (key ?? "momentum").Replace("-", "").Replace(" ", "").ToLowerInvariant();
            return k switch
            {
                "momentum" or ""               => DayPickStrategy.Momentum,
                "meanreversion" or "reversion" => DayPickStrategy.MeanReversion,
                "breakout"                     => DayPickStrategy.Breakout,
                "earnings" or "earningsplay"   => DayPickStrategy.EarningsPlay,
                _ => throw new ArgumentException(
                         $"Unknown day-pick strategy '{key}'. Valid values: momentum, meanreversion, breakout, earningsplay."),
            };
        }
    }

    /// <summary>
    /// The read-only MCP tool surface. Every tool is annotated ReadOnly / non-Destructive /
    /// Idempotent / closed-world, returns pre-serialized JSON (or markdown for the briefing),
    /// and serializes ONLY ContextProjections whitelist DTOs — never raw models, and never
    /// UserSettings / ApiKeys.
    /// </summary>
    [McpServerToolType]
    public static class McpTools
    {
        // Deterministic, LLM-friendly output: camelCase + string enums + no nulls,
        // matching ContextExportService's bundle formatting.
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented          = true,
            PropertyNamingPolicy   = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters             = { new JsonStringEnumConverter() },
        };

        private static string ToJson<T>(T value) => JsonSerializer.Serialize(value, JsonOpts);

        [McpServerTool(Name = "get_recommendations",
            ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
        [Description("Run a trading-strategy scan over an index universe and return the top " +
                     "recommendations (action, confidence, reasoning, key indicators, trade dates) as JSON.")]
        public static async Task<string> GetRecommendations(
            McpDataProvider data,
            [Description("Strategy id, e.g. momentum (see also the strategies listed by an unknown-value error).")]
            string strategy = "momentum",
            [Description("Index universe: dow30 | sp100 | nasdaq100 | sp500.")]
            string index = "dow30",
            [Description("Maximum number of recommendations to return.")]
            int top = 10,
            [Description("Weekly profit target percent used by the strategy scoring.")]
            decimal targetPercent = 2.0m,
            CancellationToken ct = default)
        {
            var scan  = await data.GetScanDataAsync(McpDataProvider.ResolveIndex(index));
            var strat = data.ResolveStrategy(strategy);

            data.Logger.LogInformation("get_recommendations: {Strategy} over {Count} symbols…",
                strat.Name, scan.Universe.Count);
            var recs = await ScanEngine.AnalyzeAndRecommendAsync(
                scan, strat, targetPercent, data.Analysis, data.Recommender);

            var ranked = recs
                .OrderByDescending(r => r.Action is RecommendationAction.StrongBuy or RecommendationAction.Buy)
                .ThenByDescending(r => r.Confidence)
                .ThenBy(r => r.ActionSortOrder)
                .Take(top)
                .Select(ContextProjections.ProjectRecommendation)
                .ToList();

            return ToJson(ranked);
        }

        [McpServerTool(Name = "get_earnings_scan",
            ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
        [Description("Rank upcoming-earnings candidates in an index by likelihood score, with expected " +
                     "move and momentum, as JSON.")]
        public static async Task<string> GetEarningsScan(
            McpDataProvider data,
            [Description("How many days ahead to look for earnings dates.")]
            int windowDays = 30,
            [Description("Upside threshold percent a pick must clear to be flagged as meeting the target.")]
            decimal targetUpPercent = 5.0m,
            [Description("Index universe: dow30 | sp100 | nasdaq100 | sp500.")]
            string index = "dow30",
            CancellationToken ct = default)
        {
            var scan = await data.GetScanDataAsync(McpDataProvider.ResolveIndex(index));

            data.Logger.LogInformation("get_earnings_scan: next {Days} days over {Count} symbols…",
                windowDays, scan.Universe.Count);
            var picks = (await data.EarningsScanner.GenerateAsync(
                    scan.Universe, scan.History, scan.Summaries, scan.NameLookup,
                    windowDays, targetUpPercent, useMargin: false,
                    marginPercent: 50m, marginRatePercent: 12.5m))
                .OrderByDescending(e => e.MeetsThreshold)
                .ThenByDescending(e => e.LikelihoodScore)
                .Select(ContextProjections.ProjectEarnings)
                .ToList();

            return ToJson(picks);
        }

        [McpServerTool(Name = "get_day_picks",
            ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
        [Description("Generate intraday (same-session) picks with direction, entry/stop/target levels, " +
                     "and risk-reward ratio, as JSON.")]
        public static async Task<string> GetDayPicks(
            McpDataProvider data,
            [Description("Day-pick strategy: momentum | meanreversion | breakout | earningsplay.")]
            string strategy = "momentum",
            [Description("Index universe: dow30 | sp100 | nasdaq100 | sp500.")]
            string index = "dow30",
            CancellationToken ct = default)
        {
            var scan = await data.GetScanDataAsync(McpDataProvider.ResolveIndex(index));
            var dayStrategy = McpDataProvider.ResolveDayPickStrategy(strategy);

            data.Logger.LogInformation("get_day_picks: {Strategy} over {Count} symbols…",
                dayStrategy, scan.Universe.Count);
            var picks = (await data.DayPicker.GenerateAsync(
                    scan.Universe, scan.History, scan.Summaries, scan.NameLookup, dayStrategy))
                .Select(ContextProjections.ProjectDayPick)
                .ToList();

            return ToJson(picks);
        }

        [McpServerTool(Name = "get_portfolio",
            ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
        [Description("Read the saved portfolio (shared with the desktop app): cash balance, open positions " +
                     "with live prices and unrealized P&L, and aggregate performance (trailing " +
                     "week/month/quarter/year returns), as JSON. Read-only — never modifies the portfolio.")]
        public static async Task<string> GetPortfolio(
            McpDataProvider data,
            CancellationToken ct = default)
        {
            // Fresh instance per call so changes made by the desktop app are visible.
            // Nothing below ever writes the store (no Flush/Sell/Deposit).
            var portfolio = new PortfolioService();
            var held = portfolio.GetHeld().ToList();
            var cash = portfolio.GetCash();

            if (held.Count > 0)
            {
                try
                {
                    // Cheap: one batched quote call for just the held symbols.
                    var summaries = await data.DataService.GetQuoteSummariesAsync(held.Select(h => h.Symbol));
                    foreach (var h in held)
                        if (summaries.TryGetValue(h.Symbol, out var qs))
                            h.LastPrice = qs.Price;
                }
                catch (Exception ex)
                {
                    data.Logger.LogWarning("Live price refresh failed ({Error}) — reporting stored prices.",
                        ex.Message);
                }
            }

            PerformanceExport? performance = null;
            if (held.Count > 0 || cash > 0m)
            {
                data.Logger.LogInformation("get_portfolio: computing performance for {Count} positions…",
                    held.Count);
                var perf = await PerformanceService.ComputeAsync(held, data.DataService, cash, ct: ct);
                performance = ContextProjections.ProjectPerformance(perf);
            }

            return ToJson(new
            {
                CashBalance = cash,
                Positions   = held.Select(ContextProjections.ProjectPosition).ToList(),
                Performance = performance,
            });
        }

        [McpServerTool(Name = "get_transactions",
            ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
        [Description("Read the full transaction ledger (buys, sells, deposits, withdrawals), newest first, " +
                     "as JSON.")]
        public static string GetTransactions()
        {
            var portfolio = new PortfolioService();
            var txns = portfolio.GetTransactions()
                .OrderByDescending(t => t.Date)
                .ThenByDescending(t => t.Type == TransactionType.Sell)
                .Select(ContextProjections.ProjectTransaction)
                .ToList();
            return ToJson(txns);
        }

        [McpServerTool(Name = "get_news_briefing",
            ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
        [Description("Build the full markdown News briefing: held-position hold/sell guidance, the best " +
                     "picks across ALL strategies, top earnings plays, and top picks for the chosen strategy.")]
        public static async Task<string> GetNewsBriefing(
            McpDataProvider data,
            [Description("Strategy id for the top-picks section, e.g. momentum.")]
            string strategy = "momentum",
            [Description("Index universe: dow30 | sp100 | nasdaq100 | sp500.")]
            string index = "dow30",
            CancellationToken ct = default)
        {
            const decimal target = 2.0m;
            const int     top    = 5;
            const int     days   = 30;

            var idx   = McpDataProvider.ResolveIndex(index);
            var scan  = await data.GetScanDataAsync(idx);
            var strat = data.ResolveStrategy(strategy);

            data.Logger.LogInformation("get_news_briefing: {Strategy} over {Count} symbols…",
                strat.Name, scan.Universe.Count);
            var recs = await ScanEngine.AnalyzeAndRecommendAsync(
                scan, strat, target, data.Analysis, data.Recommender);
            var best = await ScanEngine.BestAcrossStrategiesAsync(
                scan, data.Strategies.GetStrategies(), target, data.Analysis, data.Recommender, top);
            var earnings = await data.EarningsScanner.GenerateAsync(
                scan.Universe, scan.History, scan.Summaries, scan.NameLookup,
                days, 5.0m, useMargin: false, marginPercent: 50m, marginRatePercent: 12.5m);

            // Same read-only pattern as `stockpicker news`: load held positions and
            // refresh their live price in memory so hold/sell guidance reflects current P/L.
            var portfolio = new PortfolioService();
            var held = portfolio.GetHeld().ToList();
            foreach (var h in held)
                if (scan.Summaries.TryGetValue(h.Symbol, out var qs))
                    h.LastPrice = qs.Price;

            var monthly = Math.Round(
                (decimal)((Math.Pow((double)(1m + target / 100m), 52.0 / 12.0) - 1.0) * 100.0), 2);

            return NewsBriefingBuilder.Build(new BriefingInput
            {
                StrategyName         = strat.Name,
                UniverseDescription  = idx.Description(),
                TargetWeeklyPercent  = target,
                TargetMonthlyPercent = monthly,
                DataSources          = new[] { "YahooFinance" },
                Recommendations      = recs,
                Positions            = held,
                Earnings             = earnings,
                BestAnyStrategy      = best,
                EarningsWindowDays   = days,
                TopCount             = top,
                GeneratedAt          = DateTime.Now,
            });
        }

        [McpServerTool(Name = "get_context_manifest",
            ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
        [Description("Read the manifest of the on-disk LLM context bundle (schema version, freshness, and " +
                     "the section files present). Returns a note if no bundle has been exported yet.")]
        public static string GetContextManifest()
        {
            var manifestPath = Path.Combine(ContextExportService.ContextFolder, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                return ToJson(new
                {
                    Note = "No context bundle exists yet. Run the desktop app (any completed scan exports " +
                           "one) or `stockpicker context` to generate it.",
                    ExpectedPath = manifestPath,
                });
            }

            return File.ReadAllText(manifestPath).TrimEnd();
        }

        [McpServerTool(Name = "get_glossary",
            ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
        [Description("Return the app's canonical glossary as JSON: educational (non-advisory) definitions " +
                     "for every field, indicator, and strategy that appears in the other tools' output " +
                     "(e.g. rsi14, confidence, unrealizedGainPct, leverage, likelihoodScore). Each entry has " +
                     "a key, term, category, tooltip, explanation, and optional formula/range.")]
        public static string GetGlossary() => ToJson(Glossary.All);

        [McpServerTool(Name = "explain_term",
            ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
        [Description("Explain a single glossary term/field by its key (e.g. rsi14, confidence, riskRewardRatio, " +
                     "momentum). Case-insensitive. Returns one definition as JSON; on an unknown term throws " +
                     "an error listing the valid keys so the caller can self-correct.")]
        public static string ExplainTerm(
            [Description("The glossary key to explain, e.g. rsi14, confidence, leverage, or a strategy id like momentum.")]
            string term)
        {
            if (Glossary.TryGet(term, out var def) && def is not null)
                return ToJson(def);

            throw new ArgumentException(
                $"Unknown term '{term}'. Valid keys: {string.Join(", ", Glossary.All.Select(d => d.Key))}.");
        }

        [McpServerTool(Name = "get_app_state",
            ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
        [Description("Read the desktop app's current-focus snapshot (app-state.json): active strategy, scan " +
                     "universe, selected symbol, active view, grid sort, and scan freshness. Answers " +
                     "\"what is the user looking at right now?\". Returns a note if the desktop app has not " +
                     "exported a bundle yet.")]
        public static string GetAppState()
        {
            var appStatePath = Path.Combine(ContextExportService.ContextFolder, "app-state.json");
            if (!File.Exists(appStatePath))
            {
                return ToJson(new
                {
                    Note = "No app state exists yet. It is written by the desktop app after a scan " +
                           "(or by `stockpicker context`).",
                    ExpectedPath = appStatePath,
                });
            }

            return File.ReadAllText(appStatePath).TrimEnd();
        }
    }
}
