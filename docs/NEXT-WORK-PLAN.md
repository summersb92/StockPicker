# Next Work Plan — Cutover, Quality Foundation, Polish → v1.2.0

Scope agreed 2026-07-23: Tracks **A** (cutover & release), **D** (tests + CI), **B** (port
polish). Track C (LLM round 2: push updates, gated write tools, `ask claude`) is
deliberately deferred.

Sequencing rationale: cutover first (it changes the project layout everything else
builds on), then CI (so all later work — and the release — is gated by a green
Windows+Linux matrix), then polish, then tag. **The `v1.2.0` tag is the last step** so
the auto-published release contains all of it.

---

## Phase 1 — Track A: Cutover (WPF retired, Avalonia becomes *the* app)

| # | Task | Notes |
|---|---|---|
| A1 | Retire the WPF project | `git rm -r StockPicker/`, remove from `.sln`. History preserves it; no archive copy needed. |
| A2 | Rename output | `StockPicker.Desktop` keeps its folder/project name; set `<AssemblyName>StockPicker</AssemblyName>` (and `RootNamespace` stays `StockPicker.Desktop`). Avoids a noisy folder rename while shipping the expected exe name. |
| A3 | Publish profiles | Port the WPF csproj's single-file self-contained publish block to Desktop, parameterized by RID: `win-x64` **and** `linux-x64` (`dotnet publish -c Release -r <rid>`). No `PublishTrimmed` (Avalonia + reflection). |
| A4 | Update `setup.ps1` / `setup.cmd` | Point at the Desktop project; keep the health-check behavior. |
| A5 | Update release workflow (`.github/workflows/`) | Build/publish BOTH RIDs on `v*` tags; attach `StockPicker-win-x64.exe` and `StockPicker-linux-x64` artifacts. |
| A6 | Update README + CHANGELOG | Platforms section (Windows/Linux), new run instructions, screenshots note; changelog entry for the migration. |

**Verify:** solution builds 0/0 without the WPF project; both `dotnet publish` RIDs
produce runnable single-file outputs (launch smoke-test the win-x64 one).

## Phase 2 — Track D: Tests + CI (the quality gate)

| # | Task | Notes |
|---|---|---|
| D1 | CI workflow | GitHub Actions: matrix `windows-latest` + `ubuntu-latest`; `dotnet build` + `dotnet test` on every push/PR to master. Cache NuGet. |
| D2 | High-value Core tests | Priority order: **ledger math** (`PortfolioService` buy/sell/deposit/withdraw/realized-gain invariants), **`PerformanceService`** (cost basis, trailing windows), **strategy scoring** (`AnalysisService`/`RecommendationService` on synthetic bar fixtures — deterministic, no network), **`TradingCalendar`**. |
| D3 | Export contract test | Round-trip `ContextExportService` to a temp dir: manifest lists exactly the files written; `app-state.json`/`glossary.json` parse; data-dictionary keys match serialized JSON field names. |

**Verify:** CI green on both OSes; meaningful assertions, not coverage theater.

> **Follow-up noted during D2:** `AnalysisService`'s `WeekReturn%` is computed over the
> entire fetched history window, not the trailing 5 trading days its doc comment claims.
> Tests assert direction only and don't lock in either interpretation — decide the intended
> semantic and fix code or comment (small, standalone task; affects the displayed metric).

## Phase 3 — Track B: Port polish

| # | Task | Notes |
|---|---|---|
| B1 | Row hover highlight | Replace `LoadingRow` local `Background` (blocks `:pointerover`) with row `Classes` + theme selectors so hover *and* action tints coexist. |
| B2 | Sort-arrow on restored sort | Ensure the header glyph reflects the persisted sort applied at startup. |
| B3 | Broader glossary tooltips | Positions/Earnings/DayPicks grid headers + key dialog labels (Margin %, Leverage…) sourced from `Glossary`. |
| B4 | Dead code | Remove unused `BindingProxy`; sweep stale `MIGRATION NOTE` comments that no longer apply. |

**Verify:** build 0/0; visual items GUI-checked.

## Phase 4 — Release

1. Bump version to **1.2.0** (csproj + CHANGELOG heading).
2. Commit, tag `v1.2.0`, push — the workflow publishes both artifacts.

---

**Done means:** WPF project gone; one Avalonia app shipping for Windows + Linux from a
tagged release; CI matrix green on both OSes with real Core tests; polish items closed.
