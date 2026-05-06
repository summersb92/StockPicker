using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using StockPicker.Models;
using StockPicker.ViewModels;
using StockPicker.Views;

namespace StockPicker
{
    public partial class MainWindow : Window
    {
        private const double CompactBreakpoint = 1100.0;

        public MainWindow()
        {
            InitializeComponent();
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateLayoutMode(ActualWidth);

            if (DataContext is MainViewModel vm)
            {
                RestoreColumnOrderToGrid(vm);

                vm.Recommendations.CollectionChanged += (_, args) =>
                {
                    if (args.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
                        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                            new System.Action(() => RestoreSortToGrid(vm)));
                };

                await vm.StartupAsync();
                RestoreSortToGrid(vm);
            }
        }

        private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.WidthChanged)
                UpdateLayoutMode(e.NewSize.Width);
        }

        private void UpdateLayoutMode(double width)
        {
            if (DataContext is not MainViewModel vm) return;
            var target = width < CompactBreakpoint ? LayoutMode.Compact : LayoutMode.Full;
            if (vm.LayoutMode != target)
                vm.LayoutMode = target;
        }

        // ── Sort persistence ──────────────────────────────────────────────────

        private static readonly (string path, ListSortDirection dir)[] DefaultSort =
        {
            ("Confidence",      ListSortDirection.Descending),
            ("ActionSortOrder", ListSortDirection.Ascending),
            ("Symbol",          ListSortDirection.Ascending),
        };

        private void RestoreSortToGrid(MainViewModel vm)
        {
            var savedCol = vm.SavedSortColumn;

            FullRecsGrid.Items.SortDescriptions.Clear();
            foreach (var gridCol in FullRecsGrid.Columns)
                gridCol.SortDirection = null;

            if (string.IsNullOrEmpty(savedCol))
            {
                ApplyDefaultSort();
            }
            else
            {
                var direction = vm.SavedSortDirection == "Descending"
                    ? ListSortDirection.Descending
                    : ListSortDirection.Ascending;

                FullRecsGrid.Items.SortDescriptions.Add(new SortDescription(savedCol, direction));

                foreach (var gridCol in FullRecsGrid.Columns)
                {
                    var path = ResolveColumnSortPath(gridCol);
                    if (path == savedCol)
                        gridCol.SortDirection = direction;
                }
            }
        }

        private void ApplyDefaultSort()
        {
            foreach (var (path, dir) in DefaultSort)
                FullRecsGrid.Items.SortDescriptions.Add(new SortDescription(path, dir));

            foreach (var gridCol in FullRecsGrid.Columns)
            {
                var path = ResolveColumnSortPath(gridCol);
                gridCol.SortDirection = path switch
                {
                    "Confidence"      => ListSortDirection.Descending,
                    "ActionSortOrder" => ListSortDirection.Ascending,
                    _                 => (ListSortDirection?)null,
                };
            }
        }

        private void FullRecsGrid_Sorting(object sender, DataGridSortingEventArgs e)
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                new System.Action(() =>
                {
                    if (DataContext is not MainViewModel vm) return;

                    var descs = FullRecsGrid.Items.SortDescriptions;
                    if (descs.Count == 0)
                    {
                        vm.SavedSortColumn    = string.Empty;
                        vm.SavedSortDirection = "Ascending";
                        ApplyDefaultSort();
                    }
                    else
                    {
                        var first = descs[0];
                        vm.SavedSortColumn    = first.PropertyName;
                        vm.SavedSortDirection = first.Direction == ListSortDirection.Descending
                                                    ? "Descending" : "Ascending";
                    }
                }));
        }

        private static string ResolveColumnSortPath(DataGridColumn col)
        {
            if (!string.IsNullOrEmpty(col.SortMemberPath))
                return col.SortMemberPath;

            if (col is DataGridBoundColumn bound && bound.Binding is Binding b)
                return b.Path?.Path ?? string.Empty;

            return string.Empty;
        }

        // ── Column order persistence ──────────────────────────────────────────

        private void RestoreColumnOrderToGrid(MainViewModel vm)
        {
            var saved = vm.SavedColumnOrder;
            if (saved.Count == 0) return;

            var pairs = FullRecsGrid.Columns
                .Select(c => (col: c, header: c.Header?.ToString() ?? string.Empty))
                .Where(x => saved.ContainsKey(x.header))
                .Select(x => (x.col, target: saved[x.header]))
                .OrderBy(x => x.target)
                .ToList();

            foreach (var (col, target) in pairs)
            {
                if (col.DisplayIndex != target)
                    col.DisplayIndex = target;
            }
        }

        private void FullRecsGrid_ColumnDisplayIndexChanged(object sender, DataGridColumnEventArgs e)
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                new System.Action(() =>
                {
                    if (DataContext is not MainViewModel vm) return;

                    var order = new System.Collections.Generic.Dictionary<string, int>();
                    foreach (var col in FullRecsGrid.Columns)
                    {
                        var header = col.Header?.ToString();
                        if (!string.IsNullOrEmpty(header))
                            order[header] = col.DisplayIndex;
                    }

                    vm.SavedColumnOrder = order;
                }));
        }

        // ── Destructive portfolio actions (require confirmation) ──────────────

        private void RemoveFromWatch_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel vm || vm.SelectedWatch == null) return;
            var result = MessageBox.Show(
                $"Remove {vm.SelectedWatch.Symbol} from your watch list?",
                "Confirm Remove", MessageBoxButton.YesNo,
                MessageBoxImage.Question, MessageBoxResult.No);
            if (result == MessageBoxResult.Yes)
                vm.RemoveFromWatchCommand.Execute(null);
        }

        private void RemoveFromPosition_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel vm || vm.SelectedHeld == null) return;
            var result = MessageBox.Show(
                $"Remove {vm.SelectedHeld.Symbol} from your positions?",
                "Confirm Remove", MessageBoxButton.YesNo,
                MessageBoxImage.Question, MessageBoxResult.No);
            if (result == MessageBoxResult.Yes)
                vm.RemoveFromHeldCommand.Execute(null);
        }

        // ── Settings dialog ───────────────────────────────────────────────────

        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            var settings = new SettingsWindow
            {
                Owner       = this,
                DataContext = DataContext,
            };
            settings.ShowDialog();
        }
    }
}
