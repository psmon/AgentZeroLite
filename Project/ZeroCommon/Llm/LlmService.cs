namespace Agent.Common.Llm;

public enum LlmServiceState { Unloaded, Loading, Loaded, Unloading }

public static class LlmService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static LlmServiceState State { get; private set; } = LlmServiceState.Unloaded;

    public static ILocalLlm? Llm { get; private set; }

    public static LlmRuntimeSettings CurrentSettings { get; private set; } = new();

    public static string? LoadedModelPath { get; private set; }

    public static event Action<LlmServiceState>? StateChanged;

    public static async Task LoadAsync(LlmRuntimeSettings settings, string modelPath, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            if (State == LlmServiceState.Loaded)
                throw new InvalidOperationException("LLM already loaded. Unload first.");

            TransitionTo(LlmServiceState.Loading);
            try
            {
                // On multi-GPU boxes (e.g. laptop with AMD iGPU + NVIDIA dGPU)
                // llama.cpp's Vulkan backend otherwise picks device 0, which is
                // usually the integrated GPU and cannot host a multi-GB GGUF.
                // GGML_VK_VISIBLE_DEVICES filters the Vulkan device list *before*
                // ggml-vulkan.dll enumerates, so the selected dGPU becomes the
                // only device llama.cpp sees.
                if (settings.Backend == LocalLlmBackend.Vulkan && settings.VulkanDeviceIndex >= 0)
                {
                    Environment.SetEnvironmentVariable(
                        "GGML_VK_VISIBLE_DEVICES",
                        settings.VulkanDeviceIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }

                // Observed on Strix-Point laptop + RTX 4060 Laptop GPU (driver
                // 581.57): vk::PhysicalDevice::createDevice returns ErrorExtension
                // NotPresent on the FIRST vkCreateDevice — cold dGPU hasn't
                // published cooperative_matrix / bfloat16 extensions yet. Even
                // after retry succeeds, leaked native state from the failed
                // attempt destabilises later CreateContext. Disabling the
                // bleeding-edge extensions up-front removes the cold-path
                // requirement entirely, so first vkCreateDevice succeeds clean.
                if (settings.Backend == LocalLlmBackend.Vulkan)
                {
                    Environment.SetEnvironmentVariable("GGML_VK_DISABLE_COOPMAT", "1");
                    Environment.SetEnvironmentVariable("GGML_VK_DISABLE_COOPMAT2", "1");
                }

                var opts = settings.ToOptions(modelPath);
                var llm = await LlamaSharpLocalLlm.CreateAsync(opts, ct);
                Llm = llm;
                CurrentSettings = settings;
                LoadedModelPath = modelPath;
                TransitionTo(LlmServiceState.Loaded);
            }
            catch
            {
                TransitionTo(LlmServiceState.Unloaded);
                throw;
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    public static async Task UnloadAsync()
    {
        await Gate.WaitAsync();
        try
        {
            if (State != LlmServiceState.Loaded) return;
            TransitionTo(LlmServiceState.Unloading);
            var llm = Llm;
            Llm = null;
            LoadedModelPath = null;
            if (llm is not null)
                await llm.DisposeAsync();
            TransitionTo(LlmServiceState.Unloaded);
        }
        finally
        {
            Gate.Release();
        }
    }

    public static ILocalChatSession OpenSession()
    {
        var llm = Llm ?? throw new InvalidOperationException("LLM not loaded.");
        return llm.CreateSession();
    }

    private static void TransitionTo(LlmServiceState next)
    {
        State = next;
        try { StateChanged?.Invoke(next); } catch { /* event handler errors are swallowed */ }
    }
}
