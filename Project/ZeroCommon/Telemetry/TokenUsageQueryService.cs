using Agent.Common.Data;
using Agent.Common.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Agent.Common.Telemetry;

/// <summary>
/// Read-side helpers for the token-monitor dashboard. Pure SQL/EF queries
/// against <see cref="TokenUsageRecord"/>; never mutates state.
/// </summary>
public static class TokenUsageQueryService
{
    public sealed record TotalsRow(
        long Input, long Output, long CacheCreate, long CacheRead,
        long Reasoning, long Total, long Records);

    public sealed record VendorTotalsRow(
        string Vendor, long Input, long Output, long CacheCreate, long CacheRead,
        long Reasoning, long Total, long Records);

    public sealed record AccountTotalsRow(
        string Vendor, string AccountKey, long Input, long Output,
        long CacheCreate, long CacheRead, long Reasoning, long Total, long Records);

    public sealed record TimePoint(DateTime BucketUtc, string Vendor, long Input, long Output, long Total);

    public sealed record SessionRow(
        string Vendor, string SessionId, string Cwd, string Model,
        DateTime LastSeen, long Input, long Output, long Total, long Records);

    public sealed record RecentRow(
        long Id, string Vendor, string AccountKey, string Model,
        string SessionId, string Cwd, DateTime RecordedAt,
        long Input, long Output, long CacheCreate, long CacheRead, long Reasoning);

    public sealed record CollectorState(
        int FilesTracked, long TotalLines, DateTime? LastUpdatedUtc);

    public sealed record AliasRow(int Id, string Vendor, string AccountKey, string Alias, DateTime UpdatedAt);

    public sealed record ProfileRow(string Vendor, string AccountKey, int FileCount, long LineCount, DateTime? LastUpdatedUtc);

    public sealed record ProjectRow(
        string Project, string PathSample, string Vendors,
        long Input, long Output, long CacheCreate, long CacheRead, long Reasoning,
        long Total, long Sessions, DateTime LastSeen);

    public static TotalsRow GetTotals(DateTime? sinceUtc = null)
    {
        // EF Core 10 (preview) can't translate positional-ctor record
        // projections inside GroupBy.Select — project to an anonymous shape
        // SQL can lower, then map to the record after materialization.
        using var db = new AppDbContext();
        var q = db.TokenUsageRecords.AsQueryable();
        if (sinceUtc is { } since) q = q.Where(r => r.RecordedAt >= since);

        var raw = q
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Input = g.Sum(r => r.InputTokens),
                Output = g.Sum(r => r.OutputTokens),
                CacheCreate = g.Sum(r => r.CacheCreateTokens),
                CacheRead = g.Sum(r => r.CacheReadTokens),
                Reasoning = g.Sum(r => r.ReasoningTokens),
                Records = g.LongCount()
            })
            .FirstOrDefault();

        if (raw is null) return new TotalsRow(0, 0, 0, 0, 0, 0, 0);
        return new TotalsRow(
            raw.Input, raw.Output, raw.CacheCreate, raw.CacheRead, raw.Reasoning,
            raw.Input + raw.Output + raw.CacheCreate + raw.CacheRead + raw.Reasoning,
            raw.Records);
    }

    public static List<VendorTotalsRow> GetByVendor(DateTime? sinceUtc = null)
    {
        using var db = new AppDbContext();
        var q = db.TokenUsageRecords.AsQueryable();
        if (sinceUtc is { } since) q = q.Where(r => r.RecordedAt >= since);

        var rows = q
            .GroupBy(r => r.Vendor)
            .Select(g => new
            {
                Vendor = g.Key,
                Input = g.Sum(r => r.InputTokens),
                Output = g.Sum(r => r.OutputTokens),
                CacheCreate = g.Sum(r => r.CacheCreateTokens),
                CacheRead = g.Sum(r => r.CacheReadTokens),
                Reasoning = g.Sum(r => r.ReasoningTokens),
                Records = g.LongCount()
            })
            .ToList();

        return rows
            .Select(r => new VendorTotalsRow(
                r.Vendor ?? "",
                r.Input, r.Output, r.CacheCreate, r.CacheRead, r.Reasoning,
                r.Input + r.Output + r.CacheCreate + r.CacheRead + r.Reasoning,
                r.Records))
            .OrderByDescending(r => r.Total)
            .ToList();
    }

    public static List<AccountTotalsRow> GetByAccount(DateTime? sinceUtc = null)
    {
        using var db = new AppDbContext();
        var q = db.TokenUsageRecords.AsQueryable();
        if (sinceUtc is { } since) q = q.Where(r => r.RecordedAt >= since);

        var rows = q
            .GroupBy(r => new { r.Vendor, r.AccountKey })
            .Select(g => new
            {
                g.Key.Vendor,
                g.Key.AccountKey,
                Input = g.Sum(r => r.InputTokens),
                Output = g.Sum(r => r.OutputTokens),
                CacheCreate = g.Sum(r => r.CacheCreateTokens),
                CacheRead = g.Sum(r => r.CacheReadTokens),
                Reasoning = g.Sum(r => r.ReasoningTokens),
                Records = g.LongCount()
            })
            .ToList();

        return rows
            .Select(r => new AccountTotalsRow(
                r.Vendor ?? "",
                r.AccountKey ?? "",
                r.Input, r.Output, r.CacheCreate, r.CacheRead, r.Reasoning,
                r.Input + r.Output + r.CacheCreate + r.CacheRead + r.Reasoning,
                r.Records))
            .OrderByDescending(r => r.Total)
            .ToList();
    }

    /// <summary>
    /// Time-bucketed series for the dashboard chart. Buckets are aligned to
    /// the requested width (default = 1 hour); SQLite-friendly because we
    /// reduce to a numeric epoch then group.
    /// </summary>
    public static List<TimePoint> GetTimeSeries(int rangeHours, int bucketMinutes)
    {
        if (bucketMinutes <= 0) bucketMinutes = 60;
        var since = DateTime.UtcNow.AddHours(-Math.Max(1, rangeHours));
        var bucketSec = bucketMinutes * 60;

        using var db = new AppDbContext();

        // Materialize first — SQLite EF can't always lower (epoch / N) * N,
        // and the data volume is tiny (a few hundred rows / day).
        var rows = db.TokenUsageRecords
            .Where(r => r.RecordedAt >= since)
            .Select(r => new {
                r.RecordedAt,
                r.Vendor,
                r.InputTokens,
                r.OutputTokens,
                r.CacheCreateTokens,
                r.CacheReadTokens,
                r.ReasoningTokens
            })
            .ToList();

        return rows
            .GroupBy(r =>
            {
                var epoch = new DateTimeOffset(r.RecordedAt, TimeSpan.Zero).ToUnixTimeSeconds();
                var bucket = (epoch / bucketSec) * bucketSec;
                return new
                {
                    Bucket = DateTimeOffset.FromUnixTimeSeconds(bucket).UtcDateTime,
                    r.Vendor
                };
            })
            .Select(g => new TimePoint(
                g.Key.Bucket,
                g.Key.Vendor,
                g.Sum(r => r.InputTokens),
                g.Sum(r => r.OutputTokens),
                g.Sum(r => r.InputTokens + r.OutputTokens + r.CacheCreateTokens + r.CacheReadTokens + r.ReasoningTokens)))
            .OrderBy(p => p.BucketUtc)
            .ThenBy(p => p.Vendor)
            .ToList();
    }

    public static List<SessionRow> GetActiveSessions(DateTime? sinceUtc = null, int limit = 20)
    {
        // Two-step query: aggregate first (translatable), then resolve the
        // "latest cwd / model per group" by sorting the materialized result
        // and joining against the small per-session set. SQLite can't do
        // FIRST_VALUE-style lookups inside a GroupBy projection in EF 10,
        // so we look up the latest record per group separately.
        using var db = new AppDbContext();
        var q = db.TokenUsageRecords.AsQueryable();
        if (sinceUtc is { } since) q = q.Where(r => r.RecordedAt >= since);

        var aggregates = q
            .GroupBy(r => new { r.Vendor, r.SessionId })
            .Select(g => new
            {
                g.Key.Vendor,
                g.Key.SessionId,
                LastSeen = g.Max(x => x.RecordedAt),
                Input = g.Sum(r => r.InputTokens),
                Output = g.Sum(r => r.OutputTokens),
                CacheCreate = g.Sum(r => r.CacheCreateTokens),
                CacheRead = g.Sum(r => r.CacheReadTokens),
                Reasoning = g.Sum(r => r.ReasoningTokens),
                Records = g.LongCount()
            })
            .OrderByDescending(a => a.LastSeen)
            .Take(Math.Clamp(limit, 1, 200))
            .ToList();

        if (aggregates.Count == 0) return new List<SessionRow>();

        // Resolve cwd/model for each (vendor, sessionId) by reading the
        // single newest record. Cheap because the set is small (<= limit).
        var result = new List<SessionRow>(aggregates.Count);
        foreach (var a in aggregates)
        {
            var latest = db.TokenUsageRecords
                .Where(r => r.Vendor == a.Vendor && r.SessionId == a.SessionId)
                .OrderByDescending(r => r.RecordedAt)
                .Select(r => new { r.Cwd, r.Model })
                .FirstOrDefault();

            result.Add(new SessionRow(
                a.Vendor ?? "",
                a.SessionId ?? "",
                latest?.Cwd ?? "",
                latest?.Model ?? "",
                a.LastSeen,
                a.Input,
                a.Output,
                a.Input + a.Output + a.CacheCreate + a.CacheRead + a.Reasoning,
                a.Records));
        }
        return result;
    }

    public static List<RecentRow> GetRecent(int limit = 50)
    {
        using var db = new AppDbContext();
        var rows = db.TokenUsageRecords
            .OrderByDescending(r => r.RecordedAt)
            .Take(Math.Clamp(limit, 1, 500))
            .Select(r => new
            {
                r.Id, r.Vendor, r.AccountKey, r.Model, r.SessionId, r.Cwd, r.RecordedAt,
                r.InputTokens, r.OutputTokens, r.CacheCreateTokens, r.CacheReadTokens, r.ReasoningTokens
            })
            .ToList();

        return rows
            .Select(r => new RecentRow(
                r.Id, r.Vendor ?? "", r.AccountKey ?? "", r.Model ?? "",
                r.SessionId ?? "", r.Cwd ?? "", r.RecordedAt,
                r.InputTokens, r.OutputTokens, r.CacheCreateTokens, r.CacheReadTokens, r.ReasoningTokens))
            .ToList();
    }

    public static CollectorState GetCollectorState()
    {
        using var db = new AppDbContext();
        // Avoid GroupBy(_=>1) — EF 10 preview can't always lower it. Run
        // three trivial aggregates instead.
        var anyRow = db.TokenSourceCheckpoints.Any();
        if (!anyRow) return new CollectorState(0, 0, null);

        var files = db.TokenSourceCheckpoints.Count();
        var lines = db.TokenSourceCheckpoints.Sum(x => x.LineCount);
        var latest = db.TokenSourceCheckpoints.Max(x => (DateTime?)x.UpdatedAt);
        return new CollectorState(files, lines, latest);
    }

    /// <summary>
    /// Aggregate by project, where project = leaf folder name of the cwd
    /// stored on each row. Materializes first because <see cref="Path.GetFileName"/>
    /// isn't translatable to SQL — the volume (rows × distinct cwds) is
    /// small enough that client-side grouping is fine.
    /// </summary>
    public static List<ProjectRow> GetByProject(DateTime? sinceUtc = null, int limit = 50)
    {
        using var db = new AppDbContext();
        var q = db.TokenUsageRecords.AsQueryable();
        if (sinceUtc is { } since) q = q.Where(r => r.RecordedAt >= since);

        var rows = q
            .Select(r => new
            {
                r.Cwd, r.Vendor, r.SessionId, r.RecordedAt,
                r.InputTokens, r.OutputTokens, r.CacheCreateTokens,
                r.CacheReadTokens, r.ReasoningTokens
            })
            .ToList();

        return rows
            .GroupBy(r => ProjectKey(r.Cwd))
            .Select(g =>
            {
                var vendors = string.Join(" · ",
                    g.Select(x => x.Vendor ?? "")
                     .Distinct()
                     .OrderBy(v => v)
                     .Select(VendorAbbrev));
                // Pick a representative path — the most recently seen cwd
                // so a project that moved disks keeps showing the live one.
                var pathSample = g
                    .OrderByDescending(x => x.RecordedAt)
                    .Select(x => x.Cwd)
                    .FirstOrDefault() ?? "";
                return new ProjectRow(
                    g.Key,
                    pathSample,
                    vendors,
                    g.Sum(x => x.InputTokens),
                    g.Sum(x => x.OutputTokens),
                    g.Sum(x => x.CacheCreateTokens),
                    g.Sum(x => x.CacheReadTokens),
                    g.Sum(x => x.ReasoningTokens),
                    g.Sum(x => x.InputTokens + x.OutputTokens + x.CacheCreateTokens + x.CacheReadTokens + x.ReasoningTokens),
                    g.Select(x => x.SessionId).Distinct().LongCount(),
                    g.Max(x => x.RecordedAt));
            })
            .OrderByDescending(r => r.Total)
            .Take(Math.Clamp(limit, 1, 500))
            .ToList();
    }

    private static string ProjectKey(string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return "(unknown)";
        try
        {
            // Normalize trailing separators so "D:\foo\" and "D:\foo" group.
            var trimmed = cwd.TrimEnd('\\', '/');
            if (string.IsNullOrEmpty(trimmed)) return "(unknown)";
            var leaf = Path.GetFileName(trimmed);
            return string.IsNullOrEmpty(leaf) ? trimmed : leaf;
        }
        catch
        {
            return "(unknown)";
        }
    }

    private static string VendorAbbrev(string vendor) => vendor switch
    {
        "anthropic" => "A",
        "openai"    => "O",
        ""          => "?",
        _           => vendor,
    };

    /// <summary>One row per (vendor, account) seen in the checkpoint table —
    /// drives the Profile/Account selector in the dashboard.</summary>
    public static List<ProfileRow> GetProfiles()
    {
        using var db = new AppDbContext();
        var rows = db.TokenSourceCheckpoints
            .GroupBy(c => new { c.Vendor, c.AccountKey })
            .Select(g => new
            {
                g.Key.Vendor,
                g.Key.AccountKey,
                FileCount = g.Count(),
                LineCount = g.Sum(x => x.LineCount),
                Last = g.Max(x => (DateTime?)x.UpdatedAt)
            })
            .ToList();

        return rows
            .Select(r => new ProfileRow(r.Vendor ?? "", r.AccountKey ?? "", r.FileCount, r.LineCount, r.Last))
            .OrderBy(r => r.Vendor)
            .ThenBy(r => r.AccountKey)
            .ToList();
    }

    public static List<AliasRow> ListAliases()
    {
        using var db = new AppDbContext();
        var rows = db.TokenAccountAliases
            .Select(a => new { a.Id, a.Vendor, a.AccountKey, a.Alias, a.UpdatedAt })
            .ToList();
        return rows
            .Select(r => new AliasRow(r.Id, r.Vendor ?? "", r.AccountKey ?? "", r.Alias ?? "", r.UpdatedAt))
            .OrderBy(r => r.Vendor)
            .ThenBy(r => r.AccountKey)
            .ToList();
    }

    public static AliasRow SetAlias(string vendor, string accountKey, string alias)
    {
        if (string.IsNullOrWhiteSpace(vendor)) throw new ArgumentException("vendor required", nameof(vendor));
        if (accountKey is null) accountKey = "";
        alias ??= "";

        using var db = new AppDbContext();
        var existing = db.TokenAccountAliases.FirstOrDefault(a => a.Vendor == vendor && a.AccountKey == accountKey);
        if (existing is null)
        {
            existing = new TokenAccountAlias { Vendor = vendor, AccountKey = accountKey, Alias = alias, UpdatedAt = DateTime.UtcNow };
            db.TokenAccountAliases.Add(existing);
        }
        else
        {
            existing.Alias = alias;
            existing.UpdatedAt = DateTime.UtcNow;
        }
        db.SaveChanges();
        return new AliasRow(existing.Id, existing.Vendor, existing.AccountKey, existing.Alias, existing.UpdatedAt);
    }

    public static bool RemoveAlias(string vendor, string accountKey)
    {
        using var db = new AppDbContext();
        var existing = db.TokenAccountAliases.FirstOrDefault(a => a.Vendor == vendor && a.AccountKey == accountKey);
        if (existing is null) return false;
        db.TokenAccountAliases.Remove(existing);
        db.SaveChanges();
        return true;
    }
}
