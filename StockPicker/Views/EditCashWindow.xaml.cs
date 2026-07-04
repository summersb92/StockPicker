using System.Windows;

namespace StockPicker.Views
{
    /// <summary>
    /// Modal dialog to directly set the cash balance (correction / testing). Exposes the new
    /// value via <see cref="NewBalance"/> when confirmed. Does not imply a ledger transaction.
    /// </summary>
    public partial class EditCashWindow : Window
    {
        public bool    Confirmed  { get; private set; }
        public decimal NewBalance { get; private set; }

        public EditCashWindow(decimal currentBalance)
        {
            InitializeComponent();
            CashBox.Text = currentBalance.ToString("0.##");
            Loaded += (_, _) => { CashBox.Focus(); CashBox.SelectAll(); };
        }

        private void Set_Click(object sender, RoutedEventArgs e)
        {
            if (!decimal.TryParse(CashBox.Text, out var value) || value < 0)
            {
                MessageBox.Show(this, "Enter a valid cash balance (a non-negative number).",
                    "Invalid amount", MessageBoxButton.OK, MessageBoxImage.Warning);
                CashBox.Focus();
                return;
            }

            NewBalance = value;
            Confirmed  = true;
            DialogResult = true;
        }
    }
}
