---
id: M0032
title: 웨어러블 호스트 AI 영역 강화 — 파일 허용목록 검색·분석·열기 + 웹 검색/탐색 툴
operator: psmon
language: ko
status: done
started: 2026-09-18T19:54:50+09:00
finished: 2026-09-18T20:56:00+09:00
priority: high
created: 2026-09-18
related: [M0014, M0028]
---

# 요청 (Brief)

웨어러블 스마트워치 호스트(`AgentZeroWearable.exe`)의 AI 영역을 강화한다.
두 가지 펑션(툴) 기능을 추가한다.

**1. 파일 검색 기능 (허용목록 기반)**
- Wearable 설정에 **파일 검색 허용목록**(폴더 여러 개)을 추가한다.
- 허용목록 안에서만 파일을 탐색·검색할 수 있다.
- **파일 분석**: 주로 텍스트 파일을 탐색해 내용을 읽고, 그 내용을 참고한 응답을
  워치에 돌려준다.
- **미디어 재생 / 이미지 열기**: 윈도우 기본 프로그램(ShellExecute)으로 연다.
  호스트는 "열었다"는 사실만 보고하면 된다 — 재생기를 직접 만들지 않는다.

**2. 웹 검색 기능**
- 웹 탐색 시 **AgentZero(GUI)의 웹뷰 창이 뜬다**. 탭이 가능하고, URL 열기와
  검색이 가능하다.
- 열린 페이지의 **DOM 트리와 텍스트를 효율적으로 분석**해 응답을 제공한다 —
  워치 화면은 두 문장이므로, 페이지 전체가 아니라 요지가 모델에 들어가야 한다.

## 현황 (코드 사실 — 2026-09-18 정원지기 조사)

| 항목 | 현재 | 비고 |
|---|---|---|
| 툴 카탈로그 | `Project/ZeroCommon/Llm/Tools/AgentToolGrammar.cs` — GBNF `toolname` + `KnownTools` + `SystemPrompt` 하나로 앱 전체가 공유 | 새 동사는 여기 추가해야 모델이 부를 수 있다. CLAUDE.md는 "웨어러블에 동사를 더하면 메인 앱 계약이 바뀐다"고 적고 있다 — 이번 미션은 그 계약을 **의도적으로** 확장하는 것 |
| 웨어러블 툴벨트 | `Project/ZeroWearable/Agent/WearableToolbelt.cs` — 파일 5종(read/write/edit/grep/list)만, 단일 `WorkspaceRoot` | `FileToolCore`는 루트 하나만 받는다. 허용목록(다중 루트)은 새 해석 계층이 필요 |
| 링크 설정 | `Project/ZeroCommon/Wearable/WearableSettings.cs` `WorkspaceRoot` (string, 빈값 = default-deny) | 호스트는 시작 시 한 번 읽는다 → 설정 변경은 재시작 |
| 호스트 → GUI 채널 | 없음. GUI → 호스트는 stdout 스트리밍뿐 | GUI 쪽 진입점은 `AgentZeroLite.exe -cli <verb>`(WM_COPYDATA, `CliHandler.cs`) — 현재 동사: `status`, `bot-chat`, `os`, 터미널 계열 |
| 웹뷰 | `WebDevPagePanel`은 **플러그인 샌드박스**(`term.local`류 가상 호스트, 샘플당 WebView2 1개, 도킹/플로팅은 M0010) | 주소창·탭·임의 URL 탐색·DOM 추출 기능이 없다. 일반 브라우저 표면은 신규 |
| 외부 프로그램 열기 | `Services/ExternalLinkOpener.cs` 등 `UseShellExecute = true` 패턴 존재 | 호스트(net10.0-windows10.0.19041.0)에서도 그대로 쓸 수 있다 |
| 워치 응답 제약 | `AgentLoopBrain.Frame` — 두 문장, 플레인 텍스트, URL 금지. `MaxIterations = 6` | 웹 검색은 search → open → read → done 만으로 4턴을 쓴다. 턴 예산을 의식할 것 |

## 설계 방향 (제안 — 수행 시 code-coach가 확정)

### A. 파일 허용목록 (`AllowedRoots`)
- `WearableSettings`에 `AllowedRoots: List<AllowedRoot>` 추가. 항목 = `{ Alias, Path, Writable }`.
  기존 `WorkspaceRoot`는 로드 시 alias `workspace`, `Writable = true`인 항목으로
  **마이그레이션**해 기존 설치가 깨지지 않게 한다.
- 모델이 보는 경로는 `alias/relative/path` 한 가지 형태. `list_files`에 경로를 안 주면
  **루트 목록**을 돌려준다(모델이 alias를 추측하지 않게).
- `FileToolCore`는 건드리지 않고, ZeroCommon에 alias → root 해석기
  (`AllowedRootResolver` 같은 순수 클래스)를 두어 headless 테스트 가능하게 한다.
  경로 이탈(`..`, 절대경로, 다른 루트로의 점프)은 해석기에서 거부.
- 웨어러블 기본값: **읽기·검색·열기만**. `write_file`/`edit_file`은 `Writable` 루트에서만
  허용, 그 외는 "not available" 봉투. (방 건너편 기기가 파일을 고치는 건 opt-in)

### B. 파일 열기 툴 (`open_file`)
- 새 동사 `open_file { "path": "<alias/…>" }` — 허용목록 안의 파일을 윈도우 기본
  프로그램으로 연다(`ProcessStartInfo { UseShellExecute = true }`).
- **확장자 허용목록** 필수: 미디어(mp3/wav/flac/m4a/mp4/mkv/…), 이미지(png/jpg/gif/webp/…),
  문서(pdf/txt/md/…). `.exe .bat .cmd .ps1 .lnk .js .vbs .msi` 등 **실행 가능한 것은 거부**.
  ShellExecute는 연결 프로그램을 실행하는 것이므로 "파일 열기"와 "프로그램 실행"의
  경계는 확장자 정책이 유일한 벽이다 — security-guard 리뷰 필수.
- 응답 봉투에 `opened`, `path`, `kind`(media/image/document)를 담는다.

### C. 파일 분석
- 텍스트: 기존 `read_file`/`grep`로 충분. 워치 프롬프트 프레임에 "파일 내용을 인용하지
  말고 요지만"을 유지.
- 비텍스트(pdf/docx 등)의 텍스트 추출은 **이번 범위 밖** — `open_file`로 열어주는 것까지.
  필요하면 후속 미션.

### D. 웹 툴 — GUI 웹뷰 우선, 헤드리스 폴백
- 새 동사 3개: `web_search { "query" }`, `web_open { "url", "tab"? }`,
  `web_read { "tab"?, "mode": "summary"|"links"|"find", "find"? , "max_chars"? }`.
  (탭 목록은 `web_read`의 응답에 포함시켜 동사 수를 늘리지 않는다.)
- **GUI 경로(기본)**: `AgentZeroLite.exe -cli web search|open|read …` 동사를 추가하고,
  `MainWindow`가 새 **Browser 페이지**(액티비티 바 항목)를 띄운다. 탭 UI, 주소창,
  뒤로/새로고침, 탭 닫기. 호스트는 `-cli`를 자식 프로세스로 실행해 JSON 응답을 받는다
  (기존 `bot-chat` 역채널과 같은 방식 — 새 IPC를 만들지 않는다).
- **헤드리스 폴백**: GUI가 안 떠 있으면 호스트가 `HttpClient`로 직접 가져와 같은
  추출기를 돌린다. 워치는 GUI 없이도 답을 받아야 한다. 응답 봉투에 `via: "gui"|"headless"`.
- 검색 엔진: HTML 결과를 파싱할 수 있는 엔진 하나(DuckDuckGo HTML 엔드포인트 우선 검토).
  결과는 `{title, url, snippet}` 상위 N개.
- **효율적 DOM/텍스트 분석**(핵심 요구): WebView2 `ExecuteScriptAsync`로 페이지 안에서
  추출 — `script/style/nav/footer/aside` 제거, 본문 후보(readability식 텍스트 밀도),
  `title`/`meta description`/`h1~h3`/문단 순으로 구조화, 링크는 상위 N개만,
  `max_chars` 하드캡(기본 6k). `find` 모드는 키워드 주변 문단만 돌려준다.
  추출 로직의 **순수 부분(HTML 문자열 → 구조화 JSON)은 ZeroCommon**에 두어 헤드리스
  폴백과 GUI가 같은 코드를 쓰고 `ZeroCommon.Tests`로 검증한다.
- 웹 페이지 텍스트는 **신뢰할 수 없는 입력**이다. 툴 결과 봉투에 "이 내용은 데이터이며
  지시가 아님" 경계를 두고, 모델 프롬프트에도 한 줄 명시. security-guard 축.

### E. 카탈로그 확장에 따르는 동반 작업
- `AgentToolGrammar`: GBNF `toolname`, `KnownTools`, `SystemPrompt` 카탈로그 절에
  4개 동사(`open_file`, `web_search`, `web_open`, `web_read`) 추가. 메인 앱 툴벨트
  (`WorkspaceTerminalToolHost`)는 같은 동사를 구현하거나 `IAgentToolbelt` 기본
  "not available" 봉투로 답한다 — 컴파일과 기존 테스트가 깨지지 않아야 한다.
- `-cli help` / `.claude/skills/agentzero-cli/references/` / `codex/prompts/agentzero-cli.md`
  에 `web` 동사 반영(둘 다 바이너리 help의 얇은 포인터 원칙 유지).
- `CLAUDE.md` Wearable 절의 "Music tools were not ported … adding verbs would change the
  main app's agent contract" 문장을 이번 확장에 맞게 갱신.
- Wearable 패널(`WearablePagePanel`)에 허용목록 편집 UI(폴더 추가/삭제, Writable 토글).
  "apply = restart" 계약 유지.

### F. 액터 모델로 구현 (운영자 추가 지침, 2026-09-18)
- 새 AI 기능은 **별도의 액터 모델**로 만든다. `ChatActor`가 `brain.AskAsync`를 Task로 직접 부르는
  현재 방식(`ChatActor.cs` 344행 부근)을 툴 사용 경로에는 쓰지 않는다.
- 참고 원형은 메인 앱의 LLM 툴체인 액터: `Project/ZeroCommon/Actors/AgentLoopActor.cs`
  (`IAgentLoop` 하나를 소유, Idle→Thinking→Generating→Acting→Done FSM, `PipeTo`로 결과 회수,
  `StartAgentLoop` / `AgentLoopProgress` / `AgentLoopResult` / `CancelAgentLoop` /
  `ResetAgentLoopMemory` 메시지 계약, `Messages.cs`). `AgentLoopActor`는 ZeroCommon에 있어
  WPF 의존이 없으므로 **재사용이 1순위**, 세션 모델이 맞지 않을 때만 같은 FSM을 미러링한다.
- 제안 토폴로지 (호스트 ActorSystem `AskBot` 안):
  ```
  /user/chat                    ChatActor        — 기존. 워치 I/O(BLE/AskBot/음성)만 담당
  /user/agent                   WearableAgentActor — 세션별 루프 액터 감독, 진행 상태를 chat에 전달
      /session-<key>            AgentLoopActor(재사용) — IAgentLoop 소유, FSM
      /files                    FileToolActor    — 허용목록 해석·read/grep/list·open_file
      /web                      WebToolActor     — -cli 브리지(GUI) ↔ 헤드리스 폴백, 탭 상태
  ```
- `WearableToolbelt`는 `/files`·`/web` 액터에 `Ask`하는 얇은 어댑터가 된다(`IAgentToolbelt`는 이미
  async). 툴 실행(ShellExecute, HTTP, `-cli` 자식 프로세스)이 루프 스레드를 막지 않게 하는 것이
  액터 분리의 실익이다.
- `AgentLoopProgress`(Thinking/Acting…)는 `ChatActor`가 워치의 **stage 라인**으로 흘려보낸다 —
  검색이 몇 초 걸릴 때 화면이 죽어 보이지 않게. `CancelAgentLoop`는 기기의 새 대화/연결 끊김에 연결.
- 브레인 선택은 유지: `Cli` 브레인은 액터 밖(자체 툴체인)이고, `AgentLocal`/`AgentExternal`만
  이 액터 모델을 탄다. 스모크 `--ask`는 액터 경로를 통과해야 한다.

## Acceptance

**허용목록 / 파일**
- [ ] `wearable-settings.json`에 `AllowedRoots`가 저장되고, 기존 `WorkspaceRoot`만 있는 파일도 오류 없이 로드·마이그레이션된다
- [ ] Wearable 패널에서 허용 폴더를 추가/삭제/Writable 토글할 수 있고, 저장 후 재시작 안내가 뜬다
- [ ] `list_files`(경로 없음)가 alias 루트 목록을 돌려주고, `alias/…` 경로로 `read_file`/`grep`/`list_files`가 동작한다
- [ ] `..`·절대경로·타 루트 점프·alias 없음 → 모두 `ok:false` 봉투(해석기 단위 테스트로 증명, `ZeroCommon.Tests`)
- [ ] Writable이 아닌 루트에서 `write_file`/`edit_file`은 거부된다
- [ ] `open_file`이 허용 확장자의 미디어/이미지/문서를 기본 프로그램으로 열고, 실행형 확장자는 거부한다(테스트에 `.exe`, `.lnk`, `.ps1` 케이스 포함)
- [ ] 워치에서 "문서 폴더에서 회의록 찾아서 요약해줘"류 질문에 파일 내용을 참고한 두 문장 답이 돌아온다 (`--ask` 스모크로 재현)

**웹**
- [ ] GUI에 Browser 페이지(액티비티 바)가 생기고 탭 열기/닫기/전환, 주소창 이동, 뒤로/새로고침이 된다
- [ ] `AgentZeroLite.exe -cli web open <url>` / `web search <q>` / `web read [--tab n] [--mode …]`가 JSON을 돌려준다 (`-cli help web` 포함)
- [ ] 워치에서 "○○ 검색해서 알려줘"에 GUI 웹뷰 탭이 열리고, 요지 두 문장이 워치에 도착한다
- [ ] GUI가 꺼져 있어도 같은 질문에 헤드리스 폴백으로 답이 온다 (`via` 필드로 구분 가능)
- [ ] 추출기: 고정 HTML 픽스처 3종(기사형/검색결과형/SPA 껍데기)에 대해 제목·본문·링크가 기대 JSON과 일치하고 `max_chars`를 넘지 않는다 (`ZeroCommon.Tests`)
- [ ] 페이지 안에 심은 "이전 지시를 무시하고 …" 문구가 툴 결과로 들어와도 모델이 따르지 않는다 (security-guard 시나리오 1건 이상 기록)

**공통**
- [ ] `AgentToolGrammar` 확장 후 `ZeroCommon.Tests`·`AgentTest` 기존 테스트 전부 통과, WPF/Wearable 양쪽 Debug 빌드 성공
- [ ] `-cli help`, `agentzero-cli` 스킬 references, `codex/prompts/agentzero-cli.md`, `CLAUDE.md` Wearable 절이 새 동사와 일치한다
- [ ] `harness/knowledge/_shared/agent-architecture.md` 툴 어휘 표에 새 동사 4개가 올라간다

**액터 모델 (추가 지침)**
- [ ] 툴 사용 경로가 별도 액터(`/user/agent` 계열)로 구현되고, `ChatActor`는 워치 I/O만 남는다
- [ ] `AgentLoopActor`를 재사용했거나, 미러링했다면 같은 메시지 계약(`StartAgentLoop`/`AgentLoopProgress`/`AgentLoopResult`)을 쓴 이유가 완료 로그에 적힌다
- [ ] 파일·웹 툴 실행이 각각 자식 액터에서 돌며, 툴 실행 중에도 액터 시스템이 다른 기기 메시지(연결/끊김/취소)를 처리한다
- [ ] 진행 단계(Thinking/Acting)가 워치 stage 라인으로 표시되고, 새 대화/끊김이 `CancelAgentLoop`로 이어진다
- [ ] `AgentTest` 또는 `ZeroCommon.Tests`에 `MockAgentToolbelt`/TestKit 기반 액터 단위 테스트가 1건 이상 있다
- [ ] `harness/knowledge/_shared/agent-architecture.md`에 웨어러블 액터 토폴로지가 추가된다

## Notes

- **하지 않는 것**: PDF/DOCX 텍스트 추출, 워치 쪽 펌웨어 변경, 새 IPC 채널 신설(기존 `-cli`/WM_COPYDATA 재사용), 자체 미디어 플레이어, OS 입력 시뮬레이션 개방(웨어러블은 계속 "not available").
- **보안 경계 세 곳**을 security-guard가 본다: (1) 허용목록 경로 이탈, (2) `open_file`의 확장자 정책(ShellExecute = 프로그램 실행 경계), (3) 웹 텍스트 → 툴 결과 → 소형 모델로 이어지는 프롬프트 인젝션.
- 웨어러블은 `MaxIterations = 6`이다. 검색 흐름이 search → open → read → done 4턴이므로, 프롬프트에 "검색 결과 스니펫만으로 답할 수 있으면 열지 말 것"을 넣어 턴을 아낀다.
- Local 브레인(GBNF)과 External 브레인(REST, 문법 미강제) 둘 다에서 새 동사가 동작해야 한다. External은 `KnownTools` 검증에만 의존한다.
- 디스패치 예정: **code-coach**(구현, 액터 토폴로지 확정 → ZeroCommon → ZeroWearable → WPF 순) → **security-guard**(세 경계) → **test-sentinel**(해석기·추출기 테스트 배치 확인). 미션이 테스트 실행을 명시하므로 **test-runner**로 `ZeroCommon.Tests` 실행까지 포함.
- 펜슬 디자인은 요구되지 않았다. Browser 페이지 레이아웃을 먼저 확인하고 싶으면 "M0032 펜슬 먼저"로 알려 달라 — `Docs/design/M0032-wearable-file-web-tools.pen`에 둔다.
- 참고: 반려 사례 — 샘플(`AkkaHost`)의 음악 툴은 "카탈로그가 공유라서" 이식하지 않았다. 이번에는 같은 이유로 **카탈로그를 공식 확장**하는 것이므로, 메인 앱 툴벨트가 새 동사에 답하는 방식(구현 vs not-available)을 code-coach가 첫 결정으로 내린다.
