# AgentZero Lite — agent working guide

This repository ships a Claude-oriented skill library and the **Kakashi
harness**.  They are project assets, not Claude-only knowledge: use their
instructions when their trigger matches the task.

## Start here

1. Read [`CLAUDE.md`](CLAUDE.md) for the authoritative project conventions,
   architecture, build/test commands, and safety constraints.
2. Before performing a task that matches a project skill, read that skill's
   `SKILL.md` in full and follow its linked references as needed.  Claude
   discovers these automatically; other agents must discover them manually.
3. The live executable and its `-cli help` output are authoritative for CLI
   capabilities.  Do not rely on stale command lists.

## Project skill routing

| Task / trigger | Read first |
| --- | --- |
| Build, diagnose, or change the .NET/WPF solution | `.claude/skills/agent-zero-build/SKILL.md` |
| Drive AgentZero Lite, terminal tabs, peer-agent conversation, worktrees, orchestration, automations, cost, or native Windows control | `.claude/skills/agentzero-cli/SKILL.md` |
| Browser UI / E2E tests or requested Playwright captures | `.claude/skills/playwright-e2e/SKILL.md` |
| Build or update the harness viewer | `.claude/skills/harness-view-build/SKILL.md` |
| Pencil `.pen` design work, design export, or the bundled image-generation flow | `.claude/skills/pencil-design/SKILL.md` |

The skill directory may grow; list `.claude/skills/*/SKILL.md` before assuming
the table is exhaustive.  Do not read encrypted `.pen` files as text; use the
Pencil workflow specified by the design skill.

## Kakashi harness

`harness/` is the repository's reusable execution and review framework.
For a request mentioning **harness**, **카카시**, a mission such as `M0042`,
or a whole/change review:

1. Read `harness/docs/README.md` and the relevant specialist/workflow under
   `harness/agents/`, `harness/engine/`, and `harness/knowledge/`.
2. For a mission, read `harness/missions/README.md` and then the requested
   `harness/missions/MNNNN-*.md` brief before acting.
3. Honor the mission lifecycle and acceptance criteria.  If completing a
   mission, update its status and write the requested Korean/English result
   record under `harness/logs/mission-records/` as the mission protocol says.

Use the harness as process guidance and durable project memory; do not invent
mission records or mutate harness artifacts for ordinary code changes unless
the task explicitly invokes the harness or mission workflow.

## Core guardrails

- Keep `ZeroCommon` WPF/Win32-free; dependencies flow
  `AgentZeroWpf → ZeroCommon`, never the reverse.
- Prefer the headless `ZeroCommon.Tests` suite when it covers the change;
  WPF-dependent tests require a desktop session.
- On Windows, use the AgentZero CLI wrapper or `Start-Process -NoNewWindow
  -Wait` for the GUI-subsystem executable, as documented by `agentzero-cli`.
- If the request refers to AgentWin / the origin project, read
  `Docs/agent-origin/README.md` before inspecting the ancestor source.
