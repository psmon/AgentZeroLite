# Terminal interaction protocol — handshake, reverse channel, discussion

This is the material `-cli help` cannot teach: **how terminals talk to each
other** inside AgentZero Lite. Read it before orchestrating any cross-terminal
conversation. Ordering and `--from` naming are what make messages *land* instead
of being silently dropped.

Throughout, `$Exe` is the resolved binary and every call goes through
`Start-Process $Exe -ArgumentList '-cli',… -NoNewWindow -Wait` (or the
`AgentZeroLite.ps1` wrapper). Tabs are addressed as `<G> <T>` (group, tab).

## 1. Mental model

- **Outbound to a peer** is direct: `terminal-send` types into its console,
  `terminal-read` scrapes its output, `terminal-wait` blocks until it settles.
  This works on *any* tab — you're literally driving its keyboard.
- **Inbound from a peer** is NOT automatic. A peer agent's screen text is just
  pixels to the coordinator. For a peer to send a real message back, it must run
  a CLI line **in its own terminal**:

  ```
  AgentZeroLite.exe -cli bot-chat "DONE(<text>)" --from <peerName>
  ```

  The AgentBot broker then routes that into the active conversation (and to
  voice, if enabled). This is the **reverse channel**.

- **Why `--from` matters.** The broker keeps per-peer handshake state:
  `NotConnected → HandshakeSent → Connected`, keyed by the `--from` string.
  A `bot-chat` from a peer that is *not* an active conversation is logged and
  **dropped**. The first callback from a peer flips it to `Connected`. So the
  `--from` name must match the identity the coordinator marked active
  (typically the tab title).

- **Why `DONE(...)`.** The broker unwraps `DONE(...)` for display + voice.
  Bare acks (`ready`, `ok`, `ack`, `handshake-ok`) are recognized as
  handshake acks and are **not** treated as task answers — good for the initial
  readiness ping, wrong for actual content.

## 2. Opening a channel — the handshake

Before expecting replies, teach the peer the reverse channel and wait for its
`ready` ack.

```powershell
function Invoke-Az { param([string[]]$Args)
    Start-Process $Exe -ArgumentList (@('-cli') + $Args) -NoNewWindow -Wait `
        -RedirectStandardOutput $env:TEMP\az-out.txt
    Get-Content $env:TEMP\az-out.txt -Raw
}

$G = 0; $T = 1; $peer = 'Claude'

$handshake = @"
[handshake] Send every reply by running this in YOUR terminal:
    AgentZeroLite.exe -cli bot-chat "DONE(<text>)" --from $peer
Screen-only text is not received. Ack now:
    AgentZeroLite.exe -cli bot-chat "DONE(ready)" --from $peer
"@

Invoke-Az @('terminal-send', "$G", "$T", $handshake)
Invoke-Az @('terminal-wait', "$G", "$T", '--until', 'idle', '--timeout-ms', '30000')
Invoke-Az @('terminal-read', "$G", "$T", '--last', '600')   # confirm it ran the ready line
```

If the peer is a hosted agent CLI (Claude Code, etc.) whose folder shows a
"trust this folder?" prompt, that prompt will swallow your keystrokes — run
`… -cli trust-workspace <path>` (or `worktree add … --trust`) first.

## 3. One request/response turn

```powershell
# Ask the peer something
Invoke-Az @('terminal-send', "$G", "$T", 'Summarize the build errors in 2 lines.')

# Wait for the agent to finish, not just for idle keystrokes
Invoke-Az @('terminal-wait', "$G", "$T", '--until', 'done', '--agent', 'claude', '--timeout-ms', '120000')

# Read its answer (it should have run bot-chat "DONE(...)"; you can also scrape)
$reply = Invoke-Az @('terminal-read', "$G", "$T", '--last', '1500')
```

Prefer `--until done` (lifecycle) over `--until idle` when the peer is a real
agent CLI: idle can trigger mid-thought during a pause, while `done` waits for
the detected completion state (falls back with `--stall-ms` if it never gets
there).

## 4. Two-terminal discussion / debate loop

Relay turns between tab A `<Ga,Ta>` and tab B `<Gb,Tb>`. Handshake **both**
first, then alternate: forward A's `DONE(...)` into B, wait, read, forward
B's answer into A, repeat until a stop condition (a turn cap, or a peer emits
`DONE(CONSENSUS ...)` / `DONE(STOP)`).

```powershell
$A = @{ G = 0; T = 1; Name = 'Alice' }
$B = @{ G = 0; T = 2; Name = 'Bob'   }

# (handshake $A and $B as in §2, using each .Name as --from) …

$topic = 'Should we cache the manifest in memory or on disk? Argue your side.'
Invoke-Az @('terminal-send', "$($A.G)", "$($A.T)", $topic)

for ($turn = 0; $turn -lt 6; $turn++) {
    $from = if ($turn % 2 -eq 0) { $A } else { $B }
    $to   = if ($turn % 2 -eq 0) { $B } else { $A }

    Invoke-Az @('terminal-wait', "$($from.G)", "$($from.T)", '--until', 'done', '--timeout-ms', '120000')
    $msg = Invoke-Az @('terminal-read', "$($from.G)", "$($from.T)", '--last', '1500')

    if ($msg -match 'DONE\(\s*(CONSENSUS|STOP)') { break }

    Invoke-Az @('terminal-send', "$($to.G)", "$($to.T)",
        "[$($from.Name) said] $msg`nRespond, then reply via bot-chat DONE(...) --from $($to.Name).")
}
```

Notes:
- The coordinator (this loop) is the **relay** — the two peers never address each
  other directly; every hop goes send → wait → read → forward.
- Cap the turns. Agents will happily debate forever; a turn limit plus a
  `DONE(CONSENSUS …)` / `DONE(STOP)` sentinel keeps it bounded.
- Use `agent-state` between turns if you need a quick health check across all
  tabs (who's blocked, who's done-but-unseen).

## 5. Failure modes

| Symptom | Cause | Fix |
|---------|-------|-----|
| Peer's reply never arrives | it wrote to screen, didn't run `bot-chat` | re-send handshake; confirm it ran the `DONE(ready)` line |
| Reply "dropped as inactive peer" | `--from` name ≠ marked-active identity | use the exact tab title as `--from` |
| Keystrokes vanish on a fresh agent tab | folder-trust prompt intercepting input | `trust-workspace <path>` before sending |
| `terminal-wait --until idle` returns mid-answer | agent paused briefly | switch to `--until done --agent <name>` |
| "GUI is not running" | terminal/bot IPC needs the GUI | `open-win`, then retry |
| "WM_COPYDATA timed out" | GUI UI thread stuck | inspect the GUI log panel; don't spin retries |
