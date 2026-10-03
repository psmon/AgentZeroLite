# AgentZero Lite (Avalonia) — Microsoft Store 등록 가이드

Avalonia 호스트(`Project/AgentZeroAvalonia`)의 Windows 빌드를 Microsoft Store에 올리는 절차와 설계.
**현재 기준(2차 제출, 2026-10-02~)** 으로 쓰여 있고, 1차 제출과 반려의 기록은 §6에 있다.

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
   겹치면 멈추므로, 교체할 때는 버전을 올리고 직접 끌어다 놓는 것이 안전하다.
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

## 참고

- 개인 개발자 무료 등록: <https://learn.microsoft.com/en-us/windows/apps/publish/whats-new-individual-developer>
- 회사 계정 무료화(2026-05): <https://blogs.windows.com/windowsdeveloper/2026/05/07/publish-to-microsoft-store-as-a-company-now-with-free-registration-and-faster-onboarding/>
- MSIX 패키지 요구사항(버전 규칙, Store 재서명): <https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/app-package-requirements>
- EXE/MSI 요구사항: <https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msi/app-package-requirements>
- Flexible virtualization(MSIX의 AppData 격리): <https://learn.microsoft.com/en-us/windows/msix/desktop/flexible-virtualization>
- Desktop app 프로세스 정책(`PROC_THREAD_ATTRIBUTE_DESKTOP_APP_POLICY`): <https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-updateprocthreadattribute>
- 코드 서명 옵션: <https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options>
