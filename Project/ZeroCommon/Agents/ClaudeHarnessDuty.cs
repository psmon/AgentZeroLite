using System.Text;
using System.Text.Json;

namespace Agent.Common.Agents;

/// <summary>An expert the main conversation took on itself (no sub-agent), as a web plugin sees it.</summary>
/// <param name="Via">"read" — it read the expert's definition to follow it (Read, Grep, or a shell command
/// naming that one file); "log" — it wrote that expert's log under <c>harness/logs/&lt;name&gt;/</c>;
/// "command" — <c>/harness-creator</c> was typed; "skill" — the harness-creator skill was called.</param>
/// <param name="State">"working" while that turn runs, "done" once it ended, "stopped" when it went quiet with no end.</param>
public sealed record HarnessDutyView(
    string Name, string? Persona, string? Type, string Via, string State, long StartedAt, long? EndedAt);

/// <summary>The garden harness a Claude tab works in, and the experts on duty in its main conversation.</summary>
/// <param name="Experts">How many experts the garden has (its <c>agents</c> list, the gardener included).</param>
public sealed record HarnessView(string Name, string? Version, int Experts, IReadOnlyList<HarnessDutyView> OnDuty);

/// <summary>
/// Which garden-harness experts a Claude session's <b>main</b> conversation is working as — the inline
/// half of <see cref="KakashiHarness"/> (sub-agents are <see cref="ClaudeSubagentReader"/>'s). Read from
/// the session's own transcript, <c>&lt;config&gt;/projects/&lt;slug(cwd)&gt;/&lt;sessionId&gt;.jsonl</c>, with
/// no hook. A duty lasts one turn: it starts when the turn reads <c>harness/agents/&lt;name&gt;.md</c> — with
/// Read, Grep, or Bash/PowerShell (<c>cat</c>, measured) — or writes <c>harness/logs/&lt;name&gt;/…</c> (or at the
/// harness-creator command / skill, which puts the gardener on), and ends at the turn's <c>end_turn</c>
/// or at the person's next prompt. A turn that <i>edits</i> an agent file is tending the garden, not
/// working as that expert, so the edit takes that expert off duty.
///
/// <para>Main transcripts run to megabytes (1.3 MB measured on a day's session), so a first read
/// starts at the last <see cref="TailBytes"/> and each later pass reads only what was appended; a
/// session whose folder has no garden harness is never opened.</para>
/// </summary>
public sealed class ClaudeHarnessDutyReader
{
    private readonly HarnessLocator _locator;
    private readonly Func<DateTimeOffset> _now;
    private readonly Dictionary<string, Transcript> _cache = new(StringComparer.OrdinalIgnoreCase);

    public long KeepFinishedMs { get; init; } = 120_000;
    public long QuietStopMs { get; init; } = 600_000;
    public int TailBytes { get; init; } = 512 * 1024;

    public ClaudeHarnessDutyReader(HarnessLocator locator, Func<DateTimeOffset>? now = null)
    {
        _locator = locator;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    public static string TranscriptOf(AgentSessionRef s) =>
        Path.Combine(s.ConfigDir, "projects", ClaudeSessionLocator.Slug(s.Cwd), s.SessionId + ".jsonl");

    /// <summary>Null when the session is not in a garden harness; otherwise the garden and who is on duty.</summary>
    public HarnessView? Read(AgentSessionRef session)
    {
        if (session.Agent != "claude" || _locator.Find(session.Cwd) is not { } roster) return null;

        var path = TranscriptOf(session);
        if (!_cache.TryGetValue(path, out var t) || !ReferenceEquals(t.Roster, roster))
            _cache[path] = t = new Transcript(path, roster, TailBytes);
        t.CatchUp();

        var nowMs = _now().ToUnixTimeMilliseconds();
        var duties = new List<HarnessDutyView>();
        foreach (var d in t.Duties)
        {
            string state;
            long? ended;
            if (d.EndedAt is { } e) { state = "done"; ended = e; }
            else if (nowMs - t.LastAt > QuietStopMs) { state = "stopped"; ended = t.LastAt + QuietStopMs; }
            else { state = "working"; ended = null; }
            if (ended is { } end && nowMs - end > KeepFinishedMs) continue;
            duties.Add(new HarnessDutyView(d.Expert.Name, d.Expert.Persona, d.Expert.Type, d.Via, state, d.StartedAt, ended));
        }
        return new HarnessView(roster.Name, roster.Version, roster.Experts.Count, duties);
    }

    private sealed class Duty(HarnessExpert expert, string via, long startedAt)
    {
        public HarnessExpert Expert { get; } = expert;
        public string Via { get; } = via;
        public long StartedAt { get; } = startedAt;
        public long? EndedAt { get; set; }
    }

    private sealed class Transcript(string path, HarnessRoster roster, int tailBytes)
    {
        private long _offset = -1;
        private bool _skipTorn;
        private readonly StringBuilder _partial = new();

        public HarnessRoster Roster { get; } = roster;
        public List<Duty> Duties { get; } = [];
        public long LastAt { get; private set; }

        public void CatchUp()
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (_offset < 0 || fs.Length < _offset)
                {
                    // First look (or rewritten): only the tail can still hold a duty worth showing.
                    _offset = Math.Max(0, fs.Length - tailBytes);
                    _skipTorn = _offset > 0;
                    _partial.Clear();
                    Duties.Clear();
                }
                if (fs.Length == _offset) return;
                fs.Seek(_offset, SeekOrigin.Begin);
                var buf = new byte[fs.Length - _offset];
                var read = fs.ReadAtLeast(buf, buf.Length, throwOnEndOfStream: false);
                _offset += read;
                _partial.Append(Encoding.UTF8.GetString(buf, 0, read));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }

            var text = _partial.ToString();
            var lastNl = text.LastIndexOf('\n');
            if (lastNl < 0) return;
            _partial.Clear().Append(text, lastNl + 1, text.Length - lastNl - 1);
            var lines = text[..lastNl].Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (i == 0 && _skipTorn) { _skipTorn = false; continue; }   // the tail began mid-record
                if (lines[i].Length > 1) Fold(lines[i]);
            }
        }

        private void Fold(string line)
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var r = doc.RootElement;
                var type = Str(r, "type");
                if (type is not ("user" or "assistant")) return;
                if (Str(r, "timestamp") is { } ts && DateTimeOffset.TryParse(ts, out var at)) LastAt = at.ToUnixTimeMilliseconds();
                if (!r.TryGetProperty("message", out var msg)) return;

                if (type == "user")
                {
                    if (r.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True) return;   // skill text, caveats
                    var said = ClaudeSubagentReader.TextOf(msg, 2000);
                    if (said.Length == 0) return;                      // a tool result, not the person
                    EndTurn();                                         // the person spoke: the last turn is over
                    if (said.Contains("<command-name>", StringComparison.Ordinal) && KakashiHarness.IsGardenerCommand(said))
                        PutOn(KakashiHarness.Gardener, "command");
                    return;
                }

                if (msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                    foreach (var c in content.EnumerateArray())
                    {
                        if (Str(c, "type") != "tool_use" || Str(c, "name") is not { } name) continue;
                        c.TryGetProperty("input", out var input);
                        switch (name)
                        {
                            case "Read" when KakashiHarness.ExpertOfPath(Roster, Str(input, "file_path")) is { } e:
                                PutOn(e.Name, "read");
                                break;
                            case "Grep" when KakashiHarness.ExpertOfPath(Roster, Str(input, "path")) is { } e:
                                PutOn(e.Name, "read");
                                break;
                            case "Bash" or "PowerShell" when KakashiHarness.ExpertOfCommand(Roster, Str(input, "command"), out var writes) is { } e:
                                if (writes) TakeOff(e.Name);
                                else PutOn(e.Name, "read");
                                break;
                            case "Edit" or "Write" or "MultiEdit" when KakashiHarness.ExpertOfPath(Roster, Str(input, "file_path")) is { } e:
                                TakeOff(e.Name);
                                break;
                            case "Write" or "Edit" when KakashiHarness.ExpertOfLog(Roster, Str(input, "file_path")) is { } e:
                                PutOn(e.Name, "log");
                                break;
                            case "Skill" when KakashiHarness.IsGardenerCommand(Str(input, "skill")):
                                PutOn(KakashiHarness.Gardener, "skill");
                                break;
                        }
                    }
                if (Str(msg, "stop_reason") == "end_turn") EndTurn();
            }
            catch (JsonException) { /* a torn line — skipped */ }
        }

        private void PutOn(string name, string via)
        {
            if (Roster.Expert(name) is not { } expert) return;
            if (Duties.Any(d => d.EndedAt is null && string.Equals(d.Expert.Name, name, StringComparison.OrdinalIgnoreCase))) return;
            Duties.RemoveAll(d => string.Equals(d.Expert.Name, name, StringComparison.OrdinalIgnoreCase));   // back on: one entry
            Duties.Add(new Duty(expert, via, LastAt));
        }

        /// <summary>Editing an agent file is tending the garden: that expert's read duty is withdrawn.</summary>
        private void TakeOff(string name) =>
            Duties.RemoveAll(d => d.EndedAt is null && d.Via == "read"
                && string.Equals(d.Expert.Name, name, StringComparison.OrdinalIgnoreCase));

        private void EndTurn()
        {
            foreach (var d in Duties) d.EndedAt ??= LastAt;
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
