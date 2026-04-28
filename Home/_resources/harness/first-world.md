# First World — Pointer

> The genesis story of this harness lives in the project Docs tree, not inside the
> harness itself. This file is a pointer so readers landing in `harness/` can find it.

- **Canonical narrative (English):** [`Docs/harness/en/first-world.md`](../Docs/harness/en/first-world.md)
- **Original conversational transcript (Korean):** [`Docs/harness/first-world.md`](../Docs/harness/first-world.md)

## Quick Component Index

| Layer | File | Role |
|-------|------|------|
| Config | [`harness.config.json`](harness.config.json) | Garden identity |
| Agent | [`agents/tamer.md`](agents/tamer.md) | Gardener (meta) |
| Agent | [`agents/security-guard.md`](agents/security-guard.md) | OWASP + injection-aware security review |
| Agent | [`agents/build-doctor.md`](agents/build-doctor.md) | Build pipeline + native DLL pinning |
| Agent | [`agents/test-sentinel.md`](agents/test-sentinel.md) | Headless/WPF boundary + coverage hot spots |
| Agent | [`agents/code-coach.md`](agents/code-coach.md) | Cross-stack reviewer + tech writer + research consultant |
| Engine | [`engine/release-build-pipeline.md`](engine/release-build-pipeline.md) | security-guard → gate → build-doctor |
| Engine | [`engine/pre-commit-review.md`](engine/pre-commit-review.md) | Auto-invoke code-coach before `git commit` |
| Knowledge | [`knowledge/agentzerolite-architecture.md`](knowledge/agentzerolite-architecture.md) | Project structure, dependency rule, actor topology |
| Knowledge | [`knowledge/security-surface.md`](knowledge/security-surface.md) | Prompt-injection → OS-exec map |
| Docs | [`docs/v1.1.0.md`](docs/v1.1.0.md) | Version 1.1.0 changelog |
| Log | [`logs/tamer/`](logs/tamer/) | Gardener activity logs |

For *why* each piece is here and how they wire together, read the canonical narrative.
