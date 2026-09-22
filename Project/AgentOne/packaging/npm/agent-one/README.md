# @webnori/agent-one

A standalone CLI agent: ask a question, it reads your workspace through a small
set of tools and answers. Windows, macOS and Linux.

```bash
npm install -g @webnori/agent-one

agent-one run "hello" --provider echo      # offline smoke test, no API key
agent-one run "what does this project do?"
agent-one chat
```

## What this package is

A thin wrapper. It ships **no binary**: `postinstall` downloads the native
`agent-one` build for your OS/arch from the matching GitHub Release, verifies
its SHA256 against `checksums.txt`, and unpacks it next to the launcher. The npm
version and the release tag are published together, so they cannot drift.

Supported: `win-x64`, `linux-x64`, `osx-arm64`, `osx-x64` (Node ≥ 18).

## Configuration

```bash
agent-one config set provider openai
agent-one config set model gpt-4o-mini
export OPENAI_API_KEY=sk-...
```

Any OpenAI-compatible endpoint works — Ollama, LM Studio, vLLM, llama.cpp:

```bash
agent-one config set baseUrl http://localhost:11434/v1
agent-one config set model qwen2.5-coder:7b
```

Settings live in `~/.agent-one/config.json`. The API key is never written
there — `apiKeyEnv` names the environment variable to read it from.

## Environment variables

| Variable | Effect |
|---|---|
| `AGENT_ONE_SKIP_DOWNLOAD=1` | Skip the postinstall download (offline / vendored installs) |
| `AGENT_ONE_VERSION` | Download a specific release instead of the package version |
| `AGENT_ONE_REPO` | Download from a fork instead of `psmon/AgentZeroLite` |
| `AGENT_ONE_HOME` | Relocate `~/.agent-one/` |

Uninstalling removes only the downloaded binary; `~/.agent-one/` is your data
and is left where it is.

Full documentation:
<https://github.com/psmon/AgentZeroLite/tree/main/Project/AgentOne>
