# StockPicker.Core.Tests

A boring, reliable, **Linux-runnable** smoke/unit gate for the UI-free
`StockPicker.Core` library. It covers the pure, deterministic pieces of the
engine — Wilder RSI/ATR math, symbol normalization, the NYSE trading calendar,
trailing-window performance reconstruction (via an in-memory fake data service),
and the markdown news-briefing builder — with explicitly pinned dates and canned
data so the suite is stable across machines and time zones. It targets `net8.0`
only and pulls in no Windows dependencies, so it builds and runs on a plain
Linux CI runner with just the .NET 8 SDK.

## Run

```
dotnet test StockPicker.Core.Tests/StockPicker.Core.Tests.csproj -c Release --nologo
```

> Note: the calendar tests resolve the `America/New_York` time-zone id, so the
> runner needs tzdata installed (standard on the .NET SDK Linux images).

## Deliberately not covered (follow-ups)

- **`PortfolioService`** and **`ScanCacheService`** — these resolve hardcoded
  static paths under `%LOCALAPPDATA%`, so exercising them would read from and
  write to the real user profile and pollute live app state. Testing them safely
  needs a path-injection refactor (inject the base directory / an abstraction
  instead of computing `Environment.GetFolderPath(...)` inline).
- **The network-bound `*StockDataService` implementations** (Yahoo Finance,
  Stooq, Alpaca, Alpha Vantage, Finnhub, Polygon, Tiingo) — these make live HTTP
  calls to third-party APIs. They belong in a separate, opt-in integration-test
  suite that can be gated/skipped on CI rather than in this fast offline gate.
