# Mac App Store 심사 대응 런북 (AgentZero Lite)

심사 결과(승인·반려·질문)가 오면 이 문서대로 대응한다. 배경·실측·등록 기록은
`Docs/avalonia-v2/macos-distribution.md` §4가 원본이고, 여기는 **다음 행동**만 둔다.
Windows 쪽 선례는 `Docs/avalonia-v2/microsoft-store.md` §6(4차 만에 통과 — 반려는 정상 과정이다).

---

## 0. 현재 상태 (2026-10-30)

| 항목 | 값 |
|---|---|
| 앱 | AgentZero Lite · App Store Connect 앱 ID **6825237056** · 번들 ID `com.psmon.agentzerolite` · 팀 `HA39FQRUZG`(sang man park, 개인) |
| 제출 | **2차** — 0.26.6 (빌드 **3**) · 같은 submission `38e8bb1c-45f0-4cee-b073-0e7f9089f54a` 재제출 · 2026-10-30 16:02 KST · `WAITING_FOR_REVIEW` |
| 버전 ID | `ec895cda-2710-4017-a0c7-dfaa1cc52f1d` · 빌드 ID `3d82af5b-80eb-448d-b06b-c9dc366a271b` |
| 출시 방식 | **MANUAL** — 승인돼도 운영자가 출시를 눌러야 공개 |
| 판매 지역 | 전 지역 **중국 본토 제외**(가이드라인 5) |
| 남은 일 | 대한민국 연락처 확인(App Store Connect → 비즈니스) — Apple 측 오류로 미완. 제출은 막지 않았다. 빈 초안 submission `fe965f27-…`(항목 0, 취소 불가 상태) — 무시 |

### 심사 이력

| 차수 | 버전(빌드) | 결과 | 사유 → 조치 |
|---|---|---|---|
| 1 | 0.25.8 (2), 10-21 제출 | **반려** 2026-10-28 (MacBook Pro 14 Nov 2024) | **2.4.5(i)** 임시 예외 `home-relative-path.read-write "/"`·`absolute-path.read-write "/usr/local/"` 거부(`/opt/homebrew/`는 지적 없음) → 두 예외 삭제, 폴더 선택 + security-scoped bookmark(`SandboxFolderAccess`, `files.bookmarks.app-scope`). **5 Legal(중국)** 메타데이터의 "OpenAI" → 단어 삭제 + 중국 본토 판매 제외. Resolution Center에 조치 답변 |
| 2 | 0.26.6 (3), 10-30 재제출 | 대기 | — |

확인 기록: 2026-10-25 웹(App Store Connect → 앱 심사)으로 확인 — 여전히 `심사 대기 중`, 메시지 없음.

`.secret/apple-asc.json`에 모든 ID가 있다. `asc`는 `agentzero` 프로필로 로그인돼 있다(시스템 키체인).

## 1. 상태 확인

```sh
asc versions list --app 6825237056 --output table          # WAITING_FOR_REVIEW → IN_REVIEW → PENDING_DEVELOPER_RELEASE | REJECTED
asc submit status --app 6825237056 2>/dev/null || true
```
반려 사유 본문은 App Store Connect 웹 → 앱 → **앱 심사(Resolution Center)** 에 있다(메일은 요약만). 웹은
Playwright로 연다: 스크래치패드의 `apple-pw/browser.mjs`(정식 Chrome `channel: 'chrome'` + 별도 프로필 —
번들 Chromium은 패스키를 USB 키로만 받는다). 로그인은 운영자가 직접.
**Windows PC**(asc·Apple 키 없음): Playwright `launchPersistentContext` + `channel: 'msedge'` 전용 프로필로
`https://appstoreconnect.apple.com/apps/6825237056/distribution/reviewsubmissions`를 열고 운영자 로그인 후
본문 텍스트를 읽는다 — 2026-10-25 이 방식으로 확인했다.

## 2. 승인됐을 때

1. 운영자 확인 후 출시: 웹 → 버전 → **이 버전 출시**(또는 `asc` 해당 명령). 자동 출시하지 않는다.
2. 기록: `macos-distribution.md` §4.4에 승인 일시, 이 문서 §0 갱신.
3. 홈페이지·README(영/한 짝)에 Mac App Store 링크 추가 → `doc-v*` 태그로 Pages 배포.

## 3. 반려됐을 때 — 예상 사유와 준비된 대응

| 가이드라인 | 예상 지적 | 1차 대응(코드 변경 없이) | 2차 대응(변경) |
|---|---|---|---|
| **2.4.5(i)/임시 예외** | `home-relative-path ["/"]`, `/opt/homebrew/`·`/usr/local/` 예외가 과하다 | Resolution Center에 답변: 터미널 호스트이고 자식 셸이 샌드박스를 상속하며, 접근은 사용자가 탭에 입력한 명령에서만 일어난다(심사 메모와 같은 논리, `macos/appstore/listing.md` Notes) | 홈 전체 → 사용자가 고른 폴더만(`files.user-selected` + security-scoped bookmark) + `~/.zshrc`·`~/.claude` 등 명시 경로로 축소. 실측 필요 |
| **2.4.5(iv)** | Settings → CLI Definitions의 Install이 추가 코드를 설치한다 | 사용자의 npm을 사용자가 누를 때만 실행, 앱 기능을 바꾸지 않음을 설명 | 스토어판(샌드박스 감지: `APP_SANDBOX_CONTAINER_ID`)에서는 설치 버튼 대신 **명령 복사/터미널 탭에 입력만** 하도록 변경 |
| **2.1 / 4.2 (기능)** | 심사자가 claude/codex가 안 돈다고 지적 | 해당 도구는 사용자가 설치·인증하는 외부 CLI이며 앱의 핵심은 터미널(zsh) 호스트임을 설명(운영자 결정 2026-10-21: AgentZero는 동작하는 터미널을 띄우는 역할) | 설명문·심사 메모에 제약을 더 명확히 |
| **2.3 (메타데이터)** | 스크린샷·설명 불일치(예: 플러그인·봇이 설정 없이 비어 보임) | 해당 문구 수정 후 재제출(빌드 불필요) | 스크린샷 교체(`macos/appstore/screenshots/`, 1440×900, 알파 없음) |
| **5.1.1 (개인정보)** | "데이터 수집 안 함"과 외부 LLM 전송 | 프롬프트는 사용자가 설정한 엔드포인트로만 가고 개발자는 받지 않음을 설명 | — |
| 2.4.5(viii) 등 기타 | — | 사유 원문을 `macos-distribution.md` §4.4에 그대로 기록 후 판단 | — |

원칙: **반려 원문 → 기록 → 답변으로 풀리는지 먼저 → 안 되면 최소 변경**. 메타데이터만 바꾸면 빌드 재업로드 없이
재제출된다. 바이너리를 바꾸면 빌드 번호를 올린다.

## 4. 재빌드·재제출 절차

```sh
export DOTNET_ROOT=$HOME/.dotnet PATH=$HOME/.dotnet:$PATH          # 이 맥: PATH의 dotnet은 9.0뿐
security unlock-keychain -p "$(cat .secret/certs/signing-keychain-password.txt)" ~/Library/Keychains/agentzero-signing.keychain-db
dotnet publish Project/AgentZeroAvalonia/AgentZeroAvalonia.csproj -c Release -r osx-arm64 --self-contained true -p:UseAppHost=true -o publish-avalonia/osx-arm64
# 로컬 샌드박스 확인(선택): MAS_SANDBOX_TEST=1 … build-appstore.sh … → open publish-avalonia/appstore-test/AgentZeroLite.app
MAS_SIGN_ID="Apple Distribution: sang man park (HA39FQRUZG)" \
MAS_PKG_ID="3rd Party Mac Developer Installer: sang man park (HA39FQRUZG)" \
MAS_PROFILE=.secret/certs/AgentZeroLite_MAS.provisionprofile \
  sh Project/AgentZeroAvalonia/macos/appstore/build-appstore.sh publish-avalonia/osx-arm64 publish-avalonia/appstore <ver> <build+1>
asc builds upload --app 6825237056 --pkg publish-avalonia/appstore/AgentZeroLite-<ver>-<build>-appstore.pkg --version <ver> --build-number <build> --wait
asc versions attach-build --app 6825237056 --platform MAC_OS --version-id <버전ID> --build-id <새 빌드ID>
asc review submit --app 6825237056 --version-id <버전ID> --build-id <새 빌드ID> --dry-run   # wouldSubmit:true 확인
asc review submit --app 6825237056 --version-id <버전ID> --build-id <새 빌드ID> --confirm   # 첫 제출만
```

**반려 뒤 재제출은 위 마지막 줄이 아니다** (2026-10-30 실측): 반려된 submission(`UNRESOLVED_ISSUES`)이 버전을 계속
잡고 있어 `asc review submit`은 새 초안을 만들고 "could not be safely reused" 충돌로 끝나며, 웹의 "앱 심사에 다시 제출"
버튼도 비활성이 된다. 기존 submission을 다시 내는 것이 맞다:
```sh
asc review items list --submission <반려된 submission ID>                 # 항목 ID, state REJECTED
asc review items update --id <항목 ID> --resolved true                    # → READY_FOR_REVIEW
asc review submissions-submit --id <반려된 submission ID> --confirm       # → WAITING_FOR_REVIEW
```
버전 문자열을 새 빌드에 맞춘 뒤(`asc versions update --version-id … --version <ver>`) 빌드를 붙이고, 그다음 위 세 줄.

- 빌드 번호는 실패한 업로드까지 포함해 매번 올린다(1: ITMS-90296, 2: 1차 반려, 3: 2차 제출 → 다음은 **4**).
- `dotnet publish`가 `Project/version.txt`나 `Project/AgentOne/version.txt`를 올리면 커밋 전에 되돌린다.
- 반려 대응으로 엔타이틀먼트를 바꾸면 `MAS_SANDBOX_TEST=1` 번들로 GUI 기동·`-cli status`·zsh 탭을 먼저 확인한다
  (워크스페이스는 컨테이너 DB `CliGroups`에 한 줄 넣으면 된다). 확인 뒤 테스트 데이터는 지운다.
- 심사 메모 수정: `asc review details-update --id 87f61568-ec0b-4507-988b-67ee3eb1d369 --notes "…"`
  (원본 `macos/appstore/listing.md`를 먼저 고치고 같은 내용을 넣는다).

## 5. 함정 (이미 한 번씩 밟은 것)

- 샌드박스 GUI가 즉시 abort → apphost가 아니라 `macos/launcher` 런처가 들어갔는지(`otool -L … | grep AppKit`).
- 번들 안 모든 실행 파일에 샌드박스 엔타이틀먼트(ITMS-90296) — 보조 실행 파일은 `inherit.entitlements`.
- `com.apple.application-identifier`가 든 엔타이틀먼트를 프로파일 없이 ad-hoc 서명하면 실행 즉시 죽는다(테스트 모드는 빼고 서명).
- 스크린샷 업로드는 알파 채널 PNG를 거부한다.
- `.github/workflows/` 수정을 푸시하려면 gh 토큰에 `workflow` 권한.
- 인증서·프로파일 만료 2027-10-21(Developer ID는 2027-11-20) — 만료 전 재발급, CI Secrets도 교체.
