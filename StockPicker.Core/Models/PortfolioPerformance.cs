using System;
using System.Collections.Generic;

namespace StockPicker.Models
{
    /// <summary>
    /// Return of the current holdings over one trailing window (week/month/quarter/year),
    /// reconstructed from historical prices.
    /// </summary>
    public sealed class PerformancePeriod
    {
        /// <summary>"Week", "Month", "Quarter", or "Year".</summary>
        public string   Label            { get; init; } = "";
        public DateTime StartDate        { get; init; }
        public decimal  StartValue       { get; init; }
        public decimal  CurrentValue     { get; init; }
        /// <summary>How many holdings had price data covering this window.</summary>
        public int      PositionsCovered { get; init; }
        public bool     HasData          { get; init; }

        public decimal ChangeAmount => CurrentValue - StartValue;
        public double  ChangePct    => StartValue != 0
            ? (double)((CurrentValue - StartValue) / StartValue) * 100.0 : 0.0;
        public bool    IsUp         => ChangeAmount >= 0;

        public string AmountDisplay => !HasData ? "—"
            : (ChangeAmount >= 0 ? $"+${ChangeAmount:N0}" : $"-${Math.Abs(ChangeAmount):N0}");
        public string PctDisplay    => !HasData ? "n/a"
            : (ChangePct >= 0 ? $"+{ChangePct:F2}%" : $"{ChangePct:F2}%");
    }

    /// <summary>
    /// Aggregate performance of the current holdings: cost basis, market value, total
    /// unrealized gain, and a set of trailing-window returns.
    /// </summary>
    public sealed class PortfolioPerformance
    {
        public DateTime AsOf          { get; init; } = DateTime.Now;
        public int      PositionCount { get; init; }
        public decimal  CostBasis     { get; init; }
        public decimal  MarketValue   { get; init; }

        public decimal TotalGain    => MarketValue - CostBasis;
        public double  TotalGainPct => CostBasis != 0
            ? (double)((MarketValue - CostBasis) / CostBasis) * 100.0 : 0.0;
        public bool    IsUp         => TotalGain >= 0;

        public IReadOnlyList<PerformancePeriod> Periods { get; init; } = Array.Empty<PerformancePeriod>();

        public bool HasPositions => PositionCount > 0;

        public string MarketValueDisplay  => $"${MarketValue:N2}";
        public string CostBasisDisplay    => $"${CostBasis:N2}";
        public string TotalGainDisplay     => TotalGain >= 0 ? $"+${TotalGain:N2}" : $"-${Math.Abs(TotalGain):N2}";
        public string TotalGainPctDisplay  => TotalGainPct >= 0 ? $"+{TotalGainPct:F2}%" : $"{TotalGainPct:F2}%";
        public string AsOfDisplay          => $"as of {AsOf:MMM d, HH:mm}";

        public static PortfolioPerformance Empty => new();
    }
}
