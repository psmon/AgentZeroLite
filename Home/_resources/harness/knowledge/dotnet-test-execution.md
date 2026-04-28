# dotnet test 실행 규칙 — 정전(canon)

> 본 문서는 하네스 내 모든 에이전트/엔진이 `dotnet test`를 호출할 때 따라야 하는
> 운영 규칙이다. 이슈 사례에서 도출되어 추가됐다.

## 사례: 2026-04-28 testhost 12 GB 누적 사건

Phase 2(Voice 서브시스템) 빌드 검증 중, 같은 `ZeroCommon.Tests.csproj` 대상으로
`dotnet test`를 백그라운드 셸로 **3번 연속** 실행했다. 첫 실행 출력이 0바이트로
보여 두 번째와 세 번째를 추가로 띄운 게 원인.

결과:
- testhost.exe 3개가 각 5.3 GB / 3.4 GB / 3.4 GB → 합산 **12 GB RAM**
- CPU 100% 점유 (xUnit 자체가 클래스 병렬 실행이라 testhost 1개도 이미 멀티스레드)
- 같은 `bin/` 디렉토리에서 파일 락 경쟁 → 가장 늦은 런이 21분 소요
- 사용자가 시스템 부하로 알아차리고 중단 요청 → 수동 `taskkill`로 회수

> 모든 런 결과는 통과(72 / 73 / 84)였다. 즉, **"실패해서가 아니라 실수로 동시 실행해서"**
> 발생한 사고다. 단발 실행이었다면 RAM 4 GB / 5분 내 종료됐다.

## 핵심 규칙

### R1. 병렬 호출 금지

같은 테스트 프로젝트 대상으로 `dotnet test`를 **여러 백그라운드 셸로 동시에**
띄우지 말 것. 한 번에 한 호출만, 그것도 포어그라운드(`run_in_background: false`)로.

xUnit 자체가 클래스 병렬화를 수행하므로 동시 호출로 "더 빨리" 얻을 게 없다 —
오히려 testhost 누적 + 파일 락 경쟁으로 느려진다.

### R2. 출력 0바이트 ≠ 실패

`dotnet test`를 백그라운드로 띄웠는데 출력 파일이 즉시 0바이트로 보일 수 있다.
이 상태에서 두 번째 호출을 띄우는 건 R1 위반의 원인이다. 다음을 먼저 확인:

1. `tasklist | grep testhost` — testhost가 살아있다면 아직 진행 중이다.
2. 출력 파일의 mtime 또는 사이즈 변화 — 파일에 쓰기가 진행 중인지 확인.
3. 로그 파일 자체 (예: `*.trx`) — 별도 logger를 지정했다면 거기에 쓰여지고 있을 수 있다.

진행 중이면 **기다린다.** 진행 중이 아니면(testhost 없음 + 출력 비어있음) crash다 —
재실행 전에 마지막 cmd가 어떻게 끝났는지 진단부터.

### R3. testhost 잔존 수동 정리 금지가 아니라 권장

CTRL+C나 timeout으로 dotnet test를 중단하면 testhost가 orphan으로 남을 수
있다. 잔존 testhost는 다음 빌드/테스트와 파일 락 경쟁을 일으킨다. 정리 절차:

```bash
tasklist | grep -iE "testhost|vstest"
taskkill //F //PID <pid>           # mingw 환경
# 또는
taskkill /F /PID <pid>             # cmd / powershell
```

여러 PID 동시 kill: `taskkill //F //PID 1234 //PID 5678`.

### R4. 좁혀서 돌리기

실패 원인 추적용 반복 실행이라면 풀 스위트 대신 필터로 좁힌다:

```bash
dotnet test Project/ZeroCommon.Tests/ZeroCommon.Tests.csproj \
  --filter "FullyQualifiedName~ApprovalParserTests"
```

진단 사이클당 RAM/시간이 1/10 이하로 줄어든다.

### R5. WPF-deps 테스트는 desktop 세션에서만

`Project/AgentTest/AgentTest.csproj`는 ConPTY/WPF에 의존한다 — desktop 세션
없이 실행하면 무한 대기 또는 native crash를 낸다. CI 또는 헤드리스 환경에서
일괄 호출 금지. 호출 전에 desktop 세션 가용 여부를 명시적으로 확인할 것.

### R6. 보고 직전 정리 확인

테스트 단계가 끝나고 결과를 사용자에게 보고하기 직전, **반드시**
`tasklist | grep testhost`로 잔존 testhost가 없는지 확인한다. 있다면
보고에 "정리 완료" 또는 "정리 필요"를 명시한다. 사용자가 다음 작업을
시작했을 때 unexpected RAM 점유를 만나지 않게 하기 위함.

## 어겼을 때의 결과 (블래스트 반경)

- **즉시**: 사용자 머신 RAM 수 GB 단위로 즉시 점유 — 사용자가 동시에 돌리는
  IDE / 빌드 / 다른 작업이 멈추거나 매우 느려진다.
- **수 분 내**: 파일 락 경쟁으로 진행 중인 빌드/테스트가 자기 자신과 경쟁해
  실행 시간이 5~10배 늘어난다.
- **사용자 신뢰**: "병렬로 돌렸냐"는 질문이 나오면 사고가 표면화된 것 — 사용자가
  로그를 의심하기 전에 이 문서로 회수한다.

## 호출 측 체크리스트 (에이전트가 따를 것)

`dotnet test`를 procedure에 포함한 모든 에이전트는:

- [ ] 한 번만 호출. `run_in_background: false`가 기본.
- [ ] 호출 후 출력이 비어 보여도 즉시 재호출하지 말 것 — R2를 따른다.
- [ ] 결과 보고 직전 `tasklist | grep testhost`로 잔존 정리 확인 (R6).
- [ ] 동일 세션 내 두 번째 테스트 호출이 필요하다면, 첫 호출 종료를 명시적으로
      확인한 뒤에만 진행.
- [ ] 반복 디버깅이라면 `--filter`로 범위를 좁힌다 (R4).
