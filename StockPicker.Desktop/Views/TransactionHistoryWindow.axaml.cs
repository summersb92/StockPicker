using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using StockPicker.Models;

namespace StockPicker.Desktop.Views;

/// <summary>
/// Read-only window listing the full portfolio ledger (buys, sells, deposits,
/// withdrawals), newest first, with a roll-up summary.
/// </summary>
/// <remarks>
/// WPF-ADAPTATION result contract: this window returns nothing — it is informational.
/// Opened with a fire-and-forget <c>await ShowDialog(owner)</c>; <see cref="Close_Click"/>
/// (and the Cancel button) simply close it.
/// <list type="bullet">
///   <item>WPF <c>DataGrid.RowStyle</c> + <c>DataTrigger</c>s that tinted rows by
///   transaction Type are reproduced in <see cref="HistoryGrid_LoadingRow"/> (Avalonia has
///   no row-style triggers), which assigns <c>tintTx*</c> style classes (styled in
///   ModernTheme.axaml — a class keeps the tint below the row hover highlight, unlike a
///   local Background). LoadingRow fires on realize and on recycle, so the tint stays
///   correct under virtualization.</item>
///   <item>WPF <c>SortMemberPath</c> (sort formatted Date/Cash/Realized columns by their
///   underlying values) has no Avalonia column property; reproduced with
///   <c>CustomSortComparer</c> on the named columns.</item>
///   <item>WPF <c>AlternatingRowBackground</c> has no Avalonia DataGrid equivalent and is
///   dropped — the Type tint is the meaningful visual cue.</item>
/// </list>
/// </remarks>
public partial class TransactionHistoryWindow : Window
{
    // Row tint style classes by transaction Type — see DataGridRow.tintTx* in
    // Themes/ModernTheme.axaml.
    private const string TintBuy        = "tintTxBuy";
    private const string TintSell       = "tintTxSell";
    private const string TintWithdrawal = "tintTxWithdrawal";
    private static readonly string[] TintClasses = { TintBuy, TintSell, TintWithdrawal };

    public TransactionHistoryWindow() : this(Array.Empty<Transaction>()) { }

    public TransactionHistoryWindow(IReadOnlyList<Transaction> transactions)
    {
        InitializeComponent();

        // Reproduce WPF SortMemberPath: sort by the underlying value, not the display text.
        // (Columns can't expose a generated x:Name field — they aren't in the visual tree —
        //  so they are reached positionally, matching the column order in the .axaml.)
        HistoryGrid.Columns[0].CustomSortComparer = new TransactionFieldComparer(t => t.Date);            // Date
        HistoryGrid.Columns[5].CustomSortComparer = new TransactionFieldComparer(t => t.CashDelta);       // Cash
        HistoryGrid.Columns[6].CustomSortComparer = new TransactionFieldComparer(t => t.RealizedGain ?? decimal.MinValue); // Realized

        HistoryGrid.LoadingRow += HistoryGrid_LoadingRow;

        // Header tooltips sourced from the canonical Glossary (index → key; only columns
        // whose concept has a real Glossary entry are mapped — "Detail" combines
        // shares @ price into one display string and is left untouched).
        GlossaryTooltips.Apply(HistoryGrid, new Dictionary<int, string>
        {
            [0] = "Date",
            [1] = "Type",
            [2] = "Symbol",
            [4] = "GrossAmount",
            [5] = "CashDelta",
            [6] = "RealizedGain",
        });

        var ordered = transactions.OrderByDescending(t => t.Date)
                                  .ThenByDescending(t => t.Type == TransactionType.Sell)
                                  .ToList();
        HistoryGrid.ItemsSource = ordered;

        if (transactions.Count == 0)
        {
            SummaryText.Text = "No transactions yet. Sells, deposits, and withdrawals will appear here.";
            return;
        }

        decimal deposits    = transactions.Where(t => t.Type == TransactionType.Deposit).Sum(t => t.CashDelta);
        decimal withdrawals = -transactions.Where(t => t.Type == TransactionType.Withdrawal).Sum(t => t.CashDelta);
        decimal saleProceeds = transactions.Where(t => t.Type == TransactionType.Sell).Sum(t => t.CashDelta);
        decimal realized    = transactions.Where(t => t.RealizedGain.HasValue).Sum(t => t.RealizedGain!.Value);
        int buys  = transactions.Count(t => t.Type == TransactionType.Buy);
        int sells = transactions.Count(t => t.Type == TransactionType.Sell);

        SummaryText.Text =
            $"{transactions.Count} transactions  ·  {buys} buys, {sells} sells   |   " +
            $"Deposits ${deposits:N2}  ·  Withdrawals ${withdrawals:N2}  ·  " +
            $"Sale proceeds ${saleProceeds:N2}  ·  Realized P/L {(realized >= 0 ? "+" : "-")}${Math.Abs(realized):N2}";
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void HistoryGrid_LoadingRow(object? sender, DataGridRowEventArgs e)
    {
        // Remove stale classes then apply, because rows are recycled during virtualization.
        foreach (var c in TintClasses)
            e.Row.Classes.Remove(c);

        var tint = (e.Row.DataContext as Transaction)?.Type switch
        {
            TransactionType.Buy        => TintBuy,
            TransactionType.Sell       => TintSell,
            TransactionType.Withdrawal => TintWithdrawal,
            _                          => null,
        };
        if (tint is not null)
            e.Row.Classes.Add(tint);
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Compares two <see cref="Transaction"/> rows by a chosen field, reproducing WPF's
    /// SortMemberPath. Avalonia hands the bound row items to the comparer.
    /// </summary>
    private sealed class TransactionFieldComparer : IComparer
    {
        private readonly Func<Transaction, IComparable> _key;
        public TransactionFieldComparer(Func<Transaction, IComparable> key) => _key = key;

        public int Compare(object? x, object? y)
        {
            if (x is not Transaction tx || y is not Transaction ty) return 0;
            return Comparer<IComparable>.Default.Compare(_key(tx), _key(ty));
        }
    }
}
