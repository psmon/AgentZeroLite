using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Agent.Common.Llm.Tools;

/// <summary>What a terminal peer said through <c>bot-chat</c>: its tab name and the text.</summary>
public sealed record PeerSignal(string PeerName, string Text);

/// <summary>
/// Peer replies that arrived while an agent-loop run was in flight.
///
/// <para>Measured (2026-11-24): the bot greeted Hermes, Hermes answered
/// <c>bot-chat "DONE(handshake-ok)"</c> eleven seconds later — and the run that was waiting for
/// exactly that never saw it. The reply became a <c>StartAgentLoop</c>, the loop actor was
/// Running and ignores a second start, and the model went on polling <c>read_terminal</c> for
/// an answer that had already been delivered on the other channel. Whatever a peer says mid-run
/// now waits here: the loop's <c>wait</c> tool returns early with it, and what is still here
/// when the run ends starts the next cycle instead of being lost.</para>
///
/// <para>Thread-safe: the bot actor posts from its mailbox, the loop waits on a pool thread.</para>
/// </summary>
public sealed class PeerSignalInbox
{
    private readonly object _gate = new();
    private readonly List<PeerSignal> _pending = new();
    private TaskCompletionSource? _arrived;

    public int Count { get { lock (_gate) return _pending.Count; } }

    public void Post(PeerSignal signal)
    {
        TaskCompletionSource? wake;
        lock (_gate)
        {
            _pending.Add(signal);
            wake = _arrived;
            _arrived = null;
        }
        wake?.TrySetResult();
    }

    /// <summary>Takes everything that is waiting, oldest first.</summary>
    public IReadOnlyList<PeerSignal> Drain()
    {
        lock (_gate)
        {
            if (_pending.Count == 0) return [];
            var taken = _pending.ToArray();
            _pending.Clear();
            return taken;
        }
    }

    public void Clear()
    {
        lock (_gate) _pending.Clear();
    }

    /// <summary>
    /// Returns what is waiting, or waits up to <paramref name="max"/> for the first arrival.
    /// An empty list means the time passed with nothing said.
    /// </summary>
    public async Task<IReadOnlyList<PeerSignal>> WaitAsync(TimeSpan max, CancellationToken ct)
    {
        Task arrived;
        lock (_gate)
        {
            if (_pending.Count > 0)
            {
                var taken = _pending.ToArray();
                _pending.Clear();
                return taken;
            }
            _arrived ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            arrived = _arrived.Task;
        }

        using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var delay = Task.Delay(max, timer.Token);
        await Task.WhenAny(arrived, delay).ConfigureAwait(false);
        timer.Cancel();
        ct.ThrowIfCancellationRequested();
        return Drain();
    }

    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// The <c>wait</c> tool, shared by both loops: sleeps <paramref name="seconds"/>, or less when a
    /// peer answers through <c>bot-chat</c> first — then the result carries the reply.
    /// </summary>
    public static async Task<string> WaitToolAsync(AgentLoopOptions options, int seconds, CancellationToken ct)
    {
        if (options.WaitForPeerSignals is null)
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds), ct).ConfigureAwait(false);
            return JsonSerializer.Serialize(new { ok = true, waited_seconds = seconds });
        }

        var sw = Stopwatch.StartNew();
        var signals = await options.WaitForPeerSignals(TimeSpan.FromSeconds(seconds), ct).ConfigureAwait(false);
        if (signals.Count == 0)
            return JsonSerializer.Serialize(new { ok = true, waited_seconds = seconds });

        return JsonSerializer.Serialize(new
        {
            ok = true,
            waited_seconds = (int)Math.Round(sw.Elapsed.TotalSeconds),
            peer_signals = signals.Select(s => new { from = s.PeerName, text = s.Text }),
            note = "A peer answered through bot-chat while you waited. peer_signals IS their reply — "
                 + "no read_terminal needed for it. \"handshake-ok\" / \"ready\" only confirms the channel.",
        }, Json);
    }

    /// <summary>The prompt a run starts with when peers spoke and no run was there to hear it.</summary>
    public static string ContinuationPrompt(IReadOnlyList<PeerSignal> signals)
        => string.Join("\n", signals.Select(s => $"[peer signal from {s.PeerName} via bot-chat] {s.Text}"));
}
