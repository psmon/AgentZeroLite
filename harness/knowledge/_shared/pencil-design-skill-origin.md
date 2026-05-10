# pencil-design skill — provenance & local conventions

> Owner: **`_shared`** (no agent owns it; tooling-level skill).
> Imported via Mode C (Kakashi Copy) on 2026-05-10 from a sibling project's
> harness, brought in as a prerequisite for mission **M0016** (3D actor-world
> harness-view category).

## Provenance

| Field | Value |
|---|---|
| Origin repo | `D:/pencil-creator/` (sibling project, not vendored) |
| Origin path | `D:/pencil-creator/.claude/skills/pencil-design/` |
| Origin Pages | https://psmon.github.io/pencil-creator/ |
| Local path | `.claude/skills/pencil-design/` |
| Files copied | `SKILL.md` (829 lines) + `scripts/image-gen.py` (95) + `scripts/providers/{__init__,gemini_provider,comfyui_provider}.py` (267 total) |
| Files NOT copied | `scripts/providers/__pycache__/` (compiled cache, no value); any local `.secret/` (does not exist in source — keys load from REPO_ROOT at runtime) |
| Copy date | 2026-05-10 |
| Trigger | M0016 mission body — "D:\\pencil-creator\\.claude\\skills\\pencil-design 스킬을 참고해 여기로 가지고올것" |

## What this skill does

`pencil-design` produces **`.pen` design files** (Pencil MCP) and **AI-generated
images** (Gemini cloud + ComfyUI local). It's the texture-generation half of
M0016 — actor sphere textures, galaxy backgrounds, cyberpunk tooltip art.

Capabilities (from origin SKILL.md):

- Architecture diagrams / flowcharts / ERDs / state diagrams as `.pen`
- WPF/XAML animation control research → Pencil visualization
- Image generation via two providers (Gemini cloud, ComfyUI local)
- Image editing (Gemini)
- Pencil card illustrations / backgrounds / thumbnails

## Security — key management

Both the origin and this local copy follow the **same secret-file pattern**:

- API keys live in `<REPO_ROOT>/.secret/gemini.json`, format
  `{ "api_key": "...", "image_model": "..." }`. Override path with env var
  `GEMINI_SECRET_PATH`.
- ComfyUI provider needs no key (local server).
- **Never hardcode** keys in skill files. Already audited — origin and copy are
  clean (verified by grep on `api[_-]?key|GEMINI_API|GOOGLE_API` patterns).
- Project `.gitignore` already excludes `/.secret/` (line 1) — keys placed
  here will never enter git history.

This matches M0016's brief: "psmon-doc-writer는 문서생성을 위한 스킬로 커밋방지및
생성에 필요한 키관리 방식도 동일채택 커밋방지" — same posture.

## Local conventions vs. origin defaults

The origin SKILL.md was authored against `D:/pencil-creator/`'s layout, which
is **not** AgentZero Lite's. When invoking this skill in our project, override:

| Setting | Origin default | AgentZero Lite override |
|---|---|---|
| `.pen` save root | `D:\MYNOTE\design\` | `Docs/design/` (in-repo, indexer pairs by `M{NNNN}` prefix per [data-contracts.md Rule 5](.claude/skills/harness-view-build/references/data-contracts.md)) |
| Image output root | `tmp/images/` (origin REPO_ROOT relative) | `tmp/images/` (still REPO_ROOT relative — fine; gitignored) |
| `IMAGE_GEN_ROOT` env | unset → REPO_ROOT | unset → AgentZero Lite REPO_ROOT (fine) |

**The SKILL.md was copied verbatim** so future re-syncs from the sibling repo
are 1:1 — divergence is captured here, not in the file body. This avoids a
"fork" situation where the two copies drift.

## Anti-pattern: do NOT

- Add the skill body to `harness/` (knowledge/agents/engine). Per M0016
  constraint "(3d생성은 이 프로젝트무관으로 하네스에는 영향주지말것)" — image
  generation is a viewer-side authoring tool, not part of the harness QA
  surface.
- Include `__pycache__` in commits. Already excluded by sane Python
  defaults but worth restating since the origin repo also doesn't track it.
- Vendor the sibling repo's `.secret/`. Each project carries its own.

## Cross-references

- M0016 mission file: `harness/missions/M0016-하네스뷰-액터모델3d.md`
- M0016 completion log: `harness/logs/mission-records/M0016-수행결과.md`
- Kakashi-copy log: `harness/logs/kakashi-copy/2026-05-10-10-31-pencil-design-from-pencil-creator.md`
- Sibling project XAML reference (per M0016): `D:/pencil-creator/design/xaml`
  (out-of-repo; consult only via `Read` from absolute path, do not vendor)
