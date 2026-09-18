# macOS 스모크 체크리스트 — AgentZero Lite (Avalonia host)

CI(macos-14)는 빌드·`.app` 번들·`-cli version/selftest`·헤드리스 테스트까지만 증명한다. 화면과 WKWebView는 사람이
Mac에서 확인해야 한다. 결과는 아래 표에 날짜·기기·OS 버전과 함께 적는다.

## 준비

1. GitHub Actions의 `AgentZeroLite-Avalonia-osx-arm64` 아티팩트를 받아 푼다 (`publish-avalonia/AgentZeroLite.app`).
2. 격리 속성 해제(ad-hoc 서명이라 Gatekeeper가 막는다):
   ```sh
   xattr -dr com.apple.quarantine AgentZeroLite.app
   open AgentZeroLite.app
   ```
3. CLI 래퍼: `AgentZeroLite.app/Contents/MacOS/AgentZeroLite.sh status`

## 체크리스트

| # | 항목 | 기대 | 결과 |
|---|---|---|---|
| 1 | 첫 실행 | 창이 뜨고 `~/Library/Application Support`가 아닌 `~/.local/share/AgentZeroLite`(LocalApplicationData)에 `agentZeroLite.db`·`logs/app-log.txt` 생성 | |
| 2 | 워크스페이스 추가 | 사이드바 ＋ → 폴더 피커 → 목록에 표시 | |
| 3 | zsh 탭 | ＋ → 프롬프트 표시, 색상(`ls -G`, `TERM=xterm-256color`), `echo $LANG`=`en_US.UTF-8` | |
| 4 | 한글 입력 | `echo 한글` 에코, 한글 파일명 `ls` | |
| 5 | 리사이즈 | 창 크기 변경 후 `tput cols`가 바뀜, 화면 깨짐 없음 | |
| 6 | 분할 | Cmd+Alt+Right / Cmd+Alt+Down, 페인 각각 프롬프트 | |
| 7 | 단축키(터미널 포커스) | Cmd+Alt+T 새 탭, Cmd+Alt+W 닫기, Alt+화살표 페인 이동 | |
| 8 | 팝업·메뉴 | 탭 우클릭 메뉴와 ▾ 메뉴가 터미널 **위에** 보임 (NativeControlHost z-order) | |
| 9 | claude 탭 | `Claude` 정의로 탭 → Claude Code TUI 렌더·입력 | |
| 10 | CLI | `AgentZeroLite.sh terminal-list`, `terminal-send 0 0 "ls"`, `terminal-read 0 0`, `layout split-right` | |
| 11 | AgentBot AI | 설정 → LLM → External(Webnori) → 봇 페인 AI 모드로 "list my terminals" → 카드·결과 | |
| 12 | 설정 저장 | External 키 입력 → 저장 → `llm-settings.json`의 키가 `aesg:v1:`로 시작, `secret.key` 권한 0600 | |
| 13 | 종료 | 창 닫기 후 `ps aux | grep zsh`에 고아 zsh 없음, 파이프/락 파일 정리 | |
| 14 | 재실행 | 워크스페이스·탭·분할 복원 | |

## 기록

| 날짜 | 기기 / macOS | 빌드 | 통과 | 비고 |
|---|---|---|---|---|
| | | | | |
