using System;
using System.Windows;
using StockPicker.Models;

namespace StockPicker.Views
{
    /// <summary>
    /// Modal dialog to sell (close out) a held position. Captures a sell price and date,
    /// previews the net cash proceeds (gross less any repaid margin loan + interest), and
    /// exposes them via <see cref="SellPrice"/> / <see cref="SellDate"/> on Save.
    /// </summary>
    public partial class SellPositionWindow : Window
    {
        private readonly HeldPosition _position;

        public bool     Confirmed { get; private set; }
        public decimal  SellPrice { get; private set; }
        public DateTime SellDate  { get; private set; }

        public SellPositionWindow(HeldPosition position)
        {
            InitializeComponent();
            _position = position;

            SymbolText.Text = $"{position.Symbol}  {position.CompanyName}".Trim();
            SharesText.Text = $"{position.ShareCount} @ entry ${position.EntryPrice:F2}"
                            + (position.BoughtOnMargin ? $"  ·  {position.Leverage:0.#}× margin" : "");

            var price = position.LastPrice ?? position.EntryPrice;
            PriceBox.Text = price > 0 ? price.ToString("0.####") : "";
            SellDatePicker.SelectedDate = DateTime.Today;

            UpdateProceeds();
            Loaded += (_, _) => { PriceBox.Focus(); PriceBox.SelectAll(); };
        }

        private void PriceBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
            => UpdateProceeds();

        private void UpdateProceeds()
        {
            if (ProceedsText == null) return;
            if (!decimal.TryParse(PriceBox.Text, out var price) || price < 0)
            {
                ProceedsText.Text = "—";
                return;
            }

            decimal gross    = price * _position.ShareCount;
            decimal net      = gross - _position.BorrowedAmount - _position.InterestAccrued;
            decimal realized = net - _position.EquityInvested;

            if (_position.BoughtOnMargin)
                ProceedsText.Text =
                    $"${net:N2}  (gross ${gross:N2} − loan ${_position.BorrowedAmount:N2} " +
                    $"− interest ${_position.InterestAccrued:N2});  realized {(realized >= 0 ? "+" : "-")}${Math.Abs(realized):N2}";
            else
                ProceedsText.Text = $"${net:N2}  (realized {(realized >= 0 ? "+" : "-")}${Math.Abs(realized):N2})";
        }

        private void Sell_Click(object sender, RoutedEventArgs e)
        {
            if (!decimal.TryParse(PriceBox.Text, out var price) || price <= 0)
            {
                MessageBox.Show(this, "Enter a valid sell price greater than zero.",
                    "Invalid price", MessageBoxButton.OK, MessageBoxImage.Warning);
                PriceBox.Focus();
                return;
            }

            SellPrice = price;
            SellDate  = SellDatePicker.SelectedDate ?? DateTime.Today;
            Confirmed = true;
            DialogResult = true;
        }
    }
}
