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
    /// <summary>How many files were on disk when the last unwritten nudge went out; -1 before the first.</summary>
    private int _writesAtLastNudge = -1;

    /// <summary>Unwritten nudges sent so far, for the record.</summary>
    public int UnwrittenNudges { get; private set; }

    /// <summary>How many times a call was turned away because the route ruled its family out.</summary>
    public int FamilyRefusals { get; private set; }

    /// <summary>
    /// Counts one refusal and says whether the route should now be abandoned.
    ///
    /// The route is enforced, not suggested, because a small model treats a
    /// suggestion as one option among many — but enforcing it forever turns a
    /// 0.3 s guess into a wall. Measured: "테트리스 웹게임 만들어" routed to
    /// answer_directly at 0.77, the model's write_file for index.html and
    /// style.css were both turned away, and it then told the user it had
    /// created three files. Nothing was on disk.
    ///
    /// So the first refusal stands — that is the guard doing its job against a
    /// model idly reaching for the wrong family. A <em>second</em> one is the
    /// model insisting, and the model insisting is better evidence about the
    /// request than the route's one-shot guess was.
    /// </summary>
    public bool RefuseFamily()
    {
        FamilyRefusals++;
        return FamilyRefusals >= 2;
    }

    /// <summary>
    /// Whether to send the model back again for files it said it wrote and did
    /// not. Bounded by <em>progress</em>, not by a count.
    ///
    /// A flat budget of one was measured to be exactly one file's worth:
    /// nudged after writing index.html, the model wrote css/style.css, then
    /// claimed all six were done — and the spent budget let that through, with
    /// four files missing. Each nudge buys about one file, so a six-file build
    /// needs about six.
    ///
    /// The stop condition is the model giving up rather than a number: nudge
    /// again only when something was actually written since the last one.
    /// A model that was told what is missing and wrote nothing will not write
    /// anything the next time either, and the loop's own step budget bounds
    /// the rest.
    /// </summary>
    /// <param name="writesSoFar">Files successfully written in this turn.</param>
    public bool TryConsumeUnwrittenNudge(int writesSoFar)
    {
        if (_writesAtLastNudge >= 0 && writesSoFar <= _writesAtLastNudge) return false;
        _writesAtLastNudge = writesSoFar;
        UnwrittenNudges++;
        return true;
    }

    /// <summary>True when this exact call was already made in this run.</summary>
    public bool IsRepeat(ToolCall call) => _seen.Contains(call.Signature());

    public void Record(ToolCall call) => _seen.Add(call.Signature());

    /// <summary>
    /// Un-records a call that never ran, so asking for it again is not counted
    /// as repetition. A call the route turned away produced no result for the
    /// model to use, and the repeat guard's whole premise — "you already have
    /// its result" — is false for it. Without this, the model's second attempt
    /// is intercepted by the repeat nudge and never reaches the family check
    /// that would have let the route give way.
    /// </summary>
    public void Forget(ToolCall call) => _seen.Remove(call.Signature());

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
