namespace Agent.Common.Llm;

/// <summary>
/// Picks the GPU device index to pass to native AI runtimes (Whisper.net,
/// LLamaSharp) when the user requested "auto" selection.
///
/// Vulkan probe wins when available — its indexing matches whisper.cpp /
/// llama.cpp's <c>ggml_backend_vk_*</c> ordering. The WMI probe is a fallback
/// for systems where <c>vulkaninfo</c> isn't installed (rare on machines that
/// have a usable Vulkan loader, but possible on stripped-down installs).
///
/// Pure decision logic — both probes are injected so this can be exercised
/// headlessly without touching real hardware.
/// </summary>
public static class GpuIndexPicker
{
    public sealed record PickResult(int Index, string Source, string? Description);

    /// <summary>
    /// <paramref name="vulkanProbe"/> should return Vulkan-side device list
    /// (canonically <see cref="VulkanDeviceEnumerator.Enumerate"/>);
    /// <paramref name="wmiFallback"/> returns a WMI-ordered index. Both must
    /// be safe to call (catch their own errors and return empty/0).
    /// </summary>
    public static PickResult PickAuto(
        Func<IReadOnlyList<VulkanDeviceInfo>> vulkanProbe,
        Func<int> wmiFallback)
    {
        var vulkan = vulkanProbe();
        if (vulkan.Count > 0)
        {
            var idx = VulkanDeviceEnumerator.PickDefaultIndex(vulkan);
            var picked = vulkan.FirstOrDefault(d => d.Index == idx);
            return new PickResult(idx, "vulkan", picked?.ToString());
        }

        return new PickResult(wmiFallback(), "wmi", null);
    }
}
