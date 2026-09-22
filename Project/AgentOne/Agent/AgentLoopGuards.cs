namespace AgentOne.Agent;

/// <summary>
/// The defenses that keep a small model from burning a run: the same tool call
/// over and over, and a reply that never parses. Both are cheap to detect and
/// both are worth one corrective nudge before the loop gives up — a model that
/// is told exactly what went wrong usually recovers on the next turn.
/// </summary>
public sealed class AgentLoopGuards(int repeatNudgeBudget = 1, int parseNudgeBudget = 2)
{
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    public int RepeatNudgesLeft { get; private set; } = repeatNudgeBudget;
    public int ParseNudgesLeft { get; private set; } = parseNudgeBudget;

    /// <summary>True when this exact call was already made in this run.</summary>
    public bool IsRepeat(ToolCall call) => _seen.Contains(call.Signature());

    public void Record(ToolCall call) => _seen.Add(call.Signature());

    /// <summary>Consumes one repeat nudge. False means the budget is gone and the run should stop.</summary>
    public bool TryConsumeRepeatNudge()
    {
        if (RepeatNudgesLeft <= 0) return false;
        RepeatNudgesLeft--;
        return true;
    }

    /// <summary>Consumes one parse nudge. False means the budget is gone and the run should stop.</summary>
    public bool TryConsumeParseNudge()
    {
        if (ParseNudgesLeft <= 0) return false;
        ParseNudgesLeft--;
        return true;
    }
}
