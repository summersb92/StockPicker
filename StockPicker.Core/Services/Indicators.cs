using System;

namespace StockPicker.Services
{
    /// <summary>
    /// Shared technical-indicator math. Single source of truth so every service
    /// (analysis, day picks, earnings) reports identical values for the same bars.
    ///
    /// RSI and ATR use Wilder's smoothing: seed with a simple average over the first
    /// <c>period</c> values, then smooth each subsequent bar with
    /// <c>avg = (avg * (period - 1) + value) / period</c>. This is the industry-standard
    /// definition — a plain rolling mean is noisier and produces false extremes when a
    /// large bar enters or leaves the window.
    /// </summary>
    public static class Indicators
    {
        /// <summary>
        /// Wilder's RSI over the full series (seeded on the first <paramref name="period"/>
        /// changes, smoothed through the rest). Returns 50 when there is not enough data.
        /// </summary>
        public static double RsiWilder(double[] closes, int period = 14)
        {
            if (closes.Length < period + 1) return 50.0;

            double avgGain = 0, avgLoss = 0;

            // Seed: simple average of the first `period` changes.
            for (int i = 1; i <= period; i++)
            {
                double chg = closes[i] - closes[i - 1];
                if (chg > 0) avgGain += chg;
                else         avgLoss += -chg;
            }
            avgGain /= period;
            avgLoss /= period;

            // Wilder smoothing across the remaining series.
            for (int i = period + 1; i < closes.Length; i++)
            {
                double chg = closes[i] - closes[i - 1];
                avgGain = (avgGain * (period - 1) + (chg > 0 ?  chg : 0)) / period;
                avgLoss = (avgLoss * (period - 1) + (chg < 0 ? -chg : 0)) / period;
            }

            if (avgLoss == 0) return 100.0;
            double rs = avgGain / avgLoss;
            return 100.0 - (100.0 / (1.0 + rs));
        }

        /// <summary>
        /// Wilder's ATR over the full series (seeded on the first true ranges, then
        /// smoothed). Returns 0 when fewer than two bars are available.
        /// </summary>
        public static double AtrWilder(double[] closes, double[] highs, double[] lows, int period = 14)
        {
            int n = Math.Min(closes.Length, Math.Min(highs.Length, lows.Length));
            if (n < 2) return 0;

            // Seed: simple average of the first min(period, n-1) true ranges.
            int seedLen = Math.Min(period, n - 1);
            double atr = 0;
            for (int i = 1; i <= seedLen; i++)
                atr += TrueRange(highs[i], lows[i], closes[i - 1]);
            atr /= seedLen;

            // Wilder smoothing across the remaining series.
            for (int i = seedLen + 1; i < n; i++)
                atr = (atr * (period - 1) + TrueRange(highs[i], lows[i], closes[i - 1])) / period;

            return atr;
        }

        private static double TrueRange(double high, double low, double prevClose) =>
            Math.Max(high - low,
            Math.Max(Math.Abs(high - prevClose), Math.Abs(low - prevClose)));
    }
}
