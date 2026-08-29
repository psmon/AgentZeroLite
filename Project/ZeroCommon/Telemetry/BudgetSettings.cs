using System;
using System.Collections.Generic;
using System.Linq;

namespace Agent.Common.Telemetry;

/// <summary>
/// User-editable budget configuration layered on top of the cost telemetry
/// (orca-adoption feature K / mission W9 follow-up). Holds a monthly spend cap
/// plus per-model price overrides that replace the hard-coded
/// <see cref="TokenCostCalculator.DefaultTable"/> at runtime. Pure &amp; WPF-free
/// so it is headlessly testable; persisted as JSON by
/// <see cref="BudgetSettingsStore"/>.
/// </summary>
public sealed class BudgetSettings
{
    /// <summary>
    /// Monthly spend cap in USD. Zero or negative means "no cap" (unlimited) —
    /// the over-budget indicator simply never trips.
    /// </summary>
    public decimal MonthlyCapUsd { get; set; }

    /// <summary>
    /// Per-model price overrides. Each entry is matched by lower-cased substring
    /// just like the default table, and is tried BEFORE the defaults so an
    /// override with the same key wins.
    /// </summary>
    public List<PriceOverride> Overrides { get; set; } = new();

    /// <summary>One per-model price row, mirroring <see cref="TokenCostCalculator.ModelPricing"/>.</summary>
    public sealed class PriceOverride
    {
        public string Key { get; set; } = "";
        public decimal InputPerMTok { get; set; }
        public decimal OutputPerMTok { get; set; }
        public decimal CacheWritePerMTok { get; set; }
        public decimal CacheReadPerMTok { get; set; }
    }

    /// <summary>
    /// The effective price table: valid overrides first (so they win the
    /// first-match lookup), then the built-in defaults. Blank-keyed overrides
    /// are ignored. Safe to feed straight into
    /// <see cref="TokenCostCalculator.Lookup(string, IReadOnlyList{ValueTuple{string, TokenCostCalculator.ModelPricing}}?)"/>.
    /// </summary>
    public IReadOnlyList<(string Key, TokenCostCalculator.ModelPricing Pricing)> EffectiveTable()
    {
        var list = new List<(string, TokenCostCalculator.ModelPricing)>();
        foreach (var o in Overrides)
        {
            if (string.IsNullOrWhiteSpace(o.Key)) continue;
            list.Add((o.Key.Trim().ToLowerInvariant(),
                new TokenCostCalculator.ModelPricing(
                    o.InputPerMTok, o.OutputPerMTok, o.CacheWritePerMTok, o.CacheReadPerMTok)));
        }
        list.AddRange(TokenCostCalculator.DefaultTable.Select(x => (x.Key, x.Pricing)));
        return list;
    }

    /// <summary>
    /// Evaluate month-to-date spend against the cap. <paramref name="spentUsd"/>
    /// is typically <see cref="TokenCostCalculator.TotalUsdSince"/> from the
    /// start of the current month using <see cref="EffectiveTable"/>.
    /// </summary>
    public BudgetStatus Evaluate(decimal spentUsd)
        => new(spentUsd, MonthlyCapUsd, HasCap: MonthlyCapUsd > 0m);

    /// <summary>UTC start of the current month for <paramref name="nowUtc"/> — the default budget window.</summary>
    public static DateTime StartOfMonthUtc(DateTime nowUtc)
        => new(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc);
}

/// <summary>Immutable snapshot of budget state for the UI/indicator.</summary>
public readonly record struct BudgetStatus(decimal SpentUsd, decimal CapUsd, bool HasCap)
{
    /// <summary>True once spend meets or exceeds a real cap.</summary>
    public bool OverBudget => HasCap && SpentUsd >= CapUsd;

    /// <summary>Fraction of the cap consumed (0 when there is no cap). Not clamped.</summary>
    public double FractionUsed => HasCap && CapUsd > 0m ? (double)(SpentUsd / CapUsd) : 0d;

    /// <summary>True when spend has reached <paramref name="warnFraction"/> of the cap (e.g. 0.8) but is not yet over.</summary>
    public bool NearingCap(double warnFraction = 0.8d)
        => HasCap && !OverBudget && FractionUsed >= warnFraction;
}
