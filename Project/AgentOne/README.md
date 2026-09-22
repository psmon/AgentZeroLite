# agent-one

A standalone CLI agent. Ask it something, it reads your workspace through a small
set of tools and answers — on Windows, macOS and Linux, from one native binary
with no .NET runtime to install.

```bash
agent-one run "what does this project do?"
agent-one chat
```

It is **independent**: `Project/AgentOne` references nothing else in this
repository. AgentZero Lite may drive it as a child process, but agent-one does
not need AgentZero, a GUI, or Windows to run.

---

## Quick start

No API key, fully offline — this is the smoke test:

```bash
agent-one run "hello" --provider echo
agent-one tools prompt          # exactly what the model is told
```

Or set it up on a screen and test the connection without leaving it:

```bash
agent-one tui
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
| `agent-one chat` | Interactive session; the conversation carries over. `/reset`, `/exit`. |
| `agent-one config` | `show` / `get` / `set` / `path` / `reset` over `~/.agent-one/config.json`. |
| `agent-one tui` | Full-screen settings editor (`agent-one config tui` is the same screen). |
| `agent-one models` | List what the configured endpoint can run (`*` marks the configured one). Exit 1 if it refuses or lists nothing. |
| `agent-one auth` | `show` / `set` / `check` / `clear` / `import`. `--jev` addresses the TypeSafe key. |
| `agent-one jev` | `check` / `choose` — put a decision to TypeSafe and see the distribution. |
| `agent-one tools` | `list` / `show <name>` / `prompt`. |
| `agent-one home` | Where agent-one keeps its files. |

Shared flags for `run` and `chat` — each one overrides the stored config for
that invocation only:

```
-r, --root <dir>      Workspace the tools may read (default: cwd)
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

`agent-one tui` (or `agent-one config tui`) walks the settings as a three-step
stack, in the order they actually depend on each other:

```
1. Connection  →  2. Model  →  3. Options
```

You cannot sensibly pick a model before the endpoint and key are right, and the
endpoint is the thing that knows which models exist — so step 2 asks it.

### 1. Connection — where and who

```
╭─ agent-one config ──────────────────────────────────────────╮
│[1. Connection] →  2. Model     →  3. Options                │
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
│ 1. Connection → [2. Model]     →  3. Options                │
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

### 3. Options — how the loop behaves

`maxSteps`, `temperature`, `timeoutSeconds`, `saveSessions`. All have working
defaults, which is why they come last.

### 4. Smart — the decision service (setup only, so far)

```
╭─ agent-one config ──────────────────────────────────────────╮
│ 1. Connection →  2. Model  →  3. Options  → [4. Smart]      │
│                                                             │
│› jevApiKey       ts-abc…w9k2                                │
│  jevBaseUrl      https://api.typesafe.ai/v1                 │
│  jevModel        jev-latest                                 │
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

**Smart mode itself is not built yet.** This step only stores and verifies what
it will need, which is why there is no on/off switch here — a switch that does
nothing is a lie. The design is in
[`docs/smart-mode-jev.md`](docs/smart-mode-jev.md).

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
the measurement). `agent-one tui --selftest` drives the real screen from a
scripted key source and checks where it landed — that is what CI runs against
every release artifact, since a machine with no terminal cannot press keys.

Step navigation and the picker are checked below the UI in that selftest, on
purpose: arriving at step 2 starts an asynchronous listing and the screen
ignores keys while it is in flight, so a scripted walk would race the request
and fail at random rather than when something is broken.

## How it works

```
 prompt ──> AgentLoop ──> IChatProvider ──> model
              │  ▲                            │
              │  └──── [tool:<name>] result ──┘
              ▼
          CompositeToolbelt
             ├── files  LocalFileToolbelt, sandboxed to --root
             └── web    WebToolbelt, read-only GETs
```

### Tools

All read-only. Nothing here writes a file or runs a command, which is why there
is no approval gate yet — adding a verb that changes something is the point at
which one has to exist first, and a test fails if such a verb appears in the
catalog.

| Verb | Does |
|---|---|
| `list_files(path)` | List a directory. Skips `.git`, `bin`, `obj`, `node_modules` and friends. |
| `read_file(path)` | Read a UTF-8 file, truncated at 64 KB. |
| `find_files(pattern, path)` | Find by name pattern (`*.cs`) anywhere below a path. |
| `grep(text, path, glob)` | Which files contain a piece of text, with line numbers. Case-insensitive **plain text, not a regex** — the pattern comes from a model, and a regex from an untrusted source is a way to hang the process, not a feature. |
| `web_search(query, count)` | Search the web. Titles, URLs and snippets. |
| `web_read(url)` | Fetch one page and return its readable text, truncated at 24 000 characters. |

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
| `Agent/` | the loop, the envelope (`ToolCall`), the guards, the system prompt |
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

v0 ships **read-only** tools — `list_files` and `read_file` — scoped to
`--root` (default: the current directory). Paths are resolved to their real
target before the containment check, so `../`, an absolute path and a symlink
pointing outside all get the same refusal. There is no shell verb and no write
verb yet; widening the surface later is additive, and the sandbox is small
enough to reason about while the rest is being proven.

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

Native AOT single binary (8.3 MB on win-x64, no runtime dependency):

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
agent loop lives in `ZeroCommon` and is bound to Akka, EF Core, LLamaSharp and
ONNX — none of which survives Native AOT or a cross-platform single binary, and
all of which a small CLI has no use for. agent-one therefore reimplements the
small part it needs and stays free to be extracted into its own repository once
it ships on npm.

The intended integration is process-level: AgentZero launches `agent-one` with
`--json` and reads one object off stdout, the same way it launches
`AgentZeroWearable.exe` today.
