---
id: M0006
title: WebDev를 메인 윈도우 공식 메뉴로 승격 (시원한 풀 레이아웃)
operator: psmon
language: ko
status: done
priority: medium
created: 2026-05-03
---

# 요청 (Brief)

스킬이용 : harness-kakashi-creator

setting의 webdev를 메인윈도우 공식 메뉴로 이동하려고합 , webdev는 agent bo 다음에 메뉴아이콘으로 배치 ..
우측패널을 독립적으로 전체사용 webdev의 샘플을 두번째 메뉴뎁스 패널에서 선택할수 있으며
컨텐츠 패널은 해당 메뉴에 반응하여 표시가됨.. 셋팅내 표현된 레이아웃을 이해하고 메인윈도우의 공식 기능으로 레이아웃을 편리하게
이용할수 있게 확장 ( setting내에서 기능 작동은 너무 좁음 시원하게
확장필요)  펜슬로 디자인 작업을 먼저 검토후 승인후 진행

## Acceptance
- [ ] MainWindow 좌측 메뉴 아이콘 영역에서 AgentBot 다음 자리에 WebDev 아이콘 노출
- [ ] WebDev 메뉴 클릭 시 2뎁스 샘플 리스트 패널이 표시됨
- [ ] 2뎁스 샘플 항목 선택 시 우측 컨텐츠(WebView2) 영역이 해당 샘플로 전환됨
- [ ] 우측 컨텐츠 영역은 메인 윈도우 폭을 시원하게 활용 (Settings 시절보다 명확히 넓음)
- [ ] Settings 화면에서는 webdev 패널이 제거되거나 메인 메뉴로 안내됨 (중복 노출 X)
- [ ] 펜슬(.pen) 디자인 검토 → 사용자 승인 → 코드 구현 순서를 지킴

## Notes
- 펜슬 파일은 Docs/design/harness-viewdesign.pen 등 기존 디자인 자산과 동일한 위치 정책을 따른다 (`.pen` 은 pencil MCP로만 읽고 쓴다).
- 좌측 메뉴는 Settings의 webdev 컨텐츠 호스팅 흐름을 그대로 재사용 — WebView2 인스턴스 수명/탭 전환 회귀를 만들지 않는다 (87cbe34, cc7b534 참고).
