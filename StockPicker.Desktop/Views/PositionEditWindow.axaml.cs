using System;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using StockPicker.Models;

namespace StockPicker.Desktop.Views;

/// <summary>
/// Modal dialog to add a new held position or edit an existing one.
/// </summary>
/// <remarks>
/// WPF-ADAPTATION result contract: opened with
/// <c>await ShowDialog&lt;HeldPosition?&gt;(owner)</c> — returns the validated
/// <see cref="HeldPosition"/> on Save, or <c>null</c> on Cancel (replaces the WPF
/// <c>DialogResult</c> + public <c>Result</c> property).
/// <list type="bullet">
///   <item>Four <c>MessageBox.Show</c> validation prompts (missing symbol, invalid entry
///   price, invalid shares, invalid margin %/rate) collapse into the inline
///   <c>ErrorText</c> TextBlock, each with its own message.</item>
///   <item>WPF <c>DatePicker.SelectedDate</c> is <c>DateTime?</c>; Avalonia's is
///   <c>DateTimeOffset?</c> — converted at the boundary here.</item>
///   <item>WPF <c>TextBox.CharacterCasing="Upper"</c> has no Avalonia equivalent and is
///   dropped; the symbol is still upper-cased on Save (as WPF did) so the stored value is
///   unchanged. Live upper-casing of the on-screen text is the only lost nicety.</item>
/// </list>
/// </remarks>
public partial class PositionEditWindow : Window
{
    private readonly bool _isEdit;

    public PositionEditWindow() : this(null) { }

    public PositionEditWindow(HeldPosition? existing = null)
    {
        InitializeComponent();

        _isEdit = existing != null;
        Title   = _isEdit ? "Edit Position" : "Add Position";

        HoldingPeriodBox.ItemsSource = Enum.GetValues(typeof(HoldingPeriod));

        var p = existing ?? new HeldPosition
        {
            EntryDate     = DateTime.Today,
            HoldingPeriod = HoldingPeriod.Unspecified,
            SourceTag     = "Manual",
        };

        SymbolBox.Text                 = p.Symbol;
        SymbolBox.IsReadOnly           = _isEdit;          // symbol identifies the position
        CompanyBox.Text                = p.CompanyName;
        EntryPriceBox.Text             = p.EntryPrice > 0 ? p.EntryPrice.ToString("0.####") : "";
        SharesBox.Text                 = p.ShareCount > 0 ? p.ShareCount.ToString() : "";
        EntryDatePicker.SelectedDate   = ToOffset(p.EntryDate == default ? DateTime.Today : p.EntryDate);
        PlannedSellPicker.SelectedDate = p.PlannedSellDate.HasValue ? ToOffset(p.PlannedSellDate.Value) : null;
        HoldingPeriodBox.SelectedItem  = p.HoldingPeriod;
        SourceBox.Text                 = string.IsNullOrWhiteSpace(p.SourceTag) ? "Manual" : p.SourceTag;
        NotesBox.Text                  = p.Notes;

        // Margin: prefill sensible defaults (50% / 12.5%) so a freshly-ticked box is usable.
        MarginCheck.IsChecked = p.BoughtOnMargin;
        MarginPercentBox.Text = (p.MarginPercent > 0 ? p.MarginPercent : 50m).ToString("0.###");
        MarginRateBox.Text    = (p.MarginInterestRatePercent > 0 ? p.MarginInterestRatePercent : 12.5m).ToString("0.###");

        // Focus the first editable field.
        Loaded += (_, _) => { if (_isEdit) EntryPriceBox.Focus(); else SymbolBox.Focus(); };
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private static DateTimeOffset ToOffset(DateTime dt) =>
        new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Unspecified), TimeSpan.Zero);

    private void ShowError(string message, Control focus)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
        focus.Focus();
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        var symbol = (SymbolBox.Text ?? "").Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(symbol))
        {
            ShowError("A ticker symbol is required.", SymbolBox);
            return;
        }

        if (!decimal.TryParse(EntryPriceBox.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var entryPrice) || entryPrice < 0)
        {
            ShowError("Enter a valid entry price (a non-negative number).", EntryPriceBox);
            return;
        }

        if (!int.TryParse(SharesBox.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var shares) || shares < 0)
        {
            ShowError("Enter a valid share count (a non-negative whole number).", SharesBox);
            return;
        }

        bool onMargin = MarginCheck.IsChecked == true;
        decimal marginPercent = 50m, marginRate = 0m;
        if (onMargin)
        {
            if (!decimal.TryParse(MarginPercentBox.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out marginPercent) || marginPercent <= 0 || marginPercent > 100)
            {
                ShowError("Enter a margin % between 0 (exclusive) and 100. 50% means 2× leverage.", MarginPercentBox);
                return;
            }

            if (!decimal.TryParse(MarginRateBox.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out marginRate) || marginRate < 0)
            {
                ShowError("Enter a valid annual interest rate (a non-negative number).", MarginRateBox);
                return;
            }
        }

        var result = new HeldPosition
        {
            Symbol          = symbol,
            CompanyName     = (CompanyBox.Text ?? "").Trim(),
            EntryPrice      = entryPrice,
            ShareCount      = shares,
            EntryDate       = EntryDatePicker.SelectedDate?.DateTime ?? DateTime.Today,
            PlannedSellDate = PlannedSellPicker.SelectedDate?.DateTime,
            HoldingPeriod   = HoldingPeriodBox.SelectedItem is HoldingPeriod hp
                                  ? hp : HoldingPeriod.Unspecified,
            SourceTag       = string.IsNullOrWhiteSpace(SourceBox.Text) ? "Manual" : SourceBox.Text.Trim(),
            Notes           = (NotesBox.Text ?? "").Trim(),
            BoughtOnMargin            = onMargin,
            MarginPercent             = onMargin ? marginPercent : 50m,
            MarginInterestRatePercent = onMargin ? marginRate : 0m,
        };

        Close(result);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);
}
