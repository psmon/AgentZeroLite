# AgentZero Lite (Avalonia) — Microsoft Store 등록 가이드

Avalonia 호스트(`Project/AgentZeroAvalonia`)의 Windows 빌드를 Microsoft Store에 올리는 절차.
저장소 쪽 셋업은 끝나 있다(§2). 남은 것은 **Partner Center 계정에서만 할 수 있는 일**(§1, §4)과
**관리자 권한·개발자 모드가 필요한 로컬 검증**(§3)이다.

> 2026-09 기준으로 확인한 사실: 개인·회사 개발자 계정 모두 등록비 무료(정부 신분증 + 셀피 인증),
> MSIX 제출은 Store가 Microsoft 인증서로 다시 서명하므로 코드 서명 인증서가 필요 없음,
> EXE/MSI 제출은 직접 산 Authenticode 인증서로 서명해야 함(자체 서명 불가).

---

## 0. 결정: MSIX로 제출한다 (EXE/MSI 아님)

| | MSIX (선택) | EXE/MSI (Inno Setup 설치 파일) |
|---|---|---|
| 서명 | Store가 무료로 서명 | 유료 코드 서명 인증서 필요 (자체 서명 거부) |
| 호스팅·업데이트 | Store CDN, 자동 업데이트 | 우리 URL에 설치 파일을 올리고 앱이 직접 업데이트 |
| 대가 | AppData 가상화를 끄는 **제한된 기능**(`unvirtualizedResources`) 승인 필요 | 없음 |

MSIX를 고른 이유는 비용과 업데이트다. 그러나 MSIX는 기본적으로 앱과 **앱이 띄운 모든 자식 프로세스**의
`%LOCALAPPDATA%`·`%APPDATA%`·`HKCU` 쓰기를 패키지 전용 저장소로 돌려버리는데, AgentZero에게는 치명적이다.

- `agentZeroLite.db`와 설정 JSON이 패키지 안으로 숨어 **WPF 호스트·`-cli` 호출자와 공유가 끊긴다.**
- 터미널 탭에서 실행한 `npm install -g`, `claude`, `codex`의 캐시·설정이 패키지 캐시에 떨어져
  **사용자의 다른 터미널에서 안 보이고, 앱을 지우면 같이 사라진다.**
  (같은 문제가 실제로 보고됨: anthropics/claude-code#93152 — MSIX 데스크톱 앱 안의 세션이 설치한 도구가
  패키지 LocalCache에 갇힘.)

그래서 매니페스트가 `unvirtualizedResources` + `FileSystemWriteVirtualization=disabled` +
`RegistryWriteVirtualization=disabled`를 선언한다. 개발자 도구 MSIX(터미널류)가 흔히 쓰는 설정이지만
**제출할 때마다 사유를 적어 승인을 받아야 한다**(§4-5에 붙여 넣을 문구 준비됨). 승인이 거절되면 EXE/MSI
경로로 바꾸는 것이 대안이고, 그때는 코드 서명 인증서(예: Azure Artifact Signing, 구 Trusted Signing)
비용을 검토해야 한다.

---

## 1. 사전 준비 — 사용자가 할 일

1. **Partner Center 개발자 계정** — <https://storedeveloper.microsoft.com> 에서 개인(또는 회사) 계정 가입.
   무료. 신분증 + 셀피 인증을 마치면 바로 Partner Center에 들어갈 수 있다.
2. **앱 이름 예약** — Partner Center → Apps and games → New product → **MSIX or PWA app** →
   이름 `AgentZero Lite` 예약. (이미 쓰인 이름이면 `AgentZero Lite for Windows` 등으로.)
3. **패키지 ID 값 복사** — 예약한 앱 → Product management → **Product identity** 에서 세 값:
   - `Package/Identity/Name` (예: `12345Webnori.AgentZeroLite`)
   - `Package/Identity/Publisher` (예: `CN=ABCD1234-…`)
   - `Package/Properties/PublisherDisplayName`
   대소문자·공백까지 그대로 써야 한다(§3의 빌드 인자로 들어감).
4. **개인정보 처리방침 URL** — 앱이 인터넷에 연결하므로 필수. 초안: `Docs/privacy-policy.md`
   (내용 확인 후 GitHub에서 공개 URL로 쓰거나 GitHub Pages에 올린다).
5. **로컬 검증 환경**(§3) — Windows SDK(설치됨: 10.0.26100), **설정 → 시스템 → 개발자용 → 개발자 모드 켜기**,
   WACK 실행용 관리자 PowerShell.

---

## 2. 저장소 셋업 — 완료된 것

| 파일 | 역할 |
|---|---|
| `Project/AgentZeroAvalonia/msix/Package.appxmanifest` | MSIX 매니페스트 템플릿. `runFullTrust`, `unvirtualizedResources`(+ 가상화 해제 두 줄), 실행 별칭 `AgentZeroLite.exe`(아무 셸에서 `-cli` 사용), 최소 Windows 10 19041 |
| `Project/AgentZeroAvalonia/msix/Assets/*.png` | 타일·작업 표시줄 아이콘(44/150/310×150/StoreLogo 50, targetsize 24/48/256). `agentzero.ico`에서 생성 |
| `Project/AgentZeroAvalonia/msix/build-msix.ps1` | publish(win-x64 self-contained) → 스테이징 → ID 채우기 → `makepri` → `makeappx pack`. `-Register`(개발자 모드 제자리 등록), `-Sign`(테스트 인증서 서명) |
| `Project/AgentZeroAvalonia/msix/store-listing/` | 스토어 등록 문구 초안(`listing.md`), 300×300 로고 |
| `Docs/privacy-policy.md` | 개인정보 처리방침 초안 |
| `.gitignore` | `/publish-msix/` (빌드 산출물) |

**버전 규칙**: Store는 4자리 버전에서 첫 자리 0 금지, 마지막 자리 0 고정. 앱이 `0.25.0`이라
MSIX 버전은 **메이저 + 1**: `0.25.0 → 1.25.0.0`, 나중의 `1.0.0 → 2.0.0.0`. 단조 증가만 지키면 된다
(Store는 항상 가장 높은 버전을 배포). 다른 번호가 필요하면 `-Version 1.25.0.0` 으로 직접 지정.

**확인한 것**: 이 PC에서 dev ID로 빌드 성공 — `publish-msix/AgentZeroLite_1.25.0.0_x64.msix`, 111.8 MB.
`makeappx`가 매니페스트 스키마를 검증했다. 개발자 모드 등록 후의 실측은 §3의 "로컬 검증 결과"에. WACK는 아직(관리자 필요).

---

## 3. 빌드와 로컬 검증

```powershell
cd Project\AgentZeroAvalonia\msix

# (a) 개발자 모드에서 제자리 등록 — 서명 없이 패키지 상태로 실행해 본다
./build-msix.ps1 -Register
#   시작 메뉴 "AgentZero Lite" 실행, 다른 터미널에서:
#   AgentZeroLite.exe -cli status
#   되돌리기: Get-AppxPackage -Name AgentZeroLite.Dev | Remove-AppxPackage

# (b) Store 업로드용 — Partner Center의 값으로
./build-msix.ps1 -IdentityName "<Identity Name>" -Publisher "<CN=…>" -PublisherDisplayName "<표시 이름>"
#   → publish-msix\AgentZeroLite_<버전>_x64.msix  (이 파일을 업로드. 서명 불필요)

# (c) WACK — 관리자 PowerShell에서 (Store 인증과 같은 검사)
& "C:\Program Files (x86)\Windows Kits\10\App Certification Kit\appcert.exe" test `
  -appxpackagepath publish-msix\AgentZeroLite_<버전>_x64.msix -reportoutputpath publish-msix\wack.xml
```

패키지 상태에서 꼭 확인할 것 (§0의 가상화 해제가 실제로 먹는지):

- [ ] 앱 실행 후 `%LOCALAPPDATA%\AgentZeroLite\agentZeroLite.db` 가 **실제 경로에** 생기고 갱신되는가
      (`%LOCALAPPDATA%\Packages\<패키지>\LocalCache` 쪽에 새로 생기면 가상화가 안 꺼진 것)
- [ ] WPF 호스트(설치형)에서 만든 워크스페이스가 Store 판에서도 보이는가
- [ ] 터미널 탭에서 `npm install -g <아무 패키지>` 후 **앱 밖의** 터미널에서 그 명령이 보이는가
- [ ] 다른 셸에서 `AgentZeroLite.exe -cli status` 가 동작하는가(실행 별칭)
- [ ] Settings → CLI Definitions → Claude/Codex/AgentOne 설치 확인·npm 설치가 동작하는가
- [ ] 터미널 탭(xterm.js/WebView2)이 뜨는가 — WebView2 런타임 필요(§5)

### 로컬 검증 결과 (2026-09-28, Windows 11 26200, `-Register` 개발자 모드)

| 점검 | 결과 |
|---|---|
| 패키지 컨텍스트로 실행 | ✅ `shell:AppsFolder\AgentZeroLite.Dev_…!AgentZeroLite` → 프로세스 경로가 패키지 폴더, 창 제목 "AgentZero Lite" |
| 가상화 해제 — 앱이 띄운 프로세스의 쓰기 | ✅ `Invoke-CommandInDesktopPackage`로 패키지 안에서 cmd를 돌려 **새 폴더**를 `%LOCALAPPDATA%`·`%APPDATA%`에 만들고 HKCU 키를 씀 → 모두 실제 위치, 패키지 전용 저장소에는 없음 |
| 대조군 (가상화 설정만 뺀 같은 패키지) | 같은 동작이 `…\Packages\<pfn>\LocalCache\Local\…`·`…\LocalCache\Roaming\…`로 **숨겨짐** — 설정이 실제로 효과가 있다는 증거 |
| 실행 별칭 `-cli` | ✅ `%LOCALAPPDATA%\Microsoft\WindowsApps\AgentZeroLite.exe -cli status` → "Avalonia is running … groups 7, terminals 4" |
| DB 공유 | ✅ 기존 앱이 만든 워크스페이스 7개가 그대로 보임 |
| 터미널 탭 + AgentOne 기본 CLI | ✅ `-cli terminal-read 0 0`으로 AgentOne 탭 화면을 읽음 — `agent-one chat` TUI가 ConPTY/WebView2에서 정상 표시 |

| WACK (업로드용 패키지, `webnori.AgentZeroLite` 1.25.0.0) | ✅ **OVERALL PASS**. 선택 항목 "차단된 실행 파일"만 FAIL — `cmd`·`PowerShell`·`bash`와 `CreateProcess`/`Process.Start` 참조. 터미널 호스트라서 당연한 결과이고, 인증 메모(`listing.md`)에 설명을 넣었다 |

실측에서 배운 두 가지:

- **기존 폴더에 쓰는 테스트는 가상화를 구별하지 못한다.** 이미 있는 `%APPDATA%\npm`·`%LOCALAPPDATA%\AgentZeroLite`
  안의 새 파일은 가상화가 켜진 대조군에서도 실제 위치에 써졌다. 차이는 **아직 없는 폴더**를 만들 때 나타난다 —
  즉 가상화 해제가 없으면 **npm을 처음 쓰는 새 PC, AgentZero를 처음 설치한 PC**에서 DB와 전역 npm 설치가 패키지 안에
  갇힌다. 개발 PC에서 멀쩡해 보여도 이 설정은 빼면 안 된다.
- **PATH가 별칭을 가릴 수 있다.** 이 PC는 PATH에 WPF Debug 빌드 폴더가 먼저 있어서 셸에서 `AgentZeroLite.exe`가 WPF 쪽으로
  풀린다. Store 판만 설치한 사용자에게는 문제없지만, 두 판을 같이 쓰는 개발 PC에서는 별칭 전체 경로로 테스트할 것.

---

## 4. Partner Center 제출

1. **Packages** — (b)의 `.msix` 업로드. 장치 패밀리: Desktop만.
2. **Properties** — Category: *Developer tools*. Privacy policy URL: §1-4.
   System requirements: 8 GB RAM 권장(로컬 LLM 사용 시), x64.
3. **Age ratings** — IARC 설문. 사용자 생성 콘텐츠/웹 접근이 있으므로 "인터넷 무제한 접근: 예"로 답한다.
4. **Pricing and availability** — Free, 시장 선택.
5. **Submission options → Restricted capabilities 사유** — `store-listing/listing.md`의 영문 문구를
   그대로 붙여 넣는다(`runFullTrust`, `unvirtualizedResources` 각각).
6. **Store listings** (en-us, 필요하면 ko-kr 추가) — 설명·기능·키워드는 `listing.md`,
   스크린샷 최소 1장(권장 1920×1080 이상 4~6장), 300×300 로고는 `store-listing/StoreListing300x300.png`.
7. **Notes for certification** — `listing.md`의 문구: 계정 없이 실행 가능, 외부 LLM 키는 선택,
   터미널 탭은 PowerShell로 바로 확인 가능.
8. 제출 → 인증(보통 수 시간~3 영업일). 제한된 기능 심사가 붙으면 더 걸릴 수 있다.

---

## 5. 알려진 이슈와 후속 과제

- **WebView2 런타임**: Windows 11과 대부분의 Windows 10에는 이미 있지만 보장되지 않는다.
  없으면 터미널 탭이 뜨지 않는다. 후속: 시작 시 런타임을 확인하고 설치 링크를 안내하는 문구 추가.
- **읽기 전용 설치 폴더**: Store 판은 `C:\Program Files\WindowsApps\…` 에 설치된다. 코드는 설치 폴더를 읽기만 한다
  (확인함). 단, 워크스페이스 없이 연 터미널 탭의 시작 폴더가 exe 폴더라서 그 탭에서는 파일을 못 쓴다.
  후속: 기본 작업 폴더를 사용자 홈으로.
- **WPF 판과 동시 실행 불가**: 같은 단일 인스턴스 뮤텍스를 쓰므로 Store 판과 설치형 WPF를 동시에 띄울 수 없다
  (의도된 동작, DB 공유 때문).
- **자동 업데이트**: Store 판은 Store가 업데이트한다. GitHub 릴리스 판과 버전 번호 체계(§2)가 다르다는 점만 기억.
- **CI 자동화(후속)**: `avalonia-build.yml`에 MSIX 빌드를 붙이고, `msstore` CLI(Microsoft Store Developer CLI)로
  제출을 자동화할 수 있다. 첫 제출은 수동으로 하고 규칙이 굳으면 붙이는 것을 권장.
- **arm64(후속)**: 지금은 x64만. arm64 publish를 더하면 `.msixbundle`로 묶는다.

## 참고

- 개인 개발자 무료 등록: <https://learn.microsoft.com/en-us/windows/apps/publish/whats-new-individual-developer>
- 회사 계정 무료화(2026-05): <https://blogs.windows.com/windowsdeveloper/2026/05/07/publish-to-microsoft-store-as-a-company-now-with-free-registration-and-faster-onboarding/>
- MSIX 패키지 요구사항(버전 규칙, Store 재서명): <https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/app-package-requirements>
- EXE/MSI 요구사항: <https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msi/app-package-requirements>
- Flexible virtualization(`unvirtualizedResources`): <https://learn.microsoft.com/en-us/windows/msix/desktop/flexible-virtualization>
- 코드 서명 옵션: <https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options>
