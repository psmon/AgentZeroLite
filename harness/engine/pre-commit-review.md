---
name: pre-commit-review
agents: [code-coach]
triggers:
  - "review staged changes"
  - "스테이징 점검해"
auto_invoke_on:
  - condition: "Claude is about to run `git commit` AND staged diff includes dev code"
    dev_code_globs:
      - "*.cs"
      - "*.xaml"
      - "*.xaml.cs"
      - "*.csproj"
      - "*.props"
      - "*.targets"
    skip_when_staged_only:
      - "*.md"
      - "Docs/**"
      - "*.png"
      - "*.jpg"
      - "*.svg"
      - "*.gif"
      - "*.txt"
description: Auto-invokes code-coach to review staged dev-code diff before a `git commit` runs. Advisory, not blocking — user can override with "ignore and commit".
---

# Pre-Commit Review

## Why this engine exists

The Coach is most useful when it sees code right before it lands. Asking the user to
remember to invoke a reviewer every commit is unrealistic. This engine moves that step
from "remember to" to "happens automatically".

## Trigger condition (the rule Claude follows)

When Claude is about to call `git commit` (any variant — `git commit -m`, `git commit
--amend`, `git commit -F file`):

1. Run `git diff --cached --name-only` to enumerate staged files.
2. Classify the staged set:
   - **Doc-only** — every staged path matches `skip_when_staged_only` globs above.
     → **Skip the engine.** Proceed directly to `git commit`.
   - **Has dev code** — at least one staged path matches `dev_code_globs` above.
     → **Run the engine.**
3. Engine runs Mode 2 (Auto pre-commit review) of `code-coach`:
   - Reads the staged diff and the surrounding code.
   - Produces inline findings: file:line, severity, suggested rewrite.
4. Surface findings to the user **before** committing:
   - If findings are non-blocking or none → ask user "proceed with commit?"
   - If user says "ignore and commit" / "그냥 커밋해" / "commit anyway" → proceed.
   - Otherwise → apply the fixes (or let user fix), `git add` again, then commit.

## Output

- **Pre-commit log** — `harness/logs/code-coach/{yyyy-MM-dd-HH-mm-precommit-title}.md`
  (one per commit run, even if no findings).
- **Engine log** — `harness/logs/pre-commit-review/{yyyy-MM-dd-HH-mm-title}.md`
  recording: trigger phrase that initiated the commit, files staged, decision (skip /
  reviewed-clean / reviewed-with-findings / waived).

## Not a hook

This engine is **not** a `.claude/settings.json` PreToolUse hook. It is a behavioral
convention Claude follows in this repo, pinned by:

- The `auto_invoke_on` frontmatter above (frontmatter auto-discovery).
- A project memory: `memory/project_pre_commit_code_coach.md`.

The reason: a hook would force every Claude session in this repo to run the Coach, even
short throwaway commits. The convention version lets Claude skip when context makes
clear the user wants speed (e.g. "just commit this hotfix").

## Override

User can opt out per-commit with explicit phrasing:
- "ignore and commit"
- "그냥 커밋해"
- "skip review, commit"
- "commit anyway"

These bypass the engine but are still recorded in the engine log as `decision: waived`.
