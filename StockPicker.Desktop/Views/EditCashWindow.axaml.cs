using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace StockPicker.Desktop.Views;

/// <summary>
/// Modal dialog to directly set the cash balance (correction / testing).
/// Does not imply a ledger transaction.
/// </summary>
/// <remarks>
/// WPF-ADAPTATION: this is the Avalonia async-dialog pattern proof.
/// <list type="bullet">
///   <item>Opened with <c>await ShowDialog&lt;decimal?&gt;(owner)</c> — returns the new
///   balance on confirm, or <c>null</c> on cancel (instead of the WPF
///   <c>DialogResult</c> + public <c>Confirmed</c>/<c>NewBalance</c> properties).</item>
///   <item>WPF's <c>MessageBox.Show</c> validation is replaced with an inline error
///   <c>TextBlock</c> (Avalonia has no built-in MessageBox).</item>
///   <item><c>Loaded</c> handler focuses/selects the textbox as before.</item>
/// </list>
/// </remarks>
public partial class EditCashWindow : Window
{
    public EditCashWindow() : this(0m) { }

    public EditCashWindow(decimal currentBalance)
    {
        InitializeComponent();
        CashBox.Text = currentBalance.ToString("0.##", CultureInfo.CurrentCulture);
        Loaded += (_, _) => { CashBox.Focus(); CashBox.SelectAll(); };
    }

    private void Set_Click(object? sender, RoutedEventArgs e)
    {
        if (!decimal.TryParse(CashBox.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var value) || value < 0)
        {
            ErrorText.IsVisible = true;
            CashBox.Focus();
            return;
        }

        Close(value);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);
}
