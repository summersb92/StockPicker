# WPF → Avalonia Migration Plan

**Goal:** run the StockPicker desktop GUI on Windows **and** Linux (and macOS, for free) by
replacing the WPF frontend with [Avalonia UI](https://avaloniaui.net) 11, while keeping
`StockPicker.Core` and `StockPicker.Cli` untouched.

**Bottom line:** the migration is confined to the `StockPicker` project. `Core` and `Cli` are
already `net8.0` and platform-agnostic. The cost is almost entirely in re-expressing XAML
styling (triggers → selectors) and porting two bespoke pieces of `System.Windows`-drawn UI
(the chart and the news-briefing renderer).

---

## 0. Ground truth (measured from the current tree)

| Project | TFM | Portable today? | Action |
|---|---|---|---|
| `StockPicker.Core` | `net8.0` | ✅ yes | **No change** |
| `StockPicker.Cli` | `net8.0` | ✅ yes | **No change** |
| `StockPicker` (WPF) | `net8.0-windows`, `UseWPF` | ❌ no | **Replace with Avalonia** |

WPF surface to port: **10 XAML files (~2,685 lines)** + `Themes/ModernTheme.xaml` (338 lines)
+ ~6,000 lines of C# in ViewModels/converters/controls. **No third-party UI NuGet packages** —
everything is pure WPF, which means there are no library replacements to hunt for (good) but the
custom-drawn pieces must be ported by hand (the cost).

---

## 1. Strategy: new project, not in-place conversion

Create a **new** `StockPicker.Desktop` Avalonia project rather than mutating `StockPicker.csproj`.
Reasons:

- WPF and Avalonia XAML share a file extension but not a schema; an in-place flip breaks the
  build the instant you change the SDK, giving you nothing runnable until the very end.
- A parallel project lets you port view-by-view with **both** apps building the whole time, and
  lets you delete the old `StockPicker` project only once parity is reached.
- Keep the assembly name/output identical at the end so `setup.ps1`, the release workflow, and
  docs need minimal edits.

```
StockPicker.Desktop/           # new — Avalonia
  Program.cs                   # cross-platform entry (BuildAvaloniaApp)
  App.axaml (+ .cs)
  ViewModels/                  # moved from StockPicker, ~unchanged
  Views/                       # ported .axaml
  Controls/                    # ported chart + briefing renderer
  Converters/                  # moved, namespace-only edits
  Assets/Themes/               # ModernTheme rewritten as Avalonia Styles
```

Target `net8.0` (no `-windows`), reference `Avalonia`, `Avalonia.Desktop`,
`Avalonia.Themes.Fluent`, `Avalonia.Controls.DataGrid`, and (optional) the
`CommunityToolkit.Mvvm` package if you want to drop the hand-rolled MVVM helpers.

---

## 2. File-by-file breakdown

Effort key: **🟢 mechanical** (find/replace, namespace) · **🟡 moderate** (real edits, test each) ·
**🔴 substantial** (rewrite / design decisions).

### Entry point & app shell

| File | Effort | Notes |
|---|---|---|
| `App.xaml` (13) → `App.axaml` | 🟢 | `Application` root, swap `StartupUri`/lifetime to Avalonia's `ApplicationLifetimes`. Merge theme + `ModernTheme` via `<Application.Styles>`. |
| `App.xaml.cs` → `App.axaml.cs` | 🟢 | `OnFrameworkInitializationCompleted` sets `desktop.MainWindow`. |
| *(new)* `Program.cs` | 🟢 | Standard `BuildAvaloniaApp().StartWithClassicDesktopLifetime(args)`. |
| `AssemblyInfo.cs` | 🟢 | Drop WPF `ThemeInfo`; keep version attrs. |

### ViewModels & MVVM helpers (the big win — mostly move as-is)

| File | Effort | Notes |
|---|---|---|
| `ViewModels/MainViewModel.cs` (3046) | 🟡 | Logic ports verbatim. Edits: `using System.Windows.Threading;` → `Avalonia.Threading;` (**`DispatcherTimer` exists in both** — same API). `using System.Windows.Data;` (converter refs) → `Avalonia.Data.Converters`. Any `Application.Current.Dispatcher` → `Dispatcher.UIThread`. No `MessageBox` found in VMs (good). |
| `ViewModels/RelayCommand.cs` | 🟢 | `ICommand` is `System.Windows.Input.ICommand` in the shared BCL — **Avalonia uses the same type**. Zero change likely. |
| `ViewModels/ViewModelBase.cs` | 🟢 | `INotifyPropertyChanged` — no change. |
| `ViewModels/BulkObservableCollection.cs` | 🟡 | Relies on `NotifyCollectionChangedAction.Reset` batching. Avalonia's `DataGrid`/`ItemsControl` honor standard `INotifyCollectionChanged`, but verify a `Reset` batch repaints the grid correctly; if not, switch to Avalonia's approach or emit `Add` ranges. |
| `ViewModels/ColumnToggle.cs`, `DataSourceToggle.cs` | 🟢 | POCO + `INotifyPropertyChanged`. No change. |

### Converters

| File | Effort | Notes |
|---|---|---|
| `Converters/*.cs` (4) | 🟢 | `System.Windows.Data.IValueConverter` → `Avalonia.Data.Converters.IValueConverter`. Signature differs slightly (`CultureInfo` still passed; return `Avalonia.Data.BindingOperations.DoNothing` instead of `Binding.DoNothing`). `Visibility` converters (`InverseBoolToVisibility`, `LayoutModeToVisibility`) → **return `bool`**; Avalonia uses `IsVisible` (a bool), there is no `Visibility` enum. |
| `Converters/BindingProxy.cs` | 🟡 | WPF `Freezable` trick for DataGrid binding. Avalonia has no `Freezable`; replace with a plain `AvaloniaObject` + `StyledProperty`, or drop it — Avalonia's compiled bindings can usually reach the DataContext directly (`$parent`, `#ElementName`) without the proxy. |

### Themes & styling — **the largest single effort**

| File | Effort | Notes |
|---|---|---|
| `Themes/ModernTheme.xaml` (338) | 🔴 | Rewrite as Avalonia `Styles`. WPF `Style TargetType` + `Setter` map cleanly, **but every `Trigger`/`DataTrigger`/`Style.Triggers` (91 across the app) must be re-expressed** as selectors (`Button:pointerover`, `:pressed`, `:checked`), pseudo-classes, `Classes`, or binding-driven setters. `ControlTemplate` XAML differs (`TemplateBinding` mostly works; parts/named elements differ). Budget the most time here. |

### Windows / Views

| File | Effort | Notes |
|---|---|---|
| `MainWindow.xaml` (2094) | 🔴 | The centerpiece. Two DataGrids with heavy per-cell templating/column styling; the `Style.Triggers` for row/cell state; layout via `Grid`/`DockPanel` (ports well). Do this **last**, after the theme and one small window prove the patterns. |
| `MainWindow.xaml.cs` | 🟡 | Event handlers (`Click`, `SelectionChanged`, `MouseDoubleClick` → `DoubleTapped`) rename; window lifecycle differs. |
| `Views/SettingsWindow.xaml` (243) | 🟡 | Largest dialog; good **first** port to establish patterns. |
| `Views/PositionEditWindow.xaml` (113) | 🟡 | Form + validation. |
| `Views/SellPositionWindow.xaml` (68) | 🟡 | |
| `Views/CashTransactionWindow.xaml` (60) | 🟡 | |
| `Views/TransactionHistoryWindow.xaml` (50) | 🟡 | Second DataGrid. |
| `Views/EditCashWindow.xaml` (27) | 🟢 | Smallest — good warm-up. |
| All dialog `.xaml.cs` | 🟡 | **`ShowDialog` is `async` in Avalonia** (`await w.ShowDialog<TResult>(owner)`). Every call site that opens a modal and reads a result must become `async`/`await`. |

### Custom controls — **bespoke, hand-ported**

| File | Effort | Notes |
|---|---|---|
| `Controls/StockChartControl.xaml(.cs)` | 🔴 | Custom-drawn area chart using `System.Windows.Shapes`/`Media` + `DependencyProperty`. Avalonia has near-identical `Shapes`/`Media` primitives; `DependencyProperty` → `StyledProperty`/`DirectProperty` (register + `AffectsRender`). Mechanical but non-trivial; ~1:1 primitive mapping. |
| `Controls/NewsBriefingRenderer.cs` | 🔴 | **Builds a WPF `FlowDocument`** (`System.Windows.Documents`). **Avalonia has no `FlowDocument`.** Rewrite to build an Avalonia layout (e.g. an `ItemsControl`/`StackPanel` of `SelectableTextBlock` with `InlineCollection`: `Run`, `Bold`, and clickable ticker `Run`s wired to the existing `selectCommand`/`watchCommand`). The markdown dialect is small and app-generated, so the parser logic carries over; only the *output* target changes. |

---

## 3. Phased execution

> **Progress (build-verified, solution builds 0/0 at each step; `StockPicker.Desktop`, Avalonia 11.3.13):**
> - ✅ **Phase 1 Scaffold** — project, `Program.cs`, `App.axaml`, placeholder `MainWindow`, added to solution.
> - ✅ **Phase 2 Portable C#** — `ViewModelBase`, `RelayCommand`, converters (Visibility→`IsVisible` bool), `ColumnToggle`/`DataSourceToggle`.
> - ✅ **Phase 3 Theme** — `ModernTheme.axaml` (Styles; triggers→selectors/pseudo-classes). Opt-in contract changes: accent buttons use `Classes="accent"`; WPF `GroupBox`→`<HeaderedContentControl Classes="card">`.
> - ✅ **Phase 4 Bespoke controls** — `StockChartControl` (`Render(DrawingContext)` override + `AffectsRender` StyledProperties) and `NewsBriefingRenderer` (FlowDocument→Avalonia control tree; new signature `static Control Render(...)`, host in a `ScrollViewer`).
> - ✅ **Phase 5 Dialogs** — all six (`EditCash`, `Settings`, `PositionEdit`, `SellPosition`, `CashTransaction`, `TransactionHistory`) as async `ShowDialog<T>`; MessageBox→inline validation; DatePicker uses `DateTimeOffset?`.
> - ✅ **Phase 6A VM move** — `MainViewModel` (3064 lines) + `BulkObservableCollection` compile under Avalonia. `ICollectionView`/`CollectionViewSource`→`Avalonia.Collections.DataGridCollectionView`; clipboard via injected `CopyToClipboardAsync`; explicit `RaiseCanExecuteChanged` added where WPF auto-requeried.
> - ✅ **Phase 6B MainWindow** — full port (2094 lines): both layouts (Full/Compact @1100px), all 6 tabs, 5 DataGrids with sort/reorder/visibility persistence (via `DataGridCollectionView.SortDescriptions` + code-behind), chart, interactive briefing (re-rendered on `NewsReport` change, hosted in a `ScrollViewer`), clipboard, file-picker save, and every dialog call site wired. Typed detail templates live in `Window.DataTemplates` (Avalonia only selects implicit-by-type templates there).
> - ✅ **Phase 7 Glossary UI** — searchable `GlossaryWindow` (grouped by `TermCategory`, live filter) opened from a header button; rec-grid column-header tooltips sourced from Core `Glossary` (single source of truth).
>
> **Pins & gotchas:** **Never hand-write `private void InitializeComponent() => AvaloniaXamlLoader.Load(this);`** —
> it shadows the source-generated `InitializeComponent()`, which is what assigns the `x:Name` fields after
> loading; the fields stay null and the first dereference throws NRE at startup (found + fixed post-cutover
> across all 9 views). Avalonia **11.3.13** (DataGrid lags core 11.3.18 → NU1605 if mixed). Compiled-bindings-by-default is strict (`AVLN2100`) — data-heavy subtrees set `x:CompileBindings="False"` where they bind item-type members / use a `$parent[Window]` hop. **No `CommandManager` auto-requery** → `RaiseCanExecuteChanged()` called explicitly. Avalonia DataGrid has no `RowStyle`/`AlternatingRowBackground`/`SortMemberPath` → reproduced via `LoadingRow` tint, code-behind `CustomSortComparer`, and column-index maps.
>
> **Remaining (NOT build-verifiable here):**
> 1. **GUI validation on Windows AND Linux** — the whole port compiles 0/0 but has **never been run**. Visual fidelity, the custom scrollable tab-header template, DataGrid runtime behavior (Reset-repaint on `ReplaceAll`, sort-arrow glyph on restored sorts), chart geometry, briefing link commands, clipboard, and `Process.Start` browser-open on Linux are all unverified. See each phase report for the per-item checklist.
> 2. **Cutover** (do ONLY after GUI validation passes): rename output assembly `StockPicker.Desktop`→`StockPicker`, update `setup.ps1`/release workflow/README, then delete the WPF `StockPicker` project. Optional: remove now-unused `BindingProxy`.

1. **Scaffold** (½ day) — new `StockPicker.Desktop` Avalonia project in the solution; wire
   `Core` reference; get an empty Fluent-themed window running on Windows.
2. **Move the portable C#** (½ day) — ViewModels, helpers, converters. Fix namespaces; get it
   *compiling* against Avalonia even before any view exists.
3. **Theme** (1–2 days) — port `ModernTheme` to Avalonia Styles; establish the trigger→selector
   idioms you'll reuse everywhere.
4. **Warm-up dialogs** (1 day) — `EditCashWindow` then `SettingsWindow`. Lock in the
   `async ShowDialog` pattern and form bindings.
5. **Chart + briefing renderer** (1–2 days) — the two 🔴 controls, in isolation.
6. **MainWindow** (2–3 days) — the DataGrids and remaining dialogs. Verify `DataGrid` feature
   parity column-by-column (sorting, custom cell templates, selection).
7. **Cross-platform validation** (1 day) — build/run on Linux (a VM or WSLg is enough); fix
   font, file-path, and `%LOCALAPPDATA%` assumptions (see §5).
8. **Cutover** (½ day) — rename output to `StockPicker`, update `setup.ps1`, the release
   workflow, and README; delete the old WPF project.

**Rough total: ~1.5–2.5 focused weeks**, dominated by the theme/trigger rewrite and MainWindow.

---

## 4. Known gotchas (Avalonia 11 vs WPF)

- **No `Visibility` enum** — use `IsVisible` (bool). Adjust converters.
- **No triggers** — selectors + pseudo-classes + `Classes` instead. Biggest mental shift.
- **`ShowDialog` is async** — ripples into dialog call sites.
- **No `FlowDocument`** — the briefing renderer is a rewrite (§2).
- **`x:Type`/markup-extension** differences; prefer **compiled bindings** (`x:DataType` +
  `{CompiledBinding}`) — faster and catches binding errors at build time.
- **Resources**: `StaticResource` works; `DynamicResource` used for theme-aware brushes.
- **DataGrid** is a separate package with fewer features than WPF's — the item most likely to
  need design compromises. Validate early.
- **`DispatcherTimer` and `ICommand` survive unchanged** — pleasant surprises.

---

## 5. Linux-specific checks (Core is portable, but watch these)

- `ContextExportService.ContextFolder` uses `SpecialFolder.LocalApplicationData` → resolves to
  `~/.local/share/StockPicker/context` on Linux. Fine, but confirm the app and the MCP server
  agree on the path when run on the same machine.
- File paths: ensure no `\` literals; use `Path.Combine` (already the case in Core).
- Fonts: Fluent theme ships fonts; verify the chart/briefing look right with Linux system fonts.
- Publish per-RID: `dotnet publish -c Release -r linux-x64` (and keep `win-x64`).

---

## 6. What explicitly does NOT change

- `StockPicker.Core` — all models, services, analysis, portfolio, **`ContextExportService`**,
  **`ContextProjections`**, `NewsBriefingBuilder`.
- `StockPicker.Cli` — including the **MCP server** (`McpTools.cs`) and `context` export command.
- The on-disk LLM context bundle format and the portfolio/ledger store.

Because the LLM-facing surface lives entirely in `Core`/`Cli`, **the Avalonia migration does not
touch it** — see [LLM-CONTEXT-AND-GLOSSARY.md](LLM-CONTEXT-AND-GLOSSARY.md) for the glossary /
app-state / MCP work that makes the app self-describing to Claude (all migration-independent).
