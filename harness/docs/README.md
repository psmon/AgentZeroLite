# Harness — 정원

> "정원지기"라고 부르면 된다. 그것이 전부다.

이 프로젝트의 하네스(정원) 프레임워크.

## 정원의 구조

```
harness/
├── harness.config.json   # 정원의 이름표
├── knowledge/            # Layer 1: 햇빛 — 도메인 지식
├── agents/               # Layer 2: 영양분 — 전문가 에이전트
│   └── tamer.md          # 정원지기 (기본 내장)
├── engine/               # Layer 3: 물길 — 워크플로우
├── docs/                 # 정원 일지
└── logs/                 # 활동 기록
```

## 호출 방법

`/harness-creator` — 정원지기를 소환한다.

## 사용법

```
꽃을 심다 (개선부):
  /harness-creator 하네스를 설명해     ← 정원 상태 보고
  /harness-creator 하네스를 개선해     ← 평가 후 개선안
  /harness-creator 새 에이전트 추가해  ← 새 꽃 심기
  /harness-creator 스킬 복사해         ← 접목(接木)

꽃을 피우다 (수행부):
  /harness-creator 전체 점검해         ← 전체 리뷰
  /harness-creator 변경 점검해         ← 변경분만 리뷰
```
