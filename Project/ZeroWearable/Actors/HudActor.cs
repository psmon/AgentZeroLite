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

    public long Statuses, Events, Dropped;
    public string LastStatus = "";

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
            case Status status:
                LastStatus = status.Json;
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
