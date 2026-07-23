using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using StockPicker.Reference;

namespace StockPicker.Desktop.ViewModels
{
    /// <summary>
    /// View model backing <c>GlossaryWindow</c>: exposes the canonical
    /// <see cref="Glossary"/> as a live-filtered, category-grouped collection.
    /// </summary>
    /// <remarks>
    /// Reads straight from <c>StockPicker.Core</c> (<see cref="Glossary.All"/>) so the panel,
    /// the tooltips, and the exported <c>glossary.json</c> share a single source of truth.
    /// Search matches Term / Key / Explanation case-insensitively and rebuilds the groups
    /// live as <see cref="SearchText"/> changes.
    /// </remarks>
    public sealed class GlossaryViewModel : ViewModelBase
    {
        private readonly List<TermDefinition> _all;
        private string _searchText = string.Empty;
        private bool _hasResults = true;

        public GlossaryViewModel()
        {
            _all = Glossary.All.ToList();
            Groups = new ObservableCollection<GlossaryGroupViewModel>();
            Rebuild();
        }

        /// <summary>Category-grouped, search-filtered terms for the panel.</summary>
        public ObservableCollection<GlossaryGroupViewModel> Groups { get; }

        /// <summary>Live search text; setting it re-filters <see cref="Groups"/>.</summary>
        public string SearchText
        {
            get => _searchText;
            set { if (SetProperty(ref _searchText, value)) Rebuild(); }
        }

        /// <summary>False when the current search matches no terms (drives the empty-state).</summary>
        public bool HasResults
        {
            get => _hasResults;
            private set => SetProperty(ref _hasResults, value);
        }

        private void Rebuild()
        {
            IEnumerable<TermDefinition> matches = _all;

            var q = _searchText?.Trim();
            if (!string.IsNullOrEmpty(q))
            {
                matches = _all.Where(d =>
                    d.Term.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    d.Key.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    d.Explanation.Contains(q, StringComparison.OrdinalIgnoreCase));
            }

            Groups.Clear();
            // GroupBy preserves each category's first-appearance order, which follows the
            // canonical ordering baked into Glossary.All.
            foreach (var grp in matches.GroupBy(d => d.Category))
            {
                Groups.Add(new GlossaryGroupViewModel(
                    grp.Key.ToString(),
                    grp.Select(d => new GlossaryItemViewModel(d))));
            }

            HasResults = Groups.Count > 0;
        }
    }

    /// <summary>One category section in the glossary panel.</summary>
    public sealed class GlossaryGroupViewModel
    {
        public GlossaryGroupViewModel(string category, IEnumerable<GlossaryItemViewModel> items)
        {
            Category = category;
            Items = items.ToList();
        }

        public string Category { get; }
        public IReadOnlyList<GlossaryItemViewModel> Items { get; }
        public string HeaderDisplay => $"{Category}  ({Items.Count})";
    }

    /// <summary>
    /// Display wrapper over a <see cref="TermDefinition"/> — pre-formats the optional
    /// Formula/Range so the view can bind visibility without a converter.
    /// </summary>
    public sealed class GlossaryItemViewModel
    {
        private readonly TermDefinition _d;

        public GlossaryItemViewModel(TermDefinition d) => _d = d;

        public string Term => _d.Term;
        public string Key => _d.Key;
        public string CategoryLabel => _d.Category.ToString();
        public string Explanation => _d.Explanation;

        public bool HasFormula => !string.IsNullOrWhiteSpace(_d.Formula);
        public bool HasRange => !string.IsNullOrWhiteSpace(_d.Range);
        public string FormulaDisplay => $"Formula:  {_d.Formula}";
        public string RangeDisplay => $"Range:  {_d.Range}";
    }
}
