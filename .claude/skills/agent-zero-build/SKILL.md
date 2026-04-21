---
name: agent-zero-build
description: |
  Run the AgentZero Lite release pipeline end-to-end — sync any pending
  git work, bump the SemVer in Project/AgentZeroWpf/version.txt, push an
  annotated tag, and hand off to the GitHub Actions release workflow so
  that a self-contained win-x64 ZIP + Inno Setup installer are published
  to the GitHub Releases page. Use this skill whenever the user asks to
  "release", "deploy", "ship", "cut a release", "bump and tag",
  "publish", or "auto-build" AgentZero Lite — or says anything like
  "에이전트빌더 배포해", "에이전트 빌더 배포", "AgentZero 배포",
  "AgentZeroLite 배포", "Lite 배포", "빌드 배포", "릴리스 찍어",
  "태그 올려서 배포" — even if they do not explicitly mention tags,
  GitHub Actions, or the installer. This is the canonical entry point
  for shipping the product; prefer it over ad-hoc git commands.
---

# AgentZero Build & Release

You are the release captain for **AgentZero Lite**. When triggered, you
drive a short, well-known pipeline that takes whatever is on the user's
working tree and turns it into a published GitHub Release.

## Pipeline at a glance

```
   pending changes?          version bump            push tag v<x.y.z>
  ┌──────────────┐       ┌──────────────────┐     ┌──────────────────┐
  │ 1. Sync main │ ───▶  │ 2. Bump version  │──▶  │ 3. Tag & push    │
  └──────────────┘       └──────────────────┘     └──────────────────┘
                                                           │
                                             ┌─────────────▼──────────────┐
                                             │ 4. GitHub Actions produces │
                                             │   · AgentZeroLite-v<..>-   │
                                             │     Setup.exe (Inno Setup) │
                                             │   · AgentZeroLite-v<..>-   │
                                             │     win-x64.zip (portable) │
                                             └────────────────────────────┘
```

The heavy lifting (publish → zip → iscc → upload release assets) is
already wired up in `.github/workflows/release.yml` — **your job is to
get a clean commit + a monotonically increasing tag on `origin/main`.**

## Invariants — things to respect, always

These are not style rules; breaking them will produce broken releases
or corrupt the version history. Explain politely and stop if you hit a
conflict.

- Work only on **`main`**. If `git branch --show-current` is anything
  else, stop and tell the user.
- The version lives in **`Project/AgentZeroWpf/version.txt`**. It is a
  single line `X.Y.Z`. The tag is always `v<that>`. No other format.
- Never push `--force`, never rewrite published history, never skip
  git hooks (no `--no-verify`, `--no-gpg-sign`).
- Never tag backwards. If the new version would be `<=` an existing
  tag, stop.
- Secrets, `.env`, credentials: never stage, never commit, never
  mention in the release notes. If you see such a file in
  `git status`, warn the user and bail.
- Tagging is effectively public. If something is unclear about scope
  (which commits to include, what bump level), **ask the user once and
  wait for an answer** before touching git.

## Step 1 — Sync pending work on main

Run `git status --short` and `git log --oneline origin/main..HEAD` to
see what's local and what's ahead of the remote. Then:

1. **Worktree clean + in sync with origin/main** → nothing to do,
   continue to Step 2.
2. **Worktree clean + local commits ahead** → `git push`.
3. **Worktree dirty** → this is the common case. Review the diff,
   group logically if needed, and write a commit message that
   describes *why*, not a file list.
   - Use the heredoc pattern so multi-line messages round-trip
     cleanly:
     ```bash
     git commit -m "$(cat <<'EOF'
     short imperative subject line

     body paragraph(s) explaining the why — not the diff.

     Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
     EOF
     )"
     ```
   - Prefer `git add <paths>` over `git add -A` unless everything
     really does belong in the same commit; adding broadly risks
     pulling in local scratch files.
   - Then `git push`.

If the push is rejected (pre-receive hook, push-to-main policy),
**stop and ask** the user how to proceed. Common recoveries:
open a PR from a throwaway branch, or (with explicit confirmation)
bypass the policy.

## Step 2 — Bump the version

Read `Project/AgentZeroWpf/version.txt` and decide how to bump. The
default is **patch**. Let the user's language override:

| Cue from the user                           | Bump level |
|---------------------------------------------|------------|
| "버그 고쳐 배포", "fix release", no hint     | patch (Z+1) |
| "새 기능", "new feature", "minor"            | minor (Y+1, Z→0) |
| "breaking", "v1", "major release"            | major (X+1, Y→0, Z→0) |

Patch 9 → carry to next minor; minor 9 → carry to next major. The
logic mirrors the `BumpVersionAfterBuild` target already in the
csproj (for Release builds done locally), but here we control it
deliberately.

Before writing, confirm the target: **"Current 0.1.3 → proposed
0.1.4. Proceed?"** Then:

```bash
# write new version (single line, trailing newline)
# replace X.Y.Z with the computed value
echo "X.Y.Z" > Project/AgentZeroWpf/version.txt

git add Project/AgentZeroWpf/version.txt
git commit -m "chore: bump version to X.Y.Z"
git push
```

If git status has other unstaged files at this point, something
unexpected happened between Step 1 and here. Do not silently sweep
them into the bump commit — stop and show the user.

## Step 3 — Create the annotated tag and push it

Always an **annotated** tag (`-a`), never lightweight. The tag
message becomes part of the release notes metadata.

```bash
git tag -a v<X.Y.Z> -m "$(cat <<'EOF'
AgentZero Lite v<X.Y.Z> — <one-line theme>

<2–6 short bullets summarising what is in this release, scraped
from the commits since the previous tag. Use git log to build it:
  git log --oneline v<prev>..HEAD>
EOF
)"

git push origin v<X.Y.Z>
```

Pushing the tag is what fires `release.yml`. You are done producing
artifacts — the rest is CI.

Tell the user exactly where to watch, using the repo URL on `origin`:

- Actions run: `https://github.com/<owner>/<repo>/actions/workflows/release.yml`
- Release page (will appear when the run finishes):
  `https://github.com/<owner>/<repo>/releases/tag/v<X.Y.Z>`

For the psmon/AgentZeroLite repo those are:
https://github.com/psmon/AgentZeroLite/actions/workflows/release.yml
https://github.com/psmon/AgentZeroLite/releases/tag/v<X.Y.Z>

## Step 4 — Monitor & recover

A healthy run is 4–7 minutes on `windows-latest`. You do **not**
need to poll the CI in a loop; closing out the chat after the push
is normal. If the user returns asking "did it ship?":

- `gh run list --workflow=release.yml --limit 3` (if `gh` is
  installed) or a quick curl to the Actions API.
- If the run failed, common causes and fixes:
  - **`iscc: command not found`** — Inno Setup 6 is usually
    preinstalled on `windows-latest` but has been missing on some
    images. Add a setup step before the installer step:
    ```yaml
    - name: Install Inno Setup
      run: choco install innosetup -y --no-progress
    ```
  - **`Resource not accessible by integration` when creating the
    release** — Settings → Actions → General → Workflow permissions
    must be "Read and write permissions".
  - **Tag already exists** — the tag was reused. Either pick a new
    version or (with the user's explicit OK) delete both local and
    remote tag and re-push.
  - **`dotnet publish` trimmed a needed DLL** — `PublishSingleFile`
    is intentionally off in the workflow (`-p:PublishSingleFile=false`);
    keep it off. If publish fails on a native like `conpty.dll`,
    the file should be a `Content` item with
    `CopyToOutputDirectory=PreserveNewest` in the csproj.

## Handling "just tag" / "only release the last push"

Sometimes the user already committed and pushed manually and just
wants the ship step. Detect this: if `git status` is clean AND
the local branch is in sync with `origin/main`, skip Step 1
entirely and go straight to Step 2 (confirm the bump level) or
Step 3 (if they also already bumped `version.txt`).

## Examples

### Example 1 — typical "ship it" after a bugfix

**User:** "에이전트빌더 배포해"

1. `git status` shows `README.md` modified.
2. Review diff → it's a typo fix. Commit as
   `docs: fix typo in install steps`. Push.
3. version.txt = `0.1.3`. Default bump → `0.1.4`. Confirm with
   user, write file, commit, push.
4. `git log --oneline v0.1.3..HEAD` → 2 commits. Build a short
   annotated tag message, create `v0.1.4`, push.
5. Report back the Actions URL and the future Releases URL.

### Example 2 — user wants a feature bump

**User:** "AgentZeroLite 0.2 찍자, 새 기능 붙였어"

Default would be patch, but the user said "새 기능" → minor.
`0.1.7` → `0.2.0`. Otherwise identical flow.

### Example 3 — nothing to commit

**User:** "태그만 올려서 배포해"

`git status` clean, local in sync. Skip Step 1. Ask: "현재
`0.1.4` 입니다. `0.1.5`(patch)로 올릴까요, `0.2.0`(minor)로
올릴까요?" Proceed from Step 2 with their answer.

### Example 4 — dirty tree with unrelated experiments

**User:** "배포해"

`git status` shows 6 modified files, 2 of them look like local
scratch (`scratch/`, `*.local.ps1`). Do **not** stage them
blindly. Summarise to the user what you see and ask which go
into the release. Only then proceed.

## Tone

Be terse and confident — the user is asking you to execute a known
procedure, not design one. One-sentence progress updates between
steps are plenty ("Committed README fix as 4abc12f. Pushing…"),
and a final compact summary at the end is ideal:

```
v0.1.4 shipped.
  · README typo fix   4abc12f
  · bump version      9de3f00
Tag pushed: v0.1.4
Watch the build:   https://github.com/psmon/AgentZeroLite/actions/workflows/release.yml
Release will land: https://github.com/psmon/AgentZeroLite/releases/tag/v0.1.4
```

That's the whole skill.
