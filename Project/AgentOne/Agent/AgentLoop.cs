using System.Diagnostics;
using System.Text.RegularExpressions;
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
public sealed partial class AgentLoop(IChatProvider provider, IToolbelt toolbelt, int maxSteps = 8)
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

    /// <summary>
    /// The workspace root, for checking that a file the answer names is really
    /// there. Null skips the disk check, and only files written in this turn
    /// count as real — which would call a file from an earlier turn a lie.
    /// </summary>
    public string? Root { get; set; }

    /// <summary>Starts a fresh conversation. Called once per chat session, or once per run.</summary>
    /// <param name="memory">The workspace's memory to open with; null keeps whatever the last Reset used.</param>
    /// <summary>The pause between steps: the person holds the loop here, and may hand it a refinement on resume.</summary>
    public PauseGate Pause { get; } = new();

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
    /// <param name="familiesAreFinal">
    /// True when the set is a rule rather than a steer, and the model asking
    /// twice must not open it. The route is a 0.3 s guess and gives way; the
    /// no-tools pass behind a wrap-up or an escalation is a decision this code
    /// made, with the material already gathered, and it stands. Measured: the
    /// override leaked into a wrap-up, the model started hunting for files
    /// again instead of summarising, and the turn ended on a repeat guard with
    /// no summary for the person.
    /// </param>
    public async Task<AgentRun> RunAsync(string userPrompt, CancellationToken ct = default,
        IReadOnlySet<string>? families = null, bool familiesAreFinal = false)
    {
        if (_messages.Count == 0) Reset();
        _messages.Add(ChatMessage.User(userPrompt));

        var guards = new AgentLoopGuards();
        var steps = new List<AgentStep>();
        var sw = Stopwatch.StartNew();

        // The route steers this turn, but it can be overruled from inside the
        // turn (see AgentLoopGuards.RefuseFamily), so it is a local, not the
        // caller's set.
        var allowed = families;

        // Paths this turn actually put on disk, to check the answer against.
        var wrote = new List<string>();

        for (int step = 1; step <= maxSteps; step++)
        {
            if (ct.IsCancellationRequested)
                return Finish(StopReason.Cancelled, "cancelled", steps, sw);

            // The person may have paused; the step waits here, and what they
            // said meanwhile goes in front of the model before it thinks again.
            if (Pause.IsPaused)
            {
                ActivityStarted?.Invoke("paused");
                try { await Pause.WaitAsync(ct); }
                catch (OperationCanceledException) { return Finish(StopReason.Cancelled, "cancelled", steps, sw); }
            }
            if (Pause.TakeRefinement() is { } refinement)
                _messages.Add(ChatMessage.User("[the user, mid-turn] " + refinement));

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

                // Two shapes of the same lie, both measured on 2026-09-24.
                //
                // "세 개의 파일을 생성하고 내용을 채웠습니다" with an empty folder: every
                // write_file had been refused, and the code lived only in that
                // sentence. That is code in the answer with nothing written.
                //
                // Then, after the route was fixed: index.html really was
                // written, and the answer listed four files as done —
                // css/style.css, js/tetris.js, js/main.js were never created,
                // and the page loaded with three ERR_FILE_NOT_FOUND. One
                // successful write had disarmed the first check. So the
                // stronger question is not "did you write anything" but
                // "is every file you just named actually there".
                // ...but only for a turn that actually did file work. A plan
                // names the files it is proposing, and none of them exist yet;
                // that is what a plan is, not a false claim. A turn that wrote
                // or tried to write is reporting, and a report is checkable.
                var missing = DidAnyWork(steps) ? MissingFilesNamed(answer, wrote) : [];
                if ((missing.Count > 0 || ShowsUnwrittenCode(answer, steps)) && guards.TryConsumeUnwrittenNudge(wrote.Count))
                {
                    var why = missing.Count > 0
                        ? "names " + missing.Count + " file(s) that are not on disk: " + string.Join(", ", missing)
                        : $"carries {FencedChars(answer)} chars of code but no file was written this turn";
                    Emit(steps, step, "unwritten", "the answer " + why, ok: false, stepClock.ElapsedMilliseconds);

                    _messages.Add(ChatMessage.Assistant(raw));
                    _messages.Add(ChatMessage.User(missing.Count > 0
                        ? "[error] Your answer says these files are done, but they do not exist: "
                          + string.Join(", ", missing)
                          + ". The user cannot run any of it — loading the page gives file-not-found for each one. "
                          + "Write every missing file now with write_file, one call per file, the WHOLE file as the "
                          + "content, and do not run or test anything until they all exist. Never say you created a "
                          + "file you did not write. If you only meant them as next steps for the user to do later, "
                          + "say that plainly and answer again."
                        : "[error] Your answer contains code, but you have not written a single file this turn, so "
                          + "nothing exists on disk and the user cannot run any of it. Never say you created or "
                          + "updated a file you did not write. Write each file now with write_file (one call per "
                          + "file, the WHOLE file as the content), then answer. If the user only wanted to SEE the "
                          + "code and not have it saved, reply with \"final\" again and say so plainly."));
                    continue;
                }

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
            if (allowed is not null && ToolCatalog.FamilyOf(call.Tool) is { } family && !allowed.Contains(family))
            {
                if (!familiesAreFinal && guards.RefuseFamily())
                {
                    // The model has asked twice for a family the route ruled
                    // out. Believe the model: run it, and let every tool back in
                    // for the rest of the turn.
                    allowed = null;
                    Emit(steps, step, "route-overruled",
                        $"'{call.Tool}' was ruled out, but the model asked for {family} tools twice — the route was wrong, letting them through",
                        ok: true);

                    try { result = await toolbelt.InvokeAsync(call, ct); }
                    catch (OperationCanceledException) { return Finish(StopReason.Cancelled, "cancelled", steps, sw); }
                    catch (Exception ex) { result = ToolResult.Failure($"tool '{call.Tool}' threw: {ex.Message}"); }
                }
                else if (familiesAreFinal)
                {
                    result = ToolResult.Failure(
                        $"'{call.Tool}' cannot be used on this turn. You already have everything you need; " +
                        "answer with \"final\".");
                }
                else
                {
                    // It never ran, so asking again is insistence, not repetition.
                    guards.Forget(call);
                    result = ToolResult.Failure(
                        $"'{call.Tool}' is not available on this turn ({family} tools were ruled out for it). " +
                        "Answer with what you have, or use a tool that is allowed. " +
                        "If you truly need it — the user asked for something to be created or changed on disk — " +
                        "ask for it once more and it will be allowed.");
                }
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

            if (result.Ok && call.Tool == "write_file" && call.Arg("path") is { Length: > 0 } written)
                wrote.Add(Normalize(written));

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

    /// <summary>
    /// Files the answer presents as done that are not on disk. The check is
    /// language-neutral on purpose: the claim ("생성했습니다", "created", "done")
    /// is written in whatever language the user speaks, but "css/style.css" is
    /// the same token in every one of them.
    ///
    /// A path is forgiven when this turn wrote it, or when it is already there
    /// — the model may fairly mention a file it read or made earlier. What is
    /// left is a file named in a finished report that nobody ever created.
    /// </summary>
    /// <param name="wrote">Paths written in this turn, workspace-relative.</param>
    internal IReadOnlyList<string> MissingFilesNamed(string answer, IReadOnlyCollection<string> wrote)
    {
        var missing = new List<string>();
        foreach (var path in FilePathsIn(answer))
        {
            if (wrote.Contains(path)) continue;
            if (Root is { Length: > 0 } root && File.Exists(Path.Combine(root, path))) continue;
            if (Root is null) continue;   // no root, no way to tell an old file from a fictional one
            if (!missing.Contains(path)) missing.Add(path);
        }
        return missing;
    }

    /// <summary>Workspace-relative file paths mentioned in text: a name with a known source or asset extension.</summary>
    internal static IReadOnlyList<string> FilePathsIn(string text) =>
        FileLike().Matches(text)
            .Select(m => Normalize(m.Value))
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string Normalize(string path) =>
        path.Trim().Trim('`', '"', '\'', '(', ')', ',', ';', ':', '.').Replace('\\', '/').TrimStart('.', '/');

    [GeneratedRegex(@"(?:[\w.\-]+/)*[\w.\-]+\.(?:html?|css|js|mjs|cjs|jsx|ts|tsx|json|py|cs|java|kt|go|rs|rb|php|c|cpp|h|hpp|md|txt|ya?ml|toml|sql|sh|ps1|bat)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex FileLike();

    /// <summary>
    /// Whether this turn did any work at all. Only then is a list of files in
    /// the answer a <em>report</em> that can be checked against the disk;
    /// a turn that called no tool is proposing, and a plan naming files that do
    /// not exist yet is exactly what a plan is.
    ///
    /// Writing is not the bar, though it was at first. Measured: a turn ran
    /// <c>Get-ChildItem -Recurse</c>, got back a listing with one file in it,
    /// and answered "이전 턴에서 모든 구성 요소가 성공적으로 작성 및 저장되었습니다" — four
    /// files, one of them a README — adding that it had "confirmed them with a
    /// file system listing". It wrote nothing that turn, so the check was
    /// skipped and the fabrication went out with the evidence in its hand.
    /// </summary>
    private static bool DidAnyWork(List<AgentStep> steps) =>
        steps.Any(s => s.Tool is not (ToolCall.FinalTool or "unwrapped" or "(unparsed)" or "unwritten" or "route-overruled"));

    /// <summary>Fenced code in an answer below this many characters is an illustration, not a deliverable.</summary>
    public const int UnwrittenCodeMinChars = 400;

    /// <summary>
    /// Whether a final answer is showing code it never put on disk: a
    /// substantial fenced block, and not one successful write this turn.
    /// Deliberately language-neutral — the claim itself ("created three files")
    /// is written in whatever language the user speaks, but a code fence is not.
    /// </summary>
    internal static bool ShowsUnwrittenCode(string answer, List<AgentStep> steps)
    {
        if (steps.Any(s => s.Ok && s.Tool == "write_file")) return false;
        return FencedChars(answer) >= UnwrittenCodeMinChars;
    }

    /// <summary>Characters inside ``` fences, summed. Zero when the fences are unbalanced.</summary>
    internal static int FencedChars(string text)
    {
        var total = 0;
        var from = text.IndexOf("```", StringComparison.Ordinal);
        while (from >= 0)
        {
            var bodyStart = text.IndexOf('\n', from);
            if (bodyStart < 0) break;
            var close = text.IndexOf("```", bodyStart, StringComparison.Ordinal);
            if (close < 0) break;
            total += close - bodyStart - 1;
            from = text.IndexOf("```", close + 3, StringComparison.Ordinal);
        }
        return total;
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
