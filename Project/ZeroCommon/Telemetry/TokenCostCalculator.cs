using System;
using System.Collections.Generic;
using System.Linq;
using Agent.Common.Data.Entities;

namespace Agent.Common.Telemetry;

/// <summary>
/// Turns recorded token counts (<see cref="TokenUsageRecord"/>) into a US-dollar
/// cost estimate (mission W9, orca-adoption). AgentZero already collects token
/// usage; this adds the pricing layer on top. Pure &amp; WPF-free so it is
/// headlessly testable.
///
/// Prices are per-million-tokens and MODEL-MATCHED by substring (e.g. "opus",
/// "sonnet", "haiku", "gpt", "o3"). They are editable defaults, not a live feed
/// — treat the output as an estimate. Cache-read is far cheaper than fresh
/// input and cache-write slightly dearer, so they are priced separately.
/// </summary>
public static class TokenCostCalculator
{
    /// <summary>Per-million-token prices (USD) for one model family.</summary>
    public sealed record ModelPricing(
        decimal InputPerMTok,
        decimal OutputPerMTok,
        decimal CacheWritePerMTok,
        decimal CacheReadPerMTok);

    /// <summary>
    /// Default price table, matched by lower-cased substring against the model
    /// name. Order matters — first match wins, so put specific keys first.
    /// Values are representative defaults; adjust as vendor pricing changes.
    /// </summary>
    public static readonly IReadOnlyList<(string Key, ModelPricing Pricing)> DefaultTable = new[]
    {
        ("opus",   new ModelPricing(15.00m, 75.00m, 18.75m, 1.50m)),
        ("sonnet", new ModelPricing( 3.00m, 15.00m,  3.75m, 0.30m)),
        ("haiku",  new ModelPricing( 0.80m,  4.00m,  1.00m, 0.08m)),
        ("gpt-4o", new ModelPricing( 2.50m, 10.00m,  2.50m, 1.25m)),
        ("o3",     new ModelPricing( 2.00m,  8.00m,  2.00m, 0.50m)),
        ("gpt",    new ModelPricing( 2.50m, 10.00m,  2.50m, 1.25m)),
    };

    /// <summary>Finds pricing for a model by substring match, or null if unknown.</summary>
    public static ModelPricing? Lookup(string model, IReadOnlyList<(string Key, ModelPricing Pricing)>? table = null)
    {
        table ??= DefaultTable;
        if (string.IsNullOrEmpty(model)) return null;
        var m = model.ToLowerInvariant();
        foreach (var (key, pricing) in table)
            if (m.Contains(key, StringComparison.Ordinal))
                return pricing;
        return null;
    }

    /// <summary>Cost of one record given explicit pricing.</summary>
    public static decimal CostUsd(TokenUsageRecord r, ModelPricing p)
        => (r.InputTokens * p.InputPerMTok
            + r.OutputTokens * p.OutputPerMTok
            + r.CacheCreateTokens * p.CacheWritePerMTok
            + r.CacheReadTokens * p.CacheReadPerMTok) / 1_000_000m;

    /// <summary>Cost of one record using the default table; 0 if the model is unknown.</summary>
    public static decimal CostUsd(TokenUsageRecord r)
    {
        var p = Lookup(r.Model);
        return p is null ? 0m : CostUsd(r, p);
    }

    /// <summary>Total estimated cost over many records (unknown models contribute 0).</summary>
    public static decimal TotalUsd(IEnumerable<TokenUsageRecord> records)
        => records.Sum(CostUsd);

    /// <summary>Cost grouped by model, most expensive first.</summary>
    public static IReadOnlyList<(string Model, decimal Usd, long Records)> ByModel(IEnumerable<TokenUsageRecord> records)
        => records
            .GroupBy(r => r.Model)
            .Select(g => (Model: g.Key, Usd: g.Sum(CostUsd), Records: (long)g.Count()))
            .OrderByDescending(x => x.Usd)
            .ToList();
}
