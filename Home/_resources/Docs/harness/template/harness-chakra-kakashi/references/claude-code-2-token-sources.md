# 📚 클로드코드 2.0 토큰 관측 소스 레퍼런스

> 차크라 카카시가 평가에 활용할 수 있는 **실제 관측 가능한** 토큰 소스 목록.
> 로드맵/미지원 항목이 아니라 현재(2026년 기준) 파싱 가능한 것만.

---

## 1. 소스 요약표

| 소스 | 접근 방식 | 정확도 | 실시간 | 비고 |
|------|----------|--------|--------|------|
| `/cost` 슬래시 커맨드 | 사용자에게 실행 요청 | 추정치 (클라이언트 계산) | 현재 세션 누적 | Pro/Max는 `/stats` |
| `/context` 슬래시 커맨드 | 사용자에게 실행 요청 | 정확 | 현재 턴 스냅샷 | 누적 아님 |
| JSONL 트랜스크립트 | 파일 직접 읽기 | 캐시 필드만 신뢰 | 모든 메시지 기록 | **`input_tokens`는 불신** |
| `ccusage` CLI | `npx ccusage` 실행 | 집계된 수치 | 일/월/세션 | 커뮤니티 도구 |
| `/stats` (Pro/Max) | 사용자에게 실행 요청 | 구독자 전용 | 5시간 블록 | — |
| 하네스 엔진 자체 회상 | 현재 대화 맥락 | 도구 호출 횟수 정확 | 이번 세션 | 토큰 숫자는 불가 |

---

## 2. JSONL 트랜스크립트 세부

**경로**: `~/.claude/projects/<project_hash>/<session_id>.jsonl`

Windows: `C:\Users\<user>\.claude\projects\<project_hash>\<session_id>.jsonl`

**한 줄 = 한 JSON 메시지**. 어시스턴트 메시지에만 `usage` 블록이 있다.

```json
{
  "type": "assistant",
  "message": {
    "role": "assistant",
    "model": "claude-sonnet-4-6",
    "content": [...],
    "usage": {
      "input_tokens": 2,
      "output_tokens": 1250,
      "cache_creation_input_tokens": 18240,
      "cache_read_input_tokens": 45000,
      "cache_creation": {
        "ephemeral_5m_input_tokens": 0,
        "ephemeral_1h_input_tokens": 18240
      }
    }
  }
}
```

### 신뢰도

| 필드 | 신뢰 | 이유 |
|------|------|------|
| `cache_read_input_tokens` | ✅ | 최종값으로 기록됨 |
| `cache_creation_input_tokens` | ✅ | 최종값 |
| `output_tokens` | ⚠️ | 일부 케이스에서 과소계측 (10~17x) — 주의 |
| `input_tokens` | ❌ | 스트리밍 플레이스홀더. 대부분 0 또는 1. 100x 과소계측 |

**결론**: 세션 총 토큰 계산 시 `cache_read + cache_creation + output`만 쓴다. `input_tokens`는 버린다.

### 파싱 의사 코드

```python
import json
from pathlib import Path

def read_session_usage(jsonl_path: Path):
    total_read = 0
    total_create = 0
    total_output = 0
    model_counts = {}
    for line in jsonl_path.read_text(encoding='utf-8').splitlines():
        try:
            msg = json.loads(line)
        except json.JSONDecodeError:
            continue
        m = msg.get("message") or {}
        usage = m.get("usage")
        if not usage:
            continue
        total_read += usage.get("cache_read_input_tokens", 0) or 0
        total_create += usage.get("cache_creation_input_tokens", 0) or 0
        total_output += usage.get("output_tokens", 0) or 0
        model = m.get("model", "unknown")
        model_counts[model] = model_counts.get(model, 0) + 1
    hit_ratio = total_read / (total_read + total_create) if (total_read + total_create) else 0
    return {
        "cache_read": total_read,
        "cache_create": total_create,
        "output": total_output,
        "hit_ratio": hit_ratio,
        "models": model_counts,
    }
```

---

## 3. ccusage CLI

**설치 불필요** — `npx ccusage` 로 바로 실행.

| 명령 | 출력 |
|------|------|
| `npx ccusage daily --json` | 일일 집계 (cache_read, cache_create, output, cost) |
| `npx ccusage session --json` | 세션별 집계 |
| `npx ccusage monthly --json` | 월 단위 |
| `npx ccusage blocks --json` | 5시간 롤링 블록 (Pro/Max 플랜용) |

**추천 옵션**:
- `--since YYYYMMDD --until YYYYMMDD` — 기간 필터
- `--json` — 프로그램 친화적 출력

**보안 주의**: `--format full` 류의 원문 포함 옵션은 **사용하지 않는다**. 집계 수치만 가져온다.

---

## 4. /cost 출력 해석

```
Total cost:            $1.2345
Total duration (API):  5m 12s
Total duration (wall): 27m 4s
Total code changes:    +428 lines, -102 lines
Token usage by model:
  claude-sonnet-4-6    input: 1.2k, output: 14k, cache write: 82k, cache read: 340k
```

차크라 감사 시 주목할 값:
- **cache read : cache write 비율** — 70%+ 목표
- **API 시간 vs wall 시간** — API 시간이 wall의 50% 초과면 대화가 LLM 바운드
- **모델 분포** — Opus 비율이 높으면 인술 선택 점검

---

## 5. /context 출력 해석

현재 컨텍스트 구성 (비율) 스냅샷:
- System prompt
- CLAUDE.md (프로젝트/유저 지시문)
- Memory files
- Skills
- MCP servers
- Tools
- Conversation history

**위험 시그널**:
- CLAUDE.md가 전체의 > 30% → 슬림화 필요
- Skills 섹션이 > 25% → 불필요 스킬 unload 검토
- Conversation > 70% → 압축 근접

---

## 6. 서브에이전트 토큰 추적

서브에이전트(`Agent` 도구 호출)는 **별도 컨텍스트**로 실행된다. 부모 JSONL에는 **결과만** 기록되고, 서브에이전트의 JSONL은 별도 파일에 저장된다.

**추적 한계**:
- ccusage는 서브에이전트 토큰을 부모 세션에 집계하지 못할 수 있다 (도구 버전에 따라 다름).
- 정확한 총량이 필요하면 `~/.claude/projects/<hash>/` 하위 모든 JSONL을 시간대로 묶어 집계.

**실용적 근사**: 서브에이전트 호출 수와 각 호출의 `<usage>` 블록 (Agent 도구 결과에 포함) 합산.

---

## 7. 관측 불가능한 항목

다음은 **현재 관측 불가**하므로 평가에서 제외한다:

- Hooks(PreToolUse/PostToolUse/Stop)는 토큰 수치를 받지 못함
- SessionEnd 이벤트에도 요약 없음
- 스킬별 자동 귀속(attribution)은 네이티브 지원 없음 — 휴리스틱 추정만 가능
- 실시간 비용 스트리밍 없음 (턴 단위 기록)

---

## 8. 평가 신호 매핑

| 관측 소스 | 매핑되는 평가 축 |
|----------|----------------|
| JSONL `cache_read/create` 합계 | 차크라 총량 (E1~E5) |
| JSONL hit ratio | 순환 효율 (A~D) |
| 도구 호출 패턴 (대화 회상) | 술법 정밀도 (✓/✗) |
| JSONL `model` 필드 분포 | 인술 선택 (1~5) |
| `Agent` 도구 호출 수 | 분신술 절제 (Pass/Warn/Fail) |
