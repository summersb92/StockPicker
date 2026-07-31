# Changelog

All notable changes to StockPicker will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [2.0.0] — 2026-07-31

Major version because the desktop app users actually download changed out from under
them: the WPF build is gone, replaced by an Avalonia one that ships under a different
binary and now also targets Linux. That cutover was prepared under 1.2.0 below but never
released, so **2.0.0 is the first build to carry it** — anyone upgrading from 1.1.0 gets
both sets of changes at once.

### Breaking

- **The WPF desktop app is retired.** `StockPicker.Desktop` (Avalonia) is now *the*
  desktop app and ships as self-contained single-file builds for **win-x64 and
  linux-x64**. This is not a drop-in replacement for a 1.1.0 install: the shipped
  executable and its layout changed, so unzip fresh rather than overwriting in place.
  Settings and portfolio files are unchanged and are picked up as before. Full detail in
  the 1.2.0 notes below.

### Added

- **Analyst consensus and 1-year price targets.** The Details pane shows rating counts,
  the consensus label, the target range, and upside vs. the current price. The
  recommendations grid gains sortable **1Y Target** and **Target Δ%** columns, so the
  list can be ranked by how much upside analysts still see.
  *Coverage limit:* Yahoo only exposes price targets on a one-symbol-per-request
  endpoint (verified — `targetMeanPrice` is absent from the batch quote call), so these
  are fetched for the **top 20 rows only** and are blank below that.
- **Cash-heavy & low-debt fundamental screen** (contributed by Dakota Cooper). Cash as a
  percentage of market cap from Yahoo, plus debt/equity, net-debt/equity, and ROE from
  Finnhub, surfaced as optional columns with a filter toggle and a capped ≤5pp confidence
  tilt from the cash signal. A sortable **Cash+LowDebt** column groups qualifying stocks
  together without hiding the rest. Finnhub ratios cover the top 20 rows only, by design,
  so partial data cannot bias the ranking; see
  `docs/adr/ADR-001-cash-heavy-low-debt-data-sources.md`.
- **Post-earnings rebound scan.** The Earnings tab gains an **Upcoming / Just reported**
  mode selector. "Just reported" lists stocks that reported within a lookback window
  (default 5 days) ranked by a 0–100 **Rebound** score — how hard it sold off, how much
  analyst upside remains, and whether EPS beat anyway — for finding good quarters the
  market punished. Columns for days-since, move since earnings, drawdown, EPS vs.
  estimate, and target delta. EPS surprise prefers Finnhub and falls back to Yahoo;
  a blank EPS cell means *not published yet*, never a miss, and cannot flag a stock.
  Upcoming mode and its scoring are unchanged.
- **API key testing.** Each keyed data source in Settings gets a **Test** button with a
  quiet inline verdict (`Key OK` / `Invalid key`). Previously an expired or revoked key
  failed completely silently in release builds — the only diagnostic was a
  `Debug.WriteLine` the compiler strips — leaving blank columns indistinguishable from
  "no key configured". Finnhub is probed against the fundamentals endpoint the screen
  actually needs, so a key entitled to quotes but not fundamentals reports as invalid
  rather than passing and leaving columns empty.
- **Action and sector filters.** Dropdown filters on the recommendations grid and Daily
  Picks, composing with the existing search box and Buy-Only toggle, plus a row count
  ("Showing 12 of 200"), a Clear button, and an empty-state when filters match nothing.

### Fixed

- **Startup crash on the Avalonia app.** A hand-written `InitializeComponent` shadowed
  the generated one, leaving every named control null and throwing a
  `NullReferenceException` before the window appeared.
- **Chart not refreshing on range change.** Switching 1Y → 1W (or back) left the previous
  range rendered until another interaction forced a redraw.
- **Wasted requests on a dead Finnhub key.** The fundamentals pass walked all 20 symbols
  at a 1.1 s throttle collecting the same 401 twenty times — about 21 s per scan,
  measured. It now stops at the first 401/403 (21 s → 0.04 s) and remembers the rejected
  key for the session, so the cost is one request rather than one per scan. The rejection
  also surfaces in Settings instead of staying invisible.

### Changed

- **Test coverage and CI.** 171 Core tests run on a Windows + Linux matrix.

## [1.2.0] — 2026-07-23

> **Never released as a build.** The version was bumped and these notes written, but no
> `v1.2.0` tag was ever pushed. Everything below ships in 2.0.0 above.

### Changed

- **Desktop app migrated from WPF to Avalonia UI (cross-platform cutover).** The WPF
  `StockPicker` project is retired (history preserves it) and the Avalonia
  `StockPicker.Desktop` project is now *the* desktop app, shipping as `StockPicker(.exe)`
  for **Windows and Linux** as self-contained single-file builds
  (`dotnet publish -c Release -r win-x64|linux-x64`). All features, both layouts
  (Full/Compact), the theme, the chart, the interactive News briefing, and column/sort
  persistence were ported 1:1; macOS is untested but expected to work via Avalonia.
  The release workflow now attaches both platform artifacts on `v*` tags, and
  `setup.cmd`/`setup.ps1` point at the new project.

### Added

- **Modern UI theme.** Hand-rolled flat theme (`Themes/ModernTheme.xaml`, zero new
  dependencies) applied app-wide via implicit styles: accent-underline tab headers,
  rounded flat buttons (+ opt-in `AccentButton`), modernized DataGrid (soft headers,
  cell padding, subtle grid lines, accent selection), card-style GroupBox and tooltips
  with soft shadows, slim scrollbars, focus-accented text boxes, Segoe UI Variable
  typography, and a softened semantic palette for Buy/Sell row tints. All features and
  both layouts unchanged.

### Fixed

- **S&P 500 sector data.** The constituents CSV parser assumed "last column = sector"
  and split naively on commas; the upstream file now has extra columns (Headquarters,
  Founded, …) and quoted fields, which put founding years into the Sector field (and
  broke the sector rollup). Now parsed with a quote-aware RFC-4180 splitter reading
  the GICS Sector column positionally. Takes effect on the next fresh scan.
- **News briefing italics.** Single-asterisk `*…*` emphasis (used for strategy names)
  now renders as italics in the interactive News document, and the document no longer
  inherits the selected tab's accent color for body text.

- **Backtest harness: `stockpicker backtest`.** Point-in-time replay of every strategy
  over N years of split/dividend-adjusted daily bars (default 2). For each weekly
  rebalance, stocks are scored using only the bars available at that date, and every
  Buy signal is evaluated against the actual future: target hit-rate, win-rate, average
  return at the horizon, profit factor, per-trade Sharpe, max drawdown, and a
  **per-score-bucket calibration table** — the data that should eventually replace the
  hand-tuned score→action thresholds. Honesty notes are printed with every run
  (survivorship bias, sequential-equity simplification, excluded strategies).
- **Two new strategies.** `value` — statistically cheap stocks (low P/E and P/B,
  positive earnings, dividend cushion, value-trap volatility guard); the app's first
  fundamental (non-price-action) strategy, powered by quote-summary data that was
  fetched but previously unused. `52w-high` — the documented 52-week-high momentum
  anomaly (George & Hwang 2004). Both appear automatically in the strategy picker,
  cross-strategy consensus, per-strategy briefing sections, and the CLI.
- **Bollinger-band refinements.** Mean Reversion now rewards a close below the lower
  band (a 2σ statistical stretch, robust across volatility regimes) and fades tight
  squeezes; Breakout now detects **squeeze-fires** (bands pinched vs. their recent norm
  with a close above the upper band) — the classic coiled-spring breakout.
- **Adjusted price history from Yahoo.** Daily bars are now back-adjusted using Yahoo's
  `adjclose` (whole-bar adjustment by the adjclose/close ratio) and tagged `IsAdjusted`,
  so splits no longer appear as fake −50% moves in indicators or backtests — and Yahoo
  now participates in the merge layer's adjusted-price pool.
- **News briefing scorers can see fundamentals.** `ScanContext` now carries the live
  quote summaries (with an explicit not-point-in-time-safe caution) and a
  `SkipTargetEstimate` flag used by the backtest engine to bypass the O(n²)
  analog-matching estimate during replays.
- **LLM context bundle.** The desktop app now auto-exports everything it knows to
  `%LOCALAPPDATA%\StockPicker\context\` (`~/.local/share/StockPicker/context/` on
  Linux/macOS) as a machine-readable bundle: `manifest.json` (the entry point, written
  last), `recommendations.json`, `earnings.json`, `day-picks.json`, `portfolio.json`,
  `performance.json`, and `news-briefing.md`. Exports are debounced, whitelist-projected
  (no `UserSettings`/API-key material can ever reach a file), and written atomically
  (temp file → rename). See the README's **LLM access contract** section.
- **New CLI command: `stockpicker context`.** Generates a fresh bundle on demand and
  prints the manifest; `--stdout` skips disk entirely and emits one combined JSON
  document for piping (`stockpicker context --stdout | jq …`). Honors the usual scan
  options (`--strategy`, `--index`, `--limit`, `--target`, `--top`, `--days`).
- **New MCP server: `stockpicker mcp`.** A read-only Model Context Protocol stdio
  server with 7 tools: `get_recommendations`, `get_earnings_scan`, `get_day_picks`,
  `get_portfolio`, `get_transactions`, `get_news_briefing`, and
  `get_context_manifest`. No mutating tools are exposed; every payload is built from
  the same whitelist DTOs as the context bundle. Market data is memoized in-memory per
  index for 15 minutes. Registration steps for Claude Code / Claude Desktop are in the
  README (**Register with Claude**).
- **Self-describing context bundle + canonical glossary.** A new canonical glossary in
  Core (`StockPicker.Core/Reference/Glossary.cs`, `TermDefinition.cs`) defines every
  export field, indicator, and strategy in educational (never prescriptive) terms, with
  `TryGet`/`All`/`ByCategory` lookups. `ContextExportService` now also writes
  `glossary.json` and `app-state.json`, and each `manifest.json` file entry carries a
  per-field data dictionary (field → definition) so the bundle explains itself to an LLM
  without guesswork. The `app-state.json` snapshot ("what's going on right now") records
  the active strategy, universe, selected symbol, active view, sort, and last-scan
  freshness, populated by `MainViewModel.ScheduleContextExport`. A reflection-driven
  drift test (new `StockPicker.Core.Tests` project) fails the build if any
  `ContextProjections` export field lacks a glossary entry.
- **Three new read-only MCP tools.** `get_glossary` (the full glossary as JSON),
  `explain_term` (one definition by term/key, listing valid keys on a miss), and
  `get_app_state` (the `app-state.json` snapshot) join the existing tools. All remain
  read-only; no mutating tools are exposed.
- **StockPicker.Core as a documented, consumable library.** The WPF-free Core project
  now generates an XML documentation file, carries NuGet package metadata
  (version 1.0.0, `dotnet pack` works out of the box), and ships an API guide at
  `docs/CORE-API.md` for scripts, tools, and LLMs that consume the DLL directly.

### Fixed

- **Adjusted/unadjusted price merge.** Multi-source history merging no longer averages
  split/dividend-adjusted bars with unadjusted bars; sources are pooled by adjustment
  status so mixed feeds can't skew prices around splits.
- **Shared Wilder indicators.** RSI(14) and ATR are now computed by a single
  Wilder-smoothed implementation (`Indicators` class) used by every scanner, so the
  analysis, day-pick, and earnings services report identical values for the same bars.
- **NYSE holiday calendar.** `TradingCalendar` now knows the full NYSE holiday schedule
  (fixed holidays with observed-date shifts, floating holidays, Juneteenth from 2022,
  and Good Friday via the Easter algorithm), so recommended Buy/Sell dates no longer
  land on market holidays.
- **Symbol canonicalization.** Ticker symbols are normalized to the dash form
  (`BRK.B` → `BRK-B`) across universes, cache keys, and merge keys, so class shares
  from different providers match up instead of appearing as two symbols.
- **Portfolio persistence safety.** A corrupt `portfolio.json` found at startup is
  backed up (never silently discarded) with the error surfaced via
  `StartupLoadError`, and runtime save failures now raise `PersistenceError` so the UI
  can warn instead of silently losing a recorded trade.
- **Scan-cache hard expiry.** Cached scan results older than 24 hours are discarded and
  fresh data fetched, so stale (possibly pre-split) prices are never shown on restart.
