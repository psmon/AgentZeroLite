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

About this Mac App Store edition: the app runs in the macOS App Sandbox. Add each project with the folder dialog; the app keeps access to the folders you add, and their terminal tabs can read and write them. Tabs can run the tools you installed with Homebrew. Tools that keep their settings in your home folder cannot read them from this edition. Install Git with Homebrew (brew install git); the Git that comes with Xcode's command line tools cannot be started from a sandboxed app. Job control (Ctrl+Z) is not available in sandboxed shells.

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

No account or sign-in is needed. Click "+" (Add workspace) and choose any folder, then open a terminal tab from the workspace's + menu: choose zsh and type any command (for example ls) to see the terminal working. The built-in assistant (AgentBot) is optional and needs an LLM endpoint entered in Settings → LLM. Agent CLI tabs (Claude Code, Codex) need those tools installed by the user; Settings → CLI Definitions shows their status.

How file access works (changed after the review of 0.25.8, guideline 2.4.5(i)): the home-folder and /usr/local/ temporary exceptions are gone. The app reaches a project only through the folder the user picks in the standard folder dialog (com.apple.security.files.user-selected.read-write) and keeps that access across launches with an app-scoped security-scoped bookmark (com.apple.security.files.bookmarks.app-scope). AgentZero Lite is a terminal host: each terminal tab starts /bin/zsh as a child process, which inherits the app's sandbox and therefore the folders the user picked.

The one remaining temporary exception is com.apple.security.temporary-exception.files.absolute-path.read-write ["/opt/homebrew/"]: the user's own Homebrew installation, so a tab can start node and the command-line tools the user installed with Homebrew, as in any terminal. It is used only by commands the user types in a terminal tab.

China mainland: the app is no longer offered on the China mainland storefront, and the metadata no longer mentions ChatGPT or OpenAI (guideline 5).

The app itself does not download or run code on its own. Commands run only when the user types them in a terminal tab, or when the user presses Install for an agent CLI in Settings → CLI Definitions, which runs the user's own npm (npm install -g <package>) exactly as typing it in a terminal would. Network: loopback servers render the terminal (xterm.js) and plugin pages inside the app's web view; outbound HTTPS goes only to the LLM endpoint the user configures and to pages the user's commands fetch.

About generative AI content: every answer the built-in assistant writes carries a "Report" button that opens a "Report this AI answer" panel (category, optional note) and sends the report, with the answer quoted, as a pre-filled e-mail to the support contact or as a pre-filled GitHub issue; the user reviews and sends it.
