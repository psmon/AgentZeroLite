# AgentZero Lite — 확장 기능 매뉴얼 (README-EX)

> 🇺🇸 English: [README-EX.en.md](README-EX.en.md) · ⬅ 본 문서로 [README.md](README.md)

이 문서는 최근 추가된 **확장 기능**의 사용법만 모은 실전 매뉴얼입니다.
기존 기능은 `README`를 참고하세요.

확장 기능은 두 갈래입니다.
- **CLI 확장** — `AgentZeroLite.exe -cli <명령>` 으로 앱을 스크립팅
- **GUI/봇 확장** — 화면 안에서 쓰는 신규 기능(파일 도구, Diff 리뷰, 커맨드 팔레트 Ctrl+J, 에이전트 상태 감지)

---

## 0. 준비

```powershell
# (최초 1회) 최신 빌드
dotnet build Project/AgentZeroWpf/AgentZeroWpf.csproj -c Debug

# 편의상 exe 경로를 변수로 (PowerShell)
$AZ = "Project\AgentZeroWpf\bin\Debug\net10.0-windows\AgentZeroLite.exe"

# 전체 명령 목록
& $AZ -cli help
```

> 대부분의 CLI 명령은 **GUI가 떠 있지 않아도** 동작합니다(파일/DB/git 직접 처리).
> 터미널을 다루는 명령(`terminal-*`, `agent-hook`)만 실행 중인 GUI가 필요합니다.

---

## 1. 봇 워크스페이스 파일 도구 🗂️

봇을 **AI 모드**로 두면, 봇이 **현재 활성 워크스페이스 폴더 안에서** 파일을 직접
읽고/쓰고/수정하고/검색할 수 있습니다. 자연어로 지시하면 됩니다.

| 하고 싶은 것 | 봇에게 이렇게 | 내부 도구 |
|---|---|---|
| 파일 목록 보기 | "이 폴더에 어떤 파일 있어?" | list_files |
| 파일 읽기 | "README.md 읽어줘" | read_file |
| 파일 요약 | "Program.cs 요약해줘" | read_file |
| 내용 검색 | "이 프로젝트에서 'TODO' 찾아줘" | grep |
| 파일 수정 | "config.json 의 port를 8080으로 바꿔줘" | edit_file |
| 새 파일 | "notes.md 만들어서 회의록 넣어줘" | write_file |

**중요 — 스코프(안전장치)**
- 파일 작업은 **지금 선택된 워크스페이스 폴더**로 한정됩니다. 워크스페이스를 바꾸면
  대상 폴더도 바뀝니다.
- 폴더 **밖** 경로(`..`, 절대경로 이탈)는 자동 거부됩니다.
- 워크스페이스가 없으면 파일 접근이 막힙니다(기본 차단).

**정확도 팁**: 파일명은 확장자까지 정확히("README.md", "README" ❌).
봇은 이제 **`list_files`로 폴더를 훑어 정확한 파일명을 스스로 찾을 수 있습니다** —
"어떤 파일 있는지 보고 README 요약해줘"처럼 지시하면 목록→읽기를 이어서 합니다.

**동작 확인(로그)**: `logs\app-log.txt` 에
`[AIMODE] read_file root="C:\...\현재워크스페이스" path="README.md"` 형태로 어떤
폴더를 대상으로 했는지 찍힙니다.

---

## 2. Diff 리뷰 & 에이전트 재투입 🔍

변경사항(diff)을 앱 안에서 보고, 줄마다 코멘트를 달아 **한 번에 에이전트에게
후속 작업으로 넘기는** 리뷰 흐름입니다.

**사용법**
1. 좌측 **ActivityBar의 Diff 리뷰 아이콘** 클릭
2. 현재 워크스페이스의 git 변경분이 색상(+/-)으로 렌더됩니다
   - *변경사항이 없으면 "No changes"* — 먼저 코드를 수정해 두세요
3. 리뷰할 줄의 **💬** 클릭 → 코멘트 입력 → **Add**
4. 여러 줄에 코멘트를 단 뒤 우상단 **"Ship N to agent"** 클릭
5. 수집한 코멘트가 **하나의 지시로 묶여 봇 AI에게 전달**됩니다

코멘트는 저장되어 세션이 바뀌어도 유지됩니다.

---

## 3. 멀티 에이전트 협업 🤝

터미널에 코딩 에이전트를 **2개 이상** 띄우면, 에이전트끼리(또는 스크립트로) 서로
지시하고 결과를 받아올 수 있습니다.

```powershell
& $AZ -cli terminal-list                        # 그룹/탭 인덱스 확인
& $AZ -cli terminal-send 0 1 "이 파일 테스트 짜줘" # (그룹0 탭1) 에이전트에 지시
& $AZ -cli terminal-wait 0 1 --idle-ms 2000      # 상대가 끝날 때(무입력)까지 대기
& $AZ -cli terminal-read 0 1 --last 3000         # 결과 읽기
```

- **`terminal-wait`** 가 핵심입니다. sleep 반복 폴링 대신, 대상 터미널의 출력이
  멈출 때(작업 완료 신호)까지 정확히 기다립니다 → 한 에이전트가 다른 에이전트를
  **감독**할 수 있습니다.
- 대표 활용: **리뷰 페어**(A가 작성 → B에게 리뷰 위임 → 결과 반영),
  **분업**(A=구현, B=테스트), **토론 중재**(봇이 양쪽을 오감).

> 에이전트가 이 명령들을 **스스로 익히게** 하려면 아래 6번의 "가이드 스텁"을 설치하세요.

---

## 4. git worktree 병렬 작업 🌿

같은 저장소를 **충돌 없이** 여러 갈래로 동시에 작업합니다. 에이전트별로 격리된
체크아웃을 주는 데 좋습니다.

```powershell
& $AZ -cli worktree list                         # 현재 worktree 목록
& $AZ -cli worktree add ..\repo-featB featB       # featB 브랜치로 새 격리 폴더
& $AZ -cli worktree add ..\repo-x --trust         # 만들면서 에이전트 CLI 신뢰까지(아래 6번)
& $AZ -cli worktree remove ..\repo-featB           # 정리
```

에이전트 A는 메인에서, 에이전트 B는 `featB` worktree에서 각자 진행 → 나중에 병합.

---

## 5. 에이전트 감독 실행(오케스트레이션) 🧭

여러 작업을 **의존성(DAG)** 으로 묶어, **코디네이터가 실행 중인 터미널 에이전트들에
자동으로 배분·감독**하는 실행 단위입니다.

```powershell
# 작업 정의 파일 (deps 로 의존성 지정)
#   run.json
#   { "name":"빌드파이프라인",
#     "tasks":[ {"key":"a","prompt":"코드 생성","deps":[]},
#               {"key":"b","prompt":"테스트","deps":["a"]} ] }

& $AZ -cli orchestrate create run.json    # 실행 계획 생성 → "Created run #N"
& $AZ -cli orchestrate status N            # Task/의존성/상태 확인 (b ← [a])
& $AZ -cli orchestrate run N               # 실행! 준비된 Task를 터미널 에이전트에 배분
& $AZ -cli orchestrate list                # 최근 실행 목록
```

**동작 방식**: `run` 하면 실행 중인 각 터미널이 워커가 됩니다. 코디네이터는 의존성이
충족된 Task를 워커에 보내고(터미널에 프롬프트 전송), 그 터미널이 **유휴(작업 완료)**
가 되면 다음 Task로 전진합니다. 모든 Task가 끝나면 run 상태가 done으로 저장됩니다.

> `run` 은 실행 중 GUI + 터미널 에이전트가 필요합니다. 진행은 `orchestrate status N`
> 으로 추적하세요. (단일 위임은 3번의 멀티 에이전트 협업으로도 가능합니다.)

---

## 6. 에이전트 통합 설치 (선택 · 동의 필요) 🔗

에이전트 CLI(Claude Code / Cursor / Copilot / Codex)와 더 매끄럽게 연동하는
설치형 기능입니다. **여러분의 홈 설정 파일을 수정**하므로 원할 때만, 명시적으로
설치합니다. 되돌리기 명령도 제공합니다.

| 기능 | 하는 일 | 설치 | 되돌리기 |
|---|---|---|---|
| **상태 훅** | 에이전트 실제 상태를 봇에 전달 — **Claude + Codex/Cursor**(설치된 CLI만) | `-cli agent-hook-install` | `-cli agent-hook-uninstall` |
| **폴더 신뢰** | "이 폴더 신뢰?" 프롬프트가 자동입력을 가로채지 않게 미리 신뢰 | `-cli trust-workspace [경로]` | 신뢰 파일 수동 삭제 |
| **가이드 스텁** | 에이전트가 위 CLI 사용법을 스스로 학습(`-cli help`로 조회) | `-cli skill-stub-install` | `-cli skill-stub-uninstall` |

```powershell
& $AZ -cli agent-hook-install     # ~/.claude*/settings.json + ~/.codex, ~/.cursor 훅 등록(백업됨)
& $AZ -cli trust-workspace .      # 현재 폴더를 각 에이전트 CLI에 신뢰 등록
& $AZ -cli skill-stub-install     # 에이전트 스킬 폴더에 사용법 스텁 주입
```

- 이 명령들은 **직접 실행할 때만** 파일을 수정합니다(자동/암묵 설치 없음).
- 훅/스텁은 마커로 식별해, 제거 시 **여러분의 다른 설정은 건드리지 않습니다.**

---

## 7. 토큰 비용 추정 💰

기록된 토큰 사용량을 바탕으로 예상 비용을 모델별로 보여줍니다.

```powershell
& $AZ -cli cost
# 예)
# Estimated cost (all recorded turns): $XX.XX  (N turns)
# By model:
#   claude-opus-...      $ ...   (N turns)
```

> 가격은 편집 가능한 **기본값 기반 추정치**이며 실시간 시세가 아닙니다.

---

## 8. 내장 가이드 서빙 📖

에이전트/사용자가 언제든 최신 사용법을 조회할 수 있습니다(항상 현재 빌드와 일치).

```powershell
& $AZ -cli help agentzero      # 터미널/워크스페이스/비용 등 에이전트 제어 가이드
& $AZ -cli help orchestrate    # 오케스트레이션 가이드
```

---

## 9. 예약 자동화 (Automations) ⏰

프롬프트를 **스케줄에 맞춰 봇에 자동 실행**시킵니다. 매일 리뷰, 주기적 요약 등에 유용합니다.

```powershell
# 30분마다 / 매시 정각 / 매일 특정 시각(UTC)
& $AZ -cli automation create --name "daily-review" --schedule "daily 09:00" --prompt "오늘 변경사항 요약해줘"
& $AZ -cli automation create --name "ping" --schedule "every 30m" --prompt "빌드 상태 확인해줘"
& $AZ -cli automation list       # 등록 목록 + 다음 실행 시각
& $AZ -cli automation due         # 지금 실행 대상 확인
& $AZ -cli automation remove 3    # 삭제
```

- 스케줄 형식: **`every <N>m`** / **`every <N>h`** / **`hourly`** / **`daily HH:mm`**(UTC).
- GUI가 떠 있으면 스케줄러(60초 틱)가 실행 시각이 된 자동화를 **봇 AI에 발화**하고
  다음 실행 시각을 갱신합니다.

---

## 10. 커맨드 팔레트 (Ctrl+J) 🎯

**Ctrl+J** 로 어디서든 워크스페이스·기능으로 **퍼지 검색 점프**합니다.

1. `Ctrl+J` → 검색창이 뜹니다
2. 일부만 입력해도 매칭됩니다 — 예: `dr` → **Diff Review**, `web` → **WebDev**,
   워크스페이스 이름 일부 → 해당 워크스페이스로 전환
3. `↑`/`↓` 로 이동, `Enter` 로 실행, `Esc` 로 닫기

대상: 열려 있는 **워크스페이스**(전환) + 주요 **커맨드**(Diff Review / Bot / Harness /
WebDev / Scrap / Note). 마우스로 ActivityBar를 오가지 않고 키보드로 즉시 이동합니다.

---

## 11. 에이전트 상태 감지 🚦

호스팅한 코딩 에이전트(Claude/Codex 등)의 **상태를 실시간 감지**해서, 여러 에이전트 중
**누가 나를 기다리는지** 바로 보이게 합니다.

- **SESSIONS 상태 칩** — 세션 목록 각 행에 감지 상태가 색상 칩으로:
  🔴 `blocked`(승인/입력 대기) · 🟡 `working`(생성 중) · 🔵 `done`(끝났는데 아직 확인 안 함) ·
  ⚪ `idle`(대기). blocked/미확인 done은 굵게 강조.
- **타이틀바** — `AgentZero Lite ● N need attention` (주의 필요 개수).
- **작업표시줄 플래시** — 에이전트가 새로 blocked/done되면 창이 비활성일 때 깜빡여 알림.

```powershell
& $AZ -cli agent-state
#  Agents needing attention: 1
#    [5:0] blocked  * Claude ←     ← 승인 대기 (별표 = 아직 확인 안 함)
```

**어떻게 동작하나**: 훅이 없는 CLI도 커버합니다. 터미널 화면을 규칙(매니페스트)으로 읽어
상태를 판정합니다. 규칙은 **데이터라 튜닝 가능** —
`%LOCALAPPDATA%\AgentZeroLite\agent-detection\<agent>.json` 을 두면 빌드 없이 규칙을
덮어씁니다(claude/codex/generic).

**상태까지 대기** (스크립트/에이전트용):
```powershell
& $AZ -cli terminal-wait 0 1 --until blocked --agent claude   # 상대가 승인 대기할 때까지
& $AZ -cli terminal-wait 0 1 --until idle                     # 작업 끝날 때까지
```

**대화 복원** — 폴더의 마지막 Claude 대화를 찾아 복원 커맨드를 출력:
```powershell
& $AZ -cli agent-resume-cmd "C:\code\myproj"   # 폴더로 직접
& $AZ -cli agent-resume 5 0                     # 탭(그룹5 탭0)의 워크스페이스에서 자동 발견
#  claude --resume 8151ecda-83b1-450d-...
```
> 실행 중 터미널을 **자동 재시작하지 않습니다**(안전). `--resume`은 같은 대화를 복원하므로,
> 준비되면(예: 에이전트 종료 후) 위 커맨드를 직접 실행하세요.

---

## CLI 명령 요약

| 명령 | 설명 | GUI 필요 |
|---|---|---|
| `cost` | 토큰 사용 → 비용 추정 | ✕ |
| `worktree <list\|add\|remove>` | git worktree 관리 (`add ... --trust`) | ✕ |
| `orchestrate <list\|create\|status>` | 감독 실행 생성/조회 | ✕ |
| `orchestrate run <id>` | 실행 — 터미널 에이전트에 배분·감독 | ○ |
| `automation <create\|list\|remove\|due>` | 예약 자동화 (every/hourly/daily) | ✕ |
| `agent-state` | 터미널별 감지 상태 + 주의 롤업 | ○ |
| `terminal-wait <g> <t> --until <state>` | 특정 상태(working/blocked/idle/done)까지 대기 | ○ |
| `agent-resume-cmd [cwd]` | 폴더의 최신 Claude 대화 resume 커맨드 출력 | ✕ |
| `agent-resume <g> <t>` | 탭의 워크스페이스에서 세션 자동 발견 → resume 커맨드 | ○ |
| `help [topic]` | 가이드 서빙(agentzero/orchestrate) | ✕ |
| `trust-workspace [경로]` | 폴더를 에이전트 CLI에 신뢰 등록 | ✕ |
| `agent-hook-install` / `-uninstall` | 상태 훅 설치/제거 | ✕ |
| `skill-stub-install` / `-uninstall` | 사용법 스텁 설치/제거 | ✕ |
| `terminal-list` | 터미널 그룹/탭 목록 | ○ |
| `terminal-read <g> <t> [--last N]` | 터미널 출력 읽기 | ○ |
| `terminal-send <g> <t> "<텍스트>"` | 터미널에 입력 전송 | ○ |
| `terminal-wait <g> <t> [--idle-ms N]` | 터미널 유휴(완료)까지 대기 | ○ |

---

## 검증 / 문제 해결

- **자동 E2E**: `pwsh Test/e2e/run-all.ps1` (CLI+GUI) / `-SkipGui`(CLI만).
  기능이 정상이면 `E2E PASSED`. 결과·스크린샷은 `Test/e2e/_artifacts/`.
- **로그**: `Project\AgentZeroWpf\bin\Debug\net10.0-windows\logs\app-log.txt`
  - 파일 도구: `[AIMODE] read_file/grep root="..." path="..."`
  - Diff 리뷰: `[DiffReview]`
- **"파일을 찾을 수 없음"**: 파일명 확장자 확인 + **활성 워크스페이스**가 대상 폴더가
  맞는지 확인(로그의 `root="..."`).
- **터미널 명령 무반응**: GUI가 실행 중인지 확인. 최신 기능은 앱을 **재시작**해야
  반영됩니다.
