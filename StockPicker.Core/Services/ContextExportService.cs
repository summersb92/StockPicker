using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using StockPicker.Models;
using StockPicker.Reference;

namespace StockPicker.Services
{
    /// <summary>
    /// Writes an LLM-consumable snapshot of the app's current state to
    /// <c>%LOCALAPPDATA%\StockPicker\context\</c>:
    ///
    ///   manifest.json         — schema version, freshness, and a description of every
    ///                           file written, so an LLM reading it alone knows what is
    ///                           available and how stale it is.
    ///   recommendations.json  — whitelisted strategy recommendations.
    ///   earnings.json         — whitelisted upcoming-earnings picks.
    ///   day-picks.json        — whitelisted intraday picks.
    ///   portfolio.json        — cash balance + whitelisted positions and ledger.
    ///   performance.json      — the whitelisted PerformanceExport (skipped when null).
    ///   news-briefing.md      — the markdown News briefing verbatim (skipped when empty).
    ///   glossary.json         — canonical definitions for every field/indicator/strategy.
    ///   app-state.json        — the user's current focus (strategy, universe, selection, sort, freshness).
    ///
    /// Every file is written with the same atomic tmp→rename pattern as
    /// <see cref="PortfolioService"/> so a crash mid-write never leaves a corrupt file.
    ///
    /// SECURITY: <see cref="ContextBundle"/> already carries only the whitelist DTOs
    /// from <see cref="ContextProjections"/> (plus scalar fields), and this class
    /// serializes exactly what it is given. UserSettings — and therefore ApiKeys —
    /// is never accepted, referenced, or written by this class.
    ///
    /// ERROR HANDLING: <see cref="ExportAsync"/> never throws. Per-file failures are
    /// caught and collected, and a single summary message is raised through the
    /// <see cref="ExportError"/> event (mirroring PortfolioService.PersistenceError) so
    /// the UI can surface it in the status bar.
    /// </summary>
    public class ContextExportService
    {
        /// <summary>Folder the context bundle is written to (auto-created on export).</summary>
        public static string ContextFolder { get; } =
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "StockPicker", "context");

        /// <summary>
        /// Raised (once per export) with a human-readable message when one or more files
        /// could not be written. The chosen error mechanism — an event rather than a
        /// result object — mirrors <c>IPortfolioService.PersistenceError</c>.
        /// </summary>
        public event Action<string>? ExportError;

        // camelCase + string enums + indentation: the bundle is meant to be read by
        // LLMs and matches the manifest schema (schemaVersion, generatedAtUtc, …).
        // WriteIndented / enum converter / WhenWritingNull mirror PortfolioService & CLI.
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented          = true,
            PropertyNamingPolicy   = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters             = { new JsonStringEnumConverter() },
        };

        // Serializes overlapping exports so two writers never race on the same .tmp file.
        private readonly SemaphoreSlim _gate = new(1, 1);

        // ── Manifest shapes ───────────────────────────────────────────────────

        // Fields is a per-file data dictionary (json field name → one-line meaning),
        // sourced from Glossary, so an LLM reading e.g. portfolio.json knows what each
        // field means without guessing. Null for files with no glossary-backed fields
        // (e.g. the markdown briefing). WhenWritingNull keeps it out of the JSON then.
        private sealed record ManifestFile(
            string                      Name,
            string                      Description,
            int?                        Records,
            Dictionary<string, string>? Fields = null);

        private sealed record Manifest(
            int                SchemaVersion,
            DateTime           GeneratedAtUtc,
            DateTime?          DataFetchTimeUtc,
            double?            StalenessHours,
            List<string>       EnabledSources,
            string             Universe,
            string             Strategy,
            List<ManifestFile> Files);

        // Shape written to app-state.json (see LLM-CONTEXT doc §3.3). Kept separate from
        // ContextBundle so the on-disk schema is explicit and stable.
        private sealed record AppState(
            string     ActiveStrategy,
            string     ActiveStrategyName,
            string     Universe,
            string?    SelectedSymbol,
            string     ActiveView,
            SortState? Sort,
            DateTime?  LastScanUtc,
            double?    StalenessHours);

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Writes the full context bundle. Never throws — failures are reported
        /// through <see cref="ExportError"/>.
        /// </summary>
        public async Task ExportAsync(ContextBundle bundle)
        {
            if (bundle is null) return;

            await _gate.WaitAsync();
            try
            {
                await ExportInternalAsync(bundle);
            }
            catch (Exception ex)
            {
                // ExportInternalAsync guards each file write individually, but a
                // serializer/manifest surprise outside those guards must not fault the
                // caller's fire-and-forget task — honour the never-throws contract.
                Report($"⚠ Context export failed unexpectedly ({ex.GetType().Name}: {ex.Message}).");
            }
            finally
            {
                _gate.Release();
            }
        }

        // ── Implementation ────────────────────────────────────────────────────

        private async Task ExportInternalAsync(ContextBundle bundle)
        {
            var errors = new List<string>();

            try
            {
                Directory.CreateDirectory(ContextFolder);
            }
            catch (Exception ex)
            {
                Report($"⚠ Context export failed — could not create {ContextFolder} " +
                       $"({ex.GetType().Name}: {ex.Message}).");
                return;
            }

            var files = new List<ManifestFile>();

            // recommendations.json (the bundle already carries whitelist DTOs — see ContextBundle)
            var recs = bundle.Recommendations;
            if (await WriteJsonAsync("recommendations.json", recs, errors))
                files.Add(new ManifestFile(
                    "recommendations.json",
                    $"Strategy recommendations from the last scan (strategy: {bundle.StrategyName}); " +
                    "each row has the signal (action, confidence, reasoning), key indicators, and trade dates.",
                    recs.Count,
                    FieldDictionary(typeof(RecommendationExport))));

            // earnings.json
            var earnings = bundle.Earnings;
            if (await WriteJsonAsync("earnings.json", earnings, errors))
                files.Add(new ManifestFile(
                    "earnings.json",
                    "Upcoming-earnings candidates ranked by a 0–100 likelihood score, with expected move and momentum.",
                    earnings.Count,
                    FieldDictionary(typeof(EarningsExport))));

            // day-picks.json
            var dayPicks = bundle.DayPicks;
            if (await WriteJsonAsync("day-picks.json", dayPicks, errors))
                files.Add(new ManifestFile(
                    "day-picks.json",
                    "Intraday (same-session) picks with direction, entry/stop/target levels, and risk-reward ratio.",
                    dayPicks.Count,
                    FieldDictionary(typeof(DayPickExport))));

            // portfolio.json
            var positions    = bundle.Positions;
            var transactions = bundle.Transactions;
            var portfolio    = new
            {
                CashBalance  = bundle.CashBalance,
                Positions    = positions,
                Transactions = transactions,
            };
            if (await WriteJsonAsync("portfolio.json", portfolio, errors))
                files.Add(new ManifestFile(
                    "portfolio.json",
                    $"Portfolio snapshot: cash balance, {positions.Count} open positions (with margin detail " +
                    $"and unrealized P&L), and the full ledger of {transactions.Count} transactions.",
                    positions.Count + transactions.Count,
                    // Merge the position and transaction field dictionaries — portfolio.json carries both.
                    FieldDictionary(typeof(PositionExport), typeof(TransactionExport))));

            // performance.json (skip when the caller has no computed performance)
            if (bundle.Performance is not null)
            {
                if (await WriteJsonAsync("performance.json", bundle.Performance, errors))
                    files.Add(new ManifestFile(
                        "performance.json",
                        "Aggregate holdings performance: cost basis, market value, total gain, and trailing " +
                        "week/month/quarter/year returns.",
                        1,
                        FieldDictionary(typeof(PerformanceExport), typeof(PerformancePeriodExport))));
            }
            else
            {
                DeleteStale("performance.json");
            }

            // news-briefing.md (skip when there is no briefing)
            if (!string.IsNullOrWhiteSpace(bundle.NewsBriefingMarkdown))
            {
                if (await WriteTextAsync("news-briefing.md", bundle.NewsBriefingMarkdown, errors))
                    files.Add(new ManifestFile(
                        "news-briefing.md",
                        "The app's markdown News briefing (positions review, cross-strategy best buys, " +
                        "earnings plays, and top picks), exported verbatim.",
                        null));
            }
            else
            {
                DeleteStale("news-briefing.md");
            }

            // glossary.json — the canonical definitions for every field an LLM will
            // encounter in the other files (and the source of the per-file data
            // dictionaries above). Always written so the bundle is self-describing.
            if (await WriteJsonAsync("glossary.json", Glossary.All, errors))
                files.Add(new ManifestFile(
                    "glossary.json",
                    "Canonical, educational (non-advisory) definitions for every field, indicator, and " +
                    "strategy used in this bundle; the manifest's per-file `fields` maps are sourced from it.",
                    Glossary.All.Count));

            // app-state.json — "what's going on right now": the user's active strategy,
            // universe, selection, view, sort, and scan freshness. Lets an LLM answer
            // "what am I looking at / why is this highlighted?" beyond just the data.
            var appState = new AppState(
                ActiveStrategy:     bundle.ActiveStrategy,
                ActiveStrategyName: bundle.ActiveStrategyName,
                Universe:           bundle.Universe,
                SelectedSymbol:     bundle.SelectedSymbol,
                ActiveView:         bundle.ActiveView,
                Sort:               bundle.Sort,
                LastScanUtc:        bundle.LastScanUtc?.ToUniversalTime(),
                StalenessHours:     bundle.StalenessHours);
            if (await WriteJsonAsync("app-state.json", appState, errors))
                files.Add(new ManifestFile(
                    "app-state.json",
                    "Snapshot of the user's current focus: active strategy, scan universe, selected symbol, " +
                    "active view, grid sort, and how stale the last scan is.",
                    null));

            // manifest.json — written last so it only describes files that really exist.
            var manifest = new Manifest(
                SchemaVersion:    1,
                GeneratedAtUtc:   bundle.GeneratedAt.ToUniversalTime(),
                DataFetchTimeUtc: bundle.DataFetchTime?.ToUniversalTime(),
                StalenessHours:   bundle.DataFetchTime.HasValue
                                      ? Math.Round((bundle.GeneratedAt - bundle.DataFetchTime.Value).TotalHours, 1)
                                      : null,
                EnabledSources:   bundle.EnabledSources.ToList(),
                Universe:         bundle.UniverseDescription,
                Strategy:         bundle.StrategyName,
                Files:            files);

            await WriteJsonAsync("manifest.json", manifest, errors);

            if (errors.Count > 0)
                Report($"⚠ Context export: {errors.Count} file(s) failed — {string.Join("; ", errors)}");
        }

        /// <summary>
        /// Builds a data dictionary (json field name → one-line meaning) for the public
        /// properties of the given export DTO type(s), sourced from <see cref="Glossary"/>.
        /// Only properties that have a glossary entry are included; the field key is the
        /// camelCase name that actually appears in the serialized JSON. Returns null when
        /// no properties are glossary-backed, so the manifest omits an empty map.
        /// </summary>
        private static Dictionary<string, string>? FieldDictionary(params Type[] dtoTypes)
        {
            var map = new Dictionary<string, string>();
            foreach (var t in dtoTypes)
            {
                foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (Glossary.TryGet(p.Name, out var def) && def is not null)
                    {
                        var jsonName = JsonNamingPolicy.CamelCase.ConvertName(p.Name);
                        map[jsonName] = def.Tooltip;
                    }
                }
            }
            return map.Count > 0 ? map : null;
        }

        /// <summary>
        /// Serializes <paramref name="payload"/> to <paramref name="fileName"/> using the
        /// atomic tmp→rename pattern (FileStream → flush → File.Move overwrite) copied
        /// from <see cref="PortfolioService"/>. Returns false (and records the error)
        /// instead of throwing.
        /// </summary>
        private static async Task<bool> WriteJsonAsync<T>(string fileName, T payload, List<string> errors)
        {
            try
            {
                var path = Path.Combine(ContextFolder, fileName);
                var tmp  = path + ".tmp";

                await using (var fs = new FileStream(
                    tmp, FileMode.Create, FileAccess.Write,
                    FileShare.None, bufferSize: 4096, useAsync: true))
                {
                    await JsonSerializer.SerializeAsync(fs, payload, _jsonOptions);
                    await fs.FlushAsync();
                }

                File.Move(tmp, path, overwrite: true);
                return true;
            }
            catch (Exception ex)
            {
                errors.Add($"{fileName} ({ex.GetType().Name}: {ex.Message})");
                return false;
            }
        }

        /// <summary>Same atomic tmp→rename pattern for plain-text (markdown) content.</summary>
        private static async Task<bool> WriteTextAsync(string fileName, string content, List<string> errors)
        {
            try
            {
                var path = Path.Combine(ContextFolder, fileName);
                var tmp  = path + ".tmp";

                await using (var fs = new FileStream(
                    tmp, FileMode.Create, FileAccess.Write,
                    FileShare.None, bufferSize: 4096, useAsync: true))
                await using (var writer = new StreamWriter(fs))
                {
                    await writer.WriteAsync(content);
                    await writer.FlushAsync();
                    await fs.FlushAsync();
                }

                File.Move(tmp, path, overwrite: true);
                return true;
            }
            catch (Exception ex)
            {
                errors.Add($"{fileName} ({ex.GetType().Name}: {ex.Message})");
                return false;
            }
        }

        /// <summary>
        /// Best-effort removal of a file skipped this run (e.g. performance.json when no
        /// performance was supplied) so the folder never contradicts the fresh manifest.
        /// </summary>
        private static void DeleteStale(string fileName)
        {
            try
            {
                var path = Path.Combine(ContextFolder, fileName);
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // Best-effort only — a stale leftover is harmless because the manifest
                // (the LLM's entry point) no longer references it.
            }
        }

        private void Report(string message)
        {
            System.Diagnostics.Debug.WriteLine($"[ContextExportService] {message}");
            try { ExportError?.Invoke(message); } catch { /* subscriber threw — ignore */ }
        }
    }
}
