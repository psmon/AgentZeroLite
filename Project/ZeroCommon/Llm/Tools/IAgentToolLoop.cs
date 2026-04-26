namespace Agent.Common.Llm.Tools;

/// <summary>
/// Backend-agnostic AIMODE tool loop. Two implementations:
/// <list type="bullet">
///   <item><description><see cref="AgentToolLoop"/> — local LLamaSharp + GBNF (Gemma 4 standard)</description></item>
///   <item><description><see cref="ExternalAgentToolLoop"/> — OpenAI-compatible REST, no grammar enforcement (best-effort Gemma 4 over the wire)</description></item>
/// </list>
/// AgentReactorActor holds whichever <see cref="ReactorBindings.ToolLoopFactory"/> returns.
/// </summary>
public interface IAgentToolLoop : IAsyncDisposable
{
    int UserSendCount { get; }

    Task<AgentToolSession> RunAsync(string userRequest, CancellationToken ct = default);
}
