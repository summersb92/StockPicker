using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using StockPicker.Models;

namespace StockPicker.Services
{
    /// <summary>
    /// Computes technical indicators from real price history and returns a scored
    /// <see cref="AnalysisResult"/> for each stock.  Strategy-aware: the score
    /// is assembled differently depending on <see cref="ScanContext.Strategy"/>.
    /// </summary>
    /// <remarks>
    /// Score convention (matches <see cref="RecommendationService"/> thresholds):
    ///   ≥  2.0  → StrongBuy   |  ≥ 0.5 → Buy
    ///   ≤ -2.0  → StrongSell  |  ≤ -0.5 → Sell
    ///   otherwise → Hold
    ///
    /// Indicators computed (where enough history exists):
    ///   SMA20, SMA50 — simple moving averages of close
    ///   RSI14        — classic 14-period RSI
    ///   WeekReturn   — % change over the most recent 5 trading days
    ///   VolumeTrend  — recent-5-day avg volume vs. 20-day avg volume
    /// </remarks>
    public class AnalysisService : IAnalysisService
    {
        public Task<AnalysisResult> AnalyzeAsync(
            Stock stock,
            IReadOnlyList<StockQuote> history,
            ScanContext context)
        {
            var result = AnalyzeCore(stock.Symbol, history, context);

            if (history.Count == 0)
                return Task.FromResult(result);

            // The analog-matching estimate is O(history²) — the backtest engine replays
            // thousands of analyses and computes real outcome stats instead, so it opts out.
            if (!context.SkipTargetEstimate)
                AppendTargetEstimate(result, stock.Symbol, history, context);
            return Task.FromResult(result);
        }

        private static AnalysisResult AnalyzeCore(
            string symbol,
            IReadOnlyList<StockQuote> history,
            ScanContext context)
        {
            var result = new AnalysisResult { Symbol = symbol };

            if (history.Count == 0)
            {
                result.Signals.Add("No price history available — check data feed.");
                return result;
            }

            // ── Compute shared indicators ─────────────────────────────────────────
            var closes = history.Select(q => (double)q.Close).ToArray();
            var volumes = history.Select(q => (double)q.Volume).ToArray();

            double? sma20 = Sma(closes, 20);
            double? sma50 = Sma(closes, 50);
            double  rsi14 = Rsi(closes, 14);
            double  last  = closes[^1];
            double  first = closes[0];

            double weekReturn = first != 0
                ? ((last - first) / first) * 100.0
                : 0.0;

            double volTrend = VolumeTrend(volumes, recentDays: 5, baselineDays: 20);

            // Store indicator readings
            if (sma20.HasValue) result.Indicators["SMA20"]     = Math.Round(sma20.Value, 2);
            if (sma50.HasValue) result.Indicators["SMA50"]     = Math.Round(sma50.Value, 2);
            result.Indicators["RSI14"]       = Math.Round(rsi14,      1);
            result.Indicators["WeekReturn%"] = Math.Round(weekReturn,  2);
            result.Indicators["VolumeTrend"] = Math.Round(volTrend,    2);
            result.Indicators["LastClose"]   = Math.Round(last,        2);

            // ── Dispatch to strategy-specific scorer ──────────────────────────────
            result.Score = context.Strategy.Id switch
            {
                "mean-reversion" => ScoreMeanReversion(closes, sma20, sma50, rsi14, weekReturn, result),
                "breakout"       => ScoreBreakout(closes, volumes, sma20, rsi14, volTrend, result),
                "buy-and-hold"   => ScoreBuyAndHold(closes, sma20, sma50, rsi14, weekReturn, result),
                "value"          => ScoreValue(symbol, closes, context, result),
                "52w-high"       => Score52WeekHigh(history, closes, rsi14, weekReturn, volTrend, result),
                _                => ScoreMomentum(weekReturn, sma20, sma50, rsi14, volTrend, result),
            };

            return result;
        }

        // ── Strategy scorers ──────────────────────────────────────────────────────

        /// <summary>
        /// Momentum: rewards stocks moving strongly in one direction with rising volume.
        /// Score is driven primarily by recent % return.
        /// </summary>
        private static double ScoreMomentum(
            double weekReturn, double? sma20, double? sma50,
            double rsi14, double volTrend,
            AnalysisResult result)
        {
            double score = 0;

            // Return component — 5% weekly move ≈ score ±1.0
            double returnComponent = weekReturn / 5.0;
            score += Math.Clamp(returnComponent, -3.0, 3.0);

            if (weekReturn > 3.0)  result.Signals.Add($"Strong weekly gain of {weekReturn:+0.##;-0.##}%");
            else if (weekReturn > 0) result.Signals.Add($"Positive week: {weekReturn:+0.##}%");
            else if (weekReturn < -3.0) result.Signals.Add($"Sharp weekly loss of {weekReturn:+0.##;-0.##}%");
            else result.Signals.Add($"Weekly change: {weekReturn:+0.##;-0.##}%");

            // Trend confirmation via SMAs
            if (sma20.HasValue)
            {
                double last = result.Indicators["LastClose"];
                if (last > sma20.Value)
                {
                    score += 0.4;
                    result.Signals.Add($"Price above SMA20 ({sma20.Value:F2})");
                }
                else
                {
                    score -= 0.3;
                    result.Signals.Add($"Price below SMA20 ({sma20.Value:F2})");
                }
            }

            if (sma50.HasValue)
            {
                double last = result.Indicators["LastClose"];
                if (last > sma50.Value)
                {
                    score += 0.3;
                    result.Signals.Add($"Price above SMA50 ({sma50.Value:F2})");
                }
                else
                {
                    score -= 0.3;
                    result.Signals.Add($"Price below SMA50 ({sma50.Value:F2})");
                }
            }

            // RSI momentum
            if (rsi14 > 70) { score += 0.5; result.Signals.Add($"RSI14 overbought ({rsi14:F1}) — strong momentum"); }
            else if (rsi14 > 55) { score += 0.2; result.Signals.Add($"RSI14 bullish ({rsi14:F1})"); }
            else if (rsi14 < 30) { score -= 0.5; result.Signals.Add($"RSI14 oversold ({rsi14:F1}) — weak momentum"); }
            else if (rsi14 < 45) { score -= 0.2; result.Signals.Add($"RSI14 bearish ({rsi14:F1})"); }

            // Volume confirmation
            if (volTrend > 1.3)       { score += 0.3; result.Signals.Add($"Volume surge ({volTrend:P0} of baseline)"); }
            else if (volTrend < 0.7)  { score -= 0.2; result.Signals.Add($"Light volume ({volTrend:P0} of baseline)"); }

            return Math.Round(score, 3);
        }

        /// <summary>
        /// Mean-reversion: rewards oversold stocks trading well below their moving averages,
        /// expecting a snap-back to the mean.
        /// </summary>
        private static double ScoreMeanReversion(
            double[] closes, double? sma20, double? sma50,
            double rsi14, double weekReturn,
            AnalysisResult result)
        {
            double score = 0;
            double last  = closes[^1];

            // Primary signal: distance below SMA20
            if (sma20.HasValue)
            {
                double pct = ((last - sma20.Value) / sma20.Value) * 100.0;
                result.Indicators["PctFromSMA20"] = Math.Round(pct, 2);

                if (pct < -5.0)
                {
                    score += 2.5; // well oversold relative to mean → strong buy signal
                    result.Signals.Add($"Price {-pct:F1}% below SMA20 — deep oversold");
                }
                else if (pct < -2.0)
                {
                    score += 1.2;
                    result.Signals.Add($"Price {-pct:F1}% below SMA20 — oversold");
                }
                else if (pct > 5.0)
                {
                    score -= 2.0; // too extended above mean → sell signal
                    result.Signals.Add($"Price {pct:F1}% above SMA20 — extended, reversion risk");
                }
                else if (pct > 2.0)
                {
                    score -= 0.8;
                    result.Signals.Add($"Price {pct:F1}% above SMA20 — slightly extended");
                }
            }

            // RSI: oversold is a buy, overbought is a sell
            if (rsi14 < 30)
            {
                score += 1.0;
                result.Signals.Add($"RSI14 oversold ({rsi14:F1}) — reversion candidate");
            }
            else if (rsi14 < 40)
            {
                score += 0.5;
                result.Signals.Add($"RSI14 weak ({rsi14:F1}) — approaching oversold");
            }
            else if (rsi14 > 70)
            {
                score -= 1.0;
                result.Signals.Add($"RSI14 overbought ({rsi14:F1}) — reversion risk");
            }

            // A sharp recent drop is a buy signal for mean-reversion
            if (weekReturn < -4.0)
            {
                score += 0.8;
                result.Signals.Add($"Sharp drop {weekReturn:+0.##;-0.##}% — potential snap-back");
            }
            else if (weekReturn > 4.0)
            {
                score -= 0.6;
                result.Signals.Add($"Strong rise {weekReturn:+0.##;-0.##}% — extended, watch for fade");
            }

            // Bollinger refinement: a close below the lower band is a *statistical*
            // stretch (2σ), stronger evidence than raw %-below-SMA20 alone — the band
            // widens in volatile names, so a fixed % threshold over-fires on them.
            var (bbMid, bbUpper, bbLower, bbWidthPct) = Bollinger(closes);
            if (bbLower.HasValue && bbMid.HasValue)
            {
                double last2 = closes[^1];
                if (last2 < bbLower.Value)
                {
                    score += 1.0;
                    result.Signals.Add("Close below lower Bollinger band (2σ) — statistically stretched");
                }
                else if (last2 < bbLower.Value + 0.25 * (bbMid.Value - bbLower.Value))
                {
                    score += 0.5;
                    result.Signals.Add("Close in the lower Bollinger quartile");
                }

                // A tight squeeze means nothing is stretched — reversion has no fuel.
                if (bbWidthPct is < 3.0)
                {
                    score -= 0.4;
                    result.Signals.Add($"Bollinger bands tight ({bbWidthPct:F1}%) — little stretch to revert");
                }
            }

            return Math.Round(score, 3);
        }

        /// <summary>
        /// Breakout: rewards stocks closing near their recent highs with a volume surge.
        /// </summary>
        private static double ScoreBreakout(
            double[] closes, double[] volumes,
            double? sma20, double rsi14, double volTrend,
            AnalysisResult result)
        {
            double score = 0;
            double last  = closes[^1];

            // Distance from recent high
            double high90 = closes.Max();
            double pctFromHigh = high90 != 0 ? ((last - high90) / high90) * 100.0 : 0;
            result.Indicators["PctFrom90DHigh"] = Math.Round(pctFromHigh, 2);

            if (pctFromHigh >= -1.0)
            {
                score += 2.5;
                result.Signals.Add($"Trading at/near {closes.Length}-day high — breakout zone");
            }
            else if (pctFromHigh >= -3.0)
            {
                score += 1.2;
                result.Signals.Add($"Within 3% of {closes.Length}-day high ({pctFromHigh:+0.##;-0.##}%)");
            }
            else if (pctFromHigh < -15.0)
            {
                score -= 1.5;
                result.Signals.Add($"{-pctFromHigh:F0}% below recent high — no breakout setup");
            }
            else
            {
                result.Signals.Add($"{-pctFromHigh:F1}% below {closes.Length}-day high");
            }

            // Volume surge confirms breakout
            if (volTrend > 1.5)
            {
                score += 1.0;
                result.Signals.Add($"Volume surge {volTrend:P0} of baseline — institutional buying");
            }
            else if (volTrend > 1.2)
            {
                score += 0.5;
                result.Signals.Add($"Above-average volume ({volTrend:P0} of baseline)");
            }
            else if (volTrend < 0.8)
            {
                score -= 0.5;
                result.Signals.Add("Light volume — breakout lacks conviction");
            }

            // Price above SMA20 is healthy for breakout
            if (sma20.HasValue && last > sma20.Value)
            {
                score += 0.4;
                result.Signals.Add($"Price above SMA20 ({sma20.Value:F2}) — uptrend intact");
            }

            // RSI
            if (rsi14 > 60 && rsi14 < 80)
            {
                score += 0.3;
                result.Signals.Add($"RSI14 in bullish range ({rsi14:F1})");
            }
            else if (rsi14 >= 80)
            {
                score -= 0.3;
                result.Signals.Add($"RSI14 extremely overbought ({rsi14:F1}) — breakout may be extended");
            }

            // Bollinger refinement: the highest-quality breakouts fire out of a volatility
            // SQUEEZE (bands pinched vs their recent norm) — a coiled spring releasing —
            // rather than out of an already-loose, choppy range.
            var (_, bbUpper, _, bbWidthPct) = Bollinger(closes);
            if (bbUpper.HasValue && bbWidthPct.HasValue && closes.Length >= 40)
            {
                // Average band width over the prior ~40 bars as the "normal" width.
                double priorWidthSum = 0; int priorN = 0;
                for (int end = closes.Length - 5; end >= 20 && priorN < 8; end -= 5)
                {
                    var (_, _, _, w) = Bollinger(closes[..end]);
                    if (w.HasValue) { priorWidthSum += w.Value; priorN++; }
                }
                double? normalWidth = priorN > 0 ? priorWidthSum / priorN : null;

                bool aboveUpper = last > bbUpper.Value;
                bool squeezed   = normalWidth.HasValue && bbWidthPct.Value < normalWidth.Value * 0.65;

                if (squeezed && aboveUpper)
                {
                    score += 1.0;
                    result.Signals.Add($"Bollinger squeeze fired — bands {bbWidthPct:F1}% vs normal {normalWidth:F1}%, close above upper band");
                }
                else if (aboveUpper)
                {
                    score += 0.4;
                    result.Signals.Add("Close above upper Bollinger band — expansion underway");
                }
                else if (squeezed)
                {
                    result.Signals.Add($"Bollinger squeeze building ({bbWidthPct:F1}% vs normal {normalWidth:F1}%) — watch for the break");
                }
            }

            return Math.Round(score, 3);
        }

        /// <summary>
        /// Buy &amp; Hold: rewards fundamentally-steady names in a durable long-term uptrend —
        /// price above its long moving average with the trend aligned (SMA20 &gt; SMA50),
        /// a healthy non-extreme RSI, and steady (rather than parabolic) appreciation.
        /// Built for multi-month/year holds, so it prizes stability over short-term thrust:
        /// it deliberately fades blow-off runs and deep selloffs that a long-term holder
        /// shouldn't initiate into.
        /// </summary>
        private static double ScoreBuyAndHold(
            double[] closes, double? sma20, double? sma50,
            double rsi14, double weekReturn,
            AnalysisResult result)
        {
            double score = 0;
            double last  = closes[^1];

            // ── Long-term trend: the core of a buy-and-hold thesis ────────────────
            if (sma50.HasValue)
            {
                double pctFromSma50 = ((last - sma50.Value) / sma50.Value) * 100.0;
                result.Indicators["PctFromSMA50"] = Math.Round(pctFromSma50, 2);

                if (last > sma50.Value)
                {
                    score += 1.5;
                    result.Signals.Add($"Price {pctFromSma50:+0.##}% above SMA50 ({sma50.Value:F2}) — long-term uptrend");
                }
                else
                {
                    score -= 1.5;
                    result.Signals.Add($"Price {pctFromSma50:+0.##;-0.##}% below SMA50 ({sma50.Value:F2}) — long-term downtrend");
                }
            }

            // Trend alignment: short MA above long MA confirms a sustained advance.
            if (sma20.HasValue && sma50.HasValue)
            {
                if (sma20.Value > sma50.Value)
                {
                    score += 1.0;
                    result.Signals.Add("SMA20 above SMA50 — trend aligned for accumulation");
                }
                else
                {
                    score -= 0.8;
                    result.Signals.Add("SMA20 below SMA50 — trend not yet aligned");
                }
            }

            // ── Healthy, non-extreme RSI: steady advance, not a blow-off or a falling knife ──
            if (rsi14 >= 45 && rsi14 <= 70)
            {
                score += 0.6;
                result.Signals.Add($"RSI14 healthy ({rsi14:F1}) — steady momentum");
            }
            else if (rsi14 > 78)
            {
                score -= 0.6;
                result.Signals.Add($"RSI14 overbought ({rsi14:F1}) — poor long-term entry");
            }
            else if (rsi14 < 30)
            {
                score -= 0.8;
                result.Signals.Add($"RSI14 oversold ({rsi14:F1}) — possible thesis break");
            }

            // ── Steady appreciation over the lookback window — reward durable, modest
            //    gains; fade parabolic spikes and extended declines a long-term holder
            //    shouldn't chase into. ──
            if (weekReturn > 0 && weekReturn <= 15.0)
            {
                score += 0.5;
                result.Signals.Add($"Steady appreciation ({weekReturn:+0.##}% over window)");
            }
            else if (weekReturn > 25.0)
            {
                score -= 0.5;
                result.Signals.Add($"Parabolic run ({weekReturn:+0.##}%) — wait for a calmer entry");
            }
            else if (weekReturn < -10.0)
            {
                score -= 0.5;
                result.Signals.Add($"Extended decline ({weekReturn:+0.##;-0.##}%) — thesis at risk");
            }

            // Low day-to-day volatility is a plus for a multi-year hold.
            double vol = Volatility(closes);
            if (vol > 0)
            {
                result.Indicators["Volatility%"] = Math.Round(vol, 2);
                if (vol < 2.0)
                {
                    score += 0.4;
                    result.Signals.Add($"Low volatility ({vol:F1}%/day) — stable holding");
                }
                else if (vol > 4.0)
                {
                    score -= 0.3;
                    result.Signals.Add($"High volatility ({vol:F1}%/day) — choppy for a long hold");
                }
            }

            return Math.Round(score, 3);
        }

        /// <summary>
        /// Value: buys statistically cheap stocks — low P/E and P/B with positive
        /// earnings and an income cushion. The only fundamental (non-price-action)
        /// strategy; requires <see cref="ScanContext.Summaries"/>.
        /// NOT point-in-time backtestable: fundamentals are a TODAY-only snapshot,
        /// so replaying it against historical prices would leak future information.
        /// </summary>
        private static double ScoreValue(
            string symbol, double[] closes, ScanContext context, AnalysisResult result)
        {
            QuoteSummary? qs = null;
            context.Summaries?.TryGetValue(symbol, out qs);
            if (qs == null)
            {
                result.Signals.Add("No fundamental data available — Value strategy needs live quote summaries (run a scan).");
                return 0;
            }

            double score = 0;

            // Earnings quality first: negative earnings disqualify "cheap" — it's just cheap.
            if (qs.EPS is double eps)
            {
                if (eps <= 0)
                {
                    score -= 1.5;
                    result.Signals.Add($"Negative earnings (EPS {eps:F2}) — cheapness is a warning, not a bargain");
                }
                else
                {
                    score += 0.3;
                    result.Signals.Add($"Profitable (EPS ${eps:F2})");
                }
            }

            // Price / Earnings
            if (qs.PERatio is double pe && pe > 0)
            {
                result.Indicators["P/E"] = Math.Round(pe, 1);
                if      (pe < 10) { score += 1.5; result.Signals.Add($"Deep value P/E {pe:F1}"); }
                else if (pe < 15) { score += 1.0; result.Signals.Add($"Attractive P/E {pe:F1}"); }
                else if (pe < 20) { score += 0.4; result.Signals.Add($"Reasonable P/E {pe:F1}"); }
                else if (pe > 35) { score -= 1.0; result.Signals.Add($"Expensive P/E {pe:F1}"); }
            }

            // Improving earnings: forward P/E below trailing means estimates are rising.
            if (qs.PERatio is double trailing and > 0 && qs.ForwardPE is double fwd and > 0 && fwd < trailing)
            {
                score += 0.5;
                result.Signals.Add($"Forward P/E {fwd:F1} < trailing {trailing:F1} — earnings expected to grow");
            }

            // Price / Book
            if (qs.PriceToBook is double pb && pb > 0)
            {
                result.Indicators["P/B"] = Math.Round(pb, 2);
                if      (pb < 1.0) { score += 1.0; result.Signals.Add($"Below book value (P/B {pb:F2})"); }
                else if (pb < 2.0) { score += 0.5; result.Signals.Add($"Modest P/B {pb:F2}"); }
                else if (pb > 6.0) { score -= 0.5; result.Signals.Add($"Rich P/B {pb:F2}"); }
            }

            // Income cushion while waiting for the rerating.
            if (qs.DividendYieldPct is double dy && dy > 0)
            {
                result.Indicators["DivYield%"] = Math.Round(dy, 2);
                if      (dy >= 4.0) { score += 0.7; result.Signals.Add($"Strong dividend yield {dy:F1}%"); }
                else if (dy >= 2.0) { score += 0.5; result.Signals.Add($"Dividend yield {dy:F1}%"); }
            }

            // Value-trap guard: extreme volatility usually means the market is pricing
            // real distress, not mispricing.
            double dailyVol = Volatility(closes);
            if (dailyVol > 4.0)
            {
                score -= 0.5;
                result.Signals.Add($"High volatility ({dailyVol:F1}%/day) — possible value trap");
            }
            else if (dailyVol > 0 && dailyVol < 2.0)
            {
                score += 0.3;
                result.Signals.Add($"Stable price action ({dailyVol:F1}%/day)");
            }

            return Math.Round(score, 3);
        }

        /// <summary>
        /// 52-week high momentum: buys strength within a few percent of the yearly high —
        /// the documented anomaly (George &amp; Hwang 2004) that stocks near their 52-week
        /// high tend to keep outperforming (anchoring makes investors underreact to the
        /// news that pushed them there). Uses the fetched window's high when fewer than
        /// 252 bars are available, and says so.
        /// </summary>
        private static double Score52WeekHigh(
            IReadOnlyList<StockQuote> history, double[] closes,
            double rsi14, double weekReturn, double volTrend,
            AnalysisResult result)
        {
            double score = 0;
            double last  = closes[^1];

            int window = Math.Min(252, history.Count);
            double high = 0;
            for (int i = history.Count - window; i < history.Count; i++)
                high = Math.Max(high, (double)history[i].High);
            if (high <= 0) return 0;

            double pctFromHigh = ((last - high) / high) * 100.0;
            result.Indicators["PctFrom52wHigh"] = Math.Round(pctFromHigh, 2);
            string windowNote = window < 252 ? $" ({window}-day window)" : "";

            if (pctFromHigh >= -2.0)
            {
                score += 2.5;
                result.Signals.Add($"Within 2% of the 52-week high{windowNote} — strength begets strength");
            }
            else if (pctFromHigh >= -5.0)
            {
                score += 1.5;
                result.Signals.Add($"Within 5% of the 52-week high{windowNote}");
            }
            else if (pctFromHigh >= -10.0)
            {
                score += 0.5;
                result.Signals.Add($"{-pctFromHigh:F1}% below the 52-week high{windowNote}");
            }
            else if (pctFromHigh <= -30.0)
            {
                score -= 2.0;
                result.Signals.Add($"{-pctFromHigh:F0}% below the 52-week high{windowNote} — not this strategy's setup");
            }
            else
            {
                score -= 0.5;
                result.Signals.Add($"{-pctFromHigh:F1}% below the 52-week high{windowNote}");
            }

            // Confirmation: still advancing, not rolling over at the high.
            if      (weekReturn > 2.0)  { score += 0.5; result.Signals.Add($"Approaching on strength ({weekReturn:+0.##}% this window)"); }
            else if (weekReturn < -3.0) { score -= 0.5; result.Signals.Add($"Rolling over ({weekReturn:+0.##;-0.##}%) near the high"); }

            if (volTrend > 1.3) { score += 0.4; result.Signals.Add($"Volume expanding ({volTrend:P0} of baseline)"); }

            if      (rsi14 >= 55 && rsi14 <= 75) { score += 0.4; result.Signals.Add($"RSI14 in the momentum zone ({rsi14:F1})"); }
            else if (rsi14 > 85)                 { score -= 0.3; result.Signals.Add($"RSI14 stretched ({rsi14:F1})"); }

            return Math.Round(score, 3);
        }

        // ── Technical indicator math ──────────────────────────────────────────────

        /// <summary>
        /// Bollinger bands over the last <paramref name="period"/> bars:
        /// middle = SMA, upper/lower = ±<paramref name="k"/> standard deviations,
        /// width = (upper − lower) / middle as a percent. Nulls when insufficient data.
        /// </summary>
        private static (double? Mid, double? Upper, double? Lower, double? WidthPct)
            Bollinger(double[] closes, int period = 20, double k = 2.0)
        {
            if (closes.Length < period) return (null, null, null, null);

            double sum = 0;
            for (int i = closes.Length - period; i < closes.Length; i++) sum += closes[i];
            double mid = sum / period;

            double var = 0;
            for (int i = closes.Length - period; i < closes.Length; i++)
                var += (closes[i] - mid) * (closes[i] - mid);
            double sd = Math.Sqrt(var / period);

            double upper = mid + k * sd, lower = mid - k * sd;
            double? width = mid != 0 ? ((upper - lower) / mid) * 100.0 : (double?)null;
            return (mid, upper, lower, width);
        }

        /// <summary>Simple moving average of the last <paramref name="period"/> bars.</summary>
        private static double? Sma(double[] closes, int period)
        {
            if (closes.Length < period) return null;
            double sum = 0;
            for (int i = closes.Length - period; i < closes.Length; i++)
                sum += closes[i];
            return sum / period;
        }

        /// <summary>
        /// Wilder's RSI over the full series (shared implementation).
        /// Returns 50 if there are fewer bars than period+1.
        /// NOTE: the previous local version seeded on the LAST period changes, which
        /// meant the smoothing loop never ran — it was effectively a simple-average
        /// RSI despite the comment. The shared version seeds on the first period
        /// changes and smooths through the whole series, per Wilder's definition.
        /// </summary>
        private static double Rsi(double[] closes, int period)
            => Math.Round(Indicators.RsiWilder(closes, period), 1);

        /// <summary>
        /// Ratio of average volume over the last <paramref name="recentDays"/> bars
        /// to average volume over the last <paramref name="baselineDays"/> bars.
        /// Returns 1.0 if insufficient data.
        /// </summary>
        private static double VolumeTrend(double[] volumes, int recentDays, int baselineDays)
        {
            if (volumes.Length < baselineDays) return 1.0;

            double recent   = volumes.Skip(volumes.Length - recentDays)  .Average();
            double baseline = volumes.Skip(volumes.Length - baselineDays) .Average();

            return baseline == 0 ? 1.0 : recent / baseline;
        }

        private static void AppendTargetEstimate(
            AnalysisResult result,
            string symbol,
            IReadOnlyList<StockQuote> history,
            ScanContext context)
        {
            if (context.TargetProfitMarginPercent <= 0m || history.Count < 45)
                return;

            var currentBucket = SignalBucket(result.Score);
            if (currentBucket == 0)
                return;

            if (!result.Indicators.TryGetValue("RSI14", out var currentRsi) ||
                !result.Indicators.TryGetValue("WeekReturn%", out var currentWeekReturn) ||
                !result.Indicators.TryGetValue("VolumeTrend", out var currentVolTrend))
                return;

            var horizon = EstimateHorizon(context.Strategy.HoldingPeriod);
            var maxEndIndex = history.Count - horizon - 1;
            if (maxEndIndex < 20)
                return;

            var analogs = new List<(double Distance, int? HitDays)>();
            var step = maxEndIndex - 19 > 40 ? 2 : 1;
            for (int endIndex = 19; endIndex <= maxEndIndex; endIndex += step)
            {
                var candidateHistory = history.Take(endIndex + 1).ToList();
                var candidate = AnalyzeCore(symbol, candidateHistory, context);
                if (!candidate.Indicators.TryGetValue("RSI14", out var candidateRsi) ||
                    !candidate.Indicators.TryGetValue("WeekReturn%", out var candidateWeekReturn) ||
                    !candidate.Indicators.TryGetValue("VolumeTrend", out var candidateVolTrend))
                    continue;

                var distance = Math.Abs(candidate.Score - result.Score) / 1.25
                    + Math.Abs(candidateRsi - currentRsi) / 12.0
                    + Math.Abs(candidateWeekReturn - currentWeekReturn) / 4.0
                    + Math.Abs(candidateVolTrend - currentVolTrend) / 0.6;

                if (candidate.Indicators.TryGetValue("PctFromSMA20", out var candidatePctFromSma20) &&
                    result.Indicators.TryGetValue("PctFromSMA20", out var currentPctFromSma20))
                {
                    distance += Math.Abs(candidatePctFromSma20 - currentPctFromSma20) / 4.0;
                }

                if (candidate.Indicators.TryGetValue("PctFrom90DHigh", out var candidatePctFromHigh) &&
                    result.Indicators.TryGetValue("PctFrom90DHigh", out var currentPctFromHigh))
                {
                    distance += Math.Abs(candidatePctFromHigh - currentPctFromHigh) / 4.0;
                }

                if (SignalBucket(candidate.Score) != currentBucket)
                    distance += 1.5;

                analogs.Add((distance, FindHitDays(history, endIndex, horizon, currentBucket, (double)context.TargetProfitMarginPercent)));
            }

            const int minSample = 8;
            const int maxSample = 24;

            var sample = analogs
                .OrderBy(a => a.Distance)
                .Take(maxSample)
                .ToList();

            if (sample.Count < minSample)
                return;

            var hitDays = sample
                .Where(a => a.HitDays.HasValue)
                .Select(a => a.HitDays!.Value)
                .OrderBy(d => d)
                .ToList();

            result.TargetHitSampleSize = sample.Count;
            result.TargetHitProbability = (double)hitDays.Count / sample.Count;

            if (hitDays.Count == 0)
                return;

            result.ExpectedDaysToTarget = Math.Round(hitDays.Average(), 1);
            result.MedianDaysToTarget = hitDays.Count % 2 == 1
                ? hitDays[hitDays.Count / 2]
                : Math.Round((hitDays[hitDays.Count / 2 - 1] + hitDays[hitDays.Count / 2]) / 2.0, 1);
        }

        private static int SignalBucket(double score) => score switch
        {
            >= 2.0 => 2,
            >= 0.5 => 1,
            <= -2.0 => -2,
            <= -0.5 => -1,
            _ => 0,
        };

        private static int EstimateHorizon(HoldingPeriod period) => period switch
        {
            HoldingPeriod.Quick => 5,
            HoldingPeriod.Short => 20,
            HoldingPeriod.Long => 40,
            _ => 20,
        };

        private static int? FindHitDays(
            IReadOnlyList<StockQuote> history,
            int endIndex,
            int horizon,
            int signalBucket,
            double targetPercent)
        {
            var entry = (double)history[endIndex].Close;
            if (entry <= 0)
                return null;

            bool isLong = signalBucket > 0;
            var targetMultiplier = isLong ? 1.0 + (targetPercent / 100.0) : 1.0 - (targetPercent / 100.0);
            var targetPrice = entry * targetMultiplier;

            for (int offset = 1; offset <= horizon; offset++)
            {
                var future = history[endIndex + offset];
                if (isLong && (double)future.High >= targetPrice)
                    return offset;
                if (!isLong && (double)future.Low <= targetPrice)
                    return offset;
            }

            return null;
        }

        /// <summary>
        /// Daily-return volatility as a percent — the standard deviation of
        /// close-to-close % changes. Returns 0 if there are fewer than 2 bars.
        /// </summary>
        private static double Volatility(double[] closes)
        {
            if (closes.Length < 2) return 0;

            var returns = new double[closes.Length - 1];
            for (int i = 1; i < closes.Length; i++)
                returns[i - 1] = closes[i - 1] == 0 ? 0 : ((closes[i] - closes[i - 1]) / closes[i - 1]) * 100.0;

            double mean = returns.Average();
            double sumSq = 0;
            foreach (var r in returns) sumSq += (r - mean) * (r - mean);

            return Math.Sqrt(sumSq / returns.Length);
        }
    }
}
