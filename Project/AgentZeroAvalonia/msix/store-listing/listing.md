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

## What's new in this version (1.26.7.0 — submission 17)
Paste into Store listings → English → "What's new in this version" (≤ 1500 chars).

- Talk to the assistant: press the microphone button beside its input box and speak — your words appear while you are still talking and are sent the moment you pause. Speech recognition runs on your PC with NVIDIA's Nemotron streaming model (Korean, English and many more languages, punctuation included); download it once in Settings → Voice.
- Settings → Voice → Listen live shows the recognized words as you speak, to try a model and a microphone before using it.
- KittenTTS 2: a new on-device voice that speaks many languages, Korean included, on your GPU when it has one. Download it in Settings → Voice.
- Voice models now load when first used and stay ready, instead of loading again for every test.

## What's new in version 1.26.5.0 (submission 16)
Paste into Store listings → English → "What's new in this version" (≤ 1500 chars).

- Know when an agent needs you: when an agent in any terminal is blocked on a question or finishes its work, a small popup appears in the corner of the screen (click it to go to that terminal) and the taskbar button flashes. Settings → Notifications.
- Budget: see what the agents in your terminals have cost this month, by model and by project, against an optional monthly cap. The month's spend is on the status bar. Prices are editable, and the built-in prices now follow the current Claude models.
- Diff review: a DIFF tab beside the code view shows the workspace's changes, new files included. Add a note to any line and send all notes to the agent in the terminal or to the assistant. Files the assistant wrote in AI mode can be kept or reverted one by one.
- Command palette (Ctrl+Shift+P) to jump to any terminal, workspace, page or command, and Settings → Shortcuts to change the keys.

## What's new in version 1.26.4.0 (submission 15)
Paste into Store listings → English → "What's new in this version" (≤ 1500 chars).

- Hermes Agent (Nous Research) is now a built-in agent: a ready-made terminal profile, an install button when it is missing, and live status in Active sessions and on Agent Stage — working, waiting for your approval, done.
- Agent Stage shows the parallel sub-agents of Codex and Hermes as a crew around their terminal, as it already did for Claude.
- Fixed: a Codex tab could read as idle while it was working, with recent Codex versions.

## What's new in version 1.26.3.0 (submission 14)
Paste into Store listings → English → "What's new in this version" (≤ 1500 chars).

- Agent Stage knows a Claude tab by what runs in it, not by its name: a renamed tab, a second-account profile (such as a claude-sam command) or claude typed into a plain PowerShell tab now shows Claude ✳ — the same for Codex, AgentOne and Netclaw.
- A second Claude account (its own CLAUDE_CONFIG_DIR, e.g. ~/.claude-<name>) is now followed from its own status: working, waiting and done in Active sessions, and its sub-agent crew on Agent Stage.

## What's new in version 1.26.2.0 (submission 13)
Paste into Store listings → English → "What's new in this version" (≤ 1500 chars).

- Agent Stage: when Claude splits a job into sub-agents, they now appear as a crew around its avatar — each with its own colour and role name (lyricist, Explore, Plan …), work flowing along its tether while it runs, nested sub-agents fanned out beyond the one that started them, and a comet back to the parent when one hands in its report. Hover a crew member for its task, last tool, tool count and elapsed time. Read from Claude Code's own session records — nothing to set up.

## What's new in version 1.26.1.0 (submission 12)
Paste into Store listings → English → "What's new in this version" (≤ 1500 chars).

- Agent Stage (new official plugin): see your workspaces as stages and every terminal as an agent — Claude, Codex, AgentOne, Netclaw or a shell. While an agent works it animates (thinking ring, rising code, typing bars); it pulses red when it needs you and bursts when it finishes. Click an agent to jump to its terminal. Plugins page → Install official plugin → Agent Stage.
- The workspace you are in is now marked in teal in the sidebar — apart from the focused terminal's blue — and the sessions list names its workspace.
- Fix: long worktree names end in "…" and their close button no longer sits on the sidebar edge.

## What's new in version 1.25.9.0 (submission 11)
Paste into Store listings → English → "What's new in this version" (≤ 1500 chars).

- Git log: a GIT LOG tab beside CODE lists the workspace's recent commits — subject, author and age, the full message on hover. Read with your local git; the tab is off for folders git cannot read.
- Code view: select a folder to see its files with size, modified and created dates. The path button copies a relative or an absolute path; open shows the folder in File Explorer.
- Sidebar: right-click a workspace or worktree to open its folder or copy its path. The sidebar now scrolls when the bottom panel is tall, and a small arrow on the panel hides and shows it.

## What's new in version 1.25.8.0 (submission 10)
Paste into Store listings → English → "What's new in this version" (≤ 1500 chars).

- Agent status you can trust: ACTIVE SESSIONS now reads what Claude Code and Codex record about themselves, not only the screen. A working agent stays "working" for the whole turn, a question to you shows as "waiting", and a finished turn shows "done" within a second — until you look at it.
- Fix: a git worktree opened through a linked folder is recognised and nested under its repository.

## What's new in version 1.25.7.0 (submission 9)
Paste into Store listings → English → "What's new in this version" (≤ 1500 chars).

- Resume an agent session: when a Claude Code or Codex terminal opens, pick one of the conversations that agent already has for this folder (newest first, with its title and first prompt) — or start a new one. "Don't ask again" turns the question off per agent; Settings → Terminal turns it back on.
- Git worktrees in the sidebar: a workspace shows its branch and lists the repository's other worktrees under it — including the ones Claude Code (.claude/worktrees) and Codex create — so you can switch to one, or open it, with a click. New worktrees appear on their own.
- Fix: a terminal whose folder cannot be reached now starts in your home folder.

## What's new in version 1.25.6.0 (submission 8)
Paste into Store listings → English → "What's new in this version" (≤ 1500 chars).

- Code view: browse the workspace's files in a new CODE tab of the bottom panel, next to the bot (📄 on the left bar or Ctrl+Alt+E). Syntax highlighting with VS Code's grammars, images, and Markdown shown rendered — with Mermaid diagrams — or as source. It can float in its own window too.
- Plugins can be detached into their own window: pin it on top and set its opacity to keep a plugin beside your terminals. The spot is remembered per plugin.
- Fix: renaming a terminal tab now updates the tab strip and the session lists at once.

## What's new in version 1.25.5.0 (submission 7)
Paste into Store listings → English → "What's new in this version" (≤ 1500 chars).

- Detach a terminal into its own window and dock it back: right-click a tab → Detach to window, the ⤢ button on a pane, or Ctrl+Alt+F (Ctrl+Alt+D to dock). The terminal keeps running and keeps its screen; closing the window puts it back in its pane.
- Fix: the first output of a newly opened terminal (the start of its prompt) could be lost.

## What's new in version 1.25.4.0 (submission 6)
Paste into Store listings → English → "What's new in this version" (≤ 1500 chars).

- Plugins page: run web plugins inside the app. Install from the official list, a GitHub folder or a .zip — Voice Note (speech-to-text with speaker labels), Agent Band (live music recognition with a YouTube/MP3 stage), Token Monitor and Token Remaining.
- Settings → Models: download the on-device music classifier, vision model and speaker-diarization models.
- Settings → Voice: pick text-to-speech and speech recognition (local Whisper or OpenAI) and test them without a microphone.
- MCP servers for the assistant's AI mode: add stdio or HTTP servers and allow their tools one by one.
- Active sessions in the sidebar: every running terminal of every workspace with its agent state (working, waiting for you, done) — one click to jump there; the focused session is highlighted.
- Fixes: no more freeze when a workspace folder is on an offline network drive; OpenAI reasoning models (gpt-5 family) work with the token limit; token telemetry is faster.

## Features (one per line, ≤ 200 chars each)
- Real terminal tabs (ConPTY + xterm.js) for Claude Code, Codex, agent-one and any shell
- Split panes and per-project workspaces, remembered between sessions
- Agent CLI check and one-click install for Claude Code, Codex, agent-one, netclaw and Hermes Agent
- Built-in assistant that can read and drive your terminals, on a local model or any OpenAI-compatible API
- Scriptable from any shell: AgentZeroLite.exe -cli
- Web plugins: Agent Stage, Voice Note, live music recognition and token dashboards, installed from the official list
- Active-sessions sidebar showing which agent is working, waiting for you or done — from Claude Code's, Codex's and Hermes' own status, and Agent Stage showing their sub-agents as a crew
- Code view: the workspace's files with VS Code syntax highlighting, Markdown with Mermaid diagrams, its git log, and a diff to review and annotate
- Notifications when an agent needs you, a monthly cost view by model and project, and a command palette
- Speak to the assistant with on-device speech recognition that shows your words while you talk, and on-device voices
- Resume a Claude Code or Codex conversation when its terminal opens
- Git worktrees listed under their workspace, including those Claude Code and Codex create
- No account, no telemetry — settings and history stay on your PC
- Open source

## Keywords (≤ 7)
terminal, AI agent, Claude Code, Codex, developer tools, CLI, LLM

## Category
Developer tools

## Privacy policy URL
https://psmon.github.io/AgentZeroLite/Home/privacy-policy.html
(a real web page on GitHub Pages — submission 2 was rejected for the github.com/…/blob/… file view)

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

About generative AI content (policy 11.16): every answer the built-in assistant (AgentBot)
writes carries a "⚑ Report" button on its bubble. It opens a "Report this AI answer" panel
— category (inappropriate or offensive / harmful or dangerous / wrong or misleading / other),
an optional note — and sends the report, with the answer quoted, as a pre-filled e-mail to
the support contact or as a pre-filled GitHub issue; the user reviews and sends it. To see
it without configuring a model, any message delivered to the assistant shows the button,
e.g. from a terminal: AgentZeroLite.exe -cli bot-chat "sample answer" --from AgentBot

About the microphone (new in 1.25.4.0): the package declares the microphone device
capability for the Plugins page. Capture starts only when the user presses Start in the
Voice Note or Agent Band plugin (microphone source) and stops with Stop; the audio is turned
into text or labels on the device and no recording is kept. To try it: Plugins (puzzle icon
in the left bar) → Install official plugin → Voice Note → open it → Start. The same plugins
can listen to system audio instead (WASAPI loopback), which needs no capability.

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
