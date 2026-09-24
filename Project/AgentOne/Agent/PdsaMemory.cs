using System.Text;
using AgentOne.Graph;
using AgentOne.Llm.Decision;

namespace AgentOne.Agent;

/// <summary>What the cycle decided for one turn, carried from before the turn to after it.</summary>
/// <param name="Cycle">The cycle this turn belongs to.</param>
/// <param name="Step">plan / do / study / act — the phase the turn will be recorded as.</param>
/// <param name="Opened">True when this turn opened the cycle.</param>
/// <param name="Reinforces">The cycle this one exists to put right, or 0.</param>
public sealed record PdsaTurn(long Cycle, string Step, Decision Decision, bool Opened, long Reinforces = 0)
{
    public bool Closes => Step == SmartRouter.ActStep;
}

/// <summary>What was written for a turn: the phase, and Study's verdict when it was one.</summary>
public sealed record PdsaRecord(PdsaTurn Turn, Decision? Verdict)
{
    public string VerdictChoice => Verdict is { Ok: true } ? Verdict.Choice : "";
}

/// <summary>
/// Smart mode's improvement loop: Deming's <b>Plan · Do · Study · Act</b> run
/// across turns, recorded in the workspace graph beside the knowledge it
/// produces.
///
/// <para><b>Entering.</b> Planning is the door. A turn the scope question sent
/// to the strong model for a design <em>is</em> a Plan, so it opens a cycle
/// without another question; with no design and no cycle running, only a
/// <em>confident</em> plan from the engine opens one — an unsure guess would
/// drag the next several turns into a cycle nobody asked for. Everything else
/// leaves PDSA out of the way entirely.</para>
///
/// <para><b>Running.</b> While a cycle is open the engine is asked one fixed
/// question per turn — which of the four steps this request is — and the turn
/// is recorded as that phase. Inside a running cycle the choice is followed
/// without the floor: a mislabelled phase costs a row, not an action. A
/// <em>new</em> plan mid-cycle is the exception, because it starts a cycle:
/// the running one is abandoned and the new one reinforces it when its Study
/// said partial or unmet.</para>
///
/// <para><b>Study, not Check.</b> The third step asks what was learned against
/// what Plan predicted, and the engine answers met / partial / unmet. That
/// verdict is what makes the next cycle a <c>REINFORCES</c> cycle rather than
/// an unrelated one — the loop's only feedback edge.</para>
///
/// <para><b>Closing.</b> Act closes the cycle and wires it to knowledge: what
/// its turns taught (<c>TAUGHT</c>) and what the graph had already taught them
/// (<c>BUILT_ON</c>). That happens after the turn's distillation, never before
/// — knowledge is learned off the turn, and closing first would attach the
/// edges of a cycle whose last lesson is not stored yet.</para>
/// </summary>
public sealed class PdsaMemory(KnowledgeGraph graph)
{
    /// <summary>Chars of an answer kept as what the Plan predicted, or as what Study observed.</summary>
    public const int OutcomeChars = 1800;

    /// <summary>The cycle being run, or null.</summary>
    public PdsaCycleRow? Current => graph.OpenCycle();

    /// <summary>A plan the strong model wrote: no engine call needed to know the step.</summary>
    private static Decision Designed { get; } = new(
        true, SmartRouter.PlanStep, 1.0,
        new Dictionary<string, double> { [SmartRouter.PlanStep] = 1.0 },
        "the stronger model designed this turn", 0, Called: false);

    // ------------------------------------------------------------ before

    /// <summary>
    /// Where this turn sits in the cycle, and which cycle. Null when PDSA has
    /// nothing to say: no cycle is running and this request is not a plan.
    /// </summary>
    /// <param name="designed">The strong model produced a design for this turn.</param>
    public async Task<PdsaTurn?> BeforeAsync(SmartRouter router, string request, bool designed, CancellationToken ct)
    {
        var open = graph.OpenCycle();

        if (designed)
            return open is { Phases.Count: 0 }
                ? new PdsaTurn(open.Id, SmartRouter.PlanStep, Designed, false)
                : Start(request, Designed);

        if (open is null)
        {
            // Nothing running: the engine has to be sure before it starts one.
            var first = await router.PdsaStepAsync(request, "No cycle is running. This request would start one.", ct);
            return first is { Confident: true, Step: SmartRouter.PlanStep } ? Start(request, first.Decision) : null;
        }

        var step = await router.PdsaStepAsync(request, Context(open), ct);

        // A plan while a cycle is running is the next cycle, not a second plan
        // in this one — unless this one has not planned yet.
        if (step.Kind == SmartRouter.PlanStep && open.Phase(KnowledgeGraph.PlanPhase) is not null)
            return Start(request, step.Decision);

        return new PdsaTurn(open.Id, step.Kind, step.Decision, false);
    }

    private PdsaTurn Start(string request, Decision decision)
    {
        // Read before starting: StartCycle abandons whatever is open, and the
        // cycle being abandoned is exactly the one that may need reinforcing.
        var reinforce = ReinforceTarget();
        var id = graph.StartCycle(request, reinforce);
        return new PdsaTurn(id, SmartRouter.PlanStep, decision, true, reinforce);
    }

    /// <summary>The newest cycle whose Study fell short — the one a new plan is putting right. 0 when none.</summary>
    private long ReinforceTarget()
    {
        var last = graph.OpenCycle() ?? graph.LastClosedCycle();
        if (last is null) return 0;

        var verdict = last.Phase(KnowledgeGraph.StudyPhase)?.Verdict is { Length: > 0 } v ? v : last.Verdict;
        return verdict is SmartRouter.PartialVerdict or SmartRouter.UnmetVerdict ? last.Id : 0;
    }

    // ------------------------------------------------------------- after

    /// <summary>
    /// Writes the turn as its phase. A Plan stores what it predicts; a Study
    /// asks the engine how that prediction held and stores the verdict with
    /// what actually happened.
    /// </summary>
    /// <param name="plan">The design's text when there was one — what the plan actually promised, rather than the answer about it.</param>
    public async Task<PdsaRecord> RecordAsync(
        SmartRouter router, PdsaTurn turn, string turnId, string request, string note, string outcome,
        string? plan, CancellationToken ct)
    {
        var meta = new Dictionary<string, string>();
        Decision? verdict = null;

        switch (turn.Step)
        {
            case SmartRouter.PlanStep:
                meta["expected"] = Clip(plan is { Length: > 0 } ? plan : outcome, OutcomeChars);
                break;

            case SmartRouter.StudyStep:
                verdict = await router.StudyVerdictAsync(graph.Cycle(turn.Cycle)?.Expected ?? "", request, outcome, ct);
                meta["verdict"] = verdict.Ok ? verdict.Choice : "";
                meta["actual"] = Clip(outcome, OutcomeChars);
                break;
        }

        // RAN_IN needs the turn node; MERGE makes this safe beside the
        // learning path, which remembers the same turn with its final outcome.
        graph.RememberTurn(turnId, request, outcome);
        graph.RecordPhase(turn.Cycle, turn.Step, turnId, request, note, meta);

        return new PdsaRecord(turn, verdict);
    }

    /// <summary>
    /// Act: close the loop and attach the knowledge edges. The cycle keeps its
    /// Study verdict; a cycle that acted without studying is closed "unjudged"
    /// rather than quietly counted as a success.
    /// </summary>
    public PdsaClosing Close(long cycleId)
    {
        var verdict = graph.Cycle(cycleId)?.Phase(KnowledgeGraph.StudyPhase)?.Verdict ?? "";
        return graph.CloseCycle(cycleId, verdict.Length == 0 ? "unjudged" : verdict);
    }

    // ------------------------------------------------------------ reading

    /// <summary>What the engine is told about the cycle it is placing a request in.</summary>
    public static string Context(PdsaCycleRow cycle)
    {
        var sb = new StringBuilder();
        sb.Append("Cycle ").Append(cycle.Id).Append(" — \"").Append(cycle.Title).Append("\", opened ").Append(cycle.Started);
        sb.Append("\nSteps already done: ").Append(cycle.Progress);

        if (cycle.Expected is { Length: > 0 } expected)
            sb.Append("\nWhat the plan said would happen: ").Append(SmartRouter.Clip(expected, 1200));

        foreach (var phase in cycle.Phases.Where(p => p.Kind != KnowledgeGraph.PlanPhase))
            sb.Append("\n- ").Append(phase.Kind).Append(": ").Append(SmartRouter.Clip(phase.Note, 300));

        return sb.ToString();
    }

    /// <summary>The status block's line, or null when no cycle has ever run here.</summary>
    public string? StatusLine()
    {
        var stats = graph.CycleStats();
        if (stats.Cycles == 0) return null;

        var rate = stats.Judged > 0 ? $" · plan met {stats.Met}/{stats.Judged}" : " · none studied yet";
        var running = graph.OpenCycle() is { } open ? $" · running #{open.Id} ({open.Progress})" : "";
        return $"{stats.Cycles} cycles · {stats.Phases} phases{rate} · {stats.KnowledgeEdges} knowledge edges{running}";
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
