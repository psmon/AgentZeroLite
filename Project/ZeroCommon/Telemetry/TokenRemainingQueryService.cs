using Agent.Common.Data;
using Microsoft.EntityFrameworkCore;

namespace Agent.Common.Telemetry;

/// <summary>
/// Read-only queries over <see cref="Data.Entities.TokenRemainingObservation"/>.
/// All methods are short-lived: spin up a fresh AppDbContext, run the query,
/// dispose. Mirrors the TokenUsageQueryService pattern.
/// </summary>
public static class TokenRemainingQueryService
{
    /// <summary>Latest row per (account, model) for the given account.</summary>
    public sealed record ModelLatest(
        string Model,
        int FiveHourPercent,
        DateTime? FiveHourResetsAtUtc,
        int SevenDayPercent,
        DateTime? SevenDayResetsAtUtc,
        DateTime ObservedAtUtc,
        long Observations);

    public static IReadOnlyList<ModelLatest> GetLatestForAccount(string accountKey)
    {
        if (string.IsNullOrEmpty(accountKey)) return Array.Empty<ModelLatest>();

        using var db = new AppDbContext();
        db.Database.EnsureCreated();

        // EF 10 preview doesn't translate record positional-ctor projections
        // inside GroupBy.Select cleanly (same pattern used in
        // TokenUsageQueryService) — so materialize anonymous rows then map.
        // Strategy: pull all rows for this account once, group + max-by-time
        // in memory. Per-account data is small (one row per state-change,
        // not per request) so this is cheap.
        var rows = db.TokenRemainingObservations
            .Where(o => o.AccountKey == accountKey)
            .Select(o => new
            {
                o.Model,
                o.FiveHourPercent,
                o.FiveHourResetsAtUtc,
                o.SevenDayPercent,
                o.SevenDayResetsAtUtc,
                o.ObservedAtUtc,
            })
            .ToList();

        return rows
            .GroupBy(r => r.Model, StringComparer.Ordinal)
            .Select(g =>
            {
                var latest = g.OrderByDescending(r => r.ObservedAtUtc).First();
                return new ModelLatest(
                    latest.Model,
                    latest.FiveHourPercent,
                    latest.FiveHourResetsAtUtc,
                    latest.SevenDayPercent,
                    latest.SevenDayResetsAtUtc,
                    latest.ObservedAtUtc,
                    g.LongCount());
            })
            // Most-recently-active model first
            .OrderByDescending(m => m.ObservedAtUtc)
            .ToList();
    }

    /// <summary>Models ever observed under this account — for the settings filter list.</summary>
    public sealed record ObservedModel(
        string Model,
        DateTime LastSeenUtc,
        DateTime FirstSeenUtc,
        long Observations);

    public static IReadOnlyList<ObservedModel> GetObservedModels(string accountKey)
    {
        if (string.IsNullOrEmpty(accountKey)) return Array.Empty<ObservedModel>();

        using var db = new AppDbContext();
        db.Database.EnsureCreated();

        var rows = db.TokenRemainingObservations
            .Where(o => o.AccountKey == accountKey)
            .Select(o => new { o.Model, o.ObservedAtUtc })
            .ToList();

        return rows
            .GroupBy(r => r.Model, StringComparer.Ordinal)
            .Select(g => new ObservedModel(
                g.Key,
                g.Max(r => r.ObservedAtUtc),
                g.Min(r => r.ObservedAtUtc),
                g.LongCount()))
            .OrderByDescending(m => m.LastSeenUtc)
            .ToList();
    }

    /// <summary>Per-account summary — used by Settings "Active account" picker.</summary>
    public sealed record AccountProfile(
        string AccountKey,
        long Observations,
        int ModelCount,
        DateTime? LastSeenUtc);

    public static IReadOnlyList<AccountProfile> GetAccountProfiles()
    {
        using var db = new AppDbContext();
        db.Database.EnsureCreated();

        var rows = db.TokenRemainingObservations
            .Select(o => new { o.AccountKey, o.Model, o.ObservedAtUtc })
            .ToList();

        return rows
            .GroupBy(r => r.AccountKey, StringComparer.Ordinal)
            .Select(g => new AccountProfile(
                g.Key,
                g.LongCount(),
                g.Select(r => r.Model).Distinct(StringComparer.Ordinal).Count(),
                g.Max(r => r.ObservedAtUtc)))
            .OrderByDescending(p => p.LastSeenUtc)
            .ToList();
    }

    /// <summary>Time-series for one model under one account — for future chart support.</summary>
    public sealed record SeriesPoint(
        DateTime ObservedAtUtc,
        int FiveHourPercent,
        int SevenDayPercent);

    public static IReadOnlyList<SeriesPoint> GetSeries(string accountKey, string model, int hours)
    {
        if (string.IsNullOrEmpty(accountKey) || string.IsNullOrEmpty(model)) return Array.Empty<SeriesPoint>();
        var since = DateTime.UtcNow.AddHours(-Math.Max(1, hours));

        using var db = new AppDbContext();
        db.Database.EnsureCreated();

        return db.TokenRemainingObservations
            .Where(o => o.AccountKey == accountKey && o.Model == model && o.ObservedAtUtc >= since)
            .OrderBy(o => o.ObservedAtUtc)
            .Select(o => new { o.ObservedAtUtc, o.FiveHourPercent, o.SevenDayPercent })
            .ToList()
            .Select(o => new SeriesPoint(o.ObservedAtUtc, o.FiveHourPercent, o.SevenDayPercent))
            .ToList();
    }

    /// <summary>Collector status — for the widget footer + Settings.</summary>
    public sealed record CollectorState(
        bool Running,
        DateTime? LastTickUtc,
        int LastFilesScanned,
        int LastRowsInserted,
        int LastRowsSkippedSamePercent,
        long TotalRows,
        string? LastError);

    public static CollectorState GetCollectorState()
    {
        long total;
        try
        {
            using var db = new AppDbContext();
            db.Database.EnsureCreated();
            total = db.TokenRemainingObservations.LongCount();
        }
        catch
        {
            total = 0;
        }
        var c = TokenRemainingCollector.Instance;
        return new CollectorState(
            Running: c.LastTickUtc is not null,
            LastTickUtc: c.LastTickUtc,
            LastFilesScanned: c.LastFilesScanned,
            LastRowsInserted: c.LastRowsInserted,
            LastRowsSkippedSamePercent: c.LastRowsSkippedSamePercent,
            TotalRows: total,
            LastError: c.LastError);
    }
}
