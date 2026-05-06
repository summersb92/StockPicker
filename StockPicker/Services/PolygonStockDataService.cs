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
    /// Fetches stock data from Polygon.io (https://api.polygon.io).
    ///
    /// Rate limits:
    ///   Free tier: 5 API calls/minute (data is 15-minute delayed on free plans)
    ///   Paid tiers: up to unlimited calls with real-time data
    ///
    /// Universe is always sourced from Yahoo Finance — GetUniverseAsync throws
    /// NotSupportedException so callers cannot accidentally use this source for
    /// universe discovery.
    /// </summary>
    public class PolygonStockDataService : IStockDataService
    {
        private const string BaseUrl = "https://api.polygon.io";

        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20)
        };

        private readonly string _apiKey;

        public PolygonStockDataService(string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new ArgumentException("Polygon API key must not be empty.", nameof(apiKey));
            _apiKey = apiKey;
        }

        /// <inheritdoc />
        public DataSourceType SourceType => DataSourceType.Polygon;

        /// <summary>
        /// Not supported — universe is always loaded from Yahoo Finance.
        /// </summary>
        public Task<IReadOnlyList<Stock>> GetUniverseAsync() =>
            throw new NotSupportedException(
                "PolygonStockDataService does not supply the stock universe. " +
                "Use YahooFinanceStockDataService.GetUniverseAsync() instead.");

        /// <summary>
        /// Fetches adjusted daily OHLCV history for <paramref name="symbol"/> between
        /// <paramref name="from"/> and <paramref name="to"/> (inclusive).
        ///
        /// Endpoint: GET /v2/aggs/ticker/{symbol}/range/1/day/{from}/{to}
        /// Results are sorted ascending by timestamp (t, unix milliseconds).
        /// Returns an empty list if the symbol is unknown or the rate limit is hit.
        /// </summary>
        public async Task<IReadOnlyList<StockQuote>> GetHistoryAsync(
            string symbol, DateTime from, DateTime to)
        {
            var quotes = new List<StockQuote>();
            try
            {
                var fromStr = from.Date.ToString("yyyy-MM-dd");
                var toStr   = to.Date.ToString("yyyy-MM-dd");

                var url = $"{BaseUrl}/v2/aggs/ticker/{Uri.EscapeDataString(symbol)}" +
                          $"/range/1/day/{fromStr}/{toStr}" +
                          $"?adjusted=true&sort=asc&apiKey={Uri.EscapeDataString(_apiKey)}";

                var json = await _http.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);

                // Polygon returns {"status":"ERROR","error":"..."} on bad requests.
                if (doc.RootElement.TryGetProperty("status", out var statusEl))
                {
                    var statusStr = statusEl.GetString();
                    if (statusStr == "ERROR" || statusStr == "NOT_FOUND")
                    {
                        var errMsg = doc.RootElement.TryGetProperty("error", out var err)
                            ? err.GetString() : "unknown error";
                        Debug.WriteLine($"[Polygon] GetHistoryAsync({symbol}) status={statusStr}: {errMsg}");
                        return quotes;
                    }
                }

                if (!doc.RootElement.TryGetProperty("results", out var results) ||
                    results.ValueKind != JsonValueKind.Array)
                    return quotes;

                foreach (var bar in results.EnumerateArray())
                {
                    var tMs = GetLong(bar, "t");
                    var o   = GetDecimal(bar, "o");
                    var h   = GetDecimal(bar, "h");
                    var l   = GetDecimal(bar, "l");
                    var c   = GetDecimal(bar, "c");
                    var v   = GetLong(bar,    "v");

                    if (tMs == null || c == null || o == null) continue;

                    var date = DateTimeOffset.FromUnixTimeMilliseconds(tMs.Value).UtcDateTime;

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
            }
            catch (HttpRequestException ex) when ((int?)ex.StatusCode == 429)
            {
                Debug.WriteLine($"[Polygon] HTTP 429 (rate limit) for {symbol}. Skipping.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Polygon] GetHistoryAsync({symbol}) error: {ex.Message}");
            }

            return quotes;
        }

        /// <summary>
        /// Fetches the previous trading day's aggregate bar for a symbol using
        /// GET /v2/aggs/ticker/{symbol}/prev.
        /// </summary>
        public async Task<StockQuote?> GetLatestQuoteAsync(string symbol)
        {
            try
            {
                var url = $"{BaseUrl}/v2/aggs/ticker/{Uri.EscapeDataString(symbol)}/prev" +
                          $"?adjusted=true&apiKey={Uri.EscapeDataString(_apiKey)}";

                var json = await _http.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("results", out var results) ||
                    results.ValueKind != JsonValueKind.Array ||
                    results.GetArrayLength() == 0)
                    return null;

                var bar = results[0];
                var tMs = GetLong(bar,    "t");
                var o   = GetDecimal(bar, "o");
                var h   = GetDecimal(bar, "h");
                var l   = GetDecimal(bar, "l");
                var c   = GetDecimal(bar, "c");
                var v   = GetLong(bar,    "v");

                if (c == null || o == null) return null;

                var date = tMs.HasValue
                    ? DateTimeOffset.FromUnixTimeMilliseconds(tMs.Value).UtcDateTime
                    : DateTime.Today;

                return new StockQuote
                {
                    Symbol    = symbol,
                    Timestamp = date,
                    Open      = o.Value,
                    High      = h ?? o.Value,
                    Low       = l ?? o.Value,
                    Close     = c.Value,
                    Volume    = v ?? 0L,
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Polygon] GetLatestQuoteAsync({symbol}) error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Fetches previous-day aggregate bars per symbol (one call each, throttled).
        ///
        /// Rate limit note: Polygon free tier allows 5 calls/minute. This method
        /// throttles to 1 request every 12 seconds to stay within that cap.
        /// For large universes this will be very slow — consider enabling Polygon
        /// only as a supplemental history source, not for quote summaries.
        ///
        /// Populated fields: LastPrice, Volume.
        /// If a ticker reference call succeeds, LongName is also populated.
        /// </summary>
        public async Task<Dictionary<string, QuoteSummary>> GetQuoteSummariesAsync(
            IEnumerable<string> symbols)
        {
            var result = new Dictionary<string, QuoteSummary>(StringComparer.OrdinalIgnoreCase);

            foreach (var sym in symbols)
            {
                try
                {
                    // Throttle: free tier is 5 req/min → wait ~12 s between calls.
                    // This is intentionally conservative to avoid burning the daily allowance.
                    await Task.Delay(12_000);

                    var url = $"{BaseUrl}/v2/aggs/ticker/{Uri.EscapeDataString(sym)}/prev" +
                              $"?adjusted=true&apiKey={Uri.EscapeDataString(_apiKey)}";

                    var json = await _http.GetStringAsync(url);
                    using var doc = JsonDocument.Parse(json);

                    if (!doc.RootElement.TryGetProperty("results", out var results) ||
                        results.ValueKind != JsonValueKind.Array ||
                        results.GetArrayLength() == 0)
                        continue;

                    var bar   = results[0];
                    var price = GetDecimal(bar, "c");
                    var vol   = GetLong(bar,    "v");

                    if (price == null) continue;

                    var q = new QuoteSummary
                    {
                        Symbol = sym,
                        Price  = price,
                        Volume = vol,
                    };

                    // Optionally fetch the ticker reference for LongName.
                    // Only attempt this if we haven't exhausted the rate limit.
                    try
                    {
                        await Task.Delay(12_000);

                        var refUrl = $"{BaseUrl}/v3/reference/tickers/{Uri.EscapeDataString(sym)}" +
                                     $"?apiKey={Uri.EscapeDataString(_apiKey)}";

                        var refJson = await _http.GetStringAsync(refUrl);
                        using var refDoc = JsonDocument.Parse(refJson);

                        if (refDoc.RootElement.TryGetProperty("results", out var refResults) &&
                            refResults.ValueKind == JsonValueKind.Object)
                        {
                            q.LongName = GetString(refResults, "name");
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[Polygon] ticker reference ({sym}) error: {ex.Message}");
                        // Continue — price data is still valid.
                    }

                    result[sym] = q;
                }
                catch (HttpRequestException ex) when ((int?)ex.StatusCode == 429)
                {
                    Debug.WriteLine($"[Polygon] HTTP 429 in GetQuoteSummariesAsync — stopping batch.");
                    break;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Polygon] GetQuoteSummariesAsync({sym}) error: {ex.Message}");
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

        private static long? GetLong(JsonElement el, string key)
        {
            if (!el.TryGetProperty(key, out var p)) return null;
            return p.ValueKind == JsonValueKind.Number ? p.GetInt64() : null;
        }
    }
}
