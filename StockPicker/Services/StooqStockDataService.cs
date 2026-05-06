using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using StockPicker.Models;

namespace StockPicker.Services
{
    /// <summary>
    /// Fetches historical OHLCV data from Stooq (https://stooq.com).
    ///
    /// No API key required. Stooq returns plain CSV files via a public download URL.
    /// Used as a backend by pandas-datareader — widely proven and stable.
    ///
    /// Limitations:
    ///   • History only — no real-time quotes, no fundamentals.
    ///   • Latest available price is the previous trading day's close.
    ///   • US equities require the ".US" suffix in the symbol (e.g. "AAPL.US").
    ///   • Soft rate limit: Stooq may block rapid sequential requests.
    ///     The app throttles to one request at a time via the outer SemaphoreSlim.
    ///   • Occasionally unavailable for non-EU IPs — falls back to empty list on error.
    /// </summary>
    public class StooqStockDataService : IStockDataService
    {
        // Base CSV download URL. Parameters:
        //   s  = symbol (e.g. "AAPL.US")
        //   d1 = start date YYYYMMDD
        //   d2 = end date YYYYMMDD
        //   i  = interval (d = daily)
        private const string BaseUrl = "https://stooq.com/q/d/l/";

        private static readonly HttpClient _http = new()
        {
            Timeout = TimeSpan.FromSeconds(20),
            DefaultRequestHeaders = { { "User-Agent", "Mozilla/5.0 (compatible; StockPicker/1.0)" } }
        };

        /// <inheritdoc />
        public DataSourceType SourceType => DataSourceType.Stooq;

        /// <summary>
        /// Not supported — universe is always loaded from Yahoo Finance.
        /// </summary>
        public Task<IReadOnlyList<Stock>> GetUniverseAsync() =>
            throw new NotSupportedException(
                "StooqStockDataService does not supply the stock universe. " +
                "Use YahooFinanceStockDataService.GetUniverseAsync() instead.");

        /// <summary>
        /// Fetches daily OHLCV bars for <paramref name="symbol"/> from Stooq's CSV endpoint.
        /// Returns an empty list on any error so the caller can fall back gracefully.
        /// </summary>
        public async Task<IReadOnlyList<StockQuote>> GetHistoryAsync(
            string symbol, DateTime from, DateTime to)
        {
            var quotes = new List<StockQuote>();
            try
            {
                // Stooq uses a ".US" suffix for US-listed equities.
                var stooqSym = symbol.Contains('.') ? symbol : $"{symbol}.US";

                var url = $"{BaseUrl}?s={Uri.EscapeDataString(stooqSym)}" +
                          $"&d1={from:yyyyMMdd}" +
                          $"&d2={to:yyyyMMdd}" +
                          $"&i=d";

                var csv = await _http.GetStringAsync(url);

                // CSV format: Date,Open,High,Low,Close,Volume
                // First line is the header; subsequent lines are data (newest-first or ascending).
                using var reader = new StringReader(csv);
                string? line;
                bool firstLine = true;

                while ((line = await Task.Run(() => reader.ReadLine())) != null)
                {
                    if (firstLine) { firstLine = false; continue; } // skip header

                    var parts = line.Split(',');
                    if (parts.Length < 5) continue;

                    if (!DateTime.TryParseExact(parts[0].Trim(), "yyyy-MM-dd",
                            CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                        continue;

                    if (!decimal.TryParse(parts[1].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var open))  continue;
                    if (!decimal.TryParse(parts[2].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var high))  continue;
                    if (!decimal.TryParse(parts[3].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var low))   continue;
                    if (!decimal.TryParse(parts[4].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var close)) continue;

                    long volume = 0;
                    if (parts.Length >= 6)
                        long.TryParse(parts[5].Trim(), out volume);

                    quotes.Add(new StockQuote
                    {
                        Symbol    = symbol,
                        Timestamp = date,
                        Open      = open,
                        High      = high,
                        Low       = low,
                        Close     = close,
                        Volume    = volume,
                    });
                }

                // Ensure ascending order for the analysis engine.
                quotes.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Stooq] GetHistoryAsync({symbol}) error: {ex.Message}");
            }

            return quotes;
        }

        /// <summary>
        /// Returns the most recent bar from the history as a proxy for the latest quote.
        /// Stooq does not offer a real-time or intraday quote endpoint.
        /// </summary>
        public async Task<StockQuote?> GetLatestQuoteAsync(string symbol)
        {
            var history = await GetHistoryAsync(symbol, DateTime.Today.AddDays(-7), DateTime.Today);
            return history.Count > 0 ? history[history.Count - 1] : null;
        }

        /// <summary>
        /// Fetches the most recent close price per symbol via GetLatestQuoteAsync.
        /// Only <see cref="QuoteSummary.Price"/> and <see cref="QuoteSummary.Volume"/> are
        /// populated — Stooq does not provide fundamental or valuation data.
        /// </summary>
        public async Task<Dictionary<string, QuoteSummary>> GetQuoteSummariesAsync(
            IEnumerable<string> symbols)
        {
            var result = new Dictionary<string, QuoteSummary>(StringComparer.OrdinalIgnoreCase);

            foreach (var sym in symbols)
            {
                try
                {
                    var latest = await GetLatestQuoteAsync(sym);
                    if (latest == null) continue;

                    result[sym] = new QuoteSummary
                    {
                        Symbol = sym,
                        Price  = latest.Close,
                        Volume = latest.Volume,
                        // Stooq has no fundamentals endpoint — leave all other fields null.
                    };
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Stooq] GetQuoteSummariesAsync({sym}) error: {ex.Message}");
                }
            }

            return result;
        }
    }
}
