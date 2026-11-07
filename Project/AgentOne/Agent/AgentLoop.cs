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

    /// <summary>
    /// The person's own words for this turn — the language the answer must be
    /// in. The prompt a run gets is often not them: route guidance, a [check]
    /// or a [wrap-up] is English written by us. Null: the run's own prompt.
    /// </summary>
    public string? Request { get; set; }

    /// <summary>
    /// Asked when the model went quiet mid-step, with the stall and how many
    /// times this step has already been retried. True sends the same messages
    /// again — the step restarts from just before it stalled, nothing earlier
    /// is lost; false ends the turn as a provider error. Null never retries.
    /// </summary>
    public Func<ChatProviderStalledException, int, CancellationToken, Task<bool>>? OnStall { get; set; }

    /// <summary>
    /// True while the run carries out a design: a long job whose plan is in the
    /// conversation. Measured (2026-11-06, music-harness): a grounded design for
    /// "종말의 그날 1·2 뮤비" — read the rules, take stock, then write lore, lyrics,
    /// the MV guide and a log — got through nine good steps, then the model asked
    /// for the todo it had just read, twice. One repeat nudge per run, worded
    /// "use it, or answer with final": the turn ended on Repeat at step 12 of 50
    /// with nothing written. A build gets more nudges, and they name the next step.
    /// </summary>
    public bool Building { get; set; }

    /// <summary>Repeat nudges a design-carrying run gets before a repeat ends it.</summary>
    public const int BuildingRepeatNudges = 3;

    /// <summary>The workspace's own instructions for agents, put in every system prompt from the next Reset on.</summary>
    public WorkspaceGuideText? Guide { get; set; }

    public void Reset(string? memory = null)
    {
        if (memory is not null) _memory = memory;
        _messages.Clear();
        _messages.Add(ChatMessage.System(SystemPrompt.Build(toolbelt.Scope, _memory, Guide)));
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

        var guards = new AgentLoopGuards(repeatNudgeBudget: Building ? BuildingRepeatNudges : 1);
        var steps = new List<AgentStep>();
        var sw = Stopwatch.StartNew();

        // The route steers this turn, but it can be overruled from inside the
        // turn (see AgentLoopGuards.RefuseFamily), so it is a local, not the
        // caller's set.
        var allowed = families;

        // Paths this turn actually put on disk, to check the answer against.
        var wrote = new List<string>();
        // Paths it read, listed or ran without writing — named back by basename just as often.
        var touched = new List<string>();
        // Commands by shape (quoted values blanked), to see values being guessed.
        var tried = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var languageNudged = false;

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
            FinalAnswerStreamer streamer;

            for (var retries = 0; ; retries++)
            {
                // A fresh streamer per attempt: what a stalled attempt showed is
                // not part of the reply the retry will write.
                streamer = new FinalAnswerStreamer();
                try
                {
                    Action<string>? onDelta = null;
                    if (Streaming && AnswerDelta is not null)
                    {
                        var current = streamer;
                        onDelta = fragment =>
                        {
                            var visible = current.Push(fragment);
                            if (visible.Length > 0) AnswerDelta?.Invoke(visible);
                        };
                    }

                    raw = await provider.CompleteAsync(_messages, ct, onDelta);
                    break;
                }
                catch (OperationCanceledException)
                {
                    return Finish(StopReason.Cancelled, "cancelled", steps, sw);
                }
                catch (ChatProviderStalledException ex)
                {
                    bool again;
                    try { again = OnStall is not null && await OnStall(ex, retries, ct); }
                    catch (OperationCanceledException) { return Finish(StopReason.Cancelled, "cancelled", steps, sw); }

                    Emit(steps, step, "stalled", again ? $"{ex.Message} — retrying the step" : ex.Message,
                        ok: false, stepClock.ElapsedMilliseconds);
                    if (!again) return Finish(StopReason.ProviderError, ex.Message, steps, sw);

                    ActivityStarted?.Invoke($"retrying after a stall ({retries + 1})");
                    stepClock.Restart();
                }
                catch (ChatProviderException ex)
                {
                    return Finish(StopReason.ProviderError, ex.Message, steps, sw);
                }
                catch (Exception ex)
                {
                    // Whatever a provider lets through, the turn still ends as a
                    // turn: with its steps, so the result is logged, memory says
                    // what was done, and a resume knows where it stopped.
                    // Measured (2026-11-06): an IOException from a dropped stream
                    // escaped here and the turn vanished after writing a file.
                    Emit(steps, step, "provider-error", $"{ex.GetType().Name}: {ex.Message}", ok: false, stepClock.ElapsedMilliseconds);
                    return Finish(StopReason.ProviderError, $"the model call failed: {ex.Message}", steps, sw);
                }
            }

            string? unwrapped = null;
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
                //
                // Either way it is an answer, and it goes through the same checks
                // as a clean final below. It used to return from here directly,
                // so a small model that dropped its JSON skipped every check.
                if (TryDecodeBrokenFinal(raw, out var decoded))
                {
                    Emit(steps, step, "unwrapped",
                        $"final envelope was not valid JSON; decoded its text ({decoded.Length} chars)",
                        ok: true, stepClock.ElapsedMilliseconds);
                    unwrapped = decoded;
                }
                else if (LooksLikeAnAnswer(raw, steps))
                {
                    unwrapped = raw.Trim();
                    Emit(steps, step, "unwrapped",
                        $"prose accepted as the answer ({unwrapped.Length} chars, after {ToolStepsSoFar(steps)} tool steps)",
                        ok: true, stepClock.ElapsedMilliseconds);
                }
                else
                {
                    Emit(steps, step, "(unparsed)", parseError, ok: false, stepClock.ElapsedMilliseconds);
                    if (!guards.TryConsumeParseNudge())
                        return Finish(StopReason.ParseFailure, $"model never produced a valid envelope ({parseError})", steps, sw);

                    _messages.Add(ChatMessage.Assistant(raw));
                    _messages.Add(ChatMessage.User(
                        $"[error] {parseError}. Reply with ONE JSON object only, e.g. {ToolCall.Final("your answer").ToJson()}"));
                    continue;
                }
            }

            if (unwrapped is not null || call!.IsFinal)
            {
                var answer = unwrapped ?? call!.Arg("text");

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
                //
                // A turn that called no tool at all stays exempt here: a plan and
                // a false report look the same to a path matcher. Measured
                // (2026-09-29), that is where a lie got through — "created
                // WebAutomationTests.csproj and ran the tests" with no tool
                // called — so smart mode asks the decision engine, which reads
                // the claim itself (ChatSession.CheckClaimsAsync).
                var worked = DidAnyWork(steps);
                var missing = worked ? MissingFilesNamed(answer, wrote, touched) : [];
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

                // Asked in Korean, answered in English — measured (2026-09-30)
                // with the rule already in the prompt. Scripts are compared, not
                // languages: a Hangul request whose answer has no Hangul at all
                // is wrong whatever the words. Once per run; code and paths in
                // an answer are Latin in every language, so only a script the
                // answer lacks entirely counts.
                if (!languageNudged && WrongScript(Request ?? userPrompt, answer) is { } language)
                {
                    languageNudged = true;
                    Emit(steps, step, "language", $"the user wrote in {language}; the answer is not", ok: false, stepClock.ElapsedMilliseconds);
                    _messages.Add(ChatMessage.Assistant(raw));
                    _messages.Add(ChatMessage.User(
                        $"[check] The user wrote in {language}, so the answer must be in {language}. Reply with the same " +
                        $"\"final\" answer written in {language} — keep every fact, file name and command exactly as it is."));
                    continue;
                }

                // The nudges are spent and files the answer names are still not
                // there. The answer goes out — but no longer clean: the person
                // sees which files are missing. It is a fact, not a verdict: a
                // path matcher cannot tell "created hello.py" from "runs
                // hello.py", so whether the report is false is left to the
                // claim check, which reads the words (ChatSession.CheckClaimsAsync).
                var stillMissing = worked ? MissingFilesNamed(answer, wrote, touched) : [];
                if (stillMissing.Count > 0)
                {
                    var note = "not on disk: " + string.Join(", ", stillMissing);
                    Emit(steps, step, "missing", note, ok: false);
                    answer += "\n\n⚠ " + note;
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
                    : Building
                        ? "[error] You already made that exact call — its result is above in this conversation, and asking " +
                          "again returns nothing new. Do not read it again. Carry on with the design: take its next step that " +
                          "is not done yet — write that step's file with write_file, or run its command — and answer with " +
                          "\"final\" only when the design's files exist."
                        : "[error] You already made that exact call and have its result. Use it, or answer with \"final\"."));
                continue;
            }

            guards.Record(call);

            ActivityStarted?.Invoke(Describe(call));

            ToolResult result;
            if (allowed is not null && ToolCatalog.FamilyOf(call.Tool) is { } family && !allowed.Contains(family)
                && !(ToolCatalog.IsRouteExempt(family) && !familiesAreFinal))
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
            else if (result.Ok)
                NoteTouched(call, touched);

            Emit(steps, step, call.Tool, Summarize(call, result), result.Ok, stepClock.ElapsedMilliseconds);

            var guessNote = call.Tool == "run_command" && call.Arg("command") is { Length: > 0 } ran
                ? GuessNote(ran, CameBackEmpty(result), tried) : null;

            _messages.Add(ChatMessage.Assistant(raw));
            _messages.Add(ChatMessage.User($"[tool:{call.Tool}] {result.Text}" + (guessNote is null ? "" : "\n\n" + guessNote)));
        }

        return Finish(StopReason.MaxSteps, $"step budget ({maxSteps}) exhausted before the model answered", steps, sw);
    }

    /// <summary>
    /// A note when a command has now been run with two or more different quoted
    /// values in the same place — values being tried, not looked up. Measured
    /// (2026-11-07): dv-fetch.cjs --match "Apocalypse Day", then "Apocalypse",
    /// "apocalypse-day", "C2" — one approval each, every one matching nothing —
    /// while the song's own lyrics.md carried the title the site had used.
    /// </summary>
    internal static string? GuessNote(string command, bool empty, Dictionary<string, List<string>> tried)
    {
        // Only tries that found nothing count: the same script run over take 1,
        // take 2, take 3 is work, not guessing.
        if (!empty) return null;
        var quoted = QuotedValue().Replace(command, "\"…\"");
        if (quoted == command) return null;
        // The same script reached by another path is the same command:
        // "node dv-fetch.cjs …" failed, then "node stage-unity/…/dv-fetch.cjs …".
        var shape = ScriptPath().Replace(quoted, m => Path.GetFileName(m.Value));
        // Distinct values, not distinct commands: the same value at a corrected
        // path is a retry of the path, not a new guess.
        var value = string.Join(" ", QuotedValue().Matches(command).Select(m => m.Value));
        if (!tried.TryGetValue(shape, out var values)) tried[shape] = values = [];
        if (!values.Contains(value, StringComparer.Ordinal)) values.Add(value);
        if (values.Count < 2) return null;

        return $"[note] This is try {values.Count} of the same command with different quoted values ({string.Join(" · ", values)}). " +
               "Trying values is guessing. Before running it again, look the value up: read the script's header to see " +
               "what the option matches, then grep this work's own files (its folder, its lyrics, guide, notes and logs) " +
               "for the name, title or id that was actually used, and run it once with that.";
    }

    /// <summary>A result that failed, was declined, or reports an error or an empty find.</summary>
    internal static bool CameBackEmpty(ToolResult result) => !result.Ok || EmptyFind().IsMatch(result.Text);

    [System.Text.RegularExpressions.GeneratedRegex(@"\b(?:ERR|error|no match\w*|not found|nothing|no such|no results?|0 results)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex EmptyFind();

    [System.Text.RegularExpressions.GeneratedRegex(@"[\w.\-/\\]+\.(?:cjs|mjs|js|py|sh|ps1)\b")]
    private static partial System.Text.RegularExpressions.Regex ScriptPath();

    [System.Text.RegularExpressions.GeneratedRegex("\"[^\"]*\"|'[^']*'")]
    private static partial System.Text.RegularExpressions.Regex QuotedValue();

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
    /// <param name="touched">Paths this turn read, listed or ran (workspace-relative); forgiven when they exist.</param>
    internal IReadOnlyList<string> MissingFilesNamed(string answer, IReadOnlyCollection<string> wrote,
        IReadOnlyCollection<string>? touched = null)
    {
        var missing = new List<string>();
        foreach (var path in FilePathsIn(answer))
        {
            if (WasWrittenThisTurn(path, wrote)) continue;
            if (Root is null) continue;   // no root, no way to tell an old file from a fictional one
            if (File.Exists(Path.Combine(Root, path))) continue;
            if (ExistsBesideAWrite(Root, path, wrote)) continue;
            if (touched is not null && WasTouchedAndExists(Root, path, touched)) continue;
            // A bare name ("plan.md") that is a file somewhere in the workspace.
            // Measured (2026-11-07): an answer mentioned plan.md and
            // asset_prompts.md (both under songs/x/mv/), was told they were not on
            // disk, and the model rewrote one of them. A bare name cannot say which
            // file it means; it is not evidence of a file that was never made.
            if (!path.Contains('/') && ExistsAnywhere(Root, path)) continue;
            if (!missing.Contains(path)) missing.Add(path);
        }
        return missing;
    }

    /// <summary>
    /// Whether the named path is the tail of a path this turn read, listed or
    /// ran, and that path is on disk. Measured (2026-11-05, music-harness): the
    /// turn read <c>stage-unity/Tools/deevid/dv-gen.cjs</c>, ran it twice
    /// successfully, and reported "dv-gen.cjs 로 생성" — flagged "not on disk",
    /// which bought an apology answer, a ⚠ on a turn that had worked, and an
    /// escalation to the strong model.
    /// </summary>
    private static bool WasTouchedAndExists(string root, string named, IReadOnlyCollection<string> touched)
    {
        foreach (var t in touched)
        {
            // A folder the turn listed or handed to a command: a file named
            // inside it (a command's output, a sibling it saw) counts.
            if (Directory.Exists(Path.Combine(root, t)) && File.Exists(Path.Combine(root, t, named))) return true;

            // A bare name ("plan.md") anywhere a few levels under that folder.
            // Measured (2026-11-07): after `ls -R songs/x`, the answer named
            // plan.md (it is songs/x/mv/plan.md); flagged "not on disk", the model
            // was told to write it and replaced a good plan with a new one.
            if (!named.Contains('/') && Directory.Exists(Path.Combine(root, t)) && ExistsUnder(Path.Combine(root, t), named)) return true;

            if (!string.Equals(t, named, StringComparison.OrdinalIgnoreCase)
                && !t.EndsWith("/" + named, StringComparison.OrdinalIgnoreCase)) continue;
            if (File.Exists(Path.Combine(root, t))) return true;
        }
        return false;
    }

    /// <summary>A file of that name anywhere in the workspace (generated and engine-cache folders skipped, bounded).</summary>
    private static bool ExistsAnywhere(string root, string name)
    {
        var pending = new Stack<(string Dir, int Depth)>();
        pending.Push((root, 0));
        var seen = 0;
        while (pending.Count > 0 && seen < 30_000)
        {
            var (dir, depth) = pending.Pop();
            try
            {
                if (File.Exists(Path.Combine(dir, name))) return true;
                if (depth >= 6) continue;
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    seen++;
                    if (!WorkspaceProbe.IsExcluded(Path.GetFileName(sub)) && !LocalFileToolbelt.IsEngineCache(dir, sub))
                        pending.Push((sub, depth + 1));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* skip */ }
        }
        return false;
    }

    private static bool ExistsUnder(string dir, string name)
    {
        try
        {
            return Directory.EnumerateFiles(dir, name, new EnumerationOptions
            {
                RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive
            }).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    /// <summary>The paths a successful read/list/find/grep/run call names, for <see cref="WasTouchedAndExists"/>.</summary>
    private static void NoteTouched(ToolCall call, List<string> touched)
    {
        if (call.Tool is "read_file" or "list_files" or "find_files" or "grep"
            && call.Arg("path") is { Length: > 0 } path)
            touched.Add(Normalize(path));
        else if (call.Tool == "run_command" && call.Arg("command") is { Length: > 0 } command)
        {
            touched.AddRange(FilePathsIn(command));
            // Folder arguments too (an output directory) — any relative token with a slash.
            touched.AddRange(command.Split((char[])[' ', '\t', '"', '\''], StringSplitOptions.RemoveEmptyEntries)
                .Where(t => (t.Contains('/') || t.Contains('\\')) && !t.Contains("://") && !Path.IsPathRooted(t))
                .Select(Normalize)
                .Where(t => t.Length > 0));
        }
    }

    /// <summary>
    /// Whether a named path exists next to something this turn wrote — in the
    /// written file's folder or one above it. Measured (2026-11-05,
    /// music-harness): a turn rewrote <c>songs/x/guide.md</c> and reported its
    /// untouched siblings <c>lyrics.md</c> and <c>notes.md</c> by name; both
    /// were on disk, but only <c>root/lyrics.md</c> was checked, so each was
    /// "not on disk" and the model rewrote two good files to satisfy the guard.
    /// </summary>
    private static bool ExistsBesideAWrite(string root, string named, IReadOnlyCollection<string> wrote)
    {
        foreach (var w in wrote)
        {
            var dir = Path.GetDirectoryName(w.Replace('/', Path.DirectorySeparatorChar));
            while (!string.IsNullOrEmpty(dir))
            {
                if (File.Exists(Path.Combine(root, dir, named))) return true;
                dir = Path.GetDirectoryName(dir);
            }
        }
        return false;
    }

    /// <summary>
    /// Whether a path the answer names was written this turn. Matched by
    /// path-suffix, not exact string: the model routinely reports a file it
    /// wrote at <c>a/b/report.html</c> by its basename <c>report.html</c> (or a
    /// shorter trailing segment), and an exact compare called that real, on-disk
    /// file "missing". Measured (2026-11-04, zero-test): a finished HTML report
    /// written to <c>security-assessment/report/endpoint_enumeration_report.html</c>
    /// was named by its basename in the answer and flagged as not on disk —
    /// which drove an apology, a wasted escalation to the strong model whose own
    /// result was then discarded, and a final answer that denied its own work.
    /// A written path forgives any trailing path segment of it (basename or more).
    /// </summary>
    private static bool WasWrittenThisTurn(string named, IReadOnlyCollection<string> wrote)
    {
        foreach (var w in wrote)
            if (string.Equals(w, named, StringComparison.OrdinalIgnoreCase)
                || w.EndsWith("/" + named, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
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

    // csproj/sln/props/targets/xaml: measured, a claimed WebAutomationTests.csproj
    // passed unseen because the extension was not on this list.
    [GeneratedRegex(@"(?:[\w.\-]+/)*[\w.\-]+\.(?:html?|css|js|mjs|cjs|jsx|ts|tsx|json|py|cs|csproj|sln|slnx|props|targets|xaml|java|kt|go|rs|rb|php|c|cpp|h|hpp|md|txt|ya?ml|toml|sql|sh|ps1|bat)\b",
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
        steps.Any(s => s.Tool is not (ToolCall.FinalTool or "unwrapped" or "(unparsed)" or "unwritten" or "missing" or "stalled" or "route-overruled"));

    /// <summary>
    /// The request's language, named, when the request is written in a
    /// non-Latin script and the answer contains none of it. Null otherwise —
    /// including for a Latin-script request, where the words would have to be
    /// read to tell English from German, and for answers too short to judge.
    /// </summary>
    internal static string? WrongScript(string request, string answer)
    {
        var (script, share) = DominantScript(request);
        if (script is null || share < 0.3) return null;
        // Code and paths are Latin in every language: count prose only.
        var prose = InlineCode().Replace(FencedBlock().Replace(answer, " "), " ");
        var letters = prose.Count(char.IsLetter);
        if (letters < 40) return null;
        // Not "any letter of the script": measured (2026-11-06), a "재개" turn
        // answered in English prose whose only Hangul was the title 「종말의 그날」,
        // and the check passed it. A Korean answer that quotes English file names
        // stays far above this share.
        return (double)prose.Count(c => ScriptOf(c) == script) / letters >= AnswerScriptShare ? null : script;
    }

    /// <summary>The share of an answer's prose letters that must be in the request's script.</summary>
    internal const double AnswerScriptShare = 0.1;

    [GeneratedRegex(@"```.*?```", RegexOptions.Singleline)]
    private static partial Regex FencedBlock();

    [GeneratedRegex(@"`[^`\n]*`")]
    private static partial Regex InlineCode();

    private static (string? Script, double Share) DominantScript(string text)
    {
        var letters = 0;
        var counts = new Dictionary<string, int>();
        foreach (var c in text)
        {
            if (!char.IsLetter(c)) continue;
            letters++;
            if (ScriptOf(c) is { } script) counts[script] = counts.GetValueOrDefault(script) + 1;
        }
        if (letters == 0 || counts.Count == 0) return (null, 0);
        var top = counts.MaxBy(kv => kv.Value);
        return (top.Key, (double)top.Value / letters);
    }

    /// <summary>The non-Latin script a letter belongs to, by the name the model is told; null for Latin and the rest.</summary>
    private static string? ScriptOf(char c) => c switch
    {
        >= '가' and <= '힣' or >= 'ᄀ' and <= 'ᇿ' or >= '㄰' and <= '㆏' => "Korean",
        >= '぀' and <= 'ヿ' => "Japanese",
        >= '一' and <= '鿿' => "Chinese",
        >= 'Ѐ' and <= 'ӿ' => "Russian",
        >= '؀' and <= 'ۿ' => "Arabic",
        >= '฀' and <= '๿' => "Thai",
        _ => null
    };

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
        "decide" => $"asking Jev: {call.Arg("question")}",
        _ => call.Tool
    };

    private static AgentRun Finish(StopReason reason, string text, List<AgentStep> steps, Stopwatch sw, string streamed = "")
    {
        sw.Stop();
        return new AgentRun(reason, text, steps, sw.Elapsed, streamed);
    }
}
