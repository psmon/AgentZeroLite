# AgentZero Lite — Privacy Policy

_Last updated: 2026-10-30._

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

**Files of other programs the app reads, on your computer only:** to offer resuming a
conversation and to show whether an agent is working, the app reads the session and status
files Claude Code, Codex and Hermes Agent keep in your user folder (`.claude` and Claude's profile
folders such as `.claude-<name>`, `.codex`, and Hermes' folder — `%LOCALAPPDATA%\hermes` or
`~/.hermes` — whose conversation database is opened read-only) and the list of
running processes (to find which terminal an agent runs in); to show git worktrees, it reads
a workspace's `.git` folder. To estimate what the agents have cost (Settings → Budget), it
reads the token counts Claude Code and Codex record in those session files. To show a
workspace's history and changes (the code view's GIT LOG and DIFF tabs), it runs the git
installed on your computer in that folder, which reads the folder's files. Nothing from them
leaves your computer.

## When the app connects to the internet

Only for features you use, and only to the service involved:

| Feature | Connects to | What is sent |
|---|---|---|
| External LLM (Settings → LLM) | The endpoint **you** configure (e.g. an OpenAI-compatible API, or a server on your own network) | Your prompts, and the context the assistant includes with them |
| Model downloads (local LLM, voice, music) | huggingface.co, github.com | A standard download request |
| Web tools of the assistant | html.duckduckgo.com, the pages it opens, wttr.in (weather) | The search query or page address |
| Agent CLI install (Settings → CLI Definitions) | The npm registry (`npm install -g`), or releases.netclaw.dev / hermes-agent.nousresearch.com for the netclaw / Hermes installers | A standard package or download request |
| Voice with the OpenAI provider (Settings → Voice) | api.openai.com, with **your** API key | The text to speak, or the recorded speech to transcribe |
| MCP servers (Settings → MCP) | The servers **you** add | The tool calls the assistant makes and their arguments |
| Plugins page — official list and install | api.github.com, raw.githubusercontent.com, github.com (or the address you paste) | A standard download request |
| Plugins that play or describe YouTube videos | youtube.com | The video address (to embed the player and read its title) |
| Code view — a Markdown file shown rendered | The web addresses of pictures written in that file (for example a badge in a README) | A standard image request; the file's text itself stays on your computer |

Programs you run inside the app's terminal tabs (for example Claude Code, Codex,
agent-one, netclaw or Hermes) are separate software with their own privacy policies; the app
does not see or forward their traffic.

## Microphone and system audio

Only when you start it: in a plugin (for example Voice Note or Agent Band), with the
microphone button of the assistant, or with "Listen live" in Settings → Voice. The app then
listens to the microphone or to what your speakers play, turns it into text or labels
**on your computer** (streaming speech recognition, Whisper, the AudioSet classifier, speaker
labels), and shows the result there — the assistant's microphone puts the text in its input
box and sends it as if you had typed it. No recording is kept. If you chose the OpenAI provider
for speech recognition, the speech is sent to OpenAI to be transcribed. Stopping the plugin or
pressing the microphone button again stops the capture.

## Children

The app is a developer tool and is not directed at children.

## Contact

Questions: email <psmon@live.co.kr> or open an issue at <https://github.com/psmon/AgentZeroLite/issues>.
