using System.Collections.Generic;
using System.Linq;
using System.Windows;
using StockPicker.Models;

namespace StockPicker.Views
{
    /// <summary>
    /// Read-only window listing the full portfolio ledger (buys, sells, deposits,
    /// withdrawals), newest first, with a roll-up summary.
    /// </summary>
    public partial class TransactionHistoryWindow : Window
    {
        public TransactionHistoryWindow(IReadOnlyList<Transaction> transactions)
        {
            InitializeComponent();

            var ordered = transactions.OrderByDescending(t => t.Date)
                                      .ThenByDescending(t => t.Type == TransactionType.Sell)
                                      .ToList();
            HistoryGrid.ItemsSource = ordered;

            if (transactions.Count == 0)
            {
                SummaryText.Text = "No transactions yet. Sells, deposits, and withdrawals will appear here.";
                return;
            }

            decimal deposits   = transactions.Where(t => t.Type == TransactionType.Deposit).Sum(t => t.CashDelta);
            decimal withdrawals = -transactions.Where(t => t.Type == TransactionType.Withdrawal).Sum(t => t.CashDelta);
            decimal saleProceeds = transactions.Where(t => t.Type == TransactionType.Sell).Sum(t => t.CashDelta);
            decimal realized    = transactions.Where(t => t.RealizedGain.HasValue).Sum(t => t.RealizedGain!.Value);
            int buys  = transactions.Count(t => t.Type == TransactionType.Buy);
            int sells = transactions.Count(t => t.Type == TransactionType.Sell);

            SummaryText.Text =
                $"{transactions.Count} transactions  ·  {buys} buys, {sells} sells   |   " +
                $"Deposits ${deposits:N2}  ·  Withdrawals ${withdrawals:N2}  ·  " +
                $"Sale proceeds ${saleProceeds:N2}  ·  Realized P/L {(realized >= 0 ? "+" : "-")}${System.Math.Abs(realized):N2}";
        }
    }
}
