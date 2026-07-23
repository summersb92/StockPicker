using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using StockPicker.Models;
using StockPicker.Reference;
using StockPicker.Services;
using Xunit;

namespace StockPicker.Core.Tests
{
    /// <summary>
    /// Round-trip contract for the LLM context bundle: the manifest lists exactly
    /// the files ExportAsync wrote, glossary.json / app-state.json parse, and every
    /// manifest data-dictionary key names a camelCase field actually present in the
    /// corresponding file's serialized DTOs.
    ///
    /// ISOLATION NOTE: <see cref="ContextExportService.ContextFolder"/> is a static
    /// get-only property with no redirect seam, so this test runs against the real
    /// folder (%LOCALAPPDATA%\StockPicker\context). That folder is a cache the app
    /// regenerates on every scan, and this test performs only ExportAsync's own
    /// normal writes/deletes — nothing beyond what the app itself does. Both phases
    /// run inside ONE test method so their order is deterministic and the folder is
    /// left in the fullest (all-files) state.
    /// </summary>
    public class ContextExportContractTests
    {
        // ── Fully-populated synthetic bundle ──────────────────────────────────
        // Every nullable DTO field is set: the serializer omits nulls
        // (WhenWritingNull), and the contract check below needs each whitelisted
        // field to actually appear in the JSON.

        private static ContextBundle FullBundle() => new()
        {
            Recommendations =
            {
                new RecommendationExport(
                    "AAPL", "Apple Inc.", "Technology", "Buy", 0.8,
                    190.5m, 1.2, 55.0, 2.1, 185.0, 180.0, 200.0m,
                    new DateTime(2025, 6, 9), new DateTime(2025, 6, 13),
                    "Quick", "Synthetic reasoning"),
            },
            Earnings =
            {
                new EarningsExport(
                    "MSFT", "Microsoft", "Technology",
                    new DateTime(2025, 7, 22), 10, 72.5, true, 4.2, 1.8, 450m),
            },
            DayPicks =
            {
                new DayPickExport(
                    "NVDA", "NVIDIA", "Technology", "Long",
                    88.0, 120m, 115m, 130m, 2.0, 62.0, "Synthetic trigger"),
            },
            Positions =
            {
                new PositionExport(
                    "AMZN", "Amazon", 180m, 10, new DateTime(2025, 1, 15),
                    new DateTime(2025, 12, 31), "Long", 200m, 11.1,
                    true, 50m, 8.5m, 2m, 900m, 12.3m, 20.5),
            },
            Transactions =
            {
                new TransactionExport(
                    new DateTime(2025, 1, 15), "Buy", "AMZN", "Amazon",
                    10, 180m, 1800m, -900m, 0m, true, "synthetic"),
            },
            CashBalance = 2500m,
            Performance = new PerformanceExport(
                new DateTime(2025, 6, 10), 1, 900m, 1987.7m, 2500m, 4487.7m,
                1087.7m, 120.9,
                new List<PerformancePeriodExport>
                {
                    new("Week", new DateTime(2025, 6, 3), 1500m, 2000m, 33.3, true),
                }),
            NewsBriefingMarkdown = "# Synthetic briefing\nNothing to report.",
            DataFetchTime      = new DateTime(2025, 6, 10, 9, 30, 0),
            EnabledSources     = { "Stooq" },
            UniverseDescription = "Test universe (2 stocks)",
            StrategyName       = "Momentum (Quick)",
            GeneratedAt        = new DateTime(2025, 6, 10, 10, 0, 0),
            ActiveStrategy     = "momentum",
            ActiveStrategyName = "Momentum (Quick)",
            Universe           = "Test universe (2 stocks)",
            SelectedSymbol     = "AAPL",
            ActiveView         = "Recommendations",
            Sort               = new SortState("Confidence", Descending: true),
            LastScanUtc        = new DateTime(2025, 6, 10, 14, 30, 0, DateTimeKind.Utc),
            StalenessHours     = 0.5,
        };

        /// <summary>Minimal bundle: no performance, no briefing — those files must be skipped.</summary>
        private static ContextBundle MinimalBundle()
        {
            var b = FullBundle();
            b.Performance = null;
            b.NewsBriefingMarkdown = string.Empty;
            return b;
        }

        [Fact]
        public async Task ExportRoundTrip_ManifestFilesAndFieldDictionariesAreConsistent()
        {
            var service = new ContextExportService();
            var errors  = new List<string>();
            service.ExportError += msg => errors.Add(msg);

            // ── Phase 1: minimal bundle — optional files are skipped & not listed ──
            await service.ExportAsync(MinimalBundle());
            Assert.True(errors.Count == 0, $"export reported errors: {string.Join("; ", errors)}");

            List<string> minimalNames;
            using (var minimalManifest = ReadManifest())
                minimalNames = ManifestFileNames(minimalManifest);
            Assert.DoesNotContain("performance.json",  minimalNames);
            Assert.DoesNotContain("news-briefing.md",  minimalNames);
            Assert.False(File.Exists(ContextPath("performance.json")),
                "performance.json must be deleted when the bundle has no performance");
            Assert.False(File.Exists(ContextPath("news-briefing.md")),
                "news-briefing.md must be deleted when the bundle has no briefing");

            // ── Phase 2: full bundle — everything written and self-consistent ──
            await service.ExportAsync(FullBundle());
            Assert.True(errors.Count == 0, $"export reported errors: {string.Join("; ", errors)}");

            using var manifest = ReadManifest();
            var files = ManifestFileNames(manifest);

            var expected = new[]
            {
                "recommendations.json", "earnings.json", "day-picks.json",
                "portfolio.json", "performance.json", "news-briefing.md",
                "glossary.json", "app-state.json",
            };
            Assert.Equal(expected.OrderBy(n => n), files.OrderBy(n => n));

            // The manifest lists exactly the files written — each must exist on disk.
            Assert.All(files, name => Assert.True(
                File.Exists(ContextPath(name)), $"manifest lists '{name}' but it was not written"));

            // glossary.json parses and carries the full glossary.
            using (var glossary = ParseFile("glossary.json"))
            {
                Assert.Equal(JsonValueKind.Array, glossary.RootElement.ValueKind);
                Assert.Equal(Glossary.All.Count, glossary.RootElement.GetArrayLength());
            }

            // app-state.json parses and reflects the bundle's focus snapshot.
            using (var appState = ParseFile("app-state.json"))
            {
                Assert.Equal("momentum",
                    appState.RootElement.GetProperty("activeStrategy").GetString());
                Assert.Equal("AAPL",
                    appState.RootElement.GetProperty("selectedSymbol").GetString());
                Assert.True(appState.RootElement.GetProperty("sort")
                    .GetProperty("descending").GetBoolean());
            }

            // Every manifest data-dictionary key must be a camelCase field name that
            // actually appears in the corresponding file's serialized DTOs.
            var checkedDictionaries = 0;
            foreach (var file in manifest.RootElement.GetProperty("files").EnumerateArray())
            {
                var name = file.GetProperty("name").GetString()!;
                if (!file.TryGetProperty("fields", out var fields))
                    continue;   // glossary.json / news-briefing.md / app-state.json carry no dictionary

                var serialized = SerializedFieldNames(name);
                foreach (var key in fields.EnumerateObject().Select(p => p.Name))
                {
                    Assert.True(char.IsLower(key[0]),
                        $"{name}: field key '{key}' is not camelCase");
                    Assert.Contains(key, serialized);
                }
                Assert.NotEmpty(fields.EnumerateObject().ToList());
                checkedDictionaries++;
            }

            // All five data files carry a field dictionary (drift here means the
            // manifest silently stopped documenting a file).
            Assert.Equal(5, checkedDictionaries);
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static string ContextPath(string fileName)
            => Path.Combine(ContextExportService.ContextFolder, fileName);

        private static JsonDocument ParseFile(string fileName)
            => JsonDocument.Parse(File.ReadAllText(ContextPath(fileName)));

        private static JsonDocument ReadManifest() => ParseFile("manifest.json");

        private static List<string> ManifestFileNames(JsonDocument manifest)
            => manifest.RootElement.GetProperty("files").EnumerateArray()
                .Select(f => f.GetProperty("name").GetString()!)
                .ToList();

        /// <summary>
        /// The set of JSON property names actually present in a written file's DTO
        /// objects (union across the file's record shapes for multi-shape files).
        /// </summary>
        private static HashSet<string> SerializedFieldNames(string fileName)
        {
            using var doc = ParseFile(fileName);
            var root = doc.RootElement;
            var names = new HashSet<string>(StringComparer.Ordinal);

            switch (fileName)
            {
                case "recommendations.json":
                case "earnings.json":
                case "day-picks.json":
                    CollectObjectProperties(root[0], names);
                    break;

                case "portfolio.json":
                    CollectObjectProperties(root.GetProperty("positions")[0], names);
                    CollectObjectProperties(root.GetProperty("transactions")[0], names);
                    break;

                case "performance.json":
                    CollectObjectProperties(root, names);
                    CollectObjectProperties(root.GetProperty("periods")[0], names);
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unexpected field dictionary for '{fileName}' — teach this test its shape.");
            }

            return names;
        }

        private static void CollectObjectProperties(JsonElement obj, HashSet<string> names)
        {
            foreach (var p in obj.EnumerateObject())
                names.Add(p.Name);
        }
    }
}
