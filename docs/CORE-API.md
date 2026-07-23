# StockPicker.Core — API Guide

A practical guide for LLMs, scripts, and external tools that consume `StockPicker.Core.dll`
directly (without the desktop app or the CLI).

---

## 1. What the DLL is

`StockPicker.Core` is a **UI-free .NET 8 class library**. It contains all of the app's
models, data services, indicator math, the scan/analysis/recommendation engine, the
portfolio ledger, and the LLM news-briefing builder. It deliberately has **no UI-framework
or Windows-only dependency**, so it runs on Windows, Linux, and macOS.

Build output:

```
StockPicker.Core\bin\<Config>\net8.0\StockPicker.Core.dll   (Config = Debug or Release)
StockPicker.Core\bin\<Config>\net8.0\StockPicker.Core.xml   (XML documentation file)
```

The XML doc file ships alongside the DLL (`<GenerateDocumentationFile>true</GenerateDocumentationFile>`),
so IDEs, `docfx`, and LLMs get IntelliSense/summaries without the source tree.

Namespaces:

- `StockPicker.Services` — services, engine, indicators, calendar, universes
- `StockPicker.Models` — POCOs and enums (`Stock`, `StockQuote`, `Recommendation`, `HeldPosition`, `Transaction`, `IndexUniverse`, `DataSourceType`, …)

---

## 2. Public surface tour

All signatures below are copied from source and verified against the current build.

### Indicators (`Services/Indicators.cs`)

Static, pure math. **Wilder-smoothed** (seed with a simple average over the first
`period` values, then `avg = (avg * (period - 1) + value) / period`). Single source of
truth shared by every scanner (analysis, day picks, earnings), so all services report
identical values for the same bars.

```csharp
public static class Indicators
{
    // Returns 50 when there is not enough data (< period + 1 closes).
    public static double RsiWilder(double[] closes, int period = 14);

    // Returns 0 when fewer than two bars are available.
    public static double AtrWilder(double[] closes, double[] highs, double[] lows, int period = 14);
}
```

### TradingCalendar (`Services/TradingCalendar.cs`)

Implements `ITradingCalendar`. All logic is expressed in US Eastern time. Includes the
**full NYSE holiday calendar**: fixed-date holidays with observed-date adjustment
(Sat→Fri, Sun→Mon), floating holidays (MLK, Presidents, Memorial, Labor, Thanksgiving)
computed per year, Juneteenth from 2022, and **Good Friday** derived from the Easter
algorithm (Meeus/Jones/Butcher).

```csharp
public class TradingCalendar : ITradingCalendar
{
    // Instance members (ITradingCalendar)
    public bool     IsTradingDay(DateTime date);      // not weekend, not NYSE holiday
    public DateTime NextTradingDay(DateTime date);    // strictly after `date`
    public DateTime NextWeekStart(DateTime date);     // this Monday if Monday, else next Monday
    public DateTime WeekEndFor(DateTime monday);      // monday + 4 = Friday

    // Static helpers
    public static bool     IsMarketHoliday(DateTime date);  // full-day NYSE holidays, cached per year
    public static DateTime TargetTradingDay();               // session picks should target right now (4 PM ET cutoff)
    public static string   FormatTradingDay(DateTime date);  // "Wednesday, May 7 2026"
    public static bool     IsToday(DateTime date);           // same calendar day as now in ET
}
```

### SymbolNormalizer (`Services/SymbolNormalizer.cs`)

Canonical symbol form is **DASH** (Yahoo's convention): uppercase, trimmed, dot→dash.

```csharp
public static class SymbolNormalizer
{
    public static string ToCanonical(string? symbol);  // " brk.b " → "BRK-B"; null/blank → ""
}
```

Every universe, cache key, and merge key uses this form. Providers that require dot form
convert at their own request boundary.

### BuiltInUniverses (`Services/BuiltInUniverses.cs`)

Hard-coded index constituent lists (no network fetch), symbols pre-canonicalized.
Last verified April 2025.

```csharp
public static class BuiltInUniverses
{
    public static IReadOnlyList<Stock> Dow30     { get; }  // 30 entries
    public static IReadOnlyList<Stock> SP100     { get; }  // 100 entries
    public static IReadOnlyList<Stock> Nasdaq100 { get; }  // 100 entries
}
```

> Note: the SP100 and Nasdaq100 lists each contain exactly **100** entries (a missing
> `PLTR` row was restored in July 2026), so `Count == 100` is safe to assert. They are
> still point-in-time snapshots (April 2025 vintage) — membership drifts as the official
> indices rebalance.

`Stock` carries `Symbol`, `Name`, `Sector`, `Exchange`.

### ScanEngine (`Services/ScanEngine.cs`)

UI-free orchestration of the scan pipeline (**fetch → analyze → recommend**), shared by
the desktop app and the CLI.

```csharp
public static class ScanEngine
{
    // Universe: built-in lists for Dow30/SP100/Nasdaq100; live fetch (data.GetUniverseAsync) for SP500.
    public static Task<IReadOnlyList<Stock>> GetUniverseAsync(IStockDataService data, IndexUniverse index);

    // Daily history (last lookbackDays) + live quote summaries, concurrency-limited.
    // Per-symbol history failures are swallowed (one bad symbol never aborts the scan).
    public static Task<ScanData> FetchAsync(
        IStockDataService data,
        IReadOnlyList<Stock> universe,
        int lookbackDays = 90,
        int maxConcurrency = 15,
        IProgress<string>? progress = null,
        CancellationToken ct = default);

    // Single-strategy analysis + recommendation, enriched with name/sector/live market data.
    public static Task<IReadOnlyList<Recommendation>> AnalyzeAndRecommendAsync(
        ScanData data,
        TradingStrategy strategy,
        decimal targetProfitPercent,
        IAnalysisService analysis,
        IRecommendationService recommendation);

    // Runs every strategy, keeps the highest-confidence Buy/StrongBuy per symbol,
    // returns the top `topCount` ranked by confidence.
    public static Task<List<BestPick>> BestAcrossStrategiesAsync(
        ScanData data,
        IReadOnlyList<TradingStrategy> strategies,
        decimal targetProfitPercent,
        IAnalysisService analysis,
        IRecommendationService recommendation,
        int topCount);

    // Fills company name, sector, and live market data onto a recommendation.
    public static void Enrich(Recommendation rec, ScanData data);
}
```

`ScanData` is the cached pipeline input: `Universe`, `History` (symbol → daily bars),
`Summaries` (symbol → `QuoteSummary`), `NameLookup`, `WeekStart`/`WeekEnd`.
`BestPick` is `readonly record struct BestPick(Recommendation Rec, string Strategy)`.

Concrete engine services: `new AnalysisService()` (implements `IAnalysisService`),
`new RecommendationService()` (implements `IRecommendationService`), and
`new StrategyProvider()` (implements `IStrategyProvider`, returns the built-in
`TradingStrategy` list).

Typical pipeline:

```csharp
var data     = new YahooFinanceStockDataService();
var universe = await ScanEngine.GetUniverseAsync(data, IndexUniverse.SP100);
var scan     = await ScanEngine.FetchAsync(data, universe, lookbackDays: 90);
var strategy = new StrategyProvider().GetStrategies()[0];
var recs     = await ScanEngine.AnalyzeAndRecommendAsync(
                   scan, strategy, targetProfitPercent: 2.5m,
                   new AnalysisService(), new RecommendationService());
```

### NewsBriefingBuilder (`Services/NewsBriefingBuilder.cs`)

Pure function that renders the copy-paste-ready, **LLM-ready markdown** market briefing
(same output as the desktop News tab and the CLI `news` command).

```csharp
public static class NewsBriefingBuilder
{
    public static string Build(BriefingInput input);
}
```

`BriefingInput` (all init-only, all defaulted) carries: `StrategyName`,
`UniverseDescription`, `TargetWeeklyPercent`, `TargetMonthlyPercent`, `DataSources`,
`LastDataRefresh`, `Recommendations`, `Positions`, `Earnings`, `BestAnyStrategy`,
`EarningsWindowDays` (30), `TopCount` (5), `GeneratedAt`. Sections rendered, in order:
scan parameters; held positions with hold/sell guidance + exit strategy; best picks
across all strategies; top earnings picks; top picks for the selected strategy; and a
closing analysis request addressed to the downstream LLM.

### Data services (`IStockDataService` implementations)

All implement:

```csharp
public interface IStockDataService
{
    DataSourceType SourceType { get; }
    Task<IReadOnlyList<Stock>>      GetUniverseAsync();
    Task<IReadOnlyList<StockQuote>> GetHistoryAsync(string symbol, DateTime from, DateTime to);
    Task<StockQuote?>               GetLatestQuoteAsync(string symbol);
    Task<Dictionary<string, QuoteSummary>> GetQuoteSummariesAsync(IEnumerable<string> symbols);
    Task<IReadOnlyList<WeeklyBar>>  GetWeeklyBarsAsync(string symbol, ChartRange range = ChartRange.Year, CancellationToken ct = default);
    Task<(double? IV, double? Theta)> GetNearTermOptionsAsync(string symbol, CancellationToken ct = default);
}
```

| Class | Construction | Notes |
|---|---|---|
| `YahooFinanceStockDataService` | `new()` — **zero config, no API key** | Default source. Handles Yahoo's cookie+crumb handshake. Only implementation with a real `GetUniverseAsync()` (live S&P 500 list). |
| `StooqStockDataService` | `new()` — no API key | History only; previous-day close; no fundamentals. |
| `AlpacaStockDataService` | `new()` — reads `ALPACA_API_KEY` / `ALPACA_API_SECRET` env vars (throws if missing; check `AlpacaStockDataService.HasEnvironmentCredentials()` first) | |
| `AlphaVantageStockDataService` | `new(string apiKey)` | |
| `FinnhubStockDataService` | `new(string apiKey)` | |
| `PolygonStockDataService` | `new(string apiKey)` | |
| `TiingoStockDataService` | `new(string apiToken)` | |

Non-Yahoo sources throw `NotSupportedException` from `GetUniverseAsync()` — the universe
always comes from Yahoo or `BuiltInUniverses`.

### PortfolioService (`Services/PortfolioService.cs`)

Persistent implementation of `IPortfolioService`. State is stored in
**`%LOCALAPPDATA%\StockPicker\portfolio.json`** (read synchronously at construction;
saved asynchronously after every mutation with a tmp→rename pattern so a crash mid-write
never corrupts data).

Key surface (see `IPortfolioService` for full XML docs):

- **Watch list**: `GetWatchList()`, `AddToWatch(Recommendation)`, `RemoveFromWatch(string)`
- **Held positions**: `GetHeld()`, `AddToHeld(Recommendation)`, `UpsertHeld(HeldPosition)`,
  `RemoveFromHeld(string)`,
  `Transaction? SellHeld(string symbol, decimal sellPrice, int shares, DateTime date)`
  (full or partial sale; credits net proceeds to cash, records realized gain; returns
  `null` if the symbol isn't held)
- **Cash**: `GetCash()`, `SetCash(decimal)` (no transaction logged),
  `DepositCash(decimal, DateTime, string)`, `WithdrawCash(decimal, DateTime, string)`
- **Ledger**: `GetTransactions()` — chronological buys/sells/deposits/withdrawals
- **Caches**: `GetCachedDayPicks(DateTime)` / `SaveDayPicksCache(...)`,
  `GetCachedMarketIndices()` / `SaveMarketIndicesCache(...)`
- **Error surfacing**:
  - `event Action<string>? PersistenceError` — raised when a runtime save fails (disk
    full, permissions). Subscribe and surface it; a silent persistence failure means the
    user believes a trade was recorded when it wasn't.
  - `string? StartupLoadError` — non-null when the portfolio file existed at startup but
    could not be parsed (the corrupt file is backed up, never silently discarded; the
    message says where).

> Scripting note: mutations save **asynchronously**. A short-lived script that mutates and
> immediately exits may want to sleep briefly, or watch `PersistenceError`, before quitting.

### PerformanceService (`Services/PerformanceService.cs`)

```csharp
public static class PerformanceService
{
    public static Task<PortfolioPerformance> ComputeAsync(
        IReadOnlyList<HeldPosition> held,
        IStockDataService data,
        decimal cash = 0m,
        DateTime? asOf = null,
        int maxConcurrency = 8,
        CancellationToken ct = default);
}
```

Reconstructs trailing-window performance (Week / Month / Quarter / Year) from each held
symbol's price history (~1 year fetched per symbol). Price-performance of *today's*
holdings, not a money-weighted return; short histories clamp to the earliest available
close; margin positions are valued on equity (loan and accrued interest netted out).

### ScanCacheService (`Services/ScanCacheService.cs`)

Persists the scan cache to **`%LOCALAPPDATA%\StockPicker\scan_cache.json`** so results
show instantly on restart.

```csharp
public class ScanCacheService
{
    public Task            SaveAsync(ScanCache cache);  // best-effort, atomic tmp→rename
    public Task<ScanCache?> LoadAsync();                // null if missing/corrupt
    public bool            Exists();                    // cache file exists on disk (contents not validated)
}
```

### ContextExportService (`Services/ContextExportService.cs`)

Writes an **LLM-consumable snapshot** of the app's current state to
**`%LOCALAPPDATA%\StockPicker\context\`**.

```csharp
public class ContextExportService
{
    public static string ContextFolder { get; }        // %LOCALAPPDATA%\StockPicker\context (auto-created)
    public event Action<string>? ExportError;          // raised once per export on any file failure
    public Task ExportAsync(ContextBundle bundle);     // never throws; null bundle is a no-op
}
```

Files written (atomic tmp→rename, same pattern as `PortfolioService`):
`manifest.json` (schema version, freshness/staleness, and a description of every file —
the LLM's entry point, written last so it only describes files that really exist),
`recommendations.json`, `earnings.json`, `day-picks.json`, `portfolio.json`
(cash + positions + ledger), `performance.json` (skipped when
`ContextBundle.Performance` is null), and `news-briefing.md` (the markdown briefing
verbatim, skipped when empty). Skipped files are deleted so the folder never
contradicts the fresh manifest.

The exporter **only serializes** — it does no fetching, scanning, recalculation, or
projection. Everything it writes arrives pre-projected inside the `ContextBundle`.

### ContextBundle (`Models/ContextBundle.cs`)

The carrier handed to `ExportAsync` (namespace `StockPicker.Models`). Every list
member is an **immutable whitelist DTO** from `ContextProjections` — never a live
domain model:

```csharp
public class ContextBundle
{
    public List<RecommendationExport> Recommendations      { get; set; }  // = new()
    public List<EarningsExport>       Earnings             { get; set; }  // = new()
    public List<DayPickExport>        DayPicks             { get; set; }  // = new()
    public List<PositionExport>       Positions            { get; set; }  // = new()
    public List<TransactionExport>    Transactions         { get; set; }  // = new()
    public decimal                    CashBalance          { get; set; }
    public PerformanceExport?         Performance          { get; set; }  // null ⇒ performance.json skipped + removed
    public string                     NewsBriefingMarkdown { get; set; }  // "" ⇒ news-briefing.md skipped + removed
    public DateTime?                  DataFetchTime        { get; set; }  // null before the first scan
    public List<string>               EnabledSources       { get; set; }  // pass a COPY, never a live UserSettings list
    public string                     UniverseDescription  { get; set; }
    public string                     StrategyName         { get; set; }
    public DateTime                   GeneratedAt          { get; set; }  // = DateTime.Now
}
```

**Projection happens at the call site, not in the exporter.** Callers run
`ContextProjections.Project*` when they assemble the bundle — the desktop app on the UI
thread (its exports are debounced ~500 ms and written off-thread), the CLI at
bundle-build time inside `RunContext`. This is deliberate:

- **Torn-read safety.** The bundle holds sealed-record *copies* snapshotted at
  construction time, so a deferred/debounced export can never observe a torn,
  half-mutated domain object.
- **Whitelist enforcement by construction.** The bundle's compile-time member types
  *are* the whitelist: nothing outside the `*Export` records — in particular
  `UserSettings`, and therefore API keys — can even be handed to the exporter, so
  nothing else can reach an exported file.

### ContextProjections (`Services/ContextProjections.cs`)

Static whitelist projections (namespace `StockPicker.Services`) shared by the desktop
app, the CLI `context` command, and the MCP server:

```csharp
public static class ContextProjections
{
    public static RecommendationExport    ProjectRecommendation(Recommendation r);
    public static PositionExport          ProjectPosition(HeldPosition p);
    public static EarningsExport          ProjectEarnings(EarningsPick e);
    public static TransactionExport       ProjectTransaction(Transaction t);
    public static DayPickExport           ProjectDayPick(DayPick p);
    public static PerformancePeriodExport ProjectPerformancePeriod(PerformancePeriod p);
    public static PerformanceExport       ProjectPerformance(PortfolioPerformance p);
}
```

The DTOs are `sealed record`s built field-by-field from the source models. Enum-typed
source properties (`Action`, `HoldingPeriod`, `Type`, `Direction`) are exported as
strings; `PerformanceExport` carries the raw numbers without the model's `*Display`
formatting properties.

```csharp
public sealed record RecommendationExport(
    string Symbol, string CompanyName, string Sector, string Action, double Confidence,
    decimal? LastPrice, double? DayChangePct, double? RSI14, double? WeekReturnPct,
    double? SMA20, double? SMA50, decimal? TargetPrice, DateTime? BuyDate,
    DateTime? SellDate, string HoldingPeriod, string Reasoning);

public sealed record PositionExport(
    string Symbol, string CompanyName, decimal EntryPrice, int ShareCount,
    DateTime EntryDate, DateTime? PlannedSellDate, string HoldingPeriod,
    decimal? LastPrice, double? UnrealizedGainPct, bool BoughtOnMargin,
    decimal MarginPercent, decimal MarginInterestRatePercent, decimal Leverage,
    decimal EquityInvested, decimal InterestAccrued, double? ReturnOnEquityPct);

public sealed record EarningsExport(
    string Symbol, string CompanyName, string Sector, DateTime NextEarningsDate,
    int DaysUntilEarnings, double LikelihoodScore, bool MeetsThreshold,
    double ExpectedMovePct, double MomentumPct, decimal? LastPrice);

public sealed record TransactionExport(
    DateTime Date, string Type, string Symbol, string CompanyName, int Shares,
    decimal Price, decimal GrossAmount, decimal CashDelta, decimal? RealizedGain,
    bool OnMargin, string Note);

public sealed record DayPickExport(
    string Symbol, string CompanyName, string Sector, string Direction,
    double IntraDayScore, decimal? EntryPrice, decimal? StopLoss, decimal? Target,
    double? RiskRewardRatio, double? RSI14, string TriggerReason);

public sealed record PerformancePeriodExport(
    string Label, DateTime StartDate, decimal StartValue, decimal CurrentValue,
    double ChangePct, bool HasData);

public sealed record PerformanceExport(
    DateTime AsOf, int PositionCount, decimal CostBasis, decimal MarketValue,
    decimal CashBalance, decimal TotalValue, decimal TotalGain, double TotalGainPct,
    List<PerformancePeriodExport> Periods);
```

> **MCP server.** The CLI also hosts a read-only MCP stdio server (`stockpicker mcp`,
> 7 tools) whose tool payloads are built from these same DTOs — see the README's
> **LLM access contract → Register with Claude** section for the tool inventory and
> registration steps.

---

## 3. Recipe A — dotnet harness (proven)

The most reliable way to consume the library is a tiny console project with a
`ProjectReference`. This exact pattern is used by the repo's smoke tests.

`harness/harness.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\StockPicker.Core\StockPicker.Core.csproj" />
  </ItemGroup>
</Project>
```

`harness/Program.cs` (top-level statements):

```csharp
using StockPicker.Services;

// Indicators — Wilder-smoothed RSI over a synthetic ramp.
double[] closes = Enumerable.Range(1, 30).Select(i => 100.0 + i).ToArray();
Console.WriteLine($"RSI(14) of a rising series : {Indicators.RsiWilder(closes, 14):F1}"); // 100.0

// Trading calendar — Thanksgiving 2026 is an NYSE holiday.
Console.WriteLine($"2026-11-26 market holiday? : {TradingCalendar.IsMarketHoliday(new DateTime(2026, 11, 26))}"); // True

// Symbol canonicalization — dot form to Yahoo dash form.
Console.WriteLine($"Canonical ' brk.b '        : {SymbolNormalizer.ToCanonical(" brk.b ")}"); // BRK-B

// Built-in universes.
Console.WriteLine($"S&P 100 universe size      : {BuiltInUniverses.SP100.Count}"); // 100
```

Run it:

```
dotnet run --project harness
```

No NuGet packages, no API keys, no network access — everything above is pure in-process
computation.

---

## 4. Recipe B — PowerShell 7+

The DLL can be loaded directly into PowerShell for quick one-liners:

```powershell
# Build once, then load:
dotnet build C:\ClaudeWorking\StockPicker\StockPicker.Core\StockPicker.Core.csproj -c Release
Add-Type -Path "C:\ClaudeWorking\StockPicker\StockPicker.Core\bin\Release\net8.0\StockPicker.Core.dll"

# Static calls:
[StockPicker.Services.TradingCalendar]::IsMarketHoliday([datetime]"2026-11-26")   # True (Thanksgiving)
[StockPicker.Services.SymbolNormalizer]::ToCanonical(" brk.b ")                   # BRK-B
[StockPicker.Services.BuiltInUniverses]::SP100.Count                              # 100
[StockPicker.Services.Indicators]::RsiWilder([double[]](1..30 | % { 100 + $_ }), 14)  # 100

# Instances work too:
$cal = [StockPicker.Services.TradingCalendar]::new()
$cal.NextTradingDay([datetime]"2026-07-03")   # Monday 2026-07-06 (July 4 observed Fri 7/3)
```

> **WARNING — pwsh 7+ only.** Windows PowerShell 5.1 runs on .NET Framework 4.x and
> **cannot load .NET 8 assemblies** — `Add-Type` fails with
> `ReflectionTypeLoadException` / "assembly is built by a runtime newer than the
> currently loaded runtime". Use PowerShell 7+ (`pwsh`), which runs on .NET 8.

---

## 5. Introspection — enumerating the API without source

Two options when you have only the DLL:

1. **XML documentation file.** `StockPicker.Core.xml` sits next to the DLL and contains
   every documented member ID (`M:StockPicker.Services.Indicators.RsiWilder(System.Double[],System.Int32)`, …)
   plus its `<summary>`. It is plain XML — grep it, or feed it to an LLM wholesale.

2. **Decompilation with `ilspycmd`.**

   ```
   dotnet tool install -g ilspycmd
   ilspycmd StockPicker.Core\bin\Release\net8.0\StockPicker.Core.dll -o decompiled\
   ```

   This regenerates readable C# for the whole assembly. For a types-only overview use
   reflection instead:

   ```powershell
   # pwsh 7+
   [System.Reflection.Assembly]::LoadFrom("...\StockPicker.Core.dll").GetExportedTypes() |
       Sort-Object FullName | Select-Object FullName
   ```

---

## 6. NuGet option

The project now carries package metadata (`Version` 1.0.0, `Description`, `Authors`), so
packing works out of the box:

```
dotnet pack StockPicker.Core/StockPicker.Core.csproj -c Release
```

This produces `StockPicker.Core\bin\Release\StockPicker.Core.1.0.0.nupkg` (XML docs
included). Push it to a local or private feed and consume it with a normal
`<PackageReference Include="StockPicker.Core" Version="1.0.0" />` instead of a
`ProjectReference`.
