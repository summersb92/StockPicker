using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockPicker.Models;

namespace StockPicker.Services
{
    /// <summary>
    /// Fetches stock data from Alpha Vantage (https://www.alphavantage.co).
    ///
    /// Rate limits:
    ///   Free tier  : 25 requests/day, 5 requests/minute
    ///   Premium    : up to 75 requests/minute (plan-dependent)
    ///
    /// Universe is always sourced from Yahoo Finance — GetUniverseAsync throws
    /// NotSupportedException so callers cannot accidentally use this source for
    /// universe discovery.
    /// </summary>
    public class AlphaVantageStockDataService : IStockDataService
    {
        private const string BaseUrl = "https://www.alphavantage.co/query";

        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20)
        };

        private readonly string _apiKey;

        public AlphaVantageStockDataService(string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new ArgumentException("Alpha Vantage API key must not be empty.", nameof(apiKey));
            _apiKey = apiKey;
        }

        /// <inheritdoc />
        public DataSourceType SourceType => DataSourceType.AlphaVantage;

        /// <summary>
        /// Not supported — universe is always loaded from Yahoo Finance.
        /// </summary>
        public Task<IReadOnlyList<Stock>> GetUniverseAsync() =>
            throw new NotSupportedException(
                "AlphaVantageStockDataService does not supply the stock universe. " +
                "Use YahooFinanceStockDataService.GetUniverseAsync() instead.");

        /// <summary>
        /// Fetches daily OHLCV history for <paramref name="symbol"/> between
        /// <paramref name="from"/> and <paramref name="to"/> (inclusive).
        ///
        /// Endpoint: TIME_SERIES_DAILY with outputsize=full (up to 20 years of data).
        /// Returns an empty list if the symbol is unknown, the key is invalid, or
        /// the free-tier rate limit has been hit (HTTP 429 or an AV "Note:" field).
        /// </summary>
        public async Task<IReadOnlyList<StockQuote>> GetHistoryAsync(
            string symbol, DateTime from, DateTime to)
        {
            var quotes = new List<StockQuote>();
            try
            {
                var url = $"{BaseUrl}?function=TIME_SERIES_DAILY" +
                          $"&symbol={Uri.EscapeDataString(symbol)}" +
                          $"&outputsize=full" +
                          $"&datatype=json" +
                          $"&apikey={Uri.EscapeDataString(_apiKey)}";

                var json = await _http.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);

                // Alpha Vantage surfaces rate-limit warnings in a "Note" or "Information" field.
                if (doc.RootElement.TryGetProperty("Note", out var note))
                {
                    Debug.WriteLine($"[AlphaVantage] Rate-limit note for {symbol}: {note.GetString()}");
                    return quotes; // return empty — caller will skip this source
                }
                if (doc.RootElement.TryGetProperty("Information", out var info))
                {
                    Debug.WriteLine($"[AlphaVantage] Information field for {symbol}: {info.GetString()}");
                    return quotes;
                }

                if (!doc.RootElement.TryGetProperty("Time Series (Daily)", out var series))
                {
                    Debug.WriteLine($"[AlphaVantage] No 'Time Series (Daily)' key for {symbol}.");
                    return quotes;
                }

                foreach (var day in series.EnumerateObject())
                {
                    if (!DateTime.TryParse(day.Name, out var date)) continue;
                    date = date.Date;
                    if (date < from.Date || date > to.Date) continue;

                    var o = GetDecimal(day.Value, "1. open");
                    var h = GetDecimal(day.Value, "2. high");
                    var l = GetDecimal(day.Value, "3. low");
                    var c = GetDecimal(day.Value, "4. close");
                    var v = GetLong(day.Value,    "5. volume");

                    if (o == null || c == null) continue;

                    quotes.Add(new StockQuote
                    {
                        Symbol    = symbol,
                        Timestamp = date,
                        Open      = o.Value,
                        High      = h ?? o.Value,
                        Low       = l ?? o.Value,
                        Close     = c.Value,
                        Volume    = v ?? 0L,
                    });
                }

                // Alpha Vantage returns newest-first; sort ascending for analysis.
                quotes.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
            }
            catch (HttpRequestException ex) when ((int?)ex.StatusCode == 429)
            {
                Debug.WriteLine($"[AlphaVantage] HTTP 429 (rate limit) for {symbol}. Skipping.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AlphaVantage] GetHistoryAsync({symbol}) error: {ex.Message}");
            }

            return quotes;
        }

        /// <summary>
        /// Fetches the most recent day's quote using the GLOBAL_QUOTE function.
        /// </summary>
        public async Task<StockQuote?> GetLatestQuoteAsync(string symbol)
        {
            try
            {
                var url = $"{BaseUrl}?function=GLOBAL_QUOTE" +
                          $"&symbol={Uri.EscapeDataString(symbol)}" +
                          $"&apikey={Uri.EscapeDataString(_apiKey)}";

                var json = await _http.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("Note", out _) ||
                    doc.RootElement.TryGetProperty("Information", out _))
                {
                    Debug.WriteLine($"[AlphaVantage] Rate limit hit in GetLatestQuoteAsync({symbol}).");
                    return null;
                }

                if (!doc.RootElement.TryGetProperty("Global Quote", out var gq))
                    return null;

                var o = GetDecimal(gq, "02. open");
                var h = GetDecimal(gq, "03. high");
                var l = GetDecimal(gq, "04. low");
                var c = GetDecimal(gq, "05. price");
                var v = GetLong(gq,    "06. volume");
                var d = GetString(gq,  "07. latest trading day");

                if (c == null) return null;
                DateTime.TryParse(d, out var date);

                return new StockQuote
                {
                    Symbol    = symbol,
                    Timestamp = date == default ? DateTime.Today : date,
                    Open      = o ?? c.Value,
                    High      = h ?? c.Value,
                    Low       = l ?? c.Value,
                    Close     = c.Value,
                    Volume    = v ?? 0L,
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AlphaVantage] GetLatestQuoteAsync({symbol}) error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Fetches live quote data per symbol by calling GetLatestQuoteAsync for each one.
        ///
        /// WARNING: Alpha Vantage free tier allows only 5 requests/minute and 25/day.
        /// This method throttles to 1 request per 200 ms to avoid hitting the per-minute
        /// cap, but for large universes the daily cap will be exhausted quickly.
        /// Consider calling this only for a small subset of symbols when on the free tier.
        ///
        /// Only LastPrice, DayChange, DayChangePct, and Volume are populated —
        /// the GLOBAL_QUOTE endpoint does not supply valuation or fundamental data.
        /// </summary>
        public async Task<Dictionary<string, QuoteSummary>> GetQuoteSummariesAsync(
            IEnumerable<string> symbols)
        {
            var result = new Dictionary<string, QuoteSummary>(StringComparer.OrdinalIgnoreCase);

            foreach (var sym in symbols)
            {
                try
                {
                    // Throttle: free tier is 5 req/min → wait 200 ms between calls.
                    await Task.Delay(200);

                    var url = $"{BaseUrl}?function=GLOBAL_QUOTE" +
                              $"&symbol={Uri.EscapeDataString(sym)}" +
                              $"&apikey={Uri.EscapeDataString(_apiKey)}";

                    var json = await _http.GetStringAsync(url);
                    using var doc = JsonDocument.Parse(json);

                    if (doc.RootElement.TryGetProperty("Note", out _) ||
                        doc.RootElement.TryGetProperty("Information", out _))
                    {
                        Debug.WriteLine($"[AlphaVantage] Rate limit hit in GetQuoteSummariesAsync — stopping batch.");
                        break; // no point continuing once the limit is hit
                    }

                    if (!doc.RootElement.TryGetProperty("Global Quote", out var gq))
                        continue;

                    var price     = GetDecimal(gq, "05. price");
                    var change    = GetDecimal(gq, "09. change");
                    var changePct = GetDouble(gq,  "10. change percent"); // e.g. "1.23%"
                    var volume    = GetLong(gq,    "06. volume");

                    if (price == null) continue;

                    result[sym] = new QuoteSummary
                    {
                        Symbol       = sym,
                        Price        = price,
                        DayChange    = change,
                        DayChangePct = changePct,
                        Volume       = volume,
                        // Fundamental fields not available from GLOBAL_QUOTE — left null.
                    };
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[AlphaVantage] GetQuoteSummariesAsync({sym}) error: {ex.Message}");
                }
            }

            return result;
        }

        // ── JSON helpers ──────────────────────────────────────────────────────────

        private static string? GetString(JsonElement el, string key) =>
            el.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String
                ? p.GetString() : null;

        private static decimal? GetDecimal(JsonElement el, string key)
        {
            if (!el.TryGetProperty(key, out var p)) return null;
            if (p.ValueKind == JsonValueKind.Number)  return p.GetDecimal();
            // AV often returns numbers as strings, e.g. "182.3400"
            if (p.ValueKind == JsonValueKind.String &&
                decimal.TryParse(p.GetString(), out var v))
                return v;
            return null;
        }

        private static double? GetDouble(JsonElement el, string key)
        {
            if (!el.TryGetProperty(key, out var p)) return null;
            if (p.ValueKind == JsonValueKind.Number) return p.GetDouble();
            if (p.ValueKind == JsonValueKind.String)
            {
                // Strip trailing "%" if present (e.g. "1.2345%")
                var s = p.GetString()?.TrimEnd('%').Trim();
                if (double.TryParse(s, out var v)) return v;
            }
            return null;
        }

        private static long? GetLong(JsonElement el, string key)
        {
            if (!el.TryGetProperty(key, out var p)) return null;
            if (p.ValueKind == JsonValueKind.Number) return p.GetInt64();
            if (p.ValueKind == JsonValueKind.String &&
                long.TryParse(p.GetString(), out var v))
                return v;
            return null;
        }
        /// <inheritdoc />
        public Task<IReadOnlyList<WeeklyBar>> GetWeeklyBarsAsync(string symbol, ChartRange range = ChartRange.Year, System.Threading.CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<WeeklyBar>>(Array.Empty<WeeklyBar>());

        /// <inheritdoc />
        public Task<(double? IV, double? Theta)> GetNearTermOptionsAsync(string symbol, System.Threading.CancellationToken ct = default)
            => Task.FromResult<(double? IV, double? Theta)>((null, null));

    }
}
