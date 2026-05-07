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
            // Encode each symbol individually so ^ becomes %5E, but keep commas as literal
            // separators — Yahoo rejects %2C between symbols and returns no results.
            var symbolList = string.Join(",", symbols.Select(Uri.EscapeDataString));
            if (string.IsNullOrWhiteSpace(symbolList)) return result;

            var crumb = await EnsureCrumbAsync();
            var url = $"https://query2.finance.yahoo.com/v7/finance/quote" +
                      $"?symbols={symbolList}" +
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

                    // Options Greeks — Yahoo v7 quote includes impliedVolatility on some symbols
                    q.ImpliedVolatility = GetDouble(item, "impliedVolatility");
                    // Theta requires the options chain endpoint; not available from basic quote — left null

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
        // ── Weekly chart bars ──────────────────────────────────────────────────

        /// <inheritdoc />
        public async Task<IReadOnlyList<WeeklyBar>> GetWeeklyBarsAsync(string symbol, ChartRange range = ChartRange.Year, System.Threading.CancellationToken ct = default)
        {
            try
            {
                // Yahoo Finance chart API
                string interval   = range == ChartRange.Week ? "1d"  : "1wk";
                string rangeParam = range == ChartRange.Week ? "5d"  : "1y";

                string url = $"https://query1.finance.yahoo.com/v8/finance/chart/{Uri.EscapeDataString(symbol)}"
                           + $"?interval={interval}&range={rangeParam}";

                using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return Array.Empty<WeeklyBar>();

                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                var root = doc.RootElement;

                if (!root.TryGetProperty("chart", out var chart)) return Array.Empty<WeeklyBar>();
                if (!chart.TryGetProperty("result", out var results)) return Array.Empty<WeeklyBar>();
                if (results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0)
                    return Array.Empty<WeeklyBar>();

                var result = results[0];

                // Timestamps
                if (!result.TryGetProperty("timestamp", out var tsArr)) return Array.Empty<WeeklyBar>();

                // OHLCV lives inside indicators.quote[0]
                if (!result.TryGetProperty("indicators", out var indicators)) return Array.Empty<WeeklyBar>();
                if (!indicators.TryGetProperty("quote", out var quoteArr)) return Array.Empty<WeeklyBar>();
                if (quoteArr.ValueKind != JsonValueKind.Array || quoteArr.GetArrayLength() == 0)
                    return Array.Empty<WeeklyBar>();
                var q = quoteArr[0];

                var opens   = q.TryGetProperty("open",   out var o) ? o : default;
                var highs   = q.TryGetProperty("high",   out var h) ? h : default;
                var lows    = q.TryGetProperty("low",    out var l) ? l : default;
                var closes  = q.TryGetProperty("close",  out var c) ? c : default;
                var volumes = q.TryGetProperty("volume", out var v) ? v : default;

                var bars = new List<WeeklyBar>();
                int count = tsArr.GetArrayLength();
                for (int i = 0; i < count; i++)
                {
                    // Skip bars where close is null (can happen on partial weeks)
                    if (closes.ValueKind == JsonValueKind.Array)
                    {
                        var closeEl = closes[i];
                        if (closeEl.ValueKind == JsonValueKind.Null) continue;

                        var ts = DateTimeOffset.FromUnixTimeSeconds(tsArr[i].GetInt64()).UtcDateTime;
                        bars.Add(new WeeklyBar
                        {
                            WeekStart = ts,
                            Open      = opens.ValueKind  == JsonValueKind.Array && opens[i].ValueKind  != JsonValueKind.Null ? (decimal)opens[i].GetDouble()  : closeEl.GetDecimal(),
                            High      = highs.ValueKind  == JsonValueKind.Array && highs[i].ValueKind  != JsonValueKind.Null ? (decimal)highs[i].GetDouble()  : closeEl.GetDecimal(),
                            Low       = lows.ValueKind   == JsonValueKind.Array && lows[i].ValueKind   != JsonValueKind.Null ? (decimal)lows[i].GetDouble()   : closeEl.GetDecimal(),
                            Close     = (decimal)closeEl.GetDouble(),
                            Volume    = volumes.ValueKind == JsonValueKind.Array && volumes[i].ValueKind != JsonValueKind.Null ? volumes[i].GetInt64() : 0,
                        });
                    }
                }
                return bars;
            }
            catch (System.OperationCanceledException)
            {
                throw; // let caller handle cancellation silently
            }
            catch
            {
                return Array.Empty<WeeklyBar>();
            }
        }

        // ── Options data (IV + Black-Scholes Theta) ────────────────────────────

        /// <summary>
        /// Fetches the near-term ATM implied volatility from Yahoo's options endpoint
        /// and derives Theta via Black-Scholes.
        /// Returns (null, null) if options data is unavailable.
        /// </summary>
        public async Task<(double? IV, double? Theta)> GetNearTermOptionsAsync(
            string symbol, System.Threading.CancellationToken ct = default)
        {
            try
            {
                string url = $"https://query1.finance.yahoo.com/v7/finance/options/{Uri.EscapeDataString(symbol)}";
                using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return (null, null);

                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                var result = doc.RootElement
                    .GetProperty("optionChain")
                    .GetProperty("result")[0];

                // Underlying price
                double stockPrice = 0;
                if (result.TryGetProperty("quote", out var quote) &&
                    quote.TryGetProperty("regularMarketPrice", out var priceEl))
                    stockPrice = priceEl.GetDouble();

                // Nearest expiration's option contracts
                var optArr = result.GetProperty("options");
                if (optArr.GetArrayLength() == 0) return (null, null);
                var opts = optArr[0];

                // Expiration date → time to expiry in years
                double T = 0;
                if (result.TryGetProperty("expirationDates", out var expArr) && expArr.GetArrayLength() > 0)
                {
                    var expUnix = expArr[0].GetInt64();
                    var expDate = DateTimeOffset.FromUnixTimeSeconds(expUnix).UtcDateTime;
                    T = (expDate - DateTime.UtcNow).TotalDays / 365.0;
                }
                if (T <= 0) T = 7.0 / 365.0; // fallback: 1 week

                // Find ATM call (strike closest to stock price)
                double bestIV = double.NaN;
                double bestStrike = 0;
                double bestDist = double.MaxValue;

                if (opts.TryGetProperty("calls", out var calls))
                {
                    foreach (var call in calls.EnumerateArray())
                    {
                        if (!call.TryGetProperty("strike",            out var stEl)) continue;
                        if (!call.TryGetProperty("impliedVolatility", out var ivEl)) continue;
                        if (ivEl.ValueKind == JsonValueKind.Null) continue;

                        double strike = stEl.GetDouble();
                        double dist   = Math.Abs(strike - stockPrice);
                        if (dist < bestDist)
                        {
                            bestDist   = dist;
                            bestStrike = strike;
                            bestIV     = ivEl.GetDouble();
                        }
                    }
                }

                if (double.IsNaN(bestIV) || stockPrice <= 0) return (null, null);

                const double r = 0.053; // approximate risk-free rate
                double theta = BlackScholesTheta(stockPrice, bestStrike, T, r, bestIV);

                return (bestIV, theta);
            }
            catch (System.OperationCanceledException) { throw; }
            catch { return (null, null); }
        }

        // ── Black-Scholes helpers ──────────────────────────────────────────────

        /// <summary>Theta of a call option ($/day) using Black-Scholes.</summary>
        private static double BlackScholesTheta(double S, double K, double T, double r, double sigma)
        {
            if (T <= 0 || sigma <= 0 || S <= 0 || K <= 0) return 0;
            double d1 = (Math.Log(S / K) + (r + sigma * sigma / 2.0) * T) / (sigma * Math.Sqrt(T));
            double d2 = d1 - sigma * Math.Sqrt(T);
            double theta = -(S * NormalPdf(d1) * sigma / (2.0 * Math.Sqrt(T)))
                           - r * K * Math.Exp(-r * T) * NormalCdf(d2);
            return theta / 365.0; // convert from per-year to per-day
        }

        private static double NormalPdf(double x)
            => Math.Exp(-0.5 * x * x) / Math.Sqrt(2.0 * Math.PI);

        private static double NormalCdf(double x)
        {
            // Abramowitz & Stegun rational approximation — max error 7.5e-8
            const double a1 =  0.254829592, a2 = -0.284496736, a3 = 1.421413741;
            const double a4 = -1.453152027, a5 =  1.061405429, p  = 0.3275911;
            double sign = x < 0 ? -1.0 : 1.0;
            x = Math.Abs(x) / Math.Sqrt(2.0);
            double t = 1.0 / (1.0 + p * x);
            double poly = t * (a1 + t * (a2 + t * (a3 + t * (a4 + t * a5))));
            double erf  = 1.0 - poly * Math.Exp(-x * x);
            return 0.5 * (1.0 + sign * erf);
        }

    }
}
