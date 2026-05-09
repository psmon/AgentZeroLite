# WM_COPYDATA IPC Pitfalls — CLI ↔ GUI in AgentZero Lite

> Owner: **`code-coach`** (primary) — flag these patterns during pre-commit review
> whenever a diff touches `Project/AgentZeroWpf/CliHandler.cs`,
> `Project/AgentZeroWpf/Module/CliTerminalIpcHelper.cs`, the `WndProc` hook in
> `MainWindow.xaml.cs`, or `Project/AgentZeroWpf/NativeMethods.cs` IPC declarations.
> Cross-reference: **`build-doctor`** (secondary) — installer/runtime regressions
> involving startup hangs often surface here first.

`AgentZeroLite.exe -cli <cmd>` is a single-exe CLI that talks to the running GUI
over `WM_COPYDATA` (request, marker `0x414C "AL"`) plus a named MMF (response,
`AgentZeroLite_*` prefix). The pattern is fast and avoids extra deps — but each
of the four legs has a known failure mode the operator has hit at least once.

---

## Pitfall 1 — `SendMessageW` has no timeout: any GUI stall blocks the CLI forever

**Symptom**

```
PS> AgentZeroLite.ps1 status
   (… spinner; PowerShell wrapper does Start-Process -Wait, so PS hangs too)
```

The CLI process never returns. Task Manager shows the CLI exe alive,
unresponsive, no error printed.

**Root cause**

```csharp
// NativeMethods.cs — pre-fix declaration
[LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
public static partial IntPtr SendMessageCopyData(...);
```

`SendMessage` is **synchronous and unbounded**: it blocks the calling thread
until the receiving window's message pump dispatches the message and `WndProc`
returns. If the WPF UI thread is stuck — modal dialog, LLamaSharp model
warm-up, ConPTY input pipe blocked, GC pause, COM apartment marshal — the CLI
process waits **infinitely**.

`CliHandler._timeoutMs = 5000` only governs the *MMF response polling* loop in
`TryReadMmf`; it does **not** apply to the `SendMessage` send itself. Sending
happens before polling starts, so the CLI hangs before the timeout ever has a
chance to fire.

**Fix — `SendMessageTimeoutW` with `SMTO_ABORTIFHUNG`**

```csharp
// NativeMethods.cs
[LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
public static partial IntPtr SendMessageTimeoutCopyData(
    IntPtr hWnd, uint Msg, IntPtr wParam, ref COPYDATASTRUCT lParam,
    uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

public const uint SMTO_NORMAL              = 0x0000;
public const uint SMTO_ABORTIFHUNG         = 0x0002;
public const uint SMTO_NOTIMEOUTIFNOTHUNG  = 0x0008;

// CliHandler.SendWpfCommand — bound the send to 3s
var rc = NativeMethods.SendMessageTimeoutCopyData(
    agentWnd, NativeMethods.WM_COPYDATA, IntPtr.Zero, ref cds,
    NativeMethods.SMTO_ABORTIFHUNG | NativeMethods.SMTO_NORMAL,
    uTimeout: 3000, out _);
if (rc == IntPtr.Zero)
{
    Console.Error.WriteLine(
        "Error: AgentZero GUI unresponsive (WM_COPYDATA timed out after 3000ms). " +
        "Check the log panel for a stuck operation, then retry.");
    return false;
}
return true;
```

`SendWpfCommand` returning `bool` is load-bearing: every caller must
short-circuit on `false` before the MMF poll. Don't ignore the return value.

**Trade-off**

3 s is short enough that a healthy GUI almost never trips it but long enough
that a transient layout pass / brief Akka shutdown phase doesn't false-positive.
For a tighter budget (e.g. CI smoke), pass `--timeout` *for the polling phase
only* — the WM_COPYDATA timeout is intentionally fixed in code so flaky CI
runs can't accidentally suppress the diagnostic.

---

## Pitfall 2 — Heavy work inside `WndProc` blocks the UI thread (and via P1, the CLI)

**Symptom**

`SendMessageTimeout` from CLI now returns 0 (timeout) reliably whenever a
specific verb is invoked while the target sub-system is loaded — most often
`terminal-send` against a PTY whose child has paused.

**Root cause**

`MainWindow.HandleCliCommand` runs on the WPF UI thread. Today the handler
body does the work synchronously inside `WndProc`:

```csharp
// MainWindow.xaml.cs — HandleTerminalSend (excerpt)
session.WriteAndSubmit(text);     // calls _pty.WriteToTerm — sync WriteFile on the input pipe
IpcMemoryMappedResponseWriter.WriteJson(...);
```

`WriteToTerm` is a sync `WriteFile` on the ConPTY input pipe. If the child
process has paused or the pipe buffer is full, `WriteFile` blocks. `WndProc`
never returns → the CLI's bounded `SendMessageTimeout` ages out → user sees
"GUI unresponsive" even though the GUI is **not** unresponsive — only this one
handler is.

**Fix — move handler body off the UI thread; respond from a worker**

```csharp
private void HandleTerminalSend(int gi, int ti, string text)
{
    var captured = (gi, ti, text);
    _ = Task.Run(() =>
    {
        // … resolve session, attempt write, build resultJson …
        IpcMemoryMappedResponseWriter.WriteJson(
            TerminalSendMmfName, TerminalSendMmfSize, resultJson, ...);
    });
    // WndProc returns immediately; CLI MMF poll covers the latency
}
```

The CLI side already polls the MMF up to `_timeoutMs`, so the round-trip
latency budget shifts from "WM_COPYDATA hard block" to "MMF poll soft wait" —
which is what we want.

Apply per-handler. Start with `HandleTerminalSend` / `HandleTerminalKey`
(both write to the PTY — the riskiest); `HandleTerminalList` /
`HandleStatus` are pure reads of in-process state and don't justify the
threading overhead.

---

## Pitfall 3 — `[STAThread]` assumption: never call UIA from a non-STA thread

**Symptom**

`OsCliCommands.ElementTree` / `os text-capture` invoked from inside the GUI
(LLM toolbelt path) deadlocks, while the same call from CLI (separate process,
main-thread STA) works.

**Root cause**

`System.Windows.Automation` requires a single-threaded apartment. The CLI main
thread of a WinExe is STA by default; threadpool workers are MTA. COM marshal
across apartments needs the source thread to pump messages — most worker
threads don't.

`OsControlService.ElementTreeAsync` does this correctly:

```csharp
// fresh STA thread, .Join() to await
var t = new Thread(() => local = ElementTreeScanner.Scan((IntPtr)hwnd, maxDepth));
t.SetApartmentState(ApartmentState.STA);
t.Start();
t.Join();
```

`OsControlService.TextCapture` does **not** — it calls `ElementTreeScanner.Scan`
directly, which works only because the current callers happen to be on STA
threads. If a future caller routes through the LLM toolbelt's async pipeline
(MTA threadpool), it will deadlock.

**Fix**

Apply the same STA marshalling pattern in `TextCapture`. Treat any call to
`System.Windows.Automation` (or to a helper that wraps it) as if it were a COM
call — it is.

---

## Pitfall 4 — `Process.Start … -NoNewWindow -Wait` traps the parent shell

**Symptom**

`AgentZeroLite.ps1 <cmd>` hangs on the user's prompt even after the CLI
process exits. Ctrl+C in PowerShell does nothing.

**Root cause**

`Project/AgentZeroWpf/AgentZeroLite.ps1`:

```powershell
$proc = Start-Process -FilePath $ExePath -ArgumentList ... -NoNewWindow -Wait -PassThru
```

`Start-Process -Wait` means PowerShell blocks until the child exits. If P1
manifests in the child, P5 inherits it: PowerShell hangs as long as the child
hangs. Fix P1 first; this entry exists so the symptom isn't misdiagnosed
("PowerShell wrapper bug") when the real cause is upstream.

---

## Reviewer checklist

Apply when a diff touches any of `CliHandler.cs`, `MainWindow.HandleCliCommand`,
`NativeMethods.cs` IPC declarations, `IpcMemoryMappedResponseWriter`, or
`AgentZeroLite.ps1`:

- [ ] **P1** — All `WM_COPYDATA` send paths use `SendMessageTimeoutW`, never
  `SendMessageW`. Timeout ≤ 3000 ms with `SMTO_ABORTIFHUNG`.
- [ ] **P1** — `SendWpfCommand` (or any equivalent helper) returns `bool`,
  and every caller short-circuits on `false`.
- [ ] **P2** — New WndProc handler doesn't synchronously call into ConPTY
  `WriteToTerm`, LLM model loading, file I/O, or any operation that can wait
  on an external resource. Move to `Task.Run` and respond from there.
- [ ] **P3** — Anything using `System.Windows.Automation` is wrapped in an
  STA-pinned thread (`new Thread(...) { ApartmentState = STA }` + `Join`).
- [ ] **P4** — If `AgentZeroLite.ps1` was touched, verify it still exits
  cleanly when the child errors, and that `-Wait` is justified (it's required
  for stdio capture, but it does mean shell hangs follow CLI hangs).
- [ ] Diagnostic — when a timeout fires, the CLI prints a unique error message
  the operator can grep. Don't fail silently.

## Cross-reference

- RCA log: `harness/logs/code-coach/2026-05-10-07-51-cli-block-recurrence-rca.md`
  (the canonical post-mortem; this knowledge file is the distilled checklist)
- Terminal-side wedges (different class, often confused with this one):
  `harness/knowledge/code-coach/terminal-wedge-triage.md`
- ConPTY input-path freeze diagnostics: `Project/AgentZeroWpf/Services/ConPtyTerminalSession.cs`
  (search for `PTY-FREEZE-DIAG`)
