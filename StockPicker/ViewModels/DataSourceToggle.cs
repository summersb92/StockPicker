using StockPicker.Models;

namespace StockPicker.ViewModels
{
    /// <summary>
    /// Represents one data source option in the Settings window.
    /// Binds to a CheckBox (IsEnabled), an API key TextBox, and informational labels.
    /// </summary>
    public class DataSourceToggle : ViewModelBase
    {
        public DataSourceToggle(DataSourceType sourceType)
        {
            SourceType   = sourceType;
            DisplayName  = sourceType.DisplayName();
            RequiresApiKey = sourceType is not DataSourceType.YahooFinance
                                            and not DataSourceType.Stooq;

            FreeInfo = sourceType switch
            {
                DataSourceType.YahooFinance  => "No key required — unofficial API",
                DataSourceType.Stooq         => "No key required — CSV download, 10+ years history",
                DataSourceType.AlphaVantage  => "25 req/day free (5 req/min) — history only at scale",
                DataSourceType.Finnhub       => "60 req/min free — good for live quotes & fundamentals",
                DataSourceType.Polygon       => "5 calls/min free (delayed) — best as history backup",
                DataSourceType.Tiingo        => "500 symbols/day free, 50/hr — best free-key option",
                _                            => ""
            };

            ApiKeyUrl = sourceType switch
            {
                DataSourceType.YahooFinance  => "",
                DataSourceType.Stooq         => "",
                DataSourceType.AlphaVantage  => "https://www.alphavantage.co/support/#api-key",
                DataSourceType.Finnhub       => "https://finnhub.io/register",
                DataSourceType.Polygon       => "https://polygon.io/dashboard/signup",
                DataSourceType.Tiingo        => "https://www.tiingo.com/account/api/token",
                _                            => ""
            };
        }

        /// <summary>The enum value this toggle represents.</summary>
        public DataSourceType SourceType { get; }

        /// <summary>Human-readable name, e.g. "Alpha Vantage".</summary>
        public string DisplayName { get; }

        /// <summary>True for every source except Yahoo Finance.</summary>
        public bool RequiresApiKey { get; }

        /// <summary>Short note about the free-tier rate limits.</summary>
        public string FreeInfo { get; }

        /// <summary>URL where the user can register for an API key (empty for Yahoo).</summary>
        public string ApiKeyUrl { get; }

        private bool _isEnabled;
        /// <summary>Whether this source participates in the next scan.</summary>
        public bool IsEnabled
        {
            get => _isEnabled;
            set => SetProperty(ref _isEnabled, value);
        }

        private string _apiKey = string.Empty;
        /// <summary>The user's API key for this source (empty for Yahoo).</summary>
        public string ApiKey
        {
            get => _apiKey;
            set => SetProperty(ref _apiKey, value);
        }
    }
}
