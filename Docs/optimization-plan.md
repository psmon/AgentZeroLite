# AgentZeroLite .NET 최적화 계획서

**작성일:** 2026-05-01  
**완료일:** 2026-05-01  
**대상:** AgentZeroLite (.NET 10, WPF + Akka.NET)  
**범위:** ZeroCommon 라이브러리 + AgentZeroWpf 호스트  
**상태:** 전체 완료 (11개 항목 중 9개 적용, 2개 검토 후 스킵)

---

## 목차

1. [개요](#1-개요)
2. [HIGH — 우선 수정](#2-high--우선-수정)
3. [MEDIUM — 개선 권장](#3-medium--개선-권장)
4. [LOW — 기회되면 개선](#4-low--기회되면-개선)
5. [수정 불필요 (양호)](#5-수정-불필요-양호)
6. [작업 순서 및 검증 방법](#6-작업-순서-및-검증-방법)

---

## 1. 개요

전체 코드베이스(200+ C# 파일, 5개 프로젝트)를 분석한 결과,
**HIGH 3건 · MEDIUM 5건 · LOW 3건**의 최적화 기회를 확인했다.

전반적으로 아키텍처가 잘 설계되어 있으며(HttpClient 정적 재사용, 버퍼 재활용, Actor 단일 스레드 보장 등),
대부분의 이슈는 **불필요한 컬렉션 복사**에 집중되어 있다.

---

## 2. HIGH — 우선 수정

### H-1. LLM 요청시 `_history.ToList()` 제거 — O(n²) 할당 [완료]

| 항목 | 내용 |
|------|------|
| **위치** | `ExternalChatSession.cs:56` |
| **현상** | `SendAsync`/`SendStreamAsync` 호출마다 `_history.ToList()` 실행 |
| **영향** | 10턴 대화 = 10번의 리스트 복사, 총 55개 메시지 객체 할당 (1+2+...+10) |

```csharp
// 현재 (line 53-59)
var request = new LlmRequest
{
    Model = _model,
    Messages = _history.ToList(),   // ← 매번 전체 복사
    Temperature = _temperature,
    MaxTokens = _maxTokens,
};
```

**수정 방안:**

`LlmRequest.Messages` 타입을 `List<LlmMessage>` → `IReadOnlyList<LlmMessage>`로 변경하고,
복사 없이 직접 전달한다.

```csharp
// LlmRequest.cs
public IReadOnlyList<LlmMessage> Messages { get; init; }

// ExternalChatSession.cs
Messages = _history.AsReadOnly(),   // 래퍼만 생성, 복사 없음
```

---

### H-2. 동일 패턴 — `ExternalAgentToolLoop.cs` [완료]

| 항목 | 내용 |
|------|------|
| **위치** | `ExternalAgentToolLoop.cs:202` |
| **현상** | `_messages.ToList()` — H-1과 동일 패턴 |
| **영향** | Tool loop 재시도 시 매번 리스트 복사 반복 |

```csharp
// 현재 (line 199-205)
var request = new LlmRequest
{
    Model = _model,
    Messages = _messages.ToList(),  // ← 재시도마다 복사
    Temperature = _opts.Temperature,
    MaxTokens = _opts.MaxTokensPerTurn,
};
```

**수정:** H-1에서 `LlmRequest.Messages`를 `IReadOnlyList`로 변경하면 여기도 자동 해소.

```csharp
Messages = _messages.AsReadOnly(),
```

---

### H-3. Voice 버퍼를 `RecyclableMemoryStream` 풀링으로 전환 [완료]

| 항목 | 내용 |
|------|------|
| **위치** | `VoiceSegmenterFlow.cs:99-122` |
| **현상** | Pre-roll 시딩 시 `List<byte>.AddRange` 반복 호출, 완료 시 `ToArray()` 전체 복사 |
| **영향** | 음성 처리 핫 패스에서 재할당 + GC 부담 (발화마다 대형 byte[] 할당·해제 반복) |
| **패키지** | [`Microsoft.IO.RecyclableMemoryStream`](https://github.com/microsoft/Microsoft.IO.RecyclableMemoryStream) |

```csharp
// 현재 (line 98-104)
var capacityHint = (int)Math.Min(int.MaxValue,
    _maxPreRollBytes + BytesPerSecondMono16k16 * 8L);
_buffer = new List<byte>(capacityHint);
foreach (var chunk in _preRoll)
    if (!ReferenceEquals(chunk, frame.Pcm16k))
        _buffer.AddRange(chunk);       // AddRange #1 (반복)
_buffer.AddRange(frame.Pcm16k);        // AddRange #2

// 완료 시 (line 122)
var pcm = _buffer.ToArray();           // 전체 복사
```

**수정 방안:**

`RecyclableMemoryStream`은 내부 버퍼를 풀링하여 GC 압력을 대폭 줄인다.
`List<byte>` → `RecyclableMemoryStream`으로 교체하고,
완료 시 `GetReadOnlySequence()`로 무복사 접근한다.

**Step 1 — NuGet 패키지 추가:**

```bash
dotnet add Project/ZeroCommon/ZeroCommon.csproj package Microsoft.IO.RecyclableMemoryStream
```

**Step 2 — 싱글턴 매니저 선언** (프로세스 수명 동안 1개):

```csharp
// VoiceSegmenterFlow.cs 또는 공용 위치
private static readonly RecyclableMemoryStreamManager PoolManager = new();
```

**Step 3 — 버퍼 교체:**

```csharp
// 발화 시작 시 (기존 _buffer = new List<byte>(capacityHint))
_stream = PoolManager.GetStream(tag: "VoiceSegmenter", requiredSize: capacityHint);

// 프레임 쓰기 (기존 AddRange)
foreach (var chunk in _preRoll)
    if (!ReferenceEquals(chunk, frame.Pcm16k))
        _stream.Write(chunk);
_stream.Write(frame.Pcm16k);

// 이후 프레임 추가 (기존 _buffer!.AddRange)
_stream.Write(frame.Pcm16k);
```

**Step 4 — 발화 완료 시 데이터 추출:**

```csharp
// 완료 시 (기존 _buffer.ToArray())
var pcm = _stream.GetBuffer().AsSpan(0, (int)_stream.Length).ToArray();
var duration = pcm.Length / (double)BytesPerSecondMono16k16;
Push(_stage.Out, new PcmSegment(pcm, duration, _startedAt));
_stream.Dispose();   // 버퍼가 풀로 반환됨
_stream = null;
```

> **참고:** `PcmSegment`가 `ReadOnlyMemory<byte>`를 받을 수 있다면
> `GetBuffer()` + slice로 복사 없이 전달할 수 있으나,
> 다운스트림이 `byte[]`를 요구하므로 현재는 `ToArray()` 유지.
> 풀링 효과만으로도 GC 부담이 크게 줄어든다.

**기대 효과:**

| 항목 | `List<byte>` (현재) | `RecyclableMemoryStream` (변경 후) |
|------|---------------------|-----------------------------------|
| 발화마다 버퍼 할당 | 매번 `new byte[]` | 풀에서 대여·반환 |
| `AddRange` 내부 복사 | 용량 초과 시 재할당 | `Write`가 청크 단위로 연결 |
| GC Gen2 압력 | 85KB+ 버퍼가 LOH 진입 | 풀링으로 LOH 할당 최소화 |
| 완료 시 `ToArray()` | 전체 복사 (불가피) | 전체 복사 (불가피, 동일) |

---

## 3. MEDIUM — 개선 권장

### M-1. `OpenAiCompatibleProvider` 요청 직렬화 최적화 [완료]

| 항목 | 내용 |
|------|------|
| **위치** | `OpenAiCompatibleProvider.cs:154-161` |
| **현상** | `Dictionary<string, object>` + LINQ `.Select().ToList()` 로 요청 본문 구성 |
| **영향** | 매 API 호출마다 중간 Dictionary·List 할당 |

```csharp
// 현재
["messages"] = request.Messages.Select(m => new Dictionary<string, object>
{
    ["role"] = m.Role,
    ["content"] = m.Content,
}).ToList(),
```

**수정 방안:**

직렬화 전용 POCO 클래스를 도입하거나, `.ToList()` 대신 `IEnumerable`을 `JsonSerializer`에 직접 전달한다.
`System.Text.Json`은 `IEnumerable`을 지연 열거하므로 중간 리스트가 불필요하다.

```csharp
// .ToList() 제거 — JsonSerializer가 IEnumerable을 직접 열거
["messages"] = request.Messages.Select(m => new Dictionary<string, object>
{
    ["role"] = m.Role,
    ["content"] = m.Content,
}),  // ToList() 제거
```

---

### M-2. EF Core 읽기 전용 쿼리에 `AsNoTracking()` 추가 [완료]

| 항목 | 내용 |
|------|------|
| **위치** | `CliWorkspacePersistence.cs:121, 142` |
| **현상** | `LoadCliGroups()`, `LoadCliDefinitions()` — 읽기 전용인데 Change Tracker 활성 |
| **영향** | 엔티티 추적 오버헤드 (스냅샷 생성, identity map) |

```csharp
// 현재 (line 121)
return db.CliGroups
    .Include(group => group.Tabs)
    .ThenInclude(tab => tab.CliDefinition)
    ...

// 수정
return db.CliGroups
    .AsNoTracking()                         // ← 추가
    .Include(group => group.Tabs)
    .ThenInclude(tab => tab.CliDefinition)
    ...
```

두 메서드 모두 결과를 DTO(`CliGroupSnapshot`, `CliDefinitionSnapshot`)로 프로젝션하므로
추적이 전혀 불필요하다.

---

### M-3. `ConPtyTerminalSession.Dispose()` — Timer 정리 순서 및 블로킹 호출 [완료]

| 항목 | 내용 |
|------|------|
| **위치** | `ConPtyTerminalSession.cs:410-423` |
| **현상 1** | Timer를 `_cts.Cancel()` 보다 먼저 Dispose (line 415 vs 416) |
| **현상 2** | `_writeLoopTask.Wait()` 블로킹 호출 (line 419) |

```csharp
// 현재
_outputPollTimer.Dispose();    // ① Timer 먼저 해제
_cts.Cancel();                 // ② 그 다음 취소 신호
_writeChannel.Writer.TryComplete();
try { _writeLoopTask.Wait(TimeSpan.FromSeconds(1)); }  // ③ 블로킹
catch { }
```

**수정 방안:**

```csharp
_cts.Cancel();                                          // ① 먼저 취소 신호
_writeChannel.Writer.TryComplete();
_outputPollTimer.Change(Timeout.Infinite, Timeout.Infinite);  // ② 콜백 중단
_outputPollTimer.Dispose();                             // ③ 그 다음 해제
try { _writeLoopTask.Wait(TimeSpan.FromSeconds(1)); }
catch { }
_cts.Dispose();
```

`.Wait()`는 Dispose 컨텍스트에서 불가피하므로 유지하되,
타이머 정리 순서를 바로잡아 경합 조건을 방지한다.

---

### M-4. `AppLogger.cs` — 로그 크기 추적 최적화 [완료]

| 항목 | 내용 |
|------|------|
| **위치** | `AppLogger.cs:138` |
| **현상** | 매 로그 라인마다 `Encoding.UTF8.GetByteCount(entry)` 호출 |
| **영향** | 문자열 전체 스캔 — 로깅 처리량 저하 |

```csharp
// 현재
_fileSizeEstimate += Encoding.UTF8.GetByteCount(entry) + 2;
```

**수정 방안:**

대부분의 로그가 ASCII 위주이므로 `entry.Length`로 근사치를 사용한다.
정확도가 필요하면 `entry.Length * 1.1` 정도의 오버카운트가 안전하다.

```csharp
// ASCII 기반 근사 — 정확도보다 성능 우선
_fileSizeEstimate += entry.Length + 2;
```

---

### M-5. `AppLogger.cs` — 로그 로테이션 I/O 최적화 (**추후 수정 예정**)

| 항목 | 내용 |
|------|------|
| **위치** | `AppLogger.cs:153-155` |
| **현상** | `File.ReadAllLines()` → 슬라이스 → `File.WriteAllLines()` — 전체 파일 두 번 읽고 쓰기 |
| **비고** | 제작자 의도에 의한 현행 구현. 추후 별도 일정으로 수정 예정 |

---

## 4. LOW — 기회되면 개선

### L-1. `AgentBotActor` — 불필요한 `ToList()` [완료]

| 항목 | 내용 |
|------|------|
| **위치** | `AgentBotActor.cs:172` |
| **현상** | `_activeConversations.ToList()` — Actor 내부 HashSet을 매 쿼리마다 복사 |

**수정:** `ActiveConversationsReply`가 `IReadOnlyCollection<string>`을 받도록 변경.

---

### L-2. `VoiceCaptureService` — 이벤트 핸들러 일관성 [스킵]

| 항목 | 내용 |
|------|------|
| **위치** | `VoiceCaptureService.cs:219, 279, 309` |
| **현상** | `AmplitudeChanged?.Invoke(rms)` vs `FrameAvailable is not null` — null 체크 패턴 불일치 |
| **결론** | `FrameAvailable`은 `Muted` 조건과 결합된 의도적 패턴. 나머지 이벤트는 이미 `?.Invoke()` 사용. 변경 불필요.

---

### L-3. `CliWorkspacePersistence` — 내부 `.ToList()` 중복 [스킵]

| 항목 | 내용 |
|------|------|
| **위치** | `CliWorkspacePersistence.cs:135` |
| **현상** | Select 결과를 `.ToList()`로 변환 후 바깥에서도 `.ToList()` — 이중 변환 |
| **결론** | 내부 `.ToList()`는 EF Core LINQ-to-Entities가 서브컬렉션을 구체화하기 위해 필수. 제거 시 런타임 오류 위험. 변경 불필요.

---

## 5. 수정 불필요 (양호)

| 항목 | 위치 | 이유 |
|------|------|------|
| `ConfigureAwait(false)` 미사용 | `ZeroCommon` 전체 | 제작자 의도에 의한 설계 |
| `HttpClient` 정적 재사용 | `LlmModelDownloader.cs:7` | 권장 패턴 준수 |
| 다운로드 스트리밍 64KB 버퍼 | `LlmModelDownloader.cs:38` | 최적 크기 |
| Actor 상태 접근 패턴 | `TerminalActor.cs:63-66` | 단일 스레드 보장 |
| `AgentReactorActor` 클로저 | `AgentReactorActor.cs:98-101` | 최소 캡처, 의도적 |
| ContinueWith + TaskScheduler.Default | `ConPtyTerminalSession.cs` | 올바른 스케줄러 지정 |
| VoiceSegmenter 용량 힌트 | `VoiceSegmenterFlow.cs:98-100` | 이미 `capacityHint` 사전 계산 |

---

## 6. 작업 순서 및 검증 방법

### 작업 순서 (전체 완료)

```
Phase 1: 타입 변경                    [완료]
  H-1 + H-2  LlmRequest.Messages → IReadOnlyList 변경
  M-1        OpenAiCompatibleProvider ToList 제거

Phase 2: 리소스 정리                   [완료]
  M-3        ConPtyTerminalSession Timer/Dispose 순서
  M-4        AppLogger 크기 추적
  M-5        로그 로테이션 (추후 수정 예정)

Phase 3: EF Core                      [완료]
  M-2        AsNoTracking 추가

Phase 4: 나머지                        [완료]
  H-3        RecyclableMemoryStream 전환
  L-1        AgentBotActor ToList 제거
  L-2        VoiceCaptureService (스킵 — 의도적 패턴)
  L-3        CliWorkspacePersistence (스킵 — EF Core 필수)
```

### 검증 방법

| 단계 | 명령 | 확인 사항 |
|------|------|----------|
| 빌드 | `dotnet build Project/AgentZeroWpf/AgentZeroWpf.csproj` | 컴파일 오류 없음 |
| 단위 테스트 | `dotnet test Project/ZeroCommon.Tests/ZeroCommon.Tests.csproj` | 기존 테스트 통과 |
| WPF 테스트 | `dotnet test Project/AgentTest/AgentTest.csproj` | Actor·ConPTY 테스트 통과 |
| 수동 검증 | GUI 실행 → 터미널 탭 열기 → LLM 대화 | 기능 정상 동작 확인 |

### 롤백 기준

- 빌드 실패 시 해당 Phase 전체 revert
- 테스트 실패 시 해당 항목만 revert 후 원인 분석
- 성능 퇴보 시 (체감 지연 증가) 해당 Phase revert

---

## 변경 영향 요약

| Phase | 변경 파일 수 | 위험도 | 영향 범위 |
|-------|------------|--------|----------|
| Phase 1 | ~5 | 중 | LLM 요청 경로 전체 |
| Phase 2 | 2 | 저 | 종료 경로, 로깅 |
| Phase 3 | 1 | 저 | DB 읽기 경로 |
| Phase 4 | 4 | 저 | 음성·Actor·이벤트 |
