# First World — Pointer

> The harness lives entirely under `harness/`. This file is a quick map for
> readers landing here for the first time. (The earlier external genesis
> docs under `Docs/harness/` were removed to avoid contaminating the main
> harness with template content.)

## Quick Component Index

| Layer | File | Role |
|-------|------|------|
| Config | [`harness.config.json`](harness.config.json) | Garden identity |
| Agent | [`agents/tamer.md`](agents/tamer.md) | Gardener (meta) |
| Agent | [`agents/security-guard.md`](agents/security-guard.md) | OWASP + injection-aware security review |
| Agent | [`agents/build-doctor.md`](agents/build-doctor.md) | Build pipeline + native DLL pinning |
| Agent | [`agents/test-sentinel.md`](agents/test-sentinel.md) | Headless/WPF boundary + coverage hot spots |
| Agent | [`agents/test-runner.md`](agents/test-runner.md) | Explicit-trigger `dotnet test` execution |
| Agent | [`agents/code-coach.md`](agents/code-coach.md) | Cross-stack reviewer + tech writer + research consultant |
| Engine | [`engine/release-build-pipeline.md`](engine/release-build-pipeline.md) | security-guard → gate → build-doctor |
| Engine | [`engine/pre-commit-review.md`](engine/pre-commit-review.md) | Auto-invoke code-coach before `git commit` |
| Engine | [`engine/mission-dispatch.md`](engine/mission-dispatch.md) | M{NNNN} mission lifecycle orchestration |
| Engine | [`engine/harness-view-publish.md`](engine/harness-view-publish.md) | Tag-gated Pages publish workflow |
| Knowledge | [`knowledge/README.md`](knowledge/README.md) | Per-agent knowledge layout map |
| Knowledge | [`knowledge/_shared/agentzerolite-architecture.md`](knowledge/_shared/agentzerolite-architecture.md) | Project structure, dependency rule, actor topology |
| Knowledge | [`knowledge/security-guard/security-surface.md`](knowledge/security-guard/security-surface.md) | Prompt-injection → OS-exec map |
| Log | [`logs/tamer/`](logs/tamer/) | Gardener activity logs |
