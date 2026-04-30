using System.Runtime.CompilerServices;
using Agent.Common.Llm.Providers;

namespace Agent.Common.Llm;

/// <summary>
/// REST-backed chat session that satisfies the same <see cref="ILocalChatSession"/>
/// contract as the local LLamaSharp session. TestBot and AgentBot regular
/// chat both consume <see cref="ILocalChatSession"/>, so swapping in an
/// external provider needs no UI change beyond the active-backend selector.
///
/// History is held in-memory as a <c>messages[]</c> list; each turn replays
/// the full history (REST is stateless). The transcript is bounded by the
/// provider's context window — no client-side trimming yet.
/// </summary>
public sealed class ExternalChatSession : ILocalChatSession
{
    private readonly ILlmProvider _provider;
    private readonly string _model;
    private readonly int _maxTokens;
    private readonly float _temperature;
    private readonly List<LlmMessage> _history = [];
    private int _turnCount;
    private bool _disposed;

    public ExternalChatSession(ILlmProvider provider, string model, int maxTokens, float temperature)
    {
        _provider = provider;
        _model = model;
        _maxTokens = maxTokens;
        _temperature = temperature;
    }

    public int TurnCount => _turnCount;

    public async Task<string> SendAsync(string userMessage, CancellationToken ct = default)
    {
        var sb = new System.Text.StringBuilder();
        await foreach (var tok in SendStreamAsync(userMessage, ct))
            sb.Append(tok);
        return sb.ToString();
    }

    public async IAsyncEnumerable<string> SendStreamAsync(string userMessage,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ExternalChatSession));

        _history.Add(LlmMessage.User(userMessage));
        _turnCount++;

        var request = new LlmRequest
        {
            Model = _model,
            Messages = _history.AsReadOnly(),
            Temperature = _temperature,
            MaxTokens = _maxTokens,
        };

        var assistant = new System.Text.StringBuilder();
        await foreach (var chunk in _provider.StreamAsync(request, ct))
        {
            if (!string.IsNullOrEmpty(chunk.Text))
            {
                assistant.Append(chunk.Text);
                yield return chunk.Text;
            }
        }

        _history.Add(LlmMessage.Assistant(assistant.ToString()));
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        if (_provider is IDisposable d) d.Dispose();
        return ValueTask.CompletedTask;
    }
}
