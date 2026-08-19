---
date: 2026-08-20T02:52:13+09:00
agent: code-coach
type: review
mode: log-eval
trigger: "PR을 진행해죠 — 유닛테스트 및 e2e 테스트 진행, 검증, 개선사항 피드백"
target: PR #12 (feat/p0-cleanup → main)
engine: (none — no `pr-review` engine defined; see gap note)
---

# PR #12 검증 리뷰 — P0-1~3 (자가교정 피드백 턴 / voice-curator 지식화 / 미션 정리)

## 실행 요약

코드 리뷰 단독이 아니라 **빌드 → 유닛테스트(양쪽 스위트) → 베이스라인 대조 → e2e 스모크**
순으로 실제 실행 검증 후 개선 피드백을 PR 코멘트로 남겼다. Close/Merge 하지 않음.

### 실행한 검증

| 게이트 | 명령 | 결과 |
|---|---|---|
| WPF Debug 빌드 | `dotnet build Project/AgentZeroWpf -c Debug` | 오류 0 / 경고 10 (전부 NU1903, 기존) |
| ZeroCommon.Tests (head) | `dotnet test Project/ZeroCommon.Tests` | **483 pass / 0 fail / 25 skip** |
| ZeroCommon.Tests (baseline) | main worktree 에서 동일 실행 | **478 pass / 0 fail / 25 skip** |
| Focused | `--filter FullyQualifiedName~ExternalAgentLoopTests` | 21 pass / 0 fail |
| AgentTest (WPF) | `dotnet test Project/AgentTest` | **6 fail / 144 pass** (전체 실행 1회) — 이후 재실행은 결과 불안정 |
| e2e | `Docs/scripts/launch-self-smoke.ps1 -Configuration Debug` | **FAIL (exit 1)** — step 2 |
| e2e (수동 재현) | `os list-windows / get-window-info / screenshot` | 전부 ok — 제품은 정상, 스크립트가 문제 |
| e2e | `os element-tree <hwnd> --depth 2` | **HANG** — 60s 내 미종료, 출력 없음 |

정리: 실행한 GUI 인스턴스(pid 36692) graceful close, baseline worktree 제거, 작업트리 clean.

## 결과 — 발견 사항

### Must-fix
없음. 변경은 의도대로 동작하고 회귀 없음.

### Should-fix
1. **`ExternalAgentLoop.cs:362-372`** — `BuildFormatCorrectionInstruction` XML doc 은
   "no JSON envelope at all, **or an unparseable JSON object**" 두 경우를 커버한다고
   명시하지만, `JsonException` 분기(≈L139)는 `TryOfferFormatCorrection` 을 호출하지 않고
   여전히 hard-`break`. 문서와 동작 불일치 + RCA #3 실패군 중 더 흔한 쪽이 미커버.
2. **`ExternalAgentLoop.cs:167-171`** — unknown-tool 분기도 hard-`break`. 그런데 교정
   인스트럭션은 `AgentToolGrammar.KnownTools` 를 그대로 나열한다. 교정 턴 1회의
   기대값이 가장 높은 지점인데 미사용.

### Suggestion
3. 교정 턴이 `MaxIterations` 예산을 소모 (기본 12 iters / 2 corrections → 생산적 tool turn 2회 손실).
   `iter--` 또는 문서화 중 택1.
4. `TryOfferFormatCorrection` 이 `for` 본문 내부 local function 인데 호출 지점은 1곳.
   private 메서드로 올리거나 인라인하는 편이 읽기 좋다.
5. `offendingPreview` 가 `"{...}"` 안에 이스케이프 없이 보간 — 프리뷰에 `"` 가 있으면 framing 깨짐.
6. 테스트 스타일: `Assert.True(x == 1, "...")` 3곳 → `Assert.Equal(1, x)` 가 실패 시 expected/actual 제공.
   같은 파일 내 다른 테스트는 이미 `Assert.Equal` 사용 — 불일치.
7. 주입된 교정 메시지가 실제로 **User role** 로 들어가는지 검증하는 테스트 없음.

### PR 본문 정확도
8. 본문 "483 pass (baseline 473 +10)" — 실측 baseline(main) = **478**, head = **483**, delta **+5**.
   같은 본문의 "회귀 테스트 5건 추가" 와는 +5 가 일치. baseline/delta 숫자만 오기.
9. Verification 섹션에 `AgentTest`(WPF 스위트) 언급 없음 — 실제로 6 red 상태.

### 범위 밖 (별도 이슈 권장)
10. **`Docs/scripts/launch-self-smoke.ps1`** — `Run-OsCli` 가 `& $exe @argList` 사용.
    AgentZeroLite.exe 는 WinExe 라 detach → stdout 미포획, `$LASTEXITCODE` 비어 있음.
    결과적으로 M0014 e2e probe 가 **항상 step 2 에서 실패**. `Start-Process -NoNewWindow -Wait
    -RedirectStandardOutput` 로 같은 verb 호출 시 `ok:true` 정상 반환 확인.
    → `os-cli-e2e-smoke` 엔진의 자체 게이트가 비작동 상태.
11. **`os element-tree` 행(hang)** — depth 2/3 모두 60s 내 미종료, 출력 없음.
    엔진 rubric 은 element-tree 를 warn-only 로 두지만 *hang* 은 fail 보다 나쁨 (CI 무한 블록).
12. **AgentTest 신뢰도** — 4회 실행 총 테스트 수 151 / 144 / 145 / 143, 소요 4m41s / 739ms / 59ms
    로 편차. 전부 "통과!" 보고. 전체 실행 1회에서만 6 fail (`WhisperModelLoadException`).
    whisper 의존 테스트가 무조건 `[Fact]` — 모델 파일 유무로 conditional skip 필요.
13. **NU1903** — `System.Security.Cryptography.Xml` 10.0.7 high-severity 권고 5건 x2 프로젝트. 기존.

## 평가 (code-coach 3축)

| 축 | 판정 | 근거 |
|---|---|---|
| 코드 안전성 | **B+** | 예산 캡 per-instance 로 leak 없음, 기존 envelope-repair 계약 보존. 다만 교정 경로가 3개 실패군 중 1개만 커버 |
| 아키텍처 정합성 | **A-** | `AgentLoopOptions` 확장이 ZeroCommon 안에서 완결, WPF 무의존 유지. 문서/동작 불일치 1건 |
| 테스트 가능성 | **A-** | 5건 회귀 테스트가 happy/cap/per-instance 3축 커버. role 검증 및 JsonException 경로 테스트 부재 |

**종합: Approve-with-comments.** Merge 차단 사유 없음. Should-fix 2건은 후속 커밋 권장.

## 다음 단계 제안

- Should-fix #1/#2 를 이 브랜치에 후속 커밋으로 반영 후 머지
- PR 본문 baseline 숫자 정정 (473 → 478, +10 → +5)
- #10~#13 을 별도 이슈로 분리 (특히 #10/#11 은 e2e 게이트 자체가 죽어 있는 상태)

## 하네스 갭 (조련사 인계)

이 정원에는 **`pr-review` 엔진이 없다.** `pre-commit-review` 는 staged diff 전용이고
PR 단위(브랜치 대조 + 양쪽 테스트 스위트 + e2e + PR 코멘트 발행) 워크플로우가 미정의라
이번 수행은 code-coach / test-runner / security-guard / build-doctor 를 수동 조합했다.
→ `harness/engine/pr-review.md` 신설 제안 (Mode B).
