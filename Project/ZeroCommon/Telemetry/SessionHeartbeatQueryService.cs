using Agent.Common.Data;

namespace Agent.Common.Telemetry;

/// <summary>
/// Read-only queries over <see cref="Data.Entities.SessionHeartbeat"/>.
/// Mirrors the TokenRemainingQueryService pattern — short-lived
/// <see cref="AppDbContext"/>, dispose after each query.
/// </summary>
public static class SessionHeartbeatQueryService
{
    public sealed record ActiveSession(
        string AccountKey,
        string SessionId,
        string Cwd,
        string ProjectDir,
        string Model,
        DateTime FirstSeenUtc,
        DateTime LastSeenUtc,
        long TickCount);

    /// <summary>
    /// Sessions whose <see cref="Data.Entities.SessionHeartbeat.LastSeenUtc"/>
    /// falls within the last <paramref name="window"/>. Ordered freshest-first.
    /// </summary>
    public static IReadOnlyList<ActiveSession> GetActive(TimeSpan window)
    {
        if (window <= TimeSpan.Zero) return Array.Empty<ActiveSession>();
        var since = DateTime.UtcNow - window;

        using var db = new AppDbContext();
        db.Database.EnsureCreated();

        // EF 10 preview can't translate record positional-ctor projections
        // — same pattern as TokenRemainingQueryService. Materialize, then
        // map. The (LastSeenUtc DESC) index serves the WHERE + ORDER BY
        // with one range scan.
        return db.SessionHeartbeats
            .Where(h => h.LastSeenUtc >= since)
            .OrderByDescending(h => h.LastSeenUtc)
            .Select(h => new
            {
                h.AccountKey, h.SessionId, h.Cwd, h.ProjectDir, h.Model,
                h.FirstSeenUtc, h.LastSeenUtc, h.TickCount,
            })
            .ToList()
            .Select(h => new ActiveSession(
                h.AccountKey, h.SessionId, h.Cwd, h.ProjectDir, h.Model,
                h.FirstSeenUtc, h.LastSeenUtc, h.TickCount))
            .ToList();
    }

    public sealed record CollectorState(
        bool Running,
        DateTime? LastTickUtc,
        int LastFilesScanned,
        int LastRowsUpserted,
        int LastRowsPruned,
        long TotalRows,
        string? LastError);

    public static CollectorState GetCollectorState()
    {
        long total;
        try
        {
            using var db = new AppDbContext();
            db.Database.EnsureCreated();
            total = db.SessionHeartbeats.LongCount();
        }
        catch { total = 0; }
        var c = SessionHeartbeatCollector.Instance;
        return new CollectorState(
            Running: c.LastTickUtc is not null,
            LastTickUtc: c.LastTickUtc,
            LastFilesScanned: c.LastFilesScanned,
            LastRowsUpserted: c.LastRowsUpserted,
            LastRowsPruned: c.LastRowsPruned,
            TotalRows: total,
            LastError: c.LastError);
    }
}
