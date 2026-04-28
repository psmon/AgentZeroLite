# .NET에 Gemma 4를 온디바이스로 탑재하기 — 실패와 성공의 여정

> ⚠️ **먼저 읽어주세요 — 안전성 / 의도 안내**
>
> 이 글에서 다루는 `llama.dll` / `ggml-*.dll` 바이너리는 **공식 LLamaSharp NuGet이 Gemma 4를 따라잡기 전에, 제가 이 프로젝트에 당장 Gemma 4를 써야 해서 직접 빌드한 결과물**입니다. 검증된 배포본이 아니라 *제 여정*의 재현성을 위해 같이 커밋되어 있을 뿐입니다.
>
> - **포함된 prebuilt DLL의 안전성은 저도 보장하지 못합니다.** 이 저장소의 바이너리를 그대로 쓰지 마세요. 이 방법이 정말 필요하다면 **고정해 둔 llama.cpp 커밋(또는 본인이 검증한 커밋)에서 직접 빌드**하고 산출물을 검증해서 사용하시길 권장합니다.
> - **더 나은 방법을 알고 계신다면 공유 부탁드립니다** — 이슈 / PR 모두 환영합니다. 이렇게 정리해 두는 이유 자체가 더 좋은 답을 받기 위함입니다.
> - **이 방법은 한시적인 라이프사이클입니다.** LLamaSharp NuGet이 Gemma 4를 공식 지원하는 순간 이 우회는 전부 걷어낼 예정입니다. 이 문서가 존재하는 가장 큰 이유는 (a) 다음번에 저 / Claude 가 같은 함정을 다시 밟지 않게 하고, (b) NuGet 정식 지원으로 되돌리는 작업이 기계적으로 가능하도록 기록을 남겨두는 것입니다.
> - **그럼에도 레시피를 남겨두는 이유:** 새 온디바이스 모델이 등장했을 때 *공식 바인딩 지원 전*에 빠르게 실험해야 한다면, 이 직접 빌드 경로가 가장 빠른 실험 루프이기 때문입니다. 즉, Gemma 4 건이 종료되더라도 "직접 빌드해서 실험" 기법 자체는 계속 유지·갱신될 예정입니다.
>
> 한 줄 요약 — 이 문서는 **연구 로그**이지 **배포 가이드가 아닙니다.**

---

> 이 문서는 **.NET 10 WPF 앱(AgentZero Lite)에 Google Gemma 4를 HTTP 서버 없이 직접 내장**한 실제 과정을 처음부터 따라갈 수 있게 정리한 튜토리얼입니다. .NET 개발자가 LLM을 처음 다루더라도 이해할 수 있게 용어를 하나씩 풀어 씁니다. 실측 수치, 막혔던 포인트, 해결법이 전부 포함되어 있어요.

---

## 0. 이 글을 읽기 전에

### 우리가 원했던 것

> "사용자 PC에서 인터넷 없이, Ollama 같은 별도 서버 없이, .NET 앱이 **Gemma 4를 직접 로드해서 토큰을 생성**하고 싶다."

**왜 HTTP 말고 인프로세스?**
- 지연시간 낮음 (프로세스 내 함수 호출 수준)
- 배포 단순 (exe 하나)
- 사용자 환경 통제 쉬움
- 네트워크 포트 충돌 없음
- 오프라인 동작

### 사용한 기술 스택

| 구성요소 | 버전 / 특징 |
|---|---|
| .NET | 10.0 preview (LlamaSharp는 net8.0+ 지원) |
| LLamaSharp | 0.26.0 (llama.cpp의 C# 바인딩) |
| llama.cpp | 자체 빌드 (commit `3f7c29d`) |
| Gemma 4 | E4B / E2B UD-Q4_K_XL (GGUF) |
| Vulkan | 크로스벤더 GPU API (NVIDIA/AMD/Intel 공통) |

### 용어 정리 (처음 보는 분들 위해)

- **LLM (Large Language Model)**: GPT/Gemma/Llama 같은 언어 모델
- **GGUF**: `.gguf` 확장자. llama.cpp 생태계의 **양자화된 모델 파일 포맷**. 가중치가 메모리 절약을 위해 4-bit/8-bit 등으로 압축되어 있음
- **양자화 (Quantization)**: 32-bit float → 4-bit 등으로 모델 크기를 줄이는 기법. `Q4_K_XL`, `Q8_0` 같은 표기가 이것
- **추론 (Inference)**: 모델에 입력을 넣고 답(토큰)을 받는 과정
- **LLamaSharp**: `llama.cpp` C/C++ 라이브러리를 .NET에서 호출하게 해주는 P/Invoke 래퍼
- **Vulkan**: DirectX처럼 GPU를 다루는 API. NVIDIA/AMD/Intel 모두 지원해서 크로스벤더에 유리
- **KV 캐시 (Key-Value Cache)**: Transformer 모델이 이전 토큰들의 attention 결과를 저장해 재사용하는 버퍼. 크기가 메모리를 꽤 먹음
- **온디바이스**: 모델이 서버가 아닌 **사용자 PC 메모리에 상주**해서 직접 실행됨

---

## 1. 첫 시도와 첫 실패 — "LLamaSharp 그냥 쓰면 되는 거 아냐?"

### 순진한 접근

NuGet에서 `LLamaSharp` + `LLamaSharp.Backend.Cpu`를 설치하고 모델 로드 → 완성. 보통의 .NET 라이브러리 방식.

```xml
<PackageReference Include="LLamaSharp" Version="0.26.0" />
<PackageReference Include="LLamaSharp.Backend.Cpu" Version="0.26.0" />
```

### 우리가 만난 3가지 벽

**벽 1. Gemma 4가 너무 최신**

Google이 **2026-04-02**에 Gemma 4를 공개했습니다. LLamaSharp 0.26.0 NuGet은 **2026-02-15** 릴리스 — Gemma 4 이전 시점입니다. 번들된 네이티브 라이브러리(`llama.cpp`)가 Gemma 4 아키텍처를 몰라서 GGUF 로드 시 `unknown model architecture: 'gemma4'` 에러가 납니다.

**벽 2. ggml 심볼 충돌**

이 프로젝트에는 음성 인식 라이브러리 `Whisper.net`도 같이 쓰입니다. Whisper.net과 LLamaSharp는 **둘 다 자체 `ggml.dll`을 번들**합니다. 같은 프로세스에 로드되면 **먼저 로드된 쪽의 심볼이 나중 것을 덮어써서** 둘 중 하나가 반드시 깨집니다.

**벽 3. onnxruntime-genai도 아직**

Microsoft 공식 경로인 `Microsoft.ML.OnnxRuntimeGenAI`도 Gemma 4의 **Per-Layer Embeddings**, **Variable Head Dimensions**, **KV Cache Sharing** 3가지 아키텍처 변경 때문에 현재 미지원 ([이슈 #2062](https://github.com/microsoft/onnxruntime-genai/issues/2062)).

### 깨달음

**신모델 + 관리 런타임 생태계는 타임 랙**이 있습니다. 2~3개월 걸려 NuGet 번들이 따라잡을 때까지 기다리거나, **직접 빌드**해야 합니다.

---

## 2. 해결 전략 — llama.cpp를 직접 빌드하자

### 전략 개요

1. llama.cpp를 **특정 커밋**에서 소스로 받아 직접 빌드 → `llama.dll` + `ggml*.dll` 생성
2. LLamaSharp 0.26.0의 P/Invoke 시그니처와 **ABI가 일치하는 커밋**을 선택
3. NuGet의 `LLamaSharp.Backend.*` 패키지는 **쓰지 않음** (자체 DLL과 충돌)
4. 앱에서 `NativeLibraryConfig.All.WithLibrary(path)`로 **우리 DLL을 명시 지정**

### 어느 커밋을 써야 하나

LLamaSharp master 브랜치의 [PR #1356](https://github.com/SciSharp/LLamaSharp/pull/1356)가 Gemma 4 지원을 위해 llama.cpp 서브모듈을 `3f7c29d`로 업데이트했습니다. 이 커밋을 쓰면:
- LLamaSharp 0.26.0의 P/Invoke가 기대하는 C 함수 시그니처와 일치 ✓
- Gemma 4 모델 아키텍처 인식 ✓

다른 커밋을 쓰면 심볼 불일치로 런타임에 `EntryPointNotFoundException` 같은 오류가 납니다.

### Whisper.net 충돌 회피 원리

`NativeLibraryConfig.All.WithLibrary(path)`로 우리가 만든 DLL 경로를 **명시 지정**하면 LLamaSharp는 NuGet 번들 경로를 보지 않습니다. DLL을 `runtimes/win-x64-cpu/native/` 같은 **비표준 RID 폴더**에 두면 NuGet의 자동 로드도 트리거되지 않아, Whisper.net이 자기 ggml을 먼저 로드하든 말든 우리 ggml은 명시 경로로 독립 로드됩니다.

---

## 3. 빌드 준비

### 사전 요구사항

- **Visual Studio 2022** (Community/Professional 둘 다 가능) — MSVC + CMake 내장
- **Git** — 소스 클론
- **Vulkan SDK 1.4.x** (GPU 빌드할 거면) — `winget install KhronosGroup.VulkanSDK`

### 빌드 전용 폴더 준비

```bash
mkdir D:\Code\AI\GemmaNet
```

AgentZero Lite 프로젝트와 별도 폴더로 분리했습니다. 빌드 아티팩트와 소스 원본은 분리하는 게 깔끔합니다.

### 소스 가져오기

```bash
cd D:\Code\AI\GemmaNet
git clone --depth 1 --no-checkout https://github.com/ggml-org/llama.cpp.git
cd llama.cpp
git fetch --depth 1 origin 3f7c29d318e317b63f54c558bc69803963d7d88c
git checkout 3f7c29d318e317b63f54c558bc69803963d7d88c
# HEAD: "ggml: add graph_reused (#21764)"
```

---

## 4. CPU 빌드 (안전한 첫 단계)

### CMake configure

VS 2022 내장 CMake 사용:

```bash
CMAKE="/c/Program Files/Microsoft Visual Studio/2022/Professional/Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe"

"$CMAKE" -B build -S . -G "Visual Studio 17 2022" -A x64 \
  -DBUILD_SHARED_LIBS=ON \
  -DGGML_NATIVE=OFF \
  -DLLAMA_CURL=OFF \
  -DLLAMA_BUILD_TESTS=OFF \
  -DLLAMA_BUILD_EXAMPLES=OFF \
  -DLLAMA_BUILD_TOOLS=OFF
```

**중요 플래그**:
- `BUILD_SHARED_LIBS=ON` — DLL로 빌드 (우리 .NET이 로드)
- `GGML_NATIVE=OFF` — **빌드 머신 CPU 전용 최적화 끔** (기본값 ON은 AVX-512/AMX까지 뽑아서 다른 PC에서 `Illegal instruction` 오류 유발). AVX2 수준에서 끊음
- `LLAMA_BUILD_TESTS/EXAMPLES/TOOLS=OFF` — 우리는 DLL만 필요

### 빌드

```bash
"$CMAKE" --build build --config Release --target llama -j
```

성공하면 `build/bin/Release/`에 4개 DLL 생성:
- `llama.dll` (2.0 MB) — 메인 바인딩 타겟
- `ggml.dll` (67 KB)
- `ggml-base.dll` (614 KB)
- `ggml-cpu.dll` (878 KB) — CPU 연산 백엔드

---

## 5. .NET 프로젝트 통합

### DLL 배치

ZeroCommon 프로젝트 (core logic, WPF 의존 없는 shared 라이브러리)에 DLL을 커밋 관리:

```
Project/ZeroCommon/
├── runtimes/
│   └── win-x64-cpu/
│       └── native/
│           ├── llama.dll
│           ├── ggml.dll
│           ├── ggml-base.dll
│           └── ggml-cpu.dll
└── ZeroCommon.csproj
```

### csproj 수정

```xml
<ItemGroup>
  <PackageReference Include="LLamaSharp" Version="0.26.0" />
</ItemGroup>

<!-- Backend.* 패키지는 의도적으로 제외 — 자체 DLL과 충돌 방지 -->

<ItemGroup>
  <Content Include="runtimes\win-x64-cpu\native\*.dll">
    <Link>runtimes\win-x64-cpu\native\%(Filename)%(Extension)</Link>
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </Content>
</ItemGroup>
```

### 명시적 네이티브 로드

```csharp
using LLama.Native;

var llamaDll = Path.Combine(
    AppContext.BaseDirectory,
    "runtimes", "win-x64-cpu", "native", "llama.dll");

NativeLibraryConfig.All.WithLibrary(
    llamaPath: llamaDll,
    mtmdPath: null);  // mtmd: 멀티모달 데몬. 지금 불필요
```

> **0.26 API 주의**: 두 번째 파라미터 이름이 이전 버전의 `llavaPath`가 아니라 **`mtmdPath`** 로 바뀌었습니다. Multi-modal daemon의 약자. 비전/오디오 안 쓰면 null.

### 첫 추론 코드

```csharp
using LLama;
using LLama.Common;

var parameters = new ModelParams(modelPath)
{
    ContextSize = 2048,
    GpuLayerCount = 0  // CPU 모드
};

using var weights = LLamaWeights.LoadFromFile(parameters);
using var context = weights.CreateContext(parameters);
var executor = new StatelessExecutor(weights, parameters);

var inferenceParams = new InferenceParams
{
    MaxTokens = 64,
    AntiPrompts = new[] { "<end_of_turn>" }  // Gemma의 턴 종료 마커
};

// Gemma 4 채팅 템플릿 씌우기 (중요!)
var prompt = $"<start_of_turn>user\n안녕<end_of_turn>\n<start_of_turn>model\n";

await foreach (var tok in executor.InferAsync(prompt, inferenceParams))
    Console.Write(tok);
```

### Gemma 채팅 템플릿이 왜 필요한가

Gemma는 fine-tuning 시 **`<start_of_turn>user` / `<end_of_turn>` / `<start_of_turn>model`** 마커로 대화 경계를 학습합니다. 이 마커 없이 생 텍스트를 넣으면 모델이 **아무 말이나** 뱉거나 **프롬프트를 이어쓰기**합니다. 템플릿은 필수.

### CPU 모드 실측 (RTX 4060 Laptop, Ryzen AI 9 365)

- 로드: 2.5초
- "hello" 생성 (첫 토큰 포함): 1.3초
- 사용 가능한 속도: 12.6 토큰/초 (E4B), 18.0 토큰/초 (E2B)

CPU만으로도 충분히 사용 가능. 더 빠르게 하려면 GPU로 넘어갑니다.

---

## 6. 멀티턴 대화 — KV 캐시 재사용

### 싱글샷 vs 멀티턴

위의 `StatelessExecutor`는 **매 호출마다 상태 0에서 시작**합니다. "내 이름은 철수야" → "내 이름 뭐였지?" 물어도 모델이 기억 못함.

### 세션 기반 대화

```csharp
using var context = weights.CreateContext(parameters);
var executor = new InteractiveExecutor(context);

// 첫 턴
var prompt1 = "<start_of_turn>user\n내 이름은 철수<end_of_turn>\n<start_of_turn>model\n";
await foreach (var tok in executor.InferAsync(prompt1, inferenceParams))
    Console.Write(tok);

// 이후 턴 — <end_of_turn>을 다시 넣지 않음!
// anti-prompt가 매칭되면서 이미 컨텍스트 꼬리에 남아있음
var prompt2 = "\n<start_of_turn>user\n내 이름 뭐였지?<end_of_turn>\n<start_of_turn>model\n";
await foreach (var tok in executor.InferAsync(prompt2, inferenceParams))
    Console.Write(tok);
```

### 함정: 이중 `<end_of_turn>`

처음에 우리가 실수했던 부분. 매 턴마다 `<end_of_turn>\n<start_of_turn>user\n...`로 접두사를 붙였더니 모델이 **즉시 빈 응답**을 내뱉었습니다.

이유: `AntiPrompts`가 매칭하면 생성을 멈추지만 **매치 문자열은 이미 출력에 포함**됩니다. 즉 KV 컨텍스트 꼬리가 이미 `...<end_of_turn>`로 끝나 있는 상태. 다음 턴에 또 `<end_of_turn>`을 prepend하면 **이중 토큰**이 되어 모델이 즉시 종료 신호로 판단.

### KV 캐시 재사용 실측

세션 1개 안에서 긴 시스템 프롬프트(40문장) + 짧은 질문을 반복:

| 턴 | 상황 | 시간 |
|---|---|---|
| 1 | 40문장 prefill + 짧은 응답 | **10,150 ms** |
| 2 | KV 재사용, 새 짧은 질문 | **1,096 ms** |
| 속도 차 | | **9.3배** |

에이전트처럼 긴 시스템 프롬프트 + 짧은 반복 질문 패턴에서는 세션이 **필수**.

### anti-prompt 출력 정리

LLamaSharp의 anti-prompt 매칭은 문자열을 **출력에서 제거해주지 않습니다**. 디토크나이저 경계 때문에 `<end_of_turn>` 전체가 아닌 `<`만 꼬리에 남기도 합니다. 사용자에게 깨끗한 응답 주려면 수동으로 strip:

```csharp
private static string StripTrailingAntiPrompt(string text)
{
    foreach (var anti in new[] { "<end_of_turn>", "<eos>" })
        for (var len = anti.Length; len > 0; len--)
            if (text.EndsWith(anti[..len], StringComparison.Ordinal))
                return text[..^len];
    return text;
}
```

Full length부터 1글자까지 점진적으로 prefix 매치 검사 → 찾으면 잘라냄.

---

## 7. GPU 도전 — Vulkan으로 가자

### 왜 Vulkan?

| 백엔드 | 장점 | 단점 |
|---|---|---|
| CUDA | NVIDIA에서 가장 빠름 | NVIDIA 전용. 토큰 설치 2.8 GB |
| **Vulkan** | **크로스벤더**. SDK 500 MB | 셰이더 컴파일 이슈 간혹 |
| DirectML | Windows 기본 탑재 | llama.cpp 공식 지원 빈약 |

배포 타겟이 "사용자 PC 어디서나"면 **Vulkan이 정답**. SDK 설치 필요하지만 빌드 머신만, 배포본은 드라이버만 있으면 됨.

### Vulkan 빌드

```bash
winget install KhronosGroup.VulkanSDK

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

출력에 `ggml-vulkan.dll` (~59 MB) 추가 — GLSL 셰이더가 컴파일되어 DLL에 임베디드되어 커짐.

### 그리고… 크래시

GUI에서 Load → GPU 메모리 잠깐 상승 → **프로세스 사일런트 종료**. 예외도 없이 앱이 사라집니다. 로그에도 아무 흔적 없음.

---

## 8. 디버깅 여정 — 3개의 숨은 버그

크래시 원인을 찾기 위해 진단 도구부터 만들었습니다.

### 진단 도구 1: 네이티브 로그 캡처

**문제**: LLamaSharp는 llama.cpp가 stderr에 내뱉는 에러 메시지를 자동으로 라우팅하지 않습니다. 그래서 우리 로그엔 `LoadWeightsFailedException: Failed to load model`만 찍히고 **진짜 이유는 은폐**.

**해결**: `NativeLibraryConfig.All.WithLogCallback`으로 연결:

```csharp
NativeLibraryConfig.All.WithLogCallback((level, msg) =>
{
    if (level < LLamaLogLevel.Warning) return;
    AppLogger.Log($"[llama.cpp][{level}] {msg.TrimEnd('\r', '\n')}");
});
```

**효과**: 다음 크래시 때 바로 진짜 에러가 로그에 찍힙니다.

### 진단 도구 2: `LlmProbe` 서브프로세스

WPF 앱에서 네이티브 AV(Access Violation) 크래시가 나면 전체 앱이 사라집니다. 디버깅이 거의 불가능. 그래서 **별도 콘솔 실행파일**을 만듦:

```
Project/LlmProbe/
├── LlmProbe.csproj
└── Program.cs
```

`Program.cs`는 `Agent.Common.Llm`을 참조하는 단순 콘솔. CLI 인자로 백엔드/페이즈를 받아 Load → Session → 토큰 생성을 수행하고 JSON 결과 + exit code로 보고합니다.

```bash
# CPU Load 스모크 테스트
LlmProbe.exe Cpu load

# Vulkan 전체 경로 (Load + Session + 토큰)
LlmProbe.exe Vulkan complete

# 벤치마크 (60토큰 시 생성으로 t/s 측정)
LlmProbe.exe Vulkan bench
```

**가치**:
- 네이티브 AV가 probe 프로세스만 죽이고, 테스트 러너는 exit code로 실패 감지
- `NativeLibraryConfig.WithLibrary` 프로세스당 1회 제약에서 자유로움 (테스트마다 새 프로세스)
- env var 조합 매트릭스 자동 탐색 가능

### 버그 1: 내장 GPU(iGPU)가 먼저 선택됨

**증상**: Vulkan Load → 1.3초 만에 `LoadWeightsFailedException` → 재시도하면 성공

**원인 발견**: `vulkaninfo --summary` 실행해서:

```
GPU0: AMD Radeon(TM) 890M Graphics          PHYSICAL_DEVICE_TYPE_INTEGRATED_GPU
GPU1: NVIDIA GeForce RTX 4060 Laptop GPU    PHYSICAL_DEVICE_TYPE_DISCRETE_GPU
```

**확인**: 이 머신은 랩탑이라 **Ryzen CPU의 내장 GPU (iGPU)**와 **NVIDIA 외장 GPU (dGPU)** 두 개가 있음. llama.cpp는 기본적으로 `vkEnumeratePhysicalDevices()`의 첫 결과를 씀 → GPU0 = iGPU 선택. iGPU는 시스템 RAM 공유라 5GB GGUF 연속 할당 시도하다 실패.

**해결**: `GGML_VK_VISIBLE_DEVICES` 환경변수로 필터:

```csharp
Environment.SetEnvironmentVariable("GGML_VK_VISIBLE_DEVICES", "1");  // dGPU 지정
```

CUDA의 `CUDA_VISIBLE_DEVICES`와 같은 개념.

### 버그 2: `VK_KHR_shader_bfloat16` 확장 문제

**증상**: iGPU 회피 후에도 여전히 첫 로드 실패

**원인 발견**: Vulkan Loader 디버그 활성화:

```bash
VK_LOADER_DEBUG=error,extension LlmProbe.exe Vulkan load
```

로그에:

```
[Vulkan Loader] ERROR: loader_validate_device_extensions:
Device extension VK_KHR_shader_bfloat16 not supported by selected physical device
```

**확인**: llama.cpp가 빌드 시 Vulkan SDK 1.4가 지원한다고 판단한 `VK_KHR_shader_bfloat16`을 런타임에 `vkCreateDevice`로 요청. NVIDIA 랩탑 드라이버(581.57)는 **extension 열거에는 포함시키지만 실제 device 생성에선 거부** — 신 확장 지원 부재.

**해결**: llama.cpp의 env var로 확장 요청 비활성:

```csharp
Environment.SetEnvironmentVariable("GGML_VK_DISABLE_BFLOAT16", "1");
```

### 버그 3: .NET env var가 네이티브 `getenv()`에 안 전달됨 (진짜 범인)

**증상**: 위 env var 설정해도 여전히 실패

**원인 추적**:
- 우리 C# 코드에서 `Environment.GetEnvironmentVariable("GGML_VK_DISABLE_BFLOAT16")` 읽으면 `"1"` 반환
- 그런데 `ggml-vulkan.dll`의 C `getenv("GGML_VK_DISABLE_BFLOAT16")`은 **NULL 반환**
- 셸에서 `GGML_VK_DISABLE_BFLOAT16=1 LlmProbe.exe ...` 하면 잘 됨

**확인**: Windows에서 환경변수는 **2군데에 저장**됩니다:

1. **프로세스 환경 블록** — `SetEnvironmentVariableW` / `GetEnvironmentVariableW`. .NET `Environment.SetEnvironmentVariable`이 여기를 갱신
2. **MSVC CRT 캐시** — 프로세스 시작 시 1번에서 한 번 복사됨. 이후 `getenv`는 이 캐시만 읽음

**MSVC CRT는 프로세스 시작 이후 자동 동기화하지 않습니다.** `.NET Environment.SetEnvironmentVariable`은 1번만 갱신 → `ggml-vulkan.dll` 속 `getenv`는 여전히 캐시된 옛 값(없음)을 봄.

**해결**: `ucrtbase.dll`의 `_putenv_s`를 P/Invoke로 호출해 CRT 캐시까지 갱신:

```csharp
[DllImport("ucrtbase.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
private static extern int _putenv_s(string name, string value);

private static void SetNativeEnv(string name, string value)
{
    Environment.SetEnvironmentVariable(name, value);  // Windows env block
    try { _putenv_s(name, value); } catch { }         // MSVC CRT cache
}
```

**이 버그의 일반성**: 이 문제는 llama.cpp 한정이 아닙니다. **.NET에서 네이티브 DLL이 `getenv`로 읽는 설정을 동적으로 변경하려면 항상 이 패턴이 필요**합니다. ONNX Runtime, CUDA 라이브러리 등 C/C++ 네이티브 통합 전반에 적용되는 교훈.

### 3개 버그 종합 효과

- 버그 1 (iGPU): `GGML_VK_VISIBLE_DEVICES` 설정 → 단독으론 부족
- 버그 2 (bfloat16): `GGML_VK_DISABLE_BFLOAT16` 설정 → 단독으론 부족 (전파 안 됨)
- 버그 3 (env 전파): `_putenv_s` 추가 → 버그 2를 **실제로 적용 가능하게 만듦**

셋이 모두 해결되면 **첫 Load 시도에 성공**.

---

## 9. 성능 튜닝

### LlmProbe bench로 객관 측정

같은 프롬프트로 워밍업 + 측정을 반복:

```csharp
// Program.cs (probe) — bench phase
await session.SendAsync("Say: ready");  // 워밍업 (측정 제외)

var sw = Stopwatch.StartNew();
int tokens = 0;
await foreach (var tok in session.SendStreamAsync(
    "Write a short poem about algorithms. Six lines. Just the poem, no preamble."))
    tokens++;
sw.Stop();

Console.WriteLine($"tps={tokens / sw.Elapsed.TotalSeconds:0.0}");
```

### 결과 (RTX 4060 Laptop, E4B Q4_K_XL)

| 구성 | tok/s | 비고 |
|---|---|---|
| COOPMAT ON, NoKqv=ON (최적) | **21.8** | 최종 |
| COOPMAT OFF, NoKqv=ON | 17.2 | -26% |
| COOPMAT ON, NoKqv=**OFF** | 🔻 5.0 | -4배 (반직관!) |

### 반직관 포인트: `NoKqvOffload=true`가 빠르다

일반적으로 KV 캐시를 **GPU에 두는 게 빠를 것** 같습니다. 매 토큰마다 CPU↔GPU 왕복 없애는 게 상식.

하지만 **Gemma 4의 Sliding Window Attention (SWA)** 아키텍처는 GPU Vulkan 경로에서 `llama_kv_cache_iswa: using full-size SWA cache` 경고와 함께 padding된 full-size cache를 유지해야 해서 **오히려 느려짐**. Gemma 4 + Vulkan 고유 현상 (다른 모델/백엔드에선 정상).

교훈: **"상식적 최적화"를 실측 없이 믿지 말 것.**

### Flash Attention

- Attention 연산을 타일 단위로 스트리밍 → 중간 행렬을 VRAM에 만들지 않음
- Prefill 2-4배 가속 + 메모리 절약
- **V 캐시 양자화 (Q8_0 V)를 쓰려면 FA 필수**

### 모델 선택: E4B vs E2B

| 모델 | 파일 크기 | probe bench | 실사용 UI |
|---|---|---|---|
| **E4B** Q4_K_XL | 5.1 GB | 21.8 tok/s | ~40 tok/s (멀티턴 KV 재사용) |
| **E2B** Q4_K_XL | 3.2 GB | 59.5 tok/s | 60+ tok/s |

E2B는 E4B의 2.7배 속도. 다만 **함수콜(tool_code/JSON) 안정성이 떨어짐** → 에이전트 풀 기능엔 E4B, 일반 대화/번역/요약엔 E2B.

### 최종 권장 구성

```
Backend:           Vulkan
GPU Device:        dGPU (자동: 첫 discrete GPU)
Model:             E4B Q4_K_XL (에이전트용) / E2B Q4_K_XL (속도 우선)
ContextSize:       2048
FlashAttention:    ON
NoKqvOffload:      ON   ← Gemma 4에선 이게 정답
KvCacheTypeK/V:    F16 (기본) / Q8_0 (메모리 타이트 시)
UseMemoryMap:      RAM 여유 기준 판단
```

---

## 10. 정리 — 우리가 얻은 것

### 최종 아키텍처

```
AgentZero Lite (WPF .NET 10)
├── ZeroCommon.csproj
│   ├── LLamaSharp 0.26.0 (NuGet, Backend.* 제외)
│   ├── Agent.Common.Llm/ (ILocalLlm, LlamaSharpLocalLlm, LlmService, ...)
│   └── runtimes/
│       ├── win-x64-cpu/native/     (4 DLL, 3.5 MB)
│       └── win-x64-vulkan/native/  (5 DLL, 64 MB)
└── AgentZeroWpf.csproj
    └── UI/Components/SettingsPanel.Llm.cs (설정 탭 + Load/Unload/Chat 테스트)

Project/LlmProbe/        ← 서브프로세스 벤치/테스트 도구
Project/ZeroCommon.Tests/LlmProbeTests.cs  ← 회귀 방어
```

### 일반화된 교훈 5가지

1. **NuGet 생태계는 신모델에 늦다** — 2–3개월. 최신을 써야 하면 자체 빌드 경로를 준비하자.

2. **네이티브 로그를 반드시 AppLogger로 라우팅** — C# 예외 메시지는 네이티브 오류를 은폐. `NativeLibraryConfig.WithLogCallback` 한 줄이 전체 디버깅 경로를 살림.

3. **서브프로세스 기반 probe는 투자 대비 효과 최고** — 네이티브 AV 격리 + env var 매트릭스 탐색 + 회귀 테스트. 설정 비용 1시간, 평생 유용.

4. **`.NET Environment.SetEnvironmentVariable`은 네이티브 `getenv`까지 안 갑니다** — `_putenv_s` P/Invoke 병행 패턴을 기억하자. llama.cpp 외에도 적용됨.

5. **"상식적 최적화"를 실측으로 검증**. Gemma 4 `NoKqvOffload=false`가 오히려 4배 느린 케이스처럼, 아키텍처별 특이점은 벤치로 잡아야 함.

### 다음 단계 아이디어

- AgentBot UI 본 기능과 통합 (현재는 Settings 탭에서 실험적)
- llama.cpp 최신 master로 재빌드 — Gemma 4 GPU-KV 경로 업스트림 개선 편입
- Whisper.net을 iGPU(DirectML)로 보내서 dGPU와 병렬 활용 (Gemma=NVIDIA, Whisper=AMD iGPU)
- 임베딩 기반 로컬 RAG (같은 Gemma 모델 재사용 가능)

---

## 참고 문서

이 튜토리얼은 3개 상세 문서를 이야기로 재구성한 것입니다. 깊이 파고싶으면:

- [`Docs/resaerch-geema4.md`](../../resaerch-geema4.md) — 연구·구현 기록 전체
- [`Docs/gemma4-gpu-load-failures.md`](../../gemma4-gpu-load-failures.md) — 크래시 실패 사례집 (7가지 케이스)
- [`Docs/gemma4-performance-benchmarks.md`](../../gemma4-performance-benchmarks.md) — 성능 벤치마크 매트릭스

### 참고 외부 링크

- [llama.cpp PR #21309 — Gemma 4 지원](https://github.com/ggml-org/llama.cpp/pull/21309)
- [LLamaSharp PR #1356 — Gemma 4 ABI 호환 커밋](https://github.com/SciSharp/LLamaSharp/pull/1356)
- [HuggingFace: unsloth/gemma-4-E4B-it-GGUF](https://huggingface.co/unsloth/gemma-4-E4B-it-GGUF)
- [HuggingFace: unsloth/gemma-4-E2B-it-GGUF](https://huggingface.co/unsloth/gemma-4-E2B-it-GGUF)
