# Store listing — paste-ready text for Partner Center

Everything below is English (en-us listing). Review, then paste into the matching field.

## Product name
AgentZero Lite

## Short description (≤ 200 chars — used on smaller surfaces)
A multi-CLI shell for AI coding agents: terminal tabs for Claude Code, Codex and agent-one, split panes, workspaces, and an on-device assistant.

## Description
AgentZero Lite is a desktop home for the command-line AI tools you already use.

Open Claude Code, Codex, agent-one, netclaw, PowerShell or any shell side by side in real terminal
tabs, arrange them in split panes, and keep each project in its own workspace. The app
checks whether each agent CLI is installed and can install it for you with npm.

A built-in assistant can read what the terminals show, send them input, and relay work
between agents — running on a local model on your PC, or on any OpenAI-compatible
endpoint you configure. Everything is scriptable from any shell with
`AgentZeroLite.exe -cli`.

Your data stays on your PC: no account, no telemetry. The project is open source.

Support: psmon@live.co.kr, or open an issue at https://github.com/psmon/AgentZeroLite/issues

## Features (one per line, ≤ 200 chars each)
- Real terminal tabs (ConPTY + xterm.js) for Claude Code, Codex, agent-one and any shell
- Split panes and per-project workspaces, remembered between sessions
- Agent CLI check and one-click install for Claude Code, Codex, agent-one and netclaw
- Built-in assistant that can read and drive your terminals, on a local model or any OpenAI-compatible API
- Scriptable from any shell: AgentZeroLite.exe -cli
- No account, no telemetry — settings and history stay on your PC
- Open source

## Keywords (≤ 7)
terminal, AI agent, Claude Code, Codex, developer tools, CLI, LLM

## Category
Developer tools

## Privacy policy URL
https://github.com/psmon/AgentZeroLite/blob/main/Docs/privacy-policy.md

## Website / support
- Website: https://github.com/psmon/AgentZeroLite
- Support contact (Partner Center has one field — the reviewer asked for a direct contact): psmon@live.co.kr
- Issues (in the description and the privacy policy): https://github.com/psmon/AgentZeroLite/issues

---

## Restricted capability justifications

### runFullTrust
AgentZero Lite is a desktop terminal host. It creates pseudo-consoles (ConPTY) and starts
the user's shells and command-line tools (PowerShell, Claude Code, Codex, agent-one) as
child processes, talks to its own command-line interface over a named pipe, and serves
its terminal renderer from a loopback HTTP server to WebView2. None of this can run in an
AppContainer; the app is a packaged Win32 desktop application.

_unvirtualizedResources was requested in submission 1 and denied (policy 10.6.3,
2026-09-30); the package no longer declares it — see Docs/avalonia-v2/microsoft-store.md._

---

## Notes for certification
No account or sign-in is needed. Add a workspace (any folder), then open a terminal tab
from its + menu — choose PW5 (Windows PowerShell), which every Windows install has — and
type any command to see the terminal working. The built-in assistant
(AgentBot) needs a model: either download a local model in Settings → LLM, or enter any
OpenAI-compatible endpoint; this is optional for testing the rest of the app. Agent CLI
tabs (Claude Code, Codex, agent-one, netclaw) require those tools to be installed; Settings →
CLI Definitions shows their status and offers an install (npm, which requires Node.js, or
netclaw's own install script).

About write virtualization: this package does not declare unvirtualizedResources. The
app's own new files under AppData stay in the package's private store. Terminal tabs and
the Settings install are started with the desktop-app breakaway process policy
(PROC_THREAD_ATTRIBUTE_DESKTOP_APP_POLICY), so the shells and tools the user runs there
(npm, node, git, Claude Code) read and write the user's real profile, as they would in any
other terminal.

About the Windows App Certification Kit's optional "Blocked executables" finding: the app
is a terminal host, so it references and starts cmd.exe, PowerShell and other shells —
only when the user opens a terminal tab or runs a command in one. It does not launch them
on its own. Every mandatory WACK test passes (overall result: PASS).
