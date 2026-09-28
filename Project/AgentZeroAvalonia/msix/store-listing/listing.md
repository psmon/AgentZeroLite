# Store listing — paste-ready text for Partner Center

Everything below is English (en-us listing). Review, then paste into the matching field.

## Product name
AgentZero Lite

## Short description (≤ 200 chars — used on smaller surfaces)
A multi-CLI shell for AI coding agents: terminal tabs for Claude Code, Codex and agent-one, split panes, workspaces, and an on-device assistant.

## Description
AgentZero Lite is a desktop home for the command-line AI tools you already use.

Open Claude Code, Codex, agent-one, PowerShell or any shell side by side in real terminal
tabs, arrange them in split panes, and keep each project in its own workspace. The app
checks whether each agent CLI is installed and can install it for you with npm.

A built-in assistant can read what the terminals show, send them input, and relay work
between agents — running on a local model on your PC, or on any OpenAI-compatible
endpoint you configure. Everything is scriptable from any shell with
`AgentZeroLite.exe -cli`.

Your data stays on your PC: no account, no telemetry. The project is open source.

## Features (one per line, ≤ 200 chars each)
- Real terminal tabs (ConPTY + xterm.js) for Claude Code, Codex, agent-one and any shell
- Split panes and per-project workspaces, remembered between sessions
- Agent CLI check and one-click npm install for Claude Code, Codex and agent-one
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
https://github.com/psmon/AgentZeroLite — support: https://github.com/psmon/AgentZeroLite/issues

---

## Restricted capability justifications

### runFullTrust
AgentZero Lite is a desktop terminal host. It creates pseudo-consoles (ConPTY) and starts
the user's shells and command-line tools (PowerShell, Claude Code, Codex, agent-one) as
child processes, talks to its own command-line interface over a named pipe, and serves
its terminal renderer from a loopback HTTP server to WebView2. None of this can run in an
AppContainer; the app is a packaged Win32 desktop application.

### unvirtualizedResources
The app and the command-line tools it starts in its terminal tabs must see the same
AppData and HKCU as the user's other terminals. With write virtualization on, (1) the
app's database and settings in %LOCALAPPDATA%\AgentZeroLite would be hidden from the
same app's command-line interface and from the non-Store edition that shares them, and
(2) tools the user installs from inside a terminal tab (for example `npm install -g`,
which writes to %APPDATA%\npm) would land in the package's private store — invisible to
the user's other terminals and deleted on uninstall. This is the same need as other
terminal applications. The app writes only its own folder (%LOCALAPPDATA%\AgentZeroLite);
everything else is written by programs the user runs explicitly.

---

## Notes for certification
No account or sign-in is needed. Add a workspace (any folder), then open a terminal tab
from its + menu — choose PW5 (Windows PowerShell), which every Windows install has — and
type any command to see the terminal working. The built-in assistant
(AgentBot) needs a model: either download a local model in Settings → LLM, or enter any
OpenAI-compatible endpoint; this is optional for testing the rest of the app. Agent CLI
tabs (Claude Code, Codex, agent-one) require those tools to be installed; Settings → CLI
Definitions shows their status and offers an npm install (requires Node.js).

About the Windows App Certification Kit's optional "Blocked executables" finding: the app
is a terminal host, so it references and starts cmd.exe, PowerShell and other shells —
only when the user opens a terminal tab or runs a command in one. It does not launch them
on its own. Every mandatory WACK test passes (overall result: PASS).
