using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using StockPicker.Reference;
using StockPicker.Services;
using Xunit;

namespace StockPicker.Core.Tests
{
    /// <summary>
    /// Guards against glossary drift: every field that the whitelist export DTOs emit
    /// must have a canonical <see cref="Glossary"/> definition. A new exported field
    /// without a definition fails here rather than shipping undocumented to the LLM
    /// context bundle and the UI tooltips.
    /// </summary>
    public class GlossaryDriftTests
    {
        /// <summary>
        /// All whitelist export DTO records — every public sealed record named
        /// <c>*Export</c> in the ContextProjections namespace, discovered by reflection
        /// so newly-added DTOs are covered automatically.
        /// </summary>
        private static IEnumerable<Type> ExportTypes() =>
            typeof(ContextProjections).Assembly.GetTypes()
                .Where(t => t.IsClass
                            && t.Namespace == "StockPicker.Services"
                            && t.Name.EndsWith("Export", StringComparison.Ordinal))
                .OrderBy(t => t.Name);

        public static IEnumerable<object[]> ExportFields()
        {
            foreach (var t in ExportTypes())
                foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    yield return new object[] { t.Name, p.Name };
        }

        [Theory]
        [MemberData(nameof(ExportFields))]
        public void EveryExportFieldHasGlossaryEntry(string typeName, string propertyName)
        {
            Assert.True(
                Glossary.TryGet(propertyName, out var def) && def is not null,
                $"Export field '{typeName}.{propertyName}' has no Glossary entry. " +
                $"Add a TermDefinition with Key \"{propertyName}\" in Glossary.cs.");
        }

        [Fact]
        public void ExportTypesAreDiscovered()
        {
            // Sanity guard: if the reflection filter breaks, the Theory above would
            // silently pass with zero cases. Anchor on the known DTO set.
            var names = ExportTypes().Select(t => t.Name).ToList();
            Assert.Contains("RecommendationExport", names);
            Assert.Contains("PositionExport", names);
            Assert.Contains("EarningsExport", names);
            Assert.Contains("TransactionExport", names);
            Assert.Contains("DayPickExport", names);
            Assert.Contains("PerformanceExport", names);
            Assert.Contains("PerformancePeriodExport", names);
        }

        [Fact]
        public void GlossaryKeysAreUnique()
        {
            var duplicates = Glossary.All
                .GroupBy(d => d.Key, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            Assert.True(
                duplicates.Count == 0,
                $"Glossary has duplicate keys (case-insensitive): {string.Join(", ", duplicates)}.");
        }
    }
}
