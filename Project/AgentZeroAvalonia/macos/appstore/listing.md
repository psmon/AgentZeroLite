# Mac App Store listing — AgentZero Lite

The App Store Connect fields for the macOS app (app ID 6825237056, bundle id
`com.psmon.agentzerolite`). The text is the Microsoft Store listing
(`msix/store-listing/listing.md`) adapted to the Mac: no Windows-only parts (ConPTY,
local LLM, wearable), and the sandbox notes Apple asks for. `Docs/avalonia-v2/macos-distribution.md`
records what was submitted and what came back.

## App information

| Field | Value |
|---|---|
| Name (≤ 30) | AgentZero Lite |
| Subtitle (≤ 30) | Multi-tab shell for AI agents |
| Primary language | English (U.S.) |
| Category | Developer Tools · secondary: Utilities |
| Content rights | Does not contain, show or access third-party content |
| Age rating | Every item None / No (no general-purpose web browser, no user-to-user chat) |
| Privacy policy URL | https://psmon.github.io/AgentZeroLite/Home/privacy-policy.html |
| App privacy | Data Not Collected (no account, no telemetry; prompts go only to the LLM endpoint the person configures) |
| Price | Free, all territories except China mainland (guideline 5, generative AI) |
| EU DSA | Not a trader (free, open-source personal project) |
| Export compliance | `ITSAppUsesNonExemptEncryption = false` in Info.plist (HTTPS only) |

## Version page

**Promotional text (≤ 170)**
Run Claude Code, Codex and any shell side by side in real terminal tabs, split panes and workspaces — with a built-in assistant that can read and drive them.

**Description (≤ 4000)**

AgentZero Lite is a desktop home for the command-line AI tools you already use.

Open Claude Code, Codex, agent-one or any shell (zsh, bash) side by side in real terminal tabs, arrange them in split panes, and keep each project in its own workspace. The app checks whether each agent CLI is installed and, when you ask, installs it with your own npm (Node.js from Homebrew).

A built-in assistant can read what the terminals show, send them input, and relay work between agents, using the LLM endpoint you configure. Everything is scriptable from a shell with the app's -cli command.

• Real terminal tabs (xterm.js) for Claude Code, Codex, agent-one and any shell
• Split panes and per-project workspaces, remembered between sessions
• Active sessions sidebar: which agent is working, waiting for you or done, read from Claude Code's and Codex's own status
• Resume a Claude Code or Codex conversation when its terminal opens
• Code view: the workspace's files with syntax highlighting, Markdown rendered with Mermaid diagrams
• Git worktrees listed under their workspace, including those Claude Code and Codex create
• Web plugins: token dashboards and more, installed from the official list
• No account, no telemetry: settings and history stay on your Mac
• Open source

About this Mac App Store edition: the app runs in the macOS App Sandbox. Add each project with the folder dialog; the app keeps access to the folders you add, and their terminal tabs can read and write them. Tabs can run the tools you installed with Homebrew, and the ones uv or pipx put in ~/.local/bin (read-only). Tools that keep their settings in your home folder cannot read them from this edition. Install Git with Homebrew (brew install git); the Git that comes with Xcode's command line tools cannot be started from a sandboxed app. 

Support: psmon@live.co.kr, or open an issue at https://github.com/psmon/AgentZeroLite/issues

**Keywords (≤ 100 bytes)**
terminal,AI agent,Claude Code,Codex,CLI,LLM,shell,zsh,split panes,developer

**Support URL** https://github.com/psmon/AgentZeroLite/issues
**Marketing URL** https://psmon.github.io/AgentZeroLite/Home/
**Copyright** 2026 psmon

**Screenshots** — 16:10, one of 1280×800 / 1440×900 / 2560×1600 / 2880×1800, 1–10 images
(`macos/appstore/screenshots/`).

## App Review information

- Sign-in required: No
- Contact: sang man park · psmon@live.co.kr · +82 10 3861 7025

**Notes**

(As submitted with 0.27.3 build 7, 2026-11-27 — the App Store Connect field holds exactly this text, ≤ 4000 characters.)

Resubmission (0.27.3, build 7): we withdrew 0.27.1 (build 5) from the queue ourselves. TestFlight testing found that interactive command-line tools in terminal tabs did not work under the App Sandbox (no raw mode, Ctrl+C ignored, bash could not read /etc/profile). 0.27.2 and 0.27.3 fix this with the three narrow exceptions below; both builds passed TestFlight beta review and were tested internally before this submission.

No account or sign-in is needed. Click "+" (Add workspace) and choose any folder, then open a terminal tab from the workspace's + menu: choose zsh or bash and type any command (for example ls) to see the terminal working. The built-in assistant (AgentBot) is optional and needs an LLM endpoint entered in Settings → LLM. Agent CLI tabs (Claude Code, Codex) need those tools installed by the user.

File access: the app reaches a project only through the folder the user picks in the standard folder dialog (files.user-selected.read-write) and keeps it across launches with an app-scoped security-scoped bookmark. AgentZero Lite is a terminal host: each tab starts /bin/zsh or /bin/bash as a child process, which inherits the app's sandbox. The home-folder and /usr/local/ exceptions refused in the review of 0.25.8 (2.4.5(i)) are gone.

Temporary exceptions, all used only by commands the user types in a terminal tab:
- absolute-path.read-write ["/opt/homebrew/"]: the user's own Homebrew, so a tab can start node and the tools installed with it (unchanged).
- home-relative-path.read-only ["/.local/"]: tools installed with uv or pipx (~/.local/bin and their Python). Read-only.
- absolute-path.read-only: /private/etc/profile, bashrc, zshrc, zprofile, paths, paths.d/ — the system's shell startup files, read-only; without them bash opens with "/etc/profile: Operation not permitted". No other file under /etc.
- sbpl ["(allow file-ioctl (regex #\"^/dev/ttys[0-9]+$\"))"]: terminal control (termios, foreground process group) on the pseudo-terminal the app itself created for the tab. The sandbox profile allows ioctl on /dev/ptmx but not on its /dev/ttys* side, so without it no program can enter raw mode and Ctrl+C never arrives: interactive tools exit at start or read keys only after Return. Pseudo-terminal slaves only, no other device.

China mainland: not offered there; the metadata does not mention ChatGPT or OpenAI (guideline 5).

The app does not download or run code on its own. Commands run only when the user types them, or presses Install for an agent CLI in Settings → CLI Definitions, which runs the user's own npm (npm install -g <package>). Outbound HTTPS goes only to the LLM endpoint the user configures and to model files the user chooses to download.

network.server: loopback only. Each terminal tab is an xterm.js page in the app's web view, served from an HTTP server inside the app bound to 127.0.0.1 on an ephemeral port with a random token in every URL (the web view offers no custom-scheme handler). Nothing is reachable from other machines. Check: lsof -nP -iTCP -sTCP:LISTEN | grep AgentZero in a tab.

Generative AI content: every assistant answer has a "Report" button that opens a report panel and sends the report, with the answer quoted, as a pre-filled e-mail or GitHub issue; the user reviews and sends it.
