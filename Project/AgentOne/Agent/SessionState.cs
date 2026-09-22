namespace AgentOne.Agent;

/// <summary>What the status view shows: everything counted since the session started.</summary>
public readonly record struct SessionCounters(
    int Turns,
    int JevCalls,
    long JevMs,
    int Escalations,
    int Designs,
    int ApprovalsAsked,
    int ApprovalsGranted,
    int ToolCalls);

/// <summary>
/// One chat session's mutable state, owned in one place.
///
/// A single owner, mutations serialised, and reads that hand out immutable
/// snapshots so nothing mutable escapes. The session that owns it now lives
/// inside an Akka actor (Actors/AgentLoopActor), but this lock stays: the
/// counters are touched from the turn's pool thread, tool callbacks and the
/// decision engine, none of which run on the actor's thread.
///
/// The mode, the counters and the read grants are touched from the prompt,
/// the progress timer, tool callbacks and the decision engine at once, which is
/// why they all live behind one lock.
/// </summary>
public sealed class SessionState
{
    private readonly Lock _gate = new();
    private readonly List<string> _grants = [];

    private bool _smart;
    private int _turns, _jevCalls, _escalations, _designs, _asked, _granted, _toolCalls;
    private long _jevMs;

    public SessionState(bool smart) => _smart = smart;

    /// <summary>An immutable view of everything, taken atomically.</summary>
    public Snapshot Read()
    {
        lock (_gate)
            return new Snapshot(_smart,
                new SessionCounters(_turns, _jevCalls, _jevMs, _escalations, _designs, _asked, _granted, _toolCalls),
                _grants.ToArray());
    }

    /// <param name="Smart">Whether this session routes and escalates through the decision engine.</param>
    /// <param name="Counters">Everything counted so far.</param>
    /// <param name="ReadGrants">Folders outside the root this session may read.</param>
    public readonly record struct Snapshot(bool Smart, SessionCounters Counters, IReadOnlyList<string> ReadGrants)
    {
        public int Turns => Counters.Turns;
    }

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

    public void CountJev(long elapsedMs)
    {
        lock (_gate) { _jevCalls++; _jevMs += Math.Max(0, elapsedMs); }
    }

    public void CountEscalation() { lock (_gate) _escalations++; }
    public void CountDesign() { lock (_gate) _designs++; }
    public void CountToolCall() { lock (_gate) _toolCalls++; }

    public void CountApproval(bool granted)
    {
        lock (_gate) { _asked++; if (granted) _granted++; }
    }

    /// <summary>Remembers a folder the person named; true when it is new.</summary>
    public bool Grant(string directory)
    {
        lock (_gate)
        {
            if (_grants.Contains(directory, StringComparer.OrdinalIgnoreCase)) return false;
            _grants.Add(directory);
            return true;
        }
    }

    /// <summary>Forgets the conversation's progress and grants, keeping the mode.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _turns = _jevCalls = _escalations = _designs = _asked = _granted = _toolCalls = 0;
            _jevMs = 0;
            _grants.Clear();
        }
    }
}
