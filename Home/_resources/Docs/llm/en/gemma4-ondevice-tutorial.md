# Embedding Gemma 4 On-Device in .NET — A Story of Failure and Success

> ⚠️ **Read this first — safety & intent disclaimer**
>
> The `llama.dll` / `ggml-*.dll` binaries described here were **self-built solely because I urgently needed Gemma 4 in this project right now**, before the official LLamaSharp NuGet caught up. They are checked in for reproducibility of *my* journey, not as a vetted distribution.
>
> - **I cannot vouch for the safety of the prebuilt DLLs.** Do not use the binaries from this repo as-is. If you need this approach, **build the DLLs yourself from the pinned llama.cpp commit** (or a commit you've audited) and verify the artifacts.
> - **If you know a cleaner path, please share it** — issues / PRs welcome. The whole point of this writeup is to invite better answers.
> - **This is a temporary lifecycle.** The moment LLamaSharp ships Gemma 4 support on NuGet, this entire workaround gets ripped out. The doc exists mostly so (a) future-me / Claude doesn't re-trip the same wires, and (b) the migration back to NuGet is mechanical.
> - **Why we still keep the recipe:** when a new on-device model lands and needs to be experimented with *before* official binding support exists, this self-build path is the fastest experimentation loop. So the technique stays documented even after this specific Gemma 4 instance retires.
>
> TL;DR — treat this as a research log, not a deployment guide.

---

> This tutorial walks through the real-world process of **embedding Google's Gemma 4 directly into a .NET 10 WPF app (AgentZero Lite) without any HTTP server** — from first attempts to final working solution. Written for .NET developers who are new to LLM integration. Every term is explained as it appears. Actual measurements, dead ends, and fixes are all included.

---

## 0. Before You Read

### What We Wanted

> "On a user's PC, with no internet and no separate server like Ollama, have a .NET app **load Gemma 4 directly and generate tokens**."

**Why in-process instead of HTTP?**
- Lower latency (function-call level)
- Simpler deployment (single exe)
- Easier environment control
- No network port conflicts
- Works offline

### The Stack

| Component | Version / Note |
|---|---|
| .NET | 10.0 preview (LlamaSharp supports net8.0+) |
| LLamaSharp | 0.26.0 (C# binding for llama.cpp) |
| llama.cpp | self-built (commit `3f7c29d`) |
| Gemma 4 | E4B / E2B UD-Q4_K_XL (GGUF) |
| Vulkan | cross-vendor GPU API (NVIDIA/AMD/Intel) |

### Terminology (for newcomers)

- **LLM (Large Language Model)**: language models like GPT/Gemma/Llama
- **GGUF**: `.gguf` extension. The **quantized model file format** used by the llama.cpp ecosystem. Weights are compressed to 4-bit/8-bit etc. for memory savings
- **Quantization**: Reducing model size by going from 32-bit float to e.g. 4-bit. Notation like `Q4_K_XL` or `Q8_0`
- **Inference**: Running a prompt through a model to get its output (tokens)
- **LLamaSharp**: P/Invoke wrapper that lets .NET call the `llama.cpp` C/C++ library
- **Vulkan**: A GPU API like DirectX. Works across NVIDIA/AMD/Intel which is a big win for cross-vendor deployment
- **KV Cache (Key-Value Cache)**: Buffer where a Transformer stores the attention state of prior tokens for reuse. Non-trivial memory
- **On-device**: The model lives **in the user's PC memory** and runs directly there — no server involved

---

## 1. First Attempt and First Wall — "Can't we just use LLamaSharp?"

### The naive approach

Install `LLamaSharp` + `LLamaSharp.Backend.Cpu` from NuGet, load a model, done. The usual .NET library pattern.

```xml
<PackageReference Include="LLamaSharp" Version="0.26.0" />
<PackageReference Include="LLamaSharp.Backend.Cpu" Version="0.26.0" />
```

### Three walls we hit

**Wall 1. Gemma 4 is too new**

Google released Gemma 4 on **2026-04-02**. LLamaSharp 0.26.0 NuGet was released on **2026-02-15** — before Gemma 4 existed. The bundled native library (`llama.cpp`) doesn't know the Gemma 4 architecture, so loading a GGUF produces `unknown model architecture: 'gemma4'`.

**Wall 2. ggml symbol collision**

This project also uses `Whisper.net` for speech recognition. Both Whisper.net and LLamaSharp **bundle their own `ggml.dll`**. When loaded in the same process, **whichever loads first overwrites the other's symbols**, breaking one of them reliably.

**Wall 3. onnxruntime-genai isn't ready either**

Microsoft's official path, `Microsoft.ML.OnnxRuntimeGenAI`, doesn't support Gemma 4 either due to three architectural changes: **Per-Layer Embeddings**, **Variable Head Dimensions**, **KV Cache Sharing** ([issue #2062](https://github.com/microsoft/onnxruntime-genai/issues/2062)).

### The realization

**There is a time lag between a new model landing and managed-runtime ecosystems catching up.** You either wait 2–3 months for the NuGet bundles to catch up, or **you build it yourself**.

---

## 2. Strategy — Build llama.cpp Ourselves

### Approach

1. Clone llama.cpp source at a **specific commit** and build it → produce `llama.dll` + `ggml*.dll`
2. Pick the commit whose **ABI matches** LLamaSharp 0.26.0's P/Invoke signatures
3. **Do not use** NuGet's `LLamaSharp.Backend.*` packages (they conflict with our DLLs)
4. In the app, tell LLamaSharp to load **our** DLL via `NativeLibraryConfig.All.WithLibrary(path)`

### Which commit?

LLamaSharp's master branch [PR #1356](https://github.com/SciSharp/LLamaSharp/pull/1356) bumped the llama.cpp submodule to `3f7c29d` specifically for Gemma 4 support. Using this commit:
- Matches the C function signatures LLamaSharp 0.26.0's P/Invoke expects ✓
- Recognises the Gemma 4 model architecture ✓

Using a different commit leads to symbol mismatch errors like `EntryPointNotFoundException` at runtime.

### How this avoids the Whisper.net collision

By calling `NativeLibraryConfig.All.WithLibrary(path)` with our **explicit DLL path**, LLamaSharp bypasses the NuGet bundle lookup entirely. Placing DLLs in a **non-standard RID folder** (`runtimes/win-x64-cpu/native/` instead of `runtimes/win-x64/native/`) also prevents NuGet's automatic loading. So whether Whisper.net loads its ggml first or not, our ggml is loaded independently via our explicit path.

---

## 3. Build Prerequisites

### What you need

- **Visual Studio 2022** (Community or Professional) — MSVC + CMake included
- **Git** — to clone sources
- **Vulkan SDK 1.4.x** (if you want GPU build) — `winget install KhronosGroup.VulkanSDK`

### Prepare a dedicated build folder

```bash
mkdir D:\Code\AI\GemmaNet
```

We keep build artifacts separate from the AgentZero Lite source tree. Cleaner.

### Get the source

```bash
cd D:\Code\AI\GemmaNet
git clone --depth 1 --no-checkout https://github.com/ggml-org/llama.cpp.git
cd llama.cpp
git fetch --depth 1 origin 3f7c29d318e317b63f54c558bc69803963d7d88c
git checkout 3f7c29d318e317b63f54c558bc69803963d7d88c
# HEAD: "ggml: add graph_reused (#21764)"
```

---

## 4. CPU Build (Safe First Step)

### CMake configure

Use the CMake bundled with VS 2022:

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

**Important flags**:
- `BUILD_SHARED_LIBS=ON` — build as DLLs (our .NET loads them)
- `GGML_NATIVE=OFF` — **turn off build-host-specific CPU tuning** (default ON emits AVX-512/AMX instructions that cause `Illegal instruction` on other PCs). Caps at AVX2
- `LLAMA_BUILD_TESTS/EXAMPLES/TOOLS=OFF` — we only want DLLs

### Build

```bash
"$CMAKE" --build build --config Release --target llama -j
```

On success, `build/bin/Release/` contains 4 DLLs:
- `llama.dll` (2.0 MB) — the main P/Invoke target
- `ggml.dll` (67 KB)
- `ggml-base.dll` (614 KB)
- `ggml-cpu.dll` (878 KB) — CPU compute backend

---

## 5. Integrate Into the .NET Project

### DLL layout

In the ZeroCommon project (core logic, WPF-independent shared library), commit the DLLs:

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

### csproj changes

```xml
<ItemGroup>
  <PackageReference Include="LLamaSharp" Version="0.26.0" />
</ItemGroup>

<!-- Intentionally NOT adding Backend.* packages — they'd conflict with our DLLs -->

<ItemGroup>
  <Content Include="runtimes\win-x64-cpu\native\*.dll">
    <Link>runtimes\win-x64-cpu\native\%(Filename)%(Extension)</Link>
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </Content>
</ItemGroup>
```

### Explicit native load

```csharp
using LLama.Native;

var llamaDll = Path.Combine(
    AppContext.BaseDirectory,
    "runtimes", "win-x64-cpu", "native", "llama.dll");

NativeLibraryConfig.All.WithLibrary(
    llamaPath: llamaDll,
    mtmdPath: null);  // mtmd: multi-modal daemon. Not needed now
```

> **0.26 API note**: the second parameter used to be called `llavaPath`, now it's **`mtmdPath`** (multi-modal daemon). Pass null if you don't need vision/audio.

### First inference

```csharp
using LLama;
using LLama.Common;

var parameters = new ModelParams(modelPath)
{
    ContextSize = 2048,
    GpuLayerCount = 0  // CPU mode
};

using var weights = LLamaWeights.LoadFromFile(parameters);
using var context = weights.CreateContext(parameters);
var executor = new StatelessExecutor(weights, parameters);

var inferenceParams = new InferenceParams
{
    MaxTokens = 64,
    AntiPrompts = new[] { "<end_of_turn>" }  // Gemma's turn-end marker
};

// Wrap with the Gemma 4 chat template (important!)
var prompt = $"<start_of_turn>user\nHello<end_of_turn>\n<start_of_turn>model\n";

await foreach (var tok in executor.InferAsync(prompt, inferenceParams))
    Console.Write(tok);
```

### Why the Gemma chat template is mandatory

Gemma is fine-tuned with **`<start_of_turn>user` / `<end_of_turn>` / `<start_of_turn>model`** markers delimiting conversation turns. Without these markers, the model either **rambles incoherently** or **continues your prompt** as if it were a raw completion target. The template is not optional.

### CPU measurements (RTX 4060 Laptop, Ryzen AI 9 365)

- Load: 2.5 seconds
- "hello" generation (including first-token latency): 1.3 seconds
- Usable throughput: 12.6 tok/s (E4B), 18.0 tok/s (E2B)

CPU alone is serviceable. For more speed, we go GPU.

---

## 6. Multi-turn Chat — KV Cache Reuse

### Single-shot vs multi-turn

The `StatelessExecutor` above **starts from state zero on every call**. Tell it "My name is John" then ask "What's my name?" and it has no memory.

### Session-based conversation

```csharp
using var context = weights.CreateContext(parameters);
var executor = new InteractiveExecutor(context);

// Turn 1
var prompt1 = "<start_of_turn>user\nMy name is John<end_of_turn>\n<start_of_turn>model\n";
await foreach (var tok in executor.InferAsync(prompt1, inferenceParams))
    Console.Write(tok);

// Turn 2 — do NOT prepend <end_of_turn> again!
// The anti-prompt match already left it in the context tail.
var prompt2 = "\n<start_of_turn>user\nWhat was my name?<end_of_turn>\n<start_of_turn>model\n";
await foreach (var tok in executor.InferAsync(prompt2, inferenceParams))
    Console.Write(tok);
```

### Pitfall: double `<end_of_turn>`

Something we got wrong initially. We prepended `<end_of_turn>\n<start_of_turn>user\n...` on every turn — the model responded with **empty output**.

Why: LLamaSharp's anti-prompt match **stops generation but leaves the matched string in the output buffer**. So the KV context tail is already `...<end_of_turn>`. If we prepend another `<end_of_turn>`, we get a **doubled token**, and the model sees it as an immediate termination signal.

### Measured KV cache reuse benefit

Within a single session, a long system prompt (40 sentences) followed by short questions:

| Turn | Scenario | Time |
|---|---|---|
| 1 | 40-sentence prefill + short answer | **10,150 ms** |
| 2 | KV reused, new short question | **1,096 ms** |
| Speedup | | **9.3×** |

For agent-style workloads (long system prompt + short repeated questions), sessions are **essential**.

### Cleaning up anti-prompt residue

LLamaSharp's anti-prompt matching does **not** strip the matched text from the output. Tokenizer boundaries sometimes leave only `<` instead of the full `<end_of_turn>`. To present a clean response to the user, strip trailing prefixes manually:

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

Walk from full length down to 1 char, cut the first match.

---

## 7. Going GPU — Vulkan

### Why Vulkan?

| Backend | Pro | Con |
|---|---|---|
| CUDA | Fastest on NVIDIA | NVIDIA-only. Toolkit 2.8 GB |
| **Vulkan** | **Cross-vendor**. SDK 500 MB | Occasional shader compile quirks |
| DirectML | Ships with Windows | Poor official llama.cpp support |

If the deployment target is "any user PC", **Vulkan wins**. SDK needed on build machine only; end users just need the GPU driver.

### Vulkan build

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

Output gains `ggml-vulkan.dll` (~59 MB) — GLSL shaders are compiled and embedded into the DLL, which is why it's large.

### And… crash.

In the GUI, clicking Load → GPU memory briefly rises → **process silently exits**. No exception, app just disappears. Nothing in the log.

---

## 8. The Debugging Journey — Three Hidden Bugs

We had to build diagnostic tooling first.

### Diagnostic tool 1: native log capture

**Problem**: LLamaSharp doesn't automatically route llama.cpp's stderr output. So our log only showed `LoadWeightsFailedException: Failed to load model` while **the real reason was hidden**.

**Fix**: Wire up `NativeLibraryConfig.All.WithLogCallback`:

```csharp
NativeLibraryConfig.All.WithLogCallback((level, msg) =>
{
    if (level < LLamaLogLevel.Warning) return;
    AppLogger.Log($"[llama.cpp][{level}] {msg.TrimEnd('\r', '\n')}");
});
```

**Effect**: The next crash immediately shows the true error in the log.

### Diagnostic tool 2: `LlmProbe` subprocess

A native AV (Access Violation) crash in the WPF app takes the whole app down. Almost impossible to debug. So we built a **separate console executable**:

```
Project/LlmProbe/
├── LlmProbe.csproj
└── Program.cs
```

`Program.cs` is a simple console that references `Agent.Common.Llm`. It takes CLI args for backend/phase, runs Load → Session → token generation, and reports via JSON on stdout + exit code.

```bash
# CPU Load smoke test
LlmProbe.exe Cpu load

# Vulkan full path (Load + Session + tokens)
LlmProbe.exe Vulkan complete

# Benchmark (measure t/s over a 60-token generation)
LlmProbe.exe Vulkan bench
```

**Value**:
- A native AV only kills the probe process; the test runner detects failure via exit code
- Free from the `NativeLibraryConfig.WithLibrary` "once per process" constraint (each test gets a fresh process)
- Can explore env-var matrix configurations automatically

### Bug 1: Integrated GPU (iGPU) picked first

**Symptom**: Vulkan Load → `LoadWeightsFailedException` after 1.3s → second attempt succeeds

**Discovery**: Running `vulkaninfo --summary`:

```
GPU0: AMD Radeon(TM) 890M Graphics          PHYSICAL_DEVICE_TYPE_INTEGRATED_GPU
GPU1: NVIDIA GeForce RTX 4060 Laptop GPU    PHYSICAL_DEVICE_TYPE_DISCRETE_GPU
```

**Confirmation**: This laptop has both an **integrated GPU (iGPU)** from the Ryzen CPU and a **discrete GPU (dGPU)**, NVIDIA. llama.cpp defaults to the first result of `vkEnumeratePhysicalDevices()` → picks GPU0 = iGPU. The iGPU shares system RAM, so trying to allocate a contiguous 5 GB GGUF fails.

**Fix**: Filter devices with the `GGML_VK_VISIBLE_DEVICES` env var:

```csharp
Environment.SetEnvironmentVariable("GGML_VK_VISIBLE_DEVICES", "1");  // dGPU only
```

Same concept as CUDA's `CUDA_VISIBLE_DEVICES`.

### Bug 2: `VK_KHR_shader_bfloat16` extension problem

**Symptom**: Even after isolating the dGPU, first load still fails.

**Discovery**: Enable Vulkan Loader debug:

```bash
VK_LOADER_DEBUG=error,extension LlmProbe.exe Vulkan load
```

Log shows:

```
[Vulkan Loader] ERROR: loader_validate_device_extensions:
Device extension VK_KHR_shader_bfloat16 not supported by selected physical device
```

**Confirmation**: llama.cpp, built against Vulkan SDK 1.4, decided `VK_KHR_shader_bfloat16` is supported and requested it at `vkCreateDevice`. The NVIDIA laptop driver (581.57) **lists it during extension enumeration but rejects it at device creation** — a new-extension driver quirk.

**Fix**: llama.cpp env var to skip the extension request:

```csharp
Environment.SetEnvironmentVariable("GGML_VK_DISABLE_BFLOAT16", "1");
```

### Bug 3: .NET env vars don't reach native `getenv()` (the real villain)

**Symptom**: The env var above is set, yet the load still fails.

**Investigation**:
- In C#, `Environment.GetEnvironmentVariable("GGML_VK_DISABLE_BFLOAT16")` returns `"1"` correctly
- But `getenv("GGML_VK_DISABLE_BFLOAT16")` inside `ggml-vulkan.dll` returns **NULL**
- Setting the variable from the shell (`GGML_VK_DISABLE_BFLOAT16=1 LlmProbe.exe ...`) works fine

**Confirmation**: On Windows, environment variables live in **two places**:

1. **Process environment block** — accessed via `SetEnvironmentVariableW` / `GetEnvironmentVariableW`. .NET's `Environment.SetEnvironmentVariable` writes here
2. **MSVC CRT cache** — copied from (1) once at process startup. After that, `getenv` reads only this cache

**The MSVC CRT does not auto-sync after process start.** `.NET Environment.SetEnvironmentVariable` only updates (1) → `getenv` inside `ggml-vulkan.dll` still sees the old cached value (none).

**Fix**: P/Invoke `ucrtbase.dll`'s `_putenv_s` to also update the CRT cache:

```csharp
[DllImport("ucrtbase.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
private static extern int _putenv_s(string name, string value);

private static void SetNativeEnv(string name, string value)
{
    Environment.SetEnvironmentVariable(name, value);  // Windows env block
    try { _putenv_s(name, value); } catch { }         // MSVC CRT cache
}
```

**Why this matters beyond llama.cpp**: This is not a Gemma/LLamaSharp-specific issue. **Any time you want to dynamically change a configuration value that a native DLL reads via `getenv`, you need this pattern**. Applies equally to ONNX Runtime, CUDA libraries, or any C/C++ integration.

### Combined effect of the three bugs

- Bug 1 (iGPU): `GGML_VK_VISIBLE_DEVICES` — insufficient alone
- Bug 2 (bfloat16): `GGML_VK_DISABLE_BFLOAT16` — insufficient alone (didn't propagate)
- Bug 3 (env propagation): `_putenv_s` — **makes the Bug 2 fix actually take effect**

All three resolved → **first Load attempt succeeds**.

---

## 9. Performance Tuning

### Objective measurement with LlmProbe bench

Same prompt, warm-up then measure:

```csharp
// Program.cs (probe) — bench phase
await session.SendAsync("Say: ready");  // warm-up (excluded)

var sw = Stopwatch.StartNew();
int tokens = 0;
await foreach (var tok in session.SendStreamAsync(
    "Write a short poem about algorithms. Six lines. Just the poem, no preamble."))
    tokens++;
sw.Stop();

Console.WriteLine($"tps={tokens / sw.Elapsed.TotalSeconds:0.0}");
```

### Results (RTX 4060 Laptop, E4B Q4_K_XL)

| Configuration | tok/s | Note |
|---|---|---|
| COOPMAT ON, NoKqv=ON (optimal) | **21.8** | final |
| COOPMAT OFF, NoKqv=ON | 17.2 | -26% |
| COOPMAT ON, NoKqv=**OFF** | 🔻 5.0 | -4x (counterintuitive!) |

### Counterintuitive point: `NoKqvOffload=true` is faster

Normally keeping KV cache **on the GPU** should be faster — no per-token CPU↔GPU round-trip.

But **Gemma 4's Sliding Window Attention (SWA)** architecture on the Vulkan GPU-KV path emits `llama_kv_cache_iswa: using full-size SWA cache` and maintains a padded full-size cache — which is **slower**. This is a Gemma-4-plus-Vulkan-specific quirk (other models/backends behave normally).

Lesson: **Don't trust "obvious optimisations" without measuring.**

### Flash Attention

- Streams attention in tiled fashion → never materialises the intermediate attention matrix
- 2–4× faster prefill + significant memory savings
- **V-cache quantization (Q8_0 V) requires Flash Attention ON**

### Model choice: E4B vs E2B

| Model | File size | probe bench | Real UI usage |
|---|---|---|---|
| **E4B** Q4_K_XL | 5.1 GB | 21.8 tok/s | ~40 tok/s (multi-turn KV reuse) |
| **E2B** Q4_K_XL | 3.2 GB | 59.5 tok/s | 60+ tok/s |

E2B is 2.7× faster than E4B. But **function-calling reliability (tool_code/JSON) is noticeably weaker on E2B** → use E4B for agents, E2B for general chat/translation/summarisation.

### Final recommended configuration

```
Backend:           Vulkan
GPU Device:        dGPU (auto: first discrete GPU)
Model:             E4B Q4_K_XL (agent) / E2B Q4_K_XL (speed)
ContextSize:       2048
FlashAttention:    ON
NoKqvOffload:      ON   ← correct for Gemma 4
KvCacheTypeK/V:    F16 (default) / Q8_0 (tight memory)
UseMemoryMap:      decide based on free RAM
```

---

## 10. Wrap-up — What We Got

### Final architecture

```
AgentZero Lite (WPF .NET 10)
├── ZeroCommon.csproj
│   ├── LLamaSharp 0.26.0 (NuGet, no Backend.*)
│   ├── Agent.Common.Llm/ (ILocalLlm, LlamaSharpLocalLlm, LlmService, ...)
│   └── runtimes/
│       ├── win-x64-cpu/native/     (4 DLLs, 3.5 MB)
│       └── win-x64-vulkan/native/  (5 DLLs, 64 MB)
└── AgentZeroWpf.csproj
    └── UI/Components/SettingsPanel.Llm.cs (settings tab + Load/Unload/Chat)

Project/LlmProbe/        ← subprocess bench/test tool
Project/ZeroCommon.Tests/LlmProbeTests.cs  ← regression guard
```

### Five generalisable lessons

1. **The NuGet ecosystem lags behind new models** — 2–3 months. If you need the latest, plan for a self-build path.

2. **Always route native logs into AppLogger** — C# exceptions hide native error messages. One line of `NativeLibraryConfig.WithLogCallback` saved our debugging story.

3. **Subprocess-based probes are high-ROI tooling** — native AV isolation + env-var matrix exploration + regression tests. One hour of setup, permanently useful.

4. **`.NET Environment.SetEnvironmentVariable` does not reach native `getenv`** — remember the `_putenv_s` P/Invoke companion pattern. Applies beyond llama.cpp.

5. **Verify "obvious optimisations" by measuring**. Gemma 4's `NoKqvOffload=false` being 4× slower was a reminder that architecture-specific quirks only surface in benchmarks.

### Where to go next

- Integrate with the main AgentBot UI (currently experimental in Settings tab)
- Rebuild with newer llama.cpp master — pick up Gemma 4 GPU-KV upstream improvements
- Push Whisper.net to the iGPU (DirectML) so it can run in parallel with Gemma on NVIDIA (Gemma=NVIDIA, Whisper=AMD iGPU)
- Embedding-based local RAG (same Gemma model reusable)

---

## Reference Documents

This tutorial reconstructs three detailed docs into a narrative. Dive deeper via:

- [`Docs/resaerch-geema4.md`](../../resaerch-geema4.md) — full research and implementation log
- [`Docs/gemma4-gpu-load-failures.md`](../../gemma4-gpu-load-failures.md) — crash case catalogue (7 cases)
- [`Docs/gemma4-performance-benchmarks.md`](../../gemma4-performance-benchmarks.md) — performance benchmark matrix

### External references

- [llama.cpp PR #21309 — Gemma 4 support](https://github.com/ggml-org/llama.cpp/pull/21309)
- [LLamaSharp PR #1356 — Gemma 4 ABI-compatible commit](https://github.com/SciSharp/LLamaSharp/pull/1356)
- [HuggingFace: unsloth/gemma-4-E4B-it-GGUF](https://huggingface.co/unsloth/gemma-4-E4B-it-GGUF)
- [HuggingFace: unsloth/gemma-4-E2B-it-GGUF](https://huggingface.co/unsloth/gemma-4-E2B-it-GGUF)
