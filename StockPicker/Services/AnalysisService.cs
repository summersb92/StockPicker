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

            return Math.Round(score, 3);
        }

        // ── Technical indicator math ──────────────────────────────────────────────

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
        /// Wilder's RSI over the last <paramref name="period"/> bars.
        /// Returns 50 if there are fewer bars than period+1.
        /// </summary>
        private static double Rsi(double[] closes, int period)
        {
            if (closes.Length < period + 1) return 50.0;

            double avgGain = 0, avgLoss = 0;

            // Initial averages over the first [period] changes
            int start = closes.Length - period - 1;
            for (int i = start; i < start + period; i++)
            {
                double chg = closes[i + 1] - closes[i];
                if (chg > 0) avgGain += chg;
                else         avgLoss += -chg;
            }
            avgGain /= period;
            avgLoss /= period;

            // Smooth subsequent bars (Wilder smoothing)
            for (int i = start + period; i < closes.Length - 1; i++)
            {
                double chg = closes[i + 1] - closes[i];
                avgGain = (avgGain * (period - 1) + (chg > 0 ? chg : 0)) / period;
                avgLoss = (avgLoss * (period - 1) + (chg < 0 ? -chg : 0)) / period;
            }

            if (avgLoss == 0) return 100;
            double rs = avgGain / avgLoss;
            return Math.Round(100.0 - (100.0 / (1 + rs)), 1);
        }

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
    }
}
