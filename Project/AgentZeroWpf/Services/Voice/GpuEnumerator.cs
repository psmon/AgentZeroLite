using System.Management;

namespace AgentZeroWpf.Services.Voice;

/// <summary>
/// Lightweight GPU discovery via WMI <c>Win32_VideoController</c>. The list
/// is purely informational — it lets the Voice settings show real adapter
/// names beside the index picker — and powers the "Auto (best)" heuristic.
///
/// ⚠️ WMI's enumeration order is not strictly guaranteed to match Vulkan's
/// physical-device order, so the index passed to <c>WhisperFactoryOptions
/// .GpuDevice</c> may not always select the named adapter on multi-GPU
/// systems. On the common case (single dGPU, or dGPU + integrated where the
/// dGPU is the Vulkan default) this works fine; users with weirder setups
/// can override the index manually.
/// </summary>
public static class GpuEnumerator
{
    public sealed record GpuAdapter(int Index, string Name, long VramBytes, int VendorScore);

    public static IReadOnlyList<GpuAdapter> Enumerate()
    {
        var list = new List<GpuAdapter>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, AdapterRAM, AdapterCompatibility FROM Win32_VideoController");
            int i = 0;
            foreach (ManagementObject mo in searcher.Get())
            {
                var name = (mo["Name"] as string ?? "Unknown").Trim();
                var ramObj = mo["AdapterRAM"];
                long ram = ramObj is null ? 0 : Convert.ToInt64(ramObj);
                list.Add(new GpuAdapter(i++, name, ram, ScoreVendor(name)));
            }
        }
        catch
        {
            // WMI can fail in restricted environments — return empty list and
            // let the caller fall through to Vulkan default (index 0).
        }
        return list;
    }

    /// <summary>
    /// Pick the highest-scoring adapter index. Returns 0 if no adapters
    /// reported (Vulkan loader will pick its own default in that case).
    /// </summary>
    public static int PickBestIndex(IReadOnlyList<GpuAdapter>? adapters = null)
    {
        var list = adapters ?? Enumerate();
        if (list.Count == 0) return 0;

        GpuAdapter best = list[0];
        long bestScore = ComposeScore(best);
        for (int i = 1; i < list.Count; i++)
        {
            var s = ComposeScore(list[i]);
            if (s > bestScore) { best = list[i]; bestScore = s; }
        }
        return best.Index;
    }

    // VendorScore dominates VRAM tie-breaks: a 4 GB NVIDIA discrete still
    // beats a 4 GB integrated Intel because vendor weighting is larger than
    // any plausible VRAM delta.
    private static long ComposeScore(GpuAdapter a) =>
        (long)a.VendorScore * 1_000_000_000L + a.VramBytes;

    private static int ScoreVendor(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("nvidia") || n.Contains("geforce") || n.Contains("rtx") || n.Contains("gtx"))
            return 300;
        if (n.Contains("radeon") || n.Contains("amd") || n.Contains("rx "))
            return 200;
        if (n.Contains("arc"))           // Intel Arc (discrete)
            return 150;
        if (n.Contains("intel"))         // Intel iGPU
            return 50;
        return 10;
    }
}
