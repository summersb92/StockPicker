# App Self-Description: Glossary + LLM Understanding

Two goals, one implementation:

1. **Help users** understand the stock terms/metrics the app uses (tooltips + a glossary panel).
2. **Help Claude/LLMs** understand *what the app means* and *what's currently going on* when asked.

The insight: both are served by a **single canonical glossary in `StockPicker.Core`**, plus a
small amount of "current app state" context. None of this depends on WPF vs Avalonia — it lives
in `Core`/`Cli` and works from the CLI and MCP server **today**, independent of the frontend
migration.

```
StockPicker.Core/Reference/Glossary.cs        ← single source of truth
        ├─→ UI tooltips           (ToolTip.Tip on headers/labels)
        ├─→ UI "Glossary" panel   (searchable list)
        ├─→ glossary.json         (context bundle → LLM reads definitions)
        └─→ MCP: explain_term / get_glossary (LLM asks on demand)
```

---

## Part 1 — Canonical glossary (Core)

> **Status: Implemented.** `StockPicker.Core/Reference/TermDefinition.cs` and `Glossary.cs`
> ship the canonical glossary (`TryGet`/`All`/`ByCategory`), and a reflection-driven drift
> test in the new `StockPicker.Core.Tests` project fails the build if any `ContextProjections`
> export field lacks a matching glossary entry (plus a key-uniqueness test).

### 1.1 The model

`StockPicker.Core/Reference/TermDefinition.cs`

```csharp
namespace StockPicker.Reference;

public enum TermCategory { Signal, Indicator, Risk, Portfolio, Strategy, Earnings, General }

/// <summary>
/// One glossary entry. <see cref="Tooltip"/> is the one-liner shown on hover;
/// <see cref="Explanation"/> is the full paragraph for the glossary panel and the LLM.
/// DEFINITIONAL ONLY — describes what a term means / how it is computed here.
/// Never prescriptive ("buy when…"); the app does not give investment advice.
/// </summary>
public sealed record TermDefinition(
    string       Key,          // matches the DTO field / UI label, e.g. "RSI14"
    string       Term,         // display name, e.g. "RSI (14-day)"
    TermCategory Category,
    string       Tooltip,      // ≤120 chars, for hover
    string       Explanation,  // 1–3 sentences, panel + LLM
    string?      Formula = null,
    string?      Range   = null);
```

### 1.2 The dictionary

`StockPicker.Core/Reference/Glossary.cs` — a static, ordered dictionary keyed by `Key`.
Seed it from the **actual** fields in `ContextProjections` and `AnalysisResult`, plus the
strategies from `StrategyProvider`. Starter set (extend as needed):

| Key | Term | Category | Tooltip (short) |
|---|---|---|---|
| `Action` | Recommendation action | Signal | Strong Buy / Buy / Hold / Sell, from the strategy scan. |
| `Confidence` | Confidence | Signal | 0–1 model confidence in the recommendation. |
| `Reasoning` | Reasoning | Signal | Plain-language explanation of why the signal fired. |
| `RSI14` | RSI (14-day) | Indicator | Momentum oscillator, 0–100; >70 overbought, <30 oversold. |
| `SMA20` / `SMA50` | Simple moving average | Indicator | Average close over the last 20 / 50 sessions. |
| `WeekReturnPct` | 1-week return | Indicator | Percent price change over the trailing week. |
| `DayChangePct` | Day change | Indicator | Percent change since the prior close. |
| `TargetPrice` | Target price | Signal | Price the strategy projects for the holding period. |
| `HoldingPeriod` | Holding period | Strategy | Intended time in the trade (e.g. quick / short / long). |
| `TargetHitProbability` | Target-hit probability | Signal | Historical odds of reaching target within the window. |
| `LikelihoodScore` | Earnings likelihood | Earnings | 0–100 rank of an upcoming-earnings candidate. |
| `ExpectedMovePct` | Expected move | Earnings | Implied one-move size around the earnings date. |
| `MomentumPct` | Momentum | Earnings | Recent trend strength feeding the earnings rank. |
| `DaysUntilEarnings` | Days to earnings | Earnings | Sessions until the next reported earnings date. |
| `IntraDayScore` | Intraday score | Signal | Day-pick ranking for a same-session trade. |
| `Direction` | Direction | Signal | Long or short bias for the day pick. |
| `StopLoss` | Stop loss | Risk | Exit level that caps the loss on a pick. |
| `RiskRewardRatio` | Risk : reward | Risk | (Target − entry) ÷ (entry − stop). Higher is better. |
| `UnrealizedGainPct` | Unrealized P&L % | Portfolio | Percent change vs. entry on an open position. |
| `BoughtOnMargin` | On margin | Portfolio | Position partly funded with borrowed money. |
| `Leverage` | Leverage | Risk | Position size ÷ your own equity in it. |
| `EquityInvested` | Equity invested | Portfolio | Your own cash in the position (excl. borrowing). |
| `InterestAccrued` | Interest accrued | Portfolio | Margin interest owed to date on the position. |
| `ReturnOnEquityPct` | Return on equity | Portfolio | Gain measured against your equity, not position size. |
| `CostBasis` | Cost basis | Portfolio | Total amount paid for current holdings. |
| `MarketValue` | Market value | Portfolio | Current worth of holdings at live prices. |
| `RealizedGain` | Realized gain | Portfolio | Profit/loss locked in by a completed sale. |
| `momentum` | Momentum strategy | Strategy | Buys recent outperformers. |
| `mean-reversion` | Mean-reversion strategy | Strategy | Buys names that sold off far from their average. |
| `breakout` | Breakout strategy | Strategy | Buys moves above recent resistance on volume. |
| `value` | Value strategy | Strategy | Buys statistically cheap names (low P/E, P/B). |
| `52w-high` | 52-week-high strategy | Strategy | Buys strength near the 52-week high. |
| `buy-and-hold` | Buy & hold strategy | Strategy | Accumulate strong names, hold long-term. |

Keep `Explanation`/`Formula` fields fuller than the tooltip. Add a lookup helper:

```csharp
public static bool TryGet(string key, out TermDefinition def);
public static IReadOnlyList<TermDefinition> All { get; }
public static IEnumerable<TermDefinition> ByCategory(TermCategory c);
```

> **Test:** a `Core.Tests` unit test that asserts every DTO field name in `ContextProjections`
> has a matching `Glossary` key. This makes drift a build failure, not a surprise.

---

## Part 2 — In-app surfaces (frontend)

Build these **in Avalonia during the migration** (the tooltip API differs from WPF, so doing it
post-port avoids porting it twice). If you want it in WPF now, it ports later like any other view.

- **Tooltips** — bind DataGrid column headers and form labels to `Glossary["RSI14"].Tooltip`.
  - WPF: `ToolTip="{Binding ...}"`  ·  Avalonia: `ToolTip.Tip="{Binding ...}"`.
- **Glossary panel** — a simple `Window`/pane: search box + `ItemsControl` grouped by
  `TermCategory`, bound to `Glossary.All`. ~1 small view, reads straight from Core.
- Optional: a small "?" affordance next to section titles that opens the panel at that term.

---

## Part 3 — LLM understanding (Core/Cli — do this now, migration-independent)

> **Status: Implemented.** `ContextExportService` now writes `glossary.json` and
> `app-state.json` and adds a per-field data dictionary to every `manifest.json` file entry;
> `MainViewModel.ScheduleContextExport` populates the app-state snapshot; and the MCP server
> exposes the three new read-only tools `get_glossary`, `explain_term`, and `get_app_state`.
> (Part 2's in-app tooltips/panel remain deferred to the Avalonia migration.)

### 3.1 Ship the glossary in the context bundle

In `ContextExportService`, add `glossary.json` (serialize `Glossary.All`) and register it in the
manifest. Now any LLM reading the bundle can define every field it sees.

### 3.2 Make the manifest a data dictionary

Extend each `ManifestFile` with a `fields` map (field → one-line meaning), sourced from the same
`Glossary`. An LLM reading `portfolio.json` then knows what `unrealizedGainPct` means without
guessing — the bundle becomes **self-describing**.

### 3.3 Add an app-state snapshot ("what's going on right now")

Today the bundle has the *data* but not the *context of attention*. Add `app-state.json`, written
by the desktop app alongside the existing export:

```jsonc
{
  "activeStrategy": "momentum",
  "activeStrategyName": "Momentum (Quick)",
  "universe": "Dow 30 (~30 stocks)",
  "selectedSymbol": "AAPL",     // the row/chart the user has focused, if any
  "activeView": "Recommendations",  // which tab/pane is showing
  "sort": { "column": "Confidence", "descending": true },
  "lastScanUtc": "2026-07-23T13:05:00Z",
  "stalenessHours": 0.2
}
```

`MainViewModel` already assembles a `ContextBundle` on a debounce (`ScheduleContextExport`) — add
these fields there (they're all values it already holds). This is the piece that lets Claude
answer *"why is this pick highlighted / what am I looking at?"* rather than only *"what's in the
data."*

### 3.4 New read-only MCP tools (`McpTools.cs`)

Mirror the existing read-only, whitelist-only pattern:

| Tool | Returns |
|---|---|
| `get_glossary` | The full `Glossary.All` as JSON. |
| `explain_term` (`term`) | One `TermDefinition`; strict resolver that lists valid keys on miss (same self-correcting style as `ResolveIndex`). |
| `get_app_state` | The `app-state.json` snapshot (or a note if the desktop app hasn't run). |

And enrich the existing tools' `[Description]`s to reference glossary keys, so the tool schemas
themselves teach the vocabulary.

---

## Part 4 — Effort & sequencing

| Work | Where | Depends on migration? | Effort |
|---|---|---|---|
| `Glossary` + `TermDefinition` + seed data + drift test | Core | ❌ no — do now | 🟡 ~½–1 day |
| `glossary.json` + manifest data-dictionary | Core (`ContextExportService`) | ❌ no | 🟢 ~½ day |
| `app-state.json` (fields into `ContextBundle`/export) | Core + tiny VM edit | ❌ no | 🟡 ~½ day |
| MCP `get_glossary` / `explain_term` / `get_app_state` | Cli | ❌ no | 🟢 ~½ day |
| Tooltips wired to `Glossary` | Frontend | ✅ do in Avalonia | 🟢 folds into each view port |
| Glossary panel/window | Frontend | ✅ do in Avalonia | 🟡 ~½ day |

**Recommended order:** build Part 1 + Part 3 **now** (pure Core/Cli — immediately improves the
CLI, the MCP server, and any LLM reading the bundle), then pick up Part 2 as part of the Avalonia
view ports so the tooltip work is done once, in the target framework.

---

## Guardrail

Every definition is **educational/definitional**, never advice. Describe what a metric *is* and
how the app computes it; do not tell the user what action to take. This keeps the glossary useful
to humans and LLMs without turning the app into an investment adviser.
