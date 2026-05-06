using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockPicker.Models;

namespace StockPicker.Services
{
    /// <summary>
    /// Fetches real stock data from Yahoo Finance using the unofficial v8/finance/chart endpoint.
    /// No API key required. Automatically handles the session cookie + crumb handshake
    /// that Yahoo began requiring in 2024.
    /// </summary>
    public class YahooFinanceStockDataService : IStockDataService
    {
        /// <inheritdoc />
        public DataSourceType SourceType => DataSourceType.YahooFinance;

        // ── HTTP client (shared, one instance for the app lifetime) ──────────────
        private static readonly CookieContainer _cookies = new();
        private static readonly HttpClient _http = BuildClient();

        // Crumb is fetched once and reused across all requests.
        private string? _crumb;
        private readonly SemaphoreSlim _crumbLock = new(1, 1);

        private static HttpClient BuildClient()
        {
            var handler = new HttpClientHandler
            {
                CookieContainer = _cookies,
                AllowAutoRedirect = true,
                UseCookies = true,
            };
            var client = new HttpClient(handler);
            client.DefaultRequestHeaders.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
                "AppleWebKit/537.36 (KHTML, like Gecko) " +
                "Chrome/124.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.Add("Accept",
                "application/json,text/html,application/xhtml+xml,*/*");
            client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
            client.Timeout = TimeSpan.FromSeconds(20);
            return client;
        }

        // ── Stock universe ────────────────────────────────────────────────────────

        // Live S&P 500 constituent list from a community-maintained public dataset.
        // Fetched once per session and cached; falls back to the built-in list on failure.
        private const string SP500CsvUrl =
            "https://raw.githubusercontent.com/datasets/s-and-p-500-companies/main/data/constituents.csv";

        // Session-level cache so repeated calls (e.g. on a 15-min refresh) reuse the list.
        private static IReadOnlyList<Stock>? _universeCache;

        public async Task<IReadOnlyList<Stock>> GetUniverseAsync()
        {
            if (_universeCache != null) return _universeCache;

            var live = await TryFetchSP500Async();
            _universeCache = live.Count >= 10 ? live : _fallbackUniverse;
            return _universeCache;
        }

        private static async Task<List<Stock>> TryFetchSP500Async()
        {
            try
            {
                var csv = await _http.GetStringAsync(SP500CsvUrl);
                return ParseSP500Csv(csv);
            }
            catch
            {
                return new List<Stock>();
            }
        }

        /// <summary>
        /// Parses the S&amp;P 500 CSV (Symbol, Name, Sector).
        /// Handles names that contain commas by treating the last column as Sector
        /// and joining any middle columns back into the Name.
        /// Converts Yahoo-incompatible dot symbols (BRK.B → BRK-B).
        /// </summary>
        private static List<Stock> ParseSP500Csv(string csv)
        {
            var stocks = new List<Stock>(512);
            foreach (var rawLine in csv.Split('\n'))
            {
                var line = rawLine.Trim();
                if (string.IsNullOrEmpty(line)) continue;

                var parts = line.Split(',');
                if (parts.Length < 3) continue;

                var symbol = parts[0].Trim();
                if (string.IsNullOrEmpty(symbol) || symbol.Equals("Symbol", StringComparison.OrdinalIgnoreCase))
                    continue; // skip header or empty rows

                // Convert dot notation to dash (Yahoo Finance convention)
                symbol = symbol.Replace('.', '-');

                // Last column = Sector; everything in between = Name (handles commas in names)
                var sector = parts[^1].Trim();
                var name   = parts.Length == 3
                    ? parts[1].Trim()
                    : string.Join(",", parts[1..^1]).Trim();

                stocks.Add(new Stock
                {
                    Symbol   = symbol,
                    Name     = name,
                    Exchange = "US",
                    Sector   = sector,
                });
            }
            return stocks;
        }

        // ── Built-in fallback universe (used when GitHub CSV is unreachable) ──────
        private static readonly IReadOnlyList<Stock> _fallbackUniverse = new List<Stock>
        {
            // Technology
            new() { Symbol = "AAPL",  Name = "Apple Inc.",              Exchange = "NASDAQ", Sector = "Technology"       },
            new() { Symbol = "MSFT",  Name = "Microsoft Corp.",         Exchange = "NASDAQ", Sector = "Technology"       },
            new() { Symbol = "NVDA",  Name = "NVIDIA Corp.",            Exchange = "NASDAQ", Sector = "Technology"       },
            new() { Symbol = "GOOGL", Name = "Alphabet Inc.",           Exchange = "NASDAQ", Sector = "Technology"       },
            new() { Symbol = "META",  Name = "Meta Platforms",          Exchange = "NASDAQ", Sector = "Technology"       },
            new() { Symbol = "AMD",   Name = "Advanced Micro Devices",  Exchange = "NASDAQ", Sector = "Technology"       },
            new() { Symbol = "CRM",   Name = "Salesforce Inc.",         Exchange = "NYSE",   Sector = "Technology"       },
            new() { Symbol = "QCOM",  Name = "Qualcomm Inc.",           Exchange = "NASDAQ", Sector = "Technology"       },
            new() { Symbol = "ORCL",  Name = "Oracle Corp.",            Exchange = "NYSE",   Sector = "Technology"       },
            new() { Symbol = "INTC",  Name = "Intel Corp.",             Exchange = "NASDAQ", Sector = "Technology"       },
            // Financials
            new() { Symbol = "JPM",   Name = "JPMorgan Chase",          Exchange = "NYSE",   Sector = "Financials"       },
            new() { Symbol = "BAC",   Name = "Bank of America",         Exchange = "NYSE",   Sector = "Financials"       },
            new() { Symbol = "GS",    Name = "Goldman Sachs",           Exchange = "NYSE",   Sector = "Financials"       },
            new() { Symbol = "V",     Name = "Visa Inc.",               Exchange = "NYSE",   Sector = "Financials"       },
            new() { Symbol = "MA",    Name = "Mastercard Inc.",         Exchange = "NYSE",   Sector = "Financials"       },
            // Healthcare
            new() { Symbol = "JNJ",   Name = "Johnson & Johnson",       Exchange = "NYSE",   Sector = "Healthcare"       },
            new() { Symbol = "UNH",   Name = "UnitedHealth Group",      Exchange = "NYSE",   Sector = "Healthcare"       },
            new() { Symbol = "LLY",   Name = "Eli Lilly & Co.",         Exchange = "NYSE",   Sector = "Healthcare"       },
            new() { Symbol = "ABBV",  Name = "AbbVie Inc.",             Exchange = "NYSE",   Sector = "Healthcare"       },
            new() { Symbol = "MRK",   Name = "Merck & Co.",             Exchange = "NYSE",   Sector = "Healthcare"       },
            // Energy
            new() { Symbol = "XOM",   Name = "Exxon Mobil",            Exchange = "NYSE",   Sector = "Energy"           },
            new() { Symbol = "CVX",   Name = "Chevron Corp.",           Exchange = "NYSE",   Sector = "Energy"           },
            new() { Symbol = "COP",   Name = "ConocoPhillips",          Exchange = "NYSE",   Sector = "Energy"           },
            // Consumer
            new() { Symbol = "AMZN",  Name = "Amazon.com Inc.",         Exchange = "NASDAQ", Sector = "Consumer Disc."   },
            new() { Symbol = "TSLA",  Name = "Tesla Inc.",              Exchange = "NASDAQ", Sector = "Consumer Disc."   },
            new() { Symbol = "WMT",   Name = "Walmart Inc.",            Exchange = "NYSE",   Sector = "Consumer Staples" },
            new() { Symbol = "HD",    Name = "Home Depot Inc.",         Exchange = "NYSE",   Sector = "Consumer Disc."   },
            new() { Symbol = "MCD",   Name = "McDonald's Corp.",        Exchange = "NYSE",   Sector = "Consumer Disc."   },
            // Industrials
            new() { Symbol = "CAT",   Name = "Caterpillar Inc.",        Exchange = "NYSE",   Sector = "Industrials"      },
            new() { Symbol = "GE",    Name = "GE Aerospace",            Exchange = "NYSE",   Sector = "Industrials"      },
            // Communication
            new() { Symbol = "NFLX",  Name = "Netflix Inc.",            Exchange = "NASDAQ", Sector = "Communication"    },
            new() { Symbol = "DIS",   Name = "Walt Disney Co.",         Exchange = "NYSE",   Sector = "Communication"    },
            // Utilities / Materials
            new() { Symbol = "NEE",   Name = "NextEra Energy",          Exchange = "NYSE",   Sector = "Utilities"        },
            new() { Symbol = "LIN",   Name = "Linde plc",               Exchange = "NASDAQ", Sector = "Materials"        },
        };

        // ── Public API ────────────────────────────────────────────────────────────

        public async Task<IReadOnlyList<StockQuote>> GetHistoryAsync(
            string symbol, DateTime from, DateTime to)
        {
            var crumb = await EnsureCrumbAsync();
            var period1 = ToUnixSeconds(from.Date);
            var period2 = ToUnixSeconds(to.Date.AddDays(1)); // make end-date inclusive
            var url = $"https://query2.finance.yahoo.com/v8/finance/chart/{symbol}" +
                      $"?period1={period1}&period2={period2}&interval=1d&events=history" +
                      (crumb != null ? $"&crumb={Uri.EscapeDataString(crumb)}" : "");

            return await FetchChartAsync(symbol, url);
        }

        public async Task<StockQuote?> GetLatestQuoteAsync(string symbol)
        {
            // Pull the last 5 trading days; return the most recent bar.
            var bars = await GetHistoryAsync(symbol, DateTime.Today.AddDays(-7), DateTime.Today);
            return bars.Count > 0 ? bars[^1] : null;
        }

        // ── Batch quote summary ───────────────────────────────────────────────────

        /// <summary>
        /// Fetches live market data for all <paramref name="symbols"/> in one HTTP call
        /// using Yahoo Finance's v7/finance/quote endpoint.
        /// </summary>
        public async Task<Dictionary<string, QuoteSummary>> GetQuoteSummariesAsync(
            IEnumerable<string> symbols)
        {
            var result = new Dictionary<string, QuoteSummary>(StringComparer.OrdinalIgnoreCase);
            var symbolList = string.Join(",", symbols);
            if (string.IsNullOrWhiteSpace(symbolList)) return result;

            var crumb = await EnsureCrumbAsync();
            var url = $"https://query2.finance.yahoo.com/v7/finance/quote" +
                      $"?symbols={Uri.EscapeDataString(symbolList)}" +
                      (crumb != null ? $"&crumb={Uri.EscapeDataString(crumb)}" : "");

            try
            {
                var json = await _http.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);

                var quoteResponse = doc.RootElement.GetProperty("quoteResponse");
                if (!quoteResponse.TryGetProperty("result", out var resultArr) ||
                    resultArr.ValueKind != JsonValueKind.Array)
                    return result;

                foreach (var item in resultArr.EnumerateArray())
                {
                    var sym = GetString(item, "symbol");
                    if (string.IsNullOrEmpty(sym)) continue;

                    var q = new QuoteSummary
                    {
                        Symbol          = sym,
                        LongName        = GetString(item, "longName"),
                        ShortName       = GetString(item, "shortName"),
                        Price           = GetDecimal(item, "regularMarketPrice"),
                        PrevClose       = GetDecimal(item, "regularMarketPreviousClose"),
                        DayOpen         = GetDecimal(item, "regularMarketOpen"),
                        DayHigh         = GetDecimal(item, "regularMarketDayHigh"),
                        DayLow          = GetDecimal(item, "regularMarketDayLow"),
                        DayChange       = GetDecimal(item, "regularMarketChange"),
                        DayChangePct    = GetDouble(item,  "regularMarketChangePercent"),
                        Volume          = GetLong(item,    "regularMarketVolume"),
                        AvgVolume       = GetLong(item,    "averageVolume"),
                        MarketCap       = GetLong(item,    "marketCap"),
                        PERatio         = GetDouble(item,  "trailingPE"),
                        ForwardPE       = GetDouble(item,  "forwardPE"),
                        EPS             = GetDouble(item,  "epsTrailingTwelveMonths"),
                        PriceToBook     = GetDouble(item,  "priceToBook"),
                        Week52High      = GetDecimal(item, "fiftyTwoWeekHigh"),
                        Week52Low       = GetDecimal(item, "fiftyTwoWeekLow"),
                        Beta            = GetDouble(item,  "beta"),
                        ShortRatio      = GetDouble(item,  "shortRatio"),
                    };

                    // dividendYield from Yahoo is a fraction (e.g. 0.0055 = 0.55%); convert to %
                    var rawYield = GetDouble(item, "trailingAnnualDividendYield");
                    q.DividendYieldPct = rawYield.HasValue ? rawYield * 100.0 : null;

                    // 52-week change is also a fraction
                    var raw52Chg = GetDouble(item, "52WeekChange");
                    q.Week52ChangePct = raw52Chg.HasValue ? raw52Chg * 100.0 : null;

                    result[sym] = q;
                }
            }
            catch
            {
                // Swallow — caller handles empty dictionary gracefully.
            }

            return result;
        }

        // ── JSON field helpers ────────────────────────────────────────────────────

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

        private static long? GetLong(JsonElement el, string key)
        {
            if (!el.TryGetProperty(key, out var p)) return null;
            return p.ValueKind == JsonValueKind.Number ? p.GetInt64() : null;
        }

        // ── Yahoo Finance session / crumb ─────────────────────────────────────────

        /// <summary>
        /// Ensures we have a valid session cookie and crumb token.
        /// Yahoo Finance began requiring this in mid-2024.
        /// Thread-safe: only one fetch will run at a time.
        /// </summary>
        private async Task<string?> EnsureCrumbAsync()
        {
            if (_crumb != null) return _crumb;

            await _crumbLock.WaitAsync();
            try
            {
                if (_crumb != null) return _crumb;

                // Step 1 — hit the consent endpoint to establish a session cookie.
                try { await _http.GetAsync("https://fc.yahoo.com"); } catch { /* best-effort */ }

                // Step 2 — exchange the cookie for a crumb string.
                try
                {
                    _crumb = await _http.GetStringAsync(
                        "https://query2.finance.yahoo.com/v1/test/getcrumb");
                }
                catch
                {
                    // If crumb fetch fails we'll try unauthenticated requests.
                    _crumb = null;
                }
            }
            finally
            {
                _crumbLock.Release();
            }

            return _crumb;
        }

        // ── Parsing ───────────────────────────────────────────────────────────────

        private async Task<IReadOnlyList<StockQuote>> FetchChartAsync(string symbol, string url)
        {
            var quotes = new List<StockQuote>();
            try
            {
                var json = await _http.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);

                var chart = doc.RootElement.GetProperty("chart");

                // Surface API-level errors (e.g. unknown symbol).
                if (chart.TryGetProperty("error", out var errEl) &&
                    errEl.ValueKind != JsonValueKind.Null)
                    return quotes;

                var result = chart.GetProperty("result");
                if (result.ValueKind == JsonValueKind.Null || result.GetArrayLength() == 0)
                    return quotes;

                var item       = result[0];
                var timestamps = item.GetProperty("timestamp");
                var quoteArr   = item.GetProperty("indicators").GetProperty("quote")[0];

                var opens   = quoteArr.GetProperty("open");
                var highs   = quoteArr.GetProperty("high");
                var lows    = quoteArr.GetProperty("low");
                var closes  = quoteArr.GetProperty("close");
                var volumes = quoteArr.GetProperty("volume");

                int count = timestamps.GetArrayLength();
                for (int i = 0; i < count; i++)
                {
                    if (i >= opens.GetArrayLength())  break;

                    var o = opens[i];
                    var c = closes[i];
                    if (o.ValueKind == JsonValueKind.Null || c.ValueKind == JsonValueKind.Null)
                        continue;   // skip null bars (market-closed days Yahoo sometimes includes)

                    var h = highs[i];
                    var l = lows[i];
                    var v = volumes[i];

                    quotes.Add(new StockQuote
                    {
                        Symbol    = symbol,
                        Timestamp = DateTimeOffset.FromUnixTimeSeconds(
                                        timestamps[i].GetInt64()).UtcDateTime,
                        Open   = o.GetDecimal(),
                        High   = h.ValueKind != JsonValueKind.Null ? h.GetDecimal() : o.GetDecimal(),
                        Low    = l.ValueKind  != JsonValueKind.Null ? l.GetDecimal() : o.GetDecimal(),
                        Close  = c.GetDecimal(),
                        Volume = v.ValueKind  != JsonValueKind.Null ? v.GetInt64()   : 0L,
                    });
                }
            }
            catch
            {
                // Return whatever we collected; callers handle an empty list gracefully.
            }

            return quotes;
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        private static long ToUnixSeconds(DateTime date) =>
            new DateTimeOffset(date).ToUnixTimeSeconds();
    }
}
