namespace AgentOne.Services;

/// <summary>
/// Shows what the agent is doing while it does it.
///
/// A run that searches the web and reads two pages takes twenty seconds, and
/// twenty silent seconds read as "it has hung". This writes a single live line
/// to <b>stderr</b> — never stdout, which belongs to the answer — and rewrites
/// it in place, so piping and `--json` are unaffected.
///
/// When stderr is not a terminal it degrades to one plain line per step: a CI
/// log wants a record, not an animation.
/// </summary>
public sealed class ProgressDisplay : IDisposable
{
    private static readonly string[] Frames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];

    private readonly TextWriter _out;
    private readonly bool _animate;
    private readonly Lock _gate = new();
    private readonly System.Diagnostics.Stopwatch _sinceActivity = System.Diagnostics.Stopwatch.StartNew();
    private readonly Timer? _ticker;

    private string _activity = "";
    private int _frame;
    private int _lastLineLength;
    private bool _stopped;

    public ProgressDisplay(TextWriter output, bool animate)
    {
        _out = output;
        _animate = animate;

        if (_animate)
            _ticker = new Timer(_ => Repaint(), null, TimeSpan.FromMilliseconds(120), TimeSpan.FromMilliseconds(120));
    }

    /// <summary>The display the current terminal can support, or a silent one.</summary>
    public static ProgressDisplay For(bool enabled) =>
        new(Console.Error, animate: enabled && !Console.IsErrorRedirected);

    /// <summary>What the agent is doing now. Replaces whatever was showing.</summary>
    public void Activity(string what)
    {
        lock (_gate)
        {
            if (_stopped) return;
            _activity = what;
            _sinceActivity.Restart();
            _frame = 0;
        }

        if (_animate) Repaint();
        else WriteLine("… " + what);
    }

    /// <summary>
    /// A step that finished. Stays on screen; the live line moves below it.
    /// The display times the step itself — the caller knows what happened, this
    /// knows when it started showing, and asking the caller for both is how the
    /// duration ends up reported as 0.0s.
    /// </summary>
    public void Done(string what, bool ok)
    {
        lock (_gate)
        {
            if (_stopped) return;
            var elapsed = _sinceActivity.Elapsed;
            Erase();
            _out.WriteLine($"{(ok ? "✓" : "✗")} {what}  ({Seconds(elapsed)})");
            _activity = "";
            _out.Flush();
        }
    }

    /// <summary>Re-arms a display that was stopped — the chat REPL stops one per turn.</summary>
    public void Restart()
    {
        lock (_gate)
        {
            _stopped = false;
            _activity = "";
            _lastLineLength = 0;
        }
    }

    /// <summary>Clears the live line until the next Restart. Safe to call twice.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (_stopped) return;
            _stopped = true;
            Erase();
            _out.Flush();
        }

    }

    private void Repaint()
    {
        lock (_gate)
        {
            if (_stopped || _activity.Length == 0 || !_animate) return;

            var frame = Frames[_frame++ % Frames.Length];
            var line = $"{frame} {_activity}  ({Seconds(_sinceActivity.Elapsed)})";

            Erase();
            _out.Write(line);
            _out.Flush();
            _lastLineLength = line.Length;
        }
    }

    /// <summary>Wipes the live line so the next write does not sit on top of it.</summary>
    private void Erase()
    {
        if (!_animate || _lastLineLength == 0) return;

        _out.Write('\r');
        _out.Write(new string(' ', _lastLineLength));
        _out.Write('\r');
        _lastLineLength = 0;
    }

    private void WriteLine(string line)
    {
        lock (_gate)
        {
            if (_stopped) return;
            _out.WriteLine(line);
            _out.Flush();
        }
    }

    private static string Seconds(TimeSpan elapsed) =>
        elapsed.TotalSeconds < 10 ? $"{elapsed.TotalSeconds:0.0}s" : $"{elapsed.TotalSeconds:0}s";

    public void Dispose()
    {
        Stop();
        _ticker?.Dispose();
    }
}
