---
date: 2026-04-27T20:10:00+09:00
agent: code-coach
type: review
mode: log-eval
trigger: "묶음 커밋해 (PATH/CLI debuggability rollout)"
target: "IsAgentZeroPath cleanup + ConPTY PATH prefix + --version CLI + version.txt runtime read"
---

# Pre-commit Review — PATH/CLI debuggability bundle

## Verdict

**OK to commit.** 0 must-fix, 0 should-fix, 3 advisory suggestions
(filed as `psmon/AgentZeroLite#2`). All four changes target the same
debugging story (which build is responding? which PATH wins?) and were
verified empirically before the commit:

- `where.exe AgentZeroLite.ps1` → single Lite Debug match
- `cmd /c "set "PATH=...&%PATH%"&&pushd "..."&&..."` cmd-quote form
  validated via batch-file smoke test
- `AgentZeroLite.exe -cli --version` → `v0.1.4` (matches `version.txt`)
- `dotnet build` clean (0 errors)

## Change set

| File | Type | Concern |
|---|---|---|
| `Project/AgentZeroWpf/UI/Components/SettingsPanel.xaml.cs` | edit | `IsAgentZeroPath` recognizes `AgentZeroLite.exe` only, treats absent `AgentZero*` directories (without `Wpf`/`Win` markers) as stale. AgentWin entries auto-preserved. |
| `Project/AgentZeroWpf/UI/APP/MainWindow.xaml.cs` | edit | ConPTY spawn prepends `set "PATH={appDir};%PATH%"` so the inner shell always sees this build's directory first, regardless of User PATH state. |
| `Project/AgentZeroWpf/CliHandler.cs` | edit | New `version` / `--version` / `-v` command, `--help` header advertises build identity, `GetSelfExePath()` helper deduplicates the two existing exe-path lookups. |
| `Project/AgentZeroWpf/AgentZeroWpf.csproj` | edit | `version.txt` ships as `Content` with `PreserveNewest` so the CLI can read it at runtime. |
| `Project/ZeroCommon/Module/AppVersionProvider.cs` | edit | Lookup order: runtime `version.txt` → `Assembly.GetEntryAssembly()` attribute → `Assembly.GetExecutingAssembly()` attribute. Fixes the prior bug where the helper read ZeroCommon's default `1.0.0` instead of the WPF host's `version.txt`-injected value. |

## Findings (3 advisory)

See issue #2 for full snippets. Summary:

- **S-A** — `workDir` double-quote corner case (mostly hypothetical)
- **S-B** — `rawCmd` nested-quote depth if a future `CliDefinition` ships
  pre-quoted args
- **S-C** — `AppVersionProvider`'s broad `catch { }` could narrow to
  `IOException` / `UnauthorizedAccessException` / `SecurityException`

All three are micro-scale, defensive-coverage upgrades. No observed
failure on today's workflows.

## 4-lens cross-stack judgment

| Lens | Issues |
|---|---|
| .NET modern | None — record/init/pattern usage idiomatic, broad `catch` flagged as S-C |
| Akka.NET | Untouched (changes live in WPF + ZeroCommon helper) |
| WPF | The ConPTY spawn change is the only WPF-side touch; preserves the existing `EasyTerminalControl.StartupCommandLine` contract |
| LLM integration | Indirect — fixing the PATH/CLI identity question removes a class of "which build is the bot talking to?" debugging dead-ends |
| Windows native | The `cmd /c "set ..."` form is the supported nested-quote pattern; verified via batch-file smoke test before the patch went in |

## Owned-convention check

- `harness/knowledge/llm-prompt-conventions.md` R-1: ✅ no LLM prompts
  touched in this bundle
- `harness/knowledge/agent-origin-reference.md`: ✅ this bundle isn't an
  Origin-derived adoption — it's a standalone Lite-specific debuggability
  improvement (origin is the "two Claude tabs differ" log diagnosis from
  the user's session, not an Origin-snapshot recommendation)

## Test verification

```
dotnet build Project/AgentZeroWpf/AgentZeroWpf.csproj -c Debug
  → 0 errors, 7 pre-existing warnings (unrelated)
AgentZeroLite.exe -cli --version
  → v0.1.4
  → exe : D:\Code\AI\AgentZeroLite\Project\AgentZeroWpf\bin\Debug\net10.0-windows\AgentZeroLite.exe
  → base: D:\Code\AI\AgentZeroLite\Project\AgentZeroWpf\bin\Debug\net10.0-windows\
where.exe AgentZeroLite.ps1
  → D:\Code\AI\AgentZeroLite\Project\AgentZeroWpf\bin\Debug\net10.0-windows\AgentZeroLite.ps1
batch smoke test of the ConPTY cmdLine
  → PATH prefix ordering correct, AgentZeroLite.ps1 resolves to this build
```

## Recommendation

Commit as a single bundle (user explicitly chose grouping because the
four changes share one diagnostic story). Reference issue #2 in the
commit message.

## Related

- Diagnosis log (the original "two Claude tabs differ" investigation that
  motivated this bundle): see the user-session transcript leading up to
  this review.
- Issue handoff: psmon/AgentZeroLite#2
