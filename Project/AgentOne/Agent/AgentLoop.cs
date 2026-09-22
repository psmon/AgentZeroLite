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
    private string? _memory;

    public IReadOnlyList<ChatMessage> Messages => _messages;

    /// <summary>Raised once per turn — the CLI uses it for --verbose progress.</summary>
    public event Action<AgentStep>? StepCompleted;

    /// <summary>Raised when a turn begins, with what the agent is about to do.</summary>
    public event Action<string>? ActivityStarted;

    /// <summary>
    /// Raised with each fragment of the final answer as the model writes it.
    /// Only ever the answer: a tool call streams nothing (see
    /// <see cref="FinalAnswerStreamer"/>).
    /// </summary>
    public event Action<string>? AnswerDelta;

    /// <summary>Whether to ask the provider to stream. Off unless somebody is watching.</summary>
    public bool Streaming { get; set; }

    /// <summary>Starts a fresh conversation. Called once per chat session, or once per run.</summary>
    /// <param name="memory">The workspace's memory to open with; null keeps whatever the last Reset used.</param>
    public void Reset(string? memory = null)
    {
        if (memory is not null) _memory = memory;
        _messages.Clear();
        _messages.Add(ChatMessage.System(SystemPrompt.Build(toolbelt.Scope, _memory)));
    }

    /// <summary>
    /// Puts an earlier exchange back into the conversation, for a resumed
    /// session: the question and the answer, not the tool traffic between them
    /// — the answer is what the model needs to continue from, and the memory
    /// says what was done.
    /// </summary>
    public void Restore(string prompt, string answer)
    {
        if (_messages.Count == 0) Reset();
        _messages.Add(ChatMessage.User(prompt));
        _messages.Add(ChatMessage.Assistant(ToolCall.Final(answer).ToJson()));
    }

    /// <param name="families">
    /// The tool families this turn may use, or null for all of them. A call
    /// outside the set is answered with a refusal instead of being run — smart
    /// mode's route is enforced here, not merely suggested in the prompt, because
    /// a small model takes a suggestion as one option among many.
    /// </param>
    public async Task<AgentRun> RunAsync(string userPrompt, CancellationToken ct = default, IReadOnlySet<string>? families = null)
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

            ActivityStarted?.Invoke(step == 1 ? "thinking" : "thinking about what came back");
            var stepClock = Stopwatch.StartNew();

            string raw;
            var streamer = new FinalAnswerStreamer();

            try
            {
                Action<string>? onDelta = null;
                if (Streaming && AnswerDelta is not null)
                {
                    onDelta = fragment =>
                    {
                        var visible = streamer.Push(fragment);
                        if (visible.Length > 0) AnswerDelta?.Invoke(visible);
                    };
                }

                raw = await provider.CompleteAsync(_messages, ct, onDelta);
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
                // A reply with no envelope is usually the answer, not a mistake:
                // a model that has just read 18 KB and written a page of
                // markdown drops the JSON wrapper more often than not, and two
                // rounds of "reply with ONE JSON object" cost a minute for the
                // same text. So substantive prose is taken as final, and only a
                // short, ambiguous reply gets the nudge.
                // A final envelope that is not valid JSON — raw newlines inside the
                // string, an unescaped quote — is still a final envelope. Taking the
                // whole thing as prose would print the braces to the user, and
                // print the answer twice (the stream had already decoded the text
                // field). The lenient decoder the stream uses gets it out.
                if (TryDecodeBrokenFinal(raw, out var decoded))
                {
                    Emit(steps, step, "unwrapped",
                        $"final envelope was not valid JSON; decoded its text ({decoded.Length} chars)",
                        ok: true, stepClock.ElapsedMilliseconds);
                    Emit(steps, step, ToolCall.FinalTool, decoded, ok: true);
                    _messages.Add(ChatMessage.Assistant(raw));
                    return Finish(StopReason.Final, decoded, steps, sw, streamer.Visible);
                }

                if (LooksLikeAnAnswer(raw, steps))
                {
                    var prose = raw.Trim();
                    Emit(steps, step, "unwrapped",
                        $"prose accepted as the answer ({prose.Length} chars, after {ToolStepsSoFar(steps)} tool steps)",
                        ok: true, stepClock.ElapsedMilliseconds);
                    Emit(steps, step, ToolCall.FinalTool, prose, ok: true);
                    _messages.Add(ChatMessage.Assistant(raw));
                    return Finish(StopReason.Final, prose, steps, sw, streamer.Visible);
                }

                Emit(steps, step, "(unparsed)", parseError, ok: false, stepClock.ElapsedMilliseconds);
                if (!guards.TryConsumeParseNudge())
                    return Finish(StopReason.ParseFailure, $"model never produced a valid envelope ({parseError})", steps, sw);

                _messages.Add(ChatMessage.Assistant(raw));
                _messages.Add(ChatMessage.User(
                    $"[error] {parseError}. Reply with ONE JSON object only, e.g. {ToolCall.Final("your answer").ToJson()}"));
                continue;
            }

            if (call.IsFinal)
            {
                var answer = call.Arg("text");
                Emit(steps, step, ToolCall.FinalTool, answer, ok: true, stepClock.ElapsedMilliseconds);
                _messages.Add(ChatMessage.Assistant(raw));

                // The parsed answer is the truth; the stream was a preview. If the
                // two disagree — a provider that did not stream, a truncated
                // envelope — the caller is told what was already shown so it can
                // print only the remainder rather than the whole thing twice.
                return Finish(StopReason.Final, answer, steps, sw, streamer.Visible);
            }

            if (guards.IsRepeat(call))
            {
                Emit(steps, step, call.Tool, "repeated call", ok: false, stepClock.ElapsedMilliseconds);
                if (!guards.TryConsumeRepeatNudge())
                    return Finish(StopReason.Repeat, $"model repeated {call.Signature()} with nothing new to add", steps, sw);

                _messages.Add(ChatMessage.Assistant(raw));
                // Measured: a command failed, the model changed an unrelated
                // file, repeated the command, was told "use the result", and
                // then reported success. The nudge has to say what the result was.
                var earlier = steps.LastOrDefault(s => s.Tool == call.Tool && s.Detail != "repeated call");
                _messages.Add(ChatMessage.User(earlier is { Ok: false }
                    ? "[error] You already made that exact call and it FAILED: " + Services.WorkspaceStore.FirstLine(earlier.Detail, 300)
                      + "\nIt did not work and repeating it will not change that. Do something different, or tell the user"
                      + " plainly in \"final\" that it failed and why. Never report it as done."
                    : "[error] You already made that exact call and have its result. Use it, or answer with \"final\"."));
                continue;
            }

            guards.Record(call);

            ActivityStarted?.Invoke(Describe(call));

            ToolResult result;
            if (families is not null && ToolCatalog.FamilyOf(call.Tool) is { } family && !families.Contains(family))
            {
                result = ToolResult.Failure(
                    $"'{call.Tool}' is not available on this turn ({family} tools were ruled out for it). " +
                    "Answer with what you have, or use a tool that is allowed.");
            }
            else
            {
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
            }

            Emit(steps, step, call.Tool, Summarize(call, result), result.Ok, stepClock.ElapsedMilliseconds);

            _messages.Add(ChatMessage.Assistant(raw));
            _messages.Add(ChatMessage.User($"[tool:{call.Tool}] {result.Text}"));
        }

        return Finish(StopReason.MaxSteps, $"step budget ({maxSteps}) exhausted before the model answered", steps, sw);
    }

    private AgentStep Emit(List<AgentStep> steps, int index, string tool, string detail, bool ok, long elapsedMs = 0)
    {
        var step = new AgentStep(index, tool, detail, ok, elapsedMs);
        steps.Add(step);
        StepCompleted?.Invoke(step);
        return step;
    }

    /// <summary>
    /// A reply shaped like a final envelope that the strict parser refused: the
    /// text is pulled out with the same lenient decoder the stream uses, which
    /// tolerates raw newlines and stray quotes inside the string. False for
    /// anything that is not a final envelope, so a broken tool call still gets
    /// the parse nudge rather than being shown as an answer.
    /// </summary>
    internal static bool TryDecodeBrokenFinal(string raw, out string text)
    {
        text = "";
        var trimmed = raw.Trim();
        if (!trimmed.StartsWith('{') || !trimmed.Contains($"\"{ToolCall.FinalTool}\"", StringComparison.Ordinal)) return false;

        var decoder = new FinalAnswerStreamer();
        decoder.Push(trimmed);
        text = decoder.Visible.Trim();
        return text.Length > 0;
    }

    /// <summary>Real tool calls so far — nudges, parse failures and the final do not count.</summary>
    private static int ToolStepsSoFar(List<AgentStep> steps) =>
        steps.Count(s => s.Ok && s.Tool is not ("(unparsed)" or "unwrapped") && s.Tool != ToolCall.FinalTool);

    /// <summary>Below this a bare reply is too short to trust as an answer.</summary>
    public const int ProseAnswerMinChars = 120;

    /// <summary>
    /// Whether an envelope-less reply should be taken as the answer. Once a tool
    /// has run, the model has done its work and prose is what it has to say.
    /// Before that, only a reply long enough to be an answer rather than an
    /// announcement ("Let me search for that") qualifies.
    /// </summary>
    internal static bool LooksLikeAnAnswer(string raw, List<AgentStep> steps)
    {
        var text = raw.Trim();
        if (text.Length == 0) return false;

        // A broken tool call is a broken tool call, never an answer: measured,
        // a write_file whose content had raw newlines was shown to the user as
        // "the answer" — braces, code and all — and the file was never written.
        if (ToolCall.LooksLikeEnvelope(text)) return false;
        if (ToolStepsSoFar(steps) > 0) return true;
        return text.Length >= ProseAnswerMinChars;
    }

    private static string Summarize(ToolCall call, ToolResult result)
    {
        var args = string.Join(" ", call.Args.Select(kv => $"{kv.Key}={kv.Value}"));
        var head = args.Length > 0 ? args : "(no args)";
        return result.Ok ? $"{head} -> {result.Text.Length} chars" : $"{head} -> {result.Text}";
    }

    /// <summary>A tool call in words, for the progress line.</summary>
    private static string Describe(ToolCall call) => call.Tool.ToLowerInvariant() switch
    {
        "web_search" => $"searching the web for \"{call.Arg("query")}\"",
        "web_read" => $"reading {call.Arg("url")}",
        "read_file" => $"reading {call.Arg("path")}",
        "list_files" => $"listing {call.Arg("path", ".")}",
        "find_files" => $"finding {call.Arg("pattern")}",
        "grep" => $"searching files for \"{call.Arg("text")}\"",
        _ => call.Tool
    };

    private static AgentRun Finish(StopReason reason, string text, List<AgentStep> steps, Stopwatch sw, string streamed = "")
    {
        sw.Stop();
        return new AgentRun(reason, text, steps, sw.Elapsed, streamed);
    }
}
