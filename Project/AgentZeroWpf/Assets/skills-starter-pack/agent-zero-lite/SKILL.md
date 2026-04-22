---
name: agent-zero-lite
description: |
  AgentZero Lite 터미널 오케스트레이션 스킬. claude-code-cli 터미널 탭에서
  실행 중일 때, 같은 AgentZero Lite GUI 안의 다른 터미널 탭(Claude, Codex,
  pwsh, bash 등)을 AgentZeroLite CLI로 제어하고 AgentBot 채팅창에 메시지를
  표시한다. 마우스/키보드/스크린샷/UI 자동화 기능은 Lite에서 제공하지 않는다 —
  터미널-대-터미널 오케스트레이션에 특화된 경량 스킬이다.

  다음 상황에서 이 스킬을 사용할 것:
  - "다른 터미널에 명령 보내줘", "옆 탭에 git status 실행해줘"
  - "터미널 목록 보여줘", "지금 어떤 세션 열려 있어?"
  - "Codex한테 이 질문 물어봐줘", "Claude1한테 코드 리뷰 요청해"
  - "터미널에 엔터 보내줘", "Ctrl+C로 중단시켜줘", "ESC 눌러줘"
  - "옆 터미널 출력 확인해줘", "최근 2000자 읽어줘"
  - "AgentBot에 빌드 완료 메시지 표시해줘", "봇 채팅에 알림 보내줘"
  - Claude↔Codex 또는 Claude↔Claude 간 교차 대화, 다중 AI 티키타카
  - AgentZero Lite 상태 조회("agent status", "라이트 상태 보여줘")

  자동화/GUI 제어 요청(마우스 클릭, 스크린샷, UI 트리 탐색, 윈도우 활성화
  등)은 Lite 범위 밖이며, PRO 에디션의 `agent-zero` 스킬에서만 가능하다 —
  그런 요청이 오면 Lite 한계를 짧게 안내하고 종료할 것.
---

# AgentZero Lite — Terminal Orchestration Skill

AgentZero **Lite** 는 한 개의 GUI 프로세스 안에서 여러 개의 ConPTY 터미널 탭을
멀티워크스페이스로 묶어 보여주는 데스크톱 셸이다. 이 스킬은 **Claude Code 또는
Codex가 그 터미널 탭 중 하나에서 돌아갈 때**, 같은 앱 안의 *다른* 탭이나
AgentBot 패널로 자연어 지시를 전달할 수 있게 한다.

> **Lite의 스킬 범위는 CLI 한 바닥으로 축소되어 있다.**
> 마우스/키보드/스크린샷/윈도우 자동화가 필요하면 PRO 에디션(`agent-zero`)을 써야
> 한다. 이 스킬은 그 경계 안에서만 동작한다 — 경계를 넘는 요청은 거부하고 사용자에게
> Lite 한계를 한 줄로 안내한다.

---

## 실행 방법

AgentZero Lite는 단일 실행 파일 `AgentZeroLite.exe`가 GUI와 CLI를 모두 호스팅한다.
CLI 엔트리는 exe의 `-cli` 서브커맨드이며, 셸 종류에 상관없이 아래 형태가 안전하다:

```bash
AgentZeroLite.exe -cli <command> [options]   # bash / Git Bash / pwsh / cmd — 어디서든 OK
```

PowerShell 환경에서만 쓰면 래퍼 스크립트가 더 간결하다:

```powershell
AgentZeroLite.ps1 <command> [options]        # PowerShell 전용 — .ps1 은 bash에서 실행 불가
```

> **주의**: Git Bash / WSL 스타일 bash에서는 `.ps1` 을 직접 실행하지 말 것. bash는
> 확장자를 모르면 shell 스크립트로 파싱해버려서 `= : command not found` / `syntax error`
> 로 죽는다. 이 경우 `.exe -cli` 형태로 바꿔 호출하거나 `powershell -Command "AgentZeroLite.ps1 ..."`
> 로 감싼다.

`.claude/skills/agent-zero-lite/scripts/` 에는 명령별 PowerShell 래퍼도 같이 설치되는데,
이들은 `powershell -ExecutionPolicy Bypass -File <script>.ps1 ...` 형태로만 호출해야 하므로
일반 사용에는 위의 `.exe -cli` 직접 호출이 더 간편하다.

### PATH 리로드 (버전 업데이트 후)

`Settings → AgentZero CLI → Register PATH` 로 새 버전을 등록해도, 현재 bash 세션은
이전 PATH를 캐싱하고 있어 옛날 exe를 호출할 수 있다. `Unknown command` 오류가 나면:

```bash
export PATH="$(powershell -Command "[Environment]::GetEnvironmentVariable('Path','User') + ';' + [Environment]::GetEnvironmentVariable('Path','Machine')" | tr -d '\r')"
```

### 선결 조건

- **AgentZero Lite GUI가 실행 중**이어야 한다 (단일 인스턴스 mutex `Local\AgentZeroLite.SingleInstance`).
- 미실행이면 `AgentZeroLite.exe -cli open-win` 으로 1회 기동할 것.

---

## 명령 레퍼런스

### 앱 상태

```bash
# Lite 상태 요약 (캡처 상태, 선택된 hwnd, 스크롤 설정 등)
AgentZeroLite.exe -cli status

# CLI 액션 히스토리 (기본 최근 50건)
AgentZeroLite.exe -cli log --last 20
AgentZeroLite.exe -cli log --clear

# GUI 띄우기 / 닫기
AgentZeroLite.exe -cli open-win
AgentZeroLite.exe -cli close-win
```

글로벌 옵션:
- `--no-wait` : fire-and-forget (응답 MMF 대기 생략)
- `--timeout N` : 응답 대기시간 ms (기본 5000)

### 터미널 세션 조회

```bash
AgentZeroLite.exe -cli terminal-list
```

출력 예 (프리티 프린트 + 마지막에 JSON):

```
=== Active Terminal Sessions ===

  Group 0: monorepo  (D:\Code\MyProject)
    Tab 0: PowerShell *
      ID: monorepo/PowerShell  HWND: 0x000A1234
    Tab 1: Claude Code
      ID: monorepo/Claude Code  HWND: 0x000A5678

  Group 1: blog  (D:\Code\blog)
    Tab 0: pwsh [not started]
      ID: blog/pwsh  HWND: N/A
```

- `*` 표시 = 현재 활성 탭
- `[not started]` = 탭은 존재하지만 ConPTY 세션이 아직 기동되지 않음 → 사용자가 탭을 한 번 클릭해야 한다

### 터미널에 텍스트 + Enter 전송

```bash
# 일반 셸이나 Claude Code TUI: 한 번에 입력 + 제출
AgentZeroLite.exe -cli terminal-send 0 0 "git status"

# 다어절 문장
AgentZeroLite.exe -cli terminal-send 0 1 "이 PR에서 놓친 엣지케이스 하나만 짚어봐"
```

첫 두 인자는 `group_index tab_index`. `terminal-list`의 JSON 출력에서 확인할 것.

### 터미널에 특수키 전송

```bash
AgentZeroLite.exe -cli terminal-key 0 0 cr       # Enter 단독 (Codex 등 TUI용)
AgentZeroLite.exe -cli terminal-key 0 0 esc      # TUI 메뉴 닫기
AgentZeroLite.exe -cli terminal-key 0 0 tab      # 자동완성
AgentZeroLite.exe -cli terminal-key 0 0 ctrlc    # 프로세스 중단
AgentZeroLite.exe -cli terminal-key 0 0 up       # 히스토리 이전
AgentZeroLite.exe -cli terminal-key 0 0 hex:0D   # 임의 바이트
```

지원 키: `cr`, `lf`, `crlf`, `esc`, `tab`, `backspace`, `del`, `ctrlc`, `ctrld`,
`up` / `down` / `left` / `right`, `hex:XX`.

### 터미널 출력 읽기

```bash
# 전체 버퍼
AgentZeroLite.exe -cli terminal-read 0 0

# 마지막 2000자 (대부분의 경우 이 정도로 충분)
AgentZeroLite.exe -cli terminal-read 0 0 --last 2000
```

ANSI 코드는 제거된 클린 텍스트가 반환된다.

### AgentBot 채팅 (표시만)

```bash
# 기본 발신자 = "CLI"
AgentZeroLite.exe -cli bot-chat "빌드 완료"

# 발신자 지정
AgentZeroLite.exe -cli bot-chat --from CI "테스트 12개 전부 통과"
```

> **중요**: Lite의 `bot-chat`은 **AgentBot 창에 텍스트를 그리기만** 한다. AgentBot의
> LLM을 기동하거나 응답을 받지는 않는다. (그 기능은 PRO의 `tell-ai`만 지원)
> AgentBot 창이 닫혀 있으면 메시지는 표시되지 않는다 — 사용자에게 Bot 패널을 열어
> 달라고 안내할 것.

---

## 핵심 워크플로우

### W1. 다른 터미널에 명령 보내고 결과 읽기

```bash
# 1. 어떤 탭이 있는지 확인
AgentZeroLite.exe -cli terminal-list

# 2. 해당 탭이 "not started"면 사용자에게 클릭 부탁 후 재조회

# 3. 명령 전송
AgentZeroLite.exe -cli terminal-send 0 0 "dotnet build"

# 4. 출력 읽기 (Claude Code Opus 같은 긴 응답은 60~90초 대기 후 읽는 것이 안정적)
AgentZeroLite.exe -cli terminal-read 0 0 --last 3000
```

### W2. Claude ↔ Codex 간 교차 대화

AgentZero Lite의 "대표 유스케이스". 한쪽 AI가 다른 탭의 AI에게 말을 걸고, 응답을
`terminal-read`로 확인한다.

| 대상 TUI | 패턴 |
| --- | --- |
| **Claude Code** | `terminal-send`만으로 입력 + Enter 동시 처리 (`\r` 내장) |
| **OpenAI Codex** | `terminal-send` → 1초 대기 → `terminal-key <g> <t> cr` 2단계 필요. 긴 한글에서 `\r`이 개행으로만 처리되는 현상 있음 |
| **일반 셸 (bash/pwsh/cmd)** | `terminal-send`만으로 충분 |

```bash
# Claude Code 대상
AgentZeroLite.exe -cli terminal-send 0 1 "이 함수의 시간 복잡도를 O(n log n)으로 줄일 수 있을까?"

# Codex 대상 (2단계)
AgentZeroLite.exe -cli terminal-send 0 2 "이 함수의 시간 복잡도를 O(n log n)으로 줄일 수 있을까?"
sleep 1
AgentZeroLite.exe -cli terminal-key 0 2 cr
```

응답 대기시간 가이드 (실측 기반):

| 응답 유형 | 권장 대기 |
| --- | --- |
| 단답 1~2문장 | 1~2초 |
| 3~5문장 | 3초 |
| 코드/목록 포함 | 5~6초 |
| Claude Code Opus 복잡한 주제 | **60~90초** |

> `sleep N` (N≥2)은 일부 Claude Code 환경에서 차단된다. `ScheduleWakeup` 또는 백그라운드 실행 패턴을 우선 고려하고, 로컬 bash 스크립트에서 호출하는 경우에만 `sleep`을 써라.

### W3. 장시간 작업 진행 알림

```bash
# 작업 시작 알림
AgentZeroLite.exe -cli bot-chat --from BuildScript "dotnet build 시작"

# 작업이 끝나면
AgentZeroLite.exe -cli bot-chat --from BuildScript "빌드 OK (12.4s)"
```

AgentBot 창이 열려 있으면 사용자가 IDE를 쳐다보지 않고도 채팅 패널의 토스트로
알림을 받는다.

---

## 금지 / 회피 패턴

1. **자기 자신의 터미널에는 `terminal-send` 금지.** 자기 탭의 `group_index / tab_index`에
   메시지를 보내면 자기 입력으로 들어와 루프가 무한 증폭한다. 발신하기 전 `terminal-list`의
   `*` 표시로 활성 탭을 확인하고, 해당 조합은 건너뛸 것.
2. **`terminal-read` ANSI 버퍼 덮어쓰기 문제**: Claude Code가 `Precipitating… / Thinking…`
   애니메이션을 출력하는 동안 버퍼가 ANSI 코드로 가득 차 실제 응답이 밀려날 수 있다.
   긴 응답이면 60~90초 대기 후 읽거나, 상대 AI에게 공유 파일(메모장 경로)에 직접 기록해
   달라고 요청하고 이쪽은 `Read`로 파일을 확인한다.
3. **PRO 전용 기능 호출 금지**: `tell-ai`, `bot-signal`, `meeting_*`, `mouseclick`, `screenshot`,
   `element-tree`, `activate`, `text-capture`, `scroll-capture`, `dpi`, `keypress` 는
   Lite에 존재하지 않는다. 사용자가 요청하면 "Lite에서는 미지원 — PRO 에디션 필요" 한 줄로 안내.
4. **GUI 상태 변경 금지**: Lite CLI는 터미널 탭을 새로 만들거나 워크스페이스를 전환하지 못한다.
   그런 작업은 사용자가 GUI에서 직접 눌러야 한다. 자동으로 시도하지 말 것.

---

## 인수 없이 `/agent-zero-lite`로만 호출된 경우

즉시 작업을 수행하지 말고, 다음 구조로 짧은 사용 팁을 한국어로 안내한다:

1. **한 줄 소개** — AgentZero Lite는 단일 GUI 안의 여러 터미널 탭을 CLI로 원격 제어할 수 있으며, 본 스킬이 그 CLI 사용법을 Claude에게 가르친다.
2. **할 수 있는 것** (목록, 각 1줄):
   - 다른 탭에 명령 전송 / 출력 읽기 (`terminal-send`, `terminal-read`)
   - 특수키 전송 (`terminal-key` — Enter, ESC, Ctrl+C, 화살표 등)
   - 세션 목록 조회 (`terminal-list`)
   - AgentBot 채팅창에 알림 표시 (`bot-chat`)
   - Lite 앱 상태 조회 (`status`)
3. **할 수 없는 것** (한 줄): 마우스/키보드/스크린샷/윈도우 자동화는 Lite 범위 밖 — 필요하면 PRO 에디션 `agent-zero` 스킬.
4. **전형적 시작 패턴 1~2개**:
   - "옆 탭에서 `git status` 실행하고 결과 알려줘" → `terminal-list` → `terminal-send` → `terminal-read`
   - "Codex한테 이 코드 리뷰 요청해" → `terminal-send` + 1초 + `terminal-key cr` → 3초 뒤 `terminal-read --last 3000`
5. **마무리 질문** — "어떤 터미널 작업을 도와드릴까요?"

이 안내 중에는 실제 CLI 명령을 실행하지 말고, 사용자의 구체 요청을 받은 뒤 움직일 것.

---

## 스크립트 래퍼

`scripts/` 아래에는 각 명령별 PowerShell 파일이 있어 파라미터 검증과 함께 호출 가능:

```powershell
powershell -ExecutionPolicy Bypass -File .claude/skills/agent-zero-lite/scripts/terminal-send.ps1 -GroupIndex 0 -TabIndex 0 "git status"
```

직접 `AgentZeroLite.exe -cli terminal-send 0 0 "git status"` 를 호출하는 것과 동일한 결과.
경로 관리가 번거로우면 후자(.exe 직접 호출)를 쓸 것 — 셸 종류와 무관하게 동작한다.
