using System;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using StockPicker.Models;

namespace StockPicker.Desktop.Views;

/// <summary>
/// Modal dialog to sell (close out) a held position. Captures a sell price and date,
/// previews the net cash proceeds (gross less any repaid margin loan + interest).
/// </summary>
/// <remarks>
/// WPF-ADAPTATION result contract: opened with
/// <c>await ShowDialog&lt;SellPositionWindow.SellResult?&gt;(owner)</c> — returns a
/// <see cref="SellResult"/> (sell price + date) on Sell, or <c>null</c> on Cancel
/// (replaces the WPF <c>DialogResult</c> + public <c>Confirmed</c>/<c>SellPrice</c>/
/// <c>SellDate</c> properties).
/// <list type="bullet">
///   <item>The single <c>MessageBox.Show</c> price-validation prompt becomes the inline
///   <c>ErrorText</c> TextBlock.</item>
///   <item>WPF <c>DatePicker.SelectedDate</c> (<c>DateTime?</c>) → Avalonia
///   <c>DateTimeOffset?</c>, converted at the boundary.</item>
///   <item>The live proceeds preview (<c>PriceBox_TextChanged</c>) ports unchanged; the
///   WPF <c>TextChangedEventArgs</c> type is Avalonia's.</item>
/// </list>
/// </remarks>
public partial class SellPositionWindow : Window
{
    /// <summary>Confirmed sell result: price per share and the sell date.</summary>
    public record SellResult(decimal SellPrice, DateTime SellDate);

    private readonly HeldPosition _position;

    public SellPositionWindow() : this(new HeldPosition()) { }

    public SellPositionWindow(HeldPosition position)
    {
        InitializeComponent();
        _position = position;

        SymbolText.Text = $"{position.Symbol}  {position.CompanyName}".Trim();
        SharesText.Text = $"{position.ShareCount} @ entry ${position.EntryPrice:F2}"
                        + (position.BoughtOnMargin ? $"  ·  {position.Leverage:0.#}× margin" : "");

        var price = position.LastPrice ?? position.EntryPrice;
        PriceBox.Text = price > 0 ? price.ToString("0.####") : "";
        SellDatePicker.SelectedDate = new DateTimeOffset(DateTime.SpecifyKind(DateTime.Today, DateTimeKind.Unspecified), TimeSpan.Zero);

        UpdateProceeds();
        Loaded += (_, _) => { PriceBox.Focus(); PriceBox.SelectAll(); };
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void PriceBox_TextChanged(object? sender, TextChangedEventArgs e) => UpdateProceeds();

    private void UpdateProceeds()
    {
        if (ProceedsText == null) return;
        if (!decimal.TryParse(PriceBox.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var price) || price < 0)
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

    private void Sell_Click(object? sender, RoutedEventArgs e)
    {
        if (!decimal.TryParse(PriceBox.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var price) || price <= 0)
        {
            ErrorText.Text = "Enter a valid sell price greater than zero.";
            ErrorText.IsVisible = true;
            PriceBox.Focus();
            return;
        }

        var sellDate = SellDatePicker.SelectedDate?.DateTime ?? DateTime.Today;
        Close(new SellResult(price, sellDate));
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);
}
