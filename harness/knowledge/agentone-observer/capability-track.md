# agent-one capability track — how to observe

Canon for `agentone-observer`. Where agent-one's runtime leaves evidence, how to read it, and the failure
modes measured so far. Pair with `_shared/agent-one-private-track.md` (the track's purpose and location).

## Where agent-one logs its runs

| What | Path |
|---|---|
| Per-session turn log (JSONL) | `~/.agent-one/workspaces/<name>-<sha1[10]>/sessions/<yyyyMMdd-HHmmss>-chat.jsonl` |
| Turn-by-turn memory | `~/.agent-one/workspaces/<name>-<sha1[10]>/memory.md` |
| Background session log | `~/.agent-one/.../logs/session.log` (only when run as a background session) |

- `~` here is `C:\Users\psmon`. The current track's workspace is **`zero-test-beaaeda5ea`** (root `C:\code\zero-test`).
- Pick the newest `*-chat.jsonl` unless told otherwise. `memory.md` has one entry per *completed* turn; a turn
  stopped by a guard may leave no memory entry, so the JSONL is the source of truth.

## JSONL line kinds (one object per line)

`prompt` (turn start, `text`+`mode`) · `route` / `scope` / `pdsa` / `graph` (smart-router decisions, with
`confidence`) · `step` (`tool`, `ok`, `text` — the tool call + its short result) · `safety` (`ok`, Jev verdict
`safe`/`unsafe`) · `title` · `final` (the answer) · `result` (how the turn ended: `Final` / `Repeat` /
`ProviderError` / …) · `pdsa-study` (`unmet`/`partial`/`met`) · `knowledge` / `learned` (what it distilled).

## Parse recipe (read-only)

```bash
cd ~/.agent-one/workspaces/zero-test-beaaeda5ea/sessions
PYTHONIOENCODING=utf-8 python -c "
import json
rows=[json.loads(l) for l in open('<session>.jsonl',encoding='utf-8') if l.strip()]
# segment at kind=='prompt'; per turn collect steps/safety/result/pdsa/final/learned
"
```

Two measured traps:
- **The console is cp949.** Printing Korean without `PYTHONIOENCODING=utf-8` raises `UnicodeEncodeError` and
  the read aborts. Always set it.
- **Do not pass the log path through a `node -e "...$VAR..."` string** — the shell mangles `/c/Users/...` into
  `C:\c\Users\...` and node throws ENOENT. Use python reading the literal path, or `cat`/`grep`.

## Reading the workspace itself (ground the log against disk)

- `C:\code\zero-test\node_modules` empty ⇒ **no install happened** (the no-install axis, checked directly).
- `security-research/results/*.txt` ⇒ captured phase output. `PROGRESS.md` "Findings" still `_pending_` ⇒ ran
  but did not record.
- `.local/cdp-profile` present ⇒ `cdp.cjs` launched its own Chrome; absent while ports 9222/9223 LISTEN ⇒ it
  attached to an already-running Chrome (the operator's music-harness browser — a known port collision).

## Measured failure catalogue (append a line when a new one is observed)

| Mode | Signal in the log | Note |
|---|---|---|
| Repeat loop | `step … "repeated call"` then `result: Repeat` | Small on-device model repeats identical `write_file`/`run_command`; the Repeat guard stops the turn, often `pdsa unmet`. The dominant limiter (2026-11-03: 5 of 7 turns). |
| Korean in the tool envelope | `(unparsed) FAIL malformed JSON: '0xEC' is invalid` | Gemma native tool-call / JSON parse breaks on Korean bytes in an argument. Candidate source fix: `GemmaNativeCall` / `ToolCall.Repair`. |
| False report | `unwritten` / `missing` / `unverified` guard fires | Claims files/results not on disk; the guard catches it (good), but the model keeps doing it. |
| ProviderError | `result: ProviderError` | On-device provider errored mid-turn (concurrency 1+1 / hardware). Whole turn lost; check `OnStall`/retry covers it. |
| Port collision | `.local/cdp-profile` absent while 9222 LISTENs | `cdp.cjs` default port 9222 collides with the operator's music-harness Chrome → scan runs inside a live logged-in session. Isolate with `CDP_PORT`. |
| Re-run of a succeeding script | same `run_command … OK -> N chars` repeated across turns with identical output; turns end `Repeat` | Measured 2026-11-03: a probe script ran OK (large output) on two consecutive turns, same output, while the model kept rewriting the same file. The large tool output is not digested, so no finding is extracted and no done-signal is reached → it re-runs the success. This is the "뺑뺑이" the operator felt. Fix: big output → write to a file + summarise, and force "if it ran OK, record the finding and move on; never re-run a success." (Target-specific detail stays in the private track space.) |
| False "not on disk" on a subfolder file (FIXED 2026-11-04) | `write_file … ok=True` for `a/b/x.html`, then `unwritten` + `missing` fire naming `x.html` | The deliverable guard (`MissingFilesNamed`) matched the answer's file token against the written set by **exact string**. A file written under a subdir but named by its **basename** in the answer was declared missing though it was on disk — which drove an apology turn, a wasted escalation to the strong model whose result was then discarded, and a final answer denying its own completed work. Looks like "작업 완료가 안 됨" though the file is there. Fix: `WasWrittenThisTurn` forgives any trailing path segment of a written path (`AgentLoop.cs`); `FileDeliverableTests` pins it. Running binaries built before this still show it. |
| On-device model degeneration (repetition collapse) | **No new JSONL row** while the live TUI streams a short token cycle forever (`을 아수 을 아수 …`); session file mtime frozen | The quantized everyday model (Gemma, a1) loses coherence — measured right after a large tool result entered context — and emits a 2–3 token cycle without end. Not a loop bug: the stream is uncommitted, so the log shows nothing and only the live tab shows it. **Amplified by a high `maxTokens`** (120k): the garbage runs toward the cap instead of ending in a few hundred tokens, and the streaming idle-timeout never fires because tokens keep flowing. Detect from the live terminal, not the log. See the robustness note below — fix deferred, gathering recurrence data. |

## On-device LLM robustness — a capability lane of its own (fix deferred, accumulating)

On-device **quantized** models occasionally destabilise (degeneration / repetition collapse). This is a known
trait of aggressive quantization under long context, **not** an agent-one logic bug — so it is tracked here as
its own "agent on-device LLM" improvement line, to be solved after enough recurrence data, not patched on first
sight.

- **Observed 2026-11-04** (a fresh resumed session): the everyday model collapsed into an endless 2–3 token
  repetition while composing an answer after a large tool result (~24 KB) had entered context. The operator
  stopped it. gemma × qwen, concurrency 1+1, slow on-device hardware.
- **Why it is loud now**: `maxTokens` was raised (everyday 120k / reasoning 250k) to stop truncation; the side
  effect is that a degenerate run now streams toward that cap. The idle clock catches *silence*, and a repeating
  stream is not silent — so nothing cuts it before max_tokens or the operator does.
- **How the observer spots it**: it leaves **no JSONL evidence** (uncommitted stream). The session file mtime
  stays frozen while the TUI fills. Before calling anything "hang vs. degeneration vs. thinking", read the live
  agent-one tab (`agentzero-cli terminal-read` on the zero-test tab — Group «zero-test», the `…/PW5` session),
  then compare with the JSONL mtime.
- **Candidate fixes (deferred until recurrence rate + triggers are clearer)**: ① a stream-level repetition
  detector in the provider that cuts a degenerate generation early as a stall → the existing `OnStall` recovery
  (headless-testable with a scripted degenerate stream); ② the per-call watchdog (`maxCallSeconds`, already in
  source, default off) as a wall-clock backstop; ③ request-side `repeat_penalty` / `frequency_penalty`, or
  operator-side server tuning / shorter context / a longer-context model. Root cause is model/server quality.
- **Decision (2026-11-04, operator)**: accumulate only; run more sessions, let data build, then solve.

## What is NOT evidence of a problem

A gap between the last `step` and now that is **shorter than the on-device turn time** is thinking, not a
hang. Check the JSONL mtime before reporting anything stalled. And a **frozen** mtime with the TUI still
scrolling is not a hang either — it is the degeneration case above; confirm by reading the live tab.
