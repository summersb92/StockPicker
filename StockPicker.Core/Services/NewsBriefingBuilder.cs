using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using StockPicker.Models;

namespace StockPicker.Services
{
    /// <summary>
    /// One cross-strategy "best" pick: the winning read, which strategy produced it,
    /// and how many of the scanned strategies agreed it was a Buy (consensus).
    /// </summary>
    public readonly record struct BestPick(
        Recommendation Rec, string Strategy, int BuyStrategyCount = 1, int StrategyCount = 1);

    /// <summary>The top Buy-rated picks for a single strategy (for mixed-strategy briefings).</summary>
    public sealed record StrategyTopPicks(
        string StrategyName, string HoldingPeriod, IReadOnlyList<Recommendation> Picks);

    /// <summary>Both cross-strategy views produced by <see cref="ScanEngine.CrossStrategyAsync"/>.</summary>
    public sealed class CrossStrategyResult
    {
        public List<BestPick>        Best        { get; init; } = new();
        public List<StrategyTopPicks> PerStrategy { get; init; } = new();
    }

    /// <summary>
    /// All data the <see cref="NewsBriefingBuilder"/> needs to render a briefing.
    /// Populated identically by the WPF app and the CLI so both produce the same output.
    /// The Include* flags let the UI compose the briefing section-by-section.
    /// </summary>
    public sealed class BriefingInput
    {
        public string StrategyName         { get; init; } = "(default)";
        public string UniverseDescription  { get; init; } = "";
        public decimal TargetWeeklyPercent  { get; init; }
        public decimal TargetMonthlyPercent { get; init; }
        public IReadOnlyList<string> DataSources { get; init; } = Array.Empty<string>();
        public string LastDataRefresh { get; init; } = "";

        public IReadOnlyList<Recommendation>    Recommendations { get; init; } = Array.Empty<Recommendation>();
        public IReadOnlyList<HeldPosition>      Positions       { get; init; } = Array.Empty<HeldPosition>();
        public IReadOnlyList<EarningsPick>      Earnings        { get; init; } = Array.Empty<EarningsPick>();
        public IReadOnlyList<BestPick>          BestAnyStrategy { get; init; } = Array.Empty<BestPick>();
        public IReadOnlyList<StrategyTopPicks>  PerStrategy     { get; init; } = Array.Empty<StrategyTopPicks>();
        public IReadOnlyList<MarketIndex>       MarketIndices   { get; init; } = Array.Empty<MarketIndex>();
        public PortfolioPerformance?            Performance     { get; init; }
        public decimal?                         CashBalance     { get; init; }

        public int EarningsWindowDays { get; init; } = 30;
        public int TopCount           { get; init; } = 5;
        public DateTime GeneratedAt   { get; init; } = DateTime.Now;

        // ── Section toggles (compose the briefing) ─────────────────────────────
        public bool IncludePositions   { get; init; } = true;
        public bool IncludeBestAny     { get; init; } = true;
        public bool IncludePerStrategy { get; init; } = true;
        public bool IncludeEarnings    { get; init; } = true;
        public bool IncludeTopPicks    { get; init; } = true;

        /// <summary>
        /// Which analysis-request question set to append for the downstream LLM:
        /// "Full", "Risk review", "Entry planning", or "Portfolio fit".
        /// </summary>
        public string AnalysisPreset { get; init; } = "Full";
    }

    /// <summary>
    /// Builds the copy-paste-ready markdown News briefing. Pure and WPF-free so the
    /// same output is produced by the desktop app's News tab and the CLI's `news` command.
    ///
    /// Sections, in order (each gated by its Include* flag where applicable):
    ///   1. Scan parameters + market context
    ///   2. Portfolio summary (one line, if performance/cash available)
    ///   3. Your positions — hold/sell guidance + exit strategy
    ///   4. Best stocks right now across every strategy (score-ranked, with consensus)
    ///   5. Top picks per strategy (mixed-strategy view)
    ///   6. Top stocks heading into earnings
    ///   7. Top picks under the selected strategy
    ///   8. Sector concentration rollup (computed, not delegated to the LLM)
    ///   9. LLM analysis request (preset question sets)
    /// </summary>
    public static class NewsBriefingBuilder
    {
        /// <summary>Available analysis-request presets, in display order.</summary>
        public static IReadOnlyList<string> AnalysisPresets { get; } =
            new[] { "Full", "Risk review", "Entry planning", "Portfolio fit" };

        public static string Build(BriefingInput input)
        {
            var sb  = new StringBuilder();
            var now = input.GeneratedAt;

            sb.AppendLine("# StockPicker Market Briefing");
            sb.AppendLine($"_Generated {now:dddd, MMM d yyyy  HH:mm}_");
            sb.AppendLine();

            // ── Settings that produced this list ──
            sb.AppendLine("## Scan parameters");
            sb.AppendLine($"- Strategy: **{input.StrategyName}**");
            if (!string.IsNullOrEmpty(input.UniverseDescription))
                sb.AppendLine($"- Universe: {input.UniverseDescription}");
            sb.AppendLine($"- Profit target: {input.TargetWeeklyPercent:0.##}% weekly  (~{input.TargetMonthlyPercent:0.##}% monthly)");
            var sources = input.DataSources is { Count: > 0 }
                ? string.Join(", ", input.DataSources)
                : "YahooFinance";
            sb.AppendLine($"- Data sources: {sources}");
            if (!string.IsNullOrEmpty(input.LastDataRefresh))
                sb.AppendLine($"- Last data refresh: {input.LastDataRefresh}");
            AppendMarketContext(sb, input);
            sb.AppendLine();

            AppendPortfolioSummary(sb, input);
            if (input.IncludePositions)   AppendPositionsSection(sb, input);
            if (input.IncludeBestAny)     AppendBestAnyStrategySection(sb, input);
            if (input.IncludePerStrategy) AppendPerStrategySection(sb, input);
            if (input.IncludeEarnings)    AppendEarningsSection(sb, input);
            if (input.IncludeTopPicks)    AppendTopPicksSection(sb, input);
            AppendSectorRollup(sb, input);
            AppendAnalysisRequest(sb, input);

            return sb.ToString().TrimEnd();
        }

        // ── Market context (inside Scan parameters) ─────────────────────────────
        private static void AppendMarketContext(StringBuilder sb, BriefingInput input)
        {
            var indices = input.MarketIndices
                .Where(i => i.Price.HasValue)
                .ToList();
            if (indices.Count == 0) return;

            var parts = indices.Select(i =>
                $"{i.Name} {i.Price:N0} ({(i.DayChangePct >= 0 ? "+" : "")}{i.DayChangePct:F2}%)");
            sb.AppendLine($"- Market: {string.Join("  ·  ", parts)}");
        }

        // ── Portfolio one-liner ──────────────────────────────────────────────────
        private static void AppendPortfolioSummary(StringBuilder sb, BriefingInput input)
        {
            var p = input.Performance;
            if (p == null && !input.CashBalance.HasValue) return;

            sb.AppendLine("## Portfolio snapshot");
            if (p != null)
            {
                sb.AppendLine($"- {p.PositionCount} position(s) · cost basis ${p.CostBasis:N0} · market value ${p.MarketValue:N0}" +
                              $" · cash ${p.CashBalance:N0} · **total ${p.TotalValue:N0}**" +
                              $" ({(p.TotalGain >= 0 ? "+" : "")}${p.TotalGain:N0}, {(p.TotalGainPct >= 0 ? "+" : "")}{p.TotalGainPct:F1}%)");
                var periods = p.Periods.Where(x => x.HasData)
                    .Select(x => $"{x.Label} {(x.ChangePct >= 0 ? "+" : "")}{x.ChangePct:F1}%");
                var trail = string.Join("  ·  ", periods);
                if (!string.IsNullOrEmpty(trail))
                    sb.AppendLine($"- Trailing: {trail}");
            }
            else
            {
                sb.AppendLine($"- Cash only: ${input.CashBalance:N0}");
            }
            sb.AppendLine();
        }

        // ── Held positions — hold/sell guidance ─────────────────────────────────
        private static void AppendPositionsSection(StringBuilder sb, BriefingInput input)
        {
            if (input.Positions.Count == 0) return;

            // Current-strategy read for each held symbol (Action + confidence).
            var signalBySymbol = input.Recommendations
                .GroupBy(r => r.Symbol, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            sb.AppendLine($"## Your positions ({input.Positions.Count}) — hold / sell guidance");
            int n = 1;
            foreach (var pos in input.Positions)
            {
                signalBySymbol.TryGetValue(pos.Symbol, out var sig);
                var (verdict, rationale, exit) = AdvisePosition(pos, sig, input.TargetMonthlyPercent);

                var pl = pos.UnrealizedGainPct.HasValue
                    ? $"{(pos.UnrealizedGainPct.Value >= 0 ? "+" : "")}{pos.UnrealizedGainPct.Value:F1}%"
                    : "n/a";
                var priceNow = pos.LastPrice.HasValue
                    ? $"${pos.LastPrice.Value:F2}"
                    : "— (no live quote; showing entry-based figures)";

                sb.AppendLine($"### {n++}. {pos.Symbol} — {verdict}" +
                              (string.IsNullOrEmpty(pos.CompanyName) ? "" : $"  ({pos.CompanyName})"));
                sb.AppendLine($"- Entry ${pos.EntryPrice:F2}" +
                              (pos.ShareCount > 0 ? $" × {pos.ShareCount} sh" : "") +
                              $"  ·  Now {priceNow}  ·  P/L {pl}");
                if (pos.PlannedSellDate.HasValue)
                    sb.AppendLine($"- Planned exit: {pos.PlannedSellDate.Value:ddd, MMM d yyyy} ({pos.HoldingPeriod})");
                sb.AppendLine($"- Recommendation: **{verdict}** — {rationale}");
                sb.AppendLine($"- Exit strategy: {exit}");
                sb.AppendLine();
            }
        }

        /// <summary>
        /// Heuristic hold/sell verdict plus a concrete exit plan. Priority:
        /// planned exit reached → bearish signal → profit target hit →
        /// risk limit breached → otherwise hold with a protective stop.
        /// </summary>
        public static (string verdict, string rationale, string exit) AdvisePosition(
            HeldPosition pos, Recommendation? signal, decimal targetMonthlyPercent)
        {
            var today = DateTime.Today;
            double? gain = pos.UnrealizedGainPct;
            decimal? stop = pos.EntryPrice > 0 ? Math.Round(pos.EntryPrice * 0.92m, 2) : (decimal?)null;

            // 1. Planned exit date reached or passed.
            if (pos.PlannedSellDate.HasValue && pos.PlannedSellDate.Value.Date <= today)
                return ("SELL",
                    $"Planned exit date ({pos.PlannedSellDate.Value:MMM d}) has arrived — the intended holding window is over.",
                    "Close the position at the next open.");

            // 2. Engine now rates the symbol bearish.
            if (signal != null && (signal.Action == RecommendationAction.Sell ||
                                   signal.Action == RecommendationAction.StrongSell))
                return ("SELL",
                    $"Current signal is {FormatAction(signal.Action)} ({signal.Confidence:P0}) — momentum has turned against the position.",
                    stop.HasValue
                        ? $"Exit into any strength; if held, set a hard stop at ${stop:F2} (−8% from entry)."
                        : "Exit into any strength.");

            // 3. Profit target reached.
            if (gain.HasValue && gain.Value > 0 && gain.Value >= (double)targetMonthlyPercent)
                return ("SELL",
                    $"Up {gain.Value:+0.0;-0.0}% — at or above your {targetMonthlyPercent:0.#}% monthly target.",
                    "Take profit, or trail a stop just under the most recent swing low to ride further upside.");

            // 4. Risk limit breached.
            if (gain.HasValue && gain.Value <= -8.0)
                return ("SELL",
                    $"Down {gain.Value:0.0}% — beyond an 8% risk limit.",
                    "Cut the loss to preserve capital; re-enter only on a fresh Buy signal.");

            // 5. Otherwise hold.
            var verdictRationale = signal != null
                ? $"Current signal is {FormatAction(signal.Action)}" +
                  (signal.Confidence > 0 ? $" ({signal.Confidence:P0})" : "") + " — no reason to exit yet."
                : "No active sell signal.";

            var parts = new List<string>();
            if (stop.HasValue) parts.Add($"protective stop at ${stop:F2} (−8%)");
            if (pos.PlannedSellDate.HasValue) parts.Add($"plan to exit around {pos.PlannedSellDate.Value:MMM d}");
            var exitPlan = parts.Count > 0
                ? "Hold — " + string.Join("; ", parts) + "."
                : "Hold and monitor for a sell signal.";

            return ("HOLD", verdictRationale, exitPlan);
        }

        // ── Best stocks across every strategy (score-ranked + consensus) ─────────
        private static void AppendBestAnyStrategySection(StringBuilder sb, BriefingInput input)
        {
            sb.AppendLine($"## {input.TopCount} best stocks right now (any strategy)");
            if (input.BestAnyStrategy.Count == 0)
            {
                sb.AppendLine("_No Buy-rated stocks found across the available strategies._");
                sb.AppendLine();
                return;
            }

            sb.AppendLine("_Ranked by raw strategy score (higher = stronger signal). " +
                          "\"Consensus\" counts how many strategies independently rate the stock a Buy._");
            sb.AppendLine();
            int i = 1;
            foreach (var pick in input.BestAnyStrategy.Take(input.TopCount))
            {
                var r = pick.Rec;
                sb.AppendLine($"### {i++}. {r.Symbol}" + (string.IsNullOrEmpty(r.CompanyName) ? "" : $" — {r.CompanyName}"));
                sb.AppendLine($"**{FormatAction(r.Action)}** · score {r.Score:0.0} · via *{pick.Strategy}*" +
                              $" · consensus {pick.BuyStrategyCount}/{pick.StrategyCount} strategies" +
                              (string.IsNullOrEmpty(r.Sector) ? "" : $" · {r.Sector}"));
                if (r.LastPrice.HasValue) sb.AppendLine($"- Price: ${r.LastPrice:F2}");
                if (r.RSI14.HasValue)     sb.AppendLine($"- RSI(14): {r.RSI14:F0}");
                AppendRiskLine(sb, r);
                if (!string.IsNullOrEmpty(r.Reasoning)) sb.AppendLine($"- Rationale: {r.Reasoning}");
                sb.AppendLine();
            }
        }

        // ── Top picks per strategy (mixed-strategy view) ─────────────────────────
        private static void AppendPerStrategySection(StringBuilder sb, BriefingInput input)
        {
            var sections = input.PerStrategy.Where(s => s.Picks.Count > 0).ToList();
            if (sections.Count == 0) return;

            sb.AppendLine("## Top picks by strategy");
            sb.AppendLine("_The strongest Buy from each strategy's own lens — different lenses suit different holding periods._");
            sb.AppendLine();
            foreach (var s in sections)
            {
                // Strategy names often already embed the holding period ("Momentum (Quick)") —
                // only append it when they don't.
                var heading = s.StrategyName.Contains(s.HoldingPeriod, StringComparison.OrdinalIgnoreCase)
                    ? s.StrategyName
                    : $"{s.StrategyName}  ({s.HoldingPeriod})";
                sb.AppendLine($"### {heading}");
                foreach (var r in s.Picks)
                {
                    var line = $"- **{r.Symbol}**" +
                               (string.IsNullOrEmpty(r.CompanyName) ? "" : $" ({r.CompanyName})") +
                               $" — {FormatAction(r.Action)}, score {r.Score:0.0}";
                    if (r.LastPrice.HasValue) line += $", ${r.LastPrice:F2}";
                    if (r.DaysToEarnings is int d and <= 14) line += $" ⚠ earnings in {d}d";
                    sb.AppendLine(line);
                }
                sb.AppendLine();
            }
        }

        // ── Upcoming earnings ────────────────────────────────────────────────────
        private static void AppendEarningsSection(StringBuilder sb, BriefingInput input)
        {
            if (input.Earnings.Count == 0) return;

            var top = input.Earnings
                .OrderByDescending(e => e.MeetsThreshold)
                .ThenByDescending(e => e.LikelihoodScore)
                .Take(input.TopCount)
                .ToList();

            sb.AppendLine($"## Top stocks for earnings (next {input.EarningsWindowDays} days)");
            int i = 1;
            foreach (var e in top)
            {
                sb.AppendLine($"### {i++}. {e.Symbol}" + (string.IsNullOrEmpty(e.CompanyName) ? "" : $" — {e.CompanyName}"));
                sb.AppendLine($"- Earnings: {e.EarningsDateDisplay}");
                sb.AppendLine($"- Likelihood score: {e.ScoreDisplay}/100" +
                              (e.MeetsThreshold ? $"  ·  flagged ≥ {e.TargetUpPercent:0.#}% upside" : ""));
                sb.AppendLine($"- Expected move: {e.ExpectedMoveDisplay}  ·  Momentum: {e.MomentumDisplay}");
                if (e.LastPrice.HasValue) sb.AppendLine($"- Price: ${e.LastPrice.Value:F2}");
                if (e.MarginApplied) sb.AppendLine($"- On margin: {e.LeverageDisplay} · net return {e.NetMarginReturnDisplay}");
                sb.AppendLine();
            }
        }

        // ── Top picks under the selected strategy ────────────────────────────────
        private static void AppendTopPicksSection(StringBuilder sb, BriefingInput input)
        {
            var top = input.Recommendations
                .OrderByDescending(r => r.Action == RecommendationAction.StrongBuy ||
                                        r.Action == RecommendationAction.Buy)
                .ThenByDescending(r => r.Score)
                .ThenBy(r => r.ActionSortOrder)
                .Take(input.TopCount)
                .ToList();

            if (top.Count == 0) return;

            sb.AppendLine($"## Top picks — {input.StrategyName}");
            int i = 1;
            foreach (var r in top)
            {
                sb.AppendLine($"### {i++}. {r.Symbol} — {r.CompanyName}");
                sb.AppendLine($"**{FormatAction(r.Action)}** · score {r.Score:0.0}" +
                              (string.IsNullOrEmpty(r.Sector) ? "" : $" · {r.Sector}"));

                if (r.LastPrice.HasValue)
                {
                    var chg = r.DayChangePct.HasValue
                        ? $" ({(r.DayChangePct >= 0 ? "+" : "")}{r.DayChangePct:F2}% today)"
                        : "";
                    sb.AppendLine($"- Price: ${r.LastPrice:F2}{chg}");
                }
                if (r.WeekReturnPct.HasValue) sb.AppendLine($"- Week return: {(r.WeekReturnPct >= 0 ? "+" : "")}{r.WeekReturnPct:F2}%");
                if (r.TargetPrice.HasValue)   sb.AppendLine($"- Target price: ${r.TargetPrice:F2}");
                if (r.RSI14.HasValue)         sb.AppendLine($"- RSI(14): {r.RSI14:F0}");
                if (r.SMA20.HasValue || r.SMA50.HasValue)
                    sb.AppendLine($"- SMA20/50: {(r.SMA20.HasValue ? $"${r.SMA20:F2}" : "—")} / {(r.SMA50.HasValue ? $"${r.SMA50:F2}" : "—")}");
                if (r.VolumeRatio.HasValue)   sb.AppendLine($"- Volume: {r.VolumeRatio:F1}× average");
                if (!string.IsNullOrEmpty(r.MarketCapDisplay)) sb.AppendLine($"- Market cap: {r.MarketCapDisplay}");
                if (r.PERatio.HasValue)       sb.AppendLine($"- P/E: {r.PERatio:F1}");
                AppendRiskLine(sb, r);
                if (r.BuyDate.HasValue || r.SellDate.HasValue)
                    sb.AppendLine($"- Suggested hold: {(r.BuyDate.HasValue ? r.BuyDate.Value.ToString("MMM d") : "—")} → {(r.SellDate.HasValue ? r.SellDate.Value.ToString("MMM d") : "—")} ({r.HoldingPeriod})");
                if (!string.IsNullOrEmpty(r.Reasoning))
                    sb.AppendLine($"- Rationale: {r.Reasoning}");
                sb.AppendLine();
            }
        }

        // ── Per-pick risk stats line ─────────────────────────────────────────────
        private static void AppendRiskLine(StringBuilder sb, Recommendation r)
        {
            var parts = new List<string>(4);
            if (r.AtrPct.HasValue)            parts.Add($"ATR {r.AtrPct:F1}%/day");
            if (r.Beta.HasValue)              parts.Add($"beta {r.Beta:F1}");
            if (r.Week52PositionPct.HasValue) parts.Add($"52w range {r.Week52PositionPct:F0}%");
            if (r.DaysToEarnings is int d)
                parts.Add(d <= 14 ? $"⚠ earnings in {d}d ({r.NextEarningsDate:MMM d})"
                                  : $"earnings in {d}d");
            if (parts.Count > 0)
                sb.AppendLine($"- Risk: {string.Join(" · ", parts)}");
        }

        // ── Sector concentration rollup (computed, not delegated to the LLM) ─────
        private static void AppendSectorRollup(StringBuilder sb, BriefingInput input)
        {
            // Collect the distinct symbols the briefing actually presented, with sectors.
            var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            void Add(string symbol, string? sector)
            {
                if (string.IsNullOrEmpty(symbol)) return;
                if (!seen.ContainsKey(symbol))
                    seen[symbol] = string.IsNullOrEmpty(sector) ? "Unknown" : sector!;
            }

            if (input.IncludeBestAny)
                foreach (var p in input.BestAnyStrategy.Take(input.TopCount)) Add(p.Rec.Symbol, p.Rec.Sector);
            if (input.IncludePerStrategy)
                foreach (var s in input.PerStrategy) foreach (var r in s.Picks) Add(r.Symbol, r.Sector);
            if (input.IncludeEarnings)
                foreach (var e in input.Earnings.Take(input.TopCount)) Add(e.Symbol, e.Sector);
            if (input.IncludeTopPicks)
                foreach (var r in input.Recommendations
                             .OrderByDescending(x => x.Score).Take(input.TopCount)) Add(r.Symbol, r.Sector);

            if (seen.Count < 2) return;

            var groups = seen.Values
                .GroupBy(s => s)
                .OrderByDescending(g => g.Count())
                .ToList();

            sb.AppendLine("## Sector exposure across the picks above");
            sb.AppendLine(string.Join("  ·  ", groups.Select(g => $"{g.Key}: {g.Count()}")));
            var (topSector, topCount) = (groups[0].Key, groups[0].Count());
            if (topCount * 2 >= seen.Count && topSector != "Unknown")
                sb.AppendLine($"_⚠ Concentration: {topCount} of {seen.Count} distinct picks are {topSector} — these positions will move together._");
            sb.AppendLine();
        }

        // ── LLM analysis request (preset question sets) ──────────────────────────
        private static void AppendAnalysisRequest(StringBuilder sb, BriefingInput input)
        {
            sb.AppendLine("## Analysis request");
            sb.AppendLine("You are an equity analyst. Using the data above:");

            switch (input.AnalysisPreset)
            {
                case "Risk review":
                    sb.AppendLine("1. Identify the single biggest risk in each held position and each candidate pick.");
                    sb.AppendLine("2. Which picks carry hidden correlation (sector, factor, or macro) that the sector rollup may understate?");
                    sb.AppendLine("3. Stress-test: if the market drops 5% next week, which of these are hurt most and why?");
                    sb.AppendLine("4. Are any picks facing an earnings report or known catalyst that makes the timing dangerous?");
                    sb.AppendLine("5. Propose position-size limits (as % of portfolio) for the top three candidates based on their volatility.");
                    break;

                case "Entry planning":
                    sb.AppendLine("1. For each candidate pick, propose a concrete limit-entry price and explain the level.");
                    sb.AppendLine("2. Set an initial stop-loss and a first profit target for each (use the ATR and 52-week data given).");
                    sb.AppendLine("3. Which single pick offers the best risk:reward at today's price, and what is that ratio?");
                    sb.AppendLine("4. Which picks are better left on a watchlist until a pullback, and to what price?");
                    sb.AppendLine("5. Sequence the entries: if I can only open one position per day, in what order and why?");
                    break;

                case "Portfolio fit":
                    sb.AppendLine("1. Given my current positions and cash, which candidate best complements what I already hold?");
                    sb.AppendLine("2. Would any candidate double-up an exposure I already have? Flag overlaps.");
                    sb.AppendLine("3. Suggest a target allocation (% per position, % cash) for a balanced version of this portfolio.");
                    sb.AppendLine("4. Should any existing position be trimmed or closed to fund a stronger candidate? Compare directly.");
                    sb.AppendLine("5. What is the one trade (buy, sell, or rebalance) with the highest expected improvement to the portfolio?");
                    break;

                default: // "Full"
                    sb.AppendLine("1. For each position I hold, confirm or challenge the hold/sell call and refine the exit plan.");
                    sb.AppendLine("2. Rank the candidate picks (cross-strategy + earnings) from most to least attractive.");
                    sb.AppendLine("3. Flag any pick you would avoid and explain the risk.");
                    sb.AppendLine("4. Note any sector concentration or correlated exposure beyond the rollup above.");
                    sb.AppendLine("5. Suggest entry, stop-loss, and target levels for your top choice.");
                    break;
            }

            sb.AppendLine();
            sb.AppendLine("_Source: StockPicker algorithmic signals — not financial advice. Verify independently._");
        }

        public static string FormatAction(RecommendationAction action) => action switch
        {
            RecommendationAction.StrongBuy  => "STRONG BUY",
            RecommendationAction.Buy        => "BUY",
            RecommendationAction.Hold       => "HOLD",
            RecommendationAction.Sell       => "SELL",
            RecommendationAction.StrongSell => "STRONG SELL",
            _                               => action.ToString(),
        };
    }
}
