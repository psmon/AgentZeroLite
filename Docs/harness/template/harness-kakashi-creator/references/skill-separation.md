# 스킬 분리 원칙 — 표준 vs 프로젝트 전용

새 에이전트/엔진을 추가할 때, **트리거를 어디에 등록할지** 반드시 결정한다.
하네스 파일(`harness/agents/`, `harness/engine/`, `harness/knowledge/`)은
프로젝트 고유해도 문제없다. **트리거가 살 집**만 올바르게 고르면 된다.

---

## 분류 기준

| 질문 | 예 → | 아니오 → |
|------|------|----------|
| 다른 프로젝트에서도 쓸 수 있는가? | 표준 (kakashi) | 프로젝트 전용 |
| 특정 MCP 서버(Notion 등)에 의존하는가? | 프로젝트 전용 | 표준 가능 |
| 프로젝트 고유 트리거 문구가 필요한가? | 프로젝트 전용 | 표준 가능 |
| 프로젝트의 파일/구조를 직접 참조하는가? | 프로젝트 전용 | 표준 가능 |

네 질문 중 하나라도 "프로젝트 전용"으로 떨어지면 → 프로젝트 전용 스킬로 간다.

---

## 배치 규칙

```
표준 스킬 (harness-kakashi-creator)
├── SKILL.md       ← 코어 트리거만 등록 (범용)
├── templates/     ← 초기화 템플릿 (tamer 하나만)
└── references/    ← 범용 가이드 (이 파일 포함)

프로젝트 전용 스킬 (예: my-project-vibe)
├── SKILL.md       ← 프로젝트 트리거 + 워크플로우 안내
└── (트리거가 harness/ 하위 파일을 참조)

프로젝트 하네스 (harness/)
├── agents/        ← 프로젝트 고유 에이전트 OK
├── engine/        ← 프로젝트 고유 엔진 OK
└── knowledge/     ← 프로젝트 고유 지식 OK
```

---

## 핵심 3문장

> 하네스 파일(agents/engine/knowledge)은 프로젝트 고유해도 **OK**.
> 표준 스킬 SKILL.md 트리거 테이블은 **범용만**.
> 프로젝트 전용 트리거는 **프로젝트 전용 스킬에서**.

---

## 예시: Notion 기술문서 발행 에이전트

| 항목 | 위치 | 이유 |
|------|------|------|
| 에이전트 정의 | `harness/agents/tech-doc-publisher.md` | 하네스 구조 안에서 관리 |
| 엔진 정의 | `harness/engine/notion-tech-publish.md` | 워크플로우는 하네스에 속함 |
| 트리거 | 프로젝트 전용 스킬 `SKILL.md` | Notion MCP 의존 → 표준에 넣으면 안 됨 |
| ~~트리거~~ | ~~`harness-kakashi-creator` SKILL.md~~ | ❌ 표준 스킬 오염 |

---

## 체크: 표준 스킬을 오염시키지 않았는가

Mode B(Suggestion Tip)에서 새 에이전트/엔진을 제안한 뒤, 다음을 확인한다:

1. 트리거 문구가 프로젝트 용어를 담고 있는가? (예: "Notion에 발행", "결제팀 리뷰")
2. 에이전트가 특정 외부 시스템(MCP 서버, 사내 API)을 호출하는가?
3. 트리거가 `harness/` 내부의 특정 파일 경로를 전제하는가?

**하나라도 예** → 프로젝트 전용 스킬의 SKILL.md에 등록한다.
프로젝트 전용 스킬이 없다면 먼저 `/skill-creator`로 만들거나,
`harness-kakashi-creator`의 "프로젝트별 트리거 자동 발견"
(`harness/agents/*.md`, `harness/engine/*.md` frontmatter의 `triggers:` 스캔)에
의존한다 — 이 경로는 표준 SKILL.md를 건드리지 않는다.

**모두 아니오** → 범용이므로 표준 스킬 SKILL.md에 추가할 수 있다.
단 이 경우에도 `plugins/harness-kakashi/` 에서 먼저 수정 후
`.claude/skills/`로 동기화하는 `/harness-build` 절차를 따른다.
