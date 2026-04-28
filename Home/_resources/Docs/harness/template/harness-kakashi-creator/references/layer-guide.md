# Harness 3-Layer Architecture Guide

## Overview

하네스는 3개의 주 레이어와 2개의 지원 디렉토리로 구성된다.
모든 파일은 Markdown(.md) 형식이며, 프로젝트 루트 기준 `harness/` 하위에 위치한다.

---

## Layer 1: Knowledge (`harness/knowledge/`)

도메인 지식 문서를 저장한다.

- 프로젝트 특화 지식
- 외부에서 학습한 참조 자료
- 복사된 스킬의 참조 문서 (카카시 복사 기능)
- 네이밍: `{domain}-{topic}.md`

### 작성 원칙
- 하나의 파일은 하나의 주제에 집중
- 다른 knowledge 문서나 외부 소스를 참조할 때는 상대 경로 링크 사용
- 기존 프로젝트 문서와 중복하지 않고 참조만

---

## Layer 2: Agents (`harness/agents/`)

에이전트 역할 정의서를 저장한다.

각 에이전트는 독립된 `.md` 파일이며, 다음 구조를 따른다:

```markdown
---
name: {agent-name}
triggers:
  - "트리거 문구 1"
  - "트리거 문구 2"
description: 역할 한 줄 설명
---

# {Agent Name}

## 역할
{이 에이전트가 하는 일}

## 실행 절차
{단계별 수행 방법}

## 평가 기준
{이 에이전트의 평가축}
```

### 초기 에이전트
- `tamer.md` — 조련사: 하네스 자체를 설명/개선/평가하는 메타 에이전트

### 에이전트 추가 규칙
- Mode B(Suggestion Tip)에서 사용자 승인 후 추가
- 기존 에이전트와 트리거가 겹치지 않도록 주의
- 하나의 에이전트는 하나의 명확한 역할에 집중

---

## Layer 3: Engine (`harness/engine/`)

워크플로우 오케스트레이션 정의를 저장한다.

여러 에이전트를 조합한 실행 시퀀스를 정의한다:

```markdown
---
name: {workflow-name}
agents: [agent1, agent2]
description: 워크플로우 한 줄 설명
---

# {Workflow Name}

## Steps
1. {Step 설명} → agent: {agent-name}
2. {Step 설명} → agent: {agent-name}

## Input
{입력 조건}

## Output
{산출물}
```

### 워크플로우 추가 규칙
- 2개 이상의 에이전트가 관여하는 작업만 워크플로우로 정의
- 단일 에이전트 작업은 에이전트 정의 내에서 처리

---

## Support: docs/ (`harness/docs/`)

하네스 자체 문서를 저장한다.

- `README.md` — 하네스 개요, 이 프로젝트에서의 용도
- `vX.Y.Z.md` — 버전별 변경 히스토리

---

## Support: logs/ (`harness/logs/`)

활동 로그를 저장한다.

- 경로 패턴: `logs/{part}/{yyyy-MM-dd-HH-mm-title}.md`
- `{part}`: 에이전트명 또는 워크플로우명
- 자동 생성, 수동 삭제
- 로그는 Mode A(Log & Eval) 및 수행부 동작 시 자동 기록

---

## 디렉토리 네이밍 규칙

- 모든 파일은 kebab-case
- 로그 타임스탬프: `yyyy-MM-dd-HH-mm` 형식
- 한글 파일명은 사용하지 않음 (에이전트 역할명이 한글이더라도 파일명은 영문)
