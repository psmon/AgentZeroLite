# AgentZero Lite (Avalonia) — Microsoft Store 등록 가이드

Avalonia 호스트(`Project/AgentZeroAvalonia`)의 Windows 빌드를 Microsoft Store에 올리는 절차와 설계.
**현재 기준(2차 제출, 2026-10-02~)** 으로 쓰여 있고, 1차 제출과 반려의 기록, 공개 후 업데이트(5차·6차)는 §6에 있다.

> 2026-09 기준으로 확인한 사실: 개인·회사 개발자 계정 모두 등록비 무료(정부 신분증 + 셀피 인증),
> MSIX 제출은 Store가 Microsoft 인증서로 다시 서명하므로 코드 서명 인증서가 필요 없음,
> EXE/MSI 제출은 직접 산 Authenticode 인증서로 서명해야 함(자체 서명 불가).

---

## 0. 설계: 스토어판은 자기 데이터를 격리하고, 사용자가 실행한 도구는 격리하지 않는다

MSIX로 제출한다(Store가 무료로 서명·호스팅·자동 업데이트). MSIX 앱은 기본적으로 `%LOCALAPPDATA%`·`%APPDATA%`·
`HKCU`에 **새로** 만드는 것을 패키지 전용 저장소(`%LOCALAPPDATA%\Packages\<패키지>\LocalCache\…`)로 돌리고,
**앱이 띄운 자식 프로세스도 같은 격리를 받는다.** AgentZero는 이것을 두 갈래로 다룬다.

| 대상 | 동작 | 이유 |
|---|---|---|
| **스토어판 앱 자신의 데이터** (DB `agentZeroLite.db`, 설정 JSON, 로그) | **격리된다** (새 PC 기준) | 의도된 동작. 앱을 지우면 함께 지워지고, 다른 앱과 충돌하지 않으며, 특별 승인이 필요 없다. **WPF·일반 설치판과 데이터를 공유할 필요는 없다** |
| **터미널 탭과 설정의 설치 버튼이 실행하는 것** (셸, `npm install -g`, node, claude, codex, git…) | **격리되지 않는다** | 사용자의 작업이지 앱의 데이터가 아니다. 갇히면 "설치했는데 다른 터미널에선 없음", "앱을 지우니 도구도 사라짐"이 된다 |

두 번째 줄은 Windows의 **desktop app breakaway** 프로세스 정책으로 구현한다
(`ZeroCommon/Services/DesktopAppBreakaway.cs`): 패키지로 실행 중일 때만, 앱이 만드는 프로세스에
`PROC_THREAD_ATTRIBUTE_DESKTOP_APP_POLICY = ENABLE_PROCESS_TREE`를 붙여 그 프로세스와 자식들이 패키지 밖에서 돈다.

- 터미널 탭: `ConPtyHost`가 의사 콘솔 속성 옆에 이 속성을 넣는다.
- 설정의 설치 버튼: `AgentCliTools.RunInstallAsync` → `DesktopAppBreakaway.RunCapturedAsync`(같은 속성 + 출력 캡처).
- 패키지가 아닐 때(일반 설치판, WPF, 테스트): `IsPackagedProcess()`가 false라 아무것도 바뀌지 않는다.

참고: 이미 일반판을 쓰던 PC에서는 **기존** DB·설정 파일이 제자리에서 수정되므로(MSIX는 기존 파일 수정은 격리하지
않는다) 스토어판도 같은 파일을 쓰게 된다. 새 PC에서만 스토어판 데이터가 패키지 안에 따로 놓인다. 어느 쪽이든 문제없다.

필요한 제한된 기능은 `runFullTrust` 하나(ConPTY·named pipe·loopback 서버는 AppContainer에서 돌 수 없다).
`unvirtualizedResources`(가상화 전체 해제)는 쓰지 않는다 — 1차 제출에서 거부됐고(§6), 위 설계로 필요 없어졌다.

---

## 1. 사전 준비 — 사용자가 할 일

1. **Partner Center 개발자 계정** — <https://storedeveloper.microsoft.com> 에서 개인(또는 회사) 계정 가입.
   무료. 신분증 + 셀피 인증을 마치면 바로 Partner Center에 들어갈 수 있다.
2. **앱 이름 예약** — Partner Center → Apps and games → New product → **MSIX or PWA app** → `AgentZero Lite`.
3. **패키지 ID 값** — 예약한 앱 → Product management → **Product identity** 의 세 값(Name, Publisher,
   PublisherDisplayName)을 `.secret/msstore-identity.json`(git 제외)에 그대로 적는다. 대소문자·공백까지 일치해야 한다.
4. **개인정보 처리방침 URL** — 앱이 인터넷에 연결하므로 필수: **GitHub Pages의 HTML 페이지**
   https://psmon.github.io/AgentZeroLite/Home/privacy-policy.html
   (원본은 `Docs/privacy-policy.md`, 페이지는 `Home/privacy-policy.html`. GitHub의 `blob/…` 파일 보기 URL은
   "작동하는 웹 페이지가 아니다"로 반려됐다 — §6.)
5. **로컬 검증 환경**(§3) — Windows SDK(10.0.26100), **설정 → 시스템 → 개발자용 → 개발자 모드 켜기**,
   WACK 실행용 관리자 PowerShell.

---

## 2. 저장소 셋업

| 파일 | 역할 |
|---|---|
| `Project/AgentZeroAvalonia/msix/Package.appxmanifest` | MSIX 매니페스트 템플릿. `runFullTrust`, 실행 별칭 `AgentZeroLite.exe`(아무 셸에서 `-cli`), 최소 Windows 10 19041 |
| `Project/AgentZeroAvalonia/msix/Assets/*.png` | 타일·작업 표시줄 아이콘(44/150/310×150/StoreLogo 50, targetsize 24/48/256). `agentzero.ico`에서 생성 |
| `Project/AgentZeroAvalonia/msix/build-msix.ps1` | publish(win-x64 self-contained) → 스테이징 → ID 채우기(`.secret/msstore-identity.json`) → `makepri` → `makeappx pack`. `-Register`(개발자 모드 제자리 등록, `stage-dev` 폴더), `-Sign`(테스트 인증서 서명) |
| `Project/AgentZeroAvalonia/msix/store-listing/` | 스토어 등록 문구(`listing.md`), 300×300 로고, 스크린샷 |
| `Project/ZeroCommon/Services/DesktopAppBreakaway.cs` | 패키지 실행 감지 + breakaway 속성 + 설치 명령 실행기 (§0) |
| `Docs/privacy-policy.md` → `Home/privacy-policy.html` | 개인정보 처리방침 원본 → GitHub Pages로 게시되는 페이지 (둘을 함께 고칠 것. 배포: `doc-v*` 태그 또는 Pages 워크플로 수동 실행) |
| `.gitignore` | `/publish-msix/` (빌드 산출물), `/.secret/` |

**버전 규칙**: Store는 4자리 버전에서 첫 자리 0 금지, 마지막 자리 0 고정. 앱이 `0.x`라 MSIX 버전은
**메이저 + 1**: `0.25.1 → 1.25.1.0`, 나중의 `1.0.0 → 2.0.0.0`. 단조 증가만 지키면 된다. **제출마다 버전을 올린다** —
같은 이름·버전의 패키지가 제출 안에 남아 있으면 새 업로드가 0바이트에서 멈췄다(2차 제출 때 실측).

---

## 3. 빌드와 로컬 검증

```powershell
cd Project\AgentZeroAvalonia\msix

# (a) 개발자 모드에서 제자리 등록 — 서명 없이 패키지 상태로 실행해 본다 (dev ID, stage-dev 폴더)
./build-msix.ps1 -Register
#   시작 메뉴 "AgentZero Lite" 실행, 다른 터미널에서:
#   %LOCALAPPDATA%\Microsoft\WindowsApps\AgentZeroLite.exe -cli status
#   되돌리기: Get-AppxPackage -Name AgentZeroLite.Dev | Remove-AppxPackage

# (b) Store 업로드용 — ID는 .secret/msstore-identity.json에서 읽는다
./build-msix.ps1
#   → publish-msix\AgentZeroLite_<버전>_x64.msix  (이 파일을 업로드. 서명 불필요)

# (c) WACK — 관리자 PowerShell에서 (Store 인증과 같은 검사)
& "C:\Program Files (x86)\Windows Kits\10\App Certification Kit\appcert.exe" test `
  -appxpackagepath publish-msix\AgentZeroLite_<버전>_x64.msix -reportoutputpath publish-msix\wack.xml
```

패키지 상태에서 확인할 것:

- [ ] 터미널 탭에서 **새 폴더**를 만들면(셸 자신이든, 셸이 띄운 프로그램이든) **실제 경로**에 생기는가
      (`%LOCALAPPDATA%\Packages\<패키지>\LocalCache`에 생기면 breakaway가 안 먹은 것)
- [ ] 터미널 탭에서 `npm install -g <패키지>` 후 **앱 밖의** 터미널에서 그 명령이 보이는가
- [ ] 다른 셸에서 실행 별칭 `AgentZeroLite.exe -cli status` 가 동작하는가
- [ ] Settings → CLI Definitions → 에이전트 CLI 설치 확인·설치가 동작하는가
- [ ] 터미널 탭(xterm.js/WebView2)이 뜨는가 — WebView2 런타임 필요(§5)

### 로컬 검증 결과 (2차 방식, 2026-10-01, Windows 11 26200, 개발자 모드 등록 패키지)

| 실험 | 셸 자신이 만든 새 폴더 | 셸이 띄운 cmd가 만든 새 폴더 |
|---|---|---|
| breakaway 속성으로 만든 cmd (`Invoke-CommandInDesktopPackage` 안에서) | ✅ 실제 경로 | ✅ 실제 경로 |
| 속성 없음 (대조군) | ❌ 패키지 저장소 | ❌ 패키지 저장소 |
| **실제 패키지 앱의 터미널 탭** (`layout add` → `terminal-send`) | ✅ 실제 경로 | ✅ 실제 경로(`%APPDATA%`) |

| 그 밖의 점검 | 결과 |
|---|---|
| 실행 별칭 `-cli` | ✅ 별칭 전체 경로로 `-cli status`, `terminal-list`, `terminal-read` 동작 |
| 터미널 탭 + AgentOne 기본 CLI | ✅ `agent-one chat` TUI가 ConPTY/WebView2에서 정상 표시 |
| WACK (1차 업로드 패키지) | ✅ **OVERALL PASS**. 선택 항목 "차단된 실행 파일"만 FAIL — 셸 실행 참조. 터미널 호스트라 당연하고, 인증 메모에 설명했다 |

실측에서 배운 것:

- **기존 폴더에 쓰는 테스트는 격리 여부를 구별하지 못한다.** 이미 있는 폴더 안의 새 파일은 격리 상태에서도 실제 위치에
  써졌다. 차이는 **아직 없는 폴더**를 만들 때 나타난다 — 검증은 항상 새 폴더로 할 것.
- **PATH가 별칭을 가릴 수 있다.** 이 개발 PC는 PATH에 WPF Debug 빌드 폴더가 먼저 있어 셸의 `AgentZeroLite.exe`가 WPF로
  풀린다. Store 판만 설치한 사용자에게는 문제없지만, 개발 PC에서는 별칭 전체 경로로 테스트할 것.

---

## 4. Partner Center 제출

1. **Packages** — (b)의 `.msix` 업로드. 장치 패밀리: Desktop만. 자동화(CDP 파일 주입)는 기존 패키지와 이름이
   겹치면 멈추므로, 교체할 때는 버전을 올리고 직접 끌어다 놓는 것이 안전하다. 자동화한다면 Playwright
   `setInputFiles`가 아니라 CDP `DOM.setFileInputFiles`로, 경로는 슬래시로(50 MB 제한, §6 5차·6차 제출).
2. **Properties** — Category: *Developer tools / Utilities*. Privacy policy URL: §1-4. 웹사이트 = 저장소,
   지원 연락처 = `psmon@live.co.kr`(심사자가 직접 연락처를 요구했다). 생성형 AI 기능 선언 체크.
3. **Age ratings** — IARC 설문. "다운로드 외 콘텐츠 접근"(웹·AI 생성 텍스트)만 예. 결과 3+ / Everyone.
4. **Pricing and availability** — Free, 시장 선택.
5. **Submission options** — 제한된 기능 사유는 `runFullTrust` 하나(`listing.md`). 공개 보류(Publish now를 누를 때까지).
6. **Store listings** (en-us) — 설명·기능·키워드·지원 문구는 `listing.md`, 스크린샷은 `store-listing/screenshots/`.
7. **Additional Testing Information** — `listing.md`의 인증 메모(계정 불필요, breakaway 설명, WACK 선택 항목 설명).
8. 제출 → 인증(보통 수 시간~3 영업일). 통과하면 개요 화면에서 **Publish now**.

---

## 5. 알려진 이슈와 후속 과제

- **WebView2 런타임**: Windows 11과 대부분의 Windows 10에는 이미 있지만 보장되지 않는다.
  없으면 터미널 탭이 뜨지 않는다. 후속: 시작 시 런타임을 확인하고 설치 링크를 안내.
- **읽기 전용 설치 폴더**: Store 판은 `C:\Program Files\WindowsApps\…` 에 설치된다. 코드는 설치 폴더를 읽기만 한다.
  단, 워크스페이스 없이 연 터미널 탭의 시작 폴더가 exe 폴더라 그 탭에서는 파일을 못 쓴다. 후속: 기본 작업 폴더를 홈으로.
- **일반판과 동시 실행 불가**: 같은 단일 인스턴스 뮤텍스를 쓰므로 Store 판과 일반 설치판(WPF·Avalonia)을 동시에
  띄울 수 없다. 데이터 공유가 목적은 아니다(§0) — 한 PC에서 두 판을 같이 쓰는 경우는 고려하지 않는다.
- **자동 업데이트**: Store 판은 Store가 업데이트한다. GitHub 릴리스 판과 버전 번호 체계(§2)가 다르다는 점만 기억.
- **CI 자동화(후속)**: `avalonia-build.yml`에 MSIX 빌드를 붙이고 `msstore` CLI로 제출을 자동화할 수 있다.
- **arm64(후속)**: 지금은 x64만. arm64 publish를 더하면 `.msixbundle`로 묶는다.

---

## 6. 기록: 1차 제출과 반려 (2026-09-28 ~ 09-30)

1차 제출은 격리를 **전부 끄는** 방식이었다: 매니페스트에 `unvirtualizedResources` +
`desktop6:FileSystemWriteVirtualization=disabled` + `RegistryWriteVirtualization=disabled`. 당시 근거는
"DB·설정을 WPF·`-cli`와 공유해야 한다"와 "터미널에서 설치한 도구가 갇히면 안 된다" 두 가지였다. 앞의 것은 **필요 없는
요구였고**(스토어판은 자기 데이터를 따로 써도 된다), 뒤의 것은 breakaway로 해결된다 — 그래서 지금 설계(§0)가 됐다.

반려 리포트(10.6.3 Capabilities): *"unvirtualizedResources 요청은 제공된 정보로는 거부. 기능을 빼고 다시 제출하거나,
새/보강된 사유로 재검토를 요청하라. 유효한 지원 연락처 또는 개발자 웹사이트 URL을 포함할 것."* → 기능 제거 + 앱 수정
(`DesktopAppBreakaway`), 지원 연락처를 이메일로, 버전 `1.25.1.0`으로 2차 제출(2026-10-02).

1차 방식의 로컬 실측(참고): 가상화 해제 패키지에서는 새 폴더가 실제 경로에, 같은 패키지에서 가상화 설정만 뺀 대조군에서는
`LocalCache\Local`·`LocalCache\Roaming`으로 숨겨졌다 — 이 대조 실험이 2차 방식 검증의 바탕이 됐다.

### 2차 제출 반려 (2026-10-02 심사)

반려 리포트(10.5.1 Personal Information - Privacy Policy): *"개인정보 처리방침 링크가 작동하는 웹 페이지로 연결되지
않는다."* — 제공한 URL은 `https://github.com/psmon/AgentZeroLite/blob/main/Docs/privacy-policy.md`(GitHub의 저장소 파일
보기). 우리 쪽에서는 200이 나왔지만 심사 환경에서는 정식 웹 페이지로 인정되지 않았다. **제한된 기능에 대한 지적은 없었다**
(`unvirtualizedResources` 제거 + breakaway 방식은 통과한 것으로 보인다).

대응: 같은 내용을 HTML 페이지(`Home/privacy-policy.html`)로 만들어 GitHub Pages에 게시하고 URL을 교체, 같은 패키지
(1.25.1.0)로 3차 제출.

### 3차 제출 반려 (2026-10-05 심사)

반려 리포트(11.16 Live Generative AI Content): *"사용자 입력을 AI로 처리해 AI 모델이 만든 출력을 새 콘텐츠로 보여 주는
제품으로 보인다. 생성형 AI가 들어간 제품은 부적절한 AI 생성 결과를 사용자가 신고할 수단(예: Report an Issue)을
제공해야 한다."* (테스트 기기 Lenovo ThinkPad 450s) — **개인정보 처리방침과 제한된 기능에 대한 지적은 없었다**(2차 대응은 통과).
리포트 말미대로 정책이 바뀌면 이전 제출도 새 기준으로 다시 검사된다.

대응: AgentBot의 AI 답변 말풍선마다 **⚑ Report** 버튼 → "Report this AI answer" 패널(사유 4종 + 메모) → 답변을 인용한
메일(지원 연락처) 또는 GitHub 이슈를 **미리 채워서** 연다. 서버가 없으므로 보내는 것은 사용자다. 로직은 ZeroCommon
`Llm/AiContentReport`(WPF 봇 창도 같은 것을 쓸 수 있게), UI는 `AiReportViewModel` + `AgentBotView`. 버튼은 패널 제목
줄에 둔다 — 봇 창이 낮으면 아래 둔 버튼이 스크롤 밖으로 숨는 것을 실측했다. 인증 메모에 버튼 위치와 모델 없이 확인하는
방법(`-cli bot-chat`)을 적고, `1.25.2.0`으로 4차 제출(2026-10-07).

### 4차 제출 통과 (2026-10-08)

`1.25.2.0` 인증 통과 → Publishing. 네 번의 반려 사유가 모두 해소됐다: 10.6.3(`unvirtualizedResources` 제거 +
breakaway), 10.5.1(개인정보 처리방침을 GitHub Pages HTML로), 11.16(AI 답변 신고). Store ID `9NRHDFMD38H7`,
PFN `webnori.AgentZeroLite_7kv8x6xnmh6y0`. Store 딥링크·웹 URL은 공개가 끝나야 생긴다.

다음 제출 때 기억할 것: Partner Center의 패키지 업로드 화면은 112 MB MSIX에서 "Analyzing package"로 30분 넘게 멈춘 채
저장 버튼을 막았다 — 새로고침하면 패키지는 이미 서버에 있고 저장이 풀린다. 저장 후에는 개요 화면에서 이전 버전이
목록에서 빠졌는지 확인한다(첫 저장은 반영되지 않았다).

### 5차 제출 — 첫 업데이트 (1.25.3.0, 2026-10-09)

공개된 앱의 첫 업데이트. 내용은 OpenAI 추론 모델(gpt-5·o 계열)에서 최대 토큰 설정이 400을 내던 문제의 수정
(ZeroCommon `OpenAiCompatibleProvider`: OpenAI에는 `max_completion_tokens`, 추론 모델에는 temperature 생략).
업데이트 절차: 개요 화면 **Start update** → 새 Submission(이전 제출의 속성·등록 정보·연령 등급이 그대로 복사됨) →
Packages에 새 MSIX → Store listings(English)의 *What's new in this version* → **Submit for certification**.
나머지 항목은 Unchanged로 둔다. 인증 통과 후 **Publish now**를 눌러야 공개된다.

이번에 확인한 것:

- **버전이 version.txt에서 나오는 값보다 낮을 수 있다.** `version.txt`가 `0.25.1`인 채로 4차 제출을
  `-Version 1.25.2.0`으로 손으로 올렸기 때문에, 자동 값(`1.25.1.0`)은 이미 공개된 버전보다 낮았다. 이번에는
  `-Version 1.25.3.0`으로 빌드하고, 같은 날 WPF 릴리스를 `v0.25.3`으로 찍어 둘을 다시 맞췄다
  (WPF `0.25.x` = MSIX `1.25.x.0`). 다음 제출부터는 `version.txt`를 올리고 `-Version` 없이 빌드하면 된다.
- **Playwright(CDP 접속)로는 50 MB 넘는 파일을 넣을 수 없다** — `setInputFiles`가 "Cannot transfer files larger
  than 50Mb to a browser not co-located with the server"로 거부한다. 같은 PC의 브라우저라면 CDP
  `DOM.setFileInputFiles`(Runtime.evaluate로 `input[type=file]`의 objectId를 얻어 경로만 넘김)로 112 MB가
  45초 만에 올라가고 바로 Validated가 됐다. 이번에는 "Analyzing package"에서 멈추지 않았다.
- **이전 패키지는 직접 지우지 않아도 된다.** 더 높은 버전이 같은 고객을 덮으면 Packages 화면이 이전 패키지에
  "This package will be removed after you save this page"를 붙이고, 저장하면 빠진다(이번에는 첫 저장에 반영됐다).
  그 화면의 **Remove** 버튼은 *새* 패키지에 붙어 있으니 누르지 않는다.
- 로그인은 사람이 한다: 자동화 전용 프로필의 Edge를 `--remote-debugging-port`로 띄우고, 로그인 후
  `chromium.connectOverCDP`로 붙었다. 개요 화면의 섹션 링크는 클릭보다 `submissions/<id>/packages` 같은 URL로
  바로 가는 편이 안정적이었다.

### 6차 제출 — 두 번째 업데이트 (1.25.4.0, 2026-10-15)

5차 이후 Avalonia 호스트에 들어온 기능을 묶은 업데이트: Plugins 페이지(WPF 웹 플러그인 이식 — Voice Note,
Agent Band, Token Monitor/Remaining), Settings → Models(음악 분류·비전·화자 분리 모델 내려받기), Settings → Voice,
AI 모드의 MCP 서버, 사이드바 ACTIVE SESSIONS(에이전트 상태, 포커스 세션 강조), 오프라인 네트워크 드라이브 멈춤 수정.
`version.txt`를 `0.25.4`로 올리고 `-Version` 없이 빌드(147 MB). 매니페스트에 `DeviceCapability microphone`이
새로 들어갔고, 개인정보 처리방침을 같은 날 갱신(마이크·시스템 오디오, OpenAI 음성, MCP, 플러그인 설치·YouTube)한 뒤
제출했다. 절차는 5차와 같다 — Start update(Submission 3) → Packages → Store listings(English)의 *What's new*와
Product features 두 줄 → Submit for certification. 인증은 제출 당일 통과(Ready to publish) — 공개된 앱의
업데이트는 첫 제출과 달리 금방 끝났고, 공개는 **Publish now**를 눌러야 시작된다.

이번에 확인한 것:

- **`DOM.setFileInputFiles`의 경로는 슬래시로 넘긴다.** 백슬래시 경로가 명령 파일을 거치며 빠져
  (`C:codepsmon…msix`) 브라우저가 이름만 있고 0바이트인 파일을 만들었고, 업로드가 "0 Bytes / Uploading"에서
  멈췄다. `C:/code/…` 형태로는 154,411,714바이트 그대로 잡혔다. 먼저 빈 `input[type=file]`을 만들어
  `files[0].size`를 확인하면 업로드 전에 알 수 있다. 실패한 항목은 "Paused · Delete · Retry"로 남아 저장을 막으니
  **Delete**로 지운다.
- **"Analyzing package"가 다시 멈췄다**(147 MB, 20분+). 표에는 이미 v1.25.4.0이 1순위로 올라와 있었고, 새로고침하자
  패키지가 남아 있고 Save가 풀렸다(2차 제출 때와 같은 현상). 저장 후 개요에서 1.25.4.0 하나만 Validated.
- **인증 노트는 더 이상 Submission options에 없다.** 그 화면은 "Additional Testing Information 페이지에 적으라"는
  안내만 남았고, 노트는 제품 단위 페이지(`suppinfo/additionaltestinginfo`, 저장 버튼은 **Save description**)에 있다.
  기존 노트 끝에 마이크 안내 한 단락을 붙였다(`store-listing/listing.md`의 "About the microphone").

### 7차 제출 — 세 번째 업데이트 (1.25.5.0, 2026-10-16)

내용: 터미널을 별도 창으로 분리하고 다시 도킹(WPF의 Float / Dock — 탭 메뉴, 페인의 ⤢/⤡, Ctrl+Alt+F / D),
새 터미널의 첫 출력이 사라질 수 있던 문제 수정(`PtyOutputRelay`). 새 권한·데이터 흐름이 없어 인증 노트와
개인정보 처리방침은 그대로 두었다. Submission 4. 절차는 6차와 같고 이번에는 막힌 곳이 없었다 — 슬래시 경로로
넣기 전에 빈 `input[type=file]`로 크기를 먼저 확인했고(154,418,979바이트), 업로드 45초 후 표에 v1.25.5.0이
1순위로 올라왔고 Save가 바로 열렸다("Analyzing package"에서 멈추지 않음).

이번에 확인한 것:

- **로그인 탭이 하나 남아 있으면 탭 고르기가 틀어진다.** 만료된 로그인 페이지 URL에도
  `redirect_uri=…partner.microsoft.com…`이 들어 있어서, `url.includes('partner.microsoft.com')`가 로그인 탭을
  골랐다. `url.startsWith('https://partner.microsoft.com/')`로 고른다. 같은 이유로 "로그인 탭이 없어질 때까지"
  기다리는 감시는 남은 로그인 탭 때문에 끝나지 않았다.
- **"Sign in required" 문구가 DOM에 있다고 세션이 끊긴 것은 아니다.** 그 대화상자는 숨겨진 채 늘 들어 있다.
  세션은 개요 화면의 Submission 데이터가 읽히는지로 판단한다. 첫 Submit 클릭이 반영되지 않았던 것은 세션이
  아니라 클릭 직후 상태를 너무 일찍 읽은 것으로 보이며, 다시 눌러 In certification이 됐다.

### 8차 제출 — 네 번째 업데이트 (1.25.6.0, 2026-10-17)

내용: 코드 뷰(하단 패널 CODE 탭 — 워크스페이스 파일, VS Code TextMate 문법, 마크다운 렌더링 + Mermaid,
별도 창), 플러그인 별도 창 분리(항상 위·투명도·위치 기억), 탭 이름 변경 즉시 반영. Submission 5. 새 권한은 없지만
마크다운 미리보기가 문서에 적힌 https 이미지를 불러오므로 개인정보 처리방침에 행을 하나 더하고(`doc-v1.43.0`) 배포가
끝난 것을 확인한 뒤 제출했다. 같은 날 GitHub 릴리스 `v0.25.6`도 찍어 버전을 맞췄다. 패키지 157 MB(에디터·TextMate
문법·`libonigwrap`·Markdig 약 8 MB 증가). 업로드 45초, Save 바로 열림, 첫 Submit 클릭에 In certification.

### 9차 제출 — 다섯 번째 업데이트 (1.25.7.0, 2026-10-19)

내용: Claude Code / Codex 터미널이 열릴 때 그 에이전트가 폴더에 기록해 둔 세션을 골라 이어 열기(최신이 위,
"다시 묻지 않기"는 에이전트별), 사이드바의 git 워크트리(워크스페이스 아래 중첩 — Claude Code의
`.claude/worktrees`, Codex의 `$CODEX_HOME/worktrees` 포함), 작업 폴더에 닿지 못한 터미널은 홈 폴더에서 시작.
Submission 6. 새 권한·네트워크 흐름은 없지만 다른 프로그램의 파일(`.claude`·`.codex` 세션 파일, 워크스페이스의
`.git`)을 PC 안에서 읽으므로 개인정보 처리방침에 문단을 하나 더하고(`doc-v1.44.0`) 페이지 반영을 확인한 뒤 제출했다.
같은 날 GitHub 릴리스 `v0.25.7`. 패키지 157 MB, 업로드 약 60초, "Analyzing package"에서 멈추지 않음, 첫 Save에
1.25.6.0이 빠지고 1.25.7.0 하나만 Validated, 첫 Submit 클릭에 In certification.

이번에 확인한 것:

- **Store listings의 언어 링크는 접혀 있다.** 개요의 "Languages supported in packages"를 펼쳐야 English 링크
  (`submissions/<id>/listings?languageid=4`)가 DOM에 생긴다.
- **Product features 입력은 10칸만 보이고, 11번째부터는 "Add more"로 추가한다.** 이 "Add more"는 button도 link도
  아닌 텍스트라 역할로는 잡히지 않는다(`getByText('Add more', { exact: true })`). 입력 id는 `feature-<n>`,
  What's new는 `releaseNotes`.
- **태그 뒤에 macOS CI가 버그를 찾았다.** `/var/folders`가 `/private/var/folders`의 심볼릭 링크라 git이 기록한
  경로와 워크스페이스 경로 문자열이 달라 워크트리를 못 찾았다. Windows에는 해당 경로가 없어 동작이 같으므로
  패키지와 Windows 릴리스는 그대로 두고 수정만 main에 올렸다(f8e87da, ae76f01 — 경로를 링크까지 풀어서 비교; 다음 릴리스에 포함). 릴리스 태그 전에
  `Avalonia host — build & self-test`(macOS 포함)가 초록인지 먼저 보는 편이 낫다.

### 10차 제출 — 여섯 번째 업데이트 (1.25.8.0, 2026-10-19)

내용: ACTIVE SESSIONS의 에이전트 상태를 화면만이 아니라 에이전트 자신의 기록에서 읽는다 — Claude Code는
`~/.claude/sessions/<pid>.json`(busy / waiting / idle), Codex는 스레드 rollout의 `task_started` / `task_complete`.
작업 중에는 턴 내내 working, 질문은 waiting, 끝난 턴은 1초 안에 done(확인 전까지). 링크를 거친 워크트리 경로 수정.
Submission 7. 새 권한·네트워크 흐름은 없지만 상태를 읽으려고 Claude/Codex의 상태 파일과 **실행 중인 프로세스
목록**을 PC 안에서 읽으므로 개인정보 처리방침 문단을 넓히고(`doc-v1.45.0`) 반영을 확인한 뒤 제출했다. 같은 날
GitHub 릴리스 `v0.25.8`. 패키지 157 MB, 업로드 약 60초, 첫 Save에 1.25.7.0이 빠지고 1.25.8.0만 Validated, 첫 Submit
클릭에 In certification. Features는 ACTIVE SESSIONS 줄만 고쳤다(12줄 그대로).

이번에 바뀐 것: 멀티 OS CI(`avalonia-build.yml`)는 더 이상 push나 `v*` 태그로 돌지 않는다 — `ci-*` 태그나 수동 실행으로만.
이번 릴리스는 macOS 점검 없이 냈다(필요할 때 `ci-<버전>` 태그).

### 11차 제출 — 일곱 번째 업데이트 (1.25.9.0, 2026-10-22)

내용: 코드 뷰의 **GIT LOG** 탭(CODE 옆 — 워크스페이스의 최근 커밋 100개를 GitHub Desktop처럼 단순 목록으로, 로컬
`git log`로 읽음), 코드 뷰의 폴더 목록(크기·수정일·생성일, 상대/절대 경로 복사, 탐색기로 열기), 사이드바 우클릭(폴더
열기·경로 복사), 사이드바 스크롤과 접기 화살표. Submission 8. 새 권한·네트워크 흐름은 없지만 GIT LOG가 PC의 `git`을
워크스페이스 폴더에서 실행하므로 개인정보 처리방침 문단에 한 줄을 더하고(`doc-v1.47.0`) 반영을 확인한 뒤 제출했다.
같은 날 GitHub 릴리스 `v0.25.9` — 이번에는 태그 **전에** `ci-0.25.9`로 멀티 OS CI(macOS 포함)가 초록인 것을 먼저
봤다. 패키지 157 MB(149.87 MB), 업로드 약 60초, 첫 Save에 1.25.8.0이 빠지고 1.25.9.0만 Validated, 첫 Submit 클릭에
In certification. Features는 Code view 줄만 고쳤다(`feature-7`, 12줄 그대로). 당일 인증 통과, 게시는 운영자가 눌렀다.

**게시(Publish now)는 운영자가 한다.** 에이전트는 Submit for certification까지 하고 "인증 대기 중"으로 보고를
마친다. 인증 통과 후 운영자가 Partner Center에서 직접 게시하고 알려 준다 — 에이전트가 게시 버튼을 누르지 않는다.

이번에 확인한 것:

- **업로드가 시작되면 `input[type=file]`이 바로 비워진다.** `DOM.setFileInputFiles` 직후 `files[0]`을 읽으면 null이라
  크기 확인이 "실패"로 보였지만, 페이지에는 이미 `149.87 MB · Uploading`이 떠 있었다. 크기는 넣기 전에(빈 입력으로)
  보거나, 업로드 행의 표시 크기로 확인한다.
- **Git Bash에서 `node script.mjs /packages`처럼 `/`로 시작하는 인자는 Git 설치 경로로 바뀐다**(MSYS 경로 변환) —
  탭을 고르는 키는 슬래시 없이 넘긴다.
- 자동화 Edge(`%LOCALAPPDATA%\pw-edge-partner`)는 **9333** 포트로 떠 있었고 세션이 살아 있어 로그인이 필요 없었다.
  9222는 다른 브라우저가 쓰고 있었다.

### 12차 제출 — 여덟 번째 업데이트 (1.26.1.0, 2026-10-23)

내용: 공식 플러그인 **Agent Stage**(워크스페이스는 존, 터미널은 움직이는 에이전트 아바타 — 일하는 중·확인 필요·완료,
클릭하면 그 터미널로)와 이를 위한 브리지 `sessions.list` / `sessions.focus`, 사이드바의 활성 워크스페이스 틸 표시와
`SESSIONS · <워크스페이스>` 헤더. Submission 9. 플러그인이 워크스페이스 목록과 터미널 상태를 읽을 수 있게 됐으므로
개인정보 처리방침에 "What plugins can see" 문단을 더하고(`doc-v1.49.0`) 반영을 확인한 뒤 제출했다. 같은 날 GitHub
릴리스 `v0.26.1`(Linux `.deb`/`.tar.gz` 포함). 그 사이 `v0.26.0`(Linux 첫 릴리스)은 Store 없이 GitHub에만 냈으므로
Store 버전은 1.25.9.0 → 1.26.1.0으로 건너뛴다. 패키지 157 MB, 첫 Save에 1.25.9.0이 빠지고 1.26.1.0만 Validated,
첫 Submit 클릭에 In certification. Features는 Web plugins 줄에 Agent Stage를 더했다(`feature-5`).

이번에 확인한 것:

- **워크트리에서 빌드할 때는 `.secret`이 없다**(git에 올리지 않는 폴더). 원래 체크아웃의
  `.secret/msstore-identity.json`을 읽어 `-IdentityName/-Publisher/-PublisherDisplayName`으로 넘겼다.
- 태그 전 `ci-0.26.1`에서 Ubuntu 작업이 기존부터 가끔 실패하던 타이밍 테스트(`Mode_toast_appears_and_then_clears`,
  약 1초 대기)에 걸렸다. 대기를 5초 시간 기준으로 바꾸고 다시 돌려 세 OS 모두 초록인 것을 본 뒤 제출·태그했다.
  MSIX는 테스트만 바뀐 커밋 이전 것으로, 기능상 같다.

### 13차 제출 — 아홉 번째 업데이트 (1.26.2.0, 2026-10-25)

내용: Agent Stage의 **서브에이전트 크루** — Claude가 Agent / Task 도구로 띄운 서브에이전트를 아바타 위의 크루로(역할별
색·이름, 작업 중 연결선 흐름, 중첩은 바깥으로, 끝나면 부모로 혜성), 호버로 맡은 일·마지막 도구·도구 수·경과 시간.
ZeroCommon `ClaudeSubagentReader`가 Claude Code의 `projects/<slug>/<session>/subagents/`를 이어 읽기로 읽는다.
Submission 10(Partner Center 표기). 플러그인이 서브에이전트의 맡은 일과 마지막 도구 힌트(파일 이름·명령 설명)를 볼 수
있게 됐으므로 개인정보 처리방침 "What plugins can see" 문단을 넓히고(`doc-v1.50.0`) 반영을 확인한 뒤 제출했다.
패키지 157 MB, 첫 Save에 1.26.1.0이 빠지고 1.26.2.0만 Validated, 첫 Submit 클릭에 In certification. Features는
Active-sessions 줄에 크루를 더했다(`feature-6`). 같은 날 GitHub 릴리스 `v0.26.2`(Setup.exe · win-x64 zip ·
Linux .deb/.tar.gz · macOS .pkg, Latest).

이번에 확인한 것:

- 태그 전 `ci-0.26.2`가 **macOS·Ubuntu에서 실패**했다 — 테스트가 아니라 제품 버그였다. `Path.GetFileName`은
  macOS/Linux에서 `\`를 구분자로 보지 않아, Windows에서 쓴 기록의 파일 힌트가 경로째 나왔다. 고친 뒤 Run workflow로
  세 OS 초록을 보고 MSIX도 그 커밋으로 다시 빌드해 제출했다. 멀티 OS CI를 태그 전에 먼저 돌리는 순서가 이번에 값을 했다.
- 개요 페이지의 링크에 `…/listings`가 없을 때가 있다(언어 링크만 `managelanguages`). 제출 ID는 `…/packages` 링크에서 읽는다.
- Bash 도구에서 heredoc으로 쓴 `.mjs`의 `/\\/g`가 `/\/g`로 바뀌어 문법 오류가 났다 — 경로 정리는
  `split(String.fromCharCode(92)).join('/')`로 쓰면 이스케이프를 타지 않는다.

### 14차 제출 (1.26.3.0, 2026-10-26)

내용: Agent Stage가 탭의 에이전트를 이름이 아니라 실제로 돌고 있는 것으로 알아본다 — 에이전트가 남긴 상태 →
탭 셸 아래 프로세스(claude.exe 등) → CLI 정의(claude-sam 같은 프로필 명령 포함) → 탭 이름 순. 한글로 이름을 바꾼
탭, 두 번째 계정 프로필, 손으로 친 claude가 셸 아이콘으로 보이던 문제. 그리고 Claude 상태 파일을 모든 프로필
폴더(`~/.claude`, `~/.claude-<이름>`, `~/.claude-profiles/<이름>`)에서 읽어 두 번째 계정도 working/waiting/done과
crew가 나온다(전에는 화면 규칙으로 떨어져 done이 없었다). Submission 11. 새 권한·네트워크 흐름 없음; 개인정보
처리방침에 프로필 폴더를 명시(`doc-v1.51.0`, md/html 날짜도 맞춤)하고 반영 확인 후 제출. GitHub 릴리스 `v0.26.3`을
먼저 냈다(Setup.exe·zip·linux deb/tar.gz·osx pkg). 패키지 157 MB, 업로드 약 40초, 첫 Save에 1.26.3.0만 Validated,
첫 Submit 클릭에 In certification.

### 15차 제출 (1.26.4.0, 2026-10-26)

내용: Hermes Agent(Nous Research)를 기본 에이전트로 채택 — 기본 CLI 정의(`hermes`), 미설치 시 공식 설치 스크립트,
`runtime/active_sessions.json` + `state.db`로 working/done 추적, 승인 박스는 화면 규칙으로 blocked. Agent Stage 0.3.0(공식
플러그인, main 반영): Hermes 데스크톱 아이콘 아바타(MIT, `img/NOTICE.md`), 라벨에 에이전트 이름(`Hermes · PW5`), Codex
(`spawn_agent`)·Hermes(`delegate_task`) 하위 에이전트 crew. Codex 0.164에서 작업 중인 탭이 idle로 보이던 두 버그 수정(64 KB
꼬리 읽기 → 이어 읽기, 빈 rollout 캐시). Submission 12. 새 권한 없음; 개인정보 처리방침에 Hermes 폴더(읽기 전용)와 설치
스크립트 접속처를 추가(`doc-v1.52.0`, 반영 확인) 후 제출. GitHub 릴리스 `v0.26.4`를 먼저 냈다(Setup.exe·zip·linux
deb/tar.gz·osx pkg). 패키지 157 MB, 업로드 약 30초 + 분석 약 20초, Save에 1.26.4.0만 Validated, 리스팅 What's new와
기능 두 줄(Hermes) 갱신, 첫 Submit 클릭에 In certification.

### 16차 제출 (1.26.5.0, 2026-10-29)

내용: 에이전트가 나를 기다릴 때 알림(화면 모서리 팝업 + 작업 표시줄 깜빡임, Settings → Notifications), Budget(이번 달
에이전트 비용 — 모델·프로젝트별, 월 한도, 상태 표시줄), DIFF 탭(작업 트리 변경 검토 — 줄 메모를 터미널/어시스턴트로 보내기,
AI 모드가 쓴 파일 유지·되돌리기), 명령 팔레트(Ctrl+Shift+P)와 Settings → Shortcuts. Submission 13. GitHub 릴리스
`v0.26.5`(10/28, `ci-0.26.5` 선행)와 개인정보 처리방침(`doc-v1.53.0` — Budget의 토큰 수, DIFF의 git)은 먼저 나가 있었고,
Partner Center에는 10/26에 초안만 열린 채 패키지·목록이 비어 있었다 — 그 초안에 이어서 제출했다. 패키지 157 MB, 업로드
+분석 약 75초, Save에 1.26.5.0만 Validated, 첫 Submit 클릭에 In certification.

이번에 확인한 것:

- **목록의 Features 칸은 지난번에 채운 수만큼만 보인다.** 12칸 화면에 13줄을 넣으면 13번째가 조용히 빠진다 — "Add more"로
  칸을 늘린 뒤 채우고, 저장 후 다시 읽어 `listing.md`와 줄 단위로 대조했다. 목록 스크립트는 이제 `listing.md`에서
  What's new와 Features를 직접 읽는다(손으로 옮겨 적지 않는다).
- 세션이 며칠 지나면 Partner Center 로그인이 풀린다(`pw-edge-partner` 프로필, 9333). 운영자가 다시 로그인했다.
- "게시본"은 개요의 Store presence 줄(Submission 번호)로, 진행 중인 것은 Product update 줄로 본다 — 초안만 열려
  있고 패키지가 Unchanged면 아직 아무것도 제출되지 않은 것이다.

### 17차 제출 (1.26.7.0, 2026-10-30)

내용: 스트리밍 음성 인식 — AgentBot 입력창의 🎙(말하는 동안 글자가 나타나고 멈추면 전송), Settings → Voice의 스트리밍
공급자(NVIDIA Nemotron 3.5 ASR이 공식 모델, 한국어 Zipformer는 대안)·Download·Listen live, KittenTTS 2(다국어 온디바이스
음성), 음성 모델은 처음 쓸 때 올라가 계속 유지. sherpa-onnx 1.13.8로 올라 패키지의 `onnxruntime.dll`이 1.28.2다(로컬에서
Supertonic·KittenTTS 2·화자 분리 재확인). Partner Center는 Submission 14. 개인정보 처리방침을 먼저 고쳤다 — 마이크가
"플러그인에서만"이 아니게 됐다(`doc-v1.54.0`, Pages 배포 확인). GitHub 릴리스 `v0.26.7` 선행(`ci-0.26.7`이 macOS·Linux에서
KittenTTS 2 테스트 하나로 실패 → 테스트가 Windows 문구만 기대하던 것을 고쳐 `ci-0.26.7-2` 통과 후 태그). 패키지 152 MB,
업로드+분석 약 45초, Save에 1.26.7.0만 Validated, 목록 What's new·기능 14줄을 `listing.md`에서 채우고 다시 읽어 일치 확인,
첫 Submit 클릭에 In certification. 로그인은 이번에도 풀려 있어 운영자가 다시 로그인했다(Edge `pw-edge-partner`, 9333 —
`msedge --remote-debugging-port=9333 --user-data-dir=%LOCALAPPDATA%\pw-edge-partner`로 띄운다).

## 참고

- 개인 개발자 무료 등록: <https://learn.microsoft.com/en-us/windows/apps/publish/whats-new-individual-developer>
- 회사 계정 무료화(2026-05): <https://blogs.windows.com/windowsdeveloper/2026/05/07/publish-to-microsoft-store-as-a-company-now-with-free-registration-and-faster-onboarding/>
- MSIX 패키지 요구사항(버전 규칙, Store 재서명): <https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/app-package-requirements>
- EXE/MSI 요구사항: <https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msi/app-package-requirements>
- Flexible virtualization(MSIX의 AppData 격리): <https://learn.microsoft.com/en-us/windows/msix/desktop/flexible-virtualization>
- Desktop app 프로세스 정책(`PROC_THREAD_ATTRIBUTE_DESKTOP_APP_POLICY`): <https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-updateprocthreadattribute>
- 코드 서명 옵션: <https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options>
