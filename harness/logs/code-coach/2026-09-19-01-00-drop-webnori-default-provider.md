---
date: 2026-09-19T01:00:00+09:00
agent: code-coach
type: review
mode: execution
trigger: "pre-commit-review (staged *.cs / *.xaml)"
---

# 번들 Webnori a1/a2 프로바이더 제거 — 커밋 전 리뷰

## 실행 요약

운영자 요청으로 `a1.webnori.com` / `a2.webnori.com`을 **완전 제거**하고 기본 외부 프로바이더를
**Ollama**로 바꿨다. 두 호스트가 비공개 전환되므로, 소스에 박혀 있던 번들 테스트 키도 함께 뺐다.
히스토리는 그대로 둔다(운영자 지시).

## 결과

### 확인했고 문제 없음

- **번들 키 잔존 없음** — `grep -rn "sk-lm-AbUGwC6s"` 작업 트리에서 0건.
- **엔드포인트 잔존 없음** — `a1.webnori`/`a2.webnori`가 `Project/` 코드에 0건.
- **인덱스 매핑 정합** — WPF `SettingsPanel.xaml`의 ComboBoxItem 순서(Ollama·OpenAI·LMStudio)와
  `ProviderFromIndex`/`SelectedIndex` switch가 0·1·2로 일치. 재인덱싱은 이런 곳에서 어긋나기 쉬워 양쪽을 대조했다.
- **Avalonia 기본값 = `All[0]`** — `Providers`가 `ExternalProviderNames.All`(Ollama 우선)이고 기본/폴백도 Ollama라 일관.
- **모르는 프로바이더 처리** — 구 설정 파일의 `"Webnori"`는 `CreateExternalProvider()`가 null을 돌려
  `OpenExternalSession`이 *"Unknown external provider 'Webnori'."* 로 실패하고, `IsActiveAvailable()`은 false를
  돌려 AI 모드가 제안되지 않는다. **크래시 없이 설정으로 유도**된다 — 운영자가 마이그레이션을 원치 않았으므로 이 경로가 최종 동작이다.

### 수정한 것

1. **깨진 `<see cref>` 2건** — `Voice/WavWriter.cs`가 삭제된 `WebnoriGemmaStt`를,
   `ZeroCommon.Tests/ExternalAgentLoopTests.cs`가 삭제된 `WebnoriExternalSmokeTests`를 가리키고 있었다.
2. **죽은 호스트를 안내하던 사용자 메시지** — `AgentBotWindow.xaml.cs:1202`가 non-Gemma 모델일 때
   *"Switch to Webnori / google/gemma-4-e4b"* 라고 안내했다. 존재하지 않는 곳으로 보내는 문구라
   "provider를 Gemma 4 모델로 맞추라"로 바꿨다.
3. **죽은 코드** — `SettingsPanel.Voice.cs`의 `RestoreOrPickFirst`는 유일한 호출처(Webnori 모델 Refresh 핸들러)가
   사라져 고아가 됐다. 제거. (`PopulateModelDropdown`·`PreloadSingleItem`은 다른 호출처가 있어 유지.)

### 손대지 않은 것 (의도)

- **역사적 주석** — `GemmaNativeToolCall`의 *"observed against WebnoriA2"*, `LocalAgentLoop`의 타임아웃 근거 등은
  **코드가 왜 그렇게 생겼는지의 기록**이다. 지우면 근거만 사라진다.
- **`Docs/agent-origin/*`** — 2026-04-27 스냅샷이라고 명시된 문서. 스냅샷은 스냅샷대로 둔다.
- **`.pen` 설계 파일 2개**(`design.pen`, `M0023-architecture.pen`)에 `Webnori` 문자열이 남아 있다.
  암호화 파일이라 `Read`/`Grep`/직접 편집 금지 — pencil MCP로 여는 별도 작업이다. **후속 항목**.
- **`@webnori/pdsa`(npm), `webos.webnori.com`, `mcp.webnori.com`** — 이름만 겹치는 별개 건.

## 평가

| 축 | 등급 |
|---|---|
| 관용성 | A — 기존 per-provider 슬롯 구조를 그대로 쓰고 특수 분기만 걷어냈다 |
| 결합/시임 존중 | A — ZeroCommon은 여전히 WPF-free, 두 호스트가 같은 `ExternalProviderNames.All`을 본다 |
| 실패 모드 | A− — 구 설정의 폴백 경로를 실제로 따라가 확인했다. 다만 자동 테스트는 없다(운영자 스모크) |
| 명명 | A |

## 다음 단계 제안

- `.pen` 설계 파일의 Webnori 표기 갱신 (pencil MCP 필요).
- 신규 설치 첫 실행 경험: Ollama 미설치 사용자는 연결 실패를 보게 된다. 설정 화면의 힌트 문구가
  "Ollama를 먼저 켜라"를 충분히 안내하는지 **운영자 확인** 권장.
