namespace Agent.Common.Llm.Tools;

/// <summary>
/// Backend-agnostic AIMODE agent loop. Two implementations:
/// <list type="bullet">
///   <item><description><see cref="LocalAgentLoop"/> — local LLamaSharp + GBNF (Gemma 4 standard)</description></item>
///   <item><description><see cref="ExternalAgentLoop"/> — OpenAI-compatible REST, no grammar enforcement (best-effort Gemma 4 over the wire)</description></item>
/// </list>
/// <see cref="Agent.Common.Actors.AgentLoopActor"/> holds whichever
/// <c>AgentLoopBindings.AgentLoopFactory</c> returns.
/// </summary>
public interface IAgentLoop : IAsyncDisposable
{
    int UserSendCount { get; }

    Task<AgentLoopRun> RunAsync(string userRequest, CancellationToken ct = default);
}
