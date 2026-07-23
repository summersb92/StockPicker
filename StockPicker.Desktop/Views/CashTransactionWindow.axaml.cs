using System;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace StockPicker.Desktop.Views;

/// <summary>
/// Modal dialog to record a cash deposit (injection) or withdrawal. Captures an amount,
/// date, and optional note.
/// </summary>
/// <remarks>
/// WPF-ADAPTATION result contract: opened with
/// <c>await ShowDialog&lt;CashTransactionWindow.CashResult?&gt;(owner)</c> — returns a
/// <see cref="CashResult"/> (amount, date, note) on OK, or <c>null</c> on Cancel (replaces
/// the WPF <c>DialogResult</c> + public <c>Confirmed</c>/<c>Amount</c>/<c>Date</c>/<c>Note</c>
/// properties).
/// <list type="bullet">
///   <item>Both <c>MessageBox.Show</c> validation prompts (non-positive amount, and
///   withdrawal exceeding available cash) collapse into the inline <c>ErrorText</c>
///   TextBlock, each with its own message.</item>
///   <item>WPF <c>DatePicker.SelectedDate</c> (<c>DateTime?</c>) → Avalonia
///   <c>DateTimeOffset?</c>, converted at the boundary.</item>
/// </list>
/// </remarks>
public partial class CashTransactionWindow : Window
{
    /// <summary>Confirmed cash transaction: amount, date, and note.</summary>
    public record CashResult(decimal Amount, DateTime Date, string Note);

    private readonly bool    _isWithdrawal;
    private readonly decimal _available;

    public CashTransactionWindow() : this(false, 0m) { }

    /// <param name="isWithdrawal">True for a withdrawal, false for a deposit.</param>
    /// <param name="availableCash">Current cash balance (used to validate withdrawals).</param>
    public CashTransactionWindow(bool isWithdrawal, decimal availableCash)
    {
        InitializeComponent();
        _isWithdrawal = isWithdrawal;
        _available    = availableCash;

        Title            = isWithdrawal ? "Withdraw Cash" : "Deposit Cash";
        OkButton.Content = isWithdrawal ? "Withdraw" : "Deposit";
        HeaderText.Text  = isWithdrawal
            ? $"Withdraw cash from the account. Available: ${availableCash:N2}."
            : "Record a cash deposit (injection) into the account.";

        DatePickerBox.SelectedDate = new DateTimeOffset(DateTime.SpecifyKind(DateTime.Today, DateTimeKind.Unspecified), TimeSpan.Zero);
        Loaded += (_, _) => AmountBox.Focus();
    }

    private void Ok_Click(object? sender, RoutedEventArgs e)
    {
        if (!decimal.TryParse(AmountBox.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var amount) || amount <= 0)
        {
            ErrorText.Text = "Enter a positive dollar amount.";
            ErrorText.IsVisible = true;
            AmountBox.Focus();
            return;
        }

        if (_isWithdrawal && amount > _available)
        {
            ErrorText.Text = $"You can't withdraw more than the available cash (${_available:N2}).";
            ErrorText.IsVisible = true;
            AmountBox.Focus();
            return;
        }

        var date = DatePickerBox.SelectedDate?.DateTime ?? DateTime.Today;
        var note = (NoteBox.Text ?? "").Trim();
        Close(new CashResult(amount, date, note));
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);
}
