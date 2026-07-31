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
            decimal marginRatePercent,
            EarningsScanMode mode = EarningsScanMode.Upcoming,
            int lookbackDays = 5)
        {
            double target     = (double)targetUpPercent;
            DateTime today     = DateTime.Today;
            DateTime windowEnd = today.AddDays(Math.Max(1, windowDays));
            DateTime lookbackStart = today.AddDays(-Math.Max(1, lookbackDays));

            var picks = new List<EarningsPick>(universe.Count);

            foreach (var stock in universe)
            {
                if (!summaries.TryGetValue(stock.Symbol, out var quote) || quote == null) continue;
                if (!quote.NextEarningsDate.HasValue) continue;

                var earnings = quote.NextEarningsDate.Value.Date;

                // Yahoo's earningsTimestamp holds the CURRENT/most-recent call date, which sits in
                // the recent past for a company that just reported; the next scheduled date lives
                // in a separate field. That is what makes a lookback window possible without any
                // extra fetch — Upcoming mode simply discarded these rows.
                if (mode == EarningsScanMode.JustReported)
                {
                    if (earnings > today || earnings < lookbackStart) continue;
                }
                else
                {
                    if (earnings < today || earnings > windowEnd) continue;
                }

                history.TryGetValue(stock.Symbol, out var bars);
                if (bars == null || bars.Count < 2) continue;

                EarningsPick pick;
                if (mode == EarningsScanMode.JustReported)
                {
                    int daysSince = Math.Max(0, (today - earnings).Days);
                    pick = ScoreReported(stock, bars, quote, earnings, daysSince);
                }
                else
                {
                    int daysToEarnings = Math.Max(0, (earnings - today).Days);
                    pick = Score(stock, bars, quote, earnings, daysToEarnings, target);

                    if (useMargin)
                        ApplyMargin(pick, target, marginPercent, marginRatePercent, daysToEarnings);
                }

                EnrichName(pick, stock, quote, nameLookup);
                pick.TargetUpPercent = targetUpPercent;
                picks.Add(pick);
            }

            // JustReported has no EPS or target data yet — those need one request per symbol and
            // are filled in by the caller, which then re-runs ScoreRebound. Order by the size of
            // the selloff for now so the caller enriches the hardest-hit names first.
            var sorted = mode == EarningsScanMode.JustReported
                ? picks
                    .OrderBy(p => p.PostEarningsMovePct ?? 0)
                    .ThenBy(p => p.DaysSinceEarnings)
                    .Take(MaxPicks)
                    .ToList()
                : picks
                    .OrderByDescending(p => p.LikelihoodScore)
                    .ThenBy(p => p.DaysUntilEarnings)
                    .Take(MaxPicks)
                    .ToList();

            return Task.FromResult<IReadOnlyList<EarningsPick>>(sorted);
        }

        // ── Post-earnings (JustReported) ───────────────────────────────────────

        /// <summary>
        /// Builds a pick for a company that has already reported, capturing only the signals
        /// derivable from price history. EPS surprise and analyst targets arrive later via
        /// <see cref="ScoreRebound"/> once the caller has fetched them.
        /// </summary>
        private static EarningsPick ScoreReported(
            Stock stock, IReadOnlyList<StockQuote> bars, QuoteSummary quote,
            DateTime earnings, int daysSince)
        {
            var closes    = bars.Select(b => (double)b.Close).ToArray();
            double lastClose = closes[^1];
            decimal lastPrice = quote.Price ?? (decimal)lastClose;

            // Reaction to the print: last close vs the last close strictly BEFORE the earnings
            // date. Falls back to the first available bar when history starts after that date.
            double? postMove = null;
            var priorBar = bars.LastOrDefault(b => b.Timestamp.Date < earnings);
            double baseline = priorBar != null ? (double)priorBar.Close : closes[0];
            if (baseline > 0)
                postMove = (lastClose / baseline - 1.0) * 100.0;

            // Drawdown from the best close in the loaded window — "way down low" context that
            // does not depend on the earnings date itself.
            double periodHigh = closes.Max();
            double? drawdown = periodHigh > 0 ? (lastClose / periodHigh - 1.0) * 100.0 : null;

            double? iv = quote.ImpliedVolatility;

            double momentumPct = 0;
            if (closes.Length >= 21 && closes[^21] > 0)
                momentumPct = (closes[^1] / closes[^21] - 1.0) * 100.0;

            double sma20 = closes.Length >= 20 ? closes.Skip(closes.Length - 20).Average() : lastClose;
            double driftPct = sma20 > 0 ? (lastClose / sma20 - 1.0) * 100.0 : 0;

            var pick = new EarningsPick
            {
                Symbol              = stock.Symbol,
                Mode                = EarningsScanMode.JustReported,
                NextEarningsDate    = earnings,
                DaysUntilEarnings   = 0,
                DaysSinceEarnings   = daysSince,
                LastPrice           = lastPrice,
                DayChangePct        = quote.DayChangePct,
                ImpliedVolatility   = iv,
                PostEarningsMovePct = postMove.HasValue ? Math.Round(postMove.Value, 2) : null,
                DrawdownPct         = drawdown.HasValue ? Math.Round(drawdown.Value, 2) : null,
                MomentumPct         = Math.Round(momentumPct, 2),
                DriftPct            = Math.Round(driftPct, 2),
                GeneratedAt         = DateTime.Now,
            };

            ScoreRebound(pick);
            return pick;
        }

        // ── Rebound scoring thresholds ─────────────────────────────────────────

        /// <summary>Selloff at or beyond this (%) earns the full drop component.</summary>
        public const double ReboundFullDropPct = -20.0;

        /// <summary>Analyst upside at or beyond this (%) earns the full target component.</summary>
        public const double ReboundFullUpsidePct = 50.0;

        /// <summary>An EPS beat of at least this (%) earns the full beat component.</summary>
        public const double ReboundStrongBeatPct = 10.0;

        /// <summary>
        /// Scores a reported pick 0–100 on how much it looks like an overreaction: the market
        /// punished it, analysts still see upside, and the quarter actually beat.
        ///
        ///   Drop    (0–40): 0% → 0, <see cref="ReboundFullDropPct"/> or worse → 40.
        ///                   Only DOWN moves score; a stock that rose on the print is not a
        ///                   rebound candidate, so positive moves contribute nothing.
        ///   Upside  (0–40): 0% → 0, <see cref="ReboundFullUpsidePct"/> or more → 40.
        ///   EPS     (0–20): strong beat → 20, any beat → 12, miss → 0.
        ///                   Unknown scores 0 rather than penalising — a missing surprise means
        ///                   no provider has published it yet, not that the company missed.
        ///
        /// Safe to call before enrichment; the components simply score 0 until data arrives.
        /// Also sets <see cref="EarningsPick.MeetsThreshold"/> and the trigger reason.
        /// </summary>
        public static void ScoreRebound(EarningsPick pick)
        {
            if (pick == null) return;

            double drop = pick.PostEarningsMovePct ?? 0;
            double dropComponent = drop < 0
                ? Clamp(drop / ReboundFullDropPct, 0, 1) * 40.0
                : 0;

            double upside = pick.TargetDeltaPct ?? 0;
            double upsideComponent = upside > 0
                ? Clamp(upside / ReboundFullUpsidePct, 0, 1) * 40.0
                : 0;

            double epsComponent = pick.EpsBeat switch
            {
                true when (pick.Surprise?.SurprisePercent ?? 0) >= ReboundStrongBeatPct => 20.0,
                true  => 12.0,
                false => 0.0,
                _     => 0.0,
            };

            pick.OpportunityScore = Math.Round(
                Clamp(dropComponent + upsideComponent + epsComponent, 0, 100), 1);

            // The headline case: sold off, beat anyway, and analysts still see more than the
            // user's target upside. Unknown EPS cannot satisfy this — it is not a beat yet.
            pick.MeetsThreshold =
                drop < 0 &&
                pick.EpsBeat == true &&
                upside >= (double)pick.TargetUpPercent;

            var reasons = new List<string>(5);
            if (pick.PostEarningsMovePct.HasValue)
                reasons.Add($"{pick.PostEarningsMovePct.Value:F1}% since earnings");
            if (pick.DrawdownPct.HasValue && pick.DrawdownPct.Value <= -5)
                reasons.Add($"{pick.DrawdownPct.Value:F1}% off period high");
            if (pick.TargetDeltaPct.HasValue)
                reasons.Add($"{pick.TargetDeltaPct.Value:+0.0;-0.0}% to 1Y target");
            if (pick.Surprise is { HasVerdict: true })
                reasons.Add($"EPS {pick.Surprise.BeatDisplay.ToLowerInvariant()} {pick.Surprise.SurpriseDisplay}");
            else
                reasons.Add("EPS surprise not published yet");

            pick.TriggerReason = string.Join(" | ", reasons);
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
