using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using StockPicker.Models;

namespace StockPicker.Services
{
    /// <summary>
    /// Scores every stock in the universe for intraday trading viability.
    /// Supports four strategies: Momentum, MeanReversion, Breakout, EarningsPlay.
    /// </summary>
    public class DayPickService : IDayPickService
    {
        private const int    MaxPicks     = 10;
        private const int    MinPicks     = 5;
        private const double MaxScore     = 9.5;   // theoretical max from all scoring components — used to normalize Confidence
        private const double AtrMultStop  = 1.5;
        private const double AtrMultTarget= 2.5;

        public Task<IReadOnlyList<DayPick>> GenerateAsync(
            IReadOnlyList<Stock>                                       universe,
            IReadOnlyDictionary<string, IReadOnlyList<StockQuote>>     history,
            IReadOnlyDictionary<string, QuoteSummary>                  summaries,
            IReadOnlyDictionary<string, (string Name, string Sector)>? nameLookup,
            DayPickStrategy strategy = DayPickStrategy.Momentum)
        {
            double minScore = strategy switch
            {
                DayPickStrategy.MeanReversion => 1.0,
                DayPickStrategy.EarningsPlay  => 1.0,
                _                             => 1.5,
            };

            var candidates = new List<DayPick>(universe.Count);

            foreach (var stock in universe)
            {
                history.TryGetValue(stock.Symbol, out var bars);
                summaries.TryGetValue(stock.Symbol, out var quote);

                if (bars == null || bars.Count < 15 || quote == null) continue;

                var pick = ScoreStock(stock, bars, quote, strategy, minScore);
                if (pick == null) continue;

                EnrichName(pick, stock, quote, nameLookup);
                candidates.Add(pick);
            }

            var sorted = candidates
                .OrderByDescending(p => p.IntraDayScore)
                .Take(MaxPicks)
                .ToList();

            // Fallback: always surface at least MinPicks
            if (sorted.Count < MinPicks)
            {
                var fallback = new List<DayPick>(universe.Count);
                foreach (var stock in universe)
                {
                    if (sorted.Any(p => p.Symbol.Equals(stock.Symbol, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    history.TryGetValue(stock.Symbol, out var bars);
                    summaries.TryGetValue(stock.Symbol, out var quote);
                    if (bars == null || bars.Count < 2 || quote == null) continue;

                    var pick = ScoreStock(stock, bars, quote, strategy, minScoreOverride: 0);
                    if (pick == null) continue;

                    EnrichName(pick, stock, quote, nameLookup);
                    fallback.Add(pick);
                }

                var needed = MinPicks - sorted.Count;
                sorted.AddRange(fallback.OrderByDescending(p => p.IntraDayScore).Take(needed));
                sorted = sorted.OrderByDescending(p => p.IntraDayScore).ToList();
            }

            return Task.FromResult<IReadOnlyList<DayPick>>(sorted);
        }

        // ── Name enrichment helper ────────────────────────────────────────────

        private static void EnrichName(
            DayPick pick, Stock stock, QuoteSummary quote,
            IReadOnlyDictionary<string, (string Name, string Sector)>? nameLookup)
        {
            if (nameLookup != null && nameLookup.TryGetValue(stock.Symbol, out var info))
            { pick.CompanyName = info.Name; pick.Sector = info.Sector; }
            else
            { pick.CompanyName = stock.Name; pick.Sector = stock.Sector; }

            if (!string.IsNullOrWhiteSpace(quote.LongName))
                pick.CompanyName = quote.LongName;
        }

        // ── Strategy dispatcher ───────────────────────────────────────────────

        private static DayPick? ScoreStock(
            Stock stock,
            IReadOnlyList<StockQuote> bars,
            QuoteSummary quote,
            DayPickStrategy strategy,
            double minScoreOverride = -1)
        {
            return strategy switch
            {
                DayPickStrategy.MeanReversion => ScoreMeanReversion(stock, bars, quote, minScoreOverride),
                DayPickStrategy.Breakout      => ScoreBreakout(stock, bars, quote, minScoreOverride),
                DayPickStrategy.EarningsPlay  => ScoreEarningsPlay(stock, bars, quote, minScoreOverride),
                _                             => ScoreMomentum(stock, bars, quote, minScoreOverride),
            };
        }

        // ── Strategy 1: Momentum (original) ──────────────────────────────────

        private static DayPick? ScoreMomentum(
            Stock stock, IReadOnlyList<StockQuote> bars, QuoteSummary quote,
            double minScore)
        {
            var (closes, highs, lows, volumes, opens) = ExtractArrays(bars);
            double lastClose = closes[^1], prevClose = closes[^2], todayOpen = opens[^1];
            if (lastClose <= 0 || prevClose <= 0) return null;

            double gapPct      = prevClose != 0 ? ((todayOpen - prevClose) / prevClose) * 100.0 : 0;
            double atr14       = ComputeAtr(closes, highs, lows, 14);
            double atrPct      = lastClose > 0 ? (atr14 / lastClose) * 100.0 : 0;
            if (atrPct < 0.5) return null;

            double rsi14       = ComputeRsi(closes, 14);
            double volumeRatio = CalcVolumeRatio(quote, volumes);
            decimal lastPrice  = quote.Price ?? (decimal)lastClose;
            double dayChangePct= quote.DayChangePct ?? 0.0;

            double score = 0; var reasons = new List<string>(6);

            // Volume surge (0–3)
            if      (volumeRatio >= 3.0) { score += 3.0; reasons.Add($"Volume surge {volumeRatio:F1}×"); }
            else if (volumeRatio >= 2.0) { score += 2.0; reasons.Add($"High volume {volumeRatio:F1}×"); }
            else if (volumeRatio >= 1.5) { score += 1.5; reasons.Add($"Volume {volumeRatio:F1}× avg"); }
            else if (volumeRatio >= 1.2) { score += 0.8; reasons.Add($"Above-avg vol {volumeRatio:F1}×"); }

            // Gap (0–2)
            double absGap = Math.Abs(gapPct);
            if      (absGap >= 5.0) { score += 2.0; reasons.Add($"Large gap {gapPct:+0.##;-0.##}%"); }
            else if (absGap >= 3.0) { score += 1.5; reasons.Add($"Gap {gapPct:+0.##;-0.##}%"); }
            else if (absGap >= 1.5) { score += 1.0; reasons.Add($"Gap {gapPct:+0.##;-0.##}%"); }
            else if (absGap >= 0.5) { score += 0.5; reasons.Add($"Small gap {gapPct:+0.##;-0.##}%"); }

            // Intraday move (0–2)
            double absDayChg = Math.Abs(dayChangePct);
            if      (absDayChg >= 5.0) { score += 2.0; reasons.Add($"Strong move {dayChangePct:+0.##;-0.##}%"); }
            else if (absDayChg >= 3.0) { score += 1.5; reasons.Add($"Solid move {dayChangePct:+0.##;-0.##}%"); }
            else if (absDayChg >= 2.0) { score += 1.0; reasons.Add($"Move {dayChangePct:+0.##;-0.##}%"); }
            else if (absDayChg >= 1.0) { score += 0.5; }

            // ATR volatility (0–1.5)
            if      (atrPct >= 4.0) { score += 1.5; reasons.Add($"High ATR {atrPct:F1}%"); }
            else if (atrPct >= 2.5) { score += 1.0; reasons.Add($"ATR {atrPct:F1}%"); }
            else if (atrPct >= 1.5) { score += 0.5; }

            // RSI quality (0–1)
            bool intendLong = dayChangePct >= 0;
            if (intendLong)
            {
                if      (rsi14 >= 55 && rsi14 <= 75) { score += 1.0; reasons.Add($"RSI {rsi14:F0} (momentum)"); }
                else if (rsi14 >= 45 && rsi14 <  55) { score += 0.5; }
                else if (rsi14 < 35)                  { score += 0.8; reasons.Add($"RSI {rsi14:F0} (oversold)"); }
            }
            else
            {
                if      (rsi14 >= 65 && rsi14 <= 80) { score += 1.0; reasons.Add($"RSI {rsi14:F0} (extended)"); }
                else if (rsi14 >  80)                 { score += 0.8; reasons.Add($"RSI {rsi14:F0} (overbought)"); }
                else if (rsi14 >= 55 && rsi14 <  65) { score += 0.5; }
            }

            if (score < minScore) return null;

            var direction = DirectionFromChange(dayChangePct, gapPct);
            return BuildPick(stock, lastPrice, quote.DayOpen, atr14, atrPct, gapPct, rsi14, volumeRatio,
                             dayChangePct, direction, score, reasons);
        }

        // ── Strategy 2: Mean Reversion ────────────────────────────────────────

        private static DayPick? ScoreMeanReversion(
            Stock stock, IReadOnlyList<StockQuote> bars, QuoteSummary quote,
            double minScore)
        {
            var (closes, highs, lows, volumes, opens) = ExtractArrays(bars);
            double lastClose = closes[^1], prevClose = closes[^2];
            if (lastClose <= 0 || prevClose <= 0) return null;

            double atr14       = ComputeAtr(closes, highs, lows, 14);
            double atrPct      = lastClose > 0 ? (atr14 / lastClose) * 100.0 : 0;
            if (atrPct < 0.3) return null;

            double rsi14       = ComputeRsi(closes, 14);
            double sma20       = closes.Length >= 20 ? closes.Skip(closes.Length - 20).Average() : lastClose;
            double volumeRatio = CalcVolumeRatio(quote, volumes);
            decimal lastPrice  = quote.Price ?? (decimal)lastClose;
            double dayChangePct= quote.DayChangePct ?? 0.0;

            // Mean reversion only wants beaten-down stocks
            if (rsi14 > 45) return null;

            double score = 0; var reasons = new List<string>(6);

            // Oversold RSI (0–3)
            if      (rsi14 < 20) { score += 3.0; reasons.Add($"RSI {rsi14:F0} (extreme oversold)"); }
            else if (rsi14 < 25) { score += 2.5; reasons.Add($"RSI {rsi14:F0} (very oversold)"); }
            else if (rsi14 < 30) { score += 2.0; reasons.Add($"RSI {rsi14:F0} (oversold)"); }
            else if (rsi14 < 35) { score += 1.5; reasons.Add($"RSI {rsi14:F0} (near oversold)"); }
            else if (rsi14 < 40) { score += 0.8; reasons.Add($"RSI {rsi14:F0} (weak)"); }
            else                 { score += 0.3; }

            // Price below SMA20 (0–2)
            double pctBelowSma = sma20 > 0 ? ((sma20 - lastClose) / sma20) * 100.0 : 0;
            if      (pctBelowSma >= 8.0) { score += 2.0; reasons.Add($"{pctBelowSma:F1}% below SMA20"); }
            else if (pctBelowSma >= 5.0) { score += 1.5; reasons.Add($"{pctBelowSma:F1}% below SMA20"); }
            else if (pctBelowSma >= 3.0) { score += 1.0; reasons.Add($"{pctBelowSma:F1}% below SMA20"); }
            else if (pctBelowSma >= 1.5) { score += 0.5; }
            else return null; // not actually below SMA20 — skip

            // Volume drying up = low sell pressure, potential reversal (0–1)
            if      (volumeRatio < 0.5) { score += 1.0; reasons.Add("Volume drying up"); }
            else if (volumeRatio < 0.8) { score += 0.5; reasons.Add("Low volume"); }

            // Today's candle — small loss or early bounce is better (0–1)
            if      (dayChangePct > 0)              { score += 1.0; reasons.Add("Early bounce"); }
            else if (dayChangePct > -1.0)           { score += 0.5; }

            // ATR — need some range for the bounce (0–1)
            if      (atrPct >= 3.0) { score += 1.0; }
            else if (atrPct >= 1.5) { score += 0.5; }

            if (score < minScore) return null;

            return BuildPick(stock, lastPrice, quote.DayOpen, atr14, atrPct, 0, rsi14, volumeRatio,
                             dayChangePct, DayPickDirection.Long, score, reasons);
        }

        // ── Strategy 3: Breakout ──────────────────────────────────────────────

        private static DayPick? ScoreBreakout(
            Stock stock, IReadOnlyList<StockQuote> bars, QuoteSummary quote,
            double minScore)
        {
            var (closes, highs, lows, volumes, opens) = ExtractArrays(bars);
            double lastClose = closes[^1], todayHigh = highs[^1];
            if (lastClose <= 0) return null;

            double atr14       = ComputeAtr(closes, highs, lows, 14);
            double atrPct      = lastClose > 0 ? (atr14 / lastClose) * 100.0 : 0;
            if (atrPct < 0.5) return null;

            double rsi14       = ComputeRsi(closes, 14);
            double volumeRatio = CalcVolumeRatio(quote, volumes);
            decimal lastPrice  = quote.Price ?? (decimal)lastClose;
            double dayChangePct= quote.DayChangePct ?? 0.0;

            // 20-day high (resistance level)
            int window = Math.Min(20, closes.Length - 1);
            double high20 = highs.Skip(highs.Length - 1 - window).Take(window).Max();

            double score = 0; var reasons = new List<string>(6);

            // Breaking above 20-day high (0–3)
            if (todayHigh >= high20)
            {
                double breakPct = ((todayHigh - high20) / high20) * 100.0;
                if      (breakPct >= 3.0) { score += 3.0; reasons.Add($"Breaking out +{breakPct:F1}% above 20d high"); }
                else if (breakPct >= 1.5) { score += 2.5; reasons.Add($"Breaking out +{breakPct:F1}% above 20d high"); }
                else if (breakPct >= 0.5) { score += 2.0; reasons.Add("Testing 20d high breakout"); }
                else                      { score += 1.5; reasons.Add("At 20d high"); }
            }
            else
            {
                double pctFromHigh = ((high20 - todayHigh) / high20) * 100.0;
                if (pctFromHigh > 3.0) return null; // too far from breakout level
                score += 0.8; reasons.Add($"{pctFromHigh:F1}% from 20d breakout");
            }

            // Volume confirmation (0–2)
            if      (volumeRatio >= 2.5) { score += 2.0; reasons.Add($"Vol {volumeRatio:F1}× confirms breakout"); }
            else if (volumeRatio >= 1.8) { score += 1.5; reasons.Add($"Vol {volumeRatio:F1}× above avg"); }
            else if (volumeRatio >= 1.3) { score += 1.0; reasons.Add($"Vol {volumeRatio:F1}× avg"); }
            else                         { score -= 1.0; } // breakout on light vol = suspect

            // RSI — momentum zone preferred for breakouts (0–1)
            if      (rsi14 >= 55 && rsi14 <= 70) { score += 1.0; reasons.Add($"RSI {rsi14:F0} (momentum)"); }
            else if (rsi14 >= 45 && rsi14 <  55) { score += 0.5; }
            else if (rsi14 > 75)                  { score -= 0.5; reasons.Add($"RSI {rsi14:F0} (stretched)"); }

            // Intraday strength (0–1)
            if      (dayChangePct >= 3.0) { score += 1.0; reasons.Add($"Up {dayChangePct:F1}% today"); }
            else if (dayChangePct >= 1.5) { score += 0.5; }
            else if (dayChangePct <= 0)   { score -= 0.5; }

            if (score < minScore) return null;

            return BuildPick(stock, lastPrice, quote.DayOpen, atr14, atrPct, 0, rsi14, volumeRatio,
                             dayChangePct, DayPickDirection.Long, score, reasons);
        }

        // ── Strategy 4: Earnings Play ─────────────────────────────────────────

        private static DayPick? ScoreEarningsPlay(
            Stock stock, IReadOnlyList<StockQuote> bars, QuoteSummary quote,
            double minScore)
        {
            var (closes, highs, lows, volumes, opens) = ExtractArrays(bars);
            double lastClose = closes[^1];
            if (lastClose <= 0) return null;

            double atr14       = ComputeAtr(closes, highs, lows, 14);
            double atrPct      = lastClose > 0 ? (atr14 / lastClose) * 100.0 : 0;
            if (atrPct < 0.5) return null;

            double rsi14       = ComputeRsi(closes, 14);
            double volumeRatio = CalcVolumeRatio(quote, volumes);
            decimal lastPrice  = quote.Price ?? (decimal)lastClose;
            double dayChangePct= quote.DayChangePct ?? 0.0;

            double score = 0; var reasons = new List<string>(6);

            // Elevated implied volatility signals upcoming catalyst (0–3)
            // We approximate IV expectation via recent ATR expansion vs 20-day avg ATR
            int atrWindow = Math.Min(20, closes.Length - 1);
            double atrNow = atr14;
            double atrAvg = closes.Length >= 20
                ? ComputeAtr(closes.Take(closes.Length - 1).ToArray(),
                             highs.Take(highs.Length - 1).ToArray(),
                             lows.Take(lows.Length - 1).ToArray(), 14)
                : atrNow;
            double ivExpansion = atrAvg > 0 ? atrNow / atrAvg : 1.0;

            if      (ivExpansion >= 2.0) { score += 3.0; reasons.Add($"ATR {ivExpansion:F1}× expanded (high IV)"); }
            else if (ivExpansion >= 1.5) { score += 2.0; reasons.Add($"ATR {ivExpansion:F1}× expanded"); }
            else if (ivExpansion >= 1.2) { score += 1.0; reasons.Add($"ATR {ivExpansion:F1}× slightly expanded"); }
            else                         { score += 0.3; }

            // Volume surge — often precedes earnings (0–2)
            if      (volumeRatio >= 3.0) { score += 2.0; reasons.Add($"Volume surge {volumeRatio:F1}× (catalyst?)"); }
            else if (volumeRatio >= 2.0) { score += 1.5; reasons.Add($"High volume {volumeRatio:F1}×"); }
            else if (volumeRatio >= 1.5) { score += 1.0; reasons.Add($"Volume {volumeRatio:F1}× avg"); }

            // High ATR % = wide expected move (0–1.5)
            if      (atrPct >= 5.0) { score += 1.5; reasons.Add($"Wide ATR {atrPct:F1}%"); }
            else if (atrPct >= 3.0) { score += 1.0; reasons.Add($"ATR {atrPct:F1}%"); }
            else if (atrPct >= 2.0) { score += 0.5; }

            // Price momentum direction (0–0.5) — slight preference for bullish setup
            if (dayChangePct > 1.0) { score += 0.5; reasons.Add($"Up {dayChangePct:F1}% into event"); }

            if (score < minScore) return null;

            var direction = DirectionFromChange(dayChangePct, 0);
            return BuildPick(stock, lastPrice, quote.DayOpen, atr14, atrPct, 0, rsi14, volumeRatio,
                             dayChangePct, direction, score, reasons);
        }

        // ── Shared helpers ────────────────────────────────────────────────────

        private static DayPick BuildPick(
            Stock stock, decimal lastPrice, decimal? dayOpen,
            double atr14, double atrPct, double gapPct,
            double rsi14, double volumeRatio, double dayChangePct,
            DayPickDirection direction, double score, List<string> reasons)
        {
            decimal atrDec   = (decimal)atr14;
            decimal stopDist = atrDec * (decimal)AtrMultStop;
            decimal tgtDist  = atrDec * (decimal)AtrMultTarget;

            decimal stopLoss = direction == DayPickDirection.Long
                ? Math.Max(0.01m, lastPrice - stopDist)
                : lastPrice + stopDist;
            decimal target   = direction == DayPickDirection.Long
                ? lastPrice + tgtDist
                : Math.Max(0.01m, lastPrice - tgtDist);

            return new DayPick
            {
                Symbol        = stock.Symbol,
                Direction     = direction,
                EntryPrice    = lastPrice,
                LastPrice     = lastPrice,
                StopLoss      = Math.Round(stopLoss, 2),
                Target        = Math.Round(target, 2),
                IntraDayScore = Math.Round(score, 2),
                Confidence    = Math.Round(Math.Clamp(score / MaxScore, 0.0, 1.0), 3),
                VolumeRatio   = Math.Round(volumeRatio, 2),
                GapPct        = Math.Round(gapPct, 2),
                AtrPct        = Math.Round(atrPct, 2),
                RSI14         = Math.Round(rsi14, 1),
                DayOpen       = dayOpen,
                DayChangePct  = Math.Round(dayChangePct, 2),
                TriggerReason = reasons.Count > 0 ? string.Join(" | ", reasons) : "Composite signal",
                GeneratedAt   = DateTime.Now,
            };
        }

        private static DayPickDirection DirectionFromChange(double dayChangePct, double gapPct)
        {
            if (dayChangePct > 0.1)       return DayPickDirection.Long;
            if (dayChangePct < -0.1)      return DayPickDirection.Short;
            return gapPct >= 0 ? DayPickDirection.Long : DayPickDirection.Short;
        }

        private static double CalcVolumeRatio(QuoteSummary quote, double[] volumes)
        {
            if (quote.Volume > 0 && quote.AvgVolume > 0)
                return (double)quote.Volume / (double)quote.AvgVolume;
            return volumes.Length >= 20
                ? volumes.Skip(volumes.Length - 5).Average() /
                  Math.Max(1, volumes.Skip(volumes.Length - 20).Average())
                : 1.0;
        }

        private static (double[] closes, double[] highs, double[] lows, double[] volumes, double[] opens)
            ExtractArrays(IReadOnlyList<StockQuote> bars)
        {
            var closes  = bars.Select(b => (double)b.Close).ToArray();
            var highs   = bars.Select(b => (double)b.High).ToArray();
            var lows    = bars.Select(b => (double)b.Low).ToArray();
            var volumes = bars.Select(b => (double)b.Volume).ToArray();
            var opens   = bars.Select(b => (double)b.Open).ToArray();
            return (closes, highs, lows, volumes, opens);
        }

        private static double ComputeAtr(double[] closes, double[] highs, double[] lows, int period)
        {
            if (closes.Length < 2) return 0;
            int n = Math.Min(period, closes.Length - 1);
            double sum = 0;
            for (int i = closes.Length - n; i < closes.Length; i++)
            {
                double tr = Math.Max(highs[i] - lows[i],
                            Math.Max(Math.Abs(highs[i] - closes[i - 1]),
                                     Math.Abs(lows[i]  - closes[i - 1])));
                sum += tr;
            }
            return sum / n;
        }

        private static double ComputeRsi(double[] closes, int period)
        {
            if (closes.Length < period + 1) return 50;
            double gain = 0, loss = 0;
            for (int i = closes.Length - period; i < closes.Length; i++)
            {
                double d = closes[i] - closes[i - 1];
                if (d >= 0) gain += d; else loss -= d;
            }
            if (loss == 0) return 100;
            double rs = (gain / period) / (loss / period);
            return 100.0 - (100.0 / (1.0 + rs));
        }
    }
}
