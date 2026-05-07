# OS-Control (CLI + LLM tools)

Mission **M0014** brought Windows desktop automation into AgentZeroLite.
This page is the public reference — what verbs exist, how to call them,
and how the approval gate works. Internal architecture notes (audit
schema, file layout, anti-patterns) live in
`harness/knowledge/_shared/os-control.md`.

## Surface at a glance

Every read-only OS-control feature is exposed two ways:

- **CLI**: `AgentZeroLite.exe -cli os <verb> [args]` — runs in-process
  in the CLI invocation; no WM_COPYDATA round-trip; works whether the
  GUI is running or not.
- **LLM tool**: `os_<name>` — callable by the agent loop (Gemma 4 local
  / OpenAI-compatible external) when the on-device assistant decides a
  desktop action is what the user asked for.

Read-only verbs (window enumeration, screenshot, UIA tree, DPI) are
always available. Input-simulation verbs (mouse, keyboard) require
explicit opt-in.

## CLI verbs

```
AgentZeroLite.exe -cli os <verb> [args]
```

### Read-only

| Verb | What it does |
|---|---|
| `list-windows [--filter S] [--include-hidden]` | Enumerate visible top-level windows. Returns JSON array. |
| `get-window-info <hwnd>` | Detail one window (rect, pid, process name, class). |
| `screenshot [--hwnd N] [--color] [--full]` | PNG to `tmp/os-cli/screenshots/<date>/`. Default: full virtual desktop, grayscale, downscaled to 1920×1080 if larger. |
| `element-tree <hwnd> [--depth N] [--search S]` | UI Automation tree dump. Depth default 30, max 100. |
| `text-capture <hwnd>` | Text from a window's UIA tree (`Name` aggregation). |
| `dpi` | System and per-monitor DPI report. |
| `activate <hwnd>` | Bring a window to the foreground (uses AttachThreadInput dance). |

### Input simulation (gated)

Append `--allow-input` to enable per-call, or set
`AGENTZERO_OS_INPUT_ALLOWED=1` in the environment for process-scoped
opt-in:

| Verb | Args |
|---|---|
| `mouse-click <x> <y> [--right] [--double]` | virtual-screen coords |
| `mouse-move <x> <y>` | virtual-screen coords |
| `mouse-wheel <x> <y> <delta>` | delta in WHEEL_DELTA units (positive = up) |
| `keypress <spec>` | `'a'`, `'ctrl+c'`, `'alt+f4'`, `'f5'`, `'ctrl+shift+t'`, … |

### Inspection

| Verb | What it does |
|---|---|
| `audit [--last N]` | Print today's `tmp/os-cli/audit/<date>.jsonl`. |

### Examples

```powershell
# Full-desktop screenshot, grayscale, 1920×1080-clamped
AgentZeroLite.exe -cli os screenshot

# Native-resolution color shot of one window
AgentZeroLite.exe -cli os screenshot --hwnd 0x000A0234 --color

# Find every Notepad window and inspect the first one's element tree
$json = AgentZeroLite.exe -cli os list-windows --filter Notepad | ConvertFrom-Json
AgentZeroLite.exe -cli os element-tree $json.windows[0].hwnd --depth 5

# Press Alt+F4 in the foreground app (needs --allow-input)
AgentZeroLite.exe -cli os keypress alt+f4 --allow-input
```

## LLM tools

The agent loop's tool catalog grew six entries (M0014) on top of the
existing terminal-relay surface. The new tools are read-only by default,
with two input-simulation tools gated by the same env var as the CLI:

| Tool | Args | Notes |
|---|---|---|
| `os_list_windows` | `{ "title_filter": <string?> }` | omit filter for all |
| `os_screenshot` | `{ "hwnd": <int>, "grayscale": <bool> }` | hwnd=0 ⇒ full desktop. Returns path, not bytes. |
| `os_activate` | `{ "hwnd": <int> }` | foreground a window |
| `os_element_tree` | `{ "hwnd": <int>, "depth": <int 1..50>, "search": <string?> }` | inspection only |
| `os_mouse_click` | `{ "x": <int>, "y": <int>, "right": <bool>, "double": <bool> }` | **gated** |
| `os_key_press` | `{ "key": <string> }` | **gated**, spec same as CLI |

When the gate is closed and the LLM calls a gated tool, it receives a
denial envelope; the system prompt forbids retry — the model must
report the failure via `done`.

## Approval gate

Both CLI and LLM share **one** binary gate for input simulation. Either
of these enables it:

1. **CLI per-call**: `--allow-input` flag on `mouse-*` / `keypress` verbs.
2. **Process env var**: `AGENTZERO_OS_INPUT_ALLOWED=1` (also accepts
   `true`, `yes`).

Read-only verbs are unconditional. There is no per-call human prompt.
This is a deliberate choice — automation friction would defeat the
purpose, and the audit log gives the operator forensic visibility after
the fact.

## Audit & artefacts

Everything that happens through OS-control lands under `tmp/os-cli/`:

```
tmp/os-cli/
├── audit/2026-05-07.jsonl        one line per call (cli, llm, e2e)
├── screenshots/2026-05-07/       PNG outputs
└── e2e/2026-05-07.log            E2E smoke summary
```

The E2E smoke script itself is tracked in git at
`Docs/scripts/launch-self-smoke.ps1` — `tmp/` is gitignored, so the
script lives outside it.

Audit JSONL schema: `ts`, `caller` (`cli`/`llm`/`e2e`), `verb`, `args`,
`ok`, `error`. Use `os audit --last 50` for a quick read.

## E2E acceptance probe

The mission contract requires a smoke test that uses the new CLI verbs
to verify the program is reachable from the desktop without driving any
of its features. Run it any time after a build:

```powershell
pwsh Docs/scripts/launch-self-smoke.ps1 -Configuration Debug
```

Steps: list-windows → get-window-info → screenshot → element-tree → dpi.
Exit 0 = all probes passed; PNG and audit lines are kept for review.

## Origin parity (AgentWin)

The AgentWin origin project shipped a richer 15-verb / 9-LLM-tool
surface (see `Docs/agent-origin/03-adoption-recommendations.md` §P1-2).
M0014 imported the 80% slice that fits Lite's identity:

- **Imported**: list-windows, get-window-info, screenshot, element-tree,
  text-capture, dpi, activate, mouse-click, mouse-move, mouse-wheel,
  keypress.
- **Skipped**: scroll-capture (folded into element-tree), `copy <text>`
  (clashes with existing `copy` verb), virtual-desktop service
  (dedicated future mission).

The lift was deliberate: Lite already carried 90% of the necessary
Win32 P/Invoke (`Project/AgentZeroWpf/NativeMethods.cs`) and the
`ElementTreeScanner`. M0014 added the wrappers, audit, gating, CLI
dispatch, LLM bridge, and E2E smoke.
