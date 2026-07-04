using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using StockPicker.Models;

namespace StockPicker.Services
{
    // ── Report models ──────────────────────────────────────────────────────────

    /// <summary>Outcome statistics for one score bucket within one strategy.</summary>
    public sealed class ScoreBucketResult
    {
        public string Label        { get; init; } = "";
        public int    Signals     { get; set; }
        public double HitRatePct  { get; set; }
        public double WinRatePct  { get; set; }
        public double AvgReturnPct { get; set; }
    }

    /// <summary>Backtest outcome for a single strategy.</summary>
    public sealed class StrategyBacktestResult
    {
        public string StrategyName  { get; init; } = "";
        public string StrategyId    { get; init; } = "";
        public string HoldingPeriod { get; init; } = "";
        public int    HorizonBars   { get; init; }

        public int    Signals       { get; set; }
        /// <summary>% of Buy signals whose price TOUCHED the target within the horizon.</summary>
        public double HitRatePct    { get; set; }
        /// <summary>% of Buy signals with a positive return AT the horizon.</summary>
        public double WinRatePct    { get; set; }
        public double AvgReturnPct  { get; set; }
        public double AvgWinPct     { get; set; }
        public double AvgLossPct    { get; set; }
        /// <summary>Gross wins / gross losses at the horizon (>1 is profitable).</summary>
        public double ProfitFactor  { get; set; }
        /// <summary>Per-trade Sharpe, annualized by √(252/horizon).</summary>
        public double Sharpe        { get; set; }
        /// <summary>Max drawdown of the sequential (fully-reinvested, in signal order) trade equity curve.</summary>
        public double MaxDrawdownPct { get; set; }

        public List<ScoreBucketResult> Buckets { get; init; } = new();
    }

    /// <summary>Full backtest report across strategies.</summary>
    public sealed class BacktestReport
    {
        public DateTime From            { get; init; }
        public DateTime To              { get; init; }
        public int      UniverseSize    { get; init; }
        public decimal  TargetPercent   { get; init; }
        public int      StepBars        { get; init; }
        public List<StrategyBacktestResult> Strategies { get; init; } = new();
        /// <summary>Honesty notes: biases and simplifications a reader must know about.</summary>
        public List<string> Notes       { get; init; } = new();
    }

    public sealed class BacktestOptions
    {
        /// <summary>Re-scan every N trading bars (5 ≈ weekly).</summary>
        public int StepBars { get; init; } = 5;
        /// <summary>Minimum bars of history before the first scan (indicator warm-up).</summary>
        public int WarmupBars { get; init; } = 60;
        /// <summary>Target move used for hit-rate measurement, in percent.</summary>
        public decimal TargetPercent { get; init; } = 2.0m;
        /// <summary>Evaluation horizon in trading bars per holding period.</summary>
        public int QuickHorizonBars { get; init; } = 5;
        public int ShortHorizonBars { get; init; } = 20;
        public int LongHorizonBars  { get; init; } = 60;
    }

    /// <summary>
    /// Point-in-time replay of the strategy scorers over historical bars.
    ///
    /// For every rebalance date t (every <see cref="BacktestOptions.StepBars"/> bars),
    /// each stock is analyzed using ONLY bars[0..t]; a Buy signal (score ≥ 0.5 — the same
    /// threshold <see cref="RecommendationService"/> maps to Buy) is then evaluated
    /// against the ACTUAL future bars: did price touch entry×(1+target%) within the
    /// strategy's horizon, and what was the return at the horizon?
    ///
    /// This is what turns hand-tuned score thresholds into measured hit-rates — the
    /// calibration gap called out in the methodology review.
    ///
    /// Honesty limits (also emitted in <see cref="BacktestReport.Notes"/>):
    ///  • Survivorship bias: the universe is TODAY's constituents replayed backward.
    ///  • The Value strategy is excluded — its fundamentals are a today-only snapshot,
    ///    so replaying it would leak future information.
    ///  • The equity curve is sequential per-trade (full reinvestment in signal order),
    ///    not a calendar-time portfolio.
    /// </summary>
    public static class BacktestEngine
    {
        /// <summary>Strategy ids whose scorers depend on today-only data (excluded from replay).</summary>
        public static readonly IReadOnlySet<string> NotPointInTimeSafe =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "value" };

        public static Task<BacktestReport> RunAsync(
            IReadOnlyList<Stock> universe,
            IReadOnlyDictionary<string, IReadOnlyList<StockQuote>> history,
            IReadOnlyList<TradingStrategy> strategies,
            IAnalysisService analysis,
            BacktestOptions options,
            IProgress<string>? progress = null)
            => Task.Run(() => Run(universe, history, strategies, analysis, options, progress));

        private static BacktestReport Run(
            IReadOnlyList<Stock> universe,
            IReadOnlyDictionary<string, IReadOnlyList<StockQuote>> history,
            IReadOnlyList<TradingStrategy> strategies,
            IAnalysisService analysis,
            BacktestOptions options,
            IProgress<string>? progress)
        {
            var runnable = strategies.Where(s => !NotPointInTimeSafe.Contains(s.Id)).ToList();
            var skipped  = strategies.Where(s => NotPointInTimeSafe.Contains(s.Id)).ToList();

            DateTime from = DateTime.MaxValue, to = DateTime.MinValue;
            foreach (var bars in history.Values.Where(b => b.Count > 0))
            {
                if (bars[0].Timestamp  < from) from = bars[0].Timestamp;
                if (bars[^1].Timestamp > to)   to   = bars[^1].Timestamp;
            }

            var report = new BacktestReport
            {
                From          = from == DateTime.MaxValue ? DateTime.Today : from.Date,
                To            = to   == DateTime.MinValue ? DateTime.Today : to.Date,
                UniverseSize  = universe.Count,
                TargetPercent = options.TargetPercent,
                StepBars      = options.StepBars,
            };

            report.Notes.Add("Survivorship bias: the universe is TODAY's index constituents replayed backward — " +
                             "delisted/removed names are absent, which flatters absolute results.");
            report.Notes.Add("Buy threshold: score ≥ 0.5, mirroring RecommendationService's score→Buy mapping.");
            report.Notes.Add("Equity curve/drawdown is sequential per-trade with full reinvestment, not a calendar-time portfolio.");
            foreach (var s in skipped)
                report.Notes.Add($"'{s.Name}' excluded: its scorer uses today-only fundamentals (not point-in-time safe).");
            int adjustedSeries = history.Values.Count(b => b.Count > 0 && b.All(q => q.IsAdjusted));
            report.Notes.Add($"Split/dividend-adjusted price series: {adjustedSeries}/{history.Count} symbols.");

            double target = (double)options.TargetPercent / 100.0;

            foreach (var strat in runnable)
            {
                progress?.Report($"Backtesting {strat.Name}…");

                int horizon = strat.HoldingPeriod switch
                {
                    HoldingPeriod.Quick => options.QuickHorizonBars,
                    HoldingPeriod.Long  => options.LongHorizonBars,
                    _                   => options.ShortHorizonBars,
                };

                // (signal-order preserved: outer loop by rebalance index, inner by symbol)
                var trades = new List<(double score, bool hit, double ret)>();

                // Find the longest series to derive the rebalance index range.
                int maxBars = history.Values.Select(b => b.Count).DefaultIfEmpty(0).Max();

                for (int t = options.WarmupBars; t < maxBars - horizon; t += options.StepBars)
                {
                    foreach (var stock in universe)
                    {
                        if (!history.TryGetValue(stock.Symbol, out var bars)) continue;
                        if (t >= bars.Count - horizon || t < options.WarmupBars) continue;

                        // Point-in-time slice: the scorer sees ONLY bars[0..t].
                        var slice = new List<StockQuote>(t + 1);
                        for (int i = 0; i <= t; i++) slice.Add(bars[i]);

                        var ctx = new ScanContext
                        {
                            Strategy                  = strat,
                            TargetProfitMarginPercent = options.TargetPercent,
                            WeekStart                 = slice[0].Timestamp,
                            WeekEnd                   = slice[^1].Timestamp,
                            SkipTargetEstimate        = true,   // we measure REAL outcomes below
                            Summaries                 = null,   // no lookahead fundamentals
                        };

                        double score = analysis.AnalyzeAsync(stock, slice, ctx).Result.Score;
                        if (score < 0.5) continue;              // not a Buy — no trade

                        double entry = (double)bars[t].Close;
                        if (entry <= 0) continue;

                        bool hit = false;
                        for (int i = t + 1; i <= t + horizon && !hit; i++)
                            if ((double)bars[i].High >= entry * (1.0 + target)) hit = true;

                        double ret = (double)bars[t + horizon].Close / entry - 1.0;
                        trades.Add((score, hit, ret));
                    }
                }

                report.Strategies.Add(Aggregate(strat, horizon, trades));
                progress?.Report($"  {strat.Name}: {trades.Count} signals evaluated.");
            }

            return report;
        }

        // ── Aggregation ─────────────────────────────────────────────────────────

        private static StrategyBacktestResult Aggregate(
            TradingStrategy strat, int horizon,
            List<(double score, bool hit, double ret)> trades)
        {
            var r = new StrategyBacktestResult
            {
                StrategyName  = strat.Name,
                StrategyId    = strat.Id,
                HoldingPeriod = strat.HoldingPeriod.ToString(),
                HorizonBars   = horizon,
                Signals       = trades.Count,
            };
            if (trades.Count == 0) return r;

            var rets   = trades.Select(x => x.ret).ToList();
            var wins   = rets.Where(x => x > 0).ToList();
            var losses = rets.Where(x => x <= 0).ToList();

            r.HitRatePct   = 100.0 * trades.Count(x => x.hit) / trades.Count;
            r.WinRatePct   = 100.0 * wins.Count / trades.Count;
            r.AvgReturnPct = 100.0 * rets.Average();
            r.AvgWinPct    = wins.Count   > 0 ? 100.0 * wins.Average()   : 0;
            r.AvgLossPct   = losses.Count > 0 ? 100.0 * losses.Average() : 0;

            double grossWin  = wins.Sum();
            double grossLoss = Math.Abs(losses.Sum());
            r.ProfitFactor = grossLoss > 1e-9 ? grossWin / grossLoss : (grossWin > 0 ? double.PositiveInfinity : 0);

            double mean = rets.Average();
            double sd   = rets.Count > 1
                ? Math.Sqrt(rets.Sum(x => (x - mean) * (x - mean)) / (rets.Count - 1))
                : 0;
            r.Sharpe = sd > 1e-9 ? (mean / sd) * Math.Sqrt(252.0 / horizon) : 0;

            // Sequential fully-reinvested equity curve (signal order) → max drawdown.
            double equity = 1.0, peak = 1.0, maxDd = 0;
            foreach (var (_, _, ret) in trades)
            {
                equity *= 1.0 + ret;
                if (equity > peak) peak = equity;
                var dd = (peak - equity) / peak;
                if (dd > maxDd) maxDd = dd;
            }
            r.MaxDrawdownPct = 100.0 * maxDd;

            // Score-bucket calibration — the numbers that should eventually set the
            // StrongBuy/Buy thresholds instead of the current hand-tuned constants.
            foreach (var (label, lo, hi) in new[]
            {
                ("0.5–1", 0.5, 1.0), ("1–2", 1.0, 2.0), ("2–3", 2.0, 3.0), ("3+", 3.0, double.MaxValue),
            })
            {
                var inBucket = trades.Where(x => x.score >= lo && x.score < hi).ToList();
                var bucket = new ScoreBucketResult { Label = label, Signals = inBucket.Count };
                if (inBucket.Count > 0)
                {
                    bucket.HitRatePct   = 100.0 * inBucket.Count(x => x.hit) / inBucket.Count;
                    bucket.WinRatePct   = 100.0 * inBucket.Count(x => x.ret > 0) / inBucket.Count;
                    bucket.AvgReturnPct = 100.0 * inBucket.Average(x => x.ret);
                }
                r.Buckets.Add(bucket);
            }

            return r;
        }
    }
}
