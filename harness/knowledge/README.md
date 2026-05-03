# harness/knowledge — per-agent expert knowledge

This is the **sunlight layer** of the harness (per `tamer.md` metaphor).
Each `.md` here is domain expertise an agent reaches for to do its
work — *what's correct*, *why*, *how the field thinks about this*.

## Layout (since 2026-05-04)

Knowledge is owned by the agent that uses it most. Cross-cutting docs
that every agent reads sit in `_shared/`.

```
harness/knowledge/
├── _shared/
│   └── agentzerolite-architecture.md       # project map every agent anchors against
├── tamer/
│   ├── missions-protocol.md                 # mission file/log contract
│   └── agent-origin-reference.md            # AgentWin lookup procedure
├── code-coach/
│   ├── llm-prompt-conventions.md            # prompts default to English (R-1..R-5)
│   ├── wpf-xaml-resource-and-window-pitfalls.md   # 6 review-time XAML traps
│   ├── avalondock-float-redock-patterns.md  # AvalonDock 4.72 float/redock
│   ├── ondevice-tool-calling-survey.md      # Gemma 4 vs Nemotron Nano survey
│   └── cases/                               # narrative incident records
├── security-guard/
│   ├── security-surface.md                  # prompt-injection → OS-exec map
│   └── crash-dump-forensics.md              # *.stackdump / *.dmp playbook
├── test-runner/
│   ├── unit-test-policy.md                  # explicit-trigger-only rule
│   └── dotnet-test-execution.md             # serialization, host-cleanup
└── test-sentinel/
    └── voice-roundtrip-testing.md           # TTS↔STT methodology
```

`build-doctor/` is reserved — currently has no agent-specific knowledge
file dedicated to it (its operational rules live in the agent file
itself + `harness/engine/release-build-pipeline.md`).

## Rules — what knowledge is, what it isn't

1. **Knowledge documents the contract / domain expertise.** Procedures,
   thresholds, anti-patterns, references, comparative surveys. Static
   facts a new agent of the same type would need to do its job.

2. **Knowledge does NOT carry trigger phrases.** *When* an agent
   activates is owned by `harness/agents/<name>.md` (frontmatter
   `triggers:`). *How* the workflow flows is owned by
   `harness/engine/<name>.md`. Knowledge is the **what / why** layer
   only — drop trigger tables, "Trigger on:" sections, and "type
   `xyz` to start" lines on sight.

3. **Owner = primary agent.** Multi-agent docs go under the primary
   owner; cross-references for secondary readers stay as inline links.
   Genuinely cross-cutting docs (architecture map) go to `_shared/`.

4. **Don't move docs casually.** `git mv` preserves history; if you do
   move, fix every backlink in `agents/`, `engine/`, the skills under
   `.claude/skills/`, and the `Docs/harness/en/first-world.md` welcome
   page.

5. **The viewer reads recursively.** `harness/knowledge/**/*.md` is
   scanned by `Home/harness-view/scripts/build-indexes.js`, so new
   subdirectories don't need any indexer change — they show up in the
   Knowledge → Expert tree automatically.

## When to add a new file vs extend an existing one

- **New file** — a topic that's its own bounded domain (a new pitfall
  set, a new comparative survey, a new policy with its own override
  protocol).
- **Extend existing** — a new pitfall in an established collection,
  another rule appended to a policy, a new case under
  `code-coach/cases/`.

## Cross-references checklist

When you add or rename a knowledge file, update:

- [ ] Primary owner agent file (`harness/agents/<name>.md` "Owned
      convention sets" or similar section)
- [ ] Any engine that references it
      (`harness/engine/<name>.md` "Cross-references")
- [ ] Other knowledge files with cross-refs
- [ ] `Docs/harness/en/first-world.md` welcome page (public-facing
      tour)
- [ ] Memory files under `~/.claude/projects/.../memory/` if the
      knowledge enforces a binding rule

The viewer indexes refresh automatically on the next build.
