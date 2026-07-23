using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using StockPicker.Desktop.Controls;
using StockPicker.Desktop.ViewModels;
using StockPicker.Models;
using StockPicker.Reference;

namespace StockPicker.Desktop.Views;

/// <summary>
/// Application main window. Avalonia port of <c>StockPicker/MainWindow.xaml(.cs)</c>.
/// </summary>
/// <remarks>
/// WPF-ADAPTATION highlights:
/// <list type="bullet">
///   <item>DataContext is created here (was <c>&lt;Window.DataContext&gt;</c> in WPF XAML).</item>
///   <item><c>MessageBox.Show</c> → <see cref="MessageDialog"/> (async owner-modal) for
///     confirmations / info; <c>Microsoft.Win32.SaveFileDialog</c> → Avalonia
///     <c>StorageProvider.SaveFilePickerAsync</c>.</item>
///   <item>Row action tints + per-row context menu + hover tooltip (WPF DataGrid RowStyle /
///     DataTriggers, which Avalonia's DataGrid lacks) are reproduced in <c>*_LoadingRow</c>
///     handlers.</item>
///   <item>Column-visibility toggles (WPF bound each column's Visibility through a BindingProxy)
///     are driven here by mapping column index → <c>ColumnToggle</c> and reacting to changes.</item>
///   <item>Sort + column-order persistence use the Avalonia <c>Sorting</c> /
///     <c>ColumnReordered</c> events and the <c>DataGridCollectionView.SortDescriptions</c>.</item>
///   <item>The interactive News briefing (WPF FlowDocumentScrollViewer) is rendered by
///     <see cref="NewsBriefingRenderer"/> and hosted in the <c>NewsScrollFull</c> ScrollViewer.</item>
/// </list>
/// </remarks>
public partial class MainWindow : Window
{
    private const double CompactBreakpoint = 1100.0;

    // Row action-tint brushes (mirror the WPF DataTrigger colours).
    private static readonly IBrush StrongBuyBrush  = Brush.Parse("#CDEBD6");
    private static readonly IBrush BuyBrush        = Brush.Parse("#EAF7EE");
    private static readonly IBrush SellBrush       = Brush.Parse("#FBE7EA");
    private static readonly IBrush StrongSellBrush = Brush.Parse("#F4CBD2");

    // Recommendations-grid column index → visibility toggle. Indices MUST match the
    // column order declared in MainWindow.axaml (0 Symbol / 2 Action are always shown).
    private readonly Dictionary<int, ColumnToggle> _recColumnToggles = new();

    public MainWindow()
    {
        InitializeComponent();

        var vm = new MainViewModel();
        DataContext = vm;

        // Clipboard hook (VM has no direct access to a TopLevel).
        vm.CopyToClipboardAsync = async text =>
        {
            var top = TopLevel.GetTopLevel(this);
            if (top?.Clipboard is { } cb)
                await cb.SetTextAsync(text);
        };

        // Row tint + context-menu + tooltip handlers (both layouts share the handlers).
        FullRecsGrid.LoadingRow      += RecsRow_LoadingRow;
        CompactRecsGrid.LoadingRow   += RecsRow_LoadingRow;
        DayPicksGridFull.LoadingRow    += DayPickRow_LoadingRow;
        DayPicksGridCompact.LoadingRow += DayPickRow_LoadingRow;
        EarningsGridFull.LoadingRow    += EarningsRow_LoadingRow;
        EarningsGridCompact.LoadingRow += EarningsRow_LoadingRow;
        WatchGridFull.LoadingRow     += WatchRow_LoadingRow;
        WatchGridCompact.LoadingRow  += WatchRow_LoadingRow;
        HeldGridFull.LoadingRow      += HeldRow_LoadingRow;
        HeldGridCompact.LoadingRow   += HeldRow_LoadingRow;

        // Sort + column-order persistence (the full Recommendations grid only, as in WPF).
        FullRecsGrid.Sorting          += FullRecsGrid_Sorting;
        FullRecsGrid.ColumnReordered  += FullRecsGrid_ColumnReordered;

        // Source rec-grid header tooltips from the canonical Glossary (single source of truth).
        ApplyGlossaryHeaderTooltips();

        Loaded      += Window_Loaded;
        SizeChanged += Window_SizeChanged;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private MainViewModel? VM => DataContext as MainViewModel;

    // ── Window lifecycle ──────────────────────────────────────────────────────

    private async void Window_Loaded(object? sender, RoutedEventArgs e)
    {
        if (VM is not { } vm) return;

        UpdateLayoutMode(Bounds.Width);

        BuildRecColumnMap();
        ApplyColumnVisibility();
        foreach (var toggle in _recColumnToggles.Values)
            toggle.PropertyChanged += (_, __) => ApplyColumnVisibility();

        RestoreColumnOrderToGrid(vm);

        // Keep the details column width in sync with ShowDetails (Visibility alone doesn't
        // reclaim column space; set ColumnDefinitions[2].Width directly, as WPF did).
        void SyncDetailsColumn()
        {
            if (vm.LayoutMode != LayoutMode.Full) return;
            FullBodyGrid.ColumnDefinitions[2].Width = vm.ShowDetails
                ? new GridLength(2, GridUnitType.Star)
                : new GridLength(0);
        }

        // Re-render the interactive News briefing whenever the markdown changes.
        void RenderNews() => NewsScrollFull.Content =
            NewsBriefingRenderer.Render(vm.NewsReport, vm.SelectNewsSymbolCommand, vm.AddNewsSymbolToWatchCommand);

        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(MainViewModel.ShowDetails) or nameof(MainViewModel.LayoutMode))
                SyncDetailsColumn();
            else if (args.PropertyName == nameof(MainViewModel.NewsReport))
                Dispatcher.UIThread.Post(RenderNews, DispatcherPriority.Background);
        };

        SyncDetailsColumn();
        RenderNews();

        await vm.StartupAsync();
        RestoreSortToGrid(vm);
    }

    private void Window_SizeChanged(object? sender, SizeChangedEventArgs e)
        => UpdateLayoutMode(e.NewSize.Width);

    private void UpdateLayoutMode(double width)
    {
        if (VM is not { } vm) return;
        var target = width < CompactBreakpoint ? LayoutMode.Compact : LayoutMode.Full;
        if (vm.LayoutMode != target)
            vm.LayoutMode = target;
    }

    // ── Column visibility ─────────────────────────────────────────────────────

    private void BuildRecColumnMap()
    {
        if (VM is not { } vm) return;
        _recColumnToggles.Clear();
        // Index positions match the DataGrid.Columns order in MainWindow.axaml.
        _recColumnToggles[1]  = vm.ColSource;
        _recColumnToggles[3]  = vm.ColPrice;
        _recColumnToggles[4]  = vm.ColDayChange;
        _recColumnToggles[5]  = vm.ColDayChangePct;
        _recColumnToggles[6]  = vm.ColRSI;
        _recColumnToggles[7]  = vm.ColWeekReturn;
        _recColumnToggles[8]  = vm.ColSMA20;
        _recColumnToggles[9]  = vm.ColSMA50;
        _recColumnToggles[10] = vm.ColVolTrend;
        _recColumnToggles[11] = vm.ColConf;
        _recColumnToggles[12] = vm.ColBuyDate;
        _recColumnToggles[13] = vm.ColSellDate;
        _recColumnToggles[14] = vm.ColVolume;
        _recColumnToggles[15] = vm.ColAvgVolume;
        _recColumnToggles[16] = vm.ColMarketCap;
        _recColumnToggles[17] = vm.ColPE;
        _recColumnToggles[18] = vm.ColForwardPE;
        _recColumnToggles[19] = vm.ColEPS;
        _recColumnToggles[20] = vm.ColPriceToBook;
        _recColumnToggles[21] = vm.Col52WkHigh;
        _recColumnToggles[22] = vm.Col52WkLow;
        _recColumnToggles[23] = vm.ColBeta;
        _recColumnToggles[24] = vm.ColDivYield;
        _recColumnToggles[25] = vm.ColShortRatio;
        _recColumnToggles[26] = vm.ColIV;
        _recColumnToggles[27] = vm.ColTheta;
        _recColumnToggles[28] = vm.ColReasoning;
    }

    private void ApplyColumnVisibility()
    {
        var cols = FullRecsGrid.Columns;
        foreach (var (index, toggle) in _recColumnToggles)
            if (index < cols.Count)
                cols[index].IsVisible = toggle.IsVisible;
    }

    // ── Glossary-backed header tooltips ─────────────────────────────────────────

    /// <summary>
    /// Sources the Recommendations-grid header tooltips from the canonical
    /// <see cref="Glossary"/> so a term is documented in exactly one place. Only columns
    /// that map to a real Glossary key are touched; every other header is left as-is.
    /// </summary>
    /// <remarks>
    /// Column indices match the <c>DataGrid.Columns</c> declaration order in MainWindow.axaml
    /// (the same convention <see cref="BuildRecColumnMap"/> relies on). Where Phase 6B set an
    /// inline <c>TextBlock</c> header with a hard-coded tooltip, that tooltip is replaced by the
    /// Glossary one; plain-string headers are wrapped in a <c>TextBlock</c> that preserves their
    /// display text so <see cref="GetColumnKey"/> (used for column-order persistence) is unaffected.
    /// </remarks>
    private void ApplyGlossaryHeaderTooltips()
    {
        // Full recommendations grid: column index → Glossary key.
        ApplyGlossaryTooltips(FullRecsGrid, new Dictionary<int, string>
        {
            [0]  = "Symbol",
            [2]  = "Action",
            [3]  = "LastPrice",
            [5]  = "DayChangePct",
            [6]  = "RSI14",
            [7]  = "WeekReturnPct",
            [8]  = "SMA20",
            [9]  = "SMA50",
            [11] = "Confidence",
            [12] = "BuyDate",
            [13] = "SellDate",
            [28] = "Reasoning",
        });

        // Compact recommendations grid (narrow layout): fewer columns, same source of truth.
        ApplyGlossaryTooltips(CompactRecsGrid, new Dictionary<int, string>
        {
            [0] = "Symbol",
            [1] = "Action",
            [2] = "LastPrice",
            [3] = "DayChangePct",
            [4] = "RSI14",
            [5] = "BuyDate",
            [6] = "SellDate",
            [7] = "Confidence",
        });
    }

    private static void ApplyGlossaryTooltips(DataGrid grid, IReadOnlyDictionary<int, string> map)
    {
        var cols = grid.Columns;
        foreach (var (index, key) in map)
        {
            if (index >= cols.Count) continue;
            if (!Glossary.TryGet(key, out var def) || def is null) continue;

            var col = cols[index];
            if (col.Header is TextBlock tb)
            {
                // Inline TextBlock header (Phase 6B) — retarget its tooltip to the Glossary.
                ToolTip.SetTip(tb, def.Tooltip);
            }
            else
            {
                // Plain-string header — wrap it so ToolTip.Tip attaches to a Control while the
                // displayed text (and thus the persisted column key) stays identical.
                var text = col.Header as string ?? col.Header?.ToString() ?? key;
                var header = new TextBlock { Text = text };
                ToolTip.SetTip(header, def.Tooltip);
                col.Header = header;
            }
        }
    }

    // ── Sort persistence ──────────────────────────────────────────────────────

    private void RestoreSortToGrid(MainViewModel vm)
    {
        var view = vm.RecommendationsView;
        view.SortDescriptions.Clear();

        if (string.IsNullOrEmpty(vm.SavedSortColumn))
        {
            // Default sort: Confidence DESC, action rank ASC, then Symbol ASC.
            view.SortDescriptions.Add(DataGridSortDescription.FromPath(nameof(Recommendation.Confidence),      ListSortDirection.Descending));
            view.SortDescriptions.Add(DataGridSortDescription.FromPath(nameof(Recommendation.ActionSortOrder), ListSortDirection.Ascending));
            view.SortDescriptions.Add(DataGridSortDescription.FromPath(nameof(Recommendation.Symbol),          ListSortDirection.Ascending));
        }
        else
        {
            var dir = vm.SavedSortDirection == "Descending"
                ? ListSortDirection.Descending
                : ListSortDirection.Ascending;
            view.SortDescriptions.Add(DataGridSortDescription.FromPath(vm.SavedSortColumn, dir));
        }

        view.Refresh();
    }

    private void FullRecsGrid_Sorting(object? sender, DataGridColumnEventArgs e)
    {
        // Avalonia applies the sort after this event; read the resulting state next tick.
        Dispatcher.UIThread.Post(() =>
        {
            if (VM is not { } vm) return;
            var descs = vm.RecommendationsView.SortDescriptions;
            if (descs.Count == 0)
            {
                vm.SavedSortColumn    = string.Empty;
                vm.SavedSortDirection = "Ascending";
            }
            else
            {
                var first = descs[0];
                vm.SavedSortColumn    = first.HasPropertyPath ? first.PropertyPath : string.Empty;
                vm.SavedSortDirection = first.Direction == ListSortDirection.Descending
                    ? "Descending" : "Ascending";
            }
        }, DispatcherPriority.Background);
    }

    // ── Column-order persistence ────────────────────────────────────────────────

    private void RestoreColumnOrderToGrid(MainViewModel vm)
    {
        var saved = vm.SavedColumnOrder;
        if (saved.Count == 0) return;

        var pairs = FullRecsGrid.Columns
            .Select(c => (col: c, key: GetColumnKey(c)))
            .Where(x => saved.ContainsKey(x.key))
            .Select(x => (x.col, target: saved[x.key]))
            .OrderBy(x => x.target)
            .ToList();

        foreach (var (col, target) in pairs)
            if (col.DisplayIndex != target)
                col.DisplayIndex = target;
    }

    private void FullRecsGrid_ColumnReordered(object? sender, DataGridColumnEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (VM is not { } vm) return;
            var order = new Dictionary<string, int>();
            foreach (var col in FullRecsGrid.Columns)
            {
                var key = GetColumnKey(col);
                if (!string.IsNullOrEmpty(key))
                    order[key] = col.DisplayIndex;
            }
            vm.SavedColumnOrder = order;
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// Stable key for a Recommendations column: the header text (string headers and the
    /// TextBlock-wrapped headers that carry documentation tooltips both yield the same text
    /// WPF used), falling back to the sort path. Keeps SavedColumnOrder keys consistent with
    /// the WPF persisted format.
    /// </summary>
    private static string GetColumnKey(DataGridColumn col) => col.Header switch
    {
        string s      => s,
        TextBlock tb  => tb.Text ?? string.Empty,
        _             => col.SortMemberPath ?? string.Empty,
    };

    // ── Row tint / context-menu / tooltip handlers ──────────────────────────────

    private void RecsRow_LoadingRow(object? sender, DataGridRowEventArgs e)
    {
        if (e.Row.DataContext is not Recommendation r) { e.Row.Background = null; return; }
        e.Row.Background = r.Action switch
        {
            RecommendationAction.StrongBuy  => StrongBuyBrush,
            RecommendationAction.Buy        => BuyBrush,
            RecommendationAction.Sell       => SellBrush,
            RecommendationAction.StrongSell => StrongSellBrush,
            _                               => null,
        };
        AttachRowContextMenu(e.Row, r.Symbol);
        ToolTip.SetTip(e.Row, BuildRecTooltip(r));
    }

    private void DayPickRow_LoadingRow(object? sender, DataGridRowEventArgs e)
    {
        if (e.Row.DataContext is not DayPick d) { e.Row.Background = null; return; }
        e.Row.Background = d.Direction switch
        {
            DayPickDirection.Long  => BuyBrush,
            DayPickDirection.Short => SellBrush,
            _                      => null,
        };
        ToolTip.SetTip(e.Row, BuildSignalTooltip(d.Symbol, d.CompanyName, d.TriggerReason, d.GeneratedAt));
    }

    private void EarningsRow_LoadingRow(object? sender, DataGridRowEventArgs e)
    {
        if (e.Row.DataContext is not EarningsPick ep) { e.Row.Background = null; return; }
        e.Row.Background = ep.MeetsThreshold ? BuyBrush : null;
        ToolTip.SetTip(e.Row, BuildSignalTooltip(ep.Symbol, ep.CompanyName, ep.TriggerReason, ep.GeneratedAt));
    }

    private void WatchRow_LoadingRow(object? sender, DataGridRowEventArgs e)
    {
        if (e.Row.DataContext is not Recommendation r) { e.Row.Background = null; return; }
        e.Row.Background = r.WatchIsUp switch
        {
            true  => BuyBrush,
            false => SellBrush,
            null  => null,
        };
        AttachRowContextMenu(e.Row, r.Symbol);
        var watch = new StackPanel();
        watch.Children.Add(new TextBlock { Text = r.Symbol, FontWeight = FontWeight.Bold, FontSize = 13 });
        watch.Children.Add(new TextBlock { Text = r.CompanyName, Foreground = Brush.Parse("#444"), Margin = new Avalonia.Thickness(0, 2, 0, 6) });
        watch.Children.Add(new TextBlock { Text = $"Added at: ${r.WatchedPrice:F2}   Now: ${r.LastPrice:F2}" });
        watch.Children.Add(new TextBlock { Text = $"Change since watch: {r.WatchChangePctDisplay}", FontWeight = FontWeight.SemiBold });
        ToolTip.SetTip(e.Row, watch);
    }

    private static Control BuildSignalTooltip(string symbol, string company, string reason, DateTime generatedAt)
    {
        var panel = new StackPanel { MaxWidth = 360 };
        panel.Children.Add(new TextBlock { Text = symbol, FontWeight = FontWeight.Bold, FontSize = 13 });
        panel.Children.Add(new TextBlock { Text = company, Foreground = Brush.Parse("#444"), Margin = new Avalonia.Thickness(0, 2, 0, 4) });
        if (!string.IsNullOrWhiteSpace(reason))
            panel.Children.Add(new TextBlock { Text = reason, TextWrapping = TextWrapping.Wrap, FontStyle = FontStyle.Italic, Margin = new Avalonia.Thickness(0, 6, 0, 0) });
        panel.Children.Add(new TextBlock { Text = $"Generated: {generatedAt:HH:mm}", Foreground = Brush.Parse("#888"), FontSize = 10, Margin = new Avalonia.Thickness(0, 6, 0, 0) });
        return panel;
    }

    private void HeldRow_LoadingRow(object? sender, DataGridRowEventArgs e)
    {
        if (e.Row.DataContext is not HeldPosition h) { e.Row.Background = null; return; }
        e.Row.Background = h.IsProfit switch
        {
            true  => BuyBrush,
            false => SellBrush,
            null  => null,
        };
        AttachRowContextMenu(e.Row, h.Symbol);
    }

    /// <summary>
    /// Reproduces the WPF shared RowContextMenu (Open in Yahoo Finance + Ask-AI submenu).
    /// Commands are bound directly to the VM in code (the WPF version reached the VM through a
    /// BindingProxy, which Avalonia's column/menu popups can't inherit reliably). Built per
    /// realized/recycled row so the Yahoo parameter tracks the row's symbol.
    /// </summary>
    private void AttachRowContextMenu(DataGridRow row, string symbol)
    {
        if (VM is not { } vm) return;

        var openYahoo = new MenuItem
        {
            Header = "Open in Yahoo Finance",
            Command = vm.OpenInBrowserCommand,
            CommandParameter = symbol,
        };

        var askClaude  = new MenuItem { Header = "🤖  Ask Claude",  Command = vm.AskAICommand, CommandParameter = "claude" };
        var askGemini  = new MenuItem { Header = "✦  Ask Gemini",   Command = vm.AskAICommand, CommandParameter = "gemini" };
        var askCopilot = new MenuItem { Header = "⊕  Ask Copilot",  Command = vm.AskAICommand, CommandParameter = "copilot" };
        var askAI = new MenuItem { Header = "Ask AI about this stock" };
        askAI.Items.Add(askClaude);
        askAI.Items.Add(askGemini);
        askAI.Items.Add(askCopilot);

        var menu = new ContextMenu();
        menu.Items.Add(openYahoo);
        menu.Items.Add(new Separator());
        menu.Items.Add(askAI);
        row.ContextMenu = menu;
    }

    private static Control BuildRecTooltip(Recommendation r)
    {
        // Compact port of the WPF row ToolTip (company / sector / key signals / reasoning).
        var panel = new StackPanel { MaxWidth = 380 };
        panel.Children.Add(new TextBlock { Text = r.CompanyName, FontWeight = FontWeight.Bold, FontSize = 13 });
        panel.Children.Add(new TextBlock { Text = $"Sector: {r.Sector}", Foreground = Brush.Parse("#666"), Margin = new Avalonia.Thickness(0, 2, 0, 6) });
        panel.Children.Add(new TextBlock
        {
            Text = $"Price: ${r.LastPrice:F2}   Change: {r.DayChangePctDisplay}   RSI14: {r.RSI14:F1}",
        });
        if (!string.IsNullOrWhiteSpace(r.Reasoning))
            panel.Children.Add(new TextBlock
            {
                Text = r.Reasoning,
                TextWrapping = TextWrapping.Wrap,
                FontStyle = FontStyle.Italic,
                Foreground = Brush.Parse("#444"),
                Margin = new Avalonia.Thickness(0, 6, 0, 0),
            });
        return panel;
    }

    // ── Destructive portfolio actions (require confirmation) ────────────────────

    private async void RemoveFromWatch_Click(object? sender, RoutedEventArgs e)
    {
        if (VM is not { } vm) return;

        var grid = WatchGridFull.IsVisible ? WatchGridFull : WatchGridCompact;
        var selected = grid.SelectedItems.OfType<Recommendation>().ToList();
        if (selected.Count == 0) return;

        var msg = selected.Count == 1
            ? $"Remove {selected[0].Symbol} from your watch list?"
            : $"Remove {selected.Count} stocks from your watch list?";

        if (await MessageDialog.ConfirmAsync(this, "Confirm Remove", msg))
            vm.RemoveMultipleFromWatch(selected.Select(r => r.Symbol));
    }

    private async void RemoveFromPosition_Click(object? sender, RoutedEventArgs e)
    {
        if (VM is not { } vm || vm.SelectedHeld == null) return;
        var msg = $"Remove {vm.SelectedHeld.Symbol} WITHOUT recording a sale?\n\n" +
                  "Use this only to delete a mistaken entry — no cash is credited and nothing is " +
                  "added to history. To close out a position, use Sell instead.";
        if (await MessageDialog.ConfirmAsync(this, "Confirm Remove", msg))
            vm.RemoveFromHeldCommand.Execute(null);
    }

    // ── Sell / cash / history ───────────────────────────────────────────────────

    private async void SellPosition_Click(object? sender, RoutedEventArgs e)
    {
        if (VM is not { } vm) return;
        if (vm.SelectedHeld == null)
        {
            await MessageDialog.InfoAsync(this, "No position selected", "Select a position to sell first.");
            return;
        }
        var dlg = new SellPositionWindow(vm.SelectedHeld);
        var result = await dlg.ShowDialog<SellPositionWindow.SellResult?>(this);
        if (result is { } r)
            await vm.SellSelectedPosition(r.SellPrice, r.SellDate);
    }

    private async void DepositCash_Click(object? sender, RoutedEventArgs e)
    {
        if (VM is not { } vm) return;
        var dlg = new CashTransactionWindow(isWithdrawal: false, vm.CashBalance);
        var result = await dlg.ShowDialog<CashTransactionWindow.CashResult?>(this);
        if (result is { } c)
            await vm.DepositCash(c.Amount, c.Date, c.Note);
    }

    private async void WithdrawCash_Click(object? sender, RoutedEventArgs e)
    {
        if (VM is not { } vm) return;
        var dlg = new CashTransactionWindow(isWithdrawal: true, vm.CashBalance);
        var result = await dlg.ShowDialog<CashTransactionWindow.CashResult?>(this);
        if (result is { } c)
            await vm.WithdrawCash(c.Amount, c.Date, c.Note);
    }

    private async void ShowHistory_Click(object? sender, RoutedEventArgs e)
    {
        if (VM is not { } vm) return;
        await new TransactionHistoryWindow(vm.GetTransactions()).ShowDialog(this);
    }

    private async void EditCash_Click(object? sender, RoutedEventArgs e)
    {
        if (VM is not { } vm) return;
        var dlg = new EditCashWindow(vm.CashBalance);
        var result = await dlg.ShowDialog<decimal?>(this);
        if (result is decimal newBalance)
            await vm.EditCash(newBalance);
    }

    // ── Manual position add / edit ──────────────────────────────────────────────

    private async void AddPosition_Click(object? sender, RoutedEventArgs e)
        => await OpenPositionEditorAsync(null);

    private async void EditPosition_Click(object? sender, RoutedEventArgs e)
    {
        if (VM is { SelectedHeld: not null } vm)
            await OpenPositionEditorAsync(vm.SelectedHeld);
    }

    private async void HeldGrid_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (VM is { SelectedHeld: not null } vm)
            await OpenPositionEditorAsync(vm.SelectedHeld);
    }

    private async Task OpenPositionEditorAsync(HeldPosition? existing)
    {
        if (VM is not { } vm) return;
        var dlg = new PositionEditWindow(existing);
        var result = await dlg.ShowDialog<HeldPosition?>(this);
        if (result is { } position)
            await vm.UpsertPosition(position);
    }

    // ── Save News briefing ──────────────────────────────────────────────────────

    private async void SaveNews_Click(object? sender, RoutedEventArgs e)
    {
        if (VM is not { } vm) return;

        if (!vm.HasNewsReport)
        {
            await MessageDialog.InfoAsync(this, "Nothing to save",
                "There's no briefing to save yet — run a scan first.");
            return;
        }

        var top = TopLevel.GetTopLevel(this);
        if (top?.StorageProvider is not { } sp) return;

        var file = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title              = "Save News Briefing",
            SuggestedFileName  = vm.SuggestedNewsFileName,
            DefaultExtension   = "md",
            ShowOverwritePrompt = true,
            FileTypeChoices = new[]
            {
                new FilePickerFileType("Markdown") { Patterns = new[] { "*.md" } },
                new FilePickerFileType("Text file") { Patterns = new[] { "*.txt" } },
                new FilePickerFileType("All files") { Patterns = new[] { "*" } },
            },
        });

        var path = file?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path))
            vm.SaveNewsReport(path);
    }

    // ── Glossary panel ──────────────────────────────────────────────────────────

    private async void Glossary_Click(object? sender, RoutedEventArgs e)
        => await new GlossaryWindow().ShowDialog(this);

    // ── Settings dialog ─────────────────────────────────────────────────────────

    private async void Settings_Click(object? sender, RoutedEventArgs e)
    {
        if (VM is not { } vm) return;

        // Snapshot scanning/analysis settings so we can tell what changed.
        var snapshot = vm.CaptureScanSettings();

        var settings = new SettingsWindow { DataContext = vm };
        await settings.ShowDialog(this);

        // Refresh every table + regenerate the briefing against the new targets/data.
        await vm.RefreshAfterSettingsAsync(snapshot);
    }
}
