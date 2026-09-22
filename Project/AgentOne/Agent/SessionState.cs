namespace AgentOne.Agent;

/// <summary>
/// One chat session's mutable state, owned in one place.
///
/// Actor-shaped rather than an actor framework: a single owner, mutations
/// serialised, and reads that hand out immutable snapshots so nothing mutable
/// escapes. It is deliberately not Akka — agent-one is a Native AOT single
/// binary, and the actor runtime is exactly the kind of dependency that costs
/// that (the same reason this project does not reference ZeroCommon). If a
/// session ever needs supervision, remoting or persistence, that is the moment
/// to revisit it; a REPL turn does not.
///
/// The mode and the turn count are touched from the prompt, the progress timer
/// and async callbacks at once, which is why they live behind one lock.
/// </summary>
public sealed class SessionState
{
    private readonly Lock _gate = new();

    private bool _smart;
    private int _turns;

    public SessionState(bool smart) => _smart = smart;

    /// <summary>An immutable view of everything, taken atomically.</summary>
    public Snapshot Read()
    {
        lock (_gate) return new Snapshot(_smart, _turns);
    }

    /// <param name="Smart">Whether this session routes and escalates through the decision engine.</param>
    /// <param name="Turns">How many requests this session has handled.</param>
    public readonly record struct Snapshot(bool Smart, int Turns);

    /// <summary>Flips the mode and returns what it became.</summary>
    public bool ToggleSmart()
    {
        lock (_gate) return _smart = !_smart;
    }

    public void SetSmart(bool on)
    {
        lock (_gate) _smart = on;
    }

    public int CountTurn()
    {
        lock (_gate) return ++_turns;
    }

    /// <summary>Forgets the conversation's progress, keeping the mode.</summary>
    public void Reset()
    {
        lock (_gate) _turns = 0;
    }
}
