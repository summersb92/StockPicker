using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockPicker.Models;

namespace StockPicker.Services
{
    /// <summary>
    /// Fetches stock data from Tiingo (https://www.tiingo.com).
    ///
    /// Free tier: 500 unique symbols/day, 50 requests/hour, 1 request/second.
    /// Real-time quotes are delivered via the IEX feed.
    /// Get a free API token at: https://www.tiingo.com/account/api/token
    ///
    /// Endpoints used:
    ///   History : GET /tiingo/daily/{symbol}/prices?startDate=…&endDate=…&token=…
    ///   Latest  : GET /iex/{symbol}?token=…  (real-time via IEX)
    ///   Batch   : GET /iex?tickers={sym1,sym2,…}&token=…  (up to ~100 per call)
    ///   Meta    : GET /tiingo/daily/{symbol}?token=…  (name, exchange)
    /// </summary>
    public class TiingoStockDataService : IStockDataService
    {
        private const string BaseUrl = "https://api.tiingo.com";

        // Tiingo recommends reusing a single HttpClient per token.
        // The token is sent as a Bearer header on every request.
        private readonly HttpClient _http;

        // Throttle to ≤1 req/second to stay within the free-tier guideline.
        private readonly SemaphoreSlim _throttle = new(1, 1);

        public TiingoStockDataService(string apiToken)
        {
            if (string.IsNullOrWhiteSpace(apiToken))
                throw new ArgumentException("Tiingo API token must not be empty.", nameof(apiToken));

            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Token", apiToken);
            _http.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));
        }

        /// <inheritdoc />
        public DataSourceType SourceType => DataSourceType.Tiingo;

        /// <summary>
        /// Not supported — universe is always loaded from Yahoo Finance.
        /// </summary>
        public Task<IReadOnlyList<Stock>> GetUniverseAsync() =>
            throw new NotSupportedException(
                "TiingoStockDataService does not supply the stock universe. " +
                "Use YahooFinanceStockDataService.GetUniverseAsync() instead.");

        // ── Throttled HTTP helper ─────────────────────────────────────────────────

        private async Task<string> GetThrottledAsync(string url)
        {
            await _throttle.WaitAsync();
            try
            {
                // Tiingo free tier: 1 req/sec sustained.
                var responseTask = _http.GetStringAsync(url);
                await Task.Delay(1_100); // ensure ≥1.1 s between calls before releasing
                return await responseTask;
            }
            finally
            {
                _throttle.Release();
            }
        }

        // ── History ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Fetches adjusted daily OHLCV bars from Tiingo's EOD price endpoint.
        /// Returns split/dividend-adjusted values (adjClose, adjOpen, adjHigh, adjLow).
        /// Falls back to unadjusted values when adjusted fields are absent.
        /// </summary>
        public async Task<IReadOnlyList<StockQuote>> GetHistoryAsync(
            string symbol, DateTime from, DateTime to)
        {
            var quotes = new List<StockQuote>();
            try
            {
                var url = $"{BaseUrl}/tiingo/daily/{Uri.EscapeDataString(symbol)}/prices" +
                          $"?startDate={from:yyyy-MM-dd}" +
                          $"&endDate={to:yyyy-MM-dd}" +
                          $"&resampleFreq=daily";

                var json = await GetThrottledAsync(url);
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.ValueKind != JsonValueKind.Array) return quotes;

                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    if (!item.TryGetProperty("date", out var dateProp)) continue;
                    if (!DateTime.TryParse(dateProp.GetString(), out var date)) continue;

                    // Prefer adjusted prices when available; fall back to raw.
                    var open  = GetDecimal(item, "adjOpen")  ?? GetDecimal(item, "open");
                    var high  = GetDecimal(item, "adjHigh")  ?? GetDecimal(item, "high");
                    var low   = GetDecimal(item, "adjLow")   ?? GetDecimal(item, "low");
                    var close = GetDecimal(item, "adjClose") ?? GetDecimal(item, "close");

                    if (open == null || close == null) continue;

                    var volume = item.TryGetProperty("volume", out var vProp) &&
                                 vProp.ValueKind == JsonValueKind.Number
                                 ? vProp.GetInt64() : 0L;

                    quotes.Add(new StockQuote
                    {
                        Symbol    = symbol,
                        Timestamp = date.Date,
                        Open      = open.Value,
                        High      = high ?? open.Value,
                        Low       = low  ?? open.Value,
                        Close     = close.Value,
                        Volume    = volume,
                    });
                }

                // Tiingo returns ascending order, but sort anyway for safety.
                quotes.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Tiingo] GetHistoryAsync({symbol}) error: {ex.Message}");
            }

            return quotes;
        }

        // ── Latest quote ──────────────────────────────────────────────────────────

        /// <summary>
        /// Returns the real-time IEX quote for a single symbol.
        /// Fields available: last (price), prevClose, open, high, low, timestamp.
        /// </summary>
        public async Task<StockQuote?> GetLatestQuoteAsync(string symbol)
        {
            try
            {
                var url  = $"{BaseUrl}/iex/{Uri.EscapeDataString(symbol)}";
                var json = await GetThrottledAsync(url);
                using var doc = JsonDocument.Parse(json);

                // /iex/{symbol} returns a JSON array with one element.
                JsonElement item;
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    if (doc.RootElement.GetArrayLength() == 0) return null;
                    item = doc.RootElement[0];
                }
                else
                {
                    item = doc.RootElement;
                }

                var price = GetDecimal(item, "last") ?? GetDecimal(item, "tngoLast");
                if (price == null) return null;

                DateTime.TryParse(
                    item.TryGetProperty("timestamp", out var ts) ? ts.GetString() : null,
                    out var date);

                return new StockQuote
                {
                    Symbol    = symbol,
                    Timestamp = date == default ? DateTime.Today : date.Date,
                    Open      = GetDecimal(item, "open")  ?? price.Value,
                    High      = GetDecimal(item, "high")  ?? price.Value,
                    Low       = GetDecimal(item, "low")   ?? price.Value,
                    Close     = price.Value,
                    Volume    = 0L, // IEX tick quote does not include cumulative volume
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Tiingo] GetLatestQuoteAsync({symbol}) error: {ex.Message}");
                return null;
            }
        }

        // ── Batch quote summaries ─────────────────────────────────────────────────

        /// <summary>
        /// Batch-fetches live IEX quotes for up to 100 symbols per request.
        /// Populates: LastPrice, DayChange, DayChangePct, Volume, LongName.
        /// Fundamental data (P/E, market cap, etc.) requires a separate /fundamentals
        /// endpoint not available on the free tier.
        /// </summary>
        public async Task<Dictionary<string, QuoteSummary>> GetQuoteSummariesAsync(
            IEnumerable<string> symbols)
        {
            var result = new Dictionary<string, QuoteSummary>(StringComparer.OrdinalIgnoreCase);
            var batch  = new List<string>(100);

            async Task FlushBatch()
            {
                if (batch.Count == 0) return;
                try
                {
                    var tickers = string.Join(",", batch);
                    var url     = $"{BaseUrl}/iex?tickers={Uri.EscapeDataString(tickers)}";
                    var json    = await GetThrottledAsync(url);
                    using var doc = JsonDocument.Parse(json);

                    if (doc.RootElement.ValueKind != JsonValueKind.Array) return;

                    foreach (var item in doc.RootElement.EnumerateArray())
                    {
                        if (!item.TryGetProperty("ticker", out var tickerProp)) continue;
                        var sym = tickerProp.GetString();
                        if (string.IsNullOrEmpty(sym)) continue;

                        var price     = GetDecimal(item, "last") ?? GetDecimal(item, "tngoLast");
                        var prevClose = GetDecimal(item, "prevClose");
                        if (price == null) continue;

                        decimal? dayChange    = prevClose.HasValue ? price - prevClose : null;
                        double?  dayChangePct = prevClose.HasValue && prevClose != 0
                                               ? (double)((price - prevClose) / prevClose * 100m)
                                               : null;

                        result[sym] = new QuoteSummary
                        {
                            Symbol       = sym,
                            Price        = price,
                            DayChange    = dayChange,
                            DayChangePct = dayChangePct,
                            // Volume not available on the IEX tick endpoint.
                            // Fundamentals (P/E, market cap) require a paid Tiingo plan.
                        };
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Tiingo] FlushBatch error: {ex.Message}");
                }
                finally
                {
                    batch.Clear();
                }
            }

            foreach (var sym in symbols)
            {
                batch.Add(sym);
                if (batch.Count >= 100)
                    await FlushBatch();
            }
            await FlushBatch(); // flush any remainder

            return result;
        }

        // ── JSON helpers ──────────────────────────────────────────────────────────

        private static decimal? GetDecimal(JsonElement el, string key)
        {
            if (!el.TryGetProperty(key, out var p)) return null;
            if (p.ValueKind == JsonValueKind.Number) return p.GetDecimal();
            if (p.ValueKind == JsonValueKind.String &&
                decimal.TryParse(p.GetString(), out var v))
                return v;
            return null;
        }
    }
}
