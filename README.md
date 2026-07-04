# StockPicker

A stock-screening suite that surfaces buy/sell recommendations, intraday "day picks",
upcoming-earnings plays, and an LLM-ready market briefing — then tracks a watch list and
held positions with trailing performance.

It ships as **two front-ends over one shared engine**:

- **StockPicker** — a WPF desktop app (Windows, .NET 8).
- **stockpicker** — a cross-platform CLI (Windows / Linux / macOS, .NET 8).

> ⚠️ **Not investment advice.** All signals are algorithmic heuristics for research and
> educational use. Day-trading, earnings, and margin features are especially risky.

---

## Solution layout

```
StockPicker.sln
├── StockPicker.Core/   ← all logic: models, data services, analysis/recommendation
│                          engine, day-picks, earnings, news briefing, performance.
│                          WPF-free, net8.0 — the single source of truth.
├── StockPicker/        ← WPF desktop app (net8.0-windows). References Core.
└── StockPicker.Cli/    ← cross-platform CLI (net8.0). References Core.
```

Dependency direction: `StockPicker` → `StockPicker.Core` ← `StockPicker.Cli`.
Both front-ends consume the same models and services, so a fix in Core lands everywhere.

---

## Features

- **Recommendations scan** — runs a strategy across an index universe and ranks picks by
  action (StrongBuy → StrongSell) and confidence, enriched with live quote data.
- **Daily (intraday) picks** — momentum / mean-reversion / breakout / earnings-play
  setups with ATR-based entry, stop, and target levels and a risk:reward ratio.
- **Earnings scan** — finds names reporting within a window and scores the likelihood of
  hitting a target move (blending option-implied and realized volatility, momentum, and
  drift); optional margin math (leverage, interest cost, net return).
- **News briefing** — a copy-paste-ready Markdown brief (positions hold/sell guidance,
  best pick per strategy, top earnings plays, top picks, and an analysis request) built
  for handing to an LLM. Available in the desktop **News** tab and the CLI `news` command.
- **Portfolio** — a **Watch** list and **Held** positions, with manual add/edit of entry
  price, share count, dates, and notes. A position can be flagged as **bought on margin**
  (initial margin % + annual interest rate); its gain is then reported as the leveraged,
  interest-net **return on equity**, and a leverage badge shows in the grid.
- **Performance** — trailing week / month / quarter / year returns, cost basis, market
  value, and total unrealized gain for held positions. For margin positions, cost basis is
  the equity invested and the market value is netted of the loan and accrued interest.
- **Cash holdings** — a persisted cash balance counts toward **total portfolio value**
  (holdings + cash); a cash-only portfolio still shows a value. **Edit Cash** (the ✎ on the
  Cash card) sets the balance directly for corrections/testing without logging a transaction.
- **Transaction ledger** — buying a position pulls the investor's own money from cash (the
  **full cost** for a cash buy, the **margin down payment** for a margin buy; cash may go
  negative); selling credits the **net proceeds** (gross less any repaid margin loan +
  accrued interest) back to cash and records the realized gain — so a buy/sell round-trip
  nets to your realized gain. Editing a position moves only the change in equity in/out of
  cash and updates its Buy line; a raw **Remove** refunds the buy's cash and drops the line.
  Cash **deposits** and **withdrawals** are tracked too, and a **History** view lists every
  buy, sell, deposit, and withdrawal.
- **Desktop UX** — responsive Full/Compact layout, a market-index ticker, a weekly price
  chart, per-column show/hide + reorder + sort (all persisted), search + Buy-Only filter
  with a live result count and one-click clear, and "Ask AI" hand-offs (Claude / Gemini /
  Copilot) from any row.
- **Instant restart** — the last scan is cached to disk and restored on launch, so the app
  shows recommendations immediately while any stale data refreshes in the background.

---

## Data providers & API keys

The desktop app can enable one or more sources (Settings → Data Sources). The CLI uses
**Yahoo Finance only**. All providers are fully implemented.

| Provider       | API key | Free-tier notes                                  | Provides                       |
|----------------|:-------:|--------------------------------------------------|--------------------------------|
| Yahoo Finance  | No      | Unofficial endpoints; no signup. **Default.**    | History, quotes, weekly bars, options IV/Theta |
| Stooq          | No      | Public CSV.                                      | History only                   |
| Alpha Vantage  | Yes     | 25 calls/day, 5/min.                             | History, quotes                |
| Finnhub        | Yes     | 60 req/min.                                      | History, quotes, profile       |
| Polygon.io     | Yes     | 5 req/min, 15-min delayed.                       | Adjusted daily bars, reference |
| Tiingo         | Yes     | 500 symbols/day, ~1 req/sec.                     | EOD + IEX real-time            |

Yahoo Finance requires no setup and is the recommended default. For the keyed providers,
register on the provider's site and paste the key into Settings → Data Sources.

### Universes

Pick the index to scan: **Dow 30**, **S&P 100**, **NASDAQ-100** (all built in and
bundled), or **S&P 500** (constituents fetched live, with a built-in fallback list).
A scan limit caps how many symbols are pulled.

---

## Strategies & holding periods

| Strategy        | Holding period | Signal basis (real indicators)                                  |
|-----------------|----------------|-----------------------------------------------------------------|
| Momentum        | Quick (intraweek) | Weekly return, price vs SMA20/50, RSI, volume surge          |
| Mean Reversion  | Short (weeks–months) | Distance below SMA20, oversold RSI — snap-back candidates |
| Breakout        | Short (weeks–months) | Price breaking resistance on a volume/ATR expansion      |
| Buy & Hold      | Long (years)   | Long-term trend (price vs SMA50, SMA alignment), healthy RSI, steady low-volatility appreciation |

The analysis layer computes **real** indicators (SMA20/50, RSI14, week return, volume
trend, ATR, volatility) and each strategy has its own scorer that emits human-readable
signal explanations. A score maps to an action (≥+2 StrongBuy, ≥+0.5 Buy, ≤−0.5 Sell,
≤−2 StrongSell, else Hold); confidence is `min(1, |score| / 3)`.

> **Still placeholder:** the score→action thresholds are not yet backtest-calibrated, and
> Short/Long **exit dates** are calendar stand-ins (≈6 months / ≈2 years) pending
> signal-driven exits (trailing stop / target hit / indicator cross). The Quick path's
> Monday-open → Friday-close window is real.

---

## Persistence

State is stored as JSON under `%LOCALAPPDATA%\StockPicker\` (Windows) /
`~/.local/share/StockPicker/` (Linux/macOS), written atomically (temp file → rename):

| File                 | Contents                                                      |
|----------------------|--------------------------------------------------------------|
| `portfolio.json`     | Watch list, held positions, cached daily picks, index snapshots |
| `user_settings.json` | UI preferences — column visibility/order, sort, last strategy, targets |
| `scan_cache.json`    | The last completed scan (universe, history, quotes) for instant restart |

The portfolio store is shared: positions added in the desktop app are read by the CLI
`news` and `performance` commands.

---

## CLI usage

```
stockpicker <command> [options]
```

| Command        | Purpose                                                              |
|----------------|---------------------------------------------------------------------|
| `strategies`   | List available strategies and their holding periods.                |
| `scan`         | Run one strategy and print the top recommendations.                 |
| `news`         | Build the full Markdown briefing (positions + best-across-strategies + earnings + top picks). |
| `earnings`     | Rank upcoming-earnings candidates within a window.                  |
| `daypicks`     | Generate intraday picks (`momentum`/`mean-reversion`/`breakout`/`earnings-play`). |
| `context`      | Export the full LLM context bundle to the context folder and print the manifest; `--stdout` emits one combined JSON document instead (see **LLM access contract**). |
| `mcp`          | Run a read-only MCP stdio server exposing the app's data as tools (see **Register with Claude** under the LLM access contract). |
| `performance`  | Trailing week/month/quarter/year returns for saved held positions.  |
| `history`      | List the transaction ledger (buys, sells, deposits, withdrawals).   |
| `deposit`      | `--amount N` — add cash and record it.                              |
| `withdraw`     | `--amount N` — remove cash and record it.                           |
| `sell SYM`     | Close a position (`--price`, `--shares`): credits net proceeds to cash, logs the sale. |

**Options:** `--strategy <id>`, `--index <sp500|dow30|sp100|nasdaq100>`, `--limit <N>`,
`--top <N>`, `--days <N>` (earnings/news window), `--target <P>` (profit/upside %),
`--json` (machine-readable; results to stdout, logs to stderr).

```bash
stockpicker strategies
stockpicker scan --strategy momentum --index sp500 --top 15
stockpicker news --strategy mean-reversion --json
stockpicker earnings --days 14 --top 10
stockpicker daypicks --strategy breakout --limit 100
```

---

## Getting started

**Prerequisites:** .NET 8 SDK; Visual Studio 2022 (17.8+) for the WPF app.

```bash
# Build everything
dotnet build StockPicker.sln -c Release

# Run the CLI (no API key needed — uses Yahoo Finance)
dotnet run --project StockPicker.Cli -- scan --strategy momentum --index dow30 --top 10

# Run the desktop app
dotnet run --project StockPicker          # or open StockPicker.sln and press F5
```

**Single-file desktop publish** (self-contained, no .NET install on the target PC):

```bash
dotnet publish StockPicker/StockPicker.csproj -c Release -r win-x64
# → StockPicker/bin/Release/net8.0-windows/win-x64/publish/StockPicker.exe
```

In the desktop app: pick a strategy, set a target %, click **Scan**, then use
**Add to Watch** / **Mark as Held** on any pick. Resize below ~1100px to see Compact mode.

---

### Easiest path — one double-click (no tools to learn)

**Double-click `setup.cmd`.** It checks for the free Microsoft .NET 8 toolkit
(installing it for you if it's missing via winget), builds the app, and runs a
self-test that proves the recommendation engine works. It's safe to re-run any
time, and it finishes by telling you exactly how to launch the desktop app and
the command-line tool. Nothing else is required.

### Manual path (Visual Studio)

1. Open `StockPicker.sln` in Visual Studio 2022 (17.8+) with the .NET 8 SDK.
2. Optional: enable **Alpaca** in Settings if `ALPACA_API_KEY` and `ALPACA_API_SECRET` are already set as Windows environment variables.
3. Run (F5). Pick a strategy, set a target %, click **Scan This Week**.
4. In the Recommendations tab, click "Add to Watch" or "Mark as Held" on any
   pick.
5. Switch tabs to see your Watch / Held lists; the Details pane follows
   whichever item you click last.
6. Resize below ~1100px to see Compact mode kick in.

---

## Known limitations / roadmap

- **Recommendation thresholds** are heuristic, not backtest-calibrated.
- **Short/Long exit dates** are calendar placeholders pending signal-driven exits.
- **Performance** is a price-return view (current shares valued at the window-start price);
  it does not track transaction history or money-weighted returns.
- **CLI** is Yahoo-only and has no API-key configuration.

---

## LLM access contract

StockPicker publishes everything it knows as a machine-readable **context bundle** so an
LLM (or any tool) can consume the app's state without touching internals:

```
%LOCALAPPDATA%\StockPicker\context\          (Windows)
~/.local/share/StockPicker/context/          (Linux / macOS)
```

**Read the manifest first.** `manifest.json` is the entry point: it carries the schema
version, freshness stamps, scan parameters, and a description + record count for every
other file actually present. A consumer should read `manifest.json`, then open only the
section files it needs — never assume a file exists without the manifest listing it.

**Staleness semantics.** The manifest's `stalenessHours` is the gap between when the
underlying market data was fetched (`dataFetchTimeUtc`) and when the bundle was assembled
(`generatedAtUtc`). The desktop app rewrites the bundle on every completed scan and on
every portfolio change, so its bundle tracks the app's live state; the CLI `context`
command generates a fresh bundle on demand (its staleness is ~0 because it fetches and
exports in one run). Treat a bundle with large `stalenessHours` — or an old
`generatedAtUtc` — as historical, not live.

### File inventory

| File                   | Contents                                                             |
|------------------------|----------------------------------------------------------------------|
| `manifest.json`        | Entry point: schema version, `generatedAtUtc`, `dataFetchTimeUtc`, `stalenessHours`, enabled data sources, universe, strategy, and the list of files below with descriptions + record counts. |
| `recommendations.json` | Whitelisted strategy recommendations (action, confidence, reasoning, key indicators, trade dates). |
| `earnings.json`        | Upcoming-earnings candidates with likelihood score, expected move, and momentum. |
| `day-picks.json`       | Intraday picks with direction, entry/stop/target levels, and risk:reward. |
| `portfolio.json`       | Cash balance, open positions (incl. margin detail and unrealized P&L), and the full transaction ledger. |
| `performance.json`     | Aggregate holdings performance: cost basis, market value, total gain, trailing week/month/quarter/year returns (omitted when there is nothing to compute). |
| `news-briefing.md`     | The markdown News briefing, verbatim (omitted when empty).           |

Files are written atomically (temp file → rename), and the manifest is written **last**,
so a manifest never references a half-written or missing file.

### CLI access

```bash
# Write the bundle to the context folder and print manifest.json to stdout
stockpicker context

# Skip disk entirely — one combined JSON document on stdout
stockpicker context --stdout

# Pipe it (results go to stdout; progress goes to stderr, so pipes stay clean)
stockpicker context --stdout | jq '.portfolio.cashBalance'                     # bash
stockpicker context --stdout | ConvertFrom-Json | % { $_.strategy }            # PowerShell
```

`context` honors the usual scan options (`--strategy`, `--index`, `--limit`, `--target`,
`--top`, `--days`). The combined `--stdout` document contains a manifest-like header
(`generatedAtUtc`, `dataFetchTimeUtc`, `stalenessHours`, `enabledSources`, `universe`,
`strategy`) plus `recommendations`, `earnings`, `dayPicks`,
`portfolio { cashBalance, positions, transactions }`, `performance`, and
`newsBriefingMarkdown`.

### MCP server (`stockpicker mcp`)

`stockpicker mcp` runs a **read-only** [Model Context Protocol](https://modelcontextprotocol.io)
server over stdio, exposing the same whitelisted data as live tools:

| Tool                   | Parameters (all optional)                            | Returns |
|------------------------|------------------------------------------------------|---------|
| `get_recommendations`  | `strategy`, `index`, `top`, `targetPercent`          | Strategy-scan recommendations (JSON). |
| `get_earnings_scan`    | `windowDays`, `targetUpPercent`, `index`             | Ranked upcoming-earnings candidates (JSON). |
| `get_day_picks`        | `strategy`, `index`                                  | Intraday picks with entry/stop/target (JSON). |
| `get_portfolio`        | —                                                    | Cash, open positions (live prices), performance (JSON). |
| `get_transactions`     | —                                                    | The full ledger, newest first (JSON). |
| `get_news_briefing`    | `strategy`, `index`                                  | The markdown News briefing. |
| `get_context_manifest` | —                                                    | `manifest.json` from the context folder (or a note if none exists). |

Market data (universe + 90-day history) is fetched once per index and memoized in-memory
for **15 minutes**, so consecutive tool calls are fast. stdout carries JSON-RPC frames
only — all progress/logging goes to stderr. The server exposes **no mutating tools**
(`deposit`/`withdraw`/`sell` remain CLI-only), and every payload is built from the same
`ContextProjections` whitelist DTOs as the context bundle.

### Register with Claude

Build the CLI first, then register the **built DLL** — do **not** register `dotnet run`
(MSBuild writes build output to stdout, which corrupts the JSON-RPC channel; this is the
most common MCP registration failure):

```bash
dotnet build -c Release StockPicker.Cli/StockPicker.Cli.csproj

# Claude Code (project scope — writes .mcp.json next to your repo)
claude mcp add --transport stdio stockpicker --scope project -- dotnet <path-to>\StockPicker.Cli\bin\Release\net8.0\stockpicker.dll mcp
```

The equivalent `.mcp.json` entry (note the assembly is named `stockpicker.dll`, from the
project's `AssemblyName`):

```json
{
  "mcpServers": {
    "stockpicker": {
      "type": "stdio",
      "command": "dotnet",
      "args": ["<path-to>\\StockPicker.Cli\\bin\\Release\\net8.0\\stockpicker.dll", "mcp"]
    }
  }
}
```

Claude Desktop (`claude_desktop_config.json`, via Settings → Developer → Edit Config):

```json
{
  "mcpServers": {
    "stockpicker": {
      "command": "dotnet",
      "args": ["<path-to>\\StockPicker.Cli\\bin\\Release\\net8.0\\stockpicker.dll", "mcp"]
    }
  }
}
```

Verify the server standalone with `npx @modelcontextprotocol/inspector dotnet <path-to>\StockPicker.Cli\bin\Release\net8.0\stockpicker.dll mcp`.

### Security guarantee

**API keys and user settings are never exported.** Every file (and the `--stdout`
document) is built exclusively from explicit whitelist projections
(`StockPicker.Core`'s `ContextProjections` DTOs); `UserSettings` is never serialized,
referenced, or accepted by the export path, so no credential material can reach any
output.

### Direct Core consumption

Tools that want richer access than the file bundle can reference the WPF-free
`StockPicker.Core` DLL directly — see `docs/CORE-API.md` for the Core API surface.
