# Gemma 4 온디바이스 — GPU 로드 실패 사례집

작성일 2026-04-24. AgentBot AI Mode 온디바이스 탑재 시도 중 누적된 Vulkan 백엔드 크래시 케이스를 **재현·원인·대응**으로 정리. `Docs/resaerch-geema4.md`가 "어떻게 돌리는지"라면 이 문서는 "왜 안 돌았는지".

> 상태: **CPU 백엔드는 안정 (Pass). Vulkan 백엔드는 불안정 — 진행 중.**
> 대상 하드웨어: Strix Point 랩탑 (AMD Radeon 890M iGPU + NVIDIA RTX 4060 Laptop dGPU), NVIDIA 드라이버 581.57, Vulkan 1.4.341.

---

## 실패 #1 — iGPU가 첫 번째 디바이스로 선택됨

### 증상

- Load Model 클릭 → GPU 메모리 잠깐 상승 → 프로세스 **사일런트 종료** (네이티브 AV)
- `app-log.txt`에 `CoordinatedShutdown` 기록 없음. Dispatcher/AppDomain 핸들러도 안 잡음.

### 원인

`vulkaninfo --summary` 출력:
```
GPU0: AMD Radeon(TM) 890M Graphics          PHYSICAL_DEVICE_TYPE_INTEGRATED_GPU
GPU1: NVIDIA GeForce RTX 4060 Laptop GPU    PHYSICAL_DEVICE_TYPE_DISCRETE_GPU
```

llama.cpp Vulkan 백엔드는 `vkEnumeratePhysicalDevices()`의 첫 결과(=GPU0=iGPU)를 기본 사용. iGPU는 공유 시스템 RAM 사용 → 5 GB GGUF 연속 할당 요청을 WDDM이 거부 → ggml-vulkan 내부 assert → 프로세스 강제 종료.

### 대응

- `GGML_VK_VISIBLE_DEVICES=<dGPU_index>` 환경변수로 **필터링** (CUDA `CUDA_VISIBLE_DEVICES`와 동일 개념)
- 설정 탭 UI "GPU Device" 드롭다운에서 dGPU 선택. `(auto - prefer discrete)` 기본값은 `VulkanDeviceEnumerator.PickDefaultIndex`로 첫 PHYSICAL_DEVICE_TYPE_DISCRETE_GPU 자동 선택

### 검증 신호

```
[LLM] Load start backend=Vulkan vkDev=1 ...    ← vkDev=1이 dGPU 인덱스
```

`vkDev=0`이 iGPU를 가리키면 즉시 크래시 재현됨.

---

## 실패 #2 — 첫 vkCreateDevice가 ErrorExtensionNotPresent 반환

### 증상

- Load Model 1차 시도 → 1.3초 만에 `LLama.Exceptions.LoadWeightsFailedException`
- 재시도 2차 → 성공 (Load complete)
- 채팅 시작 시 `CreateContext` 에서 사일런트 네이티브 AV

### 진짜 에러 메시지 (네이티브 로그 콜백 연결 후 잡힘)

```
[llama.cpp][Error] llama_model_load: error loading model:
    vk::PhysicalDevice::createDevice: ErrorExtensionNotPresent
[llama.cpp][Error] llama_model_load_from_file_impl: failed to load model
```

### 원인

우리 `ggml-vulkan.dll`을 빌드할 때 cmake가 glslc 지원 기준으로 감지한 확장:
```
-- GL_KHR_cooperative_matrix   supported by glslc
-- GL_NV_cooperative_matrix2   supported by glslc
-- GL_EXT_integer_dot_product  supported by glslc
-- GL_EXT_bfloat16             supported by glslc
```

이 확장들은 런타임에 `vkCreateDevice`의 `ppEnabledExtensionNames`로 요청됨. **NVIDIA Optimus 저전력 상태의 dGPU는 콜드 엔티티일 때 고급 확장 목록을 노출하지 않음** → 요청한 확장 중 하나가 없다는 이유로 device 생성 거부.

### 인과 사슬 (사용자 가설 확정)

1. Attempt 1 → ExtensionNotPresent로 실패
2. 실패한 attempt에서 **VkInstance와 일부 내부 allocation이 부분 leak** (ggml-vulkan이 device 생성 실패 경로에서 instance 정리를 보장하지 않음)
3. 1500ms 대기 동안 NVIDIA 드라이버가 dGPU 활성 상태로 전환 — 확장 노출 완료
4. Attempt 2 → vkCreateDevice 성공, weights 로드 성공
5. 하지만 프로세스 내에는 attempt 1의 leaked instance state가 남아있음
6. `llama_new_context_with_model`이 KV cache를 할당하며 shared KV 매핑 테이블을 **leaked state의 포인터로** 참조 → AV

### 대응

로드 직전 환경변수로 문제 확장 비활성:
```csharp
Environment.SetEnvironmentVariable("GGML_VK_DISABLE_COOPMAT", "1");
Environment.SetEnvironmentVariable("GGML_VK_DISABLE_COOPMAT2", "1");
```

효과:
- 첫 vkCreateDevice가 기본 확장만 요구 → cold 드라이버도 통과
- 재시도 불필요 → leak 없음 → CreateContext 깨끗한 상태에서 실행
- 성능 비용: cooperative_matrix 계열 matmul 가속 포기 → 토큰/초 30-50% 감소

### 검증 신호

```
[LLM] Load start backend=Vulkan vkDev=1 ...
[llama.cpp][Warning] load: control-looking token ...       ← 경고만 (무해)
(ErrorExtensionNotPresent 라인이 없음)
[LLM] Load complete (attempt 1)                            ← 첫 시도 성공
[LLM] Session ctor: CreateContext done                     ← 크래시 없음
```

### 여전히 실패 시 후보

`VK_KHR_shader_bfloat16` (Vulkan 1.4 최신)까지 원인이면 환경변수로 못 막음 — llama.cpp 소스 패치 + 재빌드 필요. 메시지에 extension 이름이 함께 찍혀 있으면 정확한 타겟 지정 가능.

---

## 실패 #3 — CreateContext에서 GPU KV offload 경로 AV

### 증상

- weights Load는 성공 (`Load complete`)
- 채팅 Send 또는 New Session 클릭 시 **사일런트 프로세스 종료**
- 로그 마지막 줄: `[LLM] Session ctor: CreateContext begin (ctx=2048, gpuLayers=999)`
- `CreateContext done` 미출력

### 원인 (현재 가설)

Gemma 4의 아키텍처 특성이 Vulkan 백엔드와 충돌:
- **PLE (Per-Layer Embeddings)**: 레이어마다 별도 embedding 텐서
- **SWA (Sliding Window Attention)**: 42개 레이어 중 일부만 window attention
- **KV Cache Sharing**: 여러 레이어가 같은 KV cache 블록 공유

`llama_new_context_with_model`이 GPU에 KV cache를 배치할 때 위 구조의 레이어 매핑 테이블을 구성하는 과정에서 포인터 오남용 발생 추정. llama.cpp 업스트림 버그로 추정되나 이슈 트래커에서 정확 매칭 미확인.

### 대응

`NoKqvOffload = true`로 **KV cache를 시스템 RAM에 유지** (가중치는 GPU 유지):
```csharp
modelParams.NoKqvOffload = true;
```

- 크래시 경로(GPU shared-KV 매핑) 통째 우회
- 가중치 연산은 GPU → 속도 대부분 유지
- KV 전송 왕복이 생겨 ~30-40% 토큰 속도 저하
- 시스템 RAM 비용: ctx=2048 기준 ~500 MB (F16), Q8_0로 바꾸면 ~250 MB

### 검증 신호

```
[LLM] Session ctor: CreateContext begin (ctx=2048, gpuLayers=999)
[LLM] Session ctor: CreateContext done              ← 나와야 함
[LLM] Session ctor: InteractiveExecutor ready
[LLM] Chat session opened
```

### 미해결 상태

실패 #2 대응(COOPMAT 비활성) + 실패 #3 대응(NoKqvOffload) 조합으로 crash-free 경로가 되는지는 **검증 진행 중**. COOPMAT 비활성만으로 실패 #3도 같이 해결될 가능성 있음 (leak 방지가 근본이면 NoKqvOffload 불필요).

---

## 실패 #4 — 시스템 RAM 포화

### 증상

- mmap 기본값(`UseMemoryMap=true`)에서 GGUF 5 GB가 페이지 캐시로 상주 → 시스템 RAM 추가 점유
- `NoKqvOffload=true` 시 KV cache도 RAM으로 → 누적

### 대응

사용 가능한 시스템 RAM에 따라 자동 조정:
- `UseMemoryMap = false` (RAM 타이트 시) — 업로드 후 페이지 반환
- `KvCacheTypeK/V = GGML_TYPE_Q8_0` (RAM 3GB 미만 여유 시) — KV 크기 절반

`LlmAutoRecommend.Compute()`가 `Environment` / `PerformanceCounter`로 측정한 여유 RAM 기반으로 결정. UI `🎯 Auto Recommend` 버튼이 적용.

### 예산 계산 (Gemma 4 E4B Q4_K_XL, 5.1 GB 모델 기준)

| 옵션 | GPU VRAM | 시스템 RAM |
|---|---|---|
| 기본 (NoKqvOffload=false, FA=on) | 5.1 GB 가중치 + 500 MB KV + 500 MB 컴퓨트 = 6.1 GB | mmap 캐시 5 GB (OS 회수 가능) |
| 크래시 회피 (NoKqvOffload=true, FA=off, mmap off) | 5.1 GB 가중치 + 500 MB 컴퓨트 = 5.6 GB | 500 MB KV + 일시 5 GB 업로드 버퍼(반환됨) |
| 타이트 RAM (위 + Q8 KV) | 동일 | 250 MB KV + 일시 5 GB 업로드 버퍼 |

---

## 실패 #5 — 같은 프로세스에서 Unload→Load 재시도 불가

### 증상

- Load 성공 후 Unload 정상 동작
- 재 Load 시도 시 crash 또는 hang

### 원인

llama.cpp + Vulkan은 동일 프로세스 내 dispose→re-init 경로가 clean하지 않음. ggml backend 전역 상태가 dispose 후에도 잔존, 재 init이 이 잔존 상태 위에 쓰려다 충돌.

### 대응

UI 레벨에서 **프로세스당 Load 1회만 허용** (`_hasLoadedOnceInProcess` 가드):
```csharp
if (_hasLoadedOnceInProcess)
{
    tbLlmRuntimeState.Text = "State: Restart the app before loading again...";
    return;
}
```

사용자에게는 "설정 바꾸려면 앱 재시작" 안내. 불편하지만 안정적.

---

## 실패 #6 — 네이티브 에러가 로그에 없음 (진단 불가)

### 증상

- `LLama.Exceptions.LoadWeightsFailedException: Failed to load model` 만 찍힘
- **진짜 이유**(ErrorExtensionNotPresent 등) 은폐
- 디버깅 경로 차단

### 원인

llama.cpp는 stderr로 진단 메시지를 출력함. LLamaSharp가 이를 자동으로 AppLogger 등에 라우팅하지 않음.

### 대응

`NativeLibraryConfig.All.WithLogCallback`으로 네이티브 stderr을 `AppLogger`에 연결:
```csharp
NativeLibraryConfig.All.WithLogCallback((level, msg) =>
{
    if (level < LLamaLogLevel.Warning) return;
    AppLogger.Log($"[llama.cpp][{level}] {msg.TrimEnd('\r', '\n')}");
});
```

DLL 로드 전에 등록. 이 조치로 **실패 #2의 진짜 원인을 처음으로 로그에서 확인** 가능했음. 이 문서의 상당 부분이 이 한 줄 덕분에 특정됨.

---

## 안정 경로 — CPU 백엔드

참고로 CPU 경로는 검증 완료:

```
[LLM] Load start backend=Cpu vkDev=- ctx=2048 gpuLayers=0 ...
[LLM] Load complete (attempt 1)
[LLM] Session ctor: CreateContext done
[LLM] Chat session opened
(멀티턴 채팅 8/8 테스트 통과, 함수콜·세션·KV 재사용 모두 동작)
```

Vulkan 안정화 전까지의 fallback으로 **CPU 백엔드 + `gemma-4-E4B-it-UD-Q4_K_XL.gguf`**가 사용자에게 제시되는 권장 조합. 속도는 CPU 4-8 tok/s 수준.

---

## 크래시 진단 체크리스트 (다음에 겪었을 때)

1. `app-log.txt` 마지막 30줄 확인
2. `[llama.cpp][Error]` 또는 `[llama.cpp][Warning]` 라인 찾기 → 실패 #N 매칭
3. 없으면 → 실패 #6 상황 (로그 콜백 미연결)
4. `[LLM] Session ctor: CreateContext begin` 뒤가 끊겼으면 → 실패 #3
5. `[LLM] Load failed` 만 찍히고 네이티브 에러 없으면 → 실패 #6 재점검 필요

## 재현 가능성 재점검 원칙

이 문서의 각 실패 케이스는 사용자 UI에서 **개별 토글 가능**:
- GPU Device 드롭다운 (#1)
- 네이티브 로그 항상 활성 (#6)
- Flash Attention / NoKqvOffload / KV Type / mmap 체크박스 (#3, #4)
- Auto Recommend 버튼 — 현 머신 상태 기반 안전 기본값

각 옵션을 바꿔 Load → 로그 확인하는 재현 실험이 1사이클 < 1분.
