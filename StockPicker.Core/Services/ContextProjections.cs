using System;
using System.Collections.Generic;
using System.Linq;
using StockPicker.Models;

namespace StockPicker.Services
{
    // ─────────────────────────────────────────────────────────────────────────
    // Whitelist export DTOs
    //
    // SECURITY: these records are the ONLY shapes the context exporter (and any
    // CLI/MCP surface reusing them) ever serializes for domain objects. They are
    // built field-by-field from the source models, so nothing outside this
    // explicit whitelist — and in particular no UserSettings / ApiKeys material —
    // can ever end up in an exported file.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Whitelisted export shape of a <see cref="Recommendation"/>.</summary>
    public sealed record RecommendationExport(
        string    Symbol,
        string    CompanyName,
        string    Sector,
        string    Action,
        double    Confidence,
        decimal?  LastPrice,
        double?   DayChangePct,
        double?   RSI14,
        double?   WeekReturnPct,
        double?   SMA20,
        double?   SMA50,
        decimal?  TargetPrice,
        DateTime? BuyDate,
        DateTime? SellDate,
        string    HoldingPeriod,
        string    Reasoning);

    /// <summary>Whitelisted export shape of a <see cref="HeldPosition"/>.</summary>
    public sealed record PositionExport(
        string    Symbol,
        string    CompanyName,
        decimal   EntryPrice,
        int       ShareCount,
        DateTime  EntryDate,
        DateTime? PlannedSellDate,
        string    HoldingPeriod,
        decimal?  LastPrice,
        double?   UnrealizedGainPct,
        bool      BoughtOnMargin,
        decimal   MarginPercent,
        decimal   MarginInterestRatePercent,
        decimal   Leverage,
        decimal   EquityInvested,
        decimal   InterestAccrued,
        double?   ReturnOnEquityPct);

    /// <summary>Whitelisted export shape of an <see cref="EarningsPick"/>.</summary>
    public sealed record EarningsExport(
        string   Symbol,
        string   CompanyName,
        string   Sector,
        DateTime NextEarningsDate,
        int      DaysUntilEarnings,
        double   LikelihoodScore,
        bool     MeetsThreshold,
        double   ExpectedMovePct,
        double   MomentumPct,
        decimal? LastPrice);

    /// <summary>Whitelisted export shape of a <see cref="Transaction"/>.</summary>
    public sealed record TransactionExport(
        DateTime Date,
        string   Type,
        string   Symbol,
        string   CompanyName,
        int      Shares,
        decimal  Price,
        decimal  GrossAmount,
        decimal  CashDelta,
        decimal? RealizedGain,
        bool     OnMargin,
        string   Note);

    /// <summary>Whitelisted export shape of a <see cref="DayPick"/>.</summary>
    public sealed record DayPickExport(
        string   Symbol,
        string   CompanyName,
        string   Sector,
        string   Direction,
        double   IntraDayScore,
        decimal? EntryPrice,
        decimal? StopLoss,
        decimal? Target,
        double?  RiskRewardRatio,
        double?  RSI14,
        string   TriggerReason);

    /// <summary>Whitelisted export shape of a single <see cref="PerformancePeriod"/>.</summary>
    public sealed record PerformancePeriodExport(
        string   Label,
        DateTime StartDate,
        decimal  StartValue,
        decimal  CurrentValue,
        double   ChangePct,
        bool     HasData);

    /// <summary>
    /// Whitelisted export shape of a <see cref="PortfolioPerformance"/> — the raw
    /// numbers only, without the model's *Display formatting properties.
    /// </summary>
    public sealed record PerformanceExport(
        DateTime AsOf,
        int      PositionCount,
        decimal  CostBasis,
        decimal  MarketValue,
        decimal  CashBalance,
        decimal  TotalValue,
        decimal  TotalGain,
        double   TotalGainPct,
        List<PerformancePeriodExport> Periods);

    /// <summary>
    /// Static whitelist projections shared by the context exporter, the CLI, and
    /// any future MCP surface. Mirrors the CLI's anonymous-object projections in
    /// StockPicker.Cli/Program.cs, but as reusable, serializable public DTOs.
    /// </summary>
    public static class ContextProjections
    {
        /// <summary>Projects a recommendation onto its whitelisted export shape.</summary>
        public static RecommendationExport ProjectRecommendation(Recommendation r) => new(
            r.Symbol, r.CompanyName, r.Sector,
            r.Action.ToString(), r.Confidence,
            r.LastPrice, r.DayChangePct, r.RSI14, r.WeekReturnPct, r.SMA20, r.SMA50,
            r.TargetPrice, r.BuyDate, r.SellDate, r.HoldingPeriod.ToString(),
            r.Reasoning);

        /// <summary>Projects a held position onto its whitelisted export shape.</summary>
        public static PositionExport ProjectPosition(HeldPosition p) => new(
            p.Symbol, p.CompanyName, p.EntryPrice, p.ShareCount, p.EntryDate,
            p.PlannedSellDate, p.HoldingPeriod.ToString(),
            p.LastPrice, p.UnrealizedGainPct,
            p.BoughtOnMargin, p.MarginPercent, p.MarginInterestRatePercent,
            p.Leverage, p.EquityInvested, p.InterestAccrued, p.ReturnOnEquityPct);

        /// <summary>Projects an earnings pick onto its whitelisted export shape.</summary>
        public static EarningsExport ProjectEarnings(EarningsPick e) => new(
            e.Symbol, e.CompanyName, e.Sector,
            e.NextEarningsDate, e.DaysUntilEarnings,
            e.LikelihoodScore, e.MeetsThreshold, e.ExpectedMovePct, e.MomentumPct,
            e.LastPrice);

        /// <summary>Projects a ledger transaction onto its whitelisted export shape.</summary>
        public static TransactionExport ProjectTransaction(Transaction t) => new(
            t.Date, t.Type.ToString(), t.Symbol, t.CompanyName,
            t.Shares, t.Price, t.GrossAmount, t.CashDelta, t.RealizedGain,
            t.OnMargin, t.Note);

        /// <summary>Projects a day pick onto its whitelisted export shape.</summary>
        public static DayPickExport ProjectDayPick(DayPick p) => new(
            p.Symbol, p.CompanyName, p.Sector, p.Direction.ToString(),
            p.IntraDayScore, p.EntryPrice, p.StopLoss, p.Target,
            p.RiskRewardRatio, p.RSI14, p.TriggerReason);

        /// <summary>Projects one trailing-window period onto its whitelisted export shape.</summary>
        public static PerformancePeriodExport ProjectPerformancePeriod(PerformancePeriod p) => new(
            p.Label, p.StartDate, p.StartValue, p.CurrentValue, p.ChangePct, p.HasData);

        /// <summary>Projects the aggregate portfolio performance onto its whitelisted export shape.</summary>
        public static PerformanceExport ProjectPerformance(PortfolioPerformance p) => new(
            p.AsOf, p.PositionCount, p.CostBasis, p.MarketValue, p.CashBalance,
            p.TotalValue, p.TotalGain, p.TotalGainPct,
            p.Periods.Select(ProjectPerformancePeriod).ToList());
    }
}
