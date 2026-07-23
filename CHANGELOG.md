# Changelog

All notable changes to StockPicker will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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
