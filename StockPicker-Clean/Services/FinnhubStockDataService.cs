using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using StockPicker.Models;

namespace StockPicker.Services
{
    /// <summary>
    /// Fetches stock data from Finnhub (https://finnhub.io/api/v1).
    ///
    /// Rate limits:
    ///   Free tier: 60 API calls/minute
    ///
    /// Universe is always sourced from Yahoo Finance — GetUniverseAsync throws
    /// NotSupportedException so callers cannot accidentally use this source for
    /// universe discovery.
    /// </summary>
    public class FinnhubStockDataService : IStockDataService
    {
        private const string BaseUrl = "https://finnhub.io/api/v1";

        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20)
        };

        private readonly string _apiKey;

        public FinnhubStockDataService(string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new ArgumentException("Finnhub API key must not be empty.", nameof(apiKey));
            _apiKey = apiKey;
        }

        /// <inheritdoc />
        public DataSourceType SourceType => DataSourceType.Finnhub;

        /// <summary>
        /// Not supported — universe is always loaded from Yahoo Finance.
        /// </summary>
        public Task<IReadOnlyList<Stock>> GetUniverseAsync() =>
            throw new NotSupportedException(
                "FinnhubStockDataService does not supply the stock universe. " +
                "Use YahooFinanceStockDataService.GetUniverseAsync() instead.");

        /// <summary>
        /// Fetches daily OHLCV history for <paramref name="symbol"/> between
        /// <paramref name="from"/> and <paramref name="to"/> (inclusive).
        ///
        /// Endpoint: GET /stock/candle?symbol=&amp;resolution=D&amp;from=&amp;to=&amp;token=
        /// Response arrays: c (close), o (open), h (high), l (low), v (volume), t (unix timestamp).
        /// Returns an empty list if the symbol has no data or the API returns an error status.
        /// </summary>
        public async Task<IReadOnlyList<StockQuote>> GetHistoryAsync(
            string symbol, DateTime from, DateTime to)
        {
            var quotes = new List<StockQuote>();
            try
            {
                var unixFrom = new DateTimeOffset(from.Date).ToUnixTimeSeconds();
                var unixTo   = new DateTimeOffset(to.Date.AddDays(1).AddSeconds(-1)).ToUnixTimeSeconds();

                var url = $"{BaseUrl}/stock/candle" +
                          $"?symbol={Uri.EscapeDataString(symbol)}" +
                          $"&resolution=D" +
                          $"&from={unixFrom}" +
                          $"&to={unixTo}" +
                          $"&token={Uri.EscapeDataString(_apiKey)}";

                var json = await _http.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);

                // Finnhub returns {"s":"no_data"} when there is no data for the symbol/range.
                if (doc.RootElement.TryGetProperty("s", out var status) &&
                    status.GetString() == "no_data")
                {
                    Debug.WriteLine($"[Finnhub] No data for {symbol}.");
                    return quotes;
                }

                if (!doc.RootElement.TryGetProperty("t", out var timestamps)) return quotes;
                if (!doc.RootElement.TryGetProperty("o", out var opens))      return quotes;
                if (!doc.RootElement.TryGetProperty("h", out var highs))      return quotes;
                if (!doc.RootElement.TryGetProperty("l", out var lows))       return quotes;
                if (!doc.RootElement.TryGetProperty("c", out var closes))     return quotes;
                if (!doc.RootElement.TryGetProperty("v", out var volumes))    return quotes;

                int count = timestamps.GetArrayLength();
                for (int i = 0; i < count; i++)
                {
                    if (i >= closes.GetArrayLength()) break;

                    var tEl = timestamps[i];
                    var oEl = opens[i];
                    var hEl = highs[i];
                    var lEl = lows[i];
                    var cEl = closes[i];
                    var vEl = volumes[i];

                    if (tEl.ValueKind == JsonValueKind.Null ||
                        cEl.ValueKind == JsonValueKind.Null ||
                        oEl.ValueKind == JsonValueKind.Null)
                        continue;

                    var date = DateTimeOffset.FromUnixTimeSeconds(tEl.GetInt64()).UtcDateTime;

                    quotes.Add(new StockQuote
                    {
                        Symbol    = symbol,
                        Timestamp = date,
                        Open      = oEl.GetDecimal(),
                        High      = hEl.ValueKind != JsonValueKind.Null ? hEl.GetDecimal() : oEl.GetDecimal(),
                        Low       = lEl.ValueKind  != JsonValueKind.Null ? lEl.GetDecimal() : oEl.GetDecimal(),
                        Close     = cEl.GetDecimal(),
                        Volume    = vEl.ValueKind  != JsonValueKind.Null ? vEl.GetInt64()   : 0L,
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Finnhub] GetHistoryAsync({symbol}) error: {ex.Message}");
            }

            return quotes;
        }

        /// <summary>
        /// Fetches the real-time quote for a symbol using GET /quote.
        /// Response fields: c (current price), d (change), dp (change%), h, l, o, pc (prev close).
        /// Volume is not included in Finnhub's basic /quote response.
        /// </summary>
        public async Task<StockQuote?> GetLatestQuoteAsync(string symbol)
        {
            try
            {
                var url = $"{BaseUrl}/quote" +
                          $"?symbol={Uri.EscapeDataString(symbol)}" +
                          $"&token={Uri.EscapeDataString(_apiKey)}";

                var json = await _http.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);

                var c = GetDecimal(doc.RootElement, "c");
                if (c == null || c.Value == 0m) return null;

                var o  = GetDecimal(doc.RootElement, "o");
                var h  = GetDecimal(doc.RootElement, "h");
                var l  = GetDecimal(doc.RootElement, "l");

                return new StockQuote
                {
                    Symbol    = symbol,
                    Timestamp = DateTime.UtcNow,
                    Open      = o ?? c.Value,
                    High      = h ?? c.Value,
                    Low       = l ?? c.Value,
                    Close     = c.Value,
                    Volume    = 0L, // not available in /quote
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Finnhub] GetLatestQuoteAsync({symbol}) error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Fetches live quote + profile data per symbol.
        ///
        /// Per symbol: calls /quote for price data and /stock/profile2 for name and sector.
        /// Throttled to avoid exceeding the free-tier 60 req/min limit (each symbol
        /// costs 2 requests, so effective throughput is ~30 symbols/min).
        ///
        /// Populated fields: LastPrice, DayChange, DayChangePct, LongName, Sector.
        /// </summary>
        public async Task<Dictionary<string, QuoteSummary>> GetQuoteSummariesAsync(
            IEnumerable<string> symbols)
        {
            var result = new Dictionary<string, QuoteSummary>(StringComparer.OrdinalIgnoreCase);

            foreach (var sym in symbols)
            {
                try
                {
                    // Throttle: 2 calls per symbol at 60/min → wait ~1 s per symbol to be safe.
                    await Task.Delay(1000);

                    // ── Quote ────────────────────────────────────────────────────
                    var quoteUrl = $"{BaseUrl}/quote" +
                                   $"?symbol={Uri.EscapeDataString(sym)}" +
                                   $"&token={Uri.EscapeDataString(_apiKey)}";

                    var quoteJson = await _http.GetStringAsync(quoteUrl);
                    using var quoteDoc = JsonDocument.Parse(quoteJson);

                    var price     = GetDecimal(quoteDoc.RootElement, "c");
                    var change    = GetDecimal(quoteDoc.RootElement, "d");
                    var changePct = GetDouble(quoteDoc.RootElement,  "dp");

                    if (price == null || price.Value == 0m) continue;

                    var q = new QuoteSummary
                    {
                        Symbol       = sym,
                        Price        = price,
                        DayChange    = change,
                        DayChangePct = changePct,
                    };

                    // ── Profile ───────────────────────────────────────────────────
                    await Task.Delay(200); // small gap between the two calls for this symbol

                    var profileUrl = $"{BaseUrl}/stock/profile2" +
                                     $"?symbol={Uri.EscapeDataString(sym)}" +
                                     $"&token={Uri.EscapeDataString(_apiKey)}";

                    try
                    {
                        var profileJson = await _http.GetStringAsync(profileUrl);
                        using var profileDoc = JsonDocument.Parse(profileJson);

                        q.LongName = GetString(profileDoc.RootElement, "name");
                        q.Sector   = GetString(profileDoc.RootElement, "finnhubIndustry");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[Finnhub] profile2 ({sym}) error: {ex.Message}");
                        // Quote data is still valid — continue with what we have.
                    }

                    result[sym] = q;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Finnhub] GetQuoteSummariesAsync({sym}) error: {ex.Message}");
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
            return p.ValueKind == JsonValueKind.Number ? p.GetDecimal() : null;
        }

        private static double? GetDouble(JsonElement el, string key)
        {
            if (!el.TryGetProperty(key, out var p)) return null;
            return p.ValueKind == JsonValueKind.Number ? p.GetDouble() : null;
        }
    }
}
