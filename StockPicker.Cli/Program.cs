using System;
using System.Collections.Generic;
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

    if (json) { OutJson(ranked.Select(ProjectRec)); return; }

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
    var best     = await ScanEngine.BestAcrossStrategiesAsync(
                       data, strategyProvider.GetStrategies(), target, analysis, recommendation, top);
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
            positions   = held.Select(ProjectPosition),
            bestAnyStrategy = best.Select(b => new
            {
                b.Rec.Symbol, b.Rec.CompanyName, strategy = b.Strategy,
                action = b.Rec.Action.ToString(), b.Rec.Confidence, b.Rec.LastPrice, b.Rec.RSI14
            }),
            earnings = earnings.Take(top).Select(ProjectEarnings),
            topPicks = recs
                .OrderByDescending(r => r.Action is RecommendationAction.StrongBuy or RecommendationAction.Buy)
                .ThenByDescending(r => r.Confidence).Take(top).Select(ProjectRec),
        });
        return;
    }

    Console.WriteLine(NewsBriefingBuilder.Build(input));
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

    if (json) { OutJson(picks.Select(ProjectEarnings)); return; }

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

    if (json) { OutJson(picks.Select(ProjectDayPick)); return; }

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

// ─────────────────────────────────────────────────────────────────────────────
// Helpers
// ─────────────────────────────────────────────────────────────────────────────

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

static object ProjectRec(Recommendation r) => new
{
    r.Symbol, r.CompanyName, r.Sector,
    action = r.Action.ToString(), r.Confidence,
    r.LastPrice, r.DayChangePct, r.RSI14, r.WeekReturnPct, r.SMA20, r.SMA50,
    r.TargetPrice, r.BuyDate, r.SellDate, holdingPeriod = r.HoldingPeriod.ToString(),
    r.Reasoning,
};

static object ProjectPosition(HeldPosition p) => new
{
    p.Symbol, p.CompanyName, p.EntryPrice, p.ShareCount, p.EntryDate,
    p.PlannedSellDate, holdingPeriod = p.HoldingPeriod.ToString(),
    p.LastPrice, p.UnrealizedGainPct,
};

static object ProjectEarnings(EarningsPick e) => new
{
    e.Symbol, e.CompanyName, e.NextEarningsDate, e.DaysUntilEarnings,
    e.LikelihoodScore, e.MeetsThreshold, e.ExpectedMovePct, e.MomentumPct,
    e.LastPrice,
};

static object ProjectDayPick(DayPick p) => new
{
    p.Symbol, p.CompanyName, direction = p.Direction.ToString(),
    p.IntraDayScore, p.EntryPrice, p.StopLoss, p.Target, p.RiskRewardRatio,
    p.RSI14, p.TriggerReason,
};

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

OPTIONS
  --strategy <id>   Strategy id (see `strategies`). Default: provider default.
  --index <name>    sp500 | dow30 | sp100 | nasdaq100.   Default: sp500.
  --limit <N>       Cap how many symbols from the index to scan.
  --top <N>         How many rows/sections to show.
  --days <N>        Earnings look-ahead window (earnings/news).  Default: 30.
  --target <P>      Weekly profit target % (scan/news) or earnings upside %.
  --json            Emit machine-readable JSON instead of text. (stdout; logs go to stderr)

EXAMPLES
  stockpicker strategies
  stockpicker scan --strategy momentum --index sp500 --top 15
  stockpicker news --strategy mean-reversion --json
  stockpicker earnings --days 14 --top 10
  stockpicker daypicks --strategy breakout --limit 100
""");
}
