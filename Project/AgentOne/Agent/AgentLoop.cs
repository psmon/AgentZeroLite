using System.Diagnostics;
using AgentOne.Llm;
using AgentOne.Tools;

namespace AgentOne.Agent;

/// <summary>
/// The agent. One provider, one toolbelt, one conversation: ask the model,
/// parse its envelope, run the tool it asked for, hand the result back, repeat
/// until it calls "final" or a guard stops it.
///
/// The loop knows nothing about the CLI, so the same instance backs
/// <c>run</c> (one question) and <c>chat</c> (a REPL that keeps the history).
/// </summary>
public sealed class AgentLoop(IChatProvider provider, IToolbelt toolbelt, int maxSteps = 8)
{
    private readonly List<ChatMessage> _messages = [];

    public IReadOnlyList<ChatMessage> Messages => _messages;

    /// <summary>Raised once per turn — the CLI uses it for --verbose progress.</summary>
    public event Action<AgentStep>? StepCompleted;

    /// <summary>Starts a fresh conversation. Called once per chat session, or once per run.</summary>
    public void Reset()
    {
        _messages.Clear();
        _messages.Add(ChatMessage.System(SystemPrompt.Build(toolbelt.Scope)));
    }

    public async Task<AgentRun> RunAsync(string userPrompt, CancellationToken ct = default)
    {
        if (_messages.Count == 0) Reset();
        _messages.Add(ChatMessage.User(userPrompt));

        var guards = new AgentLoopGuards();
        var steps = new List<AgentStep>();
        var sw = Stopwatch.StartNew();

        for (int step = 1; step <= maxSteps; step++)
        {
            if (ct.IsCancellationRequested)
                return Finish(StopReason.Cancelled, "cancelled", steps, sw);

            string raw;
            try
            {
                raw = await provider.CompleteAsync(_messages, ct);
            }
            catch (OperationCanceledException)
            {
                return Finish(StopReason.Cancelled, "cancelled", steps, sw);
            }
            catch (ChatProviderException ex)
            {
                return Finish(StopReason.ProviderError, ex.Message, steps, sw);
            }

            if (!ToolCall.TryParse(raw, out var call, out var parseError))
            {
                var stepInfo = Emit(steps, step, "(unparsed)", parseError, ok: false);
                if (!guards.TryConsumeParseNudge())
                    return Finish(StopReason.ParseFailure, $"model never produced a valid envelope ({parseError})", steps, sw);

                _messages.Add(ChatMessage.Assistant(raw));
                _messages.Add(ChatMessage.User(
                    $"[error] {parseError}. Reply with ONE JSON object only, e.g. {ToolCall.Final("your answer").ToJson()}"));
                _ = stepInfo;
                continue;
            }

            if (call.IsFinal)
            {
                var answer = call.Arg("text");
                Emit(steps, step, ToolCall.FinalTool, answer, ok: true);
                _messages.Add(ChatMessage.Assistant(raw));
                return Finish(StopReason.Final, answer, steps, sw);
            }

            if (guards.IsRepeat(call))
            {
                Emit(steps, step, call.Tool, "repeated call", ok: false);
                if (!guards.TryConsumeRepeatNudge())
                    return Finish(StopReason.Repeat, $"model repeated {call.Signature()} with nothing new to add", steps, sw);

                _messages.Add(ChatMessage.Assistant(raw));
                _messages.Add(ChatMessage.User(
                    "[error] You already made that exact call and have its result. Use it, or answer with \"final\"."));
                continue;
            }

            guards.Record(call);

            ToolResult result;
            try
            {
                result = await toolbelt.InvokeAsync(call, ct);
            }
            catch (OperationCanceledException)
            {
                return Finish(StopReason.Cancelled, "cancelled", steps, sw);
            }
            catch (Exception ex)
            {
                result = ToolResult.Failure($"tool '{call.Tool}' threw: {ex.Message}");
            }

            Emit(steps, step, call.Tool, Summarize(call, result), result.Ok);

            _messages.Add(ChatMessage.Assistant(raw));
            _messages.Add(ChatMessage.User($"[tool:{call.Tool}] {result.Text}"));
        }

        return Finish(StopReason.MaxSteps, $"step budget ({maxSteps}) exhausted before the model answered", steps, sw);
    }

    private AgentStep Emit(List<AgentStep> steps, int index, string tool, string detail, bool ok)
    {
        var step = new AgentStep(index, tool, detail, ok);
        steps.Add(step);
        StepCompleted?.Invoke(step);
        return step;
    }

    private static string Summarize(ToolCall call, ToolResult result)
    {
        var args = string.Join(" ", call.Args.Select(kv => $"{kv.Key}={kv.Value}"));
        var head = args.Length > 0 ? args : "(no args)";
        return result.Ok ? $"{head} -> {result.Text.Length} chars" : $"{head} -> {result.Text}";
    }

    private static AgentRun Finish(StopReason reason, string text, List<AgentStep> steps, Stopwatch sw)
    {
        sw.Stop();
        return new AgentRun(reason, text, steps, sw.Elapsed);
    }
}
