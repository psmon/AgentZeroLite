# agent-one

A standalone CLI agent. Ask it something, it reads your workspace, writes
files, runs commands behind a gate and answers — on Windows, macOS and Linux,
from one native binary with no .NET runtime to install.

```bash
agent-one run "what does this project do?"
agent-one chat
agent-one session start --smart && agent-one ask "build it and run the tests"
```

It is **independent**: `Project/AgentOne` references nothing else in this
repository. AgentZero Lite may drive it as a child process, but agent-one does
not need AgentZero, a GUI, or Windows to run.

What is in the box, one line each — the sections below go into each:

- **Tools with a boundary** — `list_files` / `read_file` / `find_files` / `grep`,
  `write_file` (workspace root only), `web_search` / `web_read` (GETs), and
  `run_command` (PowerShell or bash in the root) behind a **gate**: risky
  patterns always ask a person, the decision engine clears the rest.
- **Smart mode with Jev** — a small, fast model does the work; a decision
  engine (TypeSafe *System One*) answers fixed-option questions in 0.3 s —
  route, scope, safety, escalate, task switch — and a slower, stronger
  *reasoning model* is brought in only when the engine says the draft needs more.
- **A workspace that remembers** — per-folder memory (50 k chars) that opens
  every session, saved sessions with `/resume` replaying the screen, and a task
  title kept by the model.
- **Long-term memory as a graph** — after each turn the engine judges whether it
  taught anything; what it did is distilled into an embedded **Kùzu** graph with
  the engine's rationale attached, and consulted before any file is scanned.
- **A PDSA improvement loop** — a planning request opens a Plan · Do · Study · Act
  cycle in that same graph; the engine places each following turn in it, Study
  judges the result against what Plan predicted, and Act wires the finished
  cycle to the knowledge it taught and the knowledge it stood on.
- **A background session** — `session start` runs one detached; `ask` sends a
  request from any shell and prints the turn as it happens. That is how the chat
  mode tests itself and how another agent collaborates with this one.
- **The Bot / Loop actor pair** — the conversation runs as AgentZero's
  `AgentBotActor` / `AgentLoopActor` on Akka.NET (a 1.6 nightly) inside the AOT
  binary; the window, the REPL, `run` and the pipe server are renderers over it.
- **Pause** — Esc holds a running turn at its next step; the next line you type
  is read as resume, stop or refine.

---

## Quick start

No API key, fully offline — this is the smoke test:

```bash
agent-one run "hello" --provider echo
agent-one tools prompt          # exactly what the model is told
```

Or set it up on a screen and test the connection without leaving it:

```bash
agent-one setup
```

With a real model:

```bash
agent-one config set provider openai
agent-one config set baseUrl https://api.openai.com/v1
agent-one auth set                      # paste the key; nothing is echoed
agent-one models                        # proves the key and URL, and lists models
agent-one config set model gpt-4o-mini
agent-one run "summarize the README" -v
```

The key goes in `~/.agent-one/credentials.json`, never in `config.json`, and
`auth set` reads it from a hidden prompt or stdin so it stays out of your shell
history. Exporting `$OPENAI_API_KEY` still works as a fallback.

Against a local server (Ollama, LM Studio, vLLM, llama.cpp — all speak the same
wire format, which is why there is only one provider):

```bash
agent-one config set provider openai
agent-one config set baseUrl http://localhost:11434/v1
agent-one config set model qwen2.5-coder:7b
agent-one run "이 폴더에 뭐가 있는지 알려줘"
```

## Commands

| Command | What it does |
|---|---|
| `agent-one run <prompt>` | Ask once, print the answer, exit. The prompt may also arrive on stdin. |
| `agent-one chat` | The chat window: transcript above, your line at the bottom. `--plain` or a pipe gives the line REPL. `/status`, `/resume`, `/new`, `/reset`, `/exit`; Esc pauses a running turn. |
| `agent-one session` | The one background session: `start` (detached), `status`, `stop`, `selftest`. |
| `agent-one ask <request>` | Send one request to the background session and print the turn. `--yes`, `--json`. |
| `agent-one config` | `show` / `get` / `set` / `path` / `reset` over `~/.agent-one/config.json`. |
| `agent-one setup` | Full-screen settings: connection, model, reasoning model, options, smart mode (`tui` still works as an alias). |
| `agent-one models` | List what the configured endpoint can run (`*` marks the configured one). Exit 1 if it refuses or lists nothing. |
| `agent-one auth` | `show` / `set` / `check` / `clear` / `import`. `--jev` addresses the TypeSafe key, `--reasoning` the strong model's. |
| `agent-one jev` | `check` / `choose` — put a decision to TypeSafe and see the distribution. |
| `--smart` / `--basic` | On `run` and `chat`: route and escalate through the decision engine, or straight to the loop. |
| `agent-one tools` | `list` / `show <name>` / `prompt`. |
| `agent-one memory` | The workspace's knowledge graph: stats, `recent`, `helpful`, `search <words>`, `path <fragment>`, `pdsa`, `query "<cypher>"`. |
| `agent-one home` | Where agent-one keeps its files. |

Shared flags for `run` and `chat` — each one overrides the stored config for
that invocation only:

```
-r, --root <dir>      Workspace the tools may read and write (default: cwd)
-p, --provider <name> echo | openai
-m, --model <name>    Model id
    --base-url <url>  OpenAI-compatible endpoint
    --max-steps <n>   Tool-loop budget (1..100)
    --temperature <t> 0..2
    --timeout <secs>  Per-request timeout
    --no-session      Do not write a transcript
    --json            One JSON object on stdout instead of prose
-v, --verbose         Trace each tool call on stderr
-q, --quiet           No progress display, no streaming
-y, --yes             run only: approve commands the gate would have asked about
```

### Watching it work

A run that searches the web and reads two pages takes twenty seconds, and twenty
silent seconds read as "it has hung". So it narrates, on **stderr**, and streams
the answer to **stdout** as the model writes it:

```console
$ agent-one run "What is Akka.NET in one sentence? Search the web first."
… searching the web for "What is Akka.NET"
✓ web_search  (1.1s)
… thinking about what came back
Akka.NET is an open-source toolkit and runtime that provides an idiomatic .NET
implementation of the actor model for building highly concurrent, distributed,
and fault-tolerant applications.
```

The live line rewrites itself in place in a terminal, and degrades to one plain
line per step when stderr is redirected — a CI log wants a record, not an
animation. Because the narration is on stderr, a pipe is unaffected:

```console
$ agent-one run "reply with exactly: PIPED" > answer.txt
… thinking
$ cat answer.txt
PIPED
```

`--quiet` silences it, and `--json` implies quiet.

The model streams JSON — `{"tool":"final","args":{"text":"…"}}` — so what you see
is the decoded `text` field being written, never the braces. A **tool call**
streams nothing: `grep` also has a `text` argument, and printing a search pattern
as though it were the answer would be a lie with a very plausible shape.

Exit codes: `0` answered, `1` stopped early (budget, repeat, parse, provider),
`2` usage error, `130` cancelled. `--json` makes the outcome machine-readable:

```console
$ agent-one run "hi" -p echo --json
{"ok":true,"stopReason":"Final","text":"hi","steps":1,"elapsedMs":8,"provider":"echo","model":"gpt-4o-mini","session":"/home/me/.agent-one/sessions/20260922-120005-run.jsonl"}
```

## Settings TUI

`agent-one setup` (`agent-one tui` is the same screen) walks the settings as a five-step
stack, in the order they actually depend on each other:

```
1. Connection  →  2. Model  →  3. Reasoning  →  4. Options  →  5. Smart
```

You cannot sensibly pick a model before the endpoint and key are right, and the
endpoint is the thing that knows which models exist — so step 2 asks it. Step 3
is the same shape again for the stronger model, with everything defaulting to
the step before.

### 1. Connection — where and who

```
╭─ agent-one config ──────────────────────────────────────────╮
│[1. Connection] →  2. Model     →  3. Reasoning  →  4. Options → │
│                                                             │
│› provider        openai  ←→                                 │
│  baseUrl         https://a1.example.com/v1                  │
│  apiKey          sk-lm-…Kpvc                                │
│  apiKeyEnv       OPENAI_API_KEY  (not set)                  │
╰─────────────────────────────────────────────────────────────╯
 paste the key itself here — stored in ~/.agent-one/credentials.json, never in config.json
 ↑↓ move · Enter edit · ←→ cycle · Tab next · s save · t test · q quit
```

**`apiKey` takes the key itself.** It is echoed as dots while you type, shown
masked afterwards, and written to `~/.agent-one/credentials.json` — never to
`config.json`, so the config file stays safe to paste into an issue or copy
between machines.

**`apiKeyEnv` is a fallback, and it holds the NAME of an environment variable,
not a key.** Pasting a key there is now refused with a message saying where the
key goes; a config file that already contains one is flagged on load, and
`agent-one auth import` moves it into the store.

Lookup order: the stored key first, then `$apiKeyEnv`.

### 2. Model — asked, not typed

`Tab` moves on, and **arriving at this step calls `GET {baseUrl}/models`**:

```
╭─ agent-one config ──────────────────────────────────────────╮
│ 1. Connection → [2. Model]     →  3. Reasoning  →  4. Options → │
│                                                             │
│  google/gemma-4-e4b                                         │
│› text-embedding-nomic-embed-text-v1.5                       │
│  · type a model id myself ·                                 │
│                                                             │
╰─────────────────────────────────────────────────────────────╯
 the list came from the endpoint itself — picking from it cannot be a typo
 ✓ 2 models from http://localhost:1234/v1/models
 ↑↓ pick · Enter take · e type · b back · Tab next · s save · t test · q quit
```

The list starts on the model already configured and stays up after a choice, so
a mis-pick costs one keystroke. The last row always falls through to typing an
id by hand, and `e` does the same — a listing that is stale or incomplete is
never a dead end.

**That one request is also the health check for step 1.** It exercises the base
URL, the network path and the API key at once, so a list appearing means all
three are right. Nothing appearing names what failed and sends you back:

```
✗ HTTP 401 — no API key: $OPENAI_API_KEY is not set — check baseUrl and $OPENAI_API_KEY · b to go back, e to type an id
✗ HTTP 401 — the endpoint rejected the key in $OPENAI_API_KEY — …
✗ cannot reach http://localhost:11434/v1/models — …
✗ http://localhost:1234/v1/models answered, but listed no models — …
```

An empty list is treated as a failure, not as an empty picker: an endpoint that
answers but offers nothing is a misconfiguration, and saying so is more use than
a blank box. The `echo` provider lists itself, so the whole flow is walkable
offline with no key at all.

### 3. Reasoning — the strong, slow model

The everyday model is small and fast and answers first. This step names a
second, stronger model that a hard question can be escalated to — the
escalation itself is smart mode's job; this is only where it points.

```
╭─ agent-one config ──────────────────────────────────────────╮
│ 1. Connection →  2. Model  → [3. Reasoning] →  4. Options → │
│                                                             │
│  reasoningBaseUrl       (same as the connection)            │
│  reasoningApiKey        (same as provider key)              │
│› reasoningModel         (none — Enter to pick, e to type)   │
╰─────────────────────────────────────────────────────────────╯
 the slow, strong model hard questions escalate to · Enter lists the endpoint's models, e types an id · empty = never escalate
 ↑↓ move · Enter list · e type · b back · Tab next · s save · t test · q quit
```

Every row defaults to the step before: an empty `reasoningBaseUrl` means the
same endpoint, an empty `reasoningApiKey` means the provider key — the common
case of one gateway serving two model sizes needs nothing but the model id. An
empty `reasoningModel` means there is no strong model and nothing is ever
escalated.

`Enter` on `reasoningModel` asks *that* endpoint for its list, exactly as step 2
does, and the list overlays the step: `Enter` takes one, `Esc` closes it, `e`
types an id by hand. `t` on this step tests the reasoning model, not the
everyday one. `agent-one auth set --reasoning` stores the key from the command
line; `agent-one config set reasoningModel <id>` does the rest without a screen.

### 4. Options — how the loop behaves

`maxSteps`, `temperature`, `timeoutSeconds`, `saveSessions`. All have working
defaults, which is why they come late.

### 5. Smart — plan first, then decide

```
╭─ agent-one config ──────────────────────────────────────────╮
│ … →  3. Reasoning  →  4. Options  → [5. Smart]              │
│                                                             │
│› smartMode        off  ←→                                   │
│  jevApiKey        ts-abc…w9k2                               │
│  jevBaseUrl       https://api.typesafe.ai/v1                │
│  jevModel         jev-latest                                │
│  jevConfidenceFloor     0.60                                │
╰─────────────────────────────────────────────────────────────╯
 the TypeSafe (Jev) key for smart mode — a different service · h to check it
 ✓ jev-1.13.0 · 184 ms, noul 0.97, 41+12 tokens
 ↑↓ move · Enter edit · h check · b back · s save · t test · q quit
```

`h` makes **one real question** to TypeSafe rather than checking that a file
exists — a key that is present but wrong would otherwise pass the check and fail
later, which is the mistake this screen already exists to prevent. The reply
carries the served model, the round trip, and the token count.

The TypeSafe key is a second key for a second service: it goes in the same
`credentials.json`, in its own slot, and storing one never disturbs the other.

Turning `smartMode` on **runs the health check first** and stays off if it
fails: it is not a preference, it is a dependency.

## The chat window

`agent-one chat` in a terminal opens a window rather than a prompt:

```
 [smart]  agent-one · turn 3
  agent-one chat · openai · google/gemma-4-e4b
  tools: files: C:\work\repo · web: the web (read-only: search and fetch)

› MSA로 전환할 때 데이터 일관성은 어떻게 보장하지?
  route: → web  (confidence 0.91)
  ✓ web_search  (1.2s)
  ✓ web_read  (4.8s)
◆ 사가 패턴과 이벤트 소싱으로 …

  escalation: escalating to qwen/qwen3.8-27b  (confidence 0.83)
  ✓ reasoning  qwen/qwen3.8-27b · 2 913 chars  (24.1s)
◆ MSA에서 데이터 일관성은 세 층위로 나눠 봐야 합니다 …

 done in 61.2s · 4 steps
› ▌
```

The answer streams into the transcript as the model writes, and the transcript
scrolls: the **mouse wheel** moves three lines, **PageUp/PageDown** a page,
**Ctrl+End** returns to the live end, and a scrollbar on the right shows where
you are. A pasted paragraph stays on the one input line as a window around the
cursor (`…` marks a cut end) instead of wrapping into the transcript. New text does not pull
you back down while you are reading — the header says `↑ N lines above the
end · Ctrl+End to follow` until you do. The header also says which mode you
are in and whether a turn is running; the bottom line is yours. Shift+Tab
switches basic ↔ smart, Esc clears the line (twice: quit), Ctrl+D quits.

**Esc while a turn runs pauses it.** Nothing can interrupt a model
mid-sentence or a command mid-run, so the turn finishes the step it is on
and then waits — the status line says so — and the line you type next is
read for what it means: an empty line or "continue" resumes, "stop" abandons
the turn (it ends as cancelled), and anything else is a *refinement* — put in
front of the model as `[the user, mid-turn] …` before it thinks again, so
"use tabs, not spaces" mid-build changes the build. With a TypeSafe key the
decision engine reads the line (resume / stop / refine, one fixed question);
without one a short word list does, in English and Korean.

```
› 보드 API 만들어줘
  ✓ write_file  (0.0s)
  … thinking about what came back            ← Esc
  ⏸ pausing at the next step — type to go on, 'stop' to abandon, or say what to change
› 테스트도 같이 만들어
  pause: refining: 테스트도 같이 만들어  (confidence 0.81)
  … thinking about what came back
  ✓ write_file  (0.0s)
```

Long answers are folded to the window width *before* they reach the transcript
(`Tui/SoftWrap`). That is a performance fix, not a cosmetic one: Termina's
streaming node re-measures a line for every cell it draws, and one
2,300-character answer line froze the window for nine seconds (measured with a
stack dump). Short lines make the cost vanish.

### Doing things, not just answering

The agent can **create files** and **run commands**, so "scaffold a FastAPI
service and run its tests" is a request it can carry out, not just describe:

```
› hello 라는 문구를 출력하는 파이썬 스크립트 hello.py 를 만들고 실행해서 결과를 확인해줘
  route: → workspace  (confidence 0.85)
  scope: small — going ahead  (confidence 1.00)
  ✓ write_file  (0.0s)
  safety: safe — asking you  (confidence 0.37)
  ⚠ run this command?  python hello.py
  in C:\work\scratch · not run unasked because: the decision engine judged it safe (confidence 0.37)
approve (y/n) › y
  ✓ run_command  (1.2s)
◆ hello.py 를 만들고 실행했습니다. 출력: hello
```

Three rules hold whatever is asked:

- **Files are only ever written under the workspace root** (`--root`, default:
  the current directory). A folder you name by its absolute path — "compare
  with D:\other\project" — becomes **readable** for the rest of the session
  (the note says so), and never writable.
- **A command runs unasked only when it is plainly safe.** A fixed list of
  patterns (`rm -rf /`, `sudo`, `format`, a piped installer, `git push --force`
  …) always asks you; for the rest the decision engine judges, and only a
  *confident* "safe" runs on its own. Everything else lands on the input line
  as a question — `y` runs it, anything else skips it and tells the model so.
  Without a TypeSafe key every command asks.
- **Large work is designed first.** In smart mode a workspace request is sized;
  one the engine calls large goes to the reasoning model for a design — file
  layout, responsibilities, order of steps — which the everyday model then
  builds step by step. The design's first lines are shown as it comes back, so
  you can follow what is being built. **When the design hinges on a choice**
  (storage engine, framework, structure) the strong model says so up front,
  and the turn stops to ask you — pick a number, press Enter for its
  recommendation, or type your own — before anything is written.
- **A turn always ends with an account of itself.** The model is told to close
  a piece of work with what was done, what is left, and 1–3 next steps. A turn
  that runs out of step budget mid-build gets one more call, without tools, to
  say the same — the stop is still named after it, but the last thing on
  screen is a summary, not `[stopped: MaxSteps]`. The budget itself is 50
  steps by default (`maxSteps`): a scaffold is many `write_file` steps before
  its first build, and eight was one file short.

**F2** (or `/status`) prints the session's status block: the task's name,
context size and a token estimate, how many times the decision engine was
called and for how long, escalations, designs, tool calls, commands approved,
the workspace memory's size, and which folders are readable. `/new` starts a
fresh session with a new log file.

### A session in the background, driven from the CLI

```bash
agent-one session start --smart -r ./myproject     # one detached process, one pipe
agent-one ask "scaffold a FastAPI hello service"    # the turn, printed as the REPL would
agent-one ask --yes "run the tests"                 # approve commands without asking back
agent-one ask --json "/status"                      # one JSON object: the result event
agent-one session stop
```

`session start` spawns `agent-one` itself, detached, holding one `ChatSession`
behind a local named pipe (`~/.agent-one/session.json` says where). `ask`
connects, sends the request, and prints the turn's events as they happen —
progress, tool steps, the streamed answer, decisions, the design's head — and
is where a question comes back: a command to approve (`y`, or `--yes` up
front) or a design choice to make (a number, Enter for the recommendation).
The conversation, the log and the workspace memory are the same as the
window's. One session at a time; a second `start` is refused while the first
is alive, and a stale record from a crash is cleared.

Two reasons it exists: **chat mode can be self-tested with no terminal** —
`agent-one session selftest` runs server and client in one process over a
private pipe on the echo provider, and the release smoke test runs it on every
artifact — and **another agent can drive this one** from a script, reading
`--json` results or the event lines.

### Long-term memory as a graph

Beside the memory file there is a **knowledge graph** — an embedded
[Kùzu](https://kuzudb.com) database under the workspace folder, queried with
Cypher, the shape borrowed from `akka-graph-loop`'s per-project graph memory.
The file remembers what happened; the graph keeps what was *judged worth
knowing*, and gets better the more it is used.

```
Turn ──LEARNED──▶ Knowledge ──JUSTIFIED_BY──▶ Rationale   (the engine's judgement, attached)
                     │  ──ABOUT──▶ Path                   (the files it concerns)
                     └──HELPED──▶ Turn                     (each later turn it was handed to)
```

**After every turn** the decision engine is asked one fixed question — did
this turn produce knowledge a future session would be glad to have? — and
and unless it answers *skip* with confidence does the everyday model distil it into one to three lines
(`kind | title | text`: fact, decision, fix, procedure, constraint). Each is
stored with the engine's verdict, confidence and the evidence it saw as a
`Rationale` node, linked to the turn and to the paths it names. All of it
off the turn, after the answer is on screen.

**Before a turn** — when the graph holds anything and the route is not the
web — the engine is asked whether the graph can help *this* request, given a
summary of what it holds (counts, the paths it knows most about, the newest
titles). On *consult* it picks one of four queries — by keywords, by the
paths named, newest first, most helpful first — and what comes back reaches
the model as `[graph memory] …` material **before any file is scanned**.
Every item handed over gets a `HELPED` edge and a use count, and the
queries rank by use, so the knowledge that keeps helping rises. Each item
also carries search words in English and in your language, so a question
asked in Korean finds what was learned in English; and when no query finds
anything, the newest few items go to the model anyway — the engine said the
graph helps, and a miss on words is not a no.

```
› 빌드가 되는지 확인해줘
  route: → workspace  (confidence 0.96)
  graph: consulted via by_keywords — 2 item(s)  (confidence 0.81)
    ↳ (procedure) Build command
    ↳ (fix) Missing entry point
  ✓ run_command  (2.4s)
◆ 빌드 성공 …
```

`agent-one memory` shows what the graph holds; `memory query "MATCH (k:Knowledge)-[:ABOUT]->(p:Path) RETURN p.path, k.title"`
runs any Cypher. The graph needs Kùzu's shared library next to the binary
(the build fetches it, the release archive carries it); without it the agent
runs as before and the status block says `graph off`.

### The improvement loop — Plan · Do · Study · Act

Work that is planned runs as a **PDSA cycle**, recorded in the same graph as
the knowledge — Deming's loop, with the third step **Study** ("what did we
learn?") rather than Check ("did it pass?"). The shape comes from
`akka-graph-loop`'s `PdsaWorkflow`; here one turn is one phase and one cycle
spans several turns.

```
Cycle ──HAS_PHASE──▶ Phase ◀──RAN_IN── Turn      (which turn performed which step)
  │  ──NEXT_CYCLE──▶ Cycle                       (the order they ran in)
  │  ──REINFORCES──▶ Cycle                      (this one exists because that one fell short)
  │  ──TAUGHT──▶ Knowledge                       (on closing: what it left behind)
  └──BUILT_ON──▶ Knowledge                       (on closing: what it drew on)
```

**Planning is the door.** A turn the scope question sent to the stronger model
for a design *is* a Plan, so it opens a cycle with no further question. With no
design and no cycle running, the engine is asked which of the four steps the
request is, and only a **confident** `plan` opens one — an unsure guess would
drag the next several turns into a cycle nobody asked for. Everything else
leaves the loop out of the way: "run the build" starts nothing.

**While a cycle runs**, that same question places every turn, and the choice is
followed without the confidence floor — a mislabelled phase costs a row, not an
action. A *new* plan mid-cycle is the exception, because it starts the next
cycle: the running one is abandoned and the new one `REINFORCES` it when its
Study said `partial` or `unmet`.

**Study asks what the plan predicted against what happened** — `met`,
`partial` or `unmet` — and that verdict is the loop's only feedback edge. A
cycle that acted without ever studying closes `unjudged`, not as a success.

**Act closes the cycle onto knowledge**, and does it *after* the turn's
distillation has finished — knowledge is learned off the turn, so closing
first would wire up a cycle whose last lesson is not stored yet.

```
› 새 게시판 API 를 어떻게 구성하면 좋을까?
  route: → workspace   scope: large — qwen3.8-27b designs first
  pdsa:  cycle #3 opened at plan
◆ src/Api 아래에 …

› 좋아, 그대로 만들고 빌드까지 돌려봐
  pdsa:  cycle #3 · do
…
› 테스트 돌려서 계획대로인지 봐줘
  pdsa:  cycle #3 · study → the plan was partial
…
› 되는 데까지 커밋하고 남은 건 적어두자
  pdsa:  cycle #3 · act
    ↳ cycle #3 closed (partial) — taught 2, built on 1
```

`agent-one memory pdsa` prints the cycles with their phases and both knowledge
lists; the status block carries one line
(`pdsa  3 cycles · 9 phases · plan met 1/2 · 5 knowledge edges`). The loop needs
the graph: without Kùzu it is off, like the graph itself.

### The workspace remembers

A session belongs to its workspace (`--root`, default: the current directory),
and the workspace keeps two things under `~/.agent-one/workspaces/<name>-<hash>/`:

- **`memory.md`** — one entry per turn: what was asked, which tools ran, how it
  ended. Capped at 50 000 characters, oldest entries falling off. The newest
  part of it opens every session's system prompt, so a new chat in the same
  folder knows what was built there a minute — or a week — ago.
- **`sessions/`** — this workspace's transcripts. `/resume` lists them, newest
  first, each with its task name and turn count; `/resume 2` picks one up: the
  screen replays it as it was, the model gets its questions and answers back
  as context, and the same file keeps being appended to.

```
› /resume
  ── sessions in this workspace (newest first) · /resume <n> to pick one ──
   1. 09-22 22:55 · 3 turns · 게시판 API 빌드 오류 수정  (this one)
   2. 09-22 22:19 · 6 turns · 게시판 API 만들기
› /resume 2
  ── resumed 20260922-221944-chat · 게시판 API 만들기 ──
› 보드 api를 만들어죠
  route: unsure (answer_directly), all tools stay available · confidence 0.58
  ✓ write_file  (24.3s)
  …
  ── continuing from here ──
```

**The task's name** is made by the model from the request, in the background
as the turn starts — so the header says "게시판 API 개발" seconds in, not
minutes later when a long build ends — and shown in the header, the status
block, the resume list and the memory. A greeting ("안녕", "hi") is not a task
and names nothing. With a TypeSafe key, each new request is first put to the
decision engine as "same task or a new one?" (0.3 s), and the model is only
asked for a new name when the task changed; without one, the task is named
once per session. `run` and the echo provider never name anything.

When input or output is a pipe — or with `--plain` — the same conversation runs
as a line-at-a-time REPL, which is what scripts and tests drive. Both are thin
renderers over one `ChatSession` — as is `run`, a single turn of the same
session — so a turn routes, runs, judges its draft and escalates identically in
all three. That is not an aesthetic choice: three copies of that logic would
drift within a week.

## Smart mode

Off by default. On, a turn asks the decision engine (TypeSafe *Jev*) two
questions with fixed options — no planning LLM call, so each costs about 0.3 s:

```
request (≥ 10 chars)
   │
   ├─ ① route: web · files · answer directly ──> the loop, restricted to that family
   │        (unsure: every tool stays available)
   │
   └─ the everyday model drafts an answer with what the tools found
            │
            ├─ ② escalate?  keep the draft ──────────────────────> answer
            │              escalate ──> the strong model reasons over the
            │                          same material ──> the everyday model
            │                          writes the final answer from it
            └─ (no reasoning model configured: the draft is the answer)
```

**① Route.** "Which resource does this need first?" — search the web, read the
workspace, or answer directly. The chosen family is the *only* one the loop may
use that turn: a call outside it is refused with a message, not run. A small
model treats a suggestion as one option among many; a refusal it understands.
Below the confidence floor nothing is restricted. Requests under ten characters
("hi", "네") skip the engine entirely.

**② Escalate.** After the draft, the engine sees the request, everything the
tools returned, the draft, and **which model wrote it and which one is on
offer** — "is this good enough?" means something different from a 4B model than
from a 27B one. If it says the request needs more, the strong model (step 3 of
the TUI) gets the same material and thinks it through; its answer goes back into
the everyday model's conversation as `[reasoning:<model>] …`, and *that* model
writes the final answer — same voice, same language, borrowed thinking. A
strong model that cannot be reached leaves the draft standing and says so.

```console
[smart] > MSA로 전환할 때 데이터 일관성은 어떻게 보장하지?
(route: → web · confidence 0.91)
… searching the web for "MSA data consistency"
✓ web_search  (1.2s)
✓ web_read  (4.8s)
사가 패턴과 이벤트 소싱으로 …
(escalation: escalating to qwen/qwen3.8-27b · confidence 0.83)
… reasoning with qwen/qwen3.8-27b
✓ reasoning  (24.1s)
MSA에서 데이터 일관성은 세 층위로 나눠 봐야 합니다 …
```

**Shift+Tab** switches basic ↔ smart, and the header (or, in the REPL, the
prompt) says which you are in. With no TypeSafe key the toggle refuses and says
why. `run` has the same modes via `--smart` / `--basic`, and is literally one
turn of the same session.

Every decision is in the session log as a `route` or `escalation` entry with
its choice, confidence and cost. [`docs/AgentLoop.md`](docs/AgentLoop.md) walks
the loop as it runs — the step loop, the turn pipeline with every smart-mode
question in place, and why the confidence floor applies to some of them and not
others; [`docs/smart-mode-jev.md`](docs/smart-mode-jev.md) is the pre-build
review of Jev itself.

`agent-one jev choose` is the bench for it — a decision put to the service by
hand, with the whole distribution shown:

```console
$ agent-one jev choose "The user asked how to build this repository."     -q "How should the agent answer?"     -o read_local="The answer is in files here; read them."     -o search_web="It needs outside information; search the web."     -o answer_now="Enough is already known; just answer."
choice      read_local
confidence  0.910
distribution
  [###################·] 0.940  read_local
  [#···················] 0.060  answer_now
  [····················] 0.000  search_web
(jev-1.13.0 · 306 ms · 376+42 tokens)
```

`--repeat n` reports min/median/max latency, because one measurement includes
connection setup and says almost nothing. Fewer than two options never reaches
the network: there is nothing to decide.

### Keys

| Key | Does |
|---|---|
| `Tab` / `n` / `PageDown` | Next step · `Shift+Tab` / `b` / `PageUp` / `Esc` — previous step |
| `↑` `↓` (`k` `j`), `Home` `End` | Move within the step. The list wraps. |
| `Enter` | Edit the selected value, prefilled. On step 2, take the highlighted model. |
| `←` `→` | Cycle a value with a fixed set (`provider`, `saveSessions`). |
| `e` | Step 2 only: type a model id by hand. |
| `l` | Ask the endpoint for its models again, from any step. |
| **`t`** | **Send one tiny request through the settings as they stand** and report latency and reply, or the exact failure. |
| `s` | Save — config to `config.json`, a newly typed key to `credentials.json` · `r` reload from disk · `d` restore defaults |
| `q` | Quit. With unsaved changes it asks once; any other key disarms the confirmation. |

`Esc` goes back a step and only quits from the first one — it is a stack, so
`Esc` means "back", not "lose my work".

A rejected value keeps you in edit mode with what you typed, so you fix it
instead of retyping. The TUI refuses to start when stdin or stdout is
redirected — a full-screen UI in a pipe renders escape sequences into a log file
and then waits for a key that cannot arrive. Scripts use `agent-one config set`.

The same listing is on the command line, for scripts and for a health check
without opening a screen:

```console
$ agent-one models
  google/gemma-4-e4b
* text-embedding-nomic-embed-text-v1.5
(2 models from http://localhost:1234/v1/models)

$ agent-one models --base-url https://api.openai.com/v1
agent-one models: HTTP 401 — no API key: $OPENAI_API_KEY is not set
agent-one models: check baseUrl (https://api.openai.com/v1) and $OPENAI_API_KEY
$ echo $?
1
```

The count goes to stderr so the list pipes cleanly:
`agent-one models | fzf | xargs agent-one config set model`.

**Implementation** — the rules live in `Tui/ConfigTuiModel.cs`, a state machine
that takes `ConsoleKeyInfo` and holds no terminal, so the steps and the key map
are unit tested; `ConfigTuiPage` only projects it. The framework is
[Termina](https://github.com/Aaronontheweb/termina), chosen because it is the one
TUI measured to survive Native AOT here (`Docs/agent-netclaw/README.md` records
the measurement). `agent-one setup --selftest` drives the real screen from a
scripted key source and checks where it landed — that is what CI runs against
every release artifact, since a machine with no terminal cannot press keys.

Step navigation and the picker are checked below the UI in that selftest, on
purpose: arriving at step 2 starts an asynchronous listing and the screen
ignores keys while it is in flight, so a scripted walk would race the request
and fail at random rather than when something is broken.

The same selftest then boots the chat window against the echo provider, types
a 300-word line, submits it and presses PageUp — checking that the window ends
up reporting "scrolled up". Every key is queued before the window starts. That
is deliberate: a key pushed into an *idle* virtual queue is forwarded by a
continuation that, under Native AOT, runs only when the loop next wakes for
something else — 2–9 seconds of nothing, measured. Real console keys take a
different path and arrive in under 100 ms (also measured, with SendKeys against
the published binary), so users never see it; the selftest simply never lets
the queue go idle. It works because the echo turn completes inside the Enter
keystroke, so PageUp finds the answer already in the transcript.

## How it works

The conversation runs as the same two actors AgentZero's Bot mode uses —
Akka.NET, a 1.6 nightly, inside the AOT binary:

```
 REPL · window · run · pipe server
        │  StartAgentLoop / CancelAgentLoop / ResolvePause / session commands
        ▼
 /user/bot        AgentBotActor   — the gateway: spawns the loop lazily, one turn
        │                           at a time, hands every event to the renderer's
        │                           callbacks in order
        ▼
 /user/bot/loop   AgentLoopActor  — the agent: owns one ChatSession, Idle ⇄ Running,
        │                           the turn on the pool, exactly one AgentLoopResult
        ▼                           per StartAgentLoop (cancelled or not)
     ChatSession  — the turn: smart routing, the graph, the loop, the gate, the memory
```

`AgentLoopProgress` (Thinking / Acting / Generating / Done / Error) is a phase
tick, `AgentLoopResult` the end of a run, `AgentLoopNotice` the side channel
(decisions, titles, designs, what the graph learned), `PersonNeeded` /
`ResolvePause` the pause for a person. `Actors/AgentGateway` wraps the pair in
the same events-and-delegates surface `ChatSession` has, which is what the
renderers hold. Inside the session, the turn is:

```
 prompt ──> AgentLoop ──> IChatProvider ──> model
              │  ▲                            │
              │  └──── [tool:<name>] result ──┘
              ▼
          CompositeToolbelt
             ├── files  LocalFileToolbelt, sandboxed to --root (+ read grants)
             ├── edit   the same belt: write_file, root only
             ├── web    WebToolbelt, read-only GETs
             └── exec   ShellToolbelt: PowerShell / bash in --root, behind the gate
```

### Tools

Six read-only verbs, and two that change things — each of those behind its own
gate: the path sandbox for `write_file`, the command gate for `run_command`. A
test keeps every verb that writes or runs inside those two families.

| Verb | Does |
|---|---|
| `list_files(path)` | List a directory. Skips `.git`, `bin`, `obj`, `node_modules` and friends. |
| `read_file(path)` | Read a UTF-8 file, truncated at 64 KB. |
| `find_files(pattern, path)` | Find by name pattern (`*.cs`) anywhere below a path. |
| `grep(text, path, glob)` | Which files contain a piece of text, with line numbers. Case-insensitive **plain text, not a regex** — the pattern comes from a model, and a regex from an untrusted source is a way to hang the process, not a feature. |
| `web_search(query, count)` | Search the web. Titles, URLs and snippets. |
| `web_read(url)` | Fetch one page and return its readable text, truncated at 24 000 characters. |
| `write_file(path, content)` | Create or overwrite a file **under the root only**, folders created as needed. Always the whole file: a partial-edit verb needs the model to quote the old text exactly, which small models get wrong. |
| `run_command(command)` | One shell command in the root — PowerShell on Windows, bash (or sh) elsewhere — output and exit code back, killed past `commandTimeoutSeconds`. Runs only when the gate says so. |

The file verbs all resolve paths against `--root` and refuse anything that lands
outside it. `grep` skips files over 2 MB and anything containing a NUL byte, and
stops at 100 matches.

`web_search` uses DuckDuckGo's HTML endpoint: no API key, no account, works the
moment agent-one is installed. The cost is that it parses someone else's markup,
so a layout change degrades to "no results parsed" — never to wrong results —
and the message says so. Search returns a menu, not an answer; the prompt tells
the model to `web_read` a page before claiming what it says.

**Web text is the least trustworthy input in the system.** It is written by
strangers and may carry instructions aimed at the model. It comes back as data
under a header naming its source, and the system prompt says a web page is to be
quoted and reasoned about, never obeyed. That is one defence, and it is the only
one — which is exactly why no verb here can act on what a page says.

The model answers with **one JSON envelope per turn** and nothing else:

```json
{"tool":"read_file","args":{"path":"README.md"}}
{"tool":"final","args":{"text":"It is a CLI agent."}}
```

One shape for both a tool call and the final answer means the loop has exactly
one thing to parse, and a small model has one format to learn. The parser is
deliberately tolerant — fenced JSON, a "Sure!" preamble and numeric argument
values all still parse — because none of that is worth failing a run over.

Three guards end a run that is going nowhere: the **step budget**
(`--max-steps`), the **repeat guard** (the same call twice gets one corrective
nudge, then stops), and the **parse budget** (two unparseable replies get a
nudge each). Every stop is reported with its reason rather than a silent hang.

### Layout

| Path | What lives there |
|---|---|
| `Program.cs` | argv routing — a plain switch, no parser library, so the AOT binary carries no reflection-based command binding |
| `Commands/` | one class per verb, plus the shared flag parser (`AgentOptions`) |
| `Actors/` | the Bot / Loop actor pair, its message vocabulary, the gateway the renderers hold, the actor system's HOCON |
| `Agent/` | the loop, the envelope (`ToolCall`), the guards, the system prompt, the session (`ChatSession`) |
| `Llm/` | `IChatProvider`, the echo provider, the OpenAI-compatible client |
| `Tools/` | `ToolCatalog` (what the model is told), the belts that run it, and `Tools/Web/` (fetch, search parsing, HTML→text) |
| `Tui/` | the settings screen — testable model, Termina page/viewmodel, host wiring |
| `Services/` | `~/.agent-one/` paths, config, session JSONL, JSON source-gen contexts |
| `packaging/npm/` | the npm wrapper that downloads a release binary |

`ToolCatalog` is the single source of truth: the system prompt is generated from
it, `agent-one tools list` prints it, each spec names the **family** that owns
the verb, and `CompositeToolbelt` routes by that family. Tests assert every
family has a belt and every verb is reachable — so adding a verb in one place and
forgetting the others fails the build rather than confusing the model at runtime.

Adding a capability is therefore: one `ToolSpec` in the catalog, one `case` in a
belt (or a whole new `IToolbelt` for a new family), and the tests tell you if you
stopped halfway.

## Where it keeps things

All under `~/.agent-one/` — never in the working directory:

```
~/.agent-one/
  config.json              settings — no secrets, safe to share
  credentials.json         the API key, alone, user-only where the OS allows it
  sessions/*.jsonl         one line per prompt / tool step / result
  logs/
```

The session file is the record of what the agent actually did, which is how you
tell a real answer from a confident one:

```console
$ jq -c '{kind,tool,text}' ~/.agent-one/sessions/*-chat.jsonl
{"kind":"prompt","tool":null,"text":"hi"}
{"kind":"step","tool":"list_files","text":"path=. -> 422 chars"}
{"kind":"step","tool":"read_file","text":"path=README.md -> 60120 chars"}
{"kind":"step","tool":"final","text":"AgentZero Lite is a desktop shell…"}
```

It is plain JSONL — UTF-8, no BOM, one object per line — so `jq`, `json.loads`
and log shippers read it directly.

`AGENT_ONE_HOME` relocates the whole tree (tests and CI use it).

Two files rather than one because they have different risk: `config.json` is the
thing you would paste into an issue, and a secret must not ride along.

## Safety boundary

**Writes stay under `--root`.** Paths are resolved to their real target before
the containment check, so `../`, an absolute path and a symlink pointing outside
all get the same refusal. Reading is the one thing that can widen: a folder the
user names by its absolute path is granted for reading (and only reading) until
the session ends — naming it is the approval.

**Commands go through a gate**, in this order: a static list of patterns that
are dangerous whatever anyone says (`Agent/CommandRisk`: recursive deletes
aimed outside the project, `sudo`, `format`, registry edits, piped installers,
destructive git, scheduled tasks…) always asks a person; otherwise the decision
engine's safety question runs the command only on a *confident* "safe"; unsafe,
unsure, no engine and no key all ask. Who answers depends on the front: the
REPL reads a line, the window takes the next line typed, `run` says no unless
started with `--yes`. A declined command is reported to the model as declined,
with the reason, so it does not try it again.

File contents reach the model as `[tool:<name>] …` user messages, and the system
prompt says in as many words that tool output is data, not instructions — a file
that contains "ignore your instructions" is just a file that says that.

## Build & test

```bash
dotnet build Project/AgentOne/AgentOne.csproj -c Debug
dotnet test  Project/AgentOne.Tests/AgentOne.Tests.csproj
Project/AgentOne/bin/Debug/net10.0/agent-one --version
```

### Running it from the source tree

`agent-one.ps1` is the development shortcut: it builds the Debug binary if it
is missing or older than the sources, then hands every argument straight to it
and returns its exit code.

```powershell
.\Project\AgentOne\agent-one.ps1 run "hello" --provider echo
.\Project\AgentOne\agent-one.ps1 tui
.\Project\AgentOne\agent-one.ps1 -Rebuild tools list    # build first, always
.\Project\AgentOne\agent-one.ps1 -NoBuild --version     # never build
```

Worth knowing:

- It works from any directory, and the agent's workspace root stays **your**
  current directory — not the script's — so `run`/`chat` see the folder you are
  actually in.
- It pins the version from `version.txt` (`SkipAutoBumpVersion`) so running the
  wrapper never dirties a tracked file the way a plain `dotnet build` does.
- It declares no PowerShell parameters. agent-one's own flags include `-r`, `-p`,
  `-m` and `-v`, and PowerShell binds those to any parameter starting with the
  same letter before the binary would ever see them — so the script reads `$args`
  raw and lifts out only `-Rebuild` / `-NoBuild`.
- It invokes the binary directly rather than through `Start-Process`, because the
  TUI needs the real console.

Handy once, then short forever:

```powershell
Set-Alias a1 C:\code\psmon\AgentZeroLite\Project\AgentOne\agent-one.ps1
a1 tui
```

Native AOT single binary (about 22 MB on win-x64 with Akka.NET and the Kùzu loader inside, no runtime dependency):

```bash
dotnet publish Project/AgentOne/AgentOne.csproj -c Release -r win-x64   -o out/win-x64
dotnet publish Project/AgentOne/AgentOne.csproj -c Release -r linux-x64 -o out/linux-x64
dotnet publish Project/AgentOne/AgentOne.csproj -c Release -r osx-arm64 -o out/osx-arm64
```

On Windows the AOT link step needs the MSVC toolchain; run it from a Developer
prompt, or put `C:\Program Files (x86)\Microsoft Visual Studio\Installer` on
`PATH` so ILCompiler can find `vswhere.exe`. On Linux it needs `clang` and
`zlib1g-dev`.

Two AOT rules this project holds itself to, both enforced at build time:

- every serialized type is declared in `AgentOneJson` (config) or
  `AgentOneWireJson` (wire, JSONL, `--json`). `JsonSerializerIsReflectionEnabledByDefault`
  is **off**, so a reflection-based call is an IL2026/IL3050 warning during
  `dotnet build`, not a crash in the published binary;
- `InvariantGlobalization` is on, so the linux-x64 binary also runs on musl /
  Alpine images with no ICU installed.

`version.txt` is auto-bumped on every local build (patch +1, rollover at 100).
CI passes `-p:SkipAutoBumpVersion=true -p:Version=<tag>` so a release keeps the
tag's version.

## Release & npm

Tagging `agent-one-v0.1.0` runs `.github/workflows/agent-one-release.yml`: it
runs the tests, builds the four RIDs (win-x64, linux-x64, osx-arm64, osx-x64),
smoke-tests each artifact, publishes the GitHub Release with `checksums.txt`,
and publishes the npm wrapper stamped with the same version.

```bash
npm install -g @webnori/agent-one
```

The npm package carries no binary. Its `postinstall` downloads the archive for
the current OS/arch from that release, verifies the SHA256 against
`checksums.txt`, and unpacks it next to the launcher — so the npm version and
the release tag can never drift. `AGENT_ONE_SKIP_DOWNLOAD=1` opts out for
offline installs.

## Relationship to AgentZero Lite

Same repository, no code dependency in either direction. AgentZero Lite's own
agent loop lives in `ZeroCommon` and is bound to EF Core, LLamaSharp and ONNX —
none of which survives Native AOT or a cross-platform single binary, and all of
which a small CLI has no use for. agent-one therefore reimplements the small
part it needs and stays free to be extracted into its own repository once it
ships on npm.

What the two do share is the **shape**: the same `AgentBotActor` / `AgentLoopActor`
split, the same message names (`StartAgentLoop`, `AgentLoopProgress`,
`AgentLoopResult`, `CancelAgentLoop`, `ResetAgentLoopMemory`,
`SetAgentLoopCallbacks`), the same rules — the bot never runs inference, the
loop is only ever Idle or Running, a cancel is a token cancel and the run's own
end tips the actor back. agent-one runs them on its own Akka.NET (a 1.6
nightly; 1.6 is not on nuget.org yet, so `NuGet.config` adds Akka.NET's feed).
Akka finds its provider and dispatchers by type name from HOCON, which the
trimmer cannot see — measured, the AOT binary died in `ActorSystem.Create`
without a `TrimmerRootAssembly` for Akka, and answers a thousand Asks in 2 ms
with one. The price is about 12 MB of binary.

The intended integration is process-level: AgentZero launches `agent-one` with
`--json` and reads one object off stdout, the same way it launches
`AgentZeroWearable.exe` today.
