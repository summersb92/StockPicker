using System;
using System.Threading.Tasks;
using System.Windows.Input;
using StockPicker.Models;

namespace StockPicker.Desktop.ViewModels
{
    /// <summary>Outcome of the last "Test" press for a source's API key.</summary>
    public enum KeyTestState
    {
        /// <summary>Never tested this session — no status shown.</summary>
        Untested,
        /// <summary>A probe request is in flight.</summary>
        Testing,
        /// <summary>The probe returned usable data.</summary>
        Valid,
        /// <summary>The key is blank, or the probe returned nothing usable.</summary>
        Invalid,
    }

    /// <summary>
    /// Represents one data source option in the Settings window.
    /// Binds to a CheckBox (IsEnabled), an API key TextBox, and informational labels.
    /// </summary>
    /// <remarks>
    /// WPF-ADAPTATION: copied verbatim from <c>StockPicker/ViewModels/DataSourceToggle.cs</c>;
    /// only the namespace changed. The <see cref="DataSourceType"/> enum and its
    /// <c>DisplayName()</c> extension live in <c>StockPicker.Core</c> (namespace
    /// <c>StockPicker.Models</c>) and are used unchanged.
    /// </remarks>
    public class DataSourceToggle : ViewModelBase
    {
        public DataSourceToggle(DataSourceType sourceType)
        {
            SourceType   = sourceType;
            DisplayName  = sourceType.DisplayName();
            RequiresApiKey = sourceType is not DataSourceType.YahooFinance
                                            and not DataSourceType.Alpaca
                                            and not DataSourceType.Stooq;

            FreeInfo = sourceType switch
            {
                DataSourceType.YahooFinance  => "No key required — unofficial API",
                DataSourceType.Stooq         => "No key required — CSV download, 10+ years history",
                DataSourceType.Alpaca        => "Uses ALPACA_API_KEY + ALPACA_API_SECRET from Windows environment variables",
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
                DataSourceType.Alpaca        => "https://alpaca.markets/",
                DataSourceType.AlphaVantage  => "https://www.alphavantage.co/support/#api-key",
                DataSourceType.Finnhub       => "https://finnhub.io/register",
                DataSourceType.Polygon       => "https://polygon.io/dashboard/signup",
                DataSourceType.Tiingo        => "https://www.tiingo.com/account/api/token",
                _                            => ""
            };

            TestKeyCommand = new RelayCommand(
                _ => _ = TestKeyAsync(),
                _ => RequiresApiKey && _keyTestState != KeyTestState.Testing);
        }

        /// <summary>The enum value this toggle represents.</summary>
        public DataSourceType SourceType { get; }

        /// <summary>Human-readable name, e.g. "Alpha Vantage".</summary>
        public string DisplayName { get; }

        /// <summary>True when this source uses the single API-key textbox in Settings.</summary>
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
            set
            {
                if (SetProperty(ref _apiKey, value))
                {
                    // A previous "Key OK"/"Invalid key" verdict applied to the old key text,
                    // so clear it rather than let a stale result sit next to a new key.
                    KeyTestState = KeyTestState.Untested;
                }
            }
        }

        // ── API-key test ──────────────────────────────────────────────────────────

        /// <summary>
        /// Probe that decides whether <see cref="ApiKey"/> actually works, injected by
        /// <see cref="MainViewModel"/> (which owns the data-source services). Returns true
        /// only when the source returned usable data.
        /// </summary>
        /// <remarks>
        /// Deliberately coarse: an expired key, a revoked key, a typo, a plan that excludes
        /// the endpoint, and an offline machine all collapse to false. The UI only claims the
        /// key "doesn't work" — it never guesses why.
        /// </remarks>
        public Func<DataSourceToggle, Task<bool>>? KeyValidator { get; set; }

        /// <summary>Runs <see cref="KeyValidator"/> and reports the verdict inline in Settings.</summary>
        public ICommand TestKeyCommand { get; }

        private KeyTestState _keyTestState = KeyTestState.Untested;
        /// <summary>Result of the most recent <see cref="TestKeyCommand"/> press.</summary>
        public KeyTestState KeyTestState
        {
            get => _keyTestState;
            private set
            {
                if (SetProperty(ref _keyTestState, value))
                {
                    OnPropertyChanged(nameof(KeyStatusText));
                    OnPropertyChanged(nameof(IsKeyInvalid));
                    ((RelayCommand)TestKeyCommand).RaiseCanExecuteChanged();
                }
            }
        }

        /// <summary>
        /// Quiet inline status next to the key box. Empty until the user presses Test,
        /// so the Settings pane looks unchanged for anyone who never uses it.
        /// </summary>
        public string KeyStatusText => _keyTestState switch
        {
            KeyTestState.Testing => "Testing…",
            KeyTestState.Valid   => "Key OK",
            KeyTestState.Invalid => "Invalid key",
            _                    => "",
        };

        /// <summary>Drives the red tint on <see cref="KeyStatusText"/>.</summary>
        public bool IsKeyInvalid => _keyTestState == KeyTestState.Invalid;

        /// <summary>
        /// Records that something outside Settings found this key unusable — currently the
        /// background fundamentals pass, which gets a 401/403 and gives up.
        ///
        /// This is why the verdict can appear without the user pressing Test: the scan already
        /// paid for the answer, so Settings may as well show it rather than make them ask again.
        /// Still silent — inline text only, never a dialog.
        /// </summary>
        public void MarkKeyInvalid() => KeyTestState = KeyTestState.Invalid;

        /// <summary>
        /// Blank keys short-circuit to Invalid without a network round-trip; everything else
        /// defers to <see cref="KeyValidator"/>. Never throws — a probe that blows up is
        /// simply a key that doesn't work.
        /// </summary>
        private async Task TestKeyAsync()
        {
            if (string.IsNullOrWhiteSpace(_apiKey) || KeyValidator is null)
            {
                KeyTestState = KeyTestState.Invalid;
                return;
            }

            KeyTestState = KeyTestState.Testing;
            bool ok;
            try
            {
                ok = await KeyValidator(this);
            }
            catch
            {
                ok = false;
            }
            KeyTestState = ok ? KeyTestState.Valid : KeyTestState.Invalid;
        }
    }
}
