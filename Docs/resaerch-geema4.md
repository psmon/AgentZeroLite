# Gemma 4 온디바이스 탑재 조사 & 구현 기록

작성일 2026-04-23. AgentZero Lite / `Project/ZeroCommon/Llm` 모듈의 배경 및 근거 문서.

## 1. 배경 / 문제 정의

- 요구사항: **.NET에서 Gemma 4를 HTTP 서버 없이 in-process로 추론**. 온디바이스/설치형.
- 선행 실패 사례: 2026-04-18경 `LLamaSharp` NuGet + 공식 `LLamaSharp.Backend.*` 네이티브로 시도 → `ggml.dll` 심볼 충돌(Whisper.net이 자체 ggml을 번들함. 먼저 로드된 쪽이 두 번째를 부숴서 한 쪽이 반드시 깨짐). 이 시점 결론은 "LLamaSharp 제거, Ollama HTTP로 대체" 였음.
- 재도전 배경: Gemma 4(2026-04-02 공개) 함수콜 성능을 활용하려면 **인프로세스 + DLL 로드 경로를 우리가 제어**하면 된다는 가설. 즉 NuGet 번들 네이티브를 쓰지 말고 **자체 빌드 DLL을 명시 경로로 주입**.

## 2. 조사 타임라인 — 왜 공식 경로가 안 되는가

| 날짜 | 이벤트 |
|---|---|
| 2026-02-15 | `LLamaSharp` 0.26.0 NuGet 릴리스. 번들된 네이티브의 llama.cpp 커밋 `506bb6e0`, Gemma 3n까지만 지원. **Gemma 4는 이 시점 이전에 존재하지 않았음**. |
| 2026-04-02 | Google, Gemma 4 공개 (E2B / E4B / 26B-A4B / 31B). 같은 날 llama.cpp 본류에 PR [#21309](https://github.com/ggml-org/llama.cpp/pull/21309) 병합 (ngxson). |
| 2026-04-03 | `onnxruntime-genai` [issue #2062](https://github.com/microsoft/onnxruntime-genai/issues/2062) — Gemma 4 지원 불가. PLE(Per-Layer Embeddings) / Variable Head Dimensions / KV Cache Sharing 세 가지 아키텍처 변경 때문에 기존 Gemma 3 파이프라인 재사용 실패. MS 공식 .NET 경로는 **현재 사용 불가**. |
| 2026-04-17 | `LLamaSharp` [PR #1356](https://github.com/SciSharp/LLamaSharp/pull/1356) 제목이 "Qwen3.5 Support"에서 "Qwen3.5/Gemma4 Support"로 변경. 서브모듈을 llama.cpp `3f7c29d`로 업데이트. |
| 2026-04-17 | `LLamaSharp` [PR #1371](https://github.com/SciSharp/LLamaSharp/pull/1371) 병합 — "unknown model architecture" 에러에 대한 FAQ 추가. 해결책 #3: **자체 llama.cpp 빌드 + `NativeLibraryConfig.All.WithLibrary()` 주입**. |
| 2026-04-19 | `LLamaSharp` PR #1356 master 병합. **NuGet 릴리스는 이 시점에도 여전히 0.26.0**에 머무름. |

즉 "0.26.0 NuGet + 공식 Backend 패키지"로 Gemma 4 GGUF를 로드하면 `unknown model architecture: 'gemma4'` 에러가 나는 것이 정상.

## 3. 해결 전략

1. llama.cpp mainline을 **특정 커밋**(`3f7c29d`)에서 직접 빌드 → LLamaSharp 0.26.0이 P/Invoke로 기대하는 ABI와 정확히 일치 + Gemma 4 지원 포함.
2. 자체 `llama.dll` + `ggml*.dll`을 프로젝트가 관리하는 경로(`runtimes/{variant}/native/`)에 둔다.
3. 애플리케이션 기동 시 `NativeLibraryConfig.All.WithLibrary(path, null)`로 **명시 주입** → LLamaSharp는 이 DLL만 쓰고 NuGet 번들 Backend 패키지는 참조도 하지 않음.
4. Whisper.net과 같은 다른 ggml 바인딩 라이브러리와 공존: **프로세스 내 ggml 심볼은 먼저 로드된 쪽이 이김**. Whisper.net 쪽은 자체 DLL 경로를 유지, 우리 쪽은 위 명시 주입 경로. 둘이 각자의 DLL을 쓰도록 격리.

왜 커밋 `3f7c29d`인가: LLamaSharp PR #1356이 정확히 이 커밋으로 서브모듈을 올렸다. 즉 **"0.26.0 P/Invoke 시그니처와 ABI 일치"가 검증된 유일한 llama.cpp 커밋**. master 최신을 쓰면 빠르지만 ABI가 어긋나면 런타임에 엔트리 포인트 실종/시그니처 불일치 오류가 뜬다.

## 4. 빌드 절차

작업 디렉토리: `D:\Code\AI\GemmaNet\`.

### 사전 요구

- Visual Studio 2022 (VS 내장 CMake/MSVC 사용). 이 머신은 Professional 설치 확인.
- Git (llama.cpp clone용).
- GPU 빌드용: **Vulkan SDK 1.4.341.1** (`winget install --id KhronosGroup.VulkanSDK`로 설치 완료. CUDA Toolkit은 쓰지 않음 — 이식성 이유).

### 소스 준비

```bash
git clone --depth 1 --no-checkout https://github.com/ggml-org/llama.cpp.git D:/Code/AI/GemmaNet/llama.cpp
cd D:/Code/AI/GemmaNet/llama.cpp
git fetch --depth 1 origin 3f7c29d318e317b63f54c558bc69803963d7d88c
git checkout 3f7c29d318e317b63f54c558bc69803963d7d88c
# HEAD: "ggml: add graph_reused (#21764)"
```

### CPU 빌드 (AVX2 + FMA + F16C 베이스라인)

```bash
CMAKE="/c/Program Files/Microsoft Visual Studio/2022/Professional/Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe"

"$CMAKE" -B build -S . -G "Visual Studio 17 2022" -A x64 \
  -DBUILD_SHARED_LIBS=ON \
  -DGGML_NATIVE=OFF \
  -DLLAMA_CURL=OFF \
  -DLLAMA_BUILD_TESTS=OFF \
  -DLLAMA_BUILD_EXAMPLES=OFF \
  -DLLAMA_BUILD_TOOLS=OFF

"$CMAKE" --build build --config Release --target llama -j
```

`GGML_NATIVE=OFF`는 중요. 기본값(`ON`)은 빌드 머신 CPU에 맞춰 가장 높은 AVX-512/AMX까지 뽑는다. 배포 시 타 머신에서 `Illegal instruction`이 뜰 수 있어서 AVX2 수준에서 끊는다.

출력: `build/bin/Release/` 안에 4개 DLL.

### Vulkan 빌드 (GPU)

```bash
export VULKAN_SDK="C:/VulkanSDK/1.4.341.1"

"$CMAKE" -B build-vulkan -S . -G "Visual Studio 17 2022" -A x64 \
  -DBUILD_SHARED_LIBS=ON \
  -DGGML_NATIVE=OFF \
  -DGGML_VULKAN=ON \
  -DLLAMA_CURL=OFF \
  -DLLAMA_BUILD_TESTS=OFF \
  -DLLAMA_BUILD_EXAMPLES=OFF \
  -DLLAMA_BUILD_TOOLS=OFF \
  -DVulkan_INCLUDE_DIR="$VULKAN_SDK/Include" \
  -DVulkan_LIBRARY="$VULKAN_SDK/Lib/vulkan-1.lib"

"$CMAKE" --build build-vulkan --config Release --target llama -j
```

cmake configure 단계에서 다음이 뜨면 성공:

```
-- Vulkan found
-- GL_KHR_cooperative_matrix supported by glslc
-- GL_NV_cooperative_matrix2 supported by glslc
-- Including Vulkan backend
```

출력: `build-vulkan/bin/Release/` 안에 5개 DLL. `ggml-vulkan.dll`이 ~59MB로 큰 이유는 **GLSL 셰이더가 컴파일되어 DLL에 임베디드**되기 때문.

## 5. 산출물 구조

`Project/ZeroCommon/runtimes/` 아래로 커밋 관리.

```
runtimes/
├── win-x64-cpu/native/           (3.5 MB 합계)
│   ├── llama.dll            2.0 MB
│   ├── ggml.dll             67 KB
│   ├── ggml-base.dll        614 KB
│   └── ggml-cpu.dll         878 KB
└── win-x64-vulkan/native/        (64 MB 합계)
    ├── llama.dll            2.0 MB
    ├── ggml.dll             67 KB
    ├── ggml-base.dll        614 KB
    ├── ggml-cpu.dll         878 KB      ← CPU 폴백 용도로 Vulkan 빌드에도 포함
    └── ggml-vulkan.dll      59 MB
```

**주의**: 폴더 이름이 표준 RID(`win-x64`)와 다르게 `win-x64-cpu` / `win-x64-vulkan`. 이렇게 한 이유는:

- `runtimes/win-x64/native/`를 쓰면 NuGet 메커니즘이 자동 로드해버림. LLamaSharp가 그걸 붙잡으면 백엔드 선택권이 없어짐.
- 비표준 이름이면 자동 로드가 트리거되지 않고 **오직 `NativeLibraryConfig.All.WithLibrary()`로 우리가 명시 지정할 때만** 로드됨. 이게 Whisper.net과 공존 가능하게 만드는 핵심 장치.

## 6. ZeroCommon 통합

### csproj 추가분

```xml
<PackageReference Include="LLamaSharp" Version="0.26.0" />

<ItemGroup>
  <Content Include="runtimes\win-x64-cpu\native\*.dll">
    <Link>runtimes\win-x64-cpu\native\%(Filename)%(Extension)</Link>
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </Content>
  <Content Include="runtimes\win-x64-vulkan\native\*.dll">
    <Link>runtimes\win-x64-vulkan\native\%(Filename)%(Extension)</Link>
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </Content>
</ItemGroup>
```

`LLamaSharp.Backend.*` 패키지는 **의도적으로 추가하지 않는다**. 추가하면 NuGet 번들 네이티브가 output에 같이 복사되어 심볼 충돌 위험이 다시 생김.

### 코드 모듈 (`Agent.Common.Llm`)

- `ILocalLlm` — `CompleteAsync(string)` / `StreamAsync(string)` (단발성) + `CreateSession()` (멀티턴 진입).
- `ILocalChatSession` — `SendAsync(string)` / `SendStreamAsync(string)` + `TurnCount`. `IAsyncDisposable`.
- `LocalLlmOptions` — `ModelPath`, `Backend`, `ContextSize`, `MaxTokens`, `Temperature`, `GpuLayerCount`.
- `LocalLlmBackend` — `Cpu` / `Vulkan`.
- `LlamaSharpLocalLlm` — 엔진. 가중치 로드(`LLamaWeights`) 1회, 세션마다 컨텍스트 복제.
- `LlamaSharpLocalChatSession` — 세션. `InteractiveExecutor` + 자체 KV 캐시 + Gemma 턴 마커 관리.
- `LlmService` — 프로세스당 싱글톤. Load/Unload 상태 머신 (`Unloaded`/`Loading`/`Loaded`/`Unloading`) + `StateChanged` 이벤트 + Vulkan 디바이스 env var 주입.
- `LlmSettingsStore` — `%LOCALAPPDATA%\AgentZeroLite\llm-settings.json` 직렬화.
- `LlmModelLocator` — 경로 우선순위 (dev `D:\Code\AI\GemmaNet\models\...` → user `%LOCALAPPDATA%\...\models\...`).
- `LlmModelDownloader` — HttpClient + Range 헤더 resume, `IProgress<DownloadProgress>` 리포트.
- `VulkanDeviceEnumerator` / `VulkanDeviceInfo` — `vulkaninfo --summary` 파싱으로 GPU 목록 얻기 (§11 참조).

### 네이티브 로드 로직 (핵심)

```csharp
private static readonly object NativeInitLock = new();
private static bool _nativeConfigured;

private static void ConfigureNativeOnce(LocalLlmBackend backend)
{
    lock (NativeInitLock)
    {
        if (_nativeConfigured) return;

        var subDir = backend switch
        {
            LocalLlmBackend.Cpu    => "win-x64-cpu",
            LocalLlmBackend.Vulkan => "win-x64-vulkan",
            _ => throw new ArgumentOutOfRangeException(nameof(backend))
        };
        var llamaDll = Path.Combine(AppContext.BaseDirectory, "runtimes", subDir, "native", "llama.dll");

        NativeLibraryConfig.All.WithLibrary(llamaPath: llamaDll, mtmdPath: null);
        _nativeConfigured = true;
    }
}
```

**제약**: `NativeLibraryConfig.All.WithLibrary()`는 **프로세스당 한 번만** 작동. 첫 모델 로드 이후엔 백엔드를 바꿀 수 없음 (`InvalidOperationException`). 런타임 CPU↔Vulkan 토글을 원하면 **별도 프로세스로 분리**해야 한다. 일반적 사용 패턴은 설정에서 백엔드를 읽어 앱 기동 시 결정.

### Gemma 4 채팅 템플릿

Gemma 계열은 템플릿을 안 씌우면 출력이 거의 망가짐. 최소 래핑:

```csharp
private static string WrapGemmaChatTemplate(string userPrompt)
    => $"<start_of_turn>user\n{userPrompt}<end_of_turn>\n<start_of_turn>model\n";
```

anti-prompt는 `{ "<end_of_turn>", "<eos>" }` 두 개.

### 멀티턴 세션 (KV 캐시 재사용)

단발성 `StatelessExecutor`는 매 호출마다 상태 0에서 시작 — 이전 대화 기억 못함. 에이전트 용도로는 부적절. 세션 API는 `InteractiveExecutor` + 독립 `LLamaContext`로 KV 캐시를 프로세스 수명 동안 유지.

**사용**:

```csharp
await using var llm = await LlamaSharpLocalLlm.CreateAsync(options);
await using var session = llm.CreateSession();

var r1 = await session.SendAsync("내 이름은 철수야");   // "OK"
var r2 = await session.SendAsync("내 이름 뭐였지?");    // "철수"
// 추가로 독립 세션을 동시 보유 가능 — 각자 KV 캐시 분리
await using var sessionB = llm.CreateSession();
```

**턴 간 프롬프트 조립** — 첫 턴과 이후 턴이 구분됨:

```csharp
var prompt = _firstTurn
    ? $"<start_of_turn>user\n{userMessage}<end_of_turn>\n<start_of_turn>model\n"
    : $"\n<start_of_turn>user\n{userMessage}<end_of_turn>\n<start_of_turn>model\n";
```

이후 턴은 **`<end_of_turn>`을 다시 넣지 않는다**. 이유는 이미 KV 컨텍스트의 꼬리가 `<end_of_turn>`로 끝나 있기 때문. 이중 `<end_of_turn>` 넣으면 모델이 즉시 종료하여 빈 응답이 나옴 — 구현 중 실제로 밟았던 함정.

**anti-prompt 잔여물 정리** — LLamaSharp는 anti-prompt 매치 시 생성을 **중지만 하고 매치 문자열은 스트림에 포함한 채**로 반환. 게다가 디토크나이저 경계 때문에 `<end_of_turn>` 전체가 아닌 부분 prefix만 남기도 함 (`<`, `<end_of_turn`). `SendAsync`는 후처리로 trailing anti-prompt prefix를 최대길이부터 1글자까지 내려가며 strip:

```csharp
private static string StripTrailingAntiPrompt(string text)
{
    foreach (var anti in AntiPrompts)
        for (var len = anti.Length; len > 0; len--)
            if (text.EndsWith(anti[..len], StringComparison.Ordinal))
                return text[..^len];
    return text;
}
```

**실측 KV 캐시 이득** (CPU 백엔드, 40문장 프리픽스 프리필):

| 턴 | 상황 | 시간 |
|---|---|---|
| 1 | 40-fact 프리픽스 프리필 포함 | ~10,150 ms |
| 2 | 짧은 질문 (KV 재사용) | ~1,096 ms |

프리필 비용이 크고 이후는 저렴 — 에이전트처럼 긴 시스템 프롬프트를 맨 앞에 박아두는 패턴에 이상적. 세션 1개당 VRAM/RAM에 KV 캐시가 상주하므로 (16K 기준 E4B ~1GB) 동시 세션 수엔 한계가 있음.

## 7. LLamaSharp 0.26 API 주의사항

- `NativeLibraryConfigContainer.WithLibrary(string llamaPath, string mtmdPath)` — 2번째 파라미터 이름이 이전 버전의 `llavaPath`가 아니라 **`mtmdPath`**(multi-modal daemon). 0.26부터 이름 변경. 멀티모달 불필요하면 `null` 전달.
- `StatelessExecutor(weights, modelParams)` + `InferAsync(prompt, inferenceParams, ct)` 시그니처 유지. `IAsyncEnumerable<string>` 반환. **상태 없음** — 매 호출이 독립.
- `InteractiveExecutor(context)` — KV 캐시 유지형. 새 `LLamaContext`를 `weights.CreateContext(modelParams)`로 만들고 주입. **세션 하나당 컨텍스트 하나**.
- `InferenceParams.SamplingPipeline`에 `DefaultSamplingPipeline { Temperature = ... }` 배치. 0.26 기준 샘플링 파라미터(Temperature/TopP/TopK)는 `SamplingPipeline`으로 이관된 상태. `InferenceParams`에 직접 Temperature를 두지 않음.
- **anti-prompt 매치는 출력에 포함됨** — 생성 중단만 할 뿐 매치된 문자열을 제거하지 않음. 사용 측에서 trailing strip 필요.
- **부분 매치도 고려** — 디토크나이저 경계에서 `<end_of_turn>` 전체가 아닌 `<` 같은 prefix만 꼬리에 남기도 함. strip 로직은 full-length부터 1글자까지 점진적으로 검사.

## 8. 모델 선택 — Gemma 4 E4B 양자 비교

RTX 4060 8GB + 함수콜 우선 시나리오에서의 VRAM 예산 분석 (모델 + KV 캐시 + 오버헤드).

| 양자 | 파일 | +8K KV | +16K KV | +32K KV | 함수콜 |
|---|---|---|---|---|---|
| UD-IQ2_M | 3.53 GB | 4.6 GB | 5.2 GB | 6.4 GB | 불안정 |
| UD-Q3_K_XL | 4.56 GB | 5.7 GB | 6.3 GB | 7.5 GB | 보통 |
| **UD-Q4_K_XL** | **5.10 GB** | **6.2 GB** | **6.8 GB** | 8.0 GB | **안정** |
| Q4_K_M | 4.98 GB | 6.1 GB | 6.7 GB | 7.9 GB | 안정 |
| UD-Q5_K_XL | 6.65 GB | 7.8 GB | 넘침 | 넘침 | 매우 안정 |
| Q6_K | 7.07 GB | 넘침 | 넘침 | 넘침 | 매우 안정 |

**채택: `gemma-4-E4B-it-UD-Q4_K_XL.gguf` (5.10 GB)**. Unsloth dynamic quantization은 attention/embedding 레이어를 상위 비트로 유지하여 함수콜 품질이 표준 Q4_K_M보다 안정적. 16K 컨텍스트까지 4060 8GB 안에 전부 상주. 현재 저장 위치는 `D:\Code\AI\GemmaNet\models\`.

E2B는 함수콜 품질 부족으로 선택하지 않음. E4B가 에이전트 용도 최저 허용선.

## 9. 테스트 및 실측

위치:
- `Project/ZeroCommon.Tests/LlamaSharpLocalLlmTests.cs` — 단발성 + 함수콜
- `Project/ZeroCommon.Tests/LlamaSharpLocalChatSessionTests.cs` — 멀티턴 세션

`Xunit.SkippableFact` 패키지 추가 — 모델 파일 부재 시 fail이 아닌 skip으로 처리.

```csharp
Skip.IfNot(File.Exists(ModelPath), $"Model not present at {ModelPath}");
```

환경 변수 `GEMMA_MODEL_PATH`로 모델 경로 오버라이드 가능. 기본값 `D:\Code\AI\GemmaNet\models\gemma-4-E4B-it-UD-Q4_K_XL.gguf`.

### 실측 결과 (CPU 백엔드, 2026-04-23) — 전체 8 테스트 통과, 총 40초

| # | 테스트 | 결과 | 핵심 실측 |
|---|---|---|---|
| 1 | Native DLL 배치 확인 | PASS | - |
| 2 | CPU 모델 로드 + 응답 | PASS | load=2.5s, gen=1.3s, reply="hello" |
| 3 | 토큰 스트리밍 | PASS | - |
| 4 | 함수콜 `tool_code` 포맷 | PASS | `print(get_weather(city="Tokyo"))` |
| 5 | 함수콜 JSON 스키마 | PASS | `{"tool":"create_reminder","args":{...}}` |
| 6 | 세션 이름 회상 | PASS | turn1="OK", turn2="Cheolsu" |
| 7 | 세션 격리 (A/B 병렬) | PASS | A=ORANGE, B=PURPLE 상호 비누출 |
| 8 | **KV 캐시 속도 이득** | PASS | **turn1 10,150ms → turn2 1,096ms (9.3배)** |

함수콜은 두 스타일 모두 동작 확인:
- **Gemma 네이티브 `tool_code` 블록** — 별도 파서 없이 프롬프트 지시만으로 Google의 정식 포맷 준수
- **JSON-only 출력** — `JsonDocument.Parse` 바로 가능한 형태, C# 에이전트 호스트에 적합

Vulkan 백엔드 실측은 별도 테스트 프로세스 분리 필요 (NativeLibraryConfig 1회 제약 — 위 제약 섹션 참조).

## 10. 공존 전략 — Whisper.net과의 ggml 충돌 회피

- Whisper.net은 자체 `LLamaSharp.Backend`와 **다른** ggml 버전을 번들. 둘을 같은 프로세스에 로드하면 OS 레벨 DLL 로더가 먼저 온 ggml 심볼로 해결 → 나중 온 쪽이 잘못된 함수로 점프하여 크래시/오작동.
- 이번 구조는 **LLamaSharp.Backend 패키지를 배제**하고 우리 경로만 `NativeLibraryConfig.All.WithLibrary`로 주입. Whisper.net이 먼저 로드되어도 자기 ggml 경로를 쓰면 되고, 우리 LLM이 명시 경로로 로드하면 우리 ggml로 귀결됨.
- 단 **같은 프로세스 내에서 동시에 추론**을 돌리면 여전히 불안할 수 있음 (ggml 심볼이 두 번 로드된 상태이므로 내부 전역 상태가 경합할 가능성). 실제 동시 사용 필요 시 별도 프로세스 분리 권장.

## 11. 멀티-GPU (iGPU + dGPU) 디바이스 선택

랩탑 (또는 iGPU 내장 데스크탑 CPU) + dGPU 구성에서 Vulkan 백엔드의 **기본 동작이 크래시 원인**이 되는 경우가 있어 실측 추적한 결과 정리.

### 실제 겪은 증상

- 환경: Ryzen AI 300 (Strix Point) 랩탑, Radeon 890M iGPU + RTX 4060 Laptop GPU
- Load Model 클릭 → GPU 메모리 사용량 잠깐 상승 → **네이티브 AV 크래시** (WPF 프로세스 사일런트 종료, `DispatcherUnhandledException` / `AppDomain.UnhandledException` 모두 미발화)
- `app-log.txt`에 크래시 직전 관리 코드 로그까지만 남음

### 근본 원인

llama.cpp의 Vulkan 백엔드는 `vkEnumeratePhysicalDevices()` 결과 **첫 번째 디바이스**를 기본 사용함. WDDM 상에서 **iGPU가 index 0**으로 열거되는 경우가 흔하다. `vulkaninfo --summary`로 확인:

```
GPU0: AMD Radeon(TM) 890M Graphics          PHYSICAL_DEVICE_TYPE_INTEGRATED_GPU
GPU1: NVIDIA GeForce RTX 4060 Laptop GPU    PHYSICAL_DEVICE_TYPE_DISCRETE_GPU
```

iGPU는 전용 VRAM이 없고 시스템 RAM에서 WDDM 할당을 받음. Gemma 4 E4B Q4 (약 5 GB 연속 블록) 할당을 시도하다 드라이버가 거부 → ggml-vulkan 내부 assert → 프로세스 강제 종료.

### 해결 — 디바이스 필터링

llama.cpp는 `GGML_VK_VISIBLE_DEVICES` 환경변수를 읽어 열거 결과를 사전 필터링. CUDA의 `CUDA_VISIBLE_DEVICES`와 동일 개념. **네이티브 DLL이 로드되기 전** 혹은 최소한 **모델 로드 전**에 프로세스 환경에 설정하면 됨.

```csharp
Environment.SetEnvironmentVariable(
    "GGML_VK_VISIBLE_DEVICES",
    settings.VulkanDeviceIndex.ToString(CultureInfo.InvariantCulture));
// 이후 LLamaWeights.LoadFromFileAsync(...) → 필터된 목록에서만 선택
```

### 구현 자산

- `Agent.Common.Llm.VulkanDeviceInfo` — record(Index, Name, IsDiscrete, VendorId). VendorId는 0x10DE=NVIDIA, 0x1002=AMD, 0x8086=Intel, 0x106B=Apple.
- `Agent.Common.Llm.VulkanDeviceEnumerator` — `%WINDIR%\System32\vulkaninfo.exe` 또는 `%VULKAN_SDK%\Bin\vulkaninfoSDK.exe`를 `--summary` 플래그로 실행 후 regex 파싱.
- `LlmRuntimeSettings.VulkanDeviceIndex` — `int`, 기본 -1. -1 = **auto** (첫 PHYSICAL_DEVICE_TYPE_DISCRETE_GPU 선택, 없으면 0).
- UI: 설정 탭의 "GPU Device" 드롭다운. 첫 항목 `(auto - prefer discrete)`, 이후 열거된 장치 나열. Vulkan 백엔드 선택 시에만 의미 있음 (CPU 모드는 무시).
- `LlmService.LoadAsync` 내부에서 Vulkan 백엔드이고 `VulkanDeviceIndex >= 0`일 때만 env var 주입. 이후 `LLamaWeights.LoadFromFileAsync` 호출.

### 디바이스 역할 가이드 (이 프로젝트 맥락)

| 용도 | 권장 | 이유 |
|---|---|---|
| Gemma 4 추론 | dGPU (NVIDIA) | 메모리 대역폭·텐서코어 필요 |
| Whisper.net 음성 인식 | iGPU (DirectML/Vulkan) 또는 CPU | 모델 작아서 dGPU 점유 안 해도 됨 |
| WPF UI 렌더링 | iGPU (OS 자동) | 배터리 효율 |
| 하드웨어 비디오 코덱 | iGPU (AMF / QSV) | dGPU보다 저전력 |

이원화 가능성: Gemma는 NVIDIA, Whisper는 AMD — 서로 다른 네이티브 라이브러리가 각자 환경변수(`GGML_VK_VISIBLE_DEVICES`, Whisper.net 해당 옵션)로 선택하므로 프로세스 내 간섭 없음.

### 검증 신호

로드 시작 로그에 `vkDev=<index>`가 찍히면 디바이스 필터가 적용된 것:

```
[LLM] Vulkan devices: GPU0: AMD Radeon(TM) 890M Graphics (AMD, integrated) | GPU1: NVIDIA GeForce RTX 4060 Laptop GPU (NVIDIA, discrete)
[LLM] Load start backend=Vulkan vkDev=1 ctx=4096 gpuLayers=999 model=gemma-4-E4B-it-UD-Q4_K_XL.gguf
[LLM] Load complete
```

`vkDev=0`으로 지정되어 AMD iGPU를 가리키고 있으면 거의 확실히 크래시 → 드롭다운에서 discrete 선택으로 교체.

### `vulkaninfo` 부재 시

Vulkan SDK 미설치 + 드라이버가 `vulkaninfo.exe`를 함께 배포하지 않은 드문 경우 열거 실패 → 드롭다운이 `(auto)`만 표시. 이때는 `LlmSettingsStore`의 JSON을 직접 편집해 `VulkanDeviceIndex`를 명시 지정 권장.

## 12. 알려진 제약 / 향후 작업

- **백엔드 런타임 전환 불가** (NativeLibraryConfig 1회 제약). 설정 변경 시 앱 재시작 필요. 토글 UI를 둘 거면 "재시작 후 적용" 안내 필요.
- **Vulkan 이식성 경계**: Vulkan SDK 1.4 기능 사용 여부가 ggml-vulkan 셰이더에 영향. 최종 배포 대상 GPU 드라이버는 **Vulkan 1.2 이상 대응** 필요 (NVIDIA 560+ / AMD 20.11+ / Intel Xe 드라이버 등). 실질 문제는 거의 없지만 엣지 케이스 존재.
- **네이티브 재-load 불가**: 같은 프로세스 내에서 `LlmService.UnloadAsync` 이후 다시 `LoadAsync`는 금지 (iGPU 경유 크래시 후 상태 꼬임 재현됨). UI 레이어의 `_hasLoadedOnceInProcess` 가드로 차단, 사용자에겐 "재시작 후 다시 로드" 안내.
- **LLamaSharp 0.27 NuGet 릴리스 대기**: 정식 릴리스 나오면 `Backend` 패키지의 네이티브도 Gemma 4 지원. 다만 Whisper.net 공존 이슈 해결이 우선이라 자체 빌드 경로 유지가 당분간 안전.
- **32K+ 컨텍스트**: 현재 모델 선택은 16K 기준. 32K 이상 필요하면 UD-IQ2_M / UD-Q3_K_XL로 다운그레이드 검토.
- **세션 영속화 없음**: `ILocalChatSession`의 KV 캐시는 프로세스 수명 동안만 유효. 앱 재시작 후 대화 이력을 잇고 싶으면 히스토리 문자열(turn별 user/model 메시지)을 직렬화해서 다음 실행 때 prefill로 재주입 필요. 첫 재주입은 느리고 이후는 다시 빠름.
- **세션 컨텍스트 자동 트리밍 없음**: 이력이 `ContextSize`에 근접하면 수동으로 옛 턴 요약/삭제 필요. 현재 구현엔 오버플로우 방어 로직 미포함.
- **동시 세션 메모리 비용**: 세션 1개당 KV 캐시가 RAM/VRAM에 독립 상주 (E4B 16K 기준 약 1GB). 동시 수십 세션은 비현실적.
- **멀티모달(vision)**: Gemma 4 E4B는 vision 입력 지원. `NativeLibraryConfig.WithLibrary`의 `mtmdPath`에 추가 DLL(`mtmd.dll`)을 빌드해 넘기면 활용 가능. audio는 llama.cpp 측 미구현 상태(issue #21325).
- **Git repo 크기**: `runtimes/` 아래 총 66MB 추가됨 (`ggml-vulkan.dll`이 59MB 차지). 모노레포 크기에 영향. 필요 시 Git LFS 도입 검토 지점.

## 13. 참고 링크

- [llama.cpp PR #21309 — Gemma 4 지원 초도 구현](https://github.com/ggml-org/llama.cpp/pull/21309)
- [llama.cpp PR #21343 — Gemma 4 tokenizer 수정](https://github.com/ggml-org/llama.cpp/pull/21343)
- [LLamaSharp PR #1356 — Qwen3.5/Gemma4 Support (커밋 3f7c29d 핀)](https://github.com/SciSharp/LLamaSharp/pull/1356)
- [LLamaSharp PR #1371 — unknown model architecture FAQ](https://github.com/SciSharp/LLamaSharp/pull/1371)
- [onnxruntime-genai issue #2062 — Gemma 4 지원 차단 사유](https://github.com/microsoft/onnxruntime-genai/issues/2062)
- [HuggingFace: unsloth/gemma-4-E4B-it-GGUF (UD dynamic quants)](https://huggingface.co/unsloth/gemma-4-E4B-it-GGUF)
- [HuggingFace blog: Welcome Gemma 4](https://huggingface.co/blog/gemma4)
