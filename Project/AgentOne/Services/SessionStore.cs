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

    /// <summary>What smart mode decided before the loop ran, and whether it was acted on.</summary>
    public void Plan(Agent.SmartPlan plan) => Append(new SessionEntry
    {
        Timestamp = Now(),
        Kind = "plan",
        Tool = plan.Decision?.Choice,
        Ok = plan.Confident,
        Text = plan.Decision is { } d
            ? $"{(plan.NeedsReview ? "needs a person" : plan.Confident ? "steering" : "unsure, not steering")} "
              + $"· confidence {d.Confidence:0.00} · options: {string.Join(", ", plan.Options.Select(o => o.Name))}"
            : plan.Options.Count == 0 ? "no plan produced" : "one approach, nothing to decide",
        // Planning (the model) plus deciding (the engine): the whole cost of smart mode this turn.
        ElapsedMs = plan.PlanningMs + (plan.Decision?.ElapsedMs ?? 0)
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
