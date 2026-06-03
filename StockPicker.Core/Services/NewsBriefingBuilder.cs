using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using StockPicker.Models;

namespace StockPicker.Services
{
    /// <summary>One cross-strategy "best" pick plus the strategy that surfaced it.</summary>
    public readonly record struct BestPick(Recommendation Rec, string Strategy);

    /// <summary>
    /// All data the <see cref="NewsBriefingBuilder"/> needs to render a briefing.
    /// Populated identically by the WPF app and the CLI so both produce the same output.
    /// </summary>
    public sealed class BriefingInput
    {
        public string StrategyName         { get; init; } = "(default)";
        public string UniverseDescription  { get; init; } = "";
        public decimal TargetWeeklyPercent  { get; init; }
        public decimal TargetMonthlyPercent { get; init; }
        public IReadOnlyList<string> DataSources { get; init; } = Array.Empty<string>();
        public string LastDataRefresh { get; init; } = "";

        public IReadOnlyList<Recommendation> Recommendations { get; init; } = Array.Empty<Recommendation>();
        public IReadOnlyList<HeldPosition>   Positions       { get; init; } = Array.Empty<HeldPosition>();
        public IReadOnlyList<EarningsPick>   Earnings        { get; init; } = Array.Empty<EarningsPick>();
        public IReadOnlyList<BestPick>       BestAnyStrategy { get; init; } = Array.Empty<BestPick>();

        public int EarningsWindowDays { get; init; } = 30;
        public int TopCount           { get; init; } = 5;
        public DateTime GeneratedAt   { get; init; } = DateTime.Now;
    }

    /// <summary>
    /// Builds the copy-paste-ready markdown News briefing. Pure and WPF-free so the
    /// same output is produced by the desktop app's News tab and the CLI's `news` command.
    ///
    /// Sections, in order:
    ///   1. Scan parameters
    ///   2. Your positions — hold/sell guidance + exit strategy (only if any held)
    ///   3. Best stocks right now across every strategy
    ///   4. Top stocks heading into earnings (only if any found)
    ///   5. Top picks under the selected strategy
    ///   6. LLM analysis request
    /// </summary>
    public static class NewsBriefingBuilder
    {
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
            sb.AppendLine();

            AppendPositionsSection(sb, input);
            AppendBestAnyStrategySection(sb, input);
            AppendEarningsSection(sb, input);
            AppendTopPicksSection(sb, input);

            // ── Analysis request for the downstream LLM ──
            sb.AppendLine("## Analysis request");
            sb.AppendLine("You are an equity analyst. Using the data above:");
            sb.AppendLine("1. For each position I hold, confirm or challenge the hold/sell call and refine the exit plan.");
            sb.AppendLine("2. Rank the candidate picks (cross-strategy + earnings) from most to least attractive.");
            sb.AppendLine("3. Flag any pick you would avoid and explain the risk.");
            sb.AppendLine("4. Note any sector concentration or correlated exposure across the list.");
            sb.AppendLine("5. Suggest entry, stop-loss, and target levels for your top choice.");
            sb.AppendLine();
            sb.AppendLine("_Source: StockPicker algorithmic signals — not financial advice. Verify independently._");

            return sb.ToString().TrimEnd();
        }

        // ── Section 2: held positions — hold/sell guidance ─────────────────────
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
                var priceNow = pos.LastPrice.HasValue ? $"${pos.LastPrice.Value:F2}" : "—";

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

        // ── Section 3: best stocks across every strategy ───────────────────────
        private static void AppendBestAnyStrategySection(StringBuilder sb, BriefingInput input)
        {
            sb.AppendLine($"## {input.TopCount} best stocks right now (any strategy)");
            if (input.BestAnyStrategy.Count == 0)
            {
                sb.AppendLine("_No Buy-rated stocks found across the available strategies._");
                sb.AppendLine();
                return;
            }

            sb.AppendLine("_Highest-conviction Buys found by scanning the universe through every strategy._");
            sb.AppendLine();
            int i = 1;
            foreach (var (r, strat) in input.BestAnyStrategy.Take(input.TopCount))
            {
                sb.AppendLine($"### {i++}. {r.Symbol}" + (string.IsNullOrEmpty(r.CompanyName) ? "" : $" — {r.CompanyName}"));
                sb.AppendLine($"**{FormatAction(r.Action)}** · {r.Confidence:P0} confidence · via *{strat}*" +
                              (string.IsNullOrEmpty(r.Sector) ? "" : $" · {r.Sector}"));
                if (r.LastPrice.HasValue) sb.AppendLine($"- Price: ${r.LastPrice:F2}");
                if (r.RSI14.HasValue)     sb.AppendLine($"- RSI(14): {r.RSI14:F0}");
                if (!string.IsNullOrEmpty(r.Reasoning)) sb.AppendLine($"- Rationale: {r.Reasoning}");
                sb.AppendLine();
            }
        }

        // ── Section 4: upcoming earnings ───────────────────────────────────────
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

        // ── Section 5: top picks under the selected strategy ───────────────────
        private static void AppendTopPicksSection(StringBuilder sb, BriefingInput input)
        {
            var top = input.Recommendations
                .OrderByDescending(r => r.Action == RecommendationAction.StrongBuy ||
                                        r.Action == RecommendationAction.Buy)
                .ThenByDescending(r => r.Confidence)
                .ThenBy(r => r.ActionSortOrder)
                .Take(input.TopCount)
                .ToList();

            if (top.Count == 0) return;

            sb.AppendLine($"## Top picks — {input.StrategyName}");
            int i = 1;
            foreach (var r in top)
            {
                sb.AppendLine($"### {i++}. {r.Symbol} — {r.CompanyName}");
                sb.AppendLine($"**{FormatAction(r.Action)}**" +
                              (r.Confidence > 0 ? $" · {r.Confidence:P0} confidence" : "") +
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
                if (r.BuyDate.HasValue || r.SellDate.HasValue)
                    sb.AppendLine($"- Suggested hold: {(r.BuyDate.HasValue ? r.BuyDate.Value.ToString("MMM d") : "—")} → {(r.SellDate.HasValue ? r.SellDate.Value.ToString("MMM d") : "—")} ({r.HoldingPeriod})");
                if (!string.IsNullOrEmpty(r.Reasoning))
                    sb.AppendLine($"- Rationale: {r.Reasoning}");
                sb.AppendLine();
            }
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
