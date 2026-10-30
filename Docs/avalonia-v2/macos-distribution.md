# AgentZero Lite (Avalonia) — macOS 배포 가이드 (Developer ID · Mac App Store)

Avalonia 호스트의 macOS(Apple Silicon) 빌드를 서명·공증해 배포하고, Mac App Store 등록 가능성을 따지는 절차와 기록.
Windows 쪽 짝은 `microsoft-store.md`. 서명 일반 지식은 `harness/knowledge/_shared/code-signing.md` §7.

---

## 0. 설계: 두 갈래 — Developer ID 먼저, App Store는 실측 뒤

| 경로 | 무엇 | 기능 | 상태 |
|---|---|---|---|
| **Developer ID 서명 + 공증** | 지금의 `.pkg`를 Apple 인증서로 서명하고 Apple 공증 티켓을 붙인다. 홈페이지·GitHub Releases 배포 | 제한 없음 | **1단계 — 진행 중** |
| **Mac App Store** | App Sandbox 안에서 도는 빌드를 App Store Connect에 제출 | 고른 폴더 + Homebrew(§4.5) | 2단계 — 1차 반려(10-28) → **0.26.6 재제출(2026-10-30)** |

Mac App Store는 **App Sandbox가 필수**이고, 앱이 띄운 **자식 프로세스는 부모의 샌드박스를 그대로 물려받는다.**
Windows MSIX의 desktop app breakaway(`DesktopAppBreakaway.cs`) 같은 탈출구가 macOS에는 없다. Apple DTS 답변(포럼
thread 685544): "A child process always inherits its sandbox from its parent", "App Review requires that all code within
your app be sandboxed … you can't include a non-sandboxed helper app". 같은 스레드의 실측: 샌드박스 앱의 `forkpty` +
zsh는 `can't set tty pgrp: operation not permitted`, `ls /Users`는 `Operation not permitted`.

AgentZero에 대입하면 터미널 탭이 사용자 프로젝트 폴더, `/opt/homebrew`의 node·claude·codex, `~/.claude`에 닿지
못하고 `npm install -g`도 막힌다(가이드라인 2.4.5도 추가 코드 설치를 금지). 그래서 기능 그대로의 배포는
Developer ID로 하고(iTerm2·Warp·Ghostty와 같은 방식), App Store판은 샌드박스 빌드를 실측해 성립 여부부터 판단한다.

---

## 1. 사전 준비 — 사용자가 할 일

1. **Apple Developer Program 가입** — <https://developer.apple.com/programs/enroll/> (개인, $99/년).
   2단계 인증이 켜진 Apple ID 필요. 승인까지 보통 수 시간~48시간. 두 경로 모두 이것이 전제.
2. **App Store Connect API 키** (가입 승인 후) — App Store Connect → 사용자 및 액세스 → 통합 → **팀 키** →
   생성, 역할 **Admin**. `AuthKey_<KEYID>.p8`(한 번만 내려받을 수 있음), Key ID, Issuer ID 세 값을
   `.secret/`(git 제외)에 둔다. `asc`와 공증(`notarytool --key`)이 이 키 하나로 동작한다.
3. **Developer ID Installer 인증서** — `.pkg` 서명용. `asc`/API가 이 유형을 만들지 못하므로 웹에서 1회:
   developer.apple.com → Certificates → **+** → *Developer ID Installer* → CSR 업로드(CSR은 저장소 쪽에서 만들어 줌).

---

## 2. 도구 (CLI, 웹 전용 아님)

| 도구 | 설치 | 용도 |
|---|---|---|
| `asc` 5.16.0 (App Store Connect CLI, Rork) | `brew install asc` | 인증서·번들 ID·프로파일, 앱 레코드(`asc web apps create`), `.pkg` 업로드, 메타데이터·스크린샷, 심사 제출, 공증(`asc notarization`) |
| `xcrun notarytool` / `stapler` | Command Line Tools에 포함(이 맥에 있음) | 공증 제출·티켓 스테이플 |
| `codesign`, `pkgbuild`, `productbuild` | Command Line Tools | 서명·패키징 |

Xcode는 필요 없다. `xcrun altool`은 Xcode에만 들어 있지만 업로드는 `asc builds upload --pkg`가 대신한다.
인증: `asc auth login --name agentzero --key-id <KEYID> --issuer-id <ISSUER> --private-key .secret/AuthKey_<KEYID>.p8`
→ `asc auth status --validate`.

이 맥의 함정: PATH의 `dotnet`은 `/usr/local/share/dotnet`(9.0만)이라 `global.json`(10.0)을 못 찾는다.
.NET 10 SDK는 `~/.dotnet`에 있으므로 `export DOTNET_ROOT=$HOME/.dotnet PATH=$HOME/.dotnet:$PATH`.

---

## 3. 빌드 → 서명 → 공증 (Developer ID)

```sh
# 0) 게시 + 자체 점검 (CI release.yml의 macos 잡과 같은 명령)
dotnet publish Project/AgentZeroAvalonia/AgentZeroAvalonia.csproj -c Release -r osx-arm64 \
  --self-contained true -p:UseAppHost=true -o publish-avalonia/osx-arm64
publish-avalonia/osx-arm64/AgentZeroLite -cli selftest all
v=$(cat publish-avalonia/osx-arm64/version.txt)

# 1) .app — SIGN_ID가 있으면 Developer ID 서명(중첩 Mach-O 먼저, 하드닝 런타임 + 타임스탬프), 없으면 ad-hoc
SIGN_ID="Developer ID Application: <이름> (<TEAMID>)" \
  sh Project/AgentZeroAvalonia/macos/build-app.sh publish-avalonia/osx-arm64 publish-avalonia/AgentZeroLite.app "$v"

# 2) .pkg — PKG_SIGN_ID로 Developer ID Installer 서명
PKG_SIGN_ID="Developer ID Installer: <이름> (<TEAMID>)" \
  sh Project/AgentZeroAvalonia/macos/build-pkg.sh publish-avalonia/AgentZeroLite.app \
  publish-avalonia/AgentZeroLite-$v-osx-arm64.pkg "$v"

# 3) 공증 + 스테이플 — .pkg 하나만 제출하면 안의 앱까지 덮는다
NOTARY_KEY=.secret/AuthKey_<KEYID>.p8 NOTARY_KEY_ID=<KEYID> NOTARY_ISSUER=<ISSUER> \
  sh Project/AgentZeroAvalonia/macos/notarize.sh publish-avalonia/AgentZeroLite-$v-osx-arm64.pkg
```

`build-app.sh`의 서명 모드는 `--deep`을 쓰지 않는다. `--deep`은 메인 실행 파일의 엔타이틀먼트를 모든 dylib에
붙이고, Apple은 공증용 서명에 권장하지 않는다. 엔타이틀먼트(`macos/entitlements.plist`)는 번들(메인 실행 파일)에만.
`notarytool submit --wait`는 결과가 Invalid여도 0으로 끝나므로, 스크립트는 `stapler staple` 실패를 판정으로 삼는다.

### 로컬 검증 결과

| 날짜 | 항목 | 결과 |
|---|---|---|
| 2026-10-21 | `dotnet publish -r osx-arm64` (SDK 10.0.402, Mac mini, Darwin 24.6) | ✅ |
| 2026-10-21 | `-cli selftest all` | ✅ ipc · secrets(AesGcm) · pty(Porta.Pty forkpty) |
| 2026-10-21 | `build-app.sh` ad-hoc 경로 (0.25.8) | ✅ `codesign --verify --deep --strict` 통과 |
| 2026-10-21 | `build-app.sh` Developer ID 경로 (375개 파일 개별 서명) | ✅ `valid on disk`, `satisfies its Designated Requirement` |
| 2026-10-21 | `build-pkg.sh` + `PKG_SIGN_ID` | ✅ Developer ID Installer, 신뢰 타임스탬프 |
| 2026-10-21 | `notarize.sh` (API 키) — submission `5094d9a2-a4d6-404e-ad73-3237cdaa5151` | ✅ **Accepted**, issues 없음, 스테이플 OK, `spctl`: `source=Notarized Developer ID` |
| 2026-10-21 | **CI** `release.yml` 수동 실행(`publish=false`, run 39561626354, macos-14) | ✅ 서명 → .pkg → 공증 Accepted → 스테이플. 내려받은 .pkg가 이 맥에서 `spctl` accepted |

### CI (`.github/workflows/release.yml`)

GitHub Secrets: `MACOS_DEVID_APP_P12`·`MACOS_DEVID_INSTALLER_P12`(base64), `MACOS_P12_PASSWORD`, `ASC_KEY_P8`·`ASC_KEY_ID`·
`ASC_ISSUER_ID`, `APPLE_TEAM_ID`. 있으면 macos 잡이 임시 키체인에 두 ID를 넣고 서명 → `AgentZeroLite-v<ver>-osx-arm64.pkg` →
공증 → 릴리스에 첨부. 없으면(포크 등) ad-hoc `.app` zip만 워크플로 결과물로. release 잡은 Windows만 성공하면 진행한다.
`.github/workflows/` 수정을 푸시하려면 gh 토큰에 `workflow` 권한이 필요하다(`gh auth refresh -h github.com -s workflow`).

### 서명 환경 (이 맥에서 실제로 쓴 구성)

- 팀: **sang man park (개인), Team ID `HA39FQRUZG`**. 계정에 팀이 여럿이므로 웹에서 팀 선택을 확인한다.
- **Developer ID 인증서는 계정 소유자만 발급할 수 있다** — `asc certificates create --certificate-type DEVELOPER_ID_APPLICATION_G2`가
  "This operation can only be performed by the Account Holder"로 거부(Admin API 키). 그래서 웹(Certificates → +)에서
  CSR을 올려 발급했다. 개인 키는 `openssl`로 이 맥에서만 만들었다. 두 인증서 모두 G2 Sub-CA, 만료 2027-11-20.
- **서명 전용 키체인** `~/Library/Keychains/agentzero-signing.keychain-db` — `.p12` 두 개 + `DeveloperIDG2CA.cer`를 넣고
  `set-key-partition-list -S apple-tool:,apple:,codesign:`. 로그인 키체인에 넣었을 때는 codesign이 키 사용 확인 창을
  띄우고 멈췄다(SSH 세션에서는 누를 수 없음). CI도 같은 구성(임시 키체인 + p12)을 쓴다.
  재부팅 뒤에는 잠기므로 `security unlock-keychain -p "$(cat .secret/certs/signing-keychain-password.txt)" ~/Library/Keychains/agentzero-signing.keychain-db`.
- 비밀 위치(`.secret/`, git 제외): `AuthKey_6FJ8Q8Y7AA.p8`, `apple-asc.json`(Key ID·Issuer ID·Team ID), `certs/`(키·CSR·cer·p12,
  `p12-password.txt`, `signing-keychain-password.txt`). `asc`는 `agentzero` 프로필로 시스템 키체인에 로그인돼 있다.
- Playwright: 패스키 로그인은 Playwright 번들 Chromium에서 USB 키만 제시된다(휴대폰 QR 없음). `channel: 'chrome'` +
  별도 프로필 디렉터리로 띄운 정식 Chrome에서는 QR 패스키 로그인이 됐다.

---

## 4. Mac App Store (2단계)

### 4.1 샌드박스 실측 (2026-10-21, Mac mini, macOS Darwin 24.6, ad-hoc 서명)

방법: (a) 0.25.8 `.app` 사본에 번들 ID `…sandboxprobe`를 주고 기존 엔타이틀먼트 + `app-sandbox` +
`files.user-selected.read-write`로 서명해 `-cli selftest all`과 GUI 실행. (b) 같은 엔타이틀먼트로 서명한 작은 번들
(`posix_spawn("/bin/zsh","-c",…)`) — 터미널 탭의 셸과 똑같이 **샌드박스를 상속한 zsh**가 각 명령을 실행.
같은 명령을 샌드박스 없이 돌린 대조군은 모두 성공했다.

| 터미널 탭에서 하는 일 | 샌드박스 안 결과 |
|---|---|
| `$HOME` | `~/Library/Containers/<번들ID>/Data`로 바뀜 |
| 사용자 프로젝트 폴더 읽기·쓰기 (`~/code/…`) | ❌ `Operation not permitted` |
| `~/.claude`, `~/Documents`, 기존 `~/Library/Application Support/AgentZeroLite` | ❌ `Operation not permitted` |
| `/opt/homebrew/bin/node`, `claude`, `codex`, `npm` 실행 | ❌ `operation not permitted` (exec 자체가 거부) |
| `npm -g` 경로(`/opt/homebrew/lib/node_modules`)에 쓰기 | ❌ |
| `git` (`/usr/bin/git`, 컨테이너 안 저장소 포함) | ❌ `xcrun: error: cannot be used within an App Sandbox.` |
| `ps` | ❌ `operation not permitted` |
| `zsh -i`, `/usr/bin/ssh -V`, `curl https://…` | ✅ |
| 앱: `selftest pty`(forkpty + `/bin/sh`), `selftest secrets` | ✅ |
| 앱: `selftest ipc` (`-cli`가 쓰는 named pipe) | ❌ no reply over the pipe |
| 앱: GUI 기동 | ❌ 270 ms 만에 SIGABRT — `Sandbox: deny(1) mach-lookup com.apple.coreservices.launchservicesd`, `[NSApplication sharedApplication]` → `_RegisterApplication` → `abort` (메인 스레드). 같은 엔타이틀먼트의 최소 AppKit 앱은 정상 기동하므로 AgentZero 쪽 원인. 하드닝 런타임 유무·중첩 서명 방식을 바꿔도 같음 |

결론(1차 실측): 예외 엔타이틀먼트 없이는 터미널이 컨테이너 안의 시스템 셸과 ssh만 쓸 수 있고, GUI도 기동하지 못했다.
아래 4.2–4.4에서 둘 다 해결했다.

### 4.2 해결 — GUI 기동, `-cli`, 탭의 HOME (2026-10-21)

| 문제 | 원인(실측) | 해결 |
|---|---|---|
| GUI 270 ms 만에 abort | App Sandbox 프로파일(`application.sb`)에는 `launchservicesd` 조회 규칙이 없다. AppKit을 **실행 시점에 링크**한 앱은 그 전에 연결이 맺어져 정상이고, **나중에 dlopen**하면 `_RegisterApplication`이 abort. 두 줄짜리 프로브로 양쪽 모두 확인. .NET apphost는 AppKit을 링크하지 않는다 | `macos/launcher/` — AppKit을 링크한 네이티브 런처가 apphost를 대신해 `hostfxr_main_startupinfo`(apphost와 같은 호출) |
| `-cli` 연결 불가 | 컨테이너 tmp 아래 소켓 경로가 Unix 소켓 한도 104바이트 초과 → accept가 200 ms마다 실패 | `CliIpcBridge.ResolvePipeName` — 샌드박스면 컨테이너 홈의 절대 경로(`~/.AgentZeroLite.cli`) |
| 탭의 `HOME`이 컨테이너 | 시스템이 샌드박스 앱의 HOME을 컨테이너로 바꾼다 → `~/.zshrc`·`~/.claude`를 못 봄 | `TerminalEnvironment.BuildPosix` — 탭 환경에서 실제 홈으로 되돌림(테스트 포함) |
| 앱 메뉴가 "Avalonia Application" | `Application.Name` 미설정 | `App.axaml`에 `Name="AgentZero Lite"` |

### 4.3 임시 예외 엔타이틀먼트 실측 (`macos/appstore/entitlements.plist`)

`temporary-exception.files.home-relative-path.read-write ["/"]` + `…absolute-path.read-write ["/opt/homebrew/", "/usr/local/"]`:

| 항목 | 결과 |
|---|---|
| 사용자 폴더 읽기·쓰기, `~/.claude`, Claude 키체인 항목 | ✅ |
| node·claude·codex `--version`, `npm -g` 경로 쓰기 | ✅ |
| `/usr/bin/git` (Xcode CLT 심) | ❌ `xcrun: error: cannot be used within an App Sandbox.` → Homebrew git 필요(설명문에 안내) |
| `ps` | ❌ → 에이전트 상태 감지 일부 제한 |
| zsh job control | ❌ `can't set tty pgrp` (tty ioctl 거부). 입력·명령 실행은 정상 |
| claude·codex **대화형 실행** | ❌ claude: `EPERM open '/tmp/claude-501'`(`/private/tmp` 거부), codex: `Operation not permitted (os error 1)`. **사용자 쪽 문제로 둔다** — AgentZero는 동작하는 터미널을 띄우는 역할(운영자 결정, 2026-10-21) |

### 4.4 등록·제출 기록

- App Store Connect: 번들 ID `com.psmon.agentzerolite`(UL4SVKU9LR), 앱 ID **6825237056**, SKU `agentzerolite-mac`, 기본 언어 en-US.
- 인증서(API로 발급 가능 — Developer ID와 달리 계정 소유자 제한 없음): Apple Distribution(N533973YS8),
  Mac Installer Distribution(CU9T288D7T, 키체인 이름 "3rd Party Mac Developer Installer"), 프로파일 MAC_APP_STORE(2J2SZTHSPZ). 모두 2027-10-21 만료.
- 빌드: `MAS_SIGN_ID=… MAS_PKG_ID=… MAS_PROFILE=… sh macos/appstore/build-appstore.sh <publish> <out> <ver> <build>`
  (`MAS_SANDBOX_TEST=1`이면 ad-hoc 로컬 실행용). 업로드: `asc builds upload --app 6825237056 --pkg … --version … --build-number …`.
  - 빌드 1: **ITMS-90296** — `createdump`에 샌드박스 엔타이틀먼트 없음 → 스토어판에서 제거, 보조 실행 파일은 `inherit.entitlements`.
  - 빌드 2: **VALID**, 암호화 exempt(`ITSAppUsesNonExemptEncryption=false`).
- 메타데이터(`macos/appstore/listing.md`가 원본, asc로 반영): 부제·설명·키워드·프로모션 문구·지원/마케팅 URL·저작권,
  카테고리 개발자 도구/유틸리티, 연령 4+(전부 없음), 무료·전 지역, 수동 출시(MANUAL), 심사 연락처·메모(예외 엔타이틀먼트 사유 포함).
- 웹(Playwright)으로만 된 것: 앱 개인정보 **데이터 수집 안 함** 게시, 콘텐츠 권한 "타사 콘텐츠 없음", EU DSA **비거래자**.
  대한민국 연락처 확인(이메일, 사업자등록번호 없음)은 Apple 쪽 "문제가 발생했습니다"로 두 번 실패 — 웹에서 재시도 필요.
- 스크린샷 3장(`macos/appstore/screenshots/`, 1440×900): 샌드박스 테스트 빌드 창(1180×788)을 캡처해 1440×900 캔버스에 배치,
  알파 채널 제거(업로드가 알파 PNG를 거부). 이 앱은 창 크기를 저장하지 않는다.
- **2026-10-21 17:46 KST 심사 제출** — submission `38e8bb1c-45f0-4cee-b073-0e7f9089f54a`, 상태 `WAITING_FOR_REVIEW`.

### 4.5 1차 반려와 대응 (2026-10-28 반려 → 2026-10-30 재제출)

반려 원문 요지(심사 기기 MacBook Pro 14-inch Nov 2024, 0.25.8 (2)):
- **2.4.5(i)**: `temporary-exception.files.absolute-path.read-write` 값 `/usr/local/`, `temporary-exception.files.home-relative-path.read-write`
  값 `/`는 부여하지 않음. `/opt/homebrew/`는 지적되지 않았다.
- **5 Legal**: 메타데이터의 "OpenAI" — 중국 생성형 AI 규제. 중국 판매를 빼거나 언급·기능을 없애라.

대응(0.26.6, 빌드 3, 커밋 `9a60255`):
- 두 예외 삭제. 워크스페이스는 폴더 선택 창으로만 추가하고 **app-scoped security-scoped bookmark**로 유지
  (`ZeroCommon/Platform/SandboxFolderAccess.cs`: 선택 시 `Remember`, 시작 시 `RestoreAll` →
  `startAccessingSecurityScopedResource`를 프로세스 수명 동안 유지. Avalonia의 북마크 API는 자기 호출 동안만 범위를 연다).
  실측: 샌드박스 프로브(Swift, NSOpenPanel) → 선택한 폴더와 재시작 후 북마크로 연 폴더 모두 자식 zsh가 읽기·쓰기 ✅,
  이어 샌드박스 테스트 빌드에서 같은 결과(`reopened 1/1`, 재시작 후 탭에서 쓰기 ✅).
- 탭의 HOME은 실제 홈을 읽을 수 있을 때만 실제 홈, 아니면 컨테이너. 컨테이너일 때 `/opt/homebrew/bin`·`sbin`을 PATH 앞에.
  결과: 탭에서 `node`·`claude`를 찾는다. 홈 폴더에 설정을 두는 도구(예: `~/.claude`)는 이 판에서 그 설정을 못 읽는다(설명문에 명시).
- 설명문에서 "OpenAI" 삭제, App Store Connect 판매 지역에서 **중국 본토 제외**.
- Resolution Center 답변 + 심사 메모 갱신 후 재제출. 재제출 절차의 함정은 런북(`harness/knowledge/_shared/mac-app-store-review.md` §4).

---

## 5. 기록

- 2026-10-21 — 경로 결정(Developer ID 먼저, App Store는 실측 후). `asc` 5.16.0 설치. `build-app.sh`에 Developer ID
  서명 모드(`SIGN_ID`), `notarize.sh` 추가. 로컬 게시·selftest·ad-hoc 번들 확인.
- 2026-10-21 — API 키(Admin) 발급·`asc` 로그인, Developer ID Application/Installer 인증서 발급(웹), 서명 전용 키체인 구성.
  0.25.8 `.pkg` 서명 → **공증 Accepted** → 스테이플. `build-app.sh`는 Mach-O만이 아니라 `Contents/MacOS`의 모든 파일을 서명하도록 수정.
- 2026-10-21 — 샌드박스 실측(§4.1): 터미널 핵심 기능·`-cli`·GUI 기동이 모두 막힘. App Store판은 기능을 줄인
  별도 구성으로만 성립하고, 그 전에 GUI 크래시 해결이 필요. 실측용 번들·컨테이너는 정리함.

## 참고

- Apple DTS, 샌드박스 앱의 forkpty와 자식 프로세스 상속: https://developer.apple.com/forums/thread/685544
- App Store Connect CLI (`asc`): https://github.com/rorkai/App-Store-Connect-CLI
- 업로드 도구 안내(altool/Transporter): https://developer.apple.com/help/app-store-connect/manage-builds/upload-builds
