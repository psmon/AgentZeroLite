# Node.js Wrapper Pitfalls — bash · child_process · file & stream IO on Windows

> Owner: **`code-coach`** (primary) — these are the patterns to flag during pre-commit
> review whenever a diff touches `Project/AgentZeroWpf/Services/Browser/StatusLineWrapperInstaller.cs`,
> any embedded Node script, or any new "wrapper" that proxies stdin/stdout
> between two processes.
> Cross-reference: **`security-guard`** (secondary) — wrapper boundaries are
> attack surfaces for command injection if quoting is wrong.

This document captures the failure-mode catalog discovered while building
the M0011 token-remaining statusLine wrapper (claude-hud chain pipe-through).
Every pitfall below corresponds to an actual failure observed in the
`%LOCALAPPDATA%\AgentZeroLite\cc-hud-snapshots\_wrapper.log` of that
work — diagnoses are not theoretical.

The wrapper pattern is going to recur whenever AgentZero needs to hook
into another tool's invocation surface (Claude Code statusLine, Codex
hooks, future Bedrock proxies, etc.), so getting the contract right once
matters more than the immediate use case.

---

## Pitfall 1 — `bash` resolves to WSL, not Git Bash

**Symptom**

```
[claude] pipe spawn program=bash argc=2
[claude] pipe exit=3221225794                ← 0xC0000142 STATUS_DLL_INIT_FAILED
```

The exit code `3221225794` is `0xC0000142` (Windows `STATUS_DLL_INIT_FAILED`).
WSL's `bash.exe` cannot initialise inside a non-interactive child of an
arbitrary Win32 parent — the LXSS init path needs the parent to be a
console host it recognises.

**Root cause**

`where bash` on a typical dev machine returns:

```
/usr/bin/bash                     ← Git Bash (correct for our scripts)
/c/Windows/system32/bash          ← WSL bash (the trap)
/c/Users/.../WindowsApps/bash     ← Microsoft Store re-direct
```

`cp.spawn('bash', args, {...})` resolves through Windows PATH and may
land on the WSL one depending on parent environment. In a Node child of
the Claude Code statusLine spawn, the WSL bash often wins.

**Fix — pin Git Bash explicitly**

```js
let program = argv[0];
if (program === 'bash' && process.platform === 'win32') {
  const candidates = [
    process.env.PROGRAMFILES + '\\Git\\bin\\bash.exe',
    process.env['PROGRAMFILES(X86)'] + '\\Git\\bin\\bash.exe',
    process.env.LOCALAPPDATA + '\\Programs\\Git\\bin\\bash.exe',
  ];
  for (const c of candidates) {
    try { if (fs.existsSync(c)) { program = c; break; } } catch {}
  }
}
```

**Pre-commit check**

A diff that adds `cp.spawn('bash', ...)` or `cp.spawn('sh', ...)` MUST
also include the Git Bash candidate-path resolution, or call out
explicitly that WSL bash is the intended target.

---

## Pitfall 2 — `cp.spawn(cmd, [], { shell: true })` shreds nested quotes

**Symptom**

A bash one-liner that runs perfectly from an interactive terminal fails
silently when invoked through `cp.spawn(pipe, [], { shell: true })`.
Stderr (when captured) shows `bash: -c: line 0: unexpected EOF` or
similar parse errors.

**Root cause**

Node's `shell: true` on Windows wraps the command as `cmd /c "<command>"`.
**`cmd` does not understand single quotes** — they're just literal
characters. So a bash trick like:

```
bash -c 'plugin_dir=$(ls -d "${HOME}"/x/*/) ; exec "/c/node" "${plugin_dir}main.js"'
```

When passed through `cmd /c`, cmd splits the string on whitespace
without honouring `'…'` boundaries. bash receives `-c` followed by the
first whitespace-bounded token and chokes.

**Fix — parse argv ourselves, skip the shell**

```js
function parseArgv(s) {
  const out = [];
  let i = 0, n = s.length;
  while (i < n) {
    while (i < n && /\s/.test(s[i])) i++;
    if (i >= n) break;
    let token = '';
    while (i < n && !/\s/.test(s[i])) {
      const c = s[i];
      if (c === '"') {
        i++;
        while (i < n && s[i] !== '"') {
          if (s[i] === '\\' && i + 1 < n && (s[i+1] === '"' || s[i+1] === '\\')) {
            token += s[i+1]; i += 2;
          } else token += s[i++];
        }
        if (i < n && s[i] === '"') i++;
      } else if (c === "'") {
        // POSIX: single quote is literal, no escapes inside.
        i++;
        while (i < n && s[i] !== "'") token += s[i++];
        if (i < n && s[i] === "'") i++;
      } else token += s[i++];
    }
    out.push(token);
  }
  return out;
}

const [program, ...args] = parseArgv(commandLine);
const child = cp.spawn(program, args, { /* no shell */ });
```

This produces `args = ['-c', '<the entire bash script verbatim>']` —
exactly what bash expects. Node's spawn passes `args` through Windows
argv parsing rules unchanged when `shell:false`.

**Pre-commit check**

Any `cp.spawn(_, _, { shell: true })` in a wrapper context is a smell.
Acceptable only when the command is fixed-shape and quote-free
(`cp.spawn('echo hello', [], {shell:true})`). For any user-supplied or
config-supplied command, parse argv first.

---

## Pitfall 3 — Git Bash needs MSYS-form HOME

**Symptom**

bash spawned from a Node child has `$HOME` either empty or set to a
Windows path like `C:\Users\you`. Scripts that probe `"$HOME/.config/..."`
get either empty results or path-handling errors:

```
[claude] pipe bash-script[56B]="plugin_dir=; exec \"/c/node\" \"dist/index.js\""
                                            ^^^^^^^^^^^ glob returned nothing
```

**Root cause**

Git Bash's interactive `$HOME` is `/c/Users/<name>` (MSYS form). It's
set by `/etc/profile` at login. A non-interactive bash spawned from
Node inherits the parent's `HOME` env, which on Windows is often empty
or set to a Windows-form path. Globs like `"$HOME"/.claude/plugins/*`
then fail to expand correctly.

**Fix — convert USERPROFILE to MSYS form, set HOME explicitly**

```js
let childEnv = process.env;
if (program.endsWith('bash.exe') && process.env.USERPROFILE) {
  // C:\Users\psmon  →  /c/Users/psmon
  const w = process.env.USERPROFILE;
  const msysHome = (w.length >= 2 && w[1] === ':')
    ? '/' + w[0].toLowerCase() + w.slice(2).replace(/\\/g, '/')
    : w;
  childEnv = Object.assign({}, process.env, { HOME: msysHome });
}
const child = cp.spawn(program, args, { env: childEnv, ... });
```

**Pre-commit check**

Any wrapper that spawns bash AND inherits env from a Win32 parent
should also set `HOME` to the MSYS form of `USERPROFILE`. Document this
in a comment so the next reviewer knows it's not redundant.

---

## Pitfall 4 — Claude Code statusLine substitutes `${VAR}` and `$(...)` even inside single quotes

**Symptom (extreme)**

A 300-byte bash script registered in `settings.json` arrives at our
wrapper as a 56-byte truncated mess:

```
[claude] pipe bash-script[56B]="plugin_dir=; exec \"/c/node\" \"dist/index.js\""
```

`$(ls -d ...)` and `${plugin_dir}` were substituted to **empty string**
before the wrapper saw them.

**Root cause**

Claude Code performs its own variable / command substitution on the
**entire `statusLine.command` string** before spawning. This applies
**even inside single quotes** — bash quoting rules don't apply, because
Claude Code never invokes bash for this substitution; it does its own
template processing first.

So this — registered as one of our wrapper's `--pipe` args:

```
node wrapper.js --pipe "bash -c '...$(ls -d ...) ${VAR}...'"
```

— becomes, by the time bash sees it:

```
bash -c '...{empty} {empty}...'
```

The original claude-hud command worked because it WAS the statusLine —
nothing else in the command string contained `$(...)` or `${VAR}` for
substitution to attack. Wrapping it added our quote layer, and inlining
the original payload as our `--pipe` arg made it visible to substitution.

**Fix — sidecar file pattern**

Don't inline complex commands into the statusLine string. Write them to
a sidecar file and pass the file PATH (which contains no `$` chars):

```csharp
// Installer side — write the raw command to a sidecar
File.WriteAllText(pipeFilePath, originalCommand);    // atomic via temp+rename
// settings.json statusLine.command becomes:
//   node "wrapper.js" --account claude --pipe-file "<pipeFilePath>"
```

```js
// Wrapper side — read the file
const pipeFile = arg('--pipe-file');
let pipe = arg('--pipe');
if (!pipe && pipeFile) {
  try { pipe = fs.readFileSync(pipeFile, 'utf8').replace(/^﻿/, '').trim(); }
  catch { /* log + exit gracefully */ }
}
```

**Pre-commit check**

Any wrapper command persisted to a host config (settings.json,
launch.json, registry, etc.) that needs to carry a complex shell
fragment MUST use a sidecar file for the fragment. Inline payloads are
a substitution-attack risk even when the host's substitution rules
look benign.

---

## Pitfall 5 — Inheriting child stderr makes diagnosis impossible

**Symptom**

A child process exits with code 1 and there is no usable diagnostic in
your wrapper's log. The child's stderr message exists but went to the
parent's stderr — which in a statusLine context is silently discarded
by the host.

**Root cause**

`stdio: ['pipe', 'inherit', 'inherit']` is the default ergonomic
choice for a stdout-passthrough wrapper, but it routes the child's
stderr to your inherited stderr, which the parent (e.g. Claude Code)
may not show anywhere.

**Fix — capture stderr to your own log**

```js
const child = cp.spawn(program, args, {
  stdio: ['pipe', 'inherit', 'pipe'],  // stderr → us
  ...
});
let stderrBuf = '';
child.stderr.on('data', (d) => {
  stderrBuf += d.toString();
  if (stderrBuf.length > 4096) stderrBuf = stderrBuf.slice(-4096);
});
child.on('exit', code => {
  dlog(`exit=${code}` + (stderrBuf ? ` stderr=${JSON.stringify(stderrBuf.trim().slice(0, 600))}` : ''));
  process.exit(code || 0);
});
```

This is the single most valuable diagnostic primitive for any
wrapper. M0011's "claude-hud silently broken for hours" became a
clear `Cannot find module 'D:\...\dist\index.js'` the moment stderr
was captured.

**Pre-commit check**

Any new `cp.spawn` in a wrapper SHOULD use `['pipe', _, 'pipe']` and
log captured stderr on non-zero exit. Inheriting stderr is acceptable
only for interactive tools whose output the user is staring at.

---

## Pitfall 6 — `child.stdin.write` without error guard crashes on early-close

**Symptom**

Wrapper crashes intermittently with `Error: write EPIPE`. Reproduces
when the downstream child exits before consuming all of stdin (e.g.
claude-hud detects an error and bails on line 1).

**Root cause**

When the OS pipe to a child closes, writes to it raise SIGPIPE on
POSIX or `EPIPE` errors on Windows. Node surfaces this as an `'error'`
event on `child.stdin`. With no listener, it becomes an uncaught
exception and kills the wrapper.

**Fix — silent error guard + try/catch around writes**

```js
const child = cp.spawn(...);
child.stdin.on('error', () => {});      // pipe might break if downstream is fast
try { child.stdin.write(stdin); } catch {}
try { child.stdin.end(); }   catch {}
```

The `try/catch` covers the synchronous case; the `'error'` handler
covers the async case (write succeeds, but `end()` triggers a flush
that detects the closed pipe).

**Pre-commit check**

Every `child.stdin.write(...)` in a wrapper must be paired with an
error guard. Lint this with a grep:

```bash
git diff --cached -U0 | grep -E '^\+.*child\.stdin\.write\(' | \
  grep -v -E '^\+.*on\(.error.,'  # report writes without prior error handler
```

---

## Pitfall 7 — File writes that aren't atomic corrupt readers

**Symptom**

Reader (collector) occasionally sees half-written JSON:

```
SyntaxError: Unexpected end of JSON input
```

…even though the writer "completed" the write.

**Root cause**

`fs.writeFileSync(target, ...)` writes in chunks. A reader opening the
file mid-write sees a truncated prefix. This is rare but inevitable in
a tight producer/consumer loop (statusLine wrapper writes every
~300ms; collector reads every 30s).

**Fix — temp-write + rename, with copy fallback**

```js
const tmp = target + '.tmp';
fs.writeFileSync(tmp, payload);
try {
  fs.renameSync(tmp, target);     // atomic on POSIX, mostly atomic on Windows
} catch {
  // Windows can fail rename if target is being read with FILE_SHARE_READ but
  // not FILE_SHARE_WRITE. Fall back to copy + unlink — non-atomic but
  // recovers from the race.
  try { fs.copyFileSync(tmp, target); fs.unlinkSync(tmp); } catch {}
}
```

For the C# reader side, mirror with `FileShare.ReadWrite | FileShare.Delete`
so the rename / unlink doesn't fail because the reader has the file open:

```csharp
using var fs = new FileStream(file, FileMode.Open, FileAccess.Read,
    FileShare.ReadWrite | FileShare.Delete);
```

**Pre-commit check**

Any new `fs.writeFileSync` to a file that another process tails or
polls MUST go through `temp + rename` with a copy fallback. Naked
`writeFileSync` to a shared file is a race waiting to happen.

---

## Pitfall 8 — Diagnostic logs that grow unbounded

**Symptom**

`_wrapper.log` is multi-GB. SSD wear + slow disk. User can't `tail`
without a long wait.

**Root cause**

The wrapper appends every tick. With Claude Code's 300ms statusLine
cadence, that's ~12,000 entries / hour.

**Fix — rolling self-trim**

```js
function dlog(msg) {
  try {
    const line = new Date().toISOString() + ' [' + account + '] ' + msg + '\n';
    let existing = '';
    try { existing = fs.readFileSync(logFile, 'utf8'); } catch {}
    const combined = existing + line;
    const trimmed = combined.length > 64 * 1024
      ? combined.slice(combined.length - 48 * 1024)   // keep last 48KB
      : combined;
    fs.writeFileSync(logFile, trimmed);
  } catch {}
}
```

Trade-off: each log call is a full read + rewrite. Fine for low-volume
diagnostic logs (tens per minute). For higher volume, rotate via
sequence-numbered files (`log.0`, `log.1`, …).

**Pre-commit check**

Any new wrapper diagnostic log MUST have a size cap, EITHER via this
self-trim pattern OR an external rotation policy. Unbounded
`appendFileSync` to a hot path is unacceptable.

---

## Pitfall 9 — Windows native exit codes look like garbage

**Symptom**

`pipe exit=3221225794` — meaningless integer in your logs.

**Root cause**

Windows process exit codes are NTSTATUS values. The "garbage" integer
is actually a hex code that means something specific.

**Fix — humanise common Windows status codes at log time**

```js
const wellKnown = {
  3221225477: '0xC0000005(ACCESS_VIOLATION)',
  3221225794: '0xC0000142(DLL_INIT_FAILED)',
  3221225495: '0xC0000017(NO_MEMORY)',
  3221225725: '0xC000007B(BAD_IMAGE_FORMAT)',  // x86/x64 mismatch
};
function humaniseExit(code) {
  return wellKnown[code] || ('exit=' + code);
}
```

This converts the most common Windows-native failures into something
greppable. `0xC0000142` immediately suggests "wrong runtime / WSL bash
in a non-interactive context", saving an hour of guessing.

**Pre-commit check**

Any wrapper's `child.on('exit', code => log(...))` should pass `code`
through a humaniser. The literal numeric code is fine to keep
alongside (for grep), but the human label is what makes triage fast.

---

## Pitfall 10 — Backup/restore for invasive installs

(Not strictly Node, but lives next to the same wrapper installer.)

**Symptom**

Installing a wrapper irreversibly clobbers the user's existing
configuration (settings.json, registry key, …).

**Fix — capture original, hash it, restore on demand**

At install time:

1. Read current target configuration verbatim, save to
   `<state-dir>/<key>/settings.json.<timestamp>.bak`.
2. Compute sha256 of the original command/value.
3. Compute sha256 of what we're writing.
4. Persist all four (orig text, orig sha256, installed text,
   installed sha256, backup file path) in a state file.

At uninstall time:

1. Read the state file.
2. Read current target configuration's command/value, sha256 it.
3. If equal to `installedCommandSha256` → silent restore.
4. Otherwise → user has manually edited since install. Surface a
   3-way diff (original / current / what-we-would-restore) and require
   explicit confirm before clobbering.

**Pre-commit check**

Any installer that mutates user config MUST follow the
capture-hash-restore pattern. Force-restore-without-diff is acceptable
only with a `--force` flag chosen by an operator who has been shown
the diff.

---

## Quick checklist (paste into PR descriptions)

For wrapper or installer-style changes:

- [ ] If spawning `bash`/`sh` on Windows, Git Bash explicit path resolution present
- [ ] No `cp.spawn(_, _, { shell: true })` for user/config-supplied commands
- [ ] If spawning bash, `HOME` is set to MSYS form of `USERPROFILE`
- [ ] Complex shell payloads stored in sidecar file, not inlined into config strings
- [ ] Child stderr captured (`['pipe', _, 'pipe']`) and logged on non-zero exit
- [ ] `child.stdin.write` paired with `'error'` listener + try/catch
- [ ] All file writes to shared paths use temp + rename with copy fallback
- [ ] All wrapper diagnostic logs have a size cap
- [ ] Exit codes humanised via well-known NTSTATUS table
- [ ] Installer captures original config + sha256 + supports diff-then-confirm uninstall

---

## See also

- M0011 mission record: `harness/logs/mission-records/M0011-수행결과.md`
  (4 회 후속 fix + chain pipe-through fix narrative)
- Reference implementation:
  - `Project/AgentZeroWpf/Services/Browser/StatusLineWrapperInstaller.cs`
  - Embedded wrapper script (search for `private const string WrapperScript`)
- Cross-domain knowledge: `harness/knowledge/code-coach/wpf-xaml-resource-and-window-pitfalls.md`
  (same "rare-trap catalogue" format)
