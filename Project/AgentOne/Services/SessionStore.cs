using System.Text;
using System.Text.Json;
using AgentOne.Agent;

namespace AgentOne.Services;

/// <summary>
/// Appends a run's transcript to ~/.agent-one/sessions/&lt;id&gt;.jsonl — one JSON
/// object per line, so a half-written session is still readable and `tail -f`
/// shows a long run as it happens. Writing is best-effort: a failed log line
/// must never take down the run that produced it.
/// </summary>
public sealed class SessionStore
{
    private static readonly UTF8Encoding NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _path;

    public string Id { get; }
    public string Path => _path;

    private SessionStore(string id, string path)
    {
        Id = id;
        _path = path;
    }

    public static SessionStore Create(string kind)
    {
        var id = $"{DateTime.Now:yyyyMMdd-HHmmss}-{kind}";
        var path = System.IO.Path.Combine(AppPaths.EnsureSessionDir(), id + ".jsonl");
        return new SessionStore(id, path);
    }

    public void Prompt(string text, bool smart = false) => Append(new SessionEntry
    {
        Timestamp = Now(),
        Kind = "prompt",
        Mode = smart ? "smart" : "basic",
        Text = text
    });

    public void Step(AgentStep step) => Append(new SessionEntry
    {
        Timestamp = Now(),
        Kind = "step",
        Tool = step.Tool,
        Ok = step.Ok,
        Text = step.Detail,
        ElapsedMs = step.ElapsedMs > 0 ? step.ElapsedMs : null
    });

    /// <summary>
    /// One of smart mode's decisions — "route" before the loop, "escalation"
    /// after the draft — with what it chose, how sure it was, and what that meant.
    /// </summary>
    public void Decision(string kind, Llm.Decision.Decision decision, string verdict) => Append(new SessionEntry
    {
        Timestamp = Now(),
        Kind = kind,
        Tool = decision.Ok ? decision.Choice : null,
        Ok = decision.Ok,
        Text = decision.Ok
            ? $"{verdict} · confidence {decision.Confidence:0.00}"
            : $"{verdict} · {decision.Message}",
        ElapsedMs = decision.ElapsedMs > 0 ? decision.ElapsedMs : null
    });

    public void Result(AgentRun run) => Append(new SessionEntry
    {
        Timestamp = Now(),
        Kind = "result",
        Ok = run.Succeeded,
        Tool = run.Reason.ToString(),
        Text = run.Text
    });

    private void Append(SessionEntry entry)
    {
        try
        {
            var line = JsonSerializer.Serialize(entry, AgentOneWireJson.Default.SessionEntry);
            // Encoding.UTF8 writes a BOM when it creates the file, and a BOM on
            // line 1 makes the first record unparseable to strict JSONL readers
            // (json.loads, jq -c, most log shippers). JSONL is bytes-of-UTF-8,
            // no preamble.
            File.AppendAllText(_path, line + "\n", NoBom);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging is a convenience, never a reason to fail the run.
        }
    }

    private static string Now() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
}
