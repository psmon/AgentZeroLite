---
date: 2026-11-19T15:41:15+09:00
agent: build-doctor
type: explanation
mode: log-eval
trigger: "에이전트제로 빌드가 어떻게 되는지 분석해"
---

# AgentZero build pipeline — how it works today (analysis)

## 실행 요약

Read-only analysis of the build and release path as of `main` @ 234edc8 (v0.27.1 released 2026-11-14).
Sources read: `Project/AgentZeroAvalonia/AgentZeroAvalonia.csproj`, `Project/AgentZeroWpf/AgentZeroWpf.csproj`,
`Project/ZeroWearable/ZeroWearable.csproj`, `Project/ZeroCommon/ZeroCommon.csproj` (package/native part),
`.github/workflows/{release,release-wpf,avalonia-build}.yml`, `installer/AgentZeroLite.iss`,
`Project/AgentZeroAvalonia/msix/build-msix.ps1`, `.claude/skills/agent-zero-build/SKILL.md`,
`harness/engine/release-build-pipeline.md`, this agent's own definition.
No `dotnet build` was run — the request was an explanation, and the working tree carries uncommitted
Agent Stage work that a build result would not describe the released state of.

## 결과

### 1. Project graph

```
AgentZeroAvalonia (net10.0, official host) ─┐
AgentZeroWpf (net10.0-windows, reference) ──┼─► ZeroCommon (net10.0; LLamaSharp + checked-in llama.cpp natives,
ZeroWearable (net10.0-windows10.0.19041.0) ─┘                 Whisper.net, ORT 1.23.2, sherpa-onnx 1.13.8, EF Core …)
Both GUI hosts → ZeroWearable as build-order-only reference (ReferenceOutputAssembly=false)
  · Build:   CopyWearableHost copies bin/…/wearable/
  · Publish: PublishWearableHost runs a nested `dotnet publish` of ZeroWearable with the same RID + self-contained flag
  · Avalonia: only on Windows and win-* RID, and IncludeWearableHost != false
AgentOne — separate product, own workflow (agent-one-release.yml), references nothing.
```

### 2. Version

- Single source: `Project/version.txt` (now 0.27.1), read by both hosts at evaluation time
  (`AppVersion` → Version/AssemblyVersion/FileVersion) and copied next to the exe.
- Local WPF **Release** build bumps it (`BumpVersionAfterBuild`: patch+1; patch≥10 → minor+1; minor≥10 → major+1).
  The Avalonia csproj has **no** bump target — a local Avalonia Release build leaves the file alone.
- CI never trusts the file for a tag: `release.yml` resolves the version from the tag (`v*`) and
  stamps it into version.txt in the runner only.
- The `agent-zero-build` skill bumps version.txt deliberately (commit "chore: bump version to X.Y.Z"), then pushes an
  annotated tag `vX.Y.Z` — the tag push is what fires the release.

### 3. Release (`release.yml`, tag `v*` or manual with publish=false)

| Job | Runner | Does |
|---|---|---|
| version | ubuntu | tag → version |
| windows | windows-latest | stamp → `dotnet publish` Avalonia win-x64 self-contained (+wearable) → `-cli version` / `-cli selftest all` / wearable exe present → ZIP → `iscc installer/AgentZeroLite.iss` → artifact |
| macos | macos-15 | publish osx-arm64 → selftest → `.app` (build-app.sh) → if secrets: Developer ID sign, `.pkg`, notarize |
| linux | ubuntu-24.04 | publish linux-x64 → selftest → `.deb` + `.tar.gz` → apt-install own .deb and run it |
| release | ubuntu | needs only windows=success; attaches mac/linux files if they exist; Latest |

Installer: AppId kept from the WPF installer (upgrades in place), `AppMutex=Local\AgentZeroLite.SingleInstance`,
per-user install, WebView2 bootstrapper downloaded on the Ready page (non-fatal), previous install's files removed first.

### 4. Other build lanes

- `release-wpf.yml` — manual only; WPF publish + `AgentZeroLite-WPF.iss`, optionally attached to an existing tag.
- `avalonia-build.yml` — `ci-*` tag or manual; the **only** CI that runs unit tests (ZeroCommon.Tests +
  AgentZeroAvalonia.Tests) on Windows/macOS/Linux. `release.yml` runs no tests, only `-cli selftest all`.
- MSIX (`msix/build-msix.ps1`, local) — publish with `IncludeWearableHost=false -p:IncludeRemote=false`,
  refuses a stage containing `Wasm\remote`, makepri + makeappx (+ signtool if a cert is given).
- Mac App Store — `macos/appstore/build-appstore.sh`, `IncludeRemote=false`.

### 5. Findings (drift between the harness and the code)

1. **build-doctor's own definition is stale.** Scope 2 / Procedure 2 check `<Content Include>` pins for
   `conpty.dll` / `Microsoft.Terminal.Control.dll` under `$(NuGetPackageRoot)` — those are gone (ManagedConPtyHost,
   per CLAUDE.md "Terminal"). The native files that *do* matter now are the checked-in llama.cpp DLLs under
   `Project/ZeroCommon/runtimes/win-x64-{cpu,vulkan}/native/` (9 tracked files, all present). Procedure 1 also builds
   only the WPF project, which is no longer what ships.
2. **Stale comment in WPF csproj**: `NoWarn NETSDK1206` is justified by `CI.Microsoft.Windows.Console.ConPTY`,
   a package no longer referenced.
3. **Release gate has not been exercised for months.** Newest `build-doctor` log 2026-05-03 (v0.3.2), newest
   `release-build-pipeline` log 2026-04-30; newest `security-guard` log 2026-09-18. 569 commits and many tags since
   (v0.27.1 on 2026-11-14). The engine says the skill "must" run it; the skill text never mentions the engine or
   security-guard. The gate exists on paper only.
4. **agent-zero-build skill text lags the product**: describes win-x64 ZIP + installer only (release also ships
   macOS .pkg and Linux .deb/.tar.gz), its "conpty.dll as Content" recovery tip refers to removed files, and its
   commit example names an old model.
5. **No tests on the release path.** By policy build-doctor does not run tests, and release.yml does not either;
   the only test-running CI (`avalonia-build.yml`) is opt-in via `ci-*`. A release can ship with a red suite.
6. **Two bump behaviours.** WPF Release bumps version.txt locally, Avalonia Release does not. Harmless for CI
   (stamped from the tag), but a local `-c Release` WPF build dirties a tracked file.

## 평가

| Axis | Result |
|---|---|
| Build correctness | not measured (no build run — explanation only) |
| Native DLL pinning | Pass — llama.cpp natives present on disk; the ConPTY pins the rubric names no longer exist |
| Version pipeline integrity | B — tag → stamp → binary/installer agree; local bump asymmetry and skill/engine wording drift |
| Security-gate compliance (release only) | Fail (process) — no gate log for any release after v0.3.2 |
| Scope discipline | Pass — `dotnet test` not invoked |

## 다음 단계 제안

- Rewrite `harness/agents/build-doctor.md` scope around the Avalonia host: build Avalonia (Debug + `-cli selftest all`),
  check `ZeroCommon/runtimes/*/native` and the wearable copy/publish targets, drop the ConPTY pin check.
- Decide whether the release gate is real: either wire `agent-zero-build` to call `release-build-pipeline`
  (security-guard first), or relax the engine's "must" so the harness stops claiming a gate that is not run.
- Refresh `agent-zero-build/SKILL.md` (artifacts per OS, remove conpty tip).
- Optionally drop the stale `NETSDK1206` NoWarn from the WPF csproj after a build confirms the warning is gone.
