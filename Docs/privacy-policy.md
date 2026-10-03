# AgentZero Lite — Privacy Policy

_Last updated: 2026-10-03._

Published page: <https://psmon.github.io/AgentZeroLite/Home/privacy-policy.html>
(this Markdown file is its source — keep the two in step).

AgentZero Lite ("the app") is a desktop shell for command-line AI tools. It is open
source: <https://github.com/psmon/AgentZeroLite>.

## What the app collects

**Nothing is collected by the developer.** The app has no accounts, no analytics, no
telemetry and no crash reporting service. The developer does not receive any data from
the app.

## What the app stores on your computer

Workspaces, terminal layouts, settings, conversation history of the built-in assistant,
downloaded models and logs are stored only on your computer. API keys you enter are
encrypted at rest with Windows DPAPI (tied to your Windows account).

- **Microsoft Store edition:** in the app's own package storage, which Windows removes
  when you uninstall the app.
- **Edition installed from GitHub:** under `%LOCALAPPDATA%\AgentZeroLite\`. Uninstalling
  does not delete this folder; you can delete it yourself.

## When the app connects to the internet

Only for features you use, and only to the service involved:

| Feature | Connects to | What is sent |
|---|---|---|
| External LLM (Settings → LLM) | The endpoint **you** configure (e.g. an OpenAI-compatible API, or a server on your own network) | Your prompts, and the context the assistant includes with them |
| Model downloads (local LLM, voice, music) | huggingface.co, github.com | A standard download request |
| Web tools of the assistant | html.duckduckgo.com, the pages it opens, wttr.in (weather) | The search query or page address |
| Agent CLI install (Settings → CLI Definitions) | The npm registry (`npm install -g`), or releases.netclaw.dev for netclaw's installer | A standard package or download request |

Programs you run inside the app's terminal tabs (for example Claude Code, Codex,
agent-one or netclaw) are separate software with their own privacy policies; the app
does not see or forward their traffic.

## Children

The app is a developer tool and is not directed at children.

## Contact

Questions: email <psmon@live.co.kr> or open an issue at <https://github.com/psmon/AgentZeroLite/issues>.
