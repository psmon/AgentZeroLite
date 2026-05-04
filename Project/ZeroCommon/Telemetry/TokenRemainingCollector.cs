using System.IO;
using System.Text.Json;
using Agent.Common.Data;
using Agent.Common.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Agent.Common.Telemetry;

/// <summary>
/// Reads per-account snapshot files written by the AgentZero statusLine
/// wrapper and persists state-changes to <see cref="AppDbContext"/>.
///
/// Snapshot format (one JSON file per account, written by the wrapper on
/// every Claude Code statusline tick):
///   {
///     "account":  "claude-pel3",
///     "writtenAt":"2026-05-04T17:48:12.123Z",
///     "model":    "Opus 4.7 (1M context)",
///     "fiveHour": { "usedPercentage": 19, "resetsAt": 1714829292 },
///     "sevenDay": { "usedPercentage": 9,  "resetsAt": 1715347692 }
///   }
///
/// Default tick: 30 s. On each tick:
///   1. enumerate %LOCALAPPDATA%\AgentZeroLite\cc-hud-snapshots\*.json
///   2. for each file, load + parse + look up latest existing row for
///      (AccountKey, Model)
///   3. INSERT only if (5h%, 7d%, resetsAt) differs (dedupe — operator's
///      "마지막 값이 중요" requirement: latest snapshot in DB matches
///      reality, but identical ticks don't bloat the table)
/// </summary>
public sealed class TokenRemainingCollector
{
    public static TokenRemainingCollector Instance { get; } = new();

    private readonly object _gate = new();
    private System.Threading.Timer? _timer;
    private CancellationTokenSource? _cts;
    private Task _running = Task.CompletedTask;
    private bool _ticking;

    public static TimeSpan DefaultInterval { get; } = TimeSpan.FromSeconds(30);
    public TimeSpan PollInterval { get; private set; } = DefaultInterval;

    public DateTime? LastTickUtc { get; private set; }
    public int LastFilesScanned { get; private set; }
    public int LastRowsInserted { get; private set; }
    public int LastRowsSkippedSamePercent { get; private set; }
    public string? LastError { get; private set; }

    public event Action<TickSummary>? TickCompleted;

    public sealed record TickSummary(
        int FilesScanned,
        int RowsInserted,
        int RowsSkippedSamePercent,
        DateTime FinishedAtUtc,
        string? Error);

    public static string SnapshotsDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AgentZeroLite",
        "cc-hud-snapshots");

    public void Start(TimeSpan? interval = null)
    {
        lock (_gate)
        {
            if (_timer is not null) return;
            PollInterval = interval ?? DefaultInterval;
            _cts = new CancellationTokenSource();
            // First fire after 5 s — same stagger pattern as
            // TokenUsageCollector so startup isn't dominated by both
            // collectors firing simultaneously.
            _timer = new System.Threading.Timer(OnTick, null, TimeSpan.FromSeconds(5), PollInterval);
            AppLogger.Log($"[TokenRemainingCollector] started (interval={PollInterval.TotalSeconds:N0}s, dir={SnapshotsDir})");
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
        AppLogger.Log("[TokenRemainingCollector] stopped");
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
        int files = 0, inserted = 0, skipped = 0;
        string? error = null;

        try
        {
            if (!Directory.Exists(SnapshotsDir))
            {
                // Nothing installed yet — no-op tick.
                var emptySummary = new TickSummary(0, 0, 0, DateTime.UtcNow, null);
                LastTickUtc = emptySummary.FinishedAtUtc;
                LastFilesScanned = 0; LastRowsInserted = 0; LastRowsSkippedSamePercent = 0;
                LastError = null;
                try { TickCompleted?.Invoke(emptySummary); } catch { }
                return emptySummary;
            }

            using var db = new AppDbContext();
            db.Database.EnsureCreated();

            foreach (var file in Directory.EnumerateFiles(SnapshotsDir, "*.json"))
            {
                if (ct.IsCancellationRequested) break;
                files++;

                var snap = TryReadSnapshot(file);
                if (snap is null) continue;

                // Look up the latest existing row for this (account, model)
                var latest = db.TokenRemainingObservations
                    .Where(o => o.AccountKey == snap.AccountKey && o.Model == snap.Model)
                    .OrderByDescending(o => o.ObservedAtUtc)
                    .FirstOrDefault();

                bool changed = latest is null
                    || latest.FiveHourPercent != snap.FiveHourPercent
                    || latest.SevenDayPercent != snap.SevenDayPercent
                    || NotSameDateTime(latest.FiveHourResetsAtUtc, snap.FiveHourResetsAtUtc)
                    || NotSameDateTime(latest.SevenDayResetsAtUtc, snap.SevenDayResetsAtUtc);

                if (!changed) { skipped++; continue; }

                db.TokenRemainingObservations.Add(new TokenRemainingObservation
                {
                    Vendor              = "claude",
                    AccountKey          = snap.AccountKey,
                    Model               = snap.Model,
                    FiveHourPercent     = snap.FiveHourPercent,
                    FiveHourResetsAtUtc = snap.FiveHourResetsAtUtc,
                    SevenDayPercent     = snap.SevenDayPercent,
                    SevenDayResetsAtUtc = snap.SevenDayResetsAtUtc,
                    ObservedAtUtc       = snap.WrittenAtUtc,
                });
                inserted++;
            }

            db.SaveChanges();
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            AppLogger.LogError("[TokenRemainingCollector] tick failed", ex);
        }

        var summary = new TickSummary(files, inserted, skipped, DateTime.UtcNow, error);
        LastTickUtc = summary.FinishedAtUtc;
        LastFilesScanned = files;
        LastRowsInserted = inserted;
        LastRowsSkippedSamePercent = skipped;
        LastError = error;

        try { TickCompleted?.Invoke(summary); } catch { /* listener errors don't kill the collector */ }
        AppLogger.Log($"[TokenRemainingCollector] tick: files={files} +rows={inserted} skipped={skipped} elapsed={(DateTime.UtcNow - started).TotalSeconds:N1}s err={(error ?? "none")}");
        return summary;
    }

    private static bool NotSameDateTime(DateTime? a, DateTime? b)
    {
        if (a is null && b is null) return false;
        if (a is null || b is null) return true;
        // Compare to second precision — wrapper writes resetsAt as epoch
        // seconds, so sub-second diffs are noise.
        return Math.Abs((a.Value - b.Value).TotalSeconds) > 1.0;
    }

    private sealed record SnapshotPayload(
        string AccountKey,
        string Model,
        int FiveHourPercent,
        DateTime? FiveHourResetsAtUtc,
        int SevenDayPercent,
        DateTime? SevenDayResetsAtUtc,
        DateTime WrittenAtUtc);

    private static SnapshotPayload? TryReadSnapshot(string file)
    {
        string text;
        try
        {
            // Wrapper writes via temp+rename, but on the off-chance we open
            // mid-rename, FileShare lets us read a previous version.
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            text = reader.ReadToEnd();
        }
        catch (IOException)
        {
            return null;
        }
        if (string.IsNullOrWhiteSpace(text)) return null;

        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;

            var account = ReadStr(root, "account");
            var model   = ReadStr(root, "model");
            // Account is required (filename should match anyway), model can be missing.
            if (string.IsNullOrEmpty(account))
                account = Path.GetFileNameWithoutExtension(file);
            if (string.IsNullOrEmpty(account)) return null;
            if (string.IsNullOrEmpty(model)) return null;

            int fh = 0, sd = 0;
            DateTime? fhReset = null, sdReset = null;
            if (root.TryGetProperty("fiveHour", out var fhEl) && fhEl.ValueKind == JsonValueKind.Object)
            {
                fh = ReadInt(fhEl, "usedPercentage");
                fhReset = ReadEpochSecondsUtc(fhEl, "resetsAt");
            }
            if (root.TryGetProperty("sevenDay", out var sdEl) && sdEl.ValueKind == JsonValueKind.Object)
            {
                sd = ReadInt(sdEl, "usedPercentage");
                sdReset = ReadEpochSecondsUtc(sdEl, "resetsAt");
            }

            DateTime writtenAt = DateTime.UtcNow;
            if (root.TryGetProperty("writtenAt", out var wEl) && wEl.ValueKind == JsonValueKind.String
                && DateTime.TryParse(wEl.GetString(), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
                writtenAt = parsed.ToUniversalTime();

            return new SnapshotPayload(account, model, fh, fhReset, sd, sdReset, writtenAt);
        }
        catch
        {
            return null;
        }
    }

    private static string ReadStr(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? (el.GetString() ?? "") : "";

    private static int ReadInt(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el)) return 0;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var v)) return Math.Clamp(v, 0, 100);
        return 0;
    }

    private static DateTime? ReadEpochSecondsUtc(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el)) return null;
        if (el.ValueKind != JsonValueKind.Number || !el.TryGetInt64(out var seconds) || seconds <= 0) return null;
        return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
    }

    /// <summary>
    /// Wipes all observations + snapshot files. Used by Settings "reset" to
    /// recover from a bad state without manual SQL.
    /// </summary>
    public sealed record ResetSummary(int RowsDeleted, int SnapshotFilesDeleted);

    public ResetSummary ResetData()
    {
        using var db = new AppDbContext();
        db.Database.EnsureCreated();
        var rows = db.TokenRemainingObservations.Count();
        db.TokenRemainingObservations.ExecuteDelete();

        int files = 0;
        if (Directory.Exists(SnapshotsDir))
        {
            foreach (var f in Directory.EnumerateFiles(SnapshotsDir, "*.json"))
            {
                try { File.Delete(f); files++; } catch { /* keep going */ }
            }
        }

        AppLogger.Log($"[TokenRemainingCollector] reset: rows-={rows} snapshots-={files}");
        return new ResetSummary(rows, files);
    }
}
