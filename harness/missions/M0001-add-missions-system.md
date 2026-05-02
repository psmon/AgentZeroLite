---
id: M0001
title: 미션(프롬프트요청) 관리 요청부 신설
operator: psmon
language: ko
status: done
priority: high
created: 2026-05-02
type: meta — bootstraps the missions subsystem itself
---

# 요청 (원문)

이 하네스에 미션(프롬프트요청)을 관리하는 요청부를 추가하려고 한다.
파일 형식은 `M0001-요청내용.md`와 같은 미션 넘버링 포맷.

경로: `harness/missions/*.md`

즉 "M0001 수행해줘" 라고 하면 해당 파일을 읽고 적절한 전문가를 소환해 수행해야 한다.
다양한 미션 (코드개선, 문서화, 역할추가/하네스업데이트 등) 이 수행될 수 있고,
미션 내용을 파악해 이 하네스가 가진 전문가를 소환하면 된다.

## 기록 분리 원칙

- **요청 기록**: 미션 파일 자체가 그대로 요청서 (별도 사본 만들지 않음)
- **수행 기록**: 별도 기록 담당이 분리되어 작동 — `harness/logs/미션기록/M{NNNN}-수행결과.md`

## 언어 정책

요청서 언어와 수행 결과 언어를 일치시킨다.
- 한국어 요청 → 한국어 결과
- 영문 요청 → 영문 결과

## 수행 순서

1. **이 요청 (M0001) 자체를 먼저 수행** — missions/ 디렉터리, tamer 디스패치 트리거, 결과 로그 등 인프라 스캐폴드.
2. 인프라 완성 후 하네스 + 관련 스킬 업데이트.
3. 다음부터는 "M0002 수행해" 처럼 짧게 호출 예정.

## Acceptance

- [ ] `harness/missions/M0001-add-missions-system.md` (이 파일) 등록
- [ ] `harness/missions/README.md` (네이밍/사용법)
- [ ] `harness/knowledge/missions-protocol.md` 작성
- [ ] `harness/agents/tamer.md` — mission dispatch 트리거 + 절차 추가
- [ ] `harness/logs/미션기록/M0001-수행결과.md` (한국어)
- [ ] `harness/harness.config.json` v1.2.0 → v1.3.0 + missions 디렉터리 명시
- [ ] `harness/docs/v1.3.0.md` 작성
- [ ] `MEMORY.md` 인덱스 갱신 (필요 시 신규 메모리)

## 향후 호출 예시

```
사용자: "M0042 수행해"
tamer:  → harness/missions/M0042-*.md 읽음
        → 미션 타입 판별 (코드개선 / 문서화 / 역할추가 …)
        → 전문가 소환 (code-coach / build-doctor / tamer 자기 자신 …)
        → 수행 후 harness/logs/미션기록/M0042-수행결과.md 기록 (요청 언어로)
```
