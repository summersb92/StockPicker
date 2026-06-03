using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace StockPicker.Models
{
    /// <summary>
    /// Represents a live snapshot of a broad market index (e.g. DOW, S&amp;P 500, NASDAQ).
    /// Implements <see cref="INotifyPropertyChanged"/> so WPF bindings update in-place
    /// without needing to replace the item in the collection.
    /// </summary>
    public class MarketIndex : INotifyPropertyChanged
    {
        /// <summary>Yahoo Finance symbol, e.g. "^DJI".</summary>
        public string Symbol { get; set; } = "";

        /// <summary>Display name shown in the UI, e.g. "DOW".</summary>
        public string Name { get; set; } = "";

        private decimal? _price;
        public decimal? Price
        {
            get => _price;
            set { if (_price != value) { _price = value; OnPropertyChanged(); OnPropertyChanged(nameof(PriceDisplay)); } }
        }

        private decimal? _dayChange;
        public decimal? DayChange
        {
            get => _dayChange;
            set { if (_dayChange != value) { _dayChange = value; OnPropertyChanged(); OnPropertyChanged(nameof(ChangeDisplay)); } }
        }

        private double? _dayChangePct;
        public double? DayChangePct
        {
            get => _dayChangePct;
            set
            {
                if (_dayChangePct != value)
                {
                    _dayChangePct = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsPositive));
                    OnPropertyChanged(nameof(ChangeDisplay));
                }
            }
        }

        /// <summary>True when the index is flat or positive on the day.</summary>
        public bool IsPositive => DayChangePct.HasValue && DayChangePct.Value >= 0;

        /// <summary>Formatted current level with thousands separator.</summary>
        public string PriceDisplay =>
            Price.HasValue ? Price.Value.ToString("N2") : "—";

        /// <summary>
        /// Combined change string: "▲ +123.45 (+0.29%)" or "▼ -55.12 (-0.31%)"
        /// Returns "—" if no data is available yet.
        /// </summary>
        public string ChangeDisplay
        {
            get
            {
                if (!DayChange.HasValue || !DayChangePct.HasValue) return "—";

                var arrow = DayChange.Value >= 0 ? "▲" : "▼";
                var pts   = DayChange.Value >= 0
                    ? $"+{DayChange.Value:N2}"
                    : $"{DayChange.Value:N2}";
                var pct   = DayChangePct.Value >= 0
                    ? $"+{DayChangePct.Value:F2}%"
                    : $"{DayChangePct.Value:F2}%";

                return $"{arrow} {pts} ({pct})";
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
