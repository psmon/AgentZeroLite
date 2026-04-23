using LLama.Native;

namespace Agent.Common.Llm;

public sealed record LlmRecommendation(
    LlmRuntimeSettings Settings,
    IReadOnlyList<string> Reasons);

public sealed record LlmSystemSnapshotInput(
    long ModelFileBytes,
    long FreeGpuMb,
    long TotalGpuMb,
    long FreeRamMb,
    long TotalRamMb,
    IReadOnlyList<VulkanDeviceInfo> VulkanDevices,
    string ModelFileName);

// Heuristics for picking a safe+fast configuration given the machine state
// at Load time. Encodes the crash-triage knowledge gathered while making
// Gemma 4 work on a laptop NVIDIA dGPU + AMD iGPU + Vulkan stack:
//
//   - Vulkan Gemma 4 GPU-KV offload path crashes in llama_new_context_with_model
//     → NoKqvOffload must be ON whenever backend=Vulkan.
//   - Flash Attention on the Vulkan+SWA path is unstable → OFF for Vulkan.
//   - mmap keeps ~model-size of page cache resident in system RAM; when the
//     machine is already RAM-tight, disable it to free that after GPU upload.
//   - If system RAM is extremely tight even for KV cache, fall back to CPU
//     backend (stable) or quantize KV (Q8_0 halves the RAM cost).
//   - iGPU must never be the Vulkan target for a 5 GB+ model → prefer discrete.
public static class LlmAutoRecommend
{
    public static LlmRecommendation Compute(LlmSystemSnapshotInput x)
    {
        var reasons = new List<string>();
        var modelMb = x.ModelFileBytes / (1024 * 1024);

        // ---- Backend decision ----
        LocalLlmBackend backend;
        var discrete = x.VulkanDevices.FirstOrDefault(d => d.IsDiscrete);

        if (discrete is not null && x.FreeGpuMb > modelMb + 1500)
        {
            backend = LocalLlmBackend.Vulkan;
            reasons.Add($"Vulkan picked — discrete GPU '{discrete.Name}' has {x.FreeGpuMb} MB free (need ~{modelMb + 1500} MB).");
        }
        else if (discrete is not null)
        {
            backend = LocalLlmBackend.Vulkan;
            reasons.Add($"Vulkan with tight GPU budget ({x.FreeGpuMb} MB free vs ~{modelMb + 1500} MB needed). Still preferred over CPU.");
        }
        else
        {
            backend = LocalLlmBackend.Cpu;
            reasons.Add("No discrete GPU detected — CPU backend is the stable choice.");
        }

        // ---- Vulkan device index ----
        var vkIndex = discrete?.Index ?? -1;
        if (backend == LocalLlmBackend.Vulkan && vkIndex >= 0)
            reasons.Add($"GPU Device = GPU{vkIndex} (skipping iGPU).");

        // ---- Context size (driven by KV budget once NoKqvOffload policy is set) ----
        // Rough E4B KV cost per token ≈ 170 KB at F16, 85 KB at Q8_0.
        // We keep 2048 as safe default unless headroom is comfortable.
        uint ctx = 2048;
        if (backend == LocalLlmBackend.Vulkan && x.FreeRamMb > 4000)
        {
            ctx = 4096;
            reasons.Add($"ContextSize 4096 — RAM free ({x.FreeRamMb} MB) absorbs KV cache on CPU comfortably.");
        }
        else if (backend == LocalLlmBackend.Cpu)
        {
            ctx = x.FreeRamMb > 10_000 ? 4096u : 2048u;
            reasons.Add($"CPU backend — ctx {ctx} based on {x.FreeRamMb} MB free RAM.");
        }
        else
        {
            reasons.Add($"ContextSize 2048 — conservative given {x.FreeRamMb} MB free RAM.");
        }

        // ---- KV cache policy ----
        // Vulkan + Gemma 4 GPU KV path is the primary known crash source.
        bool noKqv = backend == LocalLlmBackend.Vulkan;
        if (noKqv)
            reasons.Add("No KQV Offload = ON — Vulkan + Gemma 4 GPU-KV path triggers native AV in CreateContext.");

        // KV type quantization — shrink when RAM is tight
        GGMLType kvType = GGMLType.GGML_TYPE_F16;
        if (noKqv && x.FreeRamMb < 3000)
        {
            kvType = GGMLType.GGML_TYPE_Q8_0;
            reasons.Add("KV Type K/V = Q8_0 — halves KV RAM cost (~170 KB → ~85 KB per token).");
        }

        // ---- mmap ----
        // Keep model pages resident only when RAM is abundant, else release
        // after GPU upload so weights don't double-book against OS cache.
        bool useMmap = x.FreeRamMb > (modelMb + 2000);
        if (!useMmap)
            reasons.Add($"mmap OFF — free RAM ({x.FreeRamMb} MB) would be saturated by model page cache (~{modelMb} MB).");
        else
            reasons.Add($"mmap ON — free RAM ({x.FreeRamMb} MB) has headroom beyond model size.");

        // ---- Flash Attention ----
        // Known-unstable with Gemma 4 + Vulkan; safe on CPU but marginal gain.
        bool flashAttn = false;
        reasons.Add("Flash Attention = OFF — Gemma 4 SWA + FA path has reported crashes; stability > 10% throughput.");

        // ---- GPU Layer count ----
        int gpuLayers;
        if (backend == LocalLlmBackend.Vulkan)
        {
            // Full offload when weights fit comfortably; else cap to keep headroom
            if (x.FreeGpuMb >= modelMb + 800)
            {
                gpuLayers = 999;
                reasons.Add("GPU Layers = 999 (full offload) — weights fit with headroom.");
            }
            else
            {
                // Rough estimate: each E4B layer ~= modelMb / 42
                gpuLayers = Math.Max(1, (int)((x.FreeGpuMb - 600) * 42 / Math.Max(1, modelMb)));
                reasons.Add($"GPU Layers = {gpuLayers} (partial offload) — VRAM tight.");
            }
        }
        else
        {
            gpuLayers = 0;
        }

        // ---- Other tunables ----
        int maxTokens = backend == LocalLlmBackend.Cpu ? 128 : 256;
        if (backend == LocalLlmBackend.Cpu)
            reasons.Add("Max Tokens = 128 (CPU inference is slow, keep responses tight).");

        var settings = new LlmRuntimeSettings
        {
            Backend = backend,
            ContextSize = ctx,
            MaxTokens = maxTokens,
            Temperature = 0.7f,
            GpuLayerCount = gpuLayers,
            VulkanDeviceIndex = vkIndex,
            FlashAttention = flashAttn,
            NoKqvOffload = noKqv,
            KvCacheTypeK = kvType,
            KvCacheTypeV = kvType,
            UseMemoryMap = useMmap
        };

        return new LlmRecommendation(settings, reasons);
    }
}
