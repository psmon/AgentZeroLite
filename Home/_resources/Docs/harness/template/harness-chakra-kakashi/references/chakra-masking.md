# 🔒 차크라 로그 마스킹 정책

> 토큰 감사는 세션 기록을 읽지만, **기록의 내용은 차크라 감사의 대상이 아니다**. 숫자와 패턴만 남기고 원문은 마스킹한다.

---

## 1. 왜 마스킹인가

JSONL 트랜스크립트는 평문이다. 사용자가 붙여넣은 코드·API 키·파일 경로·채팅 로그 등이 그대로 들어 있다. 이를 감사 로그에 복사해 남기면:

- 감사 로그가 개인정보 저장소가 되어버린다.
- 깃/슬랙/노션에 공유되면 유출 경로가 된다.
- 모델이 재학습하거나 요약할 때 재노출될 수 있다.

**원칙**: 차크라 감사의 관심사는 **토큰 수치와 도구 사용 패턴**이다. 원문 텍스트는 감사 대상이 아니다.

---

## 2. 기록 금지 (절대)

- 사용자 메시지 원문, 어시스턴트 응답 원문
- 파일 경로 중 사용자 식별 부분 (`C:\Users\<name>\...` → `<user-home>`)
- 비밀번호, API 키, 토큰, 세션 쿠키 (부분이라도)
- 환경 변수 값 (이름은 OK, 값은 금지)
- 채팅 앱에서 캡처된 대화 내용
- 프로젝트 비공개 경로 (`D:\Private\...` 등)
- 사용자명, 이메일, 전화번호, 친구명

## 3. 기록 허용

- **토큰 수치**: `cache_read_input_tokens`, `cache_creation_input_tokens`, `output_tokens` (그대로 OK)
- **도구 이름 + 횟수**: `Read x12`, `Bash x3`, `Edit x5`
- **모델 식별자**: `claude-sonnet-4-6`, `claude-haiku-4-5`
- **서브에이전트 타입**: `Explore`, `general-purpose`, `claude-code-guide`
- **소요 시간**: 초 단위
- **패턴 분류**: `cache-busting 감지`, `재독 3회 감지`
- **파일 확장자 분포**: `.cs x8, .md x2` (경로는 제외)

## 4. 마스킹 규칙

| 원문 유형 | 치환 |
|-----------|------|
| 파일 경로 | `<path>` 또는 확장자만 (`<*.cs>`) |
| 사용자 발화 | `<user-msg len=N>` (길이만) |
| 어시스턴트 응답 | `<assistant-msg len=N>` |
| 도구 인자 (검색어 등) | `<tool-arg>` |
| 비밀 추정 문자열 | `<secret>` |
| 이메일 | `<email>` |
| 사람 이름으로 보이는 값 | `<person>` |

## 5. JSONL 파싱 시 주의

JSONL 트랜스크립트에서 필요한 것은 **`usage` 객체만**이다. `content` 배열의 텍스트는 건드리지 않는다.

```python
# 권장 패턴 (의사 코드)
for line in jsonl:
    msg = json.loads(line)
    usage = msg.get("usage")
    if usage:
        aggregate.append({
            "role": msg.get("role"),
            "cache_read": usage.get("cache_read_input_tokens", 0),
            "cache_create": usage.get("cache_creation_input_tokens", 0),
            "output": usage.get("output_tokens", 0),
        })
    # content 배열은 읽지 않음 — 원문 유출 방지
```

도구 호출 패턴을 보기 위해 `tool_use` 블록을 참조할 때도 `name` 필드만 취하고 `input`은 무시한다.

## 6. ccusage 사용 시

`npx ccusage session --json`의 출력은 이미 집계된 수치이므로 안전하다. 단, `--format full`로 원문이 포함되는 옵션은 사용하지 않는다.

## 7. 감사 로그 예시

**나쁜 예** (원문 포함):
```markdown
- 사용자: "D:\work\secret.cs 파일 봐줘"
- Read(D:\work\secret.cs) → 200 lines
```

**좋은 예** (마스킹):
```markdown
- Read x1 (.cs 파일, 200 lines 범위)
- 캐시 히트율: 82% (A)
- 총 토큰: 45k cache_read + 12k cache_create + 3k output
```

## 8. 위반 시 조치

감사 로그에 원문이 남은 것이 사후 발견되면:
1. 즉시 해당 로그 파일 삭제
2. 동일 경로로 마스킹 버전 재작성
3. 경향성 분석을 위해 위반 사례를 본 문서 하단에 **유형만** 기록 (어떤 패턴에서 유출되었는지)

---

## 부록: 위반 사례 기록

| 일자 | 유형 | 원인 | 재발 방지 |
|------|------|------|----------|
| — | — | — | — |
