# AgentZero Lite — Privacy Policy

_Last updated: 2026-09-28. Draft — review before publishing._

AgentZero Lite ("the app") is a desktop shell for command-line AI tools. It is open
source: <https://github.com/psmon/AgentZeroLite>.

## What the app collects

**Nothing is collected by the developer.** The app has no accounts, no analytics, no
telemetry and no crash reporting service. The developer does not receive any data from
the app.

## What the app stores on your computer

Workspaces, terminal layouts, settings, conversation history of the built-in assistant,
downloaded models and logs are stored locally under `%LOCALAPPDATA%\AgentZeroLite\`.
API keys you enter are encrypted at rest with Windows DPAPI (tied to your Windows
account). Uninstalling the app does not delete this folder; you can delete it yourself.

## When the app connects to the internet

Only for features you use, and only to the service involved:

| Feature | Connects to | What is sent |
|---|---|---|
| External LLM (Settings → LLM) | The endpoint **you** configure (e.g. an OpenAI-compatible API, or a server on your own network) | Your prompts, and the context the assistant includes with them |
| Model downloads (local LLM, voice, music) | huggingface.co, github.com | A standard download request |
| Web tools of the assistant | html.duckduckgo.com, the pages it opens, wttr.in (weather) | The search query or page address |
| Agent CLI install (Settings → CLI Definitions) | The npm registry, through `npm install -g` | A standard package request |

Programs you run inside the app's terminal tabs (for example Claude Code, Codex or
agent-one) are separate software with their own privacy policies; the app does not see
or forward their traffic.

## Children

The app is a developer tool and is not directed at children.

## Contact

Questions: open an issue at <https://github.com/psmon/AgentZeroLite/issues>.
