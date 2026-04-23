# Gemma 4 온디바이스 — 성능 벤치마크

작성일 2026-04-24. `LlmProbe` 벤치 도구로 수집한 실측 데이터. 크래시 진단 중심의 `gemma4-gpu-load-failures.md`와 짝을 이룸.

## 1. 테스트 환경

| 항목 | 값 |
|---|---|
| CPU | AMD Ryzen AI 9 365 (Strix Point, 12C/24T, Zen 5) |
| 시스템 RAM | 31.1 GB DDR5-5600 |
| dGPU | NVIDIA GeForce RTX 4060 Laptop (Ada Lovelace, 8 GB GDDR6) |
| iGPU | AMD Radeon 890M (RDNA 3.5, 16 CU) |
| OS | Windows 11 Home 10.0.26200 |
| NVIDIA Driver | 581.57 |
| Vulkan API | 1.4.312 (드라이버 지원), 1.4.341 (SDK 사용) |
| .NET | 10.0 preview (SDK 10.0.300-preview) |
| LLamaSharp | 0.26.0 |
| llama.cpp | commit `3f7c29d` (2026-04-17) 직접 빌드 |

## 2. 벤치마크 방법론

### 도구: `Project/LlmProbe`

관리 코드 테스트 호스트와 격리된 **서브프로세스**에서 실행. 네이티브 AV 크래시가 테스트 러너를 죽이지 않으며, `NativeLibraryConfig` 프로세스당 1회 제약으로부터 자유로움.

CLI 인터페이스: `LlmProbe.exe <backend> <phase>`

| Phase | 설명 |
|---|---|
| `load` | Weights 로드만 (LLamaWeights.LoadFromFileAsync) |
| `session` | Load + 세션(LLamaContext + KV 캐시) 생성 |
| `complete` | Load + 세션 + 짧은 응답 1회 (smoke 테스트) |
| **`bench`** | **Load → 세션 → 워밍업 1턴 → 6행 시 1회 생성(decode 측정)** |
| `reload` | Load → Session → Dispose → Unload → 재 Load → 새 Session (in-process 재사용성 검증) |

### 벤치마크 프롬프트

```
"Write a short poem about algorithms. Six lines. Just the poem, no preamble."
```

고정 프롬프트로 prefill 비용이 일정하게 유지되도록 함. 워밍업 1턴("Say: ready")이 먼저 돌아서 셰이더 / KV 캐시 / 드라이버 path를 예열. 측정은 워밍업 이후 6행 생성의 **전체 wall-clock 시간 기준**이라 prefill + decode가 포함됨. 토큰/초는 `tokens / elapsed`로 직접 계산.

### 측정 제한

- **Prefill이 포함된 측정치**라 순수 decode throughput보다 낮게 나옴. 짧은 프롬프트 환경 근사로는 유효.
- 단일 실행 기준. 분산 확인 위해 N회 반복 평균은 아직 미적용 (향후 개선 포인트).
- UI Monitor 탭의 "Tokens/sec (last)" 값은 같은 계산식이지만 프롬프트마다 달라 variance 큼. 따라서 공식 비교는 probe bench를 기준.

## 3. 벤치마크 결과 — Gemma 4 E4B (UD-Q4_K_XL, 5.10 GB)

### 구성 매트릭스 (Vulkan 백엔드, RTX 4060)

| # | COOPMAT | NoKqvOffload | BFLOAT16 disable | tokens | 시간 (s) | **tok/s** |
|---|---|---|---|---|---|---|
| A | ON | **ON** (KV on CPU) | ON | 62 | 2.84 | **21.8** |
| B | OFF | ON | ON | 60 | 3.48 | 17.2 |
| C | ON | **OFF** (KV on GPU) | ON | 70 | 14.06 | 🔻 **5.0** |

### 관찰

**A가 최적** (현재 기본 구성).

**A vs B: +26%** — `GGML_VK_DISABLE_COOPMAT`/`COOPMAT2` 해제가 matmul 가속 경로를 복귀시켜 유의미한 이득. RTX 4060(Ada Lovelace)은 `VK_KHR_cooperative_matrix`를 네이티브 지원. 크래시 회피용으로 끄고 있던 건 과잉 방어였음.

**A vs C: +4.4배** (반직관적) — `NoKqvOffload=false`(KV를 GPU에 상주)가 예상과 반대로 **대폭 느려짐**. 로그에 드러난 증거:

```
[llama.cpp][Warning] llama_kv_cache_iswa: using full-size SWA cache
```

Gemma 4의 **Sliding Window Attention** 아키텍처가 GPU에서는 full-size KV 캐시를 padding으로 유지해야 해서 메모리/연산 오버헤드가 폭증. CPU RAM에 KV를 두고 단방향 복사하는 편이 훨씬 저렴함. Gemma 4 고유 현상으로, 다른 아키텍처 모델(Llama/Qwen 등)에서는 통상 `NoKqvOffload=false`가 빠름.

### CPU 백엔드 베이스라인 (probe bench 정식 측정)

| 모델 | tokens | 시간 (s) | **tok/s** |
|---|---|---|---|
| E4B | 61 | 4.83 | **12.6** |
| E2B | 52 | 2.89 | **18.0** |

CPU는 안정적이지만 Vulkan 대비 E4B=1.7배, E2B=3.3배 느림. 크래시 디버깅/폴백 용도로 유효.

## 4. 모델 크기별 성능 — E2B vs E4B (실측)

### Vulkan 백엔드, RTX 4060 Laptop, A 구성 (COOPMAT ON, NoKqvOffload ON)

| 모델 | 파일 크기 | 가중치 유효 | GPU VRAM | Run 1 | Run 2 | **평균 tok/s** |
|---|---|---|---|---|---|---|
| **E4B** UD-Q4_K_XL | 5.10 GB | 4.5B 유효 | ~5.5 GB | 62 tok / 2.84s → 21.8 | — | **21.8** |
| **E2B** UD-Q4_K_XL | 3.17 GB | 2.3B 유효 | ~3.5 GB | 55 tok / 0.94s → 58.6 | 61 tok / 1.01s → 60.4 | **59.5** |

### 관찰

**E2B Vulkan = 59.5 tok/s** — "60 tok/s 달성" 목표 충족 (2.73배 ↑ vs E4B).

이유:
- 가중치 크기 절반 → matmul 연산 절반
- KV 캐시도 절반 → 메모리 대역폭 병목 완화
- Prefill 시간도 절반 → 측정치에 double impact

### 함수콜 품질 주의

E2B는 에이전트용 JSON/`tool_code` 출력 안정성이 E4B보다 눈에 띄게 떨어짐. 단순 대화·번역·요약 용도면 E2B로 다운그레이드 가치 크지만, 툴 호출·구조화 출력 시나리오는 E4B 유지 권장.

## 5. 로드 시간 측정

### Vulkan 백엔드 (E4B, cold + warm)

| 단계 | Cold (프로세스 첫 실행) | Warm (재로드) |
|---|---|---|
| weights Load | ~5.1s | ~4.8s |
| Session 생성 (CreateContext) | ~0.05s | ~0.05s |
| 첫 토큰 prefill | ~0.3s | ~0.2s |

디스크 I/O가 대부분이고 OS 페이지 캐시 덕에 warm은 약간 빠름. `UseMemoryMap=false`면 cold 약간 더 길어지지만 RAM 점유 적음.

### CPU 백엔드 (E4B)

| 단계 | 시간 |
|---|---|
| weights Load (mmap) | 2.5s |
| Session 생성 | <0.1s |
| 첫 토큰 prefill | 1.3s |

CPU는 Vulkan 대비 로드 빠름 (GPU 전송 생략).

## 6. 멀티턴 KV 캐시 재사용 이득

`LlamaSharpLocalChatSession`의 `InteractiveExecutor`는 세션 내내 KV 캐시 유지. 실측 (CPU, ZeroCommon.Tests):

| 턴 | 상황 | 시간 |
|---|---|---|
| 1 | 40문장 프리픽스 prefill + 짧은 응답 | **10,150 ms** |
| 2 | KV 재사용 + 새 짧은 질문 | **1,096 ms** |
| 이득 | | **9.3배** |

에이전트 패턴(긴 시스템 프롬프트 + 반복적 짧은 질문)에서 세션 기반 사용이 singleshot 대비 일관되게 빠름.

## 7. 메모리 풋프린트 실측

### GPU VRAM (nvidia-smi 기준)

| 상태 | 사용 MiB |
|---|---|
| 아이들 (앱 유휴) | ~50 |
| Load 후 (E4B, 가중치 상주) | ~5,400 |
| 세션 개설 후 (ctx=2048, F16 KV) | ~5,500 |
| Unload 후 | ~50 (완전 회수) |

### 시스템 RAM (Process.WorkingSet64)

| 구성 | MB |
|---|---|
| 앱 기동 직후 | ~200 |
| Load (mmap=true) | ~5,500 (페이지 캐시 포함) |
| Load (mmap=false) | ~600 (+ KV 상주분) |
| NoKqvOffload=true, mmap=false, ctx=2048 | ~1,000 |

`mmap=false`가 Process RSS를 확실히 줄이지만 OS 페이지 캐시 자체가 줄어드는 건 아님 (다른 프로세스 I/O 효율 동일).

## 8. 권장 구성

### 일반 사용자 (AgentBot AI Mode)

```
Backend:           Vulkan
GPU Device:        GPU1: NVIDIA RTX 4060 (auto-pick discrete)
Model:             Gemma 4 E4B UD-Q4_K_XL
ContextSize:       2048 (Vulkan 안정)
MaxTokens:         256
Temperature:       0.7
FlashAttention:    ON (V-quant 전제)
NoKqvOffload:      ON (Gemma 4 SWA 최적)
KV Type K/V:       F16 (RAM 여유 있을 때) 또는 Q8_0 (메모리 타이트)
UseMemoryMap:      RAM 여유 기준 (Auto Recommend가 결정)
```

### 최대 속도 (품질 일부 희생)

```
Model:    Gemma 4 E2B UD-Q4_K_XL  ← 모델 체급 다운
나머지:    동일
```

**실측 이득: 2.73배 (21.8 → 59.5 tok/s)**. 함수콜·구조화 출력 신뢰도가 떨어져 에이전트 풀 기능 용도는 비권장, 일반 대화/번역/요약엔 적합.

### 디버그 / 안정 우선

```
Backend:    Cpu
GpuLayers:  0 (자동)
```

Vulkan 문제 재현 시 임시 fallback. ~4-8 tok/s로 느리지만 100% 안정.

## 9. 성능 향상 로드맵

| 단계 | 방법 | 이득 (실측/예상) | 비용 |
|---|---|---|---|
| 현재 (E4B A 구성) | — | 21.8 tok/s | — |
| ✅ 적용됨 | COOPMAT 재활성 | +26% (17.2 → 21.8) | 없음 (과잉 방어 해제) |
| ✅ 적용됨 | E2B 전환 옵션 제공 | **+173% (21.8 → 59.5)** | 함수콜 품질 저하 |
| Mid term | llama.cpp 최신 master 재빌드 + ABI 재검증 | ~1.3배? | 재빌드 시간 |
| Long term | llama.cpp Vulkan SWA GPU-KV 경로 업스트림 수정 편입 시 `NoKqvOffload=false` 재검증 | ~1.3배 (추정) | 업스트림 대기 |

`NoKqvOffload=false` 경로가 현재 4.4배 역손실을 내고 있어서, 업스트림이 이걸 고치면 **E4B도 60 tok/s대 진입 여지**가 있음. 현재는 E2B가 실용적 해법.

## 10. 재현 방법

```bash
# 1. Probe 빌드
dotnet build Project/LlmProbe/LlmProbe.csproj -c Debug

# 2. Vulkan 벤치 (현재 기본 구성 = A 구성)
Project/LlmProbe/bin/Debug/net10.0/LlmProbe.exe Vulkan bench

# 3. 비교군 (예: COOPMAT 끄고 싶을 때)
GGML_VK_DISABLE_COOPMAT=1 GGML_VK_DISABLE_COOPMAT2=1 \
  Project/LlmProbe/bin/Debug/net10.0/LlmProbe.exe Vulkan bench

# 4. NoKqvOffload 토글 (probe의 PROBE_NO_KQV 환경변수)
PROBE_NO_KQV=0 Project/LlmProbe/bin/Debug/net10.0/LlmProbe.exe Vulkan bench

# 5. Reload 경로 검증
Project/LlmProbe/bin/Debug/net10.0/LlmProbe.exe Vulkan reload
```

유닛 테스트로 회귀 방어:
```bash
dotnet test Project/ZeroCommon.Tests/ZeroCommon.Tests.csproj --filter "LlmProbeTests"
```

## 11. 향후 측정 항목 (TODO)

- ✅ E2B 정식 bench — 2026-04-24 완료 (§4)
- N=5 반복 평균 + 표준편차 (현재 E2B는 N=2로 확인, 58.6 / 60.4로 variance ~2%)
- Prefill 분리 측정 (decode-only tok/s)
- 장기 세션 (20턴+) KV 캐시 선형도
- mmap=true vs false의 cold-start 시간 차이
- 컨텍스트 크기 스케일링 (1024 / 2048 / 4096 / 8192) 그래프
- Auto Recommend가 고른 구성이 실제로 수동 최적값에 근접하는지 검증
