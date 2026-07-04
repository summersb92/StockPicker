using System;
using System.Windows;

namespace StockPicker.Views
{
    /// <summary>
    /// Modal dialog to record a cash deposit (injection) or withdrawal. Captures an amount,
    /// date, and optional note; results are exposed via <see cref="Amount"/>, <see cref="Date"/>,
    /// and <see cref="Note"/> when confirmed.
    /// </summary>
    public partial class CashTransactionWindow : Window
    {
        private readonly bool    _isWithdrawal;
        private readonly decimal _available;

        public bool     Confirmed { get; private set; }
        public decimal  Amount    { get; private set; }
        public DateTime Date      { get; private set; }
        public string   Note      { get; private set; } = string.Empty;

        /// <param name="isWithdrawal">True for a withdrawal, false for a deposit.</param>
        /// <param name="availableCash">Current cash balance (used to validate withdrawals).</param>
        public CashTransactionWindow(bool isWithdrawal, decimal availableCash)
        {
            InitializeComponent();
            _isWithdrawal = isWithdrawal;
            _available    = availableCash;

            Title          = isWithdrawal ? "Withdraw Cash" : "Deposit Cash";
            OkButton.Content = isWithdrawal ? "Withdraw" : "Deposit";
            HeaderText.Text  = isWithdrawal
                ? $"Withdraw cash from the account. Available: ${availableCash:N2}."
                : "Record a cash deposit (injection) into the account.";

            DatePickerBox.SelectedDate = DateTime.Today;
            Loaded += (_, _) => AmountBox.Focus();
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (!decimal.TryParse(AmountBox.Text, out var amount) || amount <= 0)
            {
                MessageBox.Show(this, "Enter a positive dollar amount.",
                    "Invalid amount", MessageBoxButton.OK, MessageBoxImage.Warning);
                AmountBox.Focus();
                return;
            }

            if (_isWithdrawal && amount > _available)
            {
                MessageBox.Show(this,
                    $"You can't withdraw more than the available cash (${_available:N2}).",
                    "Insufficient cash", MessageBoxButton.OK, MessageBoxImage.Warning);
                AmountBox.Focus();
                return;
            }

            Amount    = amount;
            Date      = DatePickerBox.SelectedDate ?? DateTime.Today;
            Note      = (NoteBox.Text ?? "").Trim();
            Confirmed = true;
            DialogResult = true;
        }
    }
}
