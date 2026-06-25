using System;
using System.Collections.Generic;
using StockPicker.Models;

namespace StockPicker.Services
{
    /// <summary>
    /// Stateless screening helpers that evaluate fundamental quality signals
    /// on a list of <see cref="Recommendation"/> objects.
    ///
    /// All thresholds are named constants so units can be corrected on first
    /// live Finnhub run without hunting through calculation logic.
    ///
    /// DESIGN NOTE — why Finnhub D/E does NOT feed the confidence score:
    ///   Finnhub fundamentals are fetched in a background two-pass that completes
    ///   AFTER the initial ranking.  Only the first top-20 recs receive D/E data;
    ///   using it inside the ranking score would bias the universe unfairly (stocks
    ///   outside the top-20 never get enriched and would score lower with no data).
    ///   Cash/MktCap is computed from Yahoo data (universal) and is therefore safe
    ///   to use in the score tilt.
    /// </summary>
    public static class FundamentalScreen
    {
        // ── Screening thresholds ──────────────────────────────────────────────────

        /// <summary>Minimum cash-to-market-cap % for a stock to qualify as "cash-heavy".</summary>
        public const double CashHeavyMinCashToMktCapPct = 10.0;

        /// <summary>Maximum D/E ratio for a stock to qualify as "low-debt" via Finnhub D/E.</summary>
        public const double LowDebtMaxDebtToEquity = 1.0;

        /// <summary>
        /// Maximum net-debt-to-equity for a stock to qualify as "low-debt" via Finnhub
        /// net-debt/equity (used when D/E is unavailable). Values ≤ 0 indicate net cash.
        /// </summary>
        public const double NetCashMaxNetDebtToEquity = 0.0;

        // ── Score-tilt constants ──────────────────────────────────────────────────

        /// <summary>
        /// Maximum confidence nudge applied by <see cref="ApplyCashStrengthTilt"/>.
        /// The actual nudge scales linearly from 0 to this value as Cash/MktCap goes
        /// from 0 % to 25 %.  Capped so no individual stock can gain more than 5 pp.
        /// </summary>
        public const double CashStrengthTiltMax = 0.05;

        // ── Screening ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns true when the stock is both "cash-heavy" (cash ≥ <see cref="CashHeavyMinCashToMktCapPct"/> %
        /// of market cap) AND "low-debt" according to whatever Finnhub ratio is available.
        ///
        /// Degrades gracefully:
        ///   - D/E available    → use D/E ≤ <see cref="LowDebtMaxDebtToEquity"/>
        ///   - D/E missing, net-D/E available → use net-D/E ≤ <see cref="NetCashMaxNetDebtToEquity"/>
        ///   - Neither available → treat as low-debt (cash signal is still valid)
        /// </summary>
        public static bool IsCashHeavyLowDebt(Recommendation r)
        {
            if (r == null) return false;

            var cashPct = r.CashToMktCapPct;
            if (!cashPct.HasValue) return false;

            var cashHeavy = cashPct.Value >= CashHeavyMinCashToMktCapPct;
            if (!cashHeavy) return false;

            bool lowDebt;
            if (r.DebtToEquity.HasValue)
                lowDebt = r.DebtToEquity.Value <= LowDebtMaxDebtToEquity;
            else if (r.NetDebtToEquity.HasValue)
                lowDebt = r.NetDebtToEquity.Value <= NetCashMaxNetDebtToEquity;
            else
                lowDebt = true; // no debt data — degrade to cash-only screen

            return lowDebt;
        }

        // ── Score tilt ────────────────────────────────────────────────────────────

        /// <summary>
        /// Nudges <see cref="Recommendation.Confidence"/> upward for stocks with strong
        /// cash positions, then clamps the result to [0, 1].
        ///
        /// The tilt is proportional to Cash/MktCap, saturating at 25 % (above 25 % cash
        /// is unusual and may signal a holding company or liquidation, so we do not reward
        /// further).  Maximum boost is <see cref="CashStrengthTiltMax"/> (5 pp by default).
        ///
        /// Only stocks with a valid <see cref="Recommendation.CashToMktCapPct"/> are modified.
        /// Stocks without Yahoo cash data are left unchanged so the tilt is universe-wide
        /// and does not unfairly penalise symbols with missing data.
        ///
        /// Finnhub D/E intentionally does NOT contribute to the score — see class-level
        /// design note for the rationale.
        /// </summary>
        public static void ApplyCashStrengthTilt(IList<Recommendation> recs)
        {
            if (recs == null) return;

            foreach (var rec in recs)
            {
                var pct = rec.CashToMktCapPct;
                if (!pct.HasValue) continue;

                // Scale linearly 0 → CashStrengthTiltMax as pct goes 0 → 25 %.
                var tilt = CashStrengthTiltMax * Math.Min(1.0, pct.Value / 25.0);
                rec.Confidence = Math.Clamp(rec.Confidence + tilt, 0.0, 1.0);
            }
        }
    }
}
