# netclaw 분석 — AOT 가능 TUI 조사 + agent-one 참고 레퍼런스

> 스냅샷: 2026-09-22 · 대상: [netclaw-dev/netclaw](https://github.com/netclaw-dev/netclaw) (v0.27.0-beta.4)
> 목적: (1) **Native AOT를 견디는 .NET TUI가 존재하는가** 를 실측으로 판정하고,
> (2) 동일 제품군(셀프호스팅 에이전트 CLI)의 구조를 `Project/AgentOne` 설계에 참고한다.

## 참조 클론 위치 (지속 분석용)

- **`C:\code\psmon\research\netclaw`** — shallow clone (`--depth 50`). 소스 재조회 전용 참조본.
  프로젝트에 복사하지 않고 이 위치에서만 열람한다.
- 재조회 예: `cd /c/code/psmon/research/netclaw && rg <keyword> src/Netclaw.Cli`

## netclaw 한 줄 요약

Petabridge(Akka.NET 제작사)가 만든 **오픈소스 셀프호스팅 자율 운영 에이전트**.
Apache-2.0, .NET 10, Akka.NET 기반. **데몬(`netclawd`) + 얇은 CLI(`netclaw`)** 로
분리되어 로컬 소켓으로 통신한다. 승인 게이트(approval gates)와 큐레이션된 스킬 피드를
전면에 내세운 "안전 기본값" 노선.

AgentZero Lite 와 겹치는 지점: Akka 액터, 에이전트 루프, 승인 프롬프트, MCP, 채널
연동(Slack/Discord/Mattermost). 겹치지 않는 지점: 데몬/CLI 분리, 클러스터 샤딩,
Aspire, 도커 배포.

---

## 1. 핵심 발견 — Termina 는 진짜 AOT 가 된다 (실측)

netclaw 의 TUI 는 **[Termina](https://github.com/Aaronontheweb/termina) 0.16.2** 다.
Aaron Stannard(Petabridge 창업자) 작, Apache-2.0, `net10.0` 전용.
README 가 *"AOT-compatible (Native AOT publishing supported)"* 라고 주장한다 —
주장만으로는 근거가 아니므로 **직접 퍼블리시하고 실행해서 확인했다.**

### 실측 절차

스크래치 콘솔 프로젝트에 Termina 만 얹고, agent-one 과 **동일한 AOT 설정**
(`PublishAot` + `TrimMode=link` + `IlcOptimizationPreference=Size` +
`InvariantGlobalization`)으로 `dotnet publish -r win-x64`.

ViewModel(`ReactiveViewModel` + `ReactiveProperty<T>`) · Page(`ReactivePage<T>` +
`PanelNode`/`TextNode` 레이아웃) · DI/Hosting(`AddTermina` + 라우트 등록) 을 모두
사용해 프레임워크의 반사/DI 경로가 실제로 실행되게 만들었다.

### 결과

| 항목 | 결과 |
|---|---|
| AOT 퍼블리시 | **성공** |
| IL2026/IL3050 등 트리밍 경고 | **0건** |
| 바이너리 크기 (win-x64, 단독) | **5.19 MB** |
| 퍼블리시된 바이너리 **실행** | **성공** — 패널 렌더, 스크립트 입력(Down/Down/Up/Esc) 처리, 정상 종료, exit 0 |

링크만 통과한 게 아니라 **런타임까지 돌았다**는 점이 중요하다. AOT 실패는 대개
링크가 아니라 실행 시점에 터지기 때문이다.

### 부수 발견 — TUI 가 테스트 가능하다

Termina 는 `VirtualInputSource` + `AddTerminaVirtualInput(...)` 을 제공한다.
키 입력을 큐에 넣고 헤드리스로 호스트를 돌릴 수 있다 — 위 실측도 이걸로 했다.
TUI 를 단위 테스트 가능한 상태로 둘 수 있다는 뜻이며, agent-one 의
"로직은 테스트 가능하게, IO 는 얇게" 방침과 맞는다.

```csharp
var scripted = new VirtualInputSource();
builder.Services.AddTerminaVirtualInput(scripted);
scripted.EnqueueKey(ConsoleKey.DownArrow);
scripted.EnqueueString("gpt-4o-mini");
scripted.EnqueueKey(ConsoleKey.Enter);
scripted.Complete();
await host.RunAsync();
```

### 의존성 비용

Termina 가 끌고 오는 것: `Microsoft.Extensions.DependencyInjection.Abstractions`,
`Microsoft.Extensions.Hosting.Abstractions`, `R3`(리액티브). 실사용 시
`Microsoft.Extensions.Hosting` 도 필요하다.

agent-one 현재 AOT 바이너리는 5.77 MB, Termina 단독 프로브는 5.19 MB.

**채택 후 실측 (2026-09-22, win-x64)** — 추정 ~7 MB 였으나 실제는 **8.28 MB**
(+2.51 MB). 기동 시간은 `--version`/`tools list`/`config show` 모두 **81~91 ms**
로 변화 없고(이 경로는 Hosting 을 건드리지 않는다), TUI 경로는 호스트 부팅 +
렌더 + 종료까지 **124 ms**. CLI 기동 UX 에 영향 없음.

**알려진 경고 2건** — `R3` 1.3.1(Termina 의 리액티브 의존성)이 어셈블리 단위로
`IL3053`(AOT analysis) + `IL2104`(trim) 경고를 낸다. Termina 자체나 우리 코드가
아니다. `agent-one tui --selftest` 로 **퍼블리시된 AOT 바이너리에서 실제 렌더·키
라우팅·상태 전이가 정상 동작함을 확인**했으므로 현재는 무해하다. R3 를 더 깊이
쓰게 되면 재점검 대상.

---

## 2. 대조군 — 왜 Terminal.Gui 가 아닌가

`C:\code\psmon\CodeScan` 은 Terminal.Gui v2.0.0-beta 를 쓰고 csproj 에
`PublishAot=true` 를 켜 두었지만, **배포 스크립트가 트리밍을 끈다**:

```
Script/deploy-win.ps1:51   -p:TrimMode="" `
Script/deploy-linux.sh:43  -p:TrimMode="" \
```

그 결과 실제 배포물은 `~/.codescan/bin/codescan.exe` **112 MB** + `e_sqlite3.dll` +
`runtimes/` 사이드카다. AOT 단일 바이너리가 아니라 트리밍 없는 self-contained 빌드다.
(SQLite 네이티브 때문일 수도 있으므로 Terminal.Gui 단독 책임으로 단정하지는 않는다 —
다만 **AOT 무경고 통과가 실증된 쪽은 Termina 뿐**이다.)

netclaw 자신도 AOT 가 아니다. `PublishSingleFile` + self-contained 경로를 쓴다
(`scripts/swap-daemon.sh:73`, `IncludeNativeLibrariesForSelfExtract`). 즉 **netclaw 는
Termina 의 AOT 적합성을 증명해 주지 않는다** — 위 실측이 유일한 근거다.

### 후보 비교

| 후보 | AOT | 전체화면 TUI | 비용 |
|---|---|---|---|
| **Termina 0.16.2** | ✅ 실측 무경고 통과 + 실행 확인 | ✅ MVVM·라우팅·레이아웃 | 의존성 3~4개, +1~2 MB |
| Terminal.Gui v2 | ❓ 미실측. CodeScan 은 트리밍을 끔 | ✅ 성숙 | 배포물 비대 위험 |
| Spectre.Console | 문서상 AOT 지원(미실측) | ❌ 프롬프트/위젯 중심, 전체화면 프레임워크 아님 | 가벼움 |
| 무의존 Console 자작 | ✅ 자명 | 직접 구현한 만큼만 | 코드 200~300줄, 유지보수 자부담 |

---

## 3. netclaw 에서 참고할 구조 (agent-one 관점)

### 3-1. TUI 설정 화면의 조직 방식

`src/Netclaw.Cli/Tui/` 는 **Page + ViewModel 쌍**으로 화면을 나눈다:

```
Tui/ConfigDashboardPage.cs / ConfigDashboardViewModel.cs   ← 설정 허브
Tui/Config/                                                 ← 영역별 편집 화면
  WorkspacesConfigPage.cs / ...ViewModel.cs
  SecurityAccessPage.cs   / ...ViewModel.cs
  SearchConfigEditorPage.cs …
  SchemaDrivenConfigInfrastructure.cs   ← 스키마로 편집 폼을 생성
  ConfigAutosave.cs                     ← 저장 타이밍 정책
```

agent-one 은 설정 키가 8개뿐이라 이 규모는 과하다. 가져올 것은 **셋**:
`Page ↔ ViewModel` 분리, **스키마(키 목록·검증)에서 폼을 만든다**는 발상
— agent-one 은 이미 `AgentConfig.Keys` + `TrySet` 이 그 스키마 역할을 한다 —,
그리고 자동저장 정책을 한 곳에 모으는 것.

### 3-2. TUI 회귀 테스트 — VHS tape 하네스

`TOOLING.md` 의 *Interactive CLI Smoke Tests (Tape Harness)*: 실제 네이티브
바이너리를 [VHS](https://github.com/charmbracelet/vhs) tape 로 구동하고, tape 마다
어서션 스크립트가 산출물을 검증한다. 스크린샷 회귀(`run-smoke.sh screenshots`)까지
있다. agent-one 이 TUI 를 갖게 되면 참고할 만한 패턴이나, 현 규모에선 과투자다 —
Termina 의 `VirtualInputSource` 단위 테스트가 먼저다.

### 3-3. 그 외 눈여겨본 것 (지금 채택 대상 아님)

- **데몬 + 얇은 CLI 분리** — agent-one 은 반대로 "프로세스 하나로 끝"이 강점이라
  현 시점 채택 대상이 아니다. 다만 AgentZero 가 agent-one 을 상주시키게 되면
  다시 볼 구조.
- **승인 게이트(approval gates)** — agent-one 이 `write_file`/`run_shell` 로
  넓힐 때 직접 참고할 선례. `src/Netclaw.Cli/Approvals/`, `ShellCommandPolicy`.
- **중앙 패키지 버전 관리**(`Directory.Packages.props`) + `TreatWarningsAsErrors=true`.
- **openai-compatible 프로바이더의 API 키 선택화** — 로컬 엔드포인트는 키가 없다.
  agent-one 도 같은 선택을 이미 해 두었다(키 없으면 Authorization 헤더 생략).

---

## 4. 권고

**agent-one 의 설정 TUI 는 Termina 로 간다.**

근거: AOT 단일 바이너리라는 전제를 깨지 않는 유일한 실측 통과 후보이고,
`VirtualInputSource` 로 테스트 가능하며, 동일 제품군(netclaw)에서 실사용 중이다.
자작 Console UI 는 의존성 0이라는 장점이 있으나, 설정 화면 하나를 넘어
(모델 선택·세션 뷰어·승인 프롬프트) 확장되는 순간 프레임워크를 다시 짜게 된다.

**채택 완료 (2026-09-22)** — `Project/AgentOne/Tui/` 로 설정 TUI 구현. 남은 확인:

1. ~~바이너리 크기 재측정~~ → 8.28 MB (위 실측)
2. **linux-x64 / osx-arm64 AOT 퍼블리시는 아직 미확인** — win-x64 만 실측했다.
   operator 가 "당분간 이 컴퓨터 기준" 으로 진행하기로 했고, 멀티 OS 검증은 별도
   진행한다. 릴리스 워크플로의 스모크 단계에 `tui --selftest` 를 넣어 두었으므로
   태그를 밀면 4개 RID 에서 자동 판정된다.
3. ~~기동 시간 영향~~ → 없음 (위 실측)

## 재조사 트리거

이 문서는 2026-09-22 스냅샷이다. Termina 가 1.0 에 도달하거나, agent-one 이
TUI 를 설정 화면 밖으로 넓힐 때 재확인한다.
