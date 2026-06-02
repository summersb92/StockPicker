# StockPicker

A WPF desktop app template for surfacing weekly buy/sell stock recommendations,
tracking a watch list, and monitoring held positions.

## Pipeline

```
 ┌──────────────┐      ┌──────────────┐      ┌─────────────────────┐
 │ Data Fetch   │──►   │ Analysis     │──►   │ Recommendations     │
 │ (universe +  │      │ (indicators, │      │ (action + dates +   │
 │  price bars) │      │  signals)    │      │  reasoning)         │
 └──────────────┘      └──────────────┘      └─────────────────────┘
 IStockDataService     IAnalysisService      IRecommendationService
                                ▲                      ▲
                                │                      │
                      ┌─────────┴──────────────────────┴──────────┐
                      │   ScanContext (strategy, profit target,   │
                      │    date window)                           │
                      └───────────────────────────────────────────┘

       User marks picks → Watch / Held (IPortfolioService)
```

## UI

Three tabs: **Recommendations**, **Watch**, **Held**. Each has its own DataGrid,
all three feed a shared **Details** pane that shows whichever item was last
selected. The Details pane includes **Buy/Sell dates** and the **holding
period** classification.

Two layout modes, auto-switched at the 1100px width breakpoint:

- **Full** (≥ 1100px): tabs on the left, Details pane on the right.
- **Compact** (< 1100px): tabs on top, Details stacked below, fewer grid columns.

## Strategies & Holding Periods

Each strategy carries a `HoldingPeriod` that drives the suggested Buy/Sell dates:

| Strategy | Holding Period | Buy → Sell logic |
|---|---|---|
| Momentum | **Quick** | Upcoming Monday → that Friday (no weekend exposure) |
| Mean Reversion | **Short** | Next trading day → +6 months *(placeholder)* |
| Breakout | **Short** | Next trading day → +6 months *(placeholder)* |
| Buy & Hold | **Long** | Next trading day → +2 years *(placeholder)* |

"Placeholder" means the Short/Long exit dates are calendar-based stand-ins
until the real strategy's signal-driven exit rules are implemented. The Quick
path is meaningful and enforces the Mon→Fri rule you specified.

## Watch & Held Lists

- **Watch** — symbols you're tracking but haven't bought. Populated via the
  "Add to Watch" button on the Recommendations tab.
- **Held** — currently-owned positions. Populated via "Mark as Held". Each
  `HeldPosition` carries the actual entry price/date (distinct from the
  algorithm's *target* price) plus the planned exit date inherited from the
  originating recommendation.

**Persistence is stubbed** — `PortfolioService` is in-memory only, so both
lists reset when the app closes. The service interface is designed so a
JSON-file implementation drops in without touching the ViewModel.

## Project Layout

```
StockPicker/
├── StockPicker.sln
└── StockPicker/
    ├── StockPicker.csproj
    ├── App.xaml / .cs
    ├── MainWindow.xaml / .cs
    ├── AssemblyInfo.cs
    ├── Converters/
    │   └── LayoutModeToVisibilityConverter.cs
    ├── Models/
    │   ├── Stock.cs
    │   ├── StockQuote.cs
    │   ├── AnalysisResult.cs
    │   ├── Recommendation.cs          BuyDate/SellDate/HoldingPeriod
    │   ├── RecommendationAction.cs
    │   ├── TradingStrategy.cs         Strategy + HoldingPeriod
    │   ├── ScanContext.cs
    │   ├── LayoutMode.cs
    │   ├── HoldingPeriod.cs           ◄── NEW — Quick/Short/Long/Unspecified
    │   └── HeldPosition.cs            ◄── NEW — owned position
    ├── Services/
    │   ├── IStockDataService.cs       STUB: data provider
    │   ├── StockDataService.cs
    │   ├── IAnalysisService.cs        STUB: analysis (strategy-aware)
    │   ├── AnalysisService.cs
    │   ├── IRecommendationService.cs  Now computes Buy/Sell dates
    │   ├── RecommendationService.cs
    │   ├── IStrategyProvider.cs       STUB: strategy list
    │   ├── StrategyProvider.cs
    │   ├── ITradingCalendar.cs        ◄── NEW — market date helpers
    │   ├── TradingCalendar.cs         STUB: weekends only, no holidays
    │   ├── IPortfolioService.cs       ◄── NEW — watch/held list contract
    │   └── PortfolioService.cs        STUB: in-memory, no persistence
    └── ViewModels/
        ├── ViewModelBase.cs
        ├── RelayCommand.cs
        └── MainViewModel.cs
```

## Stubs — Priority Order to Fill In

Every stub has detailed `<remarks>` and `// TODO` comments. The ones that
matter most for real use:

### 🚨 Before trading real money

**`Services/TradingCalendar.cs`** handles weekends but not market holidays.
Using this for real trades WILL eventually propose a Buy date of Jan 1 or
Thanksgiving. Swap in NYSE holiday data (a hardcoded annual table is fine) or
pull the market calendar from your data provider.

### Data + analysis

1. `Services/StockDataService.cs` — plug in a real API (Alpha Vantage,
   Polygon, IEX).
2. `Services/AnalysisService.cs` — branch on strategy Id and compute real
   indicators. Consider the `Skender.Stock.Indicators` NuGet package.
3. `Services/RecommendationService.cs` — replace placeholder score thresholds
   and use `context.TargetProfitMarginPercent` to filter picks whose expected
   move is below the target.

### Short/Long exit dates

The `+6 months` and `+2 years` placeholders in `RecommendationService` should
be replaced by strategy-specific signal-driven exits (trailing stop, target
hit, indicator cross). When strategies grow parameters, move the exit horizons
onto `TradingStrategy` itself.

### Held position entry

`PortfolioService.AddToHeld` currently uses the recommendation's target price
and sets ShareCount to 0. Wire up an entry dialog so the user can input the
actual fill price and share count.

### Persistence

- Strategies → load from `%AppData%\StockPicker\strategies.json`
- Watch/Held → save to `%AppData%\StockPicker\portfolio.json` on every mutation
- User settings (last-selected strategy, target %) → `settings.json`

## Getting Started

1. Open `StockPicker.sln` in Visual Studio 2022 (17.8+) with the .NET 8 SDK.
2. Optional: enable **Alpaca** in Settings if `ALPACA_API_KEY` and `ALPACA_API_SECRET` are already set as Windows environment variables.
3. Run (F5). Pick a strategy, set a target %, click **Scan This Week**.
4. In the Recommendations tab, click "Add to Watch" or "Mark as Held" on any
   pick.
5. Switch tabs to see your Watch / Held lists; the Details pane follows
   whichever item you click last.
6. Resize below ~1100px to see Compact mode kick in.
