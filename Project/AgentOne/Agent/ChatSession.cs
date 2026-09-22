using AgentOne.Llm;
using AgentOne.Llm.Decision;
using AgentOne.Services;
using AgentOne.Tools;

namespace AgentOne.Agent;

/// <summary>Why a turn stopped to wait for a person.</summary>
public enum PauseReason
{
    /// <summary>The decision engine chose "a person has to decide this".</summary>
    NeedsPerson,
    /// <summary>The decision cleared nothing: the options did not separate.</summary>
    Unsure
}

/// <param name="Request">What the user asked, before any plan was applied.</param>
/// <param name="Plan">The plan whose decision paused the turn.</param>
public sealed record PendingReview(string Request, SmartPlan Plan, PauseReason Reason);

/// <summary>
/// One conversation, from the first prompt to /exit — everything a chat does
/// that is not drawing. The plain REPL and the full-screen TUI are both thin
/// renderers over this, so a turn plans, decides, pauses for a person and
/// resumes identically whichever one is in front of it. Two copies of that
/// logic would drift within a week.
/// </summary>
public sealed class ChatSession : IDisposable
{
    private readonly IChatProvider _provider;
    private readonly CompositeToolbelt _toolbelt;
    private readonly AgentLoop _loop;
    private readonly IDecisionEngine _engine;
    private readonly bool _smartAvailable;
    private readonly SmartTurn _smart;
    private readonly SessionStore? _log;
    private readonly SessionState _state;
    private readonly AgentConfig _config;

    /// <summary>What the agent is doing right now, for a status line.</summary>
    public event Action<string>? ActivityStarted;

    /// <summary>A tool step finished.</summary>
    public event Action<AgentStep>? StepCompleted;

    /// <summary>A fragment of the answer, as the model writes it.</summary>
    public event Action<string>? AnswerDelta;

    /// <summary>Smart mode made a plan (steering, unsure, or needing a person).</summary>
    public event Action<SmartPlan>? PlanMade;

    public ChatSession(AgentConfig config, string root, bool streaming)
        : this(config, root, streaming, ChatProviderFactory.Create(config), new JevClient(config))
    {
    }

    private ChatSession(AgentConfig config, string root, bool streaming, IChatProvider provider, JevClient jev)
        : this(config, root, streaming, provider, jev, jev.HasKey)
    {
    }

    /// <summary>Test seam: a session over a scripted provider and engine, without the network.</summary>
    internal ChatSession(AgentConfig config, string root, bool streaming,
        IChatProvider provider, IDecisionEngine engine, bool smartAvailable)
    {
        _config = config;
        _provider = provider;
        _engine = engine;
        _smartAvailable = smartAvailable;

        _toolbelt = new CompositeToolbelt(
            (ToolCatalog.FilesFamily, new LocalFileToolbelt(root)),
            (ToolCatalog.WebFamily, new WebToolbelt(TimeSpan.FromSeconds(config.WebTimeoutSeconds))));

        _loop = new AgentLoop(_provider, _toolbelt, config.MaxSteps) { Streaming = streaming };
        _loop.Reset();
        _loop.ActivityStarted += what => ActivityStarted?.Invoke(what);
        _loop.StepCompleted += step => { _log?.Step(step); StepCompleted?.Invoke(step); };
        _loop.AnswerDelta += fragment => AnswerDelta?.Invoke(fragment);

        _smart = new SmartTurn(_provider, _engine, config.JevConfidenceFloor);
        _smart.ActivityStarted += what => ActivityStarted?.Invoke(what);

        _log = config.SaveSessions ? SessionStore.Create("chat") : null;

        // Smart starts where the config left it, but only with a key to make it
        // work. Starting "on" with no key would fail on the first turn.
        _state = new SessionState(config.SmartMode && _smartAvailable);
    }

    public string ProviderName => _provider.Name;
    public string Model => _config.Model;
    public string ToolScope => _toolbelt.Scope;
    public string? LogPath => _log?.Path;
    public bool SmartAvailable => _smartAvailable;
    public double ConfidenceFloor => _config.JevConfidenceFloor;

    public bool Smart => _state.Read().Smart;
    public PendingReview? Pending => _state.Read().Pending;

    /// <summary>Flips the mode. False, with a reason, when it cannot.</summary>
    public bool TryToggleSmart(out string message)
    {
        if (!_smartAvailable)
        {
            message = "no TypeSafe key — set one on the Smart step of `agent-one tui`";
            return false;
        }

        message = _state.ToggleSmart()
            ? $"smart mode on — plan first, decide, act above confidence {_config.JevConfidenceFloor:0.00}"
            : "basic mode — straight to the tool loop";
        return true;
    }

    public void Reset()
    {
        _loop.Reset();
        _state.Reset();
    }

    /// <summary>
    /// Handles one line of input: a resume if a turn is waiting, else a new turn.
    /// Returns null when the turn paused for a person instead of finishing.
    /// </summary>
    public async Task<AgentRun?> SubmitAsync(string line, CancellationToken ct)
    {
        line = line.Trim();
        var snapshot = _state.Read();

        if (_state.TakeReview() is { } review)
            return await ResumeAsync(review, line, ct);

        if (line.Length == 0) return null;

        _log?.Prompt(line, snapshot.Smart);
        _state.CountTurn();

        var prompt = line;

        if (snapshot.Smart)
        {
            var plan = await _smart.PrepareAsync(line, _toolbelt.Scope, ct);
            _log?.Plan(plan);
            PlanMade?.Invoke(plan);

            if (plan.NeedsReview)
            {
                _state.AwaitReview(new PendingReview(line, plan, PauseReason.NeedsPerson));
                return null;
            }

            if (plan.HasChoice && !plan.Confident)
            {
                // The options did not separate. Rather than guess, show them and
                // let the next line settle it — a number, an approval, or a
                // redirect, and an empty line accepts the engine's pick.
                _state.AwaitReview(new PendingReview(line, plan, PauseReason.Unsure));
                return null;
            }

            if (plan.Confident) prompt = plan.Guidance(line);
        }

        var run = await _loop.RunAsync(prompt, ct);
        _log?.Result(run);
        return run;
    }

    /// <summary>
    /// A paused turn, with the person's answer. A number picks an approach; an
    /// empty line accepts the engine's pick when it was merely unsure; anything
    /// else is guidance carried into the turn as an approval.
    /// </summary>
    private async Task<AgentRun?> ResumeAsync(PendingReview review, string answer, CancellationToken ct)
    {
        var options = review.Plan.Options.Where(o => o.Name != SmartTurn.ReviewOption).ToList();
        string prompt;

        if (int.TryParse(answer, out var picked) && picked >= 1 && picked <= options.Count)
        {
            prompt = SmartPlan.GuidanceFor(review.Request, options[picked - 1]);
        }
        else if (answer.Length == 0)
        {
            if (review.Reason == PauseReason.NeedsPerson)
            {
                // Silence is not approval. Park it again and say so.
                _state.AwaitReview(review);
                ActivityStarted?.Invoke("waiting — type an answer, or pick an approach by number");
                return null;
            }
            prompt = review.Plan.Guidance(review.Request);
        }
        else
        {
            prompt = $"{review.Request}{Environment.NewLine}{Environment.NewLine}[approved] {answer}";
        }

        _log?.Prompt(answer.Length == 0 ? "(accepted the engine's pick)" : answer, smart: true);

        var run = await _loop.RunAsync(prompt, ct);
        _log?.Result(run);
        return run;
    }

    public void Dispose()
    {
        (_engine as IDisposable)?.Dispose();
        _toolbelt.Dispose();
        (_provider as IDisposable)?.Dispose();
    }
}
