using System;
using System.Collections.Generic;
using System.IO;
using Agent.Common.Data.Entities;
using Agent.Common.Telemetry;
using Xunit;

namespace ZeroCommon.Tests;

[Trait("Category", "Budget")]
public sealed class BudgetSettingsTests
{
    private static TokenUsageRecord Rec(string model, long input, long output, DateTime at)
        => new() { Model = model, InputTokens = input, OutputTokens = output, RecordedAt = at };

    // ── EffectiveTable ────────────────────────────────────────────────────
    [Fact]
    public void EffectiveTable_override_wins_first_match_over_default()
    {
        var s = new BudgetSettings();
        s.Overrides.Add(new BudgetSettings.PriceOverride
        {
            Key = "opus", InputPerMTok = 1m, OutputPerMTok = 2m, CacheWritePerMTok = 0m, CacheReadPerMTok = 0m
        });
        var table = s.EffectiveTable();
        var p = TokenCostCalculator.Lookup("claude-opus-4", table);
        Assert.NotNull(p);
        Assert.Equal(1m, p!.InputPerMTok);   // override, not the 15.00 default
        Assert.Equal(2m, p.OutputPerMTok);
    }

    [Fact]
    public void EffectiveTable_ignores_blank_keys_and_appends_defaults()
    {
        var s = new BudgetSettings();
        s.Overrides.Add(new BudgetSettings.PriceOverride { Key = "   " }); // blank → ignored
        var table = s.EffectiveTable();
        // Defaults still present → a known default model resolves.
        Assert.NotNull(TokenCostCalculator.Lookup("sonnet", table));
        // Blank key did not create a phantom match.
        Assert.Equal(TokenCostCalculator.DefaultTable.Count, table.Count);
    }

    [Fact]
    public void EffectiveTable_new_key_extends_coverage()
    {
        var s = new BudgetSettings();
        s.Overrides.Add(new BudgetSettings.PriceOverride
        {
            Key = "gemini", InputPerMTok = 0.5m, OutputPerMTok = 1.5m
        });
        var table = s.EffectiveTable();
        var p = TokenCostCalculator.Lookup("gemini-2.5-pro", table);
        Assert.NotNull(p);
        Assert.Equal(0.5m, p!.InputPerMTok);
    }

    // ── Cost with an override table ───────────────────────────────────────
    [Fact]
    public void CostUsd_uses_override_pricing()
    {
        var s = new BudgetSettings();
        s.Overrides.Add(new BudgetSettings.PriceOverride
        {
            Key = "opus", InputPerMTok = 10m, OutputPerMTok = 0m, CacheWritePerMTok = 0m, CacheReadPerMTok = 0m
        });
        var r = Rec("claude-opus-4", input: 1_000_000, output: 0, at: DateTime.UtcNow);
        // 1M input * $10/M = $10 under the override (vs $15 default).
        Assert.Equal(10m, TokenCostCalculator.CostUsd(r, s.EffectiveTable()));
    }

    // ── Month-to-date filtering ───────────────────────────────────────────
    [Fact]
    public void TotalUsdSince_excludes_records_before_the_window()
    {
        var now = new DateTime(2026, 8, 15, 12, 0, 0, DateTimeKind.Utc);
        var start = BudgetSettings.StartOfMonthUtc(now);
        var records = new List<TokenUsageRecord>
        {
            Rec("opus", 1_000_000, 0, new DateTime(2026, 7, 31, 23, 0, 0, DateTimeKind.Utc)), // last month → excluded
            Rec("opus", 1_000_000, 0, new DateTime(2026, 8,  2,  1, 0, 0, DateTimeKind.Utc)), // this month → included
            Rec("opus", 1_000_000, 0, now),                                                    // this month → included
        };
        var table = new BudgetSettings().EffectiveTable();
        // 2 included records * 1M input * $15/M opus default = $30.
        Assert.Equal(30m, TokenCostCalculator.TotalUsdSince(records, start, table));
    }

    // ── BudgetStatus ──────────────────────────────────────────────────────
    [Fact]
    public void Evaluate_over_and_near_and_nocap()
    {
        var capped = new BudgetSettings { MonthlyCapUsd = 100m };

        var over = capped.Evaluate(120m);
        Assert.True(over.OverBudget);
        Assert.True(over.FractionUsed > 1.0);

        var near = capped.Evaluate(85m);
        Assert.False(near.OverBudget);
        Assert.True(near.NearingCap(0.8));

        var under = capped.Evaluate(10m);
        Assert.False(under.OverBudget);
        Assert.False(under.NearingCap(0.8));

        var noCap = new BudgetSettings { MonthlyCapUsd = 0m }.Evaluate(9999m);
        Assert.False(noCap.OverBudget);       // no cap never trips
        Assert.Equal(0d, noCap.FractionUsed);
    }

    // ── Store round-trip ──────────────────────────────────────────────────
    [Fact]
    public void Store_roundtrips_and_defaults_on_missing_or_corrupt()
    {
        var dir = Path.Combine(Path.GetTempPath(), "az-budget-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "budget-settings.json");
        try
        {
            // Missing → defaults.
            var fresh = BudgetSettingsStore.Load(path);
            Assert.Equal(0m, fresh.MonthlyCapUsd);
            Assert.Empty(fresh.Overrides);

            // Round-trip.
            var s = new BudgetSettings { MonthlyCapUsd = 42.5m };
            s.Overrides.Add(new BudgetSettings.PriceOverride { Key = "opus", InputPerMTok = 9m });
            BudgetSettingsStore.Save(s, path);
            var loaded = BudgetSettingsStore.Load(path);
            Assert.Equal(42.5m, loaded.MonthlyCapUsd);
            Assert.Single(loaded.Overrides);
            Assert.Equal("opus", loaded.Overrides[0].Key);
            Assert.Equal(9m, loaded.Overrides[0].InputPerMTok);

            // Corrupt → defaults, no throw.
            File.WriteAllText(path, "{ not valid json ");
            var recovered = BudgetSettingsStore.Load(path);
            Assert.Equal(0m, recovered.MonthlyCapUsd);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
