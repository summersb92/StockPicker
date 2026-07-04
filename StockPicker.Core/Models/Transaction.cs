using System;
using System.Text.Json.Serialization;

namespace StockPicker.Models
{
    /// <summary>What kind of ledger entry a <see cref="Transaction"/> records.</summary>
    public enum TransactionType
    {
        /// <summary>Opened/added a position.</summary>
        Buy,
        /// <summary>Closed/sold a position — proceeds credited to cash.</summary>
        Sell,
        /// <summary>Cash injected into the account.</summary>
        Deposit,
        /// <summary>Cash removed from the account.</summary>
        Withdrawal,
    }

    /// <summary>
    /// One immutable entry in the portfolio ledger. Buys/sells carry share + price detail;
    /// deposits/withdrawals are pure cash moves. <see cref="CashDelta"/> is the signed effect
    /// on the cash balance (sell/deposit positive, withdrawal negative, buy zero — buying a
    /// position is recorded for history but does not auto-debit the tracked cash balance).
    /// </summary>
    public class Transaction
    {
        public DateTime        Date         { get; set; } = DateTime.Today;
        public TransactionType Type         { get; set; }

        /// <summary>Ticker for Buy/Sell; empty for cash transactions.</summary>
        public string  Symbol      { get; set; } = string.Empty;
        public string  CompanyName { get; set; } = string.Empty;

        /// <summary>Shares traded (Buy/Sell); 0 for cash transactions.</summary>
        public int     Shares      { get; set; }

        /// <summary>Per-share price (Buy/Sell); 0 for cash transactions.</summary>
        public decimal Price       { get; set; }

        /// <summary>Signed effect on the cash balance.</summary>
        public decimal CashDelta   { get; set; }

        /// <summary>Realized gain/loss on a Sell (proceeds net of cost basis and any margin interest); null otherwise.</summary>
        public decimal? RealizedGain { get; set; }

        /// <summary>True when the closed position had been bought on margin.</summary>
        public bool    OnMargin    { get; set; }

        /// <summary>Free-form note (e.g. reason for a deposit/withdrawal).</summary>
        public string  Note        { get; set; } = string.Empty;

        // ── Display helpers ───────────────────────────────────────────────────

        /// <summary>Gross trade value (shares × price) for Buy/Sell; 0 for cash moves.</summary>
        [JsonIgnore]
        public decimal GrossAmount => Price * Shares;

        [JsonIgnore] public string DateDisplay => $"{Date:MMM d, yyyy}";

        [JsonIgnore]
        public string TypeDisplay => Type switch
        {
            TransactionType.Withdrawal => "Withdrawal",
            _                          => Type.ToString(),
        };

        /// <summary>"100 @ $150.00" for trades; the note for cash moves.</summary>
        [JsonIgnore]
        public string DetailDisplay =>
            Type is TransactionType.Buy or TransactionType.Sell
                ? $"{Shares} @ ${Price:F2}{(OnMargin ? " (margin)" : "")}"
                : Note;

        [JsonIgnore]
        public string GrossDisplay =>
            Type is TransactionType.Buy or TransactionType.Sell ? $"${GrossAmount:N2}" : "";

        /// <summary>Signed cash effect, e.g. "+$1,500.00" / "-$500.00" / "" when zero.</summary>
        [JsonIgnore]
        public string CashDeltaDisplay =>
            CashDelta == 0m ? ""
            : (CashDelta > 0m ? $"+${CashDelta:N2}" : $"-${Math.Abs(CashDelta):N2}");

        [JsonIgnore]
        public string RealizedGainDisplay =>
            RealizedGain.HasValue
                ? (RealizedGain.Value >= 0 ? $"+${RealizedGain.Value:N2}" : $"-${Math.Abs(RealizedGain.Value):N2}")
                : "";
    }
}
