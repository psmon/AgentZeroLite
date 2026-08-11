# AgentZero Lite — E2E test suite

Reusable end-to-end checks for the WPF app, driven from PowerShell. Verifies the
CLI surface functionally and the GUI visually, combining **CLI state checks** with
**window control + screenshots** (via the app's own `-cli os` verbs).

Created to validate the `feat/orca` ADE features (W1–W9); designed to be reused
for future work.

## Strategy — 3 tiers

| Tier | Script | Needs GUI | What it proves |
|------|--------|-----------|----------------|
| **T1 — CLI in-process** | `cli/test-cli-inproc.ps1` | no | `help <topic>`, `worktree`, `cost`, `orchestrate create/status/list` return correct output + exit codes |
| **T2 — GUI smoke + visual** | `gui/test-gui-smoke.ps1` | yes | app launches with the build, window enumerable, screenshot captured, UIA tree reachable (warn-only) |
| **T3 — GUI↔CLI interaction** | `gui/test-gui-cli-interaction.ps1` | yes | status/terminal-list/terminal-read IPC, `terminal-wait` idle-detection against a live terminal, `agent-hook` fire-and-forget. `terminal-send` runs only against a shell-titled tab (never injects into an agent/SSH session). |

### Design notes
- **WinExe capture**: `AgentZeroLite.exe` is a GUI-subsystem binary — `& $exe`
  neither captures stdout nor sets `$LASTEXITCODE`. `Invoke-Cli` (in
  `lib/_common.ps1`) uses `Start-Process -RedirectStandardOutput` + a real
  timeout, mirroring `AgentZeroLite.ps1`.
- **Safety**: tiers never mutate the user's real `~/.cursor` / `~/.codex` /
  `~/.claude` (the commands that do — `trust-workspace`, `skill-stub-install`,
  `agent-hook-install` — are covered by headless unit tests against a temp home).
  `orchestrate` writes only to the app's own local DB, using a labeled
  `e2e-test-run` name.
- **element-tree** is expensive on this UI (WebView2 + large tree) and is
  **warn-only**; the screenshot is the visual gate.

## Running

```powershell
# everything (CLI + GUI)
pwsh Test/e2e/run-all.ps1

# CLI only (headless-friendly)
pwsh Test/e2e/run-all.ps1 -SkipGui

# individual tiers
pwsh Test/e2e/cli/test-cli-inproc.ps1
pwsh Test/e2e/gui/test-gui-smoke.ps1 -KeepOpen   # leave the GUI up for manual inspection

# against a Release build
pwsh Test/e2e/run-all.ps1 -Configuration Release
```

Build first: `dotnet build Project/AgentZeroWpf/AgentZeroWpf.csproj -c Debug`.

## Artifacts

Each run drops a timestamped folder under `_artifacts/` (git-ignored):
`report.json`, captured `*.png` screenshot, and `element-tree.json` when available.

## Layout

```
Test/e2e/
  lib/_common.ps1        shared: exe resolve, Invoke-Cli, assert/test framework, GUI+screenshot helpers
  cli/test-cli-inproc.ps1        Tier 1
  gui/test-gui-smoke.ps1         Tier 2
  gui/test-gui-cli-interaction.ps1  Tier 3
  run-all.ps1            orchestrator
  _artifacts/            per-run outputs (git-ignored)
```

## Related
- `Docs/scripts/launch-self-smoke.ps1` — original M0014 os-cli probe (this suite generalizes it)
- `harness/engine/os-cli-e2e-smoke.md` — harness engine for the os-cli smoke
