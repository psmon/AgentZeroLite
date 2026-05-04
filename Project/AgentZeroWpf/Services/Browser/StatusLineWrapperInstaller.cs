using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agent.Common;

namespace AgentZeroWpf.Services.Browser;

/// <summary>
/// Installs an AgentZero statusLine wrapper into per-account
/// <c>~/.claude*/settings.json</c>. The wrapper tees Claude Code's stdin
/// (rate-limit telemetry) into per-account snapshot files that
/// <see cref="Agent.Common.Telemetry.TokenRemainingCollector"/> picks up.
///
/// Lifecycle (per account):
///   Install   — backup settings.json → write az-hud-wrapper.js (once,
///               shared across accounts) → patch statusLine.command,
///               preserving any existing command via --pipe
///   Uninstall — verify backup is still applicable (sha256 of current
///               command matches our installed shape) → restore original
///               command from backup. If the operator edited settings
///               since install, raise <see cref="UninstallNeedsConfirm"/>
///               so the UI can show a 3-way diff before clobbering.
///
/// State persists in
///   %LOCALAPPDATA%\AgentZeroLite\statusline-state\&lt;account&gt;.json
/// alongside the timestamped backup file.
/// </summary>
public static class StatusLineWrapperInstaller
{
    private static readonly string LocalAppData =
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private static readonly string UserProfile =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string WrapperDir   => Path.Combine(LocalAppData, "AgentZeroLite", "statusline");
    public static string WrapperPath  => Path.Combine(WrapperDir, "az-hud-wrapper.js");
    public static string PipeDir      => Path.Combine(LocalAppData, "AgentZeroLite", "statusline-pipes");
    public static string BackupRoot   => Path.Combine(LocalAppData, "AgentZeroLite", "statusline-backup");
    public static string StateRoot    => Path.Combine(LocalAppData, "AgentZeroLite", "statusline-state");
    public static string SnapshotsDir => Path.Combine(LocalAppData, "AgentZeroLite", "cc-hud-snapshots");

    private static string PipeFileFor(string accountKey)
        => Path.Combine(PipeDir, accountKey + ".cmd.txt");

    /// <summary>One row per CLAUDE_CONFIG_DIR sibling found on disk.</summary>
    public sealed record AccountProfile(
        string AccountKey,
        string ConfigDir,
        string SettingsJsonPath,
        bool SettingsJsonExists,
        string? CurrentStatusLineCommand,
        bool OurWrapperInstalled,
        bool ClaudeHudDetected,
        string? PipeTarget);

    /// <summary>Discover every Claude Code profile (~/.claude*) on disk.</summary>
    public static IReadOnlyList<AccountProfile> DiscoverProfiles()
    {
        var list = new List<AccountProfile>();
        if (!Directory.Exists(UserProfile)) return list;

        IEnumerable<string> dirs;
        try { dirs = Directory.EnumerateDirectories(UserProfile, ".claude*", SearchOption.TopDirectoryOnly); }
        catch { return list; }

        foreach (var dir in dirs)
        {
            // Filter to dirs that look like a Claude Code home (have settings.json
            // OR have a projects/ subdir — same heuristic as TokenUsageCollector)
            var hasProjects = Directory.Exists(Path.Combine(dir, "projects"));
            var settingsPath = Path.Combine(dir, "settings.json");
            var hasSettings = File.Exists(settingsPath);
            if (!hasProjects && !hasSettings) continue;

            var name = Path.GetFileName(dir).TrimStart('.');
            if (string.IsNullOrEmpty(name)) name = "claude";

            var current = TryReadStatusLineCommand(settingsPath);
            var ourInstalled = current is not null && current.Contains("az-hud-wrapper.js", StringComparison.OrdinalIgnoreCase);
            var pipeTarget = ourInstalled ? ExtractPipeArg(current!) : null;
            var hudDetected = LooksLikeClaudeHud(ourInstalled ? pipeTarget : current);

            list.Add(new AccountProfile(
                AccountKey: name,
                ConfigDir: dir,
                SettingsJsonPath: settingsPath,
                SettingsJsonExists: hasSettings,
                CurrentStatusLineCommand: current,
                OurWrapperInstalled: ourInstalled,
                ClaudeHudDetected: hudDetected,
                PipeTarget: pipeTarget));
        }

        return list;
    }

    /// <summary>Result of an install/uninstall.</summary>
    public sealed record InstallResult(
        string AccountKey,
        bool Ok,
        string? Action,           // "installed" | "reinstalled" | "uninstalled" | null
        string? BackupPath,
        string? PreviousCommand,
        string? NewCommand,
        string? Error);

    public static InstallResult Install(string accountKey)
    {
        try
        {
            var prof = DiscoverProfiles().FirstOrDefault(p =>
                string.Equals(p.AccountKey, accountKey, StringComparison.OrdinalIgnoreCase));
            if (prof is null) return Fail(accountKey, $"profile '{accountKey}' not found under {UserProfile}");

            EnsureWrapperOnDisk();
            Directory.CreateDirectory(SnapshotsDir);
            var acctBackupDir = Path.Combine(BackupRoot, accountKey);
            Directory.CreateDirectory(acctBackupDir);
            Directory.CreateDirectory(StateRoot);

            // 1. Read existing settings.json (or create empty one)
            JsonNode root;
            string? originalRaw = null;
            if (prof.SettingsJsonExists)
            {
                originalRaw = File.ReadAllText(prof.SettingsJsonPath);
                root = JsonNode.Parse(originalRaw,
                    nodeOptions: null,
                    documentOptions: new JsonDocumentOptions
                    {
                        AllowTrailingCommas = true,
                        CommentHandling = JsonCommentHandling.Skip,
                    }) ?? new JsonObject();
            }
            else
            {
                root = new JsonObject();
            }
            if (root is not JsonObject obj) return Fail(accountKey, "settings.json is not a JSON object");

            // 2. Backup the original (only if we haven't already wrapped it —
            //    we don't want to backup our own wrapped state on reinstall)
            var existingCmd = ReadStatusLineCommand(obj);
            string? backupPath = null;
            string? wrappedPipeTarget = null;

            if (existingCmd is not null
                && existingCmd.Contains("az-hud-wrapper.js", StringComparison.OrdinalIgnoreCase))
            {
                // Re-installing over our own wrapper — keep the original pipe target.
                wrappedPipeTarget = ExtractPipeArg(existingCmd);
            }
            else
            {
                if (originalRaw is not null)
                {
                    var ts = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
                    backupPath = Path.Combine(acctBackupDir, $"settings.json.{ts}.bak");
                    File.WriteAllText(backupPath, originalRaw);
                }
                wrappedPipeTarget = existingCmd; // can be null — standalone mode
            }

            // 3. Build new statusLine command
            var newCmd = BuildWrapperCommand(accountKey, wrappedPipeTarget);
            WriteStatusLineCommand(obj, newCmd);

            // 4. Write settings.json atomically (temp + rename)
            var tmp = prof.SettingsJsonPath + ".tmp";
            var json = obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(tmp, json);
            try { File.Move(tmp, prof.SettingsJsonPath, overwrite: true); }
            catch { File.Delete(tmp); throw; }

            // 5. Persist install state for safe uninstall
            var state = new InstallState(
                AccountKey: accountKey,
                InstalledAtUtc: DateTime.UtcNow,
                OriginalCommand: existingCmd?.Contains("az-hud-wrapper.js", StringComparison.OrdinalIgnoreCase) == true
                    ? null     // re-install — keep prior install's recorded original
                    : existingCmd,
                OriginalCommandSha256: Sha256(existingCmd ?? ""),
                InstalledCommand: newCmd,
                InstalledCommandSha256: Sha256(newCmd),
                BackupPath: backupPath);
            // On re-install, preserve OriginalCommand from prior state
            var stateFile = Path.Combine(StateRoot, $"{accountKey}.json");
            if (File.Exists(stateFile) && state.OriginalCommand is null)
            {
                try
                {
                    var prior = JsonSerializer.Deserialize<InstallState>(File.ReadAllText(stateFile), JsonOpts);
                    if (prior is not null)
                        state = state with
                        {
                            OriginalCommand = prior.OriginalCommand,
                            OriginalCommandSha256 = prior.OriginalCommandSha256,
                            BackupPath = prior.BackupPath ?? state.BackupPath,
                        };
                }
                catch { /* fresh state if we can't read the prior */ }
            }
            File.WriteAllText(stateFile, JsonSerializer.Serialize(state, JsonOpts));

            AppLogger.Log($"[StatusLineInstaller] installed for '{accountKey}' (pipe={(wrappedPipeTarget ?? "<none>")})");

            return new InstallResult(
                AccountKey: accountKey,
                Ok: true,
                Action: existingCmd?.Contains("az-hud-wrapper.js", StringComparison.OrdinalIgnoreCase) == true
                    ? "reinstalled" : "installed",
                BackupPath: backupPath,
                PreviousCommand: existingCmd,
                NewCommand: newCmd,
                Error: null);
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[StatusLineInstaller] install failed for '{accountKey}'", ex);
            return Fail(accountKey, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    public sealed record UninstallResult(
        string AccountKey,
        bool Ok,
        bool NeedsConfirm,
        string? Action,
        string? RestoredCommand,
        string? CurrentCommand,
        string? OriginalCommandFromState,
        string? Error);

    public static UninstallResult Uninstall(string accountKey, bool force = false)
    {
        try
        {
            var prof = DiscoverProfiles().FirstOrDefault(p =>
                string.Equals(p.AccountKey, accountKey, StringComparison.OrdinalIgnoreCase));
            if (prof is null)
                return new UninstallResult(accountKey, false, false, null, null, null, null,
                    $"profile '{accountKey}' not found");

            var stateFile = Path.Combine(StateRoot, $"{accountKey}.json");
            InstallState? state = null;
            if (File.Exists(stateFile))
            {
                try { state = JsonSerializer.Deserialize<InstallState>(File.ReadAllText(stateFile), JsonOpts); }
                catch { state = null; }
            }
            if (state is null)
                return new UninstallResult(accountKey, false, false, null, null, null, null,
                    "no install state found — nothing to uninstall");

            if (!File.Exists(prof.SettingsJsonPath))
                return new UninstallResult(accountKey, false, false, null, null, null, null,
                    "settings.json missing — operator may have removed Claude Code");

            var raw = File.ReadAllText(prof.SettingsJsonPath);
            var root = JsonNode.Parse(raw,
                nodeOptions: null,
                documentOptions: new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                }) ?? new JsonObject();
            if (root is not JsonObject obj)
                return new UninstallResult(accountKey, false, false, null, null, null, null,
                    "settings.json is not an object");

            var current = ReadStatusLineCommand(obj);

            // Safety check: did the operator hand-edit our wrapper command?
            var currentSha = Sha256(current ?? "");
            if (!force && currentSha != state.InstalledCommandSha256)
            {
                AppLogger.Log($"[StatusLineInstaller] uninstall '{accountKey}' needs confirm — current!=installed");
                return new UninstallResult(
                    AccountKey: accountKey,
                    Ok: false,
                    NeedsConfirm: true,
                    Action: null,
                    RestoredCommand: state.OriginalCommand,
                    CurrentCommand: current,
                    OriginalCommandFromState: state.OriginalCommand,
                    Error: "current statusLine has been edited since install — operator must confirm restore");
            }

            // Restore original command (or remove statusLine entirely if there
            // was none originally)
            if (string.IsNullOrEmpty(state.OriginalCommand))
            {
                RemoveStatusLine(obj);
            }
            else
            {
                WriteStatusLineCommand(obj, state.OriginalCommand);
            }

            var tmp = prof.SettingsJsonPath + ".tmp";
            var json = obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(tmp, json);
            try { File.Move(tmp, prof.SettingsJsonPath, overwrite: true); }
            catch { File.Delete(tmp); throw; }

            // Keep the state file so re-install can detect prior history.
            // Snapshots are intentionally preserved (operator can manually
            // delete if they want).
            AppLogger.Log($"[StatusLineInstaller] uninstalled for '{accountKey}' (restored={(state.OriginalCommand ?? "<removed>")})");

            return new UninstallResult(
                AccountKey: accountKey,
                Ok: true,
                NeedsConfirm: false,
                Action: "uninstalled",
                RestoredCommand: state.OriginalCommand,
                CurrentCommand: state.OriginalCommand,
                OriginalCommandFromState: state.OriginalCommand,
                Error: null);
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[StatusLineInstaller] uninstall failed for '{accountKey}'", ex);
            return new UninstallResult(accountKey, false, false, null, null, null, null,
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Persisted under %LOCALAPPDATA%/AgentZeroLite/statusline-state/&lt;acct&gt;.json.</summary>
    private sealed record InstallState(
        string AccountKey,
        DateTime InstalledAtUtc,
        string? OriginalCommand,
        string OriginalCommandSha256,
        string InstalledCommand,
        string InstalledCommandSha256,
        string? BackupPath);

    private static InstallResult Fail(string acct, string msg)
        => new(acct, false, null, null, null, null, msg);

    // --- wrapper write ---

    public static void EnsureWrapperOnDisk()
    {
        Directory.CreateDirectory(WrapperDir);
        // Always overwrite — version the wrapper by content hash, so an
        // app upgrade picks up wrapper improvements without operator action.
        File.WriteAllText(WrapperPath, WrapperScript);
    }

    private static string BuildWrapperCommand(string accountKey, string? pipeTarget)
    {
        // Quote the wrapper path (may contain spaces in user-profile names).
        //
        // Pipe target is NEVER inlined into the command string — Claude Code
        // applies its own variable / command substitution (${VAR}, $(...)) to
        // the entire statusLine command before spawning, even inside single
        // quotes (verified empirically — claude-hud's bash -c '...$(ls -d ...)'
        // came out with $(ls -d ...) replaced by an empty string).
        //
        // Instead we write the raw pipe command to a sidecar text file at
        // statusline-pipes/<acct>.cmd.txt and pass the FILE PATH via
        // --pipe-file. The file path is a plain Windows path with no $ chars,
        // so substitution can't shred it.
        Directory.CreateDirectory(PipeDir);
        var pipeFile = PipeFileFor(accountKey);
        if (!string.IsNullOrWhiteSpace(pipeTarget))
        {
            // Atomic write so a tick mid-rewrite reads the previous contents.
            var tmp = pipeFile + ".tmp";
            File.WriteAllText(tmp, pipeTarget!);
            try { File.Move(tmp, pipeFile, overwrite: true); }
            catch { File.Delete(tmp); throw; }
        }
        else
        {
            // Standalone install — make sure no stale pipe file is around.
            try { if (File.Exists(pipeFile)) File.Delete(pipeFile); } catch { }
        }

        var sb = new StringBuilder();
        sb.Append("node \"").Append(WrapperPath).Append("\" --account ").Append(accountKey);
        if (!string.IsNullOrWhiteSpace(pipeTarget))
        {
            sb.Append(" --pipe-file \"").Append(pipeFile).Append("\"");
        }
        return sb.ToString();
    }

    // --- settings.json read/write helpers ---

    private static string? TryReadStatusLineCommand(string settingsPath)
    {
        if (!File.Exists(settingsPath)) return null;
        try
        {
            var raw = File.ReadAllText(settingsPath);
            var root = JsonNode.Parse(raw,
                nodeOptions: null,
                documentOptions: new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                });
            return root is JsonObject obj ? ReadStatusLineCommand(obj) : null;
        }
        catch { return null; }
    }

    private static string? ReadStatusLineCommand(JsonObject obj)
    {
        if (!obj.TryGetPropertyValue("statusLine", out var sl) || sl is null) return null;
        if (sl is JsonObject slObj && slObj.TryGetPropertyValue("command", out var c))
        {
            return c?.GetValue<string>();
        }
        if (sl is JsonValue v && v.TryGetValue<string>(out var s))
            return s;
        return null;
    }

    private static void WriteStatusLineCommand(JsonObject obj, string command)
    {
        if (obj["statusLine"] is JsonObject existing)
        {
            existing["command"] = command;
            // Ensure type is "command" (Claude Code's only supported type today)
            if (!existing.ContainsKey("type")) existing["type"] = "command";
        }
        else
        {
            obj["statusLine"] = new JsonObject
            {
                ["type"]    = "command",
                ["command"] = command,
            };
        }
    }

    private static void RemoveStatusLine(JsonObject obj)
        => obj.Remove("statusLine");

    private static string? ExtractPipeArg(string command)
    {
        // Two forms today:
        //   --pipe-file "<path>"   (current — sidecar file holds raw command)
        //   --pipe      "<cmd>"    (legacy v1 — inline; subject to Claude Code substitution)
        // Try the sidecar form first.
        const string fileMarker = "--pipe-file ";
        var fi = command.IndexOf(fileMarker, StringComparison.Ordinal);
        if (fi >= 0)
        {
            var path = ReadQuotedOrToken(command, fi + fileMarker.Length);
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                try { return File.ReadAllText(path).TrimEnd('\n', '\r'); } catch { }
            }
            return null;
        }
        const string marker = "--pipe ";
        var i = command.IndexOf(marker, StringComparison.Ordinal);
        if (i < 0) return null;
        return ReadQuotedOrToken(command, i + marker.Length);
    }

    /// <summary>Read a quoted or whitespace-delimited token starting at <paramref name="start"/>.</summary>
    private static string? ReadQuotedOrToken(string s, int start)
    {
        if (start < 0 || start >= s.Length) return null;
        // Skip leading whitespace
        while (start < s.Length && char.IsWhiteSpace(s[start])) start++;
        if (start >= s.Length) return null;

        if (s[start] == '"')
        {
            var sb = new StringBuilder();
            for (int p = start + 1; p < s.Length; p++)
            {
                if (s[p] == '\\' && p + 1 < s.Length && s[p + 1] == '"')
                {
                    sb.Append('"'); p++; continue;
                }
                if (s[p] == '"') return sb.ToString();
                sb.Append(s[p]);
            }
            return sb.ToString();
        }
        var spaceIdx = s.IndexOf(' ', start);
        return spaceIdx < 0 ? s[start..] : s[start..spaceIdx];
    }

    private static bool LooksLikeClaudeHud(string? command)
    {
        if (string.IsNullOrEmpty(command)) return false;
        // claude-hud's setup command writes `node "${CLAUDE_PLUGIN_ROOT}/dist/index.js"`
        // and variations. Match on the dist/index.js + claude-hud path tokens.
        return command.Contains("claude-hud", StringComparison.OrdinalIgnoreCase)
            || command.Contains("/dist/index.js", StringComparison.OrdinalIgnoreCase)
            || command.Contains("\\dist\\index.js", StringComparison.OrdinalIgnoreCase);
    }

    private static string Sha256(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s ?? "");
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // --- the wrapper script itself ---
    //
    // Embedded as a string so the installer is self-contained (no need to
    // bundle a separate file in the csproj). The wrapper:
    //   1. Reads stdin (Claude Code's per-tick statusline JSON) sync.
    //   2. Writes a per-account snapshot file atomically.
    //   3. If --pipe given, spawns the original command and forwards
    //      stdin → child stdin, child stdout → our stdout.
    //   4. If no --pipe, emits a minimal statusline so users see
    //      something instead of a blank line.
    private const string WrapperScript = """
#!/usr/bin/env node
// AgentZero statusLine wrapper — installed by AgentZeroLite.
// Captures Claude Code's rate-limit telemetry into per-account snapshot
// files; pipes stdin through to a downstream statusLine command if one
// was registered before install (so claude-hud etc. keep working).
'use strict';

const fs = require('fs');
const path = require('path');
const cp = require('child_process');

function arg(name) {
  const i = process.argv.indexOf(name);
  return i >= 0 ? process.argv[i + 1] : null;
}

const account = arg('--account') || 'claude';
// Two ways to get the pipe target:
//   --pipe       <command>    — inline (legacy v1; vulnerable to Claude Code's
//                               variable substitution if command contains
//                               $(...) or ${VAR})
//   --pipe-file  <path>       — sidecar file with the raw command (safe:
//                               file path has no $ chars to substitute)
let pipe = arg('--pipe');
const pipeFile = arg('--pipe-file');
if (!pipe && pipeFile) {
  try {
    pipe = fs.readFileSync(pipeFile, 'utf8').replace(/^﻿/, '').trim();
  } catch (e) {
    // Fall through — we'll log below and exit gracefully.
  }
}

const baseDir = path.join(process.env.LOCALAPPDATA || '', 'AgentZeroLite');
const snapshotsDir = path.join(baseDir, 'cc-hud-snapshots');
try { fs.mkdirSync(snapshotsDir, { recursive: true }); } catch {}
const snapshotFile = path.join(snapshotsDir, account + '.json');

// Rolling diagnostic log — last ~64KB. Helps catch stdin parsing /
// model-detection / pipe-spawn issues without flying blind. Trimmed by
// rewriting from scratch when the file exceeds the budget.
const logFile = path.join(snapshotsDir, '_wrapper.log');
function dlog(msg) {
  try {
    let line = new Date().toISOString() + ' [' + account + '] ' + msg + '\n';
    let existing = '';
    try { existing = fs.readFileSync(logFile, 'utf8'); } catch {}
    const combined = (existing + line);
    const trimmed = combined.length > 64 * 1024
      ? combined.slice(combined.length - 48 * 1024)
      : combined;
    fs.writeFileSync(logFile, trimmed);
  } catch {}
}

// Claude Code substitutes ${VAR} in command strings before spawning, but
// when our wrapper passes --pipe to a child process, ${VAR} stays
// literal unless we expand. If the env var is set (Claude Code may set
// it for plugin statuslines), expand here. If not, leave the placeholder
// so the user can see it in the diagnostic log. We also leave bash-style
// defaults (${VAR:-fallback}) untouched — those use bash syntax and the
// plain {WORD} regex below won't match them.
function expandVars(s) {
  if (!s) return s;
  return s.replace(/\$\{([A-Z0-9_]+)\}/g, (m, name) => {
    const v = process.env[name];
    return v != null ? v : m;
  });
}

// Tokenize a shell-style command line into [program, ...args] WITHOUT
// going through cmd. Handles double quotes, single quotes, and \" / \\
// escapes inside double quotes. Single-quoted strings are passed through
// literally (matching POSIX shell rules) — this is critical for piping
// to claude-hud whose bash -c arg uses '...' with embedded '"'"' nesting.
//
// We do NOT do glob expansion, history expansion, or variable
// substitution beyond what expandVars() already did — those are bash's
// job once we hand the script to bash -c.
function parseArgv(s) {
  const out = [];
  let i = 0, n = s.length;
  while (i < n) {
    while (i < n && /\s/.test(s[i])) i++;
    if (i >= n) break;
    let token = '';
    while (i < n && !/\s/.test(s[i])) {
      const c = s[i];
      if (c === '"') {
        i++;
        while (i < n && s[i] !== '"') {
          if (s[i] === '\\' && i + 1 < n && (s[i+1] === '"' || s[i+1] === '\\')) {
            token += s[i+1]; i += 2;
          } else {
            token += s[i++];
          }
        }
        if (i < n && s[i] === '"') i++;
      } else if (c === "'") {
        // POSIX: inside single quotes everything is literal — no escapes.
        i++;
        while (i < n && s[i] !== "'") token += s[i++];
        if (i < n && s[i] === "'") i++;
      } else {
        token += s[i++];
      }
    }
    out.push(token);
  }
  return out;
}

let stdin = '';
process.stdin.setEncoding('utf8');
process.stdin.on('data', d => stdin += d);
process.stdin.on('end', () => {
  dlog('tick stdinBytes=' + stdin.length);

  // 1. Snapshot — rewrite atomically.
  let parsed = null, model = '', fhPct = 0, fhReset = 0, sdPct = 0, sdReset = 0;
  try {
    parsed = JSON.parse(stdin);
    model = parsed && parsed.model && parsed.model.display_name ? parsed.model.display_name : '';
    const r = (parsed && parsed.rate_limits) || {};
    if (r.five_hour) {
      fhPct = Math.round(Number(r.five_hour.used_percentage) || 0);
      fhReset = Number(r.five_hour.resets_at) || 0;
    }
    if (r.seven_day) {
      sdPct = Math.round(Number(r.seven_day.used_percentage) || 0);
      sdReset = Number(r.seven_day.resets_at) || 0;
    }
    dlog('parsed model="' + model + '" fh=' + fhPct + '% sd=' + sdPct + '% hasRL=' + (!!parsed.rate_limits));
  } catch (e) {
    dlog('parse-error ' + (e && e.message || e));
  }

  if (model) {
    try {
      const snap = {
        account,
        writtenAt: new Date().toISOString(),
        model,
        fiveHour: { usedPercentage: fhPct, resetsAt: fhReset },
        sevenDay: { usedPercentage: sdPct, resetsAt: sdReset },
      };
      const tmp = snapshotFile + '.tmp';
      fs.writeFileSync(tmp, JSON.stringify(snap));
      try { fs.renameSync(tmp, snapshotFile); }
      catch { try { fs.copyFileSync(tmp, snapshotFile); fs.unlinkSync(tmp); } catch {} }
    } catch (e) {
      dlog('snap-write-error ' + (e && e.message || e));
    }
  } else {
    dlog('skip-snap (no model in stdin)');
  }

  // 2. Pipe-through, or standalone fallback.
  if (pipe) {
    const expanded = expandVars(pipe);
    if (expanded !== pipe) dlog('pipe expand: ' + pipe + '  ->  ' + expanded);
    if (/\$\{[A-Z0-9_]+\}/.test(expanded)) {
      dlog('pipe still has unresolved ${VAR} — leaving for downstream to fail visibly');
    }
    try {
      // Parse the pipe command into [program, ...args] OURSELVES instead
      // of letting cmd /c do it. Reason: claude-hud's setup writes
      // `bash -c 'plugin_dir=$(...) ; exec ...'` where the -c arg is a
      // bash script with single-quote-in-double-quote-in-single-quote
      // nesting (`'"'"'`). cmd /c does NOT understand single quotes
      // and shreds the script. By parsing argv here and spawning the
      // program directly (no shell), we hand bash exactly one -c arg —
      // intact.
      const argv = parseArgv(expanded);
      if (!argv.length) { dlog('pipe parse-empty'); process.exit(0); }
      let program = argv[0];
      const args  = argv.slice(1);

      // bash on Windows is ambiguous — `bash` on PATH might resolve to
      // the WSL bash (C:\Windows\System32\bash.exe) which fails on
      // Git-Bash-style paths (/c/...). Force Git Bash if we see Git
      // installed at the standard location, since claude-hud's setup
      // command is written assuming Git Bash semantics. We also need
      // to give Git Bash a HOME that matches what it sets up
      // interactively, otherwise bash scripts that probe ~/.claude/...
      // come up empty.
      let childEnv = process.env;
      if (program === 'bash' && process.platform === 'win32') {
        const candidates = [
          process.env.PROGRAMFILES + '\\Git\\bin\\bash.exe',
          process.env['PROGRAMFILES(X86)'] + '\\Git\\bin\\bash.exe',
          process.env.LOCALAPPDATA + '\\Programs\\Git\\bin\\bash.exe',
        ];
        for (const c of candidates) {
          try { if (fs.existsSync(c)) { program = c; dlog('bash -> ' + c); break; } } catch {}
        }
        // Git Bash interactive sessions get HOME=/c/Users/<name> via
        // /etc/profile, but a non-interactive bash spawned from Node
        // inherits Windows' HOME (often empty or pointing to a roaming
        // profile). Force HOME = USERPROFILE in MSYS form so $HOME-
        // relative globs (`$HOME/.claude/plugins/...`) match the same
        // place the interactive shell would.
        if (process.env.USERPROFILE) {
          // C:\Users\psmon  ->  /c/Users/psmon
          const w = process.env.USERPROFILE;
          const msysHome = (w.length >= 2 && w[1] === ':')
            ? '/' + w[0].toLowerCase() + w.slice(2).replace(/\\/g, '/')
            : w;
          childEnv = Object.assign({}, process.env, { HOME: msysHome });
        }
      }
      dlog('pipe spawn program=' + program + ' argc=' + args.length
        + ' cwd=' + process.cwd()
        + ' HOME=' + (childEnv.HOME || '<unset>')
        + ' CLAUDE_CONFIG_DIR=' + (childEnv.CLAUDE_CONFIG_DIR || '<unset>')
        + ' USERPROFILE=' + (childEnv.USERPROFILE || '<unset>'));
      // Truncated dump of the script bash will receive (one-line, escaped) —
      // helps catch parseArgv mis-tokenization when a user has a complex pipe.
      if (args.length >= 2 && args[0] === '-c') {
        const s = args[1];
        dlog('pipe bash-script[' + s.length + 'B]=' + JSON.stringify(s.length > 400 ? s.slice(0, 400) + '...' : s));
      }

      // Capture stderr so we can see WHY a child failed. Otherwise
      // Claude Code's statusLine discards stderr and we fly blind.
      const child = cp.spawn(program, args, {
        stdio: ['pipe', 'inherit', 'pipe'],
        windowsHide: true,
        env: childEnv,
      });
      let stderrBuf = '';
      child.stderr.on('data', (d) => { stderrBuf += d.toString(); if (stderrBuf.length > 4096) stderrBuf = stderrBuf.slice(-4096); });
      child.stdin.on('error', () => {}); // pipe might break if downstream is fast
      try { child.stdin.write(stdin); } catch {}
      try { child.stdin.end(); } catch {}
      child.on('exit', code => {
        const exitTag = (code === 3221225794 ? 'exit=0xC0000142(DLL_INIT_FAILED)' : 'exit=' + code);
        dlog('pipe ' + exitTag + (stderrBuf ? ' stderr=' + JSON.stringify(stderrBuf.trim().slice(0, 600)) : ''));
        process.exit(code || 0);
      });
      child.on('error', err => { dlog('pipe spawn-error ' + (err && err.message || err)); process.exit(0); });
    } catch (e) {
      dlog('pipe try-error ' + (e && e.message || e));
      process.exit(0);
    }
  } else {
    // Standalone fallback — minimal one-line statusline so the user
    // doesn't see a blank line if no other statusLine was configured.
    try {
      const m = parsed && parsed.model && parsed.model.display_name;
      const r = (parsed && parsed.rate_limits) || {};
      const fh = r.five_hour && r.five_hour.used_percentage;
      const sd = r.seven_day && r.seven_day.used_percentage;
      const parts = [];
      if (m) parts.push('[' + m + ']');
      if (fh != null) parts.push('5h ' + fh + '%');
      if (sd != null) parts.push('7d ' + sd + '%');
      if (parts.length) process.stdout.write(parts.join(' · '));
    } catch {}
    process.exit(0);
  }
});
""";
}
