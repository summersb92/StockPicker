using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using StockPicker.Models;

namespace StockPicker.Services
{
    /// <summary>
    /// Scans the universe for stocks with earnings inside a date window and scores each for the
    /// likelihood of rising by a target %. Optionally computes margin-adjusted returns.
    ///
    /// The likelihood score is a heuristic blend of option-implied volatility, momentum, and
    /// recent drift — it is an estimate, NOT a prediction or financial advice.
    /// </summary>
    public class EarningsScanService : IEarningsScanService
    {
        private const int MaxPicks = 50;

        public Task<IReadOnlyList<EarningsPick>> GenerateAsync(
            IReadOnlyList<Stock>                                       universe,
            IReadOnlyDictionary<string, IReadOnlyList<StockQuote>>     history,
            IReadOnlyDictionary<string, QuoteSummary>                  summaries,
            IReadOnlyDictionary<string, (string Name, string Sector)>? nameLookup,
            int     windowDays,
            decimal targetUpPercent,
            bool    useMargin,
            decimal marginPercent,
            decimal marginRatePercent)
        {
            double target     = (double)targetUpPercent;
            DateTime today     = DateTime.Today;
            DateTime windowEnd = today.AddDays(Math.Max(1, windowDays));

            var picks = new List<EarningsPick>(universe.Count);

            foreach (var stock in universe)
            {
                if (!summaries.TryGetValue(stock.Symbol, out var quote) || quote == null) continue;
                if (!quote.NextEarningsDate.HasValue) continue;

                var earnings = quote.NextEarningsDate.Value.Date;
                if (earnings < today || earnings > windowEnd) continue;

                history.TryGetValue(stock.Symbol, out var bars);
                if (bars == null || bars.Count < 2) continue;

                int daysToEarnings = Math.Max(0, (earnings - today).Days);

                var pick = Score(stock, bars, quote, earnings, daysToEarnings, target);
                EnrichName(pick, stock, quote, nameLookup);

                if (useMargin)
                    ApplyMargin(pick, target, marginPercent, marginRatePercent, daysToEarnings);

                pick.TargetUpPercent = targetUpPercent;
                picks.Add(pick);
            }

            var sorted = picks
                .OrderByDescending(p => p.LikelihoodScore)
                .ThenBy(p => p.DaysUntilEarnings)
                .Take(MaxPicks)
                .ToList();

            return Task.FromResult<IReadOnlyList<EarningsPick>>(sorted);
        }

        // ── Scoring ───────────────────────────────────────────────────────────

        private static EarningsPick Score(
            Stock stock, IReadOnlyList<StockQuote> bars, QuoteSummary quote,
            DateTime earnings, int daysToEarnings, double target)
        {
            var closes = bars.Select(b => (double)b.Close).ToArray();
            double lastClose = closes[^1];
            decimal lastPrice = quote.Price ?? (decimal)lastClose;

            // Volatility: prefer option-implied IV; fall back to realized vol from daily returns.
            double? iv      = quote.ImpliedVolatility;
            double realized = AnnualizedVol(closes);
            double vol       = iv ?? realized;
            double horizon   = Math.Max(1, daysToEarnings) / 365.0;
            double expectedMovePct = vol * Math.Sqrt(horizon) * 100.0;

            // Momentum: 20-day return.
            double momentumPct = 0;
            if (closes.Length >= 21 && closes[^21] > 0)
                momentumPct = (closes[^1] / closes[^21] - 1.0) * 100.0;

            // Drift: last close vs SMA20.
            double sma20 = closes.Length >= 20 ? closes.Skip(closes.Length - 20).Average() : lastClose;
            double driftPct = sma20 > 0 ? (lastClose / sma20 - 1.0) * 100.0 : 0;

            // ── Blended 0–100 likelihood ──
            // A (0–50): expected one-sigma move relative to the target — a bigger implied move
            //           means a realistic shot at reaching +target.
            double ratio = target > 0 ? expectedMovePct / target : 2.0;
            double a = Clamp(ratio / 2.0, 0, 1) * 50.0;
            // B (0–30): momentum, mapped -10%→0 … +10%→30.
            double b = Clamp((momentumPct + 10.0) / 20.0, 0, 1) * 30.0;
            // C (0–20): drift vs SMA20, mapped -5%→0 … +5%→20.
            double c = Clamp((driftPct + 5.0) / 10.0, 0, 1) * 20.0;
            double score = Clamp(a + b + c, 0, 100);

            // Threshold flag: implied move can plausibly reach the target AND not in a downtrend.
            bool meets = expectedMovePct >= target && (momentumPct + driftPct) > -5.0;

            var reasons = new List<string>(5);
            reasons.Add(iv.HasValue ? $"IV {iv.Value * 100:F0}%" : $"Realized vol {realized * 100:F0}%");
            reasons.Add($"±{expectedMovePct:F1}% implied move to earnings");
            if (momentumPct >= 5)  reasons.Add($"Momentum +{momentumPct:F1}% (20d)");
            else if (momentumPct <= -5) reasons.Add($"Weak momentum {momentumPct:F1}% (20d)");
            if (driftPct >= 2)     reasons.Add($"{driftPct:F1}% above SMA20");
            else if (driftPct <= -2) reasons.Add($"{driftPct:F1}% below SMA20");

            return new EarningsPick
            {
                Symbol            = stock.Symbol,
                NextEarningsDate  = earnings,
                DaysUntilEarnings = daysToEarnings,
                LastPrice         = lastPrice,
                DayChangePct      = quote.DayChangePct,
                ImpliedVolatility = iv,
                ExpectedMovePct   = Math.Round(expectedMovePct, 2),
                MomentumPct       = Math.Round(momentumPct, 2),
                DriftPct          = Math.Round(driftPct, 2),
                LikelihoodScore   = Math.Round(score, 1),
                MeetsThreshold    = meets,
                TriggerReason     = string.Join(" | ", reasons),
                GeneratedAt       = DateTime.Now,
            };
        }

        // ── Margin math ─────────────────────────────────────────────────────────

        private static void ApplyMargin(
            EarningsPick pick, double target,
            decimal marginPercent, decimal marginRatePercent, int daysToEarnings)
        {
            double marginPct = Math.Max(1.0, (double)marginPercent);   // avoid div-by-zero
            double rate      = (double)marginRatePercent / 100.0;
            double leverage  = 100.0 / marginPct;                       // 50% → 2×
            double years     = daysToEarnings / 365.0;

            double interestCostPct = (leverage - 1.0) * rate * years * 100.0;
            double grossPct        = leverage * target;
            double netPct          = grossPct - interestCostPct;
            double breakevenMove   = leverage > 0 ? interestCostPct / leverage : 0;

            pick.MarginApplied           = true;
            pick.Leverage                = Math.Round(leverage, 2);
            pick.InterestCostPct         = Math.Round(interestCostPct, 3);
            pick.GrossLeveragedReturnPct = Math.Round(grossPct, 2);
            pick.NetMarginReturnPct      = Math.Round(netPct, 2);
            pick.BreakevenMovePct        = Math.Round(breakevenMove, 3);
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static void EnrichName(
            EarningsPick pick, Stock stock, QuoteSummary quote,
            IReadOnlyDictionary<string, (string Name, string Sector)>? nameLookup)
        {
            if (nameLookup != null && nameLookup.TryGetValue(stock.Symbol, out var info))
            { pick.CompanyName = info.Name; pick.Sector = info.Sector; }
            else
            { pick.CompanyName = stock.Name; pick.Sector = stock.Sector; }

            if (!string.IsNullOrWhiteSpace(quote.LongName))
                pick.CompanyName = quote.LongName!;
            if (!string.IsNullOrWhiteSpace(quote.Sector))
                pick.Sector = quote.Sector!;
        }

        /// <summary>Annualized volatility from daily log returns (falls back to 0.30 when sparse).</summary>
        private static double AnnualizedVol(double[] closes)
        {
            if (closes.Length < 6) return 0.30;
            int n = Math.Min(closes.Length - 1, 60);
            var rets = new List<double>(n);
            for (int i = closes.Length - n; i < closes.Length; i++)
            {
                if (closes[i - 1] > 0 && closes[i] > 0)
                    rets.Add(Math.Log(closes[i] / closes[i - 1]));
            }
            if (rets.Count < 2) return 0.30;
            double mean = rets.Average();
            double var  = rets.Sum(r => (r - mean) * (r - mean)) / (rets.Count - 1);
            double daily = Math.Sqrt(var);
            return daily * Math.Sqrt(252.0);
        }

        private static double Clamp(double v, double lo, double hi) =>
            v < lo ? lo : (v > hi ? hi : v);
    }
}
