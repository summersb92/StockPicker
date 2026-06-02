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
    /// Fetches stock data from Alpaca's Market Data API v2 using the configured
    /// ALPACA_API_KEY and ALPACA_API_SECRET environment variables.
    /// </summary>
    public class AlpacaStockDataService : IStockDataService
    {
        private const string BaseUrl = "https://data.alpaca.markets/v2";

        private readonly HttpClient _http;

        public AlpacaStockDataService()
        {
            var apiKey = GetEnvironmentVariable("ALPACA_API_KEY");
            var apiSecret = GetEnvironmentVariable("ALPACA_API_SECRET");

            if (string.IsNullOrWhiteSpace(apiKey))
                throw new ArgumentException("ALPACA_API_KEY environment variable is required but not set.");
            if (string.IsNullOrWhiteSpace(apiSecret))
                throw new ArgumentException("ALPACA_API_SECRET environment variable is required but not set.");

            _http = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(20)
            };
            _http.DefaultRequestHeaders.Add("APCA-API-KEY-ID", apiKey);
            _http.DefaultRequestHeaders.Add("APCA-API-SECRET-KEY", apiSecret);
        }

        /// <inheritdoc />
        public DataSourceType SourceType => DataSourceType.Alpaca;

        public static bool HasEnvironmentCredentials() =>
            !string.IsNullOrWhiteSpace(GetEnvironmentVariable("ALPACA_API_KEY")) &&
            !string.IsNullOrWhiteSpace(GetEnvironmentVariable("ALPACA_API_SECRET"));

        /// <summary>
        /// Not supported — universe is always loaded from Yahoo Finance.
        /// </summary>
        public Task<IReadOnlyList<Stock>> GetUniverseAsync() =>
            throw new NotSupportedException(
                "AlpacaStockDataService does not supply the stock universe. " +
                "Use YahooFinanceStockDataService.GetUniverseAsync() instead.");

        public async Task<IReadOnlyList<StockQuote>> GetHistoryAsync(
            string symbol, DateTime from, DateTime to)
        {
            var quotes = new List<StockQuote>();

            try
            {
                var url = $"{BaseUrl}/stocks/bars" +
                          $"?symbols={Uri.EscapeDataString(symbol)}" +
                          $"&timeframe=1Day" +
                          $"&start={Uri.EscapeDataString(from.Date.ToString("yyyy-MM-dd"))}" +
                          $"&end={Uri.EscapeDataString(to.Date.ToString("yyyy-MM-dd"))}" +
                          $"&adjustment=raw" +
                          $"&feed=iex" +
                          $"&sort=asc" +
                          $"&limit=1000";

                var json = await _http.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("bars", out var barsRoot) ||
                    barsRoot.ValueKind != JsonValueKind.Object ||
                    !barsRoot.TryGetProperty(symbol, out var bars) ||
                    bars.ValueKind != JsonValueKind.Array)
                    return quotes;

                foreach (var bar in bars.EnumerateArray())
                {
                    var timestamp = GetDateTime(bar, "t");
                    var open      = GetDecimal(bar, "o");
                    var high      = GetDecimal(bar, "h");
                    var low       = GetDecimal(bar, "l");
                    var close     = GetDecimal(bar, "c");
                    var volume    = GetLong(bar, "v");

                    if (timestamp == null || open == null || close == null)
                        continue;

                    quotes.Add(new StockQuote
                    {
                        Symbol    = symbol,
                        Timestamp = timestamp.Value.UtcDateTime,
                        Open      = open.Value,
                        High      = high ?? open.Value,
                        Low       = low ?? open.Value,
                        Close     = close.Value,
                        Volume    = volume ?? 0L,
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Alpaca] GetHistoryAsync({symbol}) error: {ex.Message}");
            }

            return quotes;
        }

        public async Task<StockQuote?> GetLatestQuoteAsync(string symbol)
        {
            try
            {
                var snapshot = await GetSnapshotsAsync(new[] { symbol });
                if (!snapshot.TryGetValue(symbol, out var item))
                    return null;

                var latestTrade = item.TryGetProperty("latestTrade", out var trade) ? trade : default;
                var dailyBar    = item.TryGetProperty("dailyBar", out var day) ? day : default;

                var price = GetDecimal(latestTrade, "p") ?? GetDecimal(dailyBar, "c");
                if (price == null)
                    return null;

                return new StockQuote
                {
                    Symbol    = symbol,
                    Timestamp = GetDateTime(latestTrade, "t")?.UtcDateTime ?? DateTime.UtcNow,
                    Open      = GetDecimal(dailyBar, "o") ?? price.Value,
                    High      = GetDecimal(dailyBar, "h") ?? price.Value,
                    Low       = GetDecimal(dailyBar, "l") ?? price.Value,
                    Close     = price.Value,
                    Volume    = GetLong(dailyBar, "v") ?? 0L,
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Alpaca] GetLatestQuoteAsync({symbol}) error: {ex.Message}");
                return null;
            }
        }

        public async Task<Dictionary<string, QuoteSummary>> GetQuoteSummariesAsync(
            IEnumerable<string> symbols)
        {
            var result = new Dictionary<string, QuoteSummary>(StringComparer.OrdinalIgnoreCase);

            foreach (var batch in BatchSymbols(symbols, 500))
            {
                try
                {
                    var snapshots = await GetSnapshotsAsync(batch);
                    foreach (var sym in batch)
                    {
                        if (!snapshots.TryGetValue(sym, out var item))
                            continue;

                        var latestTrade = item.TryGetProperty("latestTrade", out var trade) ? trade : default;
                        var dailyBar    = item.TryGetProperty("dailyBar", out var day) ? day : default;
                        var prevDaily   = item.TryGetProperty("prevDailyBar", out var prev) ? prev : default;

                        var price     = GetDecimal(latestTrade, "p") ?? GetDecimal(dailyBar, "c") ?? GetDecimal(prevDaily, "c");
                        var prevClose = GetDecimal(prevDaily, "c");
                        if (price == null)
                            continue;

                        decimal? dayChange = prevClose.HasValue ? price.Value - prevClose.Value : null;
                        double? dayChangePct = prevClose.HasValue && prevClose.Value != 0m
                            ? (double)((price.Value - prevClose.Value) / prevClose.Value * 100m)
                            : null;

                        result[sym] = new QuoteSummary
                        {
                            Symbol       = sym,
                            Price        = price,
                            PrevClose    = prevClose,
                            DayOpen      = GetDecimal(dailyBar, "o"),
                            DayHigh      = GetDecimal(dailyBar, "h"),
                            DayLow       = GetDecimal(dailyBar, "l"),
                            DayChange    = dayChange,
                            DayChangePct = dayChangePct,
                            Volume       = GetLong(dailyBar, "v"),
                        };
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Alpaca] GetQuoteSummariesAsync batch error: {ex.Message}");
                }
            }

            return result;
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<WeeklyBar>> GetWeeklyBarsAsync(string symbol, ChartRange range = ChartRange.Year, System.Threading.CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<WeeklyBar>>(Array.Empty<WeeklyBar>());

        /// <inheritdoc />
        public Task<(double? IV, double? Theta)> GetNearTermOptionsAsync(string symbol, System.Threading.CancellationToken ct = default)
            => Task.FromResult<(double? IV, double? Theta)>((null, null));

        private async Task<Dictionary<string, JsonElement>> GetSnapshotsAsync(IEnumerable<string> symbols)
        {
            var batch = string.Join(",", symbols);
            if (string.IsNullOrWhiteSpace(batch))
                return new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

            var url = $"{BaseUrl}/stocks/snapshots" +
                      $"?symbols={Uri.EscapeDataString(batch)}" +
                      $"&feed=iex";

            var json = await _http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);

            var result = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return result;

            foreach (var property in doc.RootElement.EnumerateObject())
                result[property.Name] = property.Value.Clone();

            return result;
        }

        private static IEnumerable<List<string>> BatchSymbols(IEnumerable<string> symbols, int batchSize)
        {
            var batch = new List<string>(batchSize);
            foreach (var symbol in symbols)
            {
                if (string.IsNullOrWhiteSpace(symbol))
                    continue;

                batch.Add(symbol);
                if (batch.Count >= batchSize)
                {
                    yield return batch;
                    batch = new List<string>(batchSize);
                }
            }

            if (batch.Count > 0)
                yield return batch;
        }

        private static string? GetEnvironmentVariable(string name) =>
            Environment.GetEnvironmentVariable(name) ??
            Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User) ??
            Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine);

        private static DateTimeOffset? GetDateTime(JsonElement el, string key)
        {
            if (el.ValueKind == JsonValueKind.Undefined ||
                !el.TryGetProperty(key, out var p) ||
                p.ValueKind != JsonValueKind.String)
                return null;

            return DateTimeOffset.TryParse(p.GetString(), out var value) ? value : null;
        }

        private static decimal? GetDecimal(JsonElement el, string key)
        {
            if (el.ValueKind == JsonValueKind.Undefined ||
                !el.TryGetProperty(key, out var p) ||
                p.ValueKind != JsonValueKind.Number)
                return null;

            return p.GetDecimal();
        }

        private static long? GetLong(JsonElement el, string key)
        {
            if (el.ValueKind == JsonValueKind.Undefined ||
                !el.TryGetProperty(key, out var p) ||
                p.ValueKind != JsonValueKind.Number)
                return null;

            return p.TryGetInt64(out var value) ? value : null;
        }
    }
}
