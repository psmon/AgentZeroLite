using System.IO;
using System.Text.Json;
using Agent.Common.Data;
using Agent.Common.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Agent.Common.Telemetry;

/// <summary>
/// Reads per-session snapshot files written by wrapper v3 and UPSERTs
/// <see cref="SessionHeartbeat"/> rows. One row per (AccountKey, SessionId)
/// pair; every tick refreshes <see cref="SessionHeartbeat.LastSeenUtc"/>
/// and bumps <see cref="SessionHeartbeat.TickCount"/>.
///
/// Snapshot path layout (wrapper v3, M0012):
///   %LOCALAPPDATA%\AgentZeroLite\cc-hud-snapshots\{account}\{sessionId}.json
///
/// Payload (JSON, written atomically via tmp+rename):
///   {
///     "wrapperVersion": "3.0",
///     "account":        "claude-pel3",
///     "sessionId":      "8a3f4b21-...",
///     "cwd":            "C:/Code/AI/AgentZeroLite",
///     "projectDir":     "C:/Code/AI/AgentZeroLite",
///     "writtenAt":      "2026-05-04T17:48:12.123Z",
///     "model":          "Opus 4.7 (1M context)",
///     "fiveHour":       { ... },   // ignored by this collector
///     "sevenDay":       { ... }    // ignored by this collector
///   }
///
/// This collector is a heartbeat aggregator — it cares only about
/// (account, session, cwd, projectDir, model, writtenAt). Rate-limit
/// fields are owned by <see cref="TokenRemainingCollector"/> which writes
/// to a different table (state-change log) and reads the same files.
/// Both collectors run in parallel; neither depends on the other.
///
/// Heartbeat rows that haven't been touched in &gt; 1 day are pruned at
/// the start of each tick to keep the table from growing unbounded
/// (orphaned sessions from previous app runs etc.).
/// </summary>
public sealed class SessionHeartbeatCollector
{
    public static SessionHeartbeatCollector Instance { get; } = new();

    private readonly object _gate = new();
    private System.Threading.Timer? _timer;
    private CancellationTokenSource? _cts;
    private Task _running = Task.CompletedTask;
    private bool _ticking;

    public static TimeSpan DefaultInterval { get; } = TimeSpan.FromSeconds(15);
    public TimeSpan PollInterval { get; private set; } = DefaultInterval;

    public DateTime? LastTickUtc { get; private set; }
    public int LastFilesScanned { get; private set; }
    public int LastRowsUpserted { get; private set; }
    public int LastRowsPruned { get; private set; }
    public string? LastError { get; private set; }

    public event Action<TickSummary>? TickCompleted;

    public sealed record TickSummary(
        int FilesScanned,
        int RowsUpserted,
        int RowsPruned,
        DateTime FinishedAtUtc,
        string? Error);

    public static string SnapshotsDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AgentZeroLite",
        "cc-hud-snapshots");

    /// <summary>Heartbeats older than this are pruned each tick.</summary>
    public static TimeSpan StaleAfter { get; } = TimeSpan.FromDays(1);

    public void Start(TimeSpan? interval = null)
    {
        lock (_gate)
        {
            if (_timer is not null) return;
            PollInterval = interval ?? DefaultInterval;
            _cts = new CancellationTokenSource();
            // Stagger start by 7 s — the rate-limit collector starts at +5 s
            // (TokenRemainingCollector.Start), so two collectors don't fire
            // simultaneously on app boot. After that, 15 s cadence which is
            // tighter than rate-limit's 30 s because heartbeats need quicker
            // "active session is gone" feedback (3-5 min UI window).
            _timer = new System.Threading.Timer(OnTick, null, TimeSpan.FromSeconds(7), PollInterval);
            AppLogger.Log($"[SessionHeartbeatCollector] started (interval={PollInterval.TotalSeconds:N0}s, dir={SnapshotsDir})");
        }
    }

    public void Stop()
    {
        Task running;
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            _cts?.Cancel();
            running = _running;
        }
        try { running.Wait(TimeSpan.FromSeconds(5)); } catch { /* best-effort */ }
        AppLogger.Log("[SessionHeartbeatCollector] stopped");
    }

    public Task<TickSummary> TickNowAsync(CancellationToken ct = default)
        => Task.Run(() => RunTick(ct), ct);

    private void OnTick(object? _)
    {
        lock (_gate)
        {
            if (_ticking) return;
            _ticking = true;
            _running = Task.Run(() =>
            {
                try { RunTick(_cts?.Token ?? CancellationToken.None); }
                finally { lock (_gate) _ticking = false; }
            });
        }
    }

    private TickSummary RunTick(CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        int files = 0, upserted = 0, pruned = 0;
        string? error = null;

        try
        {
            if (!Directory.Exists(SnapshotsDir))
            {
                var emptySummary = new TickSummary(0, 0, 0, DateTime.UtcNow, null);
                LastTickUtc = emptySummary.FinishedAtUtc;
                LastFilesScanned = 0; LastRowsUpserted = 0; LastRowsPruned = 0;
                LastError = null;
                try { TickCompleted?.Invoke(emptySummary); } catch { }
                return emptySummary;
            }

            using var db = new AppDbContext();
            db.Database.EnsureCreated();

            // 1. Prune stale rows first — keeps the table small for the
            //    UPSERT lookup below.
            var cutoff = DateTime.UtcNow - StaleAfter;
            pruned = db.SessionHeartbeats
                .Where(h => h.LastSeenUtc < cutoff)
                .ExecuteDelete();

            // 2. Scan v3 per-session snapshot files.
            //    Recurse so we pick up `cc-hud-snapshots/{account}/{session}.json`.
            //    Skip flat files at the root (legacy v2 — token-remaining
            //    collector handles those, and they don't carry sessionId).
            foreach (var file in Directory.EnumerateFiles(SnapshotsDir, "*.json", SearchOption.AllDirectories))
            {
                if (ct.IsCancellationRequested) break;

                // Files at the root (legacy v2) have no session info — skip.
                var parent = Path.GetDirectoryName(file);
                if (parent is null || string.Equals(
                        Path.GetFullPath(parent),
                        Path.GetFullPath(SnapshotsDir),
                        StringComparison.OrdinalIgnoreCase)) continue;

                files++;

                var snap = TryReadSnapshot(file);
                if (snap is null) continue;
                if (string.IsNullOrEmpty(snap.SessionId) || string.IsNullOrEmpty(snap.AccountKey)) continue;

                var existing = db.SessionHeartbeats
                    .FirstOrDefault(h => h.AccountKey == snap.AccountKey && h.SessionId == snap.SessionId);

                if (existing is null)
                {
                    db.SessionHeartbeats.Add(new SessionHeartbeat
                    {
                        AccountKey    = snap.AccountKey,
                        SessionId     = snap.SessionId,
                        Cwd           = snap.Cwd,
                        ProjectDir    = snap.ProjectDir,
                        Model         = snap.Model,
                        FirstSeenUtc  = snap.WrittenAtUtc,
                        LastSeenUtc   = snap.WrittenAtUtc,
                        TickCount     = 1,
                    });
                    upserted++;
                }
                else if (snap.WrittenAtUtc > existing.LastSeenUtc)
                {
                    existing.LastSeenUtc = snap.WrittenAtUtc;
                    existing.Cwd         = snap.Cwd;        // refresh — could move within session
                    existing.ProjectDir  = snap.ProjectDir;
                    existing.Model       = snap.Model;
                    existing.TickCount  += 1;
                    upserted++;
                }
                // else: snapshot is older than what we have (shouldn't happen
                // unless clock skew) — ignore silently.
            }

            db.SaveChanges();
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            AppLogger.LogError("[SessionHeartbeatCollector] tick failed", ex);
        }

        var summary = new TickSummary(files, upserted, pruned, DateTime.UtcNow, error);
        LastTickUtc = summary.FinishedAtUtc;
        LastFilesScanned = files;
        LastRowsUpserted = upserted;
        LastRowsPruned = pruned;
        LastError = error;

        try { TickCompleted?.Invoke(summary); } catch { /* listener errors don't kill the collector */ }
        AppLogger.Log($"[SessionHeartbeatCollector] tick: files={files} upserts={upserted} pruned={pruned} elapsed={(DateTime.UtcNow - started).TotalSeconds:N1}s err={(error ?? "none")}");
        return summary;
    }

    private sealed record SnapshotPayload(
        string AccountKey,
        string SessionId,
        string Cwd,
        string ProjectDir,
        string Model,
        DateTime WrittenAtUtc);

    private static SnapshotPayload? TryReadSnapshot(string file)
    {
        string text;
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            text = reader.ReadToEnd();
        }
        catch (IOException) { return null; }
        if (string.IsNullOrWhiteSpace(text)) return null;

        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;

            var account    = ReadStr(root, "account");
            var sessionId  = ReadStr(root, "sessionId");
            var cwd        = ReadStr(root, "cwd");
            var projectDir = ReadStr(root, "projectDir");
            var model      = ReadStr(root, "model");

            if (string.IsNullOrEmpty(account))
                account = Path.GetFileName(Path.GetDirectoryName(file)) ?? "";
            if (string.IsNullOrEmpty(sessionId))
                sessionId = Path.GetFileNameWithoutExtension(file);

            DateTime writtenAt = DateTime.UtcNow;
            if (root.TryGetProperty("writtenAt", out var wEl) && wEl.ValueKind == JsonValueKind.String
                && DateTime.TryParse(wEl.GetString(), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
                writtenAt = parsed.ToUniversalTime();

            return new SnapshotPayload(account, sessionId, cwd, projectDir, model, writtenAt);
        }
        catch { return null; }
    }

    private static string ReadStr(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? (el.GetString() ?? "") : "";
}
