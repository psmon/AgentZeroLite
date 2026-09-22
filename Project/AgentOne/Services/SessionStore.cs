using System.Text;
using System.Text.Json;
using AgentOne.Agent;

namespace AgentOne.Services;

/// <summary>
/// Appends a session's transcript to a <c>.jsonl</c> file — one JSON object per
/// line, so a half-written session is still readable and `tail -f` shows a long
/// run as it happens. Writing is best-effort: a failed log line must never take
/// down the run that produced it. A session lives in its workspace's folder
/// (see <see cref="WorkspaceStore"/>), which is what makes it resumable there.
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

    /// <summary>A new session file in the global sessions folder (runs with no workspace store).</summary>
    public static SessionStore Create(string kind) => Create(kind, AppPaths.EnsureSessionDir());

    /// <summary>A new session file in <paramref name="directory"/>.</summary>
    public static SessionStore Create(string kind, string directory)
    {
        Directory.CreateDirectory(directory);
        var id = $"{DateTime.Now:yyyyMMdd-HHmmss}-{kind}";
        var path = System.IO.Path.Combine(directory, id + ".jsonl");
        return new SessionStore(id, path);
    }

    /// <summary>An existing session file, to keep appending to after a resume.</summary>
    public static SessionStore Open(string path) =>
        new(System.IO.Path.GetFileNameWithoutExtension(path), System.IO.Path.GetFullPath(path));

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

    /// <summary>The task the session is on, as named by the model. The last one is the session's title.</summary>
    public void Title(string title) => Append(new SessionEntry
    {
        Timestamp = Now(),
        Kind = "title",
        Text = title
    });

    /// <summary>Marks where a resumed session picked up, so the file reads honestly.</summary>
    public void Resumed() => Append(new SessionEntry
    {
        Timestamp = Now(),
        Kind = "resumed",
        Text = "session resumed"
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
