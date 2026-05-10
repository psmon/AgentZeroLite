---
date: 2026-05-10T10:31+09:00
agent: tamer
type: copy
mode: kakashi-copy
trigger: "M0016 미션수행 (Phase A — pencil-design skill ingestion)"
related_mission: M0016
---

# pencil-design skill copy from D:/pencil-creator → AgentZeroLite

## 실행 요약

Mode C (Kakashi Copy) — M0016 의 "사전 조사및 스킬업데이트" 단계로,
형제 프로젝트 `pencil-creator` 의 `pencil-design` 스킬을 본 프로젝트로
가져옴. 텍스처 생성용 (Gemini cloud + ComfyUI local 이미지 생성) +
.pen 디자인 산출용. M0016 에서 액터 구체 텍스처, 은하수 배경,
사이버펑크 툴팁 일러스트를 만들기 위한 prerequisite.

## 결과

5개 파일 복사 (총 1,191줄):

```
.claude/skills/pencil-design/
├── SKILL.md                              (829 줄)
└── scripts/
    ├── image-gen.py                      ( 95 줄)
    └── providers/
        ├── __init__.py                   ( 10 줄)
        ├── gemini_provider.py            ( 74 줄)
        └── comfyui_provider.py           (183 줄)
```

제외 대상:
- `scripts/providers/__pycache__/` — 컴파일 캐시
- 원본에 `.secret/` 없음 (런타임 REPO_ROOT 기준 로드)

## 보안 검증

- 원본 SKILL.md / *.py 전체에 `api[_-]?key|GEMINI_API|GOOGLE_API|password|secret`
  grep — 하드코딩된 키 없음.
- 키 로딩 경로: `REPO_ROOT/.secret/gemini.json` (env `GEMINI_SECRET_PATH` override 가능).
- 본 프로젝트 `.gitignore:1` 에 `/.secret/` 이미 등재 → 키 git 유출 불가.
- ComfyUI provider 는 로컬 서버 호출만, 키 불필요.

## 변경 파일

- `.claude/skills/pencil-design/` (신규, 5 파일)
- `harness/knowledge/_shared/pencil-design-skill-origin.md` (신규, provenance + 로컬 컨벤션)

## 평가 (Mode C rubric)

| 축 | 측정 | 결과 |
|---|---|---|
| 복사 무결성 | 5/5 파일 복사 + 라인 수 일치 | Pass |
| 보안 위생 | 키 미포함 + .gitignore 게이트 확인 | Pass |
| Provenance 등록 | `harness/knowledge/_shared/` 에 출처/규약 문서화 | Pass |
| Anti-fork 보장 | SKILL.md 본문 무수정 — divergence 는 provenance doc 에만 기록 | Pass |
| 로컬 컨벤션 명시 | `Docs/design/` (M{NNNN} 프리픽스 규칙) override 명시 | Pass |

## 다음 단계 제안 (M0016 후속 phase)

복사가 끝났다고 미션이 끝난 것은 아님. M0016 의 본 작업은 이제 시작:

- **Phase B** — psmon-doc-writer 가 Three.js / WebGL 등 프론트 3D 기술 학습
  지식을 흡수. Mode B (Suggestion Tip) 로 새 knowledge note 추가 검토.
- **Phase C** — `Home/harness-view/` 에 Actor 3D World 카테고리 신설.
  사이드바 엔트리 + 새 view 파일 + Three.js scene + 사이버펑크 툴팁
  + drag-link persistence + 갤럭시 배경.
- **Phase D** — `Docs/design/M0016-actor-3d-world.pen` 디자인 파일.
  데이터-컨트랙트 Rule 5 준수 (M{NNNN} 프리픽스).
- **Phase E** — pencil-design 스킬을 실제 호출해 액터 구체 텍스처 6장
  (역할별: tamer, security-guard, build-doctor, test-sentinel,
  test-runner, code-coach) + 갤럭시 배경 1장 생성.

각 Phase 는 별도 턴으로 진행 — operator 가 Phase A 결과 확인 후 다음 단계 트리거.
