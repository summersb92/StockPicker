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
        /// Parses the constituents CSV with a quote-aware splitter. The upstream file's
        /// column layout is Symbol,Security,GICS Sector,… (it has since grown extra
        /// columns — Headquarters, Date added, CIK, Founded — and quoted fields that
        /// contain commas, so neither "last column = sector" nor a naive Split(',')
        /// is safe: that combination once put founding years in the Sector field).
        /// Converts Yahoo-incompatible dot symbols (BRK.B → BRK-B).
        /// </summary>
        private static List<Stock> ParseSP500Csv(string csv)
        {
            var stocks = new List<Stock>(512);
            foreach (var rawLine in csv.Split('\n'))
            {
                var line = rawLine.Trim();
                if (string.IsNullOrEmpty(line)) continue;

                var parts = SplitCsvLine(line);
                if (parts.Count < 3) continue;

                var symbol = parts[0].Trim();
                if (string.IsNullOrEmpty(symbol) || symbol.Equals("Symbol", StringComparison.OrdinalIgnoreCase))
                    continue; // skip header or empty rows

                // Canonicalize (dot → dash, Yahoo Finance convention) via shared helper
                symbol = SymbolNormalizer.ToCanonical(symbol);

                stocks.Add(new Stock
                {
                    Symbol   = symbol,
                    Name     = parts[1].Trim(),
                    Exchange = "US",
                    Sector   = parts[2].Trim(),   // GICS Sector is always the third column
                });
            }
            return stocks;
        }

        /// <summary>Minimal RFC-4180 field splitter: honors double quotes and "" escapes.</summary>
        private static List<string> SplitCsvLine(string line)
        {
            var fields = new List<string>(8);
            var sb = new System.Text.StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                        else inQuotes = false;
                    }
                    else sb.Append(c);
                }
                else if (c == '"') inQuotes = true;
                else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(c);
            }
            fields.Add(sb.ToString());
            return fields;
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

            // Explicitly enumerate every field we consume so adding totalCash does not
            // silently drop any existing field if Yahoo ever tightens field filtering.
            const string fields =
                "longName,shortName,regularMarketPrice,regularMarketPreviousClose," +
                "regularMarketOpen,regularMarketDayHigh,regularMarketDayLow," +
                "regularMarketChange,regularMarketChangePercent,regularMarketVolume," +
                "averageVolume,marketCap,trailingPE,forwardPE,epsTrailingTwelveMonths," +
                "priceToBook,fiftyTwoWeekHigh,fiftyTwoWeekLow,beta,shortRatio," +
                "trailingAnnualDividendYield,52WeekChange,impliedVolatility," +
                "earningsTimestamp,totalCash";

            var url = $"https://query2.finance.yahoo.com/v7/finance/quote" +
                      $"?symbols={symbolList}" +
                      $"&fields={fields}" +
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

                    // totalCash is a flat number (e.g. 2.4e10 = $24B cash on balance sheet)
                    q.TotalCash = GetDecimal(item, "totalCash");

                    // dividendYield from Yahoo is a fraction (e.g. 0.0055 = 0.55%); convert to %
                    var rawYield = GetDouble(item, "trailingAnnualDividendYield");
                    q.DividendYieldPct = rawYield.HasValue ? rawYield * 100.0 : null;

                    // 52-week change is also a fraction
                    var raw52Chg = GetDouble(item, "52WeekChange");
                    q.Week52ChangePct = raw52Chg.HasValue ? raw52Chg * 100.0 : null;

                    // Options Greeks — Yahoo v7 quote includes impliedVolatility on some symbols
                    q.ImpliedVolatility = GetDouble(item, "impliedVolatility");
                    // Theta requires the options chain endpoint; not available from basic quote — left null

                    // Next earnings date — Yahoo reports earningsTimestamp (unix seconds) for most
                    // large caps. Symbols without it simply won't surface in the Earnings scanner.
                    q.NextEarningsDate = FromUnixSeconds(GetLong(item, "earningsTimestamp"));

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

        /// <summary>Convert a unix-seconds timestamp to local DateTime, ignoring non-positive values.</summary>
        private static DateTime? FromUnixSeconds(long? seconds)
        {
            if (!seconds.HasValue || seconds.Value <= 0) return null;
            try { return DateTimeOffset.FromUnixTimeSeconds(seconds.Value).LocalDateTime; }
            catch { return null; }
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
                var indicators = item.GetProperty("indicators");
                var quoteArr   = indicators.GetProperty("quote")[0];

                var opens   = quoteArr.GetProperty("open");
                var highs   = quoteArr.GetProperty("high");
                var lows    = quoteArr.GetProperty("low");
                var closes  = quoteArr.GetProperty("close");
                var volumes = quoteArr.GetProperty("volume");

                // Split/dividend-adjusted closes, when Yahoo provides them. We back-adjust
                // the whole bar by the adjclose/close ratio (the standard method), so a
                // 2:1 split doesn't appear as a fake −50% move in indicators or backtests.
                JsonElement adjCloses = default;
                bool hasAdj = indicators.TryGetProperty("adjclose", out var adjArr) &&
                              adjArr.ValueKind == JsonValueKind.Array &&
                              adjArr.GetArrayLength() > 0 &&
                              adjArr[0].TryGetProperty("adjclose", out adjCloses) &&
                              adjCloses.ValueKind == JsonValueKind.Array;

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

                    decimal open  = o.GetDecimal();
                    decimal close = c.GetDecimal();
                    decimal high  = h.ValueKind != JsonValueKind.Null ? h.GetDecimal() : open;
                    decimal low   = l.ValueKind != JsonValueKind.Null ? l.GetDecimal() : open;
                    bool adjusted = false;

                    if (hasAdj && i < adjCloses.GetArrayLength() &&
                        adjCloses[i].ValueKind != JsonValueKind.Null && close != 0)
                    {
                        decimal adj    = adjCloses[i].GetDecimal();
                        decimal factor = adj / close;
                        open  = Math.Round(open  * factor, 4);
                        high  = Math.Round(high  * factor, 4);
                        low   = Math.Round(low   * factor, 4);
                        close = adj;
                        adjusted = true;
                    }

                    quotes.Add(new StockQuote
                    {
                        Symbol    = symbol,
                        Timestamp = DateTimeOffset.FromUnixTimeSeconds(
                                        timestamps[i].GetInt64()).UtcDateTime,
                        Open       = open,
                        High       = high,
                        Low        = low,
                        Close      = close,
                        Volume     = v.ValueKind != JsonValueKind.Null ? v.GetInt64() : 0L,
                        IsAdjusted = adjusted,
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

        // ── Analyst ratings (quoteSummary v10) ─────────────────────────────────

        // Per-symbol cache. Yahoo serves this data with maxAge 86400 (daily), so a
        // 24-hour TTL for hits; failures are cached briefly so a bad symbol doesn't
        // re-fetch on every selection change. Static: one cache for the app lifetime,
        // matching the shared HttpClient above.
        private static readonly TimeSpan _analystTtl        = TimeSpan.FromHours(24);
        private static readonly TimeSpan _analystFailureTtl = TimeSpan.FromMinutes(15);
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<
            string, (DateTime FetchedAtUtc, AnalystRatings? Data)> _analystCache =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Fetches analyst consensus data (rating counts, recommendation mean, price
        /// targets) for one symbol from Yahoo's quoteSummary endpoint. On-demand only —
        /// this endpoint accepts a single symbol per request, so it is called for the
        /// selected symbol, never a whole universe. Returns null on any HTTP or parse
        /// failure; an analyst-data failure must never break anything else.
        /// </summary>
        public async Task<AnalystRatings?> GetAnalystRatingsAsync(
            string symbol, System.Threading.CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(symbol)) return null;

            // Cache hit (data within 24h, or a recent failure) — no network.
            if (_analystCache.TryGetValue(symbol, out var cached))
            {
                var ttl = cached.Data != null ? _analystTtl : _analystFailureTtl;
                if (DateTime.UtcNow - cached.FetchedAtUtc < ttl)
                    return cached.Data;
            }

            try
            {
                var crumb = await EnsureCrumbAsync();
                var url = $"https://query2.finance.yahoo.com/v10/finance/quoteSummary/" +
                          $"{Uri.EscapeDataString(symbol)}" +
                          "?modules=recommendationTrend,financialData" +
                          (crumb != null ? $"&crumb={Uri.EscapeDataString(crumb)}" : "");

                using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    _analystCache[symbol] = (DateTime.UtcNow, null);
                    return null;
                }

                var json    = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var ratings = ParseAnalystRatings(symbol, json, DateTime.UtcNow);
                _analystCache[symbol] = (DateTime.UtcNow, ratings);
                return ratings;
            }
            catch (System.OperationCanceledException)
            {
                // A superseded selection — don't poison the cache, just report nothing.
                return null;
            }
            catch
            {
                _analystCache[symbol] = (DateTime.UtcNow, null);
                return null;
            }
        }

        /// <summary>
        /// Parses a quoteSummary JSON payload (recommendationTrend + financialData
        /// modules) into an <see cref="AnalystRatings"/>. Defensive throughout: missing
        /// modules or fields become nulls/zeros; returns null when neither module
        /// yields any usable data or the JSON is malformed. Public and static so it is
        /// testable offline against canned fixtures.
        /// </summary>
        public static AnalystRatings? ParseAnalystRatings(string symbol, string json, DateTime fetchedAtUtc)
        {
            return ParseAnalystRatingsCore(symbol, json, fetchedAtUtc);
        }

        // ── Earnings surprise (quoteSummary earningsHistory) ───────────────────

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<
            string, (DateTime FetchedAtUtc, EarningsSurprise? Data)> _surpriseCache =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Fetches the most recent reported EPS vs. estimate for one symbol from the
        /// quoteSummary <c>earningsHistory</c> module. Same one-symbol-per-request and 24h
        /// cache characteristics as <see cref="GetAnalystRatingsAsync"/>.
        ///
        /// IMPORTANT LIMITATION: earningsHistory lags. A company that reported in the last few
        /// days will usually still show its PREVIOUS quarter as the newest entry, so this is the
        /// fallback source, used when Finnhub is unavailable. See
        /// <c>FinnhubStockDataService.GetEarningsSurpriseAsync</c> for the fresher path.
        ///
        /// Returns null on any HTTP or parse failure — an earnings-data failure must never break
        /// anything else.
        /// </summary>
        public async Task<EarningsSurprise?> GetEarningsSurpriseAsync(
            string symbol, System.Threading.CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(symbol)) return null;

            if (_surpriseCache.TryGetValue(symbol, out var cachedSurprise))
            {
                var ttl = cachedSurprise.Data != null ? _analystTtl : _analystFailureTtl;
                if (DateTime.UtcNow - cachedSurprise.FetchedAtUtc < ttl)
                    return cachedSurprise.Data;
            }

            try
            {
                var crumb = await EnsureCrumbAsync();
                var url = $"https://query2.finance.yahoo.com/v10/finance/quoteSummary/" +
                          $"{Uri.EscapeDataString(symbol)}" +
                          "?modules=earningsHistory" +
                          (crumb != null ? $"&crumb={Uri.EscapeDataString(crumb)}" : "");

                using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    _surpriseCache[symbol] = (DateTime.UtcNow, null);
                    return null;
                }

                var json     = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var surprise = ParseEarningsSurprise(symbol, json);
                _surpriseCache[symbol] = (DateTime.UtcNow, surprise);
                return surprise;
            }
            catch (System.OperationCanceledException)
            {
                return null;
            }
            catch
            {
                _surpriseCache[symbol] = (DateTime.UtcNow, null);
                return null;
            }
        }

        /// <summary>
        /// Parses the newest entry out of a quoteSummary <c>earningsHistory</c> payload.
        ///
        /// Yahoo's <c>surprisePercent</c> is a FRACTION (0.1012 = +10.12%) and is multiplied by
        /// 100 here so <see cref="EarningsSurprise"/> is uniformly in percent regardless of
        /// which provider filled it. Public and static so it is testable offline against canned
        /// fixtures, matching <see cref="ParseAnalystRatings"/>.
        /// </summary>
        public static EarningsSurprise? ParseEarningsSurprise(string symbol, string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("quoteSummary", out var qs)) return null;
                if (!qs.TryGetProperty("result", out var resultArr) ||
                    resultArr.ValueKind != JsonValueKind.Array ||
                    resultArr.GetArrayLength() == 0)
                    return null;

                if (!resultArr[0].TryGetProperty("earningsHistory", out var eh) ||
                    !eh.TryGetProperty("history", out var hist) ||
                    hist.ValueKind != JsonValueKind.Array)
                    return null;

                EarningsSurprise? newest = null;
                foreach (var h in hist.EnumerateArray())
                {
                    // "quarter" is a wrapped unix timestamp for the period end.
                    var quarterRaw = GetRawLong(h, "quarter");
                    if (!quarterRaw.HasValue) continue;
                    var period = DateTimeOffset.FromUnixTimeSeconds(quarterRaw.Value).UtcDateTime.Date;

                    if (newest != null && period <= newest.PeriodEnd) continue;

                    var fractional = GetRawDouble(h, "surprisePercent");

                    newest = new EarningsSurprise
                    {
                        Symbol          = symbol.ToUpperInvariant(),
                        PeriodEnd       = period,
                        EpsActual       = GetRawDouble(h, "epsActual"),
                        EpsEstimate     = GetRawDouble(h, "epsEstimate"),
                        // Fraction -> percent. Yahoo and Finnhub disagree on this unit.
                        SurprisePercent = fractional.HasValue ? fractional.Value * 100.0 : null,
                        Source          = DataSourceType.YahooFinance,
                    };
                }

                return newest;
            }
            catch
            {
                return null;
            }
        }

        private static AnalystRatings? ParseAnalystRatingsCore(string symbol, string json, DateTime fetchedAtUtc)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("quoteSummary", out var qs)) return null;
                if (!qs.TryGetProperty("result", out var resultArr) ||
                    resultArr.ValueKind != JsonValueKind.Array ||
                    resultArr.GetArrayLength() == 0)
                    return null;

                var result  = resultArr[0];
                var ratings = new AnalystRatings { Symbol = symbol, FetchedAtUtc = fetchedAtUtc };
                bool any    = false;

                // recommendationTrend.trend[] — use the current-month "0m" bucket.
                if (result.TryGetProperty("recommendationTrend", out var trendModule) &&
                    trendModule.ValueKind == JsonValueKind.Object &&
                    trendModule.TryGetProperty("trend", out var trendArr) &&
                    trendArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var t in trendArr.EnumerateArray())
                    {
                        if (GetString(t, "period") != "0m") continue;
                        ratings.StrongBuy  = (int)(GetLong(t, "strongBuy")  ?? 0);
                        ratings.Buy        = (int)(GetLong(t, "buy")        ?? 0);
                        ratings.Hold       = (int)(GetLong(t, "hold")       ?? 0);
                        ratings.Sell       = (int)(GetLong(t, "sell")       ?? 0);
                        ratings.StrongSell = (int)(GetLong(t, "strongSell") ?? 0);
                        any = any || ratings.TotalRatings > 0;
                        break;
                    }
                }

                // financialData — numeric values are wrapped objects with a "raw" field.
                if (result.TryGetProperty("financialData", out var fin) &&
                    fin.ValueKind == JsonValueKind.Object)
                {
                    ratings.RecommendationMean      = GetRawDouble(fin, "recommendationMean");
                    ratings.RecommendationKey       = GetString(fin, "recommendationKey") ?? "";
                    ratings.NumberOfAnalystOpinions = (int?)GetRawLong(fin, "numberOfAnalystOpinions");
                    ratings.TargetMeanPrice         = GetRawDecimal(fin, "targetMeanPrice");
                    ratings.TargetMedianPrice       = GetRawDecimal(fin, "targetMedianPrice");
                    ratings.TargetHighPrice         = GetRawDecimal(fin, "targetHighPrice");
                    ratings.TargetLowPrice          = GetRawDecimal(fin, "targetLowPrice");

                    any = any
                          || ratings.RecommendationMean.HasValue
                          || !string.IsNullOrEmpty(ratings.RecommendationKey)
                          || ratings.NumberOfAnalystOpinions.HasValue
                          || ratings.HasTargets;
                }

                return any ? ratings : null;
            }
            catch
            {
                return null;
            }
        }

        // Yahoo wraps quoteSummary numerics as { "raw": 1.94, "fmt": "1.94" }.
        private static double? GetRawDouble(JsonElement el, string key) =>
            el.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.Object
                ? GetDouble(p, "raw") : null;

        private static decimal? GetRawDecimal(JsonElement el, string key) =>
            el.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.Object
                ? GetDecimal(p, "raw") : null;

        private static long? GetRawLong(JsonElement el, string key) =>
            el.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.Object
                ? GetLong(p, "raw") : null;

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
