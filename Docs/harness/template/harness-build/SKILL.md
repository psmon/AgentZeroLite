---
name: harness-build
description: |
  카카시하네스 마더 생성기 — 배포판(harness-kakashi-creator)의 엔진, 템플릿, 참조 문서를 개발하고 개선하는 도구.
  이 스킬은 이 저장소(harness-kakashi) 전용이며, 배포판에 포함되지 않는다.
  반드시 /harness-build 명령으로만 활성화할 것.
  다음 상황에서 사용:
  - "배포판 SKILL.md 수정해", "마더 하네스 개선해", "엔진 템플릿 수정해"
  - "references 업데이트해", "평가 체계 수정해", "레이어 가이드 고쳐"
  - "init 템플릿 수정해", "tamer 템플릿 개선해"
  - "배포판 테스트해", "하네스 빌드 상태 확인"
  - "버전 올려", "배포 준비해"
argument-hint: "[명령] [요구사항]"
---

# Harness Build — 카카시하네스 마더 생성기

이 저장소(harness-kakashi)의 배포판을 개발하는 전용 스킬.

> **자동 트리거 금지** — `/harness-build` 명령으로만 활성화한다.
> **배포판에 포함되지 않는다** — 이 스킬은 `.claude/skills/`에만 존재하며 `plugins/`에 들어가지 않는다.

---

## 마더 vs 배포판 — 핵심 구분

```
이 저장소 (harness-kakashi)
│
├── /harness-build (이 스킬)          ← 마더 생성기. 개발자 전용.
│   작업 대상: plugins/ 하위 배포판 소스
│   로그 위치: harness/logs/harness-build/  ← 개발 로그 (git에 남음)
│
├── /harness-kakashi-creator          ← 배포판 스킬 (로컬 테스트 복사본)
│   .claude/skills/harness-kakashi-creator/  ← plugins/에서 복사된 것
│   이 저장소에서 실행하면 테스트 목적
│   테스트 로그: harness/logs/ 에 쌓임 ← git에 남지만 배포판(plugins/)과 무관
│
└── plugins/harness-kakashi/          ← 배포판 소스 (source of truth)
    skills/harness-kakashi-creator/
    ├── SKILL.md                      ← 배포되는 스킬 본체
    ├── references/                   ← 배포되는 참조 문서
    └── templates/harness/            ← 배포되는 init 템플릿
        └── agents/tamer.md           ← 기본 프레임: tamer만 포함
```

### 절대 규칙

1. **배포판 templates/에는 tamer만** — 다른 에이전트는 사용자가 직접 추가하는 것.
   init 템플릿은 빈 캔버스 + 조련사(tamer) 하나만 제공한다.
2. **harness/logs/는 배포판과 무관** — `plugins/` 밖에 있으므로 배포에 영향 없음.
   개발/테스트 로그는 이 저장소의 git 이력으로 자연스럽게 남긴다.
3. **plugins/ 가 source of truth** — SKILL.md, references/, templates/ 수정은
   반드시 `plugins/harness-kakashi/skills/harness-kakashi-creator/` 에서 먼저 한다.
4. **수정 후 .claude/skills/에 동기화** — plugins/ 수정 후 `.claude/skills/harness-kakashi-creator/`에
   복사하여 로컬 테스트가 가능하게 한다.

---

## 작업 모드

### Mode 1: 엔진 개선 (배포판 SKILL.md 수정)

배포판의 핵심 로직(모드 판별, 실행 절차, 로그 체계 등)을 수정한다.

**작업 경로**: `plugins/harness-kakashi/skills/harness-kakashi-creator/SKILL.md`

**절차**:
1. 현재 SKILL.md를 읽고 변경 사항 파악
2. 수정 적용
3. `.claude/skills/harness-kakashi-creator/SKILL.md`에 동기화 (cp)
4. 로그 기록: `harness/logs/harness-build/{yyyy-MM-dd-HH-mm-title}.md`

### Mode 2: 참조 문서 수정 (references/)

평가 체계, 레이어 가이드, 조련사 정의 등 참조 문서를 수정한다.

**작업 경로**: `plugins/harness-kakashi/skills/harness-kakashi-creator/references/`

**대상 파일**:
- `layer-guide.md` — 3-Layer 아키텍처 가이드
- `evaluation.md` — 3축 평가 체계
- `tamer-agent.md` — 조련사 에이전트 상세 정의

**절차**:
1. 해당 참조 문서 읽기
2. 수정 적용
3. `.claude/skills/harness-kakashi-creator/references/`에 동기화
4. 로그 기록

### Mode 3: 템플릿 수정 (templates/)

init 시 복사되는 초기 구조를 수정한다.

**작업 경로**: `plugins/harness-kakashi/skills/harness-kakashi-creator/templates/harness/`

**주의**: templates/harness/agents/ 에는 **tamer.md만** 존재해야 한다.
다른 에이전트를 테스트용으로 추가하더라도 배포 전 반드시 제거할 것.

**절차**:
1. 템플릿 파일 읽기
2. 수정 적용
3. `.claude/skills/harness-kakashi-creator/templates/`에 동기화
4. 로그 기록

### Mode 4: 배포판 테스트

harness-kakashi-creator를 이 저장소에서 실행하여 동작을 검증한다.

**절차**:
1. plugins/ → .claude/skills/ 동기화 확인
2. `/harness-kakashi-creator init` 또는 다른 명령 실행
3. 결과 확인 (harness/logs/에 테스트 로그가 쌓임)
4. 테스트 결과를 `harness/logs/harness-build/`에 별도 기록
5. 필요 시 skill-test/result/에 테스트 결과 보관

### Mode 5: 버전 관리 & 배포 준비

**절차**:
1. 변경 사항 요약 확인
2. 버전 넘버 결정 (Patch/Minor/Major)
3. 다음 파일들의 버전 동기화:
   - `.claude-plugin/marketplace.json` → `metadata.version` + `plugins[0].version`
   - `plugins/harness-kakashi/.claude-plugin/plugin.json` → `version`
   - `harness/harness.config.json` → `version`
4. `harness/docs/vX.Y.Z.md` 변경 히스토리 작성
5. plugins/ → .claude/skills/ 최종 동기화
6. 배포판 templates/ 점검: tamer.md만 포함되어 있는지 확인
7. 로그 기록

### Mode 6: 빌드 상태 확인

현재 배포판의 상태를 종합 보고한다.

**절차**:
1. `plugins/harness-kakashi/` 디렉토리 스캔
2. 버전 동기화 상태 확인 (marketplace.json ↔ plugin.json ↔ harness.config.json)
3. templates/ 내용 점검 (tamer만 있는지)
4. `.claude/skills/`와 `plugins/` 동기화 상태 비교
5. 최근 harness-build 로그 요약
6. 보고

---

## 동기화 명령

plugins/ 수정 후 .claude/skills/에 반영할 때:

```bash
rm -rf .claude/skills/harness-kakashi-creator
cp -r plugins/harness-kakashi/skills/harness-kakashi-creator .claude/skills/
```

---

## 로그 체계

마더 생성기의 로그는 배포판 로그와 분리한다.

**경로**: `harness/logs/harness-build/{yyyy-MM-dd-HH-mm-title}.md`

**형식**:
```markdown
---
date: {ISO 8601}
agent: harness-build
type: {engine-update | reference-update | template-update | test | release-prep}
mode: {mode 1~6}
---

# {활동 제목}

## 변경 대상
{수정한 파일 목록}

## 변경 내용
{구체적 변경 사항}

## 테스트 결과
{있는 경우}

## 비고
{추가 사항}
```

---

## 주의사항

- 이 스킬은 `.claude/skills/harness-build/`에만 존재한다. `plugins/`에 절대 넣지 않는다.
- **동명이술 주의**: `plugins/harness-kakashi/skills/harness-build/`는 별개 스킬이다 — 사용자용 "하네스 빌더"(자기 하네스의 에이전트/지식/엔진 설계 도구). 이름만 같을 뿐 본 마더 스킬과 무관하며, 배포판의 정식 3종 컴포넌트 중 하나(빌드/카카시/차크라)다. 그쪽은 삭제하지 말 것.
- harness-kakashi-creator를 테스트 실행하면서 생기는 `harness/logs/tamer/` 등의 로그는 개발 이력이다. `plugins/` 밖이므로 배포판과 무관하며, git에 자연스럽게 남긴다.
- 배포판의 init 템플릿은 최소한의 기본 프레임만 제공한다: tamer 에이전트 하나 + 빈 3-Layer 구조.
