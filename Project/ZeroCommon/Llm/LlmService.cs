using System.Runtime.InteropServices;

namespace Agent.Common.Llm;

public enum LlmServiceState { Unloaded, Loading, Loaded, Unloading }

public static class LlmService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    // On Windows, .NET's Environment.SetEnvironmentVariable updates the
    // process environment block seen by GetEnvironmentVariable, but it does
    // NOT propagate into the MSVC CRT's cached env block used by getenv().
    // llama.cpp / ggml-vulkan read env vars via getenv(), so managed-only
    // updates are invisible to them for options set after process start.
    // Route each env var through both paths: .NET for tooling, _putenv_s for
    // the native CRT. See realization in Docs/gemma4-gpu-load-failures.md.
    [DllImport("ucrtbase.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern int _putenv_s(string name, string value);

    private static void SetNativeEnv(string name, string value)
    {
        Environment.SetEnvironmentVariable(name, value);
        try { _putenv_s(name, value); } catch { /* fall back silently */ }
    }

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
                    SetNativeEnv(
                        "GGML_VK_VISIBLE_DEVICES",
                        settings.VulkanDeviceIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }

                // Observed on Strix-Point laptop + RTX 4060 Laptop GPU (driver
                // 581.57): vk::PhysicalDevice::createDevice returns
                // ErrorExtensionNotPresent despite vkEnumerateDeviceExtension
                // Properties listing the extension — NVIDIA driver reports
                // VK_KHR_shader_bfloat16 as available but rejects it at device
                // creation time (new-extension driver quirk). The same pattern
                // was previously suspected for cooperative_matrix; disabling
                // the full set of bleeding-edge opt-ins is the only reliable
                // fix for first-attempt stability.
                //
                // ggml-vulkan.cpp only adds each extension to pEnabledExtensions
                // when its probe is non-null, so setting these env vars makes
                // the whole driver capability check return false and the
                // extension is never requested.
                if (settings.Backend == LocalLlmBackend.Vulkan)
                {
                    // The one essential disable: VK_KHR_shader_bfloat16 is
                    // advertised by NVIDIA laptop drivers during device
                    // enumeration but rejected at vkCreateDevice time. This is
                    // the actual root cause of the ErrorExtensionNotPresent
                    // crash — other extensions (coopmat, integer_dot_product,
                    // etc.) were initially suspected but verified innocent via
                    // LlmProbe. Keep this disable on.
                    SetNativeEnv("GGML_VK_DISABLE_BFLOAT16", "1");

                    // COOPMAT / COOPMAT2 / INTEGER_DOT_PRODUCT are kept ENABLED
                    // on purpose: they deliver substantial matmul speedups on
                    // Ada Lovelace (RTX 4060). Disabling them as a crash
                    // workaround cost ~20-30 t/s with zero safety benefit once
                    // the bfloat16 issue was identified. LlmProbe confirms the
                    // Vulkan path is crash-free with these enabled.
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
