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
agent-one config set model gpt-4o-mini
export OPENAI_API_KEY=sk-...            # Windows: setx OPENAI_API_KEY sk-...
agent-one run "summarize the README" -v
```

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
```

Exit codes: `0` answered, `1` stopped early (budget, repeat, parse, provider),
`2` usage error, `130` cancelled. `--json` makes the outcome machine-readable:

```console
$ agent-one run "hi" -p echo --json
{"ok":true,"stopReason":"Final","text":"hi","steps":1,"elapsedMs":8,"provider":"echo","model":"gpt-4o-mini","session":"/home/me/.agent-one/sessions/20260922-120005-run.jsonl"}
```

## Settings TUI

`agent-one tui` (or `agent-one config tui`) opens the settings as a screen:

```
╭ agent-one config ──────────────────────────────────────────╮
│› provider        openai  ←→                                │
│  baseUrl         http://localhost:11434/v1                 │
│  model           qwen2.5-coder:7b                          │
│  apiKeyEnv       OPENAI_API_KEY                            │
│  maxSteps        8                                         │
│  temperature     0.2                                       │
│  timeoutSeconds  120                                       │
│  saveSessions    true                                      │
╰────────────────────────────────────────────────────────────╯
 echo runs offline and exercises the real loop · openai talks to any…
 ✓ openai · qwen2.5-coder:7b · 412 ms · replied: ok
 ↑↓ move · Enter edit · ←→ cycle · s save · r reload · d defaults · t test · q quit
```

| Key | Does |
|---|---|
| `↑` `↓` (`k` `j`), `Home` `End` | Move. The list wraps. |
| `Enter` | Edit the selected value, prefilled. `Enter` accepts, `Esc` cancels. A rejected value keeps you in edit mode with what you typed, so you fix it instead of retyping. |
| `←` `→` | Cycle a value that has a fixed set (`provider`, `saveSessions`). |
| `s` | Save to `~/.agent-one/config.json`. |
| `r` | Reload from disk, discarding edits. |
| `d` | Restore defaults in memory — still needs `s`. |
| **`t`** | **Send one tiny request through the settings as they stand** and report what came back: latency and reply, or the exact failure. |
| `q` / `Esc` | Quit. With unsaved changes it asks once; any other key disarms the confirmation. |

`t` is the reason the screen exists: changing an endpoint and finding out whether
it answers should not need a second command.

The TUI refuses to start when stdin or stdout is redirected — a full-screen UI in
a pipe renders escape sequences into a log file and then waits for a key that
cannot arrive. Scripts use `agent-one config set` instead.

**Implementation** — the rules live in `Tui/ConfigTuiModel.cs`, a state machine
that takes `ConsoleKeyInfo` and holds no terminal, so the key map itself is unit
tested; `ConfigTuiPage` only projects it. The framework is
[Termina](https://github.com/Aaronontheweb/termina), chosen because it is the one
TUI measured to survive Native AOT here (`Docs/agent-netclaw/README.md` records
the measurement). `agent-one tui --selftest` drives the real screen from a
scripted key source and checks where it landed — that is what CI runs against
every release artifact, since a machine with no terminal cannot press keys.

## How it works

```
 prompt ──> AgentLoop ──> IChatProvider ──> model
              │  ▲                            │
              │  └──── [tool:<name>] result ──┘
              ▼
          IToolbelt (LocalFileToolbelt, sandboxed to --root)
```

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
| `Tools/` | `ToolCatalog` (what the model is told) and `LocalFileToolbelt` (what actually runs) |
| `Tui/` | the settings screen — testable model, Termina page/viewmodel, host wiring |
| `Services/` | `~/.agent-one/` paths, config, session JSONL, JSON source-gen contexts |
| `packaging/npm/` | the npm wrapper that downloads a release binary |

`ToolCatalog` is the single source of truth: the system prompt is generated from
it, `agent-one tools list` prints it, and a test asserts the toolbelt handles
every verb in it. Adding a verb in one place and forgetting the others fails the
build's tests rather than confusing the model at runtime.

## Where it keeps things

All under `~/.agent-one/` — never in the working directory:

```
~/.agent-one/
  config.json              settings (the API key is NOT here)
  sessions/*.jsonl         one line per prompt / tool step / result
  logs/
```

`AGENT_ONE_HOME` relocates the whole tree (tests and CI use it).

The API key is never written to disk: `apiKeyEnv` names the environment variable
to read it from (default `OPENAI_API_KEY`).

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
