using Akka.Actor;
using Akka.Event;
using ZeroWearable.Ble;

namespace ZeroWearable.Actors;

/// <summary>
/// The Claude HUD channel: Claude Code's statusLine and hooks describe what a session is
/// doing, and the watch's HUD app renders it.
///
/// The device side needs nothing new - hud_ble consumes "S" and "E" lines itself, which is why
/// the HUD app keeps working unchanged. What changed is who writes them: this host owns the BLE
/// link now, so it has to carry this traffic as well as the two chat apps'. The hooks already
/// installed in ~/.claude keep posting to the same local port, so settings.json is untouched.
///
/// An actor rather than a direct write because the writes then serialise behind one mailbox
/// (a statusLine update and a hook event can arrive on different HTTP threads at once), and
/// because a later consumer - showing session state on AskBot's screen, say - subscribes here
/// instead of racing for the link.
/// </summary>
public sealed class HudActor : UntypedActor
{
    /// <summary>Null when the host runs with the radio off: every line is then a reported drop,
    /// which is still the fastest way to see that the hooks are wired to this endpoint.</summary>
    private readonly BleLink? _link;
    private readonly ILoggingAdapter _log = Context.GetLogger();

    /// <summary>Claude Code statusLine payload, verbatim.</summary>
    public sealed record Status(string Json);
    /// <summary>Claude Code hook payload, verbatim.</summary>
    public sealed record Event(string Json);
    /// <summary>The watch just connected — see <see cref="Replay"/> for why that is our cue.</summary>
    public sealed record LinkUp;

    public long Statuses, Events, Dropped;
    public string LastStatus = "";
    private DateTime _lastStatusAtUtc = DateTime.MinValue;

    /// <summary>
    /// How old a status may be and still be worth showing on a watch that just connected.
    /// A statusLine describes a live session; past this it is a claim about cost and context
    /// that may no longer be true, and the HUD's own "waiting for sessions..." is the more
    /// honest screen.
    /// </summary>
    private static readonly TimeSpan StatusTtl = TimeSpan.FromMinutes(5);

    /// <summary>
    /// statusLine fires on nearly every render, so logging each one at Info would bury the
    /// rest of the host's output. The first line of a run is always logged (that is the one
    /// that answers "are the hooks reaching me?"), then one in this many.
    /// </summary>
    private const int StatusLogEvery = 20;

    public HudActor(BleLink? link) => _link = link;

    protected override void OnReceive(object message)
    {
        switch (message)
        {
            case LinkUp:
                Replay();
                break;

            case Status status:
                LastStatus = status.Json;
                _lastStatusAtUtc = DateTime.UtcNow;
                Statuses++;
                // Every event line is logged; statusLine is sampled (see StatusLogEvery).
                Send('S', status.Json, loud: Statuses == 1 || Statuses % StatusLogEvery == 0);
                break;

            case Event hookEvent:
                Events++;
                Send('E', hookEvent.Json, loud: true);
                break;

            case WriteFailed failed:
                Dropped++;
                _log.Warning("{0} line, {1} bytes, the link refused the write (dropped {2})",
                    failed.Tag, failed.Bytes, Dropped);
                break;

            default:
                Unhandled(message);
                break;
        }
    }

    /// <summary>
    /// Pushes the last status to a watch that has just connected.
    ///
    /// <para>Without this the watch sits on "waiting for sessions..." until Claude Code
    /// happens to render a statusLine — and it renders on interaction, not on a timer. A host
    /// restart or a watch reboot in the middle of a live session therefore leaves the HUD
    /// blank for as long as nobody types, which reads exactly like a link that never came up.
    /// Measured on 2026-09-18: link up at 00:33:01, first S line at 00:36:31, zero drops in
    /// between.</para>
    ///
    /// <para>This does not conjure a session out of nothing: on a cold host start there is no
    /// status yet and the watch's waiting screen is correct. What it fixes is the reconnect,
    /// where we already know what the screen should say.</para>
    /// </summary>
    private void Replay()
    {
        if (LastStatus.Length == 0)
        {
            _log.Info("watch connected; no statusLine seen yet, so nothing to show it");
            return;
        }
        var age = DateTime.UtcNow - _lastStatusAtUtc;
        if (age > StatusTtl)
        {
            _log.Info("watch connected; last status is {0:F0} min old, too stale to replay",
                age.TotalMinutes);
            return;
        }
        _log.Info("watch connected; replaying the last status ({0:F0} s old)", age.TotalSeconds);
        Send('S', LastStatus, loud: true);
    }

    /// <param name="loud">
    /// Log this one at Info. Dropping used to be silent at Debug, which the host does not
    /// emit — so a HUD that was working and a HUD nobody had wired up looked identical from
    /// the outside. Now the panel shows either "-> watch" or the reason it went nowhere.
    /// </param>
    private void Send(char tag, string json, bool loud)
    {
        if (_link?.IsConnected != true)
        {
            // The watch is simply not in range; the HUD is a display, so dropping is right —
            // there is no value in replaying a status from ten minutes ago.
            Dropped++;
            if (loud)
            {
                _log.Info("{0} line, {1} bytes, dropped ({2}) — {3}", tag, json.Length, Dropped,
                    _link is null ? "BLE is off on this host" : "no watch connected");
            }
            return;
        }

        var self = Self;
        _ = _link.SendLineAsync($"{tag} {json}").ContinueWith(sent =>
        {
            // SendLineAsync answers false when the write was refused (link dropped mid-send,
            // payload over the MTU). Swallowing that was the other half of the blind spot.
            if (sent.IsCompletedSuccessfully && !sent.Result)
                self.Tell(new WriteFailed(tag, json.Length));
        }, TaskScheduler.Default);

        if (loud) _log.Info("{0} line, {1} bytes -> watch (S:{2} E:{3} dropped:{4})",
            tag, json.Length, Statuses, Events, Dropped);
    }

    /// <summary>A write the link refused, reported back on the mailbox so the counter stays
    /// single-threaded.</summary>
    private sealed record WriteFailed(char Tag, int Bytes);
}
