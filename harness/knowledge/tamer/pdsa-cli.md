# PDSA CLI (`@webnori/pdsa`) — graph-memory improvement loop

> Status: binding. Installed globally via `npm i -g @webnori/pdsa` (v0.0.5).
> Owner: tamer; any specialist may drive a cycle as a secondary reader.
> Engine: none — this is a tamer-owned single-agent capability (knowledge + agent only).

## Why this knowledge file exists

`@webnori/pdsa` is a standalone CLI (a .NET Native AOT single binary) that
coaches an AI agent through **Deming's PDSA loop — Plan → Do → Study → Act —**
and accumulates every step into a **per-project Kùzu graph DB** ("long-term
memory for AI agents"). In `plan` an LLM sets a *verifiable expected outcome*
(hypothesis + metrics); in `study` the same LLM judges the result vs. that
expectation (**met / partial / unmet**); in `act` it derives the next
improvement action and, when reinforcement is needed, the next `plan`
auto-links a **reinforcement cycle** (`REINFORCES` edge). The more cycles you
run, the more that project's process learning builds up.

This file is the single source of truth tamer (and any specialist) reads before
driving the loop: which verbs exist, how auth works, how the graph memory
behaves, and where the traps are.

### ⚠️ Not the same as "harness-view PDSA"

The harness already uses the word "PDSA" for a **different** thing. Always
disambiguate in docs and reports:

| | **PDSA CLI** (this file) | **harness-view PDSA** |
|---|---|---|
| What | `@webnori/pdsa` improvement-loop tool | `Home/harness-view/data/pdsa-insight.json` |
| Nature | Live cycles + graph memory + LLM verdicts | Static display data (read-only) |
| Producer | The CLI, driven by an agent | `harness-view-build` skill "PDSA UPDATE" mode |
| Storage | Kùzu graph DB (LocalAppData, per project) | One JSON file in the repo |
| Purpose | Accumulate process learning, test hypotheses | Summarize the newest 5 logs for the dashboard |

They are unrelated mechanisms that happen to share the "Plan-Do-Study-Act"
name. Do not refresh one thinking you touched the other.

## Invocation (decide once)

From any shell where the global install is on PATH, use `pdsa` directly
(`pdsa version` confirms). The CLI is **stateless** — each call runs and exits;
state lives entirely in the per-project graph DB. Pin language per-call with
`--lang en|ko` or the `PDSA_LANG` env var (help + recorded coaching follow it).

## Surface map

| CLI verb | Purpose | Side-effect | Mutates state? |
|---|---|---|---|
| `pdsa project set/list/show/clear <name>` | Select/list the active project (separate DBs) | writes global current-project pointer | yes (pointer) |
| `pdsa plan "<what/why/how>"` | Start a cycle; LLM sets **[Hypothesis]** + **[Metrics]** | LLM call + graph write | yes (new cycle) |
| `pdsa do "<what you did>"` | Organize Plan→Do; **[Plan→Do summary]** | LLM call + graph write | yes |
| `pdsa study "<results/metrics>"` | Judge vs. expected (met/partial/unmet); **[Learnings & improvements]** | LLM call + graph write | yes (verdict) |
| `pdsa act [--note "…"] [--fresh]` | Next improvement action; ends cycle; may auto-link `REINFORCES` | LLM call + graph write | yes (closes cycle) |
| `pdsa status` | Progress + accumulated state for current project | read-only | no |
| `pdsa eval` | Per-cycle expected/verdict/actual + expectation hit-rate (recall) | read-only | no |
| `pdsa view` | Local web viewer of the accumulated graph | opens local port | no |
| `pdsa guide "<q>"` | One-off PDSA guidance from the LLM | LLM call, not recorded | no |
| `pdsa run` | Run the loop via Akka streams + record to graph | LLM calls + graph writes | yes |
| `pdsa config …` | key / model / provider / auth / lang / oauth | writes global config | yes (config) |
| `pdsa check` | Verify LLM connection (real round-trip) | LLM call | no |
| `pdsa models [--filter S]` | List supported models | read-only | no |
| `pdsa version` | Version + runtime info | read-only | no |
| `pdsa init --lang en\|ko` | Install `.claude/skills/pdsa/SKILL.md` | writes a skill file | (not used here — see anti-patterns) |

`--project <name>` on any command runs that single call against that project's
DB independently of the global pointer — the way to drive multiple projects
concurrently without `project set` overwriting each other.

## One closed cycle (repo context)

Active project for this repo is **`agentzero-lite`**
(DB: `%LOCALAPPDATA%\pdsa-cli\agentzero-lite\graph.kuzu`).

```bash
pdsa project set agentzero-lite            # once per repo (already done)
pdsa plan  "what & why & how"              # → read [Hypothesis] + [Metrics]; do the work toward it
pdsa do    "what you actually did"         # → check [Plan→Do summary] for gaps
pdsa study "result numbers & observations" # → LLM verdict met|partial|unmet + [Learnings & improvements]
pdsa act   --note "memo"                   # → [Next improvement action]; carry it into the next plan
pdsa status                                # progress + hit-rate
pdsa eval                                  # per-cycle expected/verdict/actual + recall
```

Only **one in-progress cycle** is tracked per project. To run parallel flows,
split by role with `<project>-<role>` project names driven via `--project`
(e.g. `agentzero-lite-audio`, `agentzero-lite-security`).

## LLM provider / auth

Judging + coaching need an LLM; without one, inputs are still **recorded** to
the graph (only the verdict/coaching is skipped). Current machine config
(`pdsa config show`): `auth=apikey`, `model=gpt-5.6-terra`,
`base_url=https://api.openai.com/v1`, key in
`%LOCALAPPDATA%\pdsa-cli\openai.json`. `pdsa check` confirmed a live round-trip.

Alternatives (set via `pdsa config`):
- **② keyless local / OpenAI-compatible** — `provider local` (ollama/vLLM/LM
  Studio at `http://localhost:11434/v1`) or `provider openai-compat <URL>`.
  `allow-insecure-no-auth true` only for a *remote* no-auth endpoint (explicit opt-in).
- **⑤ `auth claude-cli`** — reuse the already-logged-in Claude via `claude -p`,
  no API key. Convenient but see anti-pattern (d).

Load priority: env vars → global config → repo `.secret/openai.json`.

## Graph memory model

- **Per-project DB** in LocalAppData; projects never conflict (stateless CLI).
- **Verdict colors** in `pdsa view`: met=green, partial=orange, unmet=red.
- **`REINFORCES` edges** link a reinforcement cycle to the one it follows up.
- **Recall** = expectation hit-rate = `met / cycles-with-a-verdict`, shown in
  `status`, `eval`, and the viewer badge.

## Anti-patterns (do NOT do)

- **(a) Confusing it with harness-view PDSA.** They are different mechanisms
  (see the table above). Never edit `pdsa-insight.json` expecting it to change
  the graph memory, or vice-versa.
- **(b) Spamming cycles.** One cycle per *meaningful* improvement unit (a
  mission, a release gate, a real RCA), not per trivial edit — otherwise recall
  becomes noise and the graph loses signal.
- **(c) Abusing `allow-insecure-no-auth`.** Only for a trusted local/compat
  endpoint you control. Never point it at an arbitrary remote host.
- **(d) `claude-cli` for bulk/automated runs.** `claude -p` is not the official
  API path; it spawns the agent CLI as a subprocess with startup latency and
  token-inefficient internal context, burning subscription credits faster (the
  tool's own README warns this). For automation prefer an API key (①).
- **(e) Keeping `pdsa init`'s skill in the repo.** `init` writes
  `.claude/skills/pdsa/SKILL.md` — a *tip* for driving the CLI as a standalone
  Claude skill. This harness deliberately integrates PDSA through **this
  knowledge file + tamer**, not a parallel skill. If `init` is run for
  reference, delete `.claude/skills/pdsa/` afterward.

## Cross-references

- npm package: `C:\Users\psmon\AppData\Roaming\npm\node_modules\@webnori\pdsa`
  (`README.md` / `README-ko.md`).
- Repo · full docs (EN/KO): `https://github.com/psmon/akka-graph-loop`
  (background: `PDSA.md` / `PDSA-ko.md`).
- Active DB: `%LOCALAPPDATA%\pdsa-cli\agentzero-lite\graph.kuzu`.
- Global config: `%LOCALAPPDATA%\pdsa-cli\openai.json`.
- Agent that drives it: `harness/agents/tamer.md` ("PDSA 개선 사이클" 절차 + triggers).
- Not to be confused with: `Home/harness-view/data/pdsa-insight.json`
  (owned by the `harness-view-build` skill's "PDSA UPDATE" mode).
