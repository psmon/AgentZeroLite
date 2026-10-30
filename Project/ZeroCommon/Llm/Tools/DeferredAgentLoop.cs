namespace Agent.Common.Llm.Tools;

/// <summary>
/// An <see cref="IAgentLoop"/> whose real loop does not exist yet — the local model loads on the first
/// <see cref="RunAsync"/> (on the pool, where the loop actor runs every turn), so AI mode works without pressing
/// Settings → LLM → Load first and nothing loads before it is used. The factory returning null means the backend
/// is not ready after all; the run then fails with <paramref name="notReady"/>.
/// </summary>
public sealed class DeferredAgentLoop(Func<CancellationToken, Task<IAgentLoop?>> create, string notReady) : IAgentLoop
{
    private IAgentLoop? _inner;

    public int UserSendCount => _inner?.UserSendCount ?? 0;

    public async Task<AgentLoopRun> RunAsync(string userRequest, CancellationToken ct = default)
    {
        _inner ??= await create(ct).ConfigureAwait(false) ?? throw new InvalidOperationException(notReady);
        return await _inner.RunAsync(userRequest, ct).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => _inner?.DisposeAsync() ?? ValueTask.CompletedTask;
}
