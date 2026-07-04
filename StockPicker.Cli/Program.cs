using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using StockPicker.Models;
using StockPicker.Services;

// =====================================================================================
// StockPicker CLI — cross-platform (Windows + Linux + macOS).
//
//   stockpicker strategies
//   stockpicker scan     --strategy momentum [--index sp500] [--limit N] [--top N] [--json]
//   stockpicker news     [--strategy momentum] [--index sp500] [--limit N] [--top N] [--json]
//   stockpicker earnings [--index sp500] [--limit N] [--days 30] [--target 5] [--top N] [--json]
//   stockpicker daypicks [--strategy momentum] [--index sp500] [--limit N] [--top N] [--json]
//   stockpicker context  [--strategy momentum] [--index sp500] [--limit N] [--stdout]
//   stockpicker mcp      (read-only MCP stdio server — see McpTools.cs)
//
// Results go to stdout; progress/status goes to stderr, so `--json` output pipes cleanly.
// =====================================================================================

Console.OutputEncoding = Encoding.UTF8;

var argv = args.ToList();
string command = argv.Count > 0 && !argv[0].StartsWith('-') ? argv[0].ToLowerInvariant() : "help";

bool   Flag(string name)        => argv.Contains(name, StringComparer.OrdinalIgnoreCase);
string? Opt(string name)
{
    int i = argv.FindIndex(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
    return i >= 0 && i + 1 < argv.Count ? argv[i + 1] : null;
}
int?     OptInt(string name)     => int.TryParse(Opt(name), out var v) ? v : null;
decimal? OptDecimal(string name) => decimal.TryParse(Opt(name), out var v) ? v : null;

bool json = Flag("--json");

void Log(string msg) => Console.Error.WriteLine(msg);

var jsonOpts = new JsonSerializerOptions
{
    WriteIndented        = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters           = { new JsonStringEnumConverter() },
};
void OutJson(object o) => Console.WriteLine(JsonSerializer.Serialize(o, jsonOpts));

// ── Services (single data source: Yahoo Finance — no API key required) ──
var dataService    = new YahooFinanceStockDataService();
var analysis       = new AnalysisService();
var recommendation = new RecommendationService();
var strategyProvider = new StrategyProvider();
var earningsService  = new EarningsScanService();
var dayPickService   = new DayPickService();

try
{
    switch (command)
    {
        case "strategies": RunStrategies(); break;
        case "scan":       await RunScan();     break;
        case "news":       await RunNews();     break;
        case "earnings":   await RunEarnings(); break;
        case "daypicks":   await RunDayPicks(); break;
        case "context":    await RunContext();  break;
        case "backtest":   await RunBacktest(); break;
        case "mcp":        await RunMcpServer(); break;
        case "performance":
        case "perf":       await RunPerformance(); break;
        case "history":
        case "ledger":     RunHistory();      break;
        case "deposit":    await RunDeposit();  break;
        case "withdraw":   await RunWithdraw(); break;
        case "sell":       await RunSell();     break;
        case "help":
        case "--help":
        case "-h":         PrintUsage(); break;
        default:
            Log($"Unknown command '{command}'.");
            PrintUsage();
            return 2;
    }
    return 0;
}
catch (Exception ex)
{
    Log($"Error: {ex.Message}");
    return 1;
}

// ─────────────────────────────────────────────────────────────────────────────
// Commands
// ─────────────────────────────────────────────────────────────────────────────

void RunStrategies()
{
    var strategies = strategyProvider.GetStrategies();
    if (json)
    {
        OutJson(strategies.Select(s => new
        {
            s.Id, s.Name, HoldingPeriod = s.HoldingPeriod.ToString(), s.Description
        }));
        return;
    }

    Console.WriteLine("Available strategies (use the id with --strategy):");
    Console.WriteLine();
    foreach (var s in strategies)
    {
        Console.WriteLine($"  {s.Id,-16} {s.Name}  [{s.HoldingPeriod}]");
        Console.WriteLine($"  {"",-16} {s.Description}");
        Console.WriteLine();
    }
}

async Task RunScan()
{
    var data  = await LoadDataAsync();
    var strat = ResolveStrategy(Opt("--strategy"));
    var target = OptDecimal("--target") ?? 2.0m;
    int top    = OptInt("--top") ?? 10;

    Log($"Analyzing {data.Universe.Count} stocks with '{strat.Name}'…");
    var recs = await ScanEngine.AnalyzeAndRecommendAsync(data, strat, target, analysis, recommendation);

    var ranked = recs
        .OrderByDescending(r => r.Action is RecommendationAction.StrongBuy or RecommendationAction.Buy)
        .ThenByDescending(r => r.Confidence)
        .ThenBy(r => r.ActionSortOrder)
        .Take(top)
        .ToList();

    if (json) { OutJson(ranked.Select(ContextProjections.ProjectRecommendation)); return; }

    Console.WriteLine($"Top {ranked.Count} picks — {strat.Name}");
    Console.WriteLine(new string('─', 92));
    Console.WriteLine($"{"#",-3} {"SYM",-7} {"ACTION",-11} {"CONF",6} {"PRICE",10} {"RSI",5}  REASONING");
    Console.WriteLine(new string('─', 92));
    int i = 1;
    foreach (var r in ranked)
    {
        var price = r.LastPrice.HasValue ? $"${r.LastPrice.Value:F2}" : "—";
        var rsi   = r.RSI14.HasValue ? $"{r.RSI14.Value:F0}" : "—";
        Console.WriteLine($"{i++,-3} {r.Symbol,-7} {NewsBriefingBuilder.FormatAction(r.Action),-11} " +
                          $"{r.Confidence,6:P0} {price,10} {rsi,5}  {Truncate(r.Reasoning, 40)}");
    }
}

async Task RunNews()
{
    var data  = await LoadDataAsync();
    var strat = ResolveStrategy(Opt("--strategy"));
    var target = OptDecimal("--target") ?? 2.0m;
    int top    = OptInt("--top") ?? 5;
    int days   = OptInt("--days") ?? 30;

    Log("Building recommendations, cross-strategy picks, and earnings scan…");
    var recs     = await ScanEngine.AnalyzeAndRecommendAsync(data, strat, target, analysis, recommendation);
    var cross    = await ScanEngine.CrossStrategyAsync(
                       data, strategyProvider.GetStrategies(), target, analysis, recommendation, top);
    var best     = cross.Best;
    var earnings = await earningsService.GenerateAsync(
                       data.Universe, data.History, data.Summaries, data.NameLookup,
                       days, OptDecimal("--target") ?? 5.0m, useMargin: false,
                       marginPercent: 50m, marginRatePercent: 12.5m);

    // Load any held positions saved by the desktop app / a prior CLI run and
    // refresh their live price so the hold/sell section reflects current P/L.
    var portfolio = new PortfolioService();
    var held = portfolio.GetHeld().ToList();
    foreach (var h in held)
        if (data.Summaries.TryGetValue(h.Symbol, out var qs))
            h.LastPrice = qs.Price;

    var monthly = Math.Round(
        (decimal)((Math.Pow((double)(1m + target / 100m), 52.0 / 12.0) - 1.0) * 100.0), 2);

    var input = new BriefingInput
    {
        StrategyName         = strat.Name,
        UniverseDescription  = ResolveIndex(Opt("--index")).Description(),
        TargetWeeklyPercent  = target,
        TargetMonthlyPercent = monthly,
        DataSources          = new[] { "YahooFinance" },
        Recommendations      = recs,
        Positions            = held,
        Earnings             = earnings,
        BestAnyStrategy      = best,
        PerStrategy          = cross.PerStrategy,
        EarningsWindowDays   = days,
        TopCount             = top,
        GeneratedAt          = DateTime.Now,
    };

    if (json)
    {
        OutJson(new
        {
            generatedAt = input.GeneratedAt,
            strategy    = input.StrategyName,
            positions   = held.Select(ContextProjections.ProjectPosition),
            bestAnyStrategy = best.Select(b => new
            {
                b.Rec.Symbol, b.Rec.CompanyName, strategy = b.Strategy,
                action = b.Rec.Action.ToString(), score = b.Rec.Score,
                consensus = $"{b.BuyStrategyCount}/{b.StrategyCount}",
                b.Rec.LastPrice, b.Rec.RSI14
            }),
            earnings = earnings.Take(top).Select(ContextProjections.ProjectEarnings),
            topPicks = recs
                .OrderByDescending(r => r.Action is RecommendationAction.StrongBuy or RecommendationAction.Buy)
                .ThenByDescending(r => r.Confidence).Take(top).Select(ContextProjections.ProjectRecommendation),
        });
        return;
    }

    Console.WriteLine(NewsBriefingBuilder.Build(input));
}

async Task RunContext()
{
    bool toStdout = Flag("--stdout");

    var index  = ResolveIndex(Opt("--index"));
    var strat  = ResolveStrategy(Opt("--strategy"));
    var target = OptDecimal("--target") ?? 2.0m;
    int top    = OptInt("--top") ?? 5;
    int days   = OptInt("--days") ?? 30;

    var data      = await LoadDataAsync();
    var fetchTime = DateTime.Now; // market data is fresh as of this moment

    Log("Building recommendations, cross-strategy picks, earnings scan, and day picks…");
    var recs     = await ScanEngine.AnalyzeAndRecommendAsync(data, strat, target, analysis, recommendation);
    var cross    = await ScanEngine.CrossStrategyAsync(
                       data, strategyProvider.GetStrategies(), target, analysis, recommendation, top);
    var best     = cross.Best;
    var earnings = (await earningsService.GenerateAsync(
                       data.Universe, data.History, data.Summaries, data.NameLookup,
                       days, OptDecimal("--target") ?? 5.0m, useMargin: false,
                       marginPercent: 50m, marginRatePercent: 12.5m))
        .OrderByDescending(e => e.MeetsThreshold)
        .ThenByDescending(e => e.LikelihoodScore)
        .ToList();
    var dayPicks = (await dayPickService.GenerateAsync(
                       data.Universe, data.History, data.Summaries, data.NameLookup,
                       ResolveDayPickStrategy(Opt("--strategy")))).ToList();

    // Portfolio state (shared store with the desktop app), with live prices applied
    // so the briefing's hold/sell guidance reflects current P/L — same as RunNews.
    var portfolio = new PortfolioService();
    var held = portfolio.GetHeld().ToList();
    foreach (var h in held)
        if (data.Summaries.TryGetValue(h.Symbol, out var qs))
            h.LastPrice = qs.Price;
    var cash = portfolio.GetCash();
    var txns = portfolio.GetTransactions().OrderByDescending(t => t.Date).ToList();

    PortfolioPerformance? perf = null;
    if (held.Count > 0 || cash > 0m)
    {
        Log($"Computing performance for {held.Count} positions (cash ${cash:N2})…");
        perf = await PerformanceService.ComputeAsync(held, dataService, cash);
    }

    var monthly = Math.Round(
        (decimal)((Math.Pow((double)(1m + target / 100m), 52.0 / 12.0) - 1.0) * 100.0), 2);

    var briefing = NewsBriefingBuilder.Build(new BriefingInput
    {
        StrategyName         = strat.Name,
        UniverseDescription  = index.Description(),
        TargetWeeklyPercent  = target,
        TargetMonthlyPercent = monthly,
        DataSources          = new[] { "YahooFinance" },
        LastDataRefresh      = fetchTime.ToString("yyyy-MM-dd HH:mm"),
        Recommendations      = recs,
        Positions            = held,
        Earnings             = earnings,
        BestAnyStrategy      = best,
        PerStrategy          = cross.PerStrategy,
        Performance          = perf,
        CashBalance          = cash,
        EarningsWindowDays   = days,
        TopCount             = top,
        GeneratedAt          = DateTime.Now,
    });

    // The bundle carries only immutable whitelist DTOs — project everything here,
    // once; the --stdout document below reuses the same projected lists.
    var bundle = new ContextBundle
    {
        Recommendations      = recs.Select(ContextProjections.ProjectRecommendation).ToList(),
        Earnings             = earnings.Select(ContextProjections.ProjectEarnings).ToList(),
        DayPicks             = dayPicks.Select(ContextProjections.ProjectDayPick).ToList(),
        Positions            = held.Select(ContextProjections.ProjectPosition).ToList(),
        Transactions         = txns.Select(ContextProjections.ProjectTransaction).ToList(),
        CashBalance          = cash,
        Performance          = perf is null ? null : ContextProjections.ProjectPerformance(perf),
        NewsBriefingMarkdown = briefing,
        DataFetchTime        = fetchTime,
        EnabledSources       = new List<string> { "YahooFinance" }, // the CLI's single source
        UniverseDescription  = index.Description(),
        StrategyName         = strat.Name,
        GeneratedAt          = DateTime.Now,
    };

    if (toStdout)
    {
        // One combined document, whitelist-projected — no files touched.
        OutJson(new
        {
            generatedAtUtc   = bundle.GeneratedAt.ToUniversalTime(),
            dataFetchTimeUtc = bundle.DataFetchTime?.ToUniversalTime(),
            stalenessHours   = bundle.DataFetchTime.HasValue
                                   ? Math.Round((bundle.GeneratedAt - bundle.DataFetchTime.Value).TotalHours, 1)
                                   : (double?)null,
            enabledSources   = bundle.EnabledSources,
            universe         = bundle.UniverseDescription,
            strategy         = bundle.StrategyName,
            recommendations  = bundle.Recommendations,
            earnings         = bundle.Earnings,
            dayPicks         = bundle.DayPicks,
            portfolio        = new
            {
                cashBalance  = bundle.CashBalance,
                positions    = bundle.Positions,
                transactions = bundle.Transactions,
            },
            performance          = bundle.Performance,
            newsBriefingMarkdown = bundle.NewsBriefingMarkdown,
        });
        return;
    }

    var exporter = new ContextExportService();
    exporter.ExportError += Log; // exporter never throws; surface failures on stderr
    await exporter.ExportAsync(bundle);

    var manifestPath = Path.Combine(ContextExportService.ContextFolder, "manifest.json");
    if (!File.Exists(manifestPath))
    {
        Log($"Context export failed — no manifest was written to {ContextExportService.ContextFolder}.");
        return;
    }

    string manifestJson = await File.ReadAllTextAsync(manifestPath);
    int sectionFiles = 0;
    try
    {
        using var doc = JsonDocument.Parse(manifestJson);
        if (doc.RootElement.TryGetProperty("files", out var f)) sectionFiles = f.GetArrayLength();
    }
    catch { /* count is cosmetic — still print the manifest below */ }

    Log($"Wrote {sectionFiles + 1} files to {ContextExportService.ContextFolder}");
    Console.WriteLine(manifestJson.TrimEnd());
}

async Task RunEarnings()
{
    var data = await LoadDataAsync();
    int days = OptInt("--days") ?? 30;
    int top  = OptInt("--top") ?? 10;
    var target = OptDecimal("--target") ?? 5.0m;

    Log($"Scanning for earnings within {days} days…");
    var picks = (await earningsService.GenerateAsync(
                    data.Universe, data.History, data.Summaries, data.NameLookup,
                    days, target, useMargin: false, marginPercent: 50m, marginRatePercent: 12.5m))
        .OrderByDescending(e => e.MeetsThreshold)
        .ThenByDescending(e => e.LikelihoodScore)
        .Take(top)
        .ToList();

    if (json) { OutJson(picks.Select(ContextProjections.ProjectEarnings)); return; }

    Console.WriteLine($"Top {picks.Count} upcoming earnings (next {days} days)");
    Console.WriteLine(new string('─', 80));
    Console.WriteLine($"{"#",-3} {"SYM",-7} {"EARNINGS",-18} {"SCORE",6} {"MOVE",8}  FLAG");
    Console.WriteLine(new string('─', 80));
    int i = 1;
    foreach (var e in picks)
        Console.WriteLine($"{i++,-3} {e.Symbol,-7} {e.EarningsDateDisplay,-18} {e.ScoreDisplay,6} " +
                          $"{e.ExpectedMoveDisplay,8}  {e.FlagDisplay}");
}

async Task RunDayPicks()
{
    var data = await LoadDataAsync();
    var dayStrategy = ResolveDayPickStrategy(Opt("--strategy"));
    int top = OptInt("--top") ?? 10;

    Log($"Generating intraday picks ({dayStrategy})…");
    var picks = (await dayPickService.GenerateAsync(
                    data.Universe, data.History, data.Summaries, data.NameLookup, dayStrategy))
        .Take(top)
        .ToList();

    if (json) { OutJson(picks.Select(ContextProjections.ProjectDayPick)); return; }

    Console.WriteLine($"Top {picks.Count} daily picks — {dayStrategy}");
    Console.WriteLine(new string('─', 86));
    Console.WriteLine($"{"#",-3} {"SYM",-7} {"DIR",-8} {"SCORE",6} {"ENTRY",9} {"STOP",9} {"TARGET",9}  R:R");
    Console.WriteLine(new string('─', 86));
    int i = 1;
    foreach (var p in picks)
    {
        string F(decimal? d) => d.HasValue ? $"${d.Value:F2}" : "—";
        Console.WriteLine($"{i++,-3} {p.Symbol,-7} {p.DirectionDisplay,-8} {p.ScoreDisplay,6} " +
                          $"{F(p.EntryPrice),9} {F(p.StopLoss),9} {F(p.Target),9}  {p.RiskRewardDisplay}");
    }
}

async Task RunPerformance()
{
    var portfolio = new PortfolioService();
    var held = portfolio.GetHeld().ToList();
    var cash = portfolio.GetCash();

    if (held.Count == 0 && cash <= 0m)
    {
        Log("No positions or cash found (portfolio store is empty). Add them in the desktop app first.");
        if (json) OutJson(new { positions = 0, cash = 0m, totalValue = 0m, periods = Array.Empty<object>() });
        else Console.WriteLine("No open positions or cash.");
        return;
    }

    Log($"Computing performance for {held.Count} positions (cash ${cash:N2})…");
    var perf = await PerformanceService.ComputeAsync(held, dataService, cash);

    if (json)
    {
        OutJson(new
        {
            asOf         = perf.AsOf,
            positions    = perf.PositionCount,
            cash         = perf.CashBalance,
            costBasis    = perf.CostBasis,
            marketValue  = perf.MarketValue,
            totalValue   = perf.TotalValue,
            totalGain    = perf.TotalGain,
            totalGainPct = perf.TotalGainPct,
            periods = perf.Periods.Select(p => new
            {
                p.Label, p.StartDate, p.StartValue, p.CurrentValue,
                p.ChangeAmount, p.ChangePct, p.PositionsCovered, p.HasData,
            }),
        });
        return;
    }

    Console.WriteLine($"Portfolio performance — {perf.PositionCount} positions  ({perf.AsOfDisplay})");
    Console.WriteLine(new string('─', 52));
    Console.WriteLine($"  Total value  : {perf.TotalValueDisplay}");
    Console.WriteLine($"  Holdings     : {perf.MarketValueDisplay}");
    Console.WriteLine($"  Cash         : {perf.CashDisplay}");
    Console.WriteLine($"  Cost basis   : {perf.CostBasisDisplay}");
    Console.WriteLine($"  Total gain   : {perf.TotalGainDisplay} ({perf.TotalGainPctDisplay})");
    Console.WriteLine();
    Console.WriteLine($"  {"PERIOD",-9} {"RETURN",10} {"CHANGE",14}");
    Console.WriteLine("  " + new string('─', 35));
    foreach (var p in perf.Periods)
        Console.WriteLine($"  {p.Label,-9} {p.PctDisplay,10} {p.AmountDisplay,14}");
}

void RunHistory()
{
    var portfolio = new PortfolioService();
    var txns = portfolio.GetTransactions()
        .OrderByDescending(t => t.Date)
        .ThenByDescending(t => t.Type == TransactionType.Sell)
        .ToList();

    if (json) { OutJson(txns.Select(ContextProjections.ProjectTransaction)); return; }

    if (txns.Count == 0) { Console.WriteLine("No transactions recorded yet."); return; }

    Console.WriteLine($"Transaction history — {txns.Count} entries");
    Console.WriteLine(new string('─', 94));
    Console.WriteLine($"{"DATE",-13} {"TYPE",-11} {"SYM",-7} {"DETAIL",-24} {"CASH",13} {"REALIZED",12}");
    Console.WriteLine(new string('─', 94));
    foreach (var t in txns)
        Console.WriteLine($"{t.DateDisplay,-13} {t.TypeDisplay,-11} {t.Symbol,-7} " +
                          $"{Truncate(t.DetailDisplay, 24),-24} {t.CashDeltaDisplay,13} {t.RealizedGainDisplay,12}");
}

async Task RunDeposit()
{
    var amount = OptDecimal("--amount");
    if (amount is null || amount.Value <= 0m) { Log("Provide --amount greater than zero."); return; }

    var portfolio = new PortfolioService();
    portfolio.DepositCash(amount.Value, OptDateOr("--date", DateTime.Today), Opt("--note") ?? "");
    await portfolio.FlushAsync();

    if (json) OutJson(new { deposited = amount.Value, cash = portfolio.GetCash() });
    else Console.WriteLine($"Deposited ${amount.Value:N2}. Cash balance: ${portfolio.GetCash():N2}");
}

async Task RunWithdraw()
{
    var amount = OptDecimal("--amount");
    if (amount is null || amount.Value <= 0m) { Log("Provide --amount greater than zero."); return; }

    var portfolio = new PortfolioService();
    portfolio.WithdrawCash(amount.Value, OptDateOr("--date", DateTime.Today), Opt("--note") ?? "");
    await portfolio.FlushAsync();

    if (json) OutJson(new { requested = amount.Value, cash = portfolio.GetCash() });
    else Console.WriteLine($"Withdrew up to ${amount.Value:N2}. Cash balance: ${portfolio.GetCash():N2}");
}

async Task RunSell()
{
    string? sym = argv.Count > 1 && !argv[1].StartsWith('-')
        ? argv[1].ToUpperInvariant()
        : Opt("--symbol")?.ToUpperInvariant();
    if (string.IsNullOrWhiteSpace(sym)) { Log("Usage: sell SYMBOL [--price P] [--shares N] [--date yyyy-MM-dd]"); return; }

    var portfolio = new PortfolioService();
    var pos = portfolio.GetHeld().FirstOrDefault(
        h => h.Symbol.Equals(sym, StringComparison.OrdinalIgnoreCase));
    if (pos is null) { Log($"No held position found for {sym}."); return; }

    decimal price;
    if (OptDecimal("--price") is decimal p) price = p;
    else
    {
        Log($"No --price given; fetching the latest close for {sym}…");
        var bars = await dataService.GetHistoryAsync(sym, DateTime.Today.AddDays(-10), DateTime.Today);
        price = bars.Count > 0 ? bars[^1].Close : pos.EntryPrice;
    }

    int shares = OptInt("--shares") ?? pos.ShareCount;
    var txn = portfolio.SellHeld(sym, price, shares, OptDateOr("--date", DateTime.Today));
    await portfolio.FlushAsync();

    if (txn is null) { Log("Sell failed (nothing to sell)."); return; }

    if (json) { OutJson(ContextProjections.ProjectTransaction(txn)); return; }
    Console.WriteLine(
        $"Sold {txn.Shares} {txn.Symbol} @ ${txn.Price:F2} → {txn.CashDeltaDisplay} to cash " +
        $"(realized {txn.RealizedGainDisplay}). Cash balance: ${portfolio.GetCash():N2}");
}

// ─────────────────────────────────────────────────────────────────────────────
// Helpers
// ─────────────────────────────────────────────────────────────────────────────

DateTime OptDateOr(string name, DateTime fallback) =>
    DateTime.TryParse(Opt(name), out var d) ? d : fallback;

async Task<ScanData> LoadDataAsync()
{
    var index = ResolveIndex(Opt("--index"));
    Log($"Loading {index.DisplayName()} universe…");
    var universe = await ScanEngine.GetUniverseAsync(dataService, index);

    int cap = OptInt("--limit") is int l ? Math.Min(l, index.MaxSize()) : index.MaxSize();
    universe = universe.Take(cap).ToList();

    return await ScanEngine.FetchAsync(
        dataService, universe, progress: new Progress<string>(Log));
}

async Task RunBacktest()
{
    bool json   = Flag("--json");
    int  years  = Math.Clamp(OptInt("--years") ?? 2, 1, 10);
    var  target = OptDecimal("--target") ?? 2.0m;
    int  step   = Math.Clamp(OptInt("--step") ?? 5, 1, 60);

    var index = ResolveIndex(Opt("--index"));
    Log($"Loading {index.DisplayName()} universe…");
    var universe = await ScanEngine.GetUniverseAsync(dataService, index);
    int cap = OptInt("--limit") is int l ? Math.Min(l, index.MaxSize()) : index.MaxSize();
    universe = universe.Take(cap).ToList();

    Log($"Fetching {years}y of daily bars for {universe.Count} symbols (adjusted closes)…");
    var data = await ScanEngine.FetchAsync(
        dataService, universe, lookbackDays: years * 365, progress: new Progress<string>(Log));

    // One strategy or all (the engine itself excludes non-point-in-time-safe ones).
    var strategyOpt = Opt("--strategy");
    var strategies  = string.IsNullOrEmpty(strategyOpt) || strategyOpt == "all"
        ? strategyProvider.GetStrategies()
        : new[] { ResolveStrategy(strategyOpt) };

    var report = await BacktestEngine.RunAsync(
        universe, data.History, strategies.ToList(), analysis,
        new BacktestOptions { TargetPercent = target, StepBars = step },
        new Progress<string>(Log));

    if (json) { OutJson(report); return; }

    Console.WriteLine($"# Backtest — {index.DisplayName()}, {report.From:yyyy-MM-dd} → {report.To:yyyy-MM-dd}");
    Console.WriteLine($"Universe {report.UniverseSize} symbols · rebalance every {report.StepBars} bars · target {report.TargetPercent:0.##}%");
    Console.WriteLine();
    Console.WriteLine($"{"Strategy",-26} {"Signals",7} {"Hit%",6} {"Win%",6} {"AvgRet%",8} {"PF",6} {"Sharpe",7} {"MaxDD%",7}");
    Console.WriteLine(new string('-', 78));
    foreach (var s in report.Strategies)
    {
        Console.WriteLine($"{s.StrategyName,-26} {s.Signals,7} {s.HitRatePct,6:F1} {s.WinRatePct,6:F1} " +
                          $"{s.AvgReturnPct,8:F2} {s.ProfitFactor,6:F2} {s.Sharpe,7:F2} {s.MaxDrawdownPct,7:F1}");
    }
    Console.WriteLine();
    Console.WriteLine("Score-bucket calibration (hit% / win% / avg-ret% at horizon):");
    foreach (var s in report.Strategies)
    {
        var cells = s.Buckets.Select(b => b.Signals == 0
            ? $"{b.Label}: —"
            : $"{b.Label}: {b.HitRatePct:F0}%/{b.WinRatePct:F0}%/{b.AvgReturnPct:+0.0;-0.0}% (n={b.Signals})");
        Console.WriteLine($"  {s.StrategyName,-26} {string.Join("   ", cells)}");
    }
    Console.WriteLine();
    foreach (var note in report.Notes) Console.WriteLine($"⚠ {note}");
}

IndexUniverse ResolveIndex(string? key)
{
    var k = (key ?? "sp500").Replace("&", "").Replace("-", "").Replace(" ", "").ToLowerInvariant();
    return k switch
    {
        "dow" or "dow30" or "djia"            => IndexUniverse.Dow30,
        "sp100" or "s&p100" or "100"          => IndexUniverse.SP100,
        "nasdaq" or "nasdaq100" or "ndx"      => IndexUniverse.Nasdaq100,
        _                                     => IndexUniverse.SP500,
    };
}

TradingStrategy ResolveStrategy(string? key)
{
    var all = strategyProvider.GetStrategies();
    if (string.IsNullOrWhiteSpace(key)) return strategyProvider.GetDefault();

    var norm = key.Replace("-", "").Replace(" ", "").ToLowerInvariant();
    return all.FirstOrDefault(s => s.Id.Replace("-", "").Equals(norm, StringComparison.OrdinalIgnoreCase))
        ?? all.FirstOrDefault(s => s.Name.Replace(" ", "").Contains(norm, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException(
               $"Unknown strategy '{key}'. Available: {string.Join(", ", all.Select(s => s.Id))}");
}

DayPickStrategy ResolveDayPickStrategy(string? key)
{
    var k = (key ?? "momentum").Replace("-", "").Replace(" ", "").ToLowerInvariant();
    return k switch
    {
        "meanreversion" or "reversion" => DayPickStrategy.MeanReversion,
        "breakout"                     => DayPickStrategy.Breakout,
        "earnings" or "earningsplay"   => DayPickStrategy.EarningsPlay,
        _                              => DayPickStrategy.Momentum,
    };
}

static string Truncate(string? s, int max)
    => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..(max - 1)] + "…");

// JSON projections come from StockPicker.Core's ContextProjections so the CLI, the
// context exporter, and any future MCP surface all share one whitelisted shape.
// UserSettings / ApiKeys can never appear in output because only those DTOs are serialized.

void PrintUsage()
{
    Console.WriteLine(
"""
StockPicker CLI — run any strategy or build the News briefing from the terminal.

USAGE
  stockpicker <command> [options]

COMMANDS
  strategies                      List the available strategies.
  scan       --strategy <id>      Run one strategy and print the top recommendations.
  news       [--strategy <id>]    Build the full briefing: positions (hold/sell + exit
                                  strategy), the 5 best stocks across ALL strategies,
                                  top earnings plays, and top picks for the strategy.
  earnings   [--days N]           Rank upcoming-earnings candidates.
  daypicks   [--strategy <s>]     Generate intraday picks (momentum|meanreversion|
                                  breakout|earningsplay).
  context    [--stdout]           Export the full LLM context bundle (recommendations,
                                  earnings, day picks, portfolio + ledger, performance,
                                  news briefing) as whitelisted JSON/markdown files under
                                  %LOCALAPPDATA%\StockPicker\context and print the
                                  manifest. --stdout emits ONE combined JSON document to
                                  stdout instead of writing files.
  backtest   [--years N]          Point-in-time replay of every strategy over N years
                                  (default 2) of adjusted daily bars: hit-rate, win-rate,
                                  avg return, profit factor, Sharpe, max drawdown, and
                                  per-score-bucket calibration. Options: --index --limit
                                  --strategy <id|all> --target <pct> --step <bars> --json.
                                  (Value is excluded: today-only fundamentals.)
  mcp                             Run a read-only MCP (Model Context Protocol) server on
                                  stdio exposing recommendations, earnings, day picks,
                                  portfolio, ledger, news briefing, and the context
                                  manifest as tools. Register it with `claude mcp add`
                                  (see README, "Register with Claude"). stdout carries
                                  JSON-RPC only; all logs go to stderr.
  performance                     Week/month/quarter/year returns for your held
                                  positions (reads the saved portfolio).
  history                         List the transaction ledger (buys, sells, deposits,
                                  withdrawals).
  deposit    --amount N           Add cash (injection) and record it in the ledger.
  withdraw   --amount N           Remove cash (withdrawal) and record it.
  sell SYM   [--price P]          Close out a held position: credits net proceeds to
                                  cash and logs the sale. Fetches the latest price if
                                  --price is omitted. [--shares N] for a partial sell.

OPTIONS
  --strategy <id>   Strategy id (see `strategies`). Default: provider default.
  --index <name>    sp500 | dow30 | sp100 | nasdaq100.   Default: sp500.
  --limit <N>       Cap how many symbols from the index to scan.
  --top <N>         How many rows/sections to show.
  --days <N>        Earnings look-ahead window (earnings/news).  Default: 30.
  --target <P>      Weekly profit target % (scan/news) or earnings upside %.
  --amount <N>      Dollar amount for deposit / withdraw.
  --price <P>       Sell price per share (sell). Defaults to the latest close.
  --shares <N>      Shares to sell (sell). Defaults to the whole position.
  --note <text>     Optional note on a deposit / withdrawal.
  --date <date>     Transaction date (deposit/withdraw/sell). Defaults to today.
  --json            Emit machine-readable JSON instead of text. (stdout; logs go to stderr)
  --stdout          (context) Emit the combined context bundle as one JSON document on
                    stdout instead of writing files to disk.

EXAMPLES
  stockpicker strategies
  stockpicker scan --strategy momentum --index sp500 --top 15
  stockpicker news --strategy mean-reversion --json
  stockpicker earnings --days 14 --top 10
  stockpicker daypicks --strategy breakout --limit 100
  stockpicker context
  stockpicker context --stdout > context.json
""");
}
