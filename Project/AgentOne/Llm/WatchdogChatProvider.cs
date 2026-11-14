namespace AgentOne.Llm;

/// <summary>
/// A hard wall-clock ceiling on one model call, wrapped around any
/// <see cref="IChatProvider"/>. The streaming provider already has an <em>idle</em>
/// clock, but that only fires on silence: a server that keeps the socket warm with
/// keep-alive frames while the model produces nothing, or a call whose first token
/// never comes while the connection stays open, can sit forever. Measured on the
/// escalation→final path: the everyday model's final call hung with no way out.
///
/// Past the deadline this cancels the inner call and throws
/// <see cref="ChatProviderStalledException"/> — the very type
/// <see cref="Agent.AgentLoop.OnStall"/> and the escalation already catch and retry
/// or end on — so a single stuck call can never hang the loop. The caller's own
/// cancellation is passed through untouched (it is a cancel, not a stall).
///
/// Zero or less means "no ceiling", and <see cref="Wrap"/> then returns the inner
/// provider unchanged, so the default configuration behaves exactly as before.
/// </summary>
internal sealed class WatchdogChatProvider(IChatProvider inner, TimeSpan deadline) : IChatProvider, IDisposable
{
    public string Name => inner.Name;

    /// <summary>The inner provider, or a watchdog around it when <paramref name="seconds"/> &gt; 0.</summary>
    public static IChatProvider? Wrap(IChatProvider? inner, int seconds) =>
        inner is null || seconds <= 0 ? inner : new WatchdogChatProvider(inner, TimeSpan.FromSeconds(seconds));

    public async Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct, Action<string>? onDelta = null)
    {
        using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadlineCts.CancelAfter(deadline);

        try
        {
            return await inner.CompleteAsync(messages, deadlineCts.Token, onDelta);
        }
        // The deadline fired, not the caller: report it as a stall the loop can recover from.
        catch (OperationCanceledException) when (deadlineCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new ChatProviderStalledException(
                $"{inner.Name} did not finish within {deadline.TotalSeconds:0}s (call watchdog)");
        }
        // The inner provider turned our deadline into its own stall — relabel it, keep the count.
        catch (ChatProviderStalledException ex) when (deadlineCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new ChatProviderStalledException(
                $"{inner.Name} did not finish within {deadline.TotalSeconds:0}s (call watchdog)", ex.ReceivedChars);
        }
    }

    public void Dispose() => (inner as IDisposable)?.Dispose();
}
