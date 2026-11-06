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
| Blind to Claude skills (FIXED 2026-11-05) | Asked to "find it in the Claude skills / guidance", the turn only `list_files`/`find_files` the visible tree; no `.claude/…/SKILL.md` or plugin skill is ever read | The file walk skipped every dot-folder, so `.claude/skills` was invisible to list/find/grep, and enabled-plugin skills live outside the root (`~/.claude*/plugins/cache/…`). Measured in a Claude-skill-based workspace: five turns rebuilt the method from one script found by luck. Fix: `Tools/ClaudeSkillScope` detects project `.claude/{skills,commands,agents}`, user skills and **enabled** plugins (`enabledPlugins` + `installed_plugins.json`, project settings win, one copy per plugin across `~/.claude-*` profiles), plus the workspace paths those skills name (record folders like `logs/` dropped). `list_files .` names them; whole-workspace `find_files`/`grep` walk skills → linked paths → rest; plugin folders are readable, never writable. `ClaudeSkillScopeTests`. Measured after: `find_files *deevid*` → `grep deevid` → read the generator → guide reshaped to its 3-block format → both generations run. |
| False "not on disk" on an untouched sibling / a read file (FIXED 2026-11-05) | `unwritten` naming `lyrics.md` / `dv-gen.cjs` that exist under a subfolder the turn wrote into or read | Same family as the row above, two more holes: only `root/<name>` was checked, so a sibling of a written file and a file the turn **read and ran** were "missing". Cost: two good files rewritten; an apology answer + ⚠ + escalation on a turn that had worked. Fix: `ExistsBesideAWrite` (written file's folder and up) and `WasTouchedAndExists` (paths of successful read/list/find/grep/run calls incl. a command's folder arguments — a file inside a listed folder counts — suffix match, must exist). `FileDeliverableTests`. Sibling hole in the smart-mode claim check: a download script saved four takes and "files created with no write_file" sent the finished turn back into a 15-min correction pass; a successful `run_command` now backs a file change too (`ClaimCheckTests`). |
| On-device model degeneration (repetition collapse) | **No new JSONL row** while the live TUI streams a short token cycle forever (`을 아수 을 아수 …`); session file mtime frozen | The quantized everyday model (Gemma, a1) loses coherence — measured right after a large tool result entered context — and emits a 2–3 token cycle without end. Not a loop bug: the stream is uncommitted, so the log shows nothing and only the live tab shows it. **Amplified by a high `maxTokens`** (120k): the garbage runs toward the cap instead of ending in a few hundred tokens, and the streaming idle-timeout never fires because tokens keep flowing. Detect from the live terminal, not the log. See the robustness note below — fix deferred, gathering recurrence data. |
| Unused tool → hand-back (FIXED 2026-11-06) | Asked to "drive Playwright", the turn answers with **no tool** ("I can't see your browser") or `web_search` → `write_file *.cjs` that `require`s a missing package, ending "start Chrome with --remote-debugging-port and tell me" | The catalog had no browser and the prompt named no installed CLI, so the model rebuilt the tool from a web snippet and handed the work back — with `playwright-cli` on PATH and a debuggable Chrome already on 9222. Fix: `browser_*` verbs over `@playwright/cli` (attach to the CDP Chrome, new tab, detach only), `KnownCli` + `cli_guide` (installed tools named in the prompt with usage cards), router web/workspace admit the browser, `RepeatRule` so a snapshot after a click is not a repeat. `BrowserToolTests`. Measured after: same request → `web_search` → `browser_open` (search) → `browser_open` (/videos) → 7 titles + dates matching the page, claims `backed 0.83`. |
| Cut page, silent omission (FIXED 2026-11-06) | Answer lists items from a long page but misses ones near its end; the tool result ended in a bare "truncated" | A YouTube channel tree (~38 KB) was clipped at 16 000 chars; the oldest history video fell below the cut and the answer never said the list was partial. Fix: the cut now says what is NOT shown and names `browser_read` / `browser_find`. |

## On-device LLM robustness — a capability lane of its own (fix deferred, accumulating)

On-device **quantized** models occasionally destabilise (degeneration / repetition collapse). This is a known
trait of aggressive quantization under long context, **not** an agent-one logic bug — so it is tracked here as
its own "agent on-device LLM" improvement line, to be solved after enough recurrence data, not patched on first
sight.

- **Observed 2026-11-04** (a fresh resumed session): the everyday model collapsed into an endless 2–3 token
  repetition while composing an answer after a large tool result (~24 KB) had entered context. The operator
  stopped it. gemma × qwen, concurrency 1+1, slow on-device hardware.
- **Recurred 2026-11-05 ×2, both at escalation** (music workspace): ① after a 9,899-char reasoning hand-off the
  everyday model's rewrite sat at `thinking` 30+ min, JSONL frozen at the escalation row (release 0.8.x binary);
  ② during the escalation itself the live tab streamed an endless column of digit strings
  (`1123478 / 2123469 / …`), JSONL frozen at `escalating to qwen…` for 10+ min. A third escalation the same night
  ended `ResponseEnded` (the strong model's stream cut). Pattern so far: **the escalation path** — large context
  (20 KB knowledge doc + guide + drafts) handed to/back from the strong model — is where it happens.
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
