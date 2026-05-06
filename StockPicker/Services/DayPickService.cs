using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using StockPicker.Models;

namespace StockPicker.Services
{
    /// <summary>
    /// Scores every stock in the universe for intraday trading viability and returns
    /// the highest-scoring candidates as "Stock of the Day" picks.
    ///
    /// Scoring signals (all computed from the cached daily OHLCV data + live quotes):
    ///
    ///   1. Volume Surge  (0–3 pts) — today's volume vs 3-month avg daily volume.
    ///      High relative volume signals institutional activity and tight spreads.
    ///
    ///   2. Gap Magnitude (0–2 pts) — opening gap vs prior close (absolute %).
    ///      Large gaps attract momentum traders and create high-probability setups.
    ///
    ///   3. Intraday Move (0–2 pts) — magnitude of today's price change so far.
    ///      Strong directional moves show conviction and continuation potential.
    ///
    ///   4. ATR Volatility(0–1.5 pts) — 14-day Average True Range as % of price.
    ///      Higher ATR means more intraday range = more opportunity for day traders.
    ///
    ///   5. RSI Setup     (0–1 pt)  — RSI quality for the directional bias.
    ///      Momentum RSI (55–75) for longs; oversold RSI (&lt;35) for bounce plays.
    ///
    /// Picks below a minimum composite score are filtered out before ranking.
    /// The top <see cref="MaxPicks"/> are returned, sorted by score descending.
    /// </summary>
    public class DayPickService : IDayPickService
    {
        private const int    MaxPicks      = 10;
        private const int    MinPicks      = 5;
        private const double MinScore      = 2.5;   // floor — below this = not worth flagging
        private const double AtrMultStop   = 1.5;   // stop = 1.5× ATR from entry
        private const double AtrMultTarget = 2.5;   // target = 2.5× ATR from entry

        public Task<IReadOnlyList<DayPick>> GenerateAsync(
            IReadOnlyList<Stock>                                      universe,
            IReadOnlyDictionary<string, IReadOnlyList<StockQuote>>    history,
            IReadOnlyDictionary<string, QuoteSummary>                 summaries,
            IReadOnlyDictionary<string, (string Name, string Sector)>? nameLookup)
        {
            var candidates = new List<DayPick>(universe.Count);

            foreach (var stock in universe)
            {
                history.TryGetValue(stock.Symbol, out var bars);
                summaries.TryGetValue(stock.Symbol, out var quote);

                // Need at least 15 bars for ATR-14 + gap (prev close)
                if (bars == null || bars.Count < 15) continue;
                if (quote == null) continue;

                var pick = ScoreStock(stock, bars, quote);
                if (pick != null)
                {
                    // Fill in name / sector from the universe or name lookup
                    if (nameLookup != null &&
                        nameLookup.TryGetValue(stock.Symbol, out var info))
                    {
                        pick.CompanyName = info.Name;
                        pick.Sector      = info.Sector;
                    }
                    else
                    {
                        pick.CompanyName = stock.Name;
                        pick.Sector      = stock.Sector;
                    }

                    // Prefer the richer name from the live quote when available
                    if (!string.IsNullOrWhiteSpace(quote.LongName))
                        pick.CompanyName = quote.LongName;

                    candidates.Add(pick);
                }
            }

            // Sort by score desc, take top MaxPicks
            var sorted = candidates
                .OrderByDescending(p => p.IntraDayScore)
                .Take(MaxPicks)
                .ToList();

            return Task.FromResult<IReadOnlyList<DayPick>>(sorted);
        }

        // ── Per-stock scoring ─────────────────────────────────────────────────

        /// <summary>
        /// Returns a <see cref="DayPick"/> if the stock meets the minimum intraday
        /// score threshold, or <c>null</c> if it should be excluded.
        /// </summary>
        private static DayPick? ScoreStock(
            Stock stock,
            IReadOnlyList<StockQuote> bars,
            QuoteSummary quote)
        {
            double[] closes  = bars.Select(b => (double)b.Close).ToArray();
            double[] highs   = bars.Select(b => (double)b.High).ToArray();
            double[] lows    = bars.Select(b => (double)b.Low).ToArray();
            double[] volumes = bars.Select(b => (double)b.Volume).ToArray();
            double[] opens   = bars.Select(b => (double)b.Open).ToArray();

            double lastClose = closes[^1];
            double prevClose = closes[^2];
            double todayOpen = opens[^1];
            double todayHigh = highs[^1];
            double todayLow  = lows[^1];

            if (lastClose <= 0 || prevClose <= 0) return null;

            // ── 1. Gap % (open vs previous close) ─────────────────────────────
            double gapPct = prevClose != 0
                ? ((todayOpen - prevClose) / prevClose) * 100.0
                : 0.0;

            // ── 2. ATR-14 ──────────────────────────────────────────────────────
            double atr14 = ComputeAtr(closes, highs, lows, 14);
            double atrPct = lastClose > 0 ? (atr14 / lastClose) * 100.0 : 0.0;

            // Skip stocks with negligible intraday range (e.g. < 0.5% ATR)
            if (atrPct < 0.5) return null;

            // ── 3. RSI-14 ─────────────────────────────────────────────────────
            double rsi14 = ComputeRsi(closes, 14);

            // ── 4. Volume ratio ───────────────────────────────────────────────
            double volumeRatio;
            if (quote.Volume > 0 && quote.AvgVolume > 0)
            {
                volumeRatio = (double)quote.Volume / (double)quote.AvgVolume;
            }
            else
            {
                // Fallback: recent 5-day vol vs 20-day baseline from history
                volumeRatio = volumes.Length >= 20
                    ? volumes.Skip(volumes.Length - 5).Average() /
                      volumes.Skip(volumes.Length - 20).Average()
                    : 1.0;
            }

            // ── 5. Today's price-change magnitude ─────────────────────────────
            decimal lastPrice = quote.Price ?? (decimal)lastClose;
            double dayChangePct = quote.DayChangePct ?? 0.0;

            // ── Composite score ───────────────────────────────────────────────
            double score = 0;
            var    reasons = new List<string>(6);

            // Signal 1 — Volume Surge (0–3 pts)
            if (volumeRatio >= 3.0)      { score += 3.0; reasons.Add($"Volume surge {volumeRatio:F1}×"); }
            else if (volumeRatio >= 2.0) { score += 2.0; reasons.Add($"High volume {volumeRatio:F1}×"); }
            else if (volumeRatio >= 1.5) { score += 1.5; reasons.Add($"Volume {volumeRatio:F1}× avg"); }
            else if (volumeRatio >= 1.2) { score += 0.8; reasons.Add($"Above-avg vol {volumeRatio:F1}×"); }
            else                         {               /* no bonus for average or light volume */ }

            // Signal 2 — Gap Magnitude (0–2 pts, absolute value — gap direction → Direction)
            double absGap = Math.Abs(gapPct);
            if      (absGap >= 5.0) { score += 2.0; reasons.Add($"Large gap {gapPct:+0.##;-0.##}%"); }
            else if (absGap >= 3.0) { score += 1.5; reasons.Add($"Gap {gapPct:+0.##;-0.##}%"); }
            else if (absGap >= 1.5) { score += 1.0; reasons.Add($"Gap {gapPct:+0.##;-0.##}%"); }
            else if (absGap >= 0.5) { score += 0.5; reasons.Add($"Small gap {gapPct:+0.##;-0.##}%"); }

            // Signal 3 — Intraday Move (0–2 pts)
            double absDayChg = Math.Abs(dayChangePct);
            if      (absDayChg >= 5.0) { score += 2.0; reasons.Add($"Strong move {dayChangePct:+0.##;-0.##}%"); }
            else if (absDayChg >= 3.0) { score += 1.5; reasons.Add($"Solid move {dayChangePct:+0.##;-0.##}%"); }
            else if (absDayChg >= 2.0) { score += 1.0; reasons.Add($"Move {dayChangePct:+0.##;-0.##}%"); }
            else if (absDayChg >= 1.0) { score += 0.5; }

            // Signal 4 — ATR volatility (0–1.5 pts)
            if      (atrPct >= 4.0) { score += 1.5; reasons.Add($"High ATR {atrPct:F1}% (wide range)"); }
            else if (atrPct >= 2.5) { score += 1.0; reasons.Add($"ATR {atrPct:F1}% (active)"); }
            else if (atrPct >= 1.5) { score += 0.5; }

            // Signal 5 — RSI quality for direction (0–1 pt)
            // Long setup: RSI 45–75 (momentum zone, room to run)
            // Short setup: RSI 65–80 (overextended, fade candidate)
            // Oversold bounce: RSI < 35 (long mean-reversion)
            bool intendLong = dayChangePct >= 0;   // preliminary direction guess
            if (intendLong)
            {
                if      (rsi14 >= 55 && rsi14 <= 75) { score += 1.0; reasons.Add($"RSI {rsi14:F0} (bullish momentum)"); }
                else if (rsi14 >= 45 && rsi14 <  55) { score += 0.5; }
                else if (rsi14 < 35)                  { score += 0.8; reasons.Add($"RSI {rsi14:F0} (oversold bounce)"); }
            }
            else
            {
                if      (rsi14 >= 65 && rsi14 <= 80) { score += 1.0; reasons.Add($"RSI {rsi14:F0} (extended, short)"); }
                else if (rsi14 >  80)                 { score += 0.8; reasons.Add($"RSI {rsi14:F0} (overbought)"); }
                else if (rsi14 >= 55 && rsi14 <  65) { score += 0.5; }
            }

            // ── Filter ────────────────────────────────────────────────────────
            if (score < MinScore) return null;

            // ── Direction ─────────────────────────────────────────────────────
            // Primary cue: today's price change direction
            // Tie-break: gap direction
            DayPickDirection direction;
            if (dayChangePct > 0.1)       direction = DayPickDirection.Long;
            else if (dayChangePct < -0.1) direction = DayPickDirection.Short;
            else                          direction = gapPct >= 0 ? DayPickDirection.Long : DayPickDirection.Short;

            // ── Trade levels ──────────────────────────────────────────────────
            decimal atrDec    = (decimal)atr14;
            decimal stopDist  = atrDec * (decimal)AtrMultStop;
            decimal tgtDist   = atrDec * (decimal)AtrMultTarget;

            decimal? stopLoss = direction == DayPickDirection.Long
                ? lastPrice - stopDist
                : lastPrice + stopDist;
            decimal? target   = direction == DayPickDirection.Long
                ? lastPrice + tgtDist
                : lastPrice - tgtDist;

            // Clamp stop to avoid negative prices
            if (stopLoss < 0.01m) stopLoss = 0.01m;
            if (target   < 0.01m) target   = 0.01m;

            return new DayPick
            {
                Symbol       = stock.Symbol,
                Direction    = direction,
                EntryPrice   = lastPrice,
                StopLoss     = Math.Round(stopLoss.Value, 2),
                Target       = Math.Round(target.Value,   2),
                IntraDayScore = Math.Round(score, 2),
                VolumeRatio  = Math.Round(volumeRatio, 2),
                GapPct       = Math.Round(gapPct, 2),
                AtrPct       = Math.Round(atrPct, 2),
                RSI14        = Math.Round(rsi14, 1),
                LastPrice    = lastPrice,
                DayChangePct = Math.Round(dayChangePct, 2),
                TriggerReason = reasons.Count > 0
                    ? string.Join(" | ", reasons)
                    : "Composite signal",
                GeneratedAt  = DateTime.Now,
            };
        }

        // ── Technical indicator helpers ───────────────────────────────────────

        /// <summary>
        /// Computes the 14-period Average True Range (simple average of true ranges).
        /// Returns 0 if there are fewer than 2 bars.
        /// </summary>
        private static double ComputeAtr(
            double[] closes, double[] highs, double[] lows, int period)
        {
            if (closes.Length < 2) return 0.0;

            int startIdx = Math.Max(1, closes.Length - period);
            double sum = 0;
            int count = 0;

            for (int i = startIdx; i < closes.Length; i++)
            {
                double hl    = highs[i]  - lows[i];
                double hpc   = Math.Abs(highs[i]  - closes[i - 1]);
                double lpc   = Math.Abs(lows[i]   - closes[i - 1]);
                double tr    = Math.Max(hl, Math.Max(hpc, lpc));
                sum += tr;
                count++;
            }

            return count > 0 ? sum / count : 0.0;
        }

        /// <summary>
        /// Computes Wilder's RSI over the last <paramref name="period"/> bars.
        /// Returns 50 if there are insufficient bars.
        /// </summary>
        private static double ComputeRsi(double[] closes, int period)
        {
            if (closes.Length < period + 1) return 50.0;

            int start = closes.Length - period - 1;
            double avgGain = 0, avgLoss = 0;

            for (int i = start; i < start + period; i++)
            {
                double chg = closes[i + 1] - closes[i];
                if (chg > 0) avgGain += chg;
                else         avgLoss += -chg;
            }
            avgGain /= period;
            avgLoss /= period;

            for (int i = start + period; i < closes.Length - 1; i++)
            {
                double chg = closes[i + 1] - closes[i];
                avgGain = (avgGain * (period - 1) + (chg > 0 ? chg : 0)) / period;
                avgLoss = (avgLoss * (period - 1) + (chg < 0 ? -chg : 0)) / period;
            }

            if (avgLoss == 0) return 100.0;
            return 100.0 - (100.0 / (1 + avgGain / avgLoss));
        }
    }
}
