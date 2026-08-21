# Long-lived & experimental branches — do NOT auto-flag as "stale"

> `pr-review` / `tamer`가 브랜치 지형을 평가할 때 **먼저 이 표를 확인**한다.
> 여기 등재된 브랜치는 "main 대비 N커밋 뒤처짐"만으로 rebase/폐기 권고를 내면 안 된다.
> 의도된 상태이므로, 리뷰 리포트에서는 `intentional`로 분류하고 조치 권고를 생략한다.

| 브랜치 | 분류 | 병합 정책 | 근거 |
|--------|------|-----------|------|
| `feat/avalonia-crossplatform` | **experimental — 장기 모험** | **master 합류 금지 (당분간)** | WPF 종속 Windows 앱을 크로스플랫폼(Avalonia)으로 전환 시도하는 큰 실험. 현재 **상당수 호환 불가**. main 대비 크게 뒤처지는 것은 정상 — 실험이 성숙해 실현성이 판명될 때까지 격리 유지. drift 자체는 문제가 아님. |

## 정리 후보 (별건)

| 브랜치 | 상태 | 권고 |
|--------|------|------|
| `fix/voice-note-loopback-crash` | main 대비 고유 커밋 0 (이미 반영됨으로 보임) | 로컬 브랜치 삭제 검토 — 단, operator 확인 후 |

## 갱신 규칙

- 새 실험/장기 브랜치가 생기면 여기 등재한다.
- 실험이 종료(main 합류 또는 폐기)되면 해당 행을 제거한다.
- **자동 정리 금지** — 브랜치 삭제/rebase는 항상 operator 승인 후에만.

_등재: 2026-08-21 (PR #12 리뷰 세션, operator 구두 정보 반영)_
