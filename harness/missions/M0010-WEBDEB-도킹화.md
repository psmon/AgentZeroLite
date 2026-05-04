---
id: M0010
title: WebDev 플러그인 새창 도킹화 (detach / dock / pin / 투명 / 타이틀바 OFF)
operator: psmon
language: ko
status: done
priority: medium
created: 2026-05-04
related: [M0006]
---

# 요청 (Brief)

AgentZero Lite에 탑재할는 WebDEV의 플러그인 작동 새창모드로 실행을 만들고 싶음

이유 : 멀티CLI제어가 AgentZero의 주기능이고 WebDev AI툴은 보조기능이여야함
그래서 분리되어 AgentZero의 메인창에서 발생하는 JOB을 서포터 하는 컨셉으로 변경


주요기능
- 설치된 플러그인을 WebDev 창내에서 지금같이 표현도하지만, 새창모드로 전환해 분리도가능
- 분리된 창은 다시 돌아가기기능으로 도킹가능
- 분리된 새창모드는 핀모드(다른창보다 최상위), 투명기능, 타이틀바 OFF설정등이 가능
- 각설정은 다시 할수 있게 설정가능해야함 (ex>해당창 우클릭시 설정변경)

펜슬디자인으로부터 초기 디자인컨셉을 먼저 컨펌받고 구현으로 진행할것

## Acceptance
- [ ] WebDev 페이지(또는 분리 창) 우상단에 "Detach to floating window" 액션이 노출된다
- [ ] Detach 시 현재 선택된 sample/plugin WebView2 가 새 Window 로 reparent 되며 재로딩이 발생하지 않는다 (cache 유지)
- [ ] Detach 후 메인 WebDev 페이지에는 "도킹 복귀" 안내가 표시되고, 한 번에 한 floating window 만 띄울 수 있다 (또는 sample 별 1개)
- [ ] Floating window 우클릭 컨텍스트 메뉴: Pin (Always-on-top) / Transparency (불투명도 0.5–1.0) / Title bar 토글 / Dock back / Reload / DevTools
- [ ] 각 설정 + **창의 위치/크기**는 sample 별로 영구 저장되며, 다음 Detach 시 마지막 상태로 복원된다 (현재 작업영역으로 clamp)
- [ ] Title bar OFF 상태에서 창 이동은 **Alt + 마우스 좌클릭 드래그** 로 가능 (일반 좌클릭은 WebView2 로 전달); 우클릭 메뉴에 안내 노출
- [ ] Detach 는 **활성 sample 1개만** 분리 — 같은 sample 재-Detach 는 기존 floating window focus, "전체 detach" 같은 일괄 동작은 없음 (여러 sample 띄우려면 개별 Detach)
- [ ] Dock back 시 WebView2 가 메인 페이지로 reparent 되고 floating window 는 닫힌다 (재로딩 없음)
- [ ] 펜슬(.pen) 디자인 검토 → 사용자 승인 → 코드 구현 순서를 지킴

## Notes
- M0006 의 ViewSlot cache (`_viewsBySampleId`) 를 깨뜨리면 안 된다 — Detach/Dock 은 WebView2 인스턴스를 destroy 하지 않고 reparent 만 한다.
- 펜슬 파일은 `Docs/design/M0010-{english-kebab-slug}.pen` 규칙을 따른다.
- 투명 / titlebar-less window 는 WPF `WindowStyle="None"` + `AllowsTransparency="True"` + `Background="Transparent"` 조합 필요 — WebView2 자체 background 와 충돌 가능하므로 검증 필요.
- Pin (Always-on-top) 은 `Window.Topmost`, transparency 는 `Window.Opacity` 로 구현.
- 우클릭 컨텍스트 메뉴는 floating window chrome 자체에 부착 (titlebar OFF 상태에서도 진입 가능해야 함).
