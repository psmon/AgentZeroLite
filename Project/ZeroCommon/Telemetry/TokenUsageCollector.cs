using System.IO;
using System.Text.Json;
using Agent.Common.Data;
using Agent.Common.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Agent.Common.Telemetry;

/// <summary>
/// Polls the Claude Code and Codex CLI JSONL transcripts on disk, extracts
/// token usage rows, and persists them via <see cref="AppDbContext"/>.
///
/// Sources:
///   • %USERPROFILE%\.claude\projects\&lt;slugged-cwd&gt;\&lt;sessionId&gt;.jsonl   (assistant lines, message.usage)
///   • %USERPROFILE%\.codex\sessions\YYYY\MM\DD\rollout-*.jsonl       (event_msg payload type=token_count)
///
/// Both are append-only, so a per-file byte offset is enough to resume.
///
/// Lifecycle: <see cref="Start"/> launches a timer (default 60 s).
/// <see cref="Stop"/> awaits the in-flight tick. The collector is safe to
/// run alongside the producing CLI (uses FileShare.ReadWrite|Delete).
/// </summary>
public sealed class TokenUsageCollector
{
    public static TokenUsageCollector Instance { get; } = new();

    private readonly object _gate = new();
    private System.Threading.Timer? _timer;
    private CancellationTokenSource? _cts;
    private Task _running = Task.CompletedTask;
    private bool _ticking;

    // 10-minute default — usage tracking doesn't need second-level
    // freshness, and the dashboard's Refresh button covers the
    // "I want it now" case. App restart also gap-fills via byte-offset
    // checkpoints, so missing a few minutes between the last tick and
    // shutdown costs nothing.
    public static TimeSpan DefaultInterval { get; } = TimeSpan.FromMinutes(10);

    public TimeSpan PollInterval { get; private set; } = DefaultInterval;

    public event Action<TickSummary>? TickCompleted;

    public sealed record TickSummary(
        int FilesScanned,
        int RowsInserted,
        int ClaudeRows,
        int CodexRows,
        DateTime FinishedAt,
        string? Error);

    public void Start(TimeSpan? interval = null)
    {
        lock (_gate)
        {
            if (_timer is not null) return;
            PollInterval = interval ?? DefaultInterval;
            _cts = new CancellationTokenSource();
            // First fire after 5 s so app startup isn't dominated by a
            // bootstrap pass; subsequent fires honour PollInterval.
            _timer = new System.Threading.Timer(OnTick, null, TimeSpan.FromSeconds(5), PollInterval);
            AppLogger.Log($"[TokenCollector] started (interval={PollInterval.TotalSeconds:N0}s)");
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
        AppLogger.Log("[TokenCollector] stopped");
    }

    /// <summary>Force an out-of-band tick (e.g. UI refresh button).</summary>
    public Task<TickSummary> TickNowAsync(CancellationToken ct = default)
        => Task.Run(() => RunTick(ct), ct);

    private void OnTick(object? _)
    {
        // Skip if previous tick still running — keeps memory pressure flat
        // when a bootstrap pass on a slow disk takes longer than the
        // configured interval.
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
        int files = 0, rows = 0, claude = 0, codex = 0;
        string? error = null;

        try
        {
            using var db = new AppDbContext();
            db.Database.EnsureCreated();

            var checkpoints = db.TokenSourceCheckpoints
                .ToDictionary(c => c.SourceFile, c => c, StringComparer.OrdinalIgnoreCase);

            var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            // The user can run multiple Claude Code profiles by pointing
            // CLAUDE_CONFIG_DIR at sibling dirs (~/.claude, ~/.claude-qa,
            // ~/.claude-pel3, …). Each is an independent installation with
            // its own credentials.json + projects/. AccountKey = the dir
            // name (the suffix after the leading dot) since that's what the
            // operator actually types in their shell.
            foreach (var profile in EnumerateClaudeProfiles(userHome))
            {
                if (ct.IsCancellationRequested) break;
                foreach (var file in EnumerateClaudeFiles(profile.ProjectsRoot))
                {
                    if (ct.IsCancellationRequested) break;
                    files++;
                    var c = ScanClaudeFile(db, file, checkpoints, profile.AccountKey, ct);
                    rows += c; claude += c;
                }
            }

            var codexRoot = Path.Combine(userHome, ".codex", "sessions");
            if (Directory.Exists(codexRoot))
            {
                foreach (var file in EnumerateCodexFiles(codexRoot))
                {
                    if (ct.IsCancellationRequested) break;
                    files++;
                    var c = ScanCodexFile(db, file, checkpoints, ct);
                    rows += c; codex += c;
                }
            }

            db.SaveChanges();
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            AppLogger.LogError("[TokenCollector] tick failed", ex);
        }

        var summary = new TickSummary(files, rows, claude, codex, DateTime.UtcNow, error);
        try { TickCompleted?.Invoke(summary); } catch { /* listener errors don't kill the collector */ }
        AppLogger.Log($"[TokenCollector] tick: files={files} rows+={rows} (claude={claude} codex={codex}) elapsed={(DateTime.UtcNow - started).TotalSeconds:N1}s err={(error ?? "none")}");
        return summary;
    }

    private sealed record ClaudeProfile(string AccountKey, string ProjectsRoot);

    private static IEnumerable<ClaudeProfile> EnumerateClaudeProfiles(string userHome)
    {
        // Default profile + every sibling that follows the `.claude*` dot
        // convention. We require a `projects/` subdir so unrelated dotted
        // dirs (e.g. ".claude_backup_zip") don't get pulled in unless they
        // actually contain a Claude Code transcript layout.
        foreach (var dir in SafeEnumerateDirs(userHome, ".claude*"))
        {
            var projects = Path.Combine(dir, "projects");
            if (!Directory.Exists(projects)) continue;
            // Strip leading dot so AccountKey reads like the shell macro
            // name the operator already uses (`claude`, `claude-qa`).
            var name = Path.GetFileName(dir).TrimStart('.');
            if (string.IsNullOrEmpty(name)) name = "claude";
            yield return new ClaudeProfile(name, projects);
        }
    }

    private static IEnumerable<string> SafeEnumerateDirs(string root, string pattern)
    {
        if (!Directory.Exists(root)) yield break;
        IEnumerable<string> dirs;
        try { dirs = Directory.EnumerateDirectories(root, pattern, SearchOption.TopDirectoryOnly); }
        catch { yield break; }
        foreach (var d in dirs) yield return d;
    }

    private static IEnumerable<string> EnumerateClaudeFiles(string projectsRoot)
    {
        // Only top-level <projectSlug>/<sessionId>.jsonl. The sub-folders
        // named after a sessionId hold sidechain forks which duplicate
        // usage already accounted for in the parent transcript.
        foreach (var projectDir in Directory.EnumerateDirectories(projectsRoot))
        {
            foreach (var f in Directory.EnumerateFiles(projectDir, "*.jsonl", SearchOption.TopDirectoryOnly))
                yield return f;
        }
    }

    private static IEnumerable<string> EnumerateCodexFiles(string root)
    {
        // ~/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl — depth is fixed.
        return Directory.EnumerateFiles(root, "rollout-*.jsonl", SearchOption.AllDirectories);
    }

    private int ScanClaudeFile(
        AppDbContext db,
        string file,
        Dictionary<string, TokenSourceCheckpoint> checkpoints,
        string accountKey,
        CancellationToken ct)
    {
        var added = 0;
        if (!checkpoints.TryGetValue(file, out var cp))
        {
            cp = new TokenSourceCheckpoint
            {
                SourceFile = file,
                Vendor = TokenVendor.Anthropic,
                ByteOffset = 0,
                LineCount = 0,
                UpdatedAt = DateTime.UtcNow,
                AccountKey = accountKey,
            };
            db.TokenSourceCheckpoints.Add(cp);
            checkpoints[file] = cp;
        }
        else if (string.IsNullOrEmpty(cp.AccountKey))
        {
            // Backfill: checkpoint existed before AccountKey was tracked.
            cp.AccountKey = accountKey;
        }

        // Always trust the pinned account on the checkpoint — never the
        // currently-active profile — because the file's owning profile
        // doesn't change for the lifetime of the file.
        var pinned = string.IsNullOrEmpty(cp.AccountKey) ? accountKey : cp.AccountKey;

        long fileLen;
        try { fileLen = new FileInfo(file).Length; }
        catch { return 0; }
        if (fileLen <= cp.ByteOffset) return 0;

        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            fs.Position = cp.ByteOffset;
            using var reader = new StreamReader(fs);

            string? line;
            long lineNo = cp.LineCount;
            while ((line = reader.ReadLine()) != null)
            {
                if (ct.IsCancellationRequested) break;
                lineNo++;
                if (line.Length == 0) continue;
                if (TryParseClaudeLine(line, file, lineNo, pinned, out var rec))
                {
                    db.TokenUsageRecords.Add(rec);
                    added++;
                }
            }

            cp.ByteOffset = fs.Position;
            cp.LineCount = lineNo;
            cp.UpdatedAt = DateTime.UtcNow;
        }
        catch (IOException ex)
        {
            AppLogger.Log($"[TokenCollector] skip claude file (locked): {Path.GetFileName(file)} — {ex.Message}");
        }
        return added;
    }

    private int ScanCodexFile(
        AppDbContext db,
        string file,
        Dictionary<string, TokenSourceCheckpoint> checkpoints,
        CancellationToken ct)
    {
        var added = 0;
        if (!checkpoints.TryGetValue(file, out var cp))
        {
            cp = new TokenSourceCheckpoint
            {
                SourceFile = file,
                Vendor = TokenVendor.OpenAI,
                ByteOffset = 0,
                LineCount = 0,
                UpdatedAt = DateTime.UtcNow
            };
            db.TokenSourceCheckpoints.Add(cp);
            checkpoints[file] = cp;
        }

        long fileLen;
        try { fileLen = new FileInfo(file).Length; }
        catch { return 0; }
        if (fileLen <= cp.ByteOffset) return 0;

        // Header (line 1) is a session_meta envelope. Always re-read it
        // even on resume — the rest of the parser uses it for sessionId/cwd.
        CodexSessionHeader? header = null;
        try
        {
            using var headerFs = new FileStream(file, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var headerReader = new StreamReader(headerFs);
            var first = headerReader.ReadLine();
            if (first is { Length: > 0 })
                header = TryParseCodexHeader(first);
        }
        catch (IOException ex)
        {
            AppLogger.Log($"[TokenCollector] skip codex header (locked): {Path.GetFileName(file)} — {ex.Message}");
            return 0;
        }
        if (header is null) return 0;

        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            fs.Position = cp.ByteOffset;
            using var reader = new StreamReader(fs);

            string? line;
            long lineNo = cp.LineCount;
            while ((line = reader.ReadLine()) != null)
            {
                if (ct.IsCancellationRequested) break;
                lineNo++;
                if (line.Length == 0) continue;
                if (TryParseCodexLine(line, file, lineNo, header, out var rec))
                {
                    db.TokenUsageRecords.Add(rec);
                    added++;
                }
            }

            cp.ByteOffset = fs.Position;
            cp.LineCount = lineNo;
            cp.UpdatedAt = DateTime.UtcNow;
        }
        catch (IOException ex)
        {
            AppLogger.Log($"[TokenCollector] skip codex file (locked): {Path.GetFileName(file)} — {ex.Message}");
        }
        return added;
    }

    private static bool TryParseClaudeLine(
        string line, string file, long lineNo, string? accountKey, out TokenUsageRecord rec)
    {
        rec = null!;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (!(root.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String && typeEl.GetString() == "assistant"))
                return false;
            if (!root.TryGetProperty("message", out var msgEl)) return false;
            if (!msgEl.TryGetProperty("usage", out var usageEl) || usageEl.ValueKind != JsonValueKind.Object)
                return false;

            var input = ReadLong(usageEl, "input_tokens");
            var output = ReadLong(usageEl, "output_tokens");
            var cacheCreate = ReadLong(usageEl, "cache_creation_input_tokens");
            var cacheRead = ReadLong(usageEl, "cache_read_input_tokens");

            // Skip totally empty usage rows (some keepalive lines have all zeros).
            if (input == 0 && output == 0 && cacheCreate == 0 && cacheRead == 0)
                return false;

            var model = msgEl.TryGetProperty("model", out var modelEl) && modelEl.ValueKind == JsonValueKind.String
                ? (modelEl.GetString() ?? "") : "";
            var sessionId = root.TryGetProperty("sessionId", out var sidEl) && sidEl.ValueKind == JsonValueKind.String
                ? (sidEl.GetString() ?? "") : "";
            var cwd = root.TryGetProperty("cwd", out var cwdEl) && cwdEl.ValueKind == JsonValueKind.String
                ? (cwdEl.GetString() ?? "") : "";
            var gitBranch = root.TryGetProperty("gitBranch", out var gbEl) && gbEl.ValueKind == JsonValueKind.String
                ? (gbEl.GetString() ?? "") : "";
            var requestId = root.TryGetProperty("requestId", out var rqEl) && rqEl.ValueKind == JsonValueKind.String
                ? (rqEl.GetString() ?? "") : "";
            var ts = root.TryGetProperty("timestamp", out var tsEl) && tsEl.ValueKind == JsonValueKind.String
                && DateTime.TryParse(tsEl.GetString(), out var parsed)
                ? parsed.ToUniversalTime()
                : DateTime.UtcNow;

            rec = new TokenUsageRecord
            {
                Vendor = TokenVendor.Anthropic,
                AccountKey = accountKey ?? "",
                SessionId = sessionId,
                Cwd = cwd,
                GitBranch = gitBranch,
                Model = model,
                RecordedAt = ts,
                InputTokens = input,
                OutputTokens = output,
                CacheCreateTokens = cacheCreate,
                CacheReadTokens = cacheRead,
                ReasoningTokens = 0,
                RawRequestId = requestId,
                SourceFile = file,
                SourceLine = lineNo,
            };
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseCodexLine(
        string line, string file, long lineNo, CodexSessionHeader header, out TokenUsageRecord rec)
    {
        rec = null!;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (!(root.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String && typeEl.GetString() == "event_msg"))
                return false;
            if (!root.TryGetProperty("payload", out var payloadEl) || payloadEl.ValueKind != JsonValueKind.Object)
                return false;
            if (!(payloadEl.TryGetProperty("type", out var ptype) && ptype.ValueKind == JsonValueKind.String && ptype.GetString() == "token_count"))
                return false;

            // last_token_usage = this turn's delta. Falling back to total
            // is meaningful only on the first row; subsequent diffs would
            // require the previous total, which we don't track per file —
            // so prefer last and only fall back to total if last is absent.
            if (!payloadEl.TryGetProperty("info", out var infoEl)) return false;

            JsonElement usage;
            if (infoEl.TryGetProperty("last_token_usage", out var last) && last.ValueKind == JsonValueKind.Object)
                usage = last;
            else if (infoEl.TryGetProperty("total_token_usage", out var total) && total.ValueKind == JsonValueKind.Object)
                usage = total;
            else
                return false;

            var input = ReadLong(usage, "input_tokens");
            var cached = ReadLong(usage, "cached_input_tokens");
            var output = ReadLong(usage, "output_tokens");
            var reasoning = ReadLong(usage, "reasoning_output_tokens");
            if (input == 0 && output == 0 && cached == 0 && reasoning == 0) return false;

            var planType = "";
            if (payloadEl.TryGetProperty("rate_limits", out var rate) && rate.ValueKind == JsonValueKind.Object
                && rate.TryGetProperty("plan_type", out var pt) && pt.ValueKind == JsonValueKind.String)
                planType = pt.GetString() ?? "";

            var account = string.IsNullOrEmpty(planType)
                ? header.CliVersion
                : $"{planType}:{header.CliVersion}";

            var ts = root.TryGetProperty("timestamp", out var tsEl) && tsEl.ValueKind == JsonValueKind.String
                && DateTime.TryParse(tsEl.GetString(), out var parsed)
                ? parsed.ToUniversalTime()
                : DateTime.UtcNow;

            rec = new TokenUsageRecord
            {
                Vendor = TokenVendor.OpenAI,
                AccountKey = account,
                SessionId = header.SessionId,
                Cwd = header.Cwd,
                GitBranch = header.GitBranch,
                Model = header.Model,           // best-effort; rollouts don't always have it in header
                RecordedAt = ts,
                InputTokens = input,
                OutputTokens = output,
                CacheCreateTokens = 0,
                CacheReadTokens = cached,
                ReasoningTokens = reasoning,
                RawRequestId = "",
                SourceFile = file,
                SourceLine = lineNo,
            };
            return true;
        }
        catch
        {
            return false;
        }
    }

    private sealed record CodexSessionHeader(
        string SessionId, string Cwd, string GitBranch, string CliVersion, string Model);

    private static CodexSessionHeader? TryParseCodexHeader(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
                return null;

            var sessionId = payload.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                ? (idEl.GetString() ?? "") : "";
            var cwd = payload.TryGetProperty("cwd", out var cwdEl) && cwdEl.ValueKind == JsonValueKind.String
                ? (cwdEl.GetString() ?? "") : "";
            var cliVersion = payload.TryGetProperty("cli_version", out var cvEl) && cvEl.ValueKind == JsonValueKind.String
                ? (cvEl.GetString() ?? "") : "";
            var gitBranch = "";
            if (payload.TryGetProperty("git", out var gitEl) && gitEl.ValueKind == JsonValueKind.Object
                && gitEl.TryGetProperty("branch", out var brEl) && brEl.ValueKind == JsonValueKind.String)
                gitBranch = brEl.GetString() ?? "";
            var model = payload.TryGetProperty("model", out var modelEl) && modelEl.ValueKind == JsonValueKind.String
                ? (modelEl.GetString() ?? "") : "";

            return new CodexSessionHeader(sessionId, cwd, gitBranch, cliVersion, model);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Wipes all collected rows + checkpoints. Used by the dashboard
    /// "Reset &amp; Re-scan" action so the user can recover from a bad
    /// labelling state without touching SQL. The next tick rebuilds.
    /// </summary>
    public sealed record ResetSummary(int RowsDeleted, int CheckpointsDeleted);

    public ResetSummary ResetData()
    {
        using var db = new AppDbContext();
        db.Database.EnsureCreated();
        var rows = db.TokenUsageRecords.Count();
        var cps  = db.TokenSourceCheckpoints.Count();
        // ExecuteDelete avoids loading rows into memory.
        db.TokenUsageRecords.ExecuteDelete();
        db.TokenSourceCheckpoints.ExecuteDelete();
        AppLogger.Log($"[TokenCollector] reset: rows-={rows} checkpoints-={cps}");
        return new ResetSummary(rows, cps);
    }

    private static long ReadLong(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el)) return 0;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var v)) return v;
        return 0;
    }
}

public static class TokenVendor
{
    public const string Anthropic = "anthropic";
    public const string OpenAI = "openai";
}
