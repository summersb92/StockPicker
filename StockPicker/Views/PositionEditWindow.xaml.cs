using System;
using System.Windows;
using StockPicker.Models;

namespace StockPicker.Views
{
    /// <summary>
    /// Modal dialog to add a new held position or edit an existing one.
    /// On Save it validates the inputs and exposes the resulting <see cref="HeldPosition"/>
    /// via <see cref="Result"/>; the caller persists it through the portfolio service.
    /// </summary>
    public partial class PositionEditWindow : Window
    {
        /// <summary>The validated position, set only when the user clicks Save.</summary>
        public HeldPosition? Result { get; private set; }

        private readonly bool _isEdit;

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

            SymbolBox.Text             = p.Symbol;
            SymbolBox.IsReadOnly       = _isEdit;          // symbol identifies the position
            CompanyBox.Text            = p.CompanyName;
            EntryPriceBox.Text         = p.EntryPrice > 0 ? p.EntryPrice.ToString("0.####") : "";
            SharesBox.Text             = p.ShareCount > 0 ? p.ShareCount.ToString() : "";
            EntryDatePicker.SelectedDate   = p.EntryDate == default ? DateTime.Today : p.EntryDate;
            PlannedSellPicker.SelectedDate = p.PlannedSellDate;
            HoldingPeriodBox.SelectedItem  = p.HoldingPeriod;
            SourceBox.Text             = string.IsNullOrWhiteSpace(p.SourceTag) ? "Manual" : p.SourceTag;
            NotesBox.Text              = p.Notes;

            // Focus the first editable field.
            Loaded += (_, _) => { if (_isEdit) EntryPriceBox.Focus(); else SymbolBox.Focus(); };
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            var symbol = (SymbolBox.Text ?? "").Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(symbol))
            {
                MessageBox.Show(this, "A ticker symbol is required.", "Missing symbol",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                SymbolBox.Focus();
                return;
            }

            if (!decimal.TryParse(EntryPriceBox.Text, out var entryPrice) || entryPrice < 0)
            {
                MessageBox.Show(this, "Enter a valid entry price (a non-negative number).",
                    "Invalid entry price", MessageBoxButton.OK, MessageBoxImage.Warning);
                EntryPriceBox.Focus();
                return;
            }

            if (!int.TryParse(SharesBox.Text, out var shares) || shares < 0)
            {
                MessageBox.Show(this, "Enter a valid share count (a non-negative whole number).",
                    "Invalid shares", MessageBoxButton.OK, MessageBoxImage.Warning);
                SharesBox.Focus();
                return;
            }

            Result = new HeldPosition
            {
                Symbol          = symbol,
                CompanyName     = (CompanyBox.Text ?? "").Trim(),
                EntryPrice      = entryPrice,
                ShareCount      = shares,
                EntryDate       = EntryDatePicker.SelectedDate ?? DateTime.Today,
                PlannedSellDate = PlannedSellPicker.SelectedDate,
                HoldingPeriod   = HoldingPeriodBox.SelectedItem is HoldingPeriod hp
                                      ? hp : HoldingPeriod.Unspecified,
                SourceTag       = string.IsNullOrWhiteSpace(SourceBox.Text) ? "Manual" : SourceBox.Text.Trim(),
                Notes           = (NotesBox.Text ?? "").Trim(),
            };

            DialogResult = true;
        }
    }
}
