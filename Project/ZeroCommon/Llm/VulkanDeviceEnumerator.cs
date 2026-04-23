using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Agent.Common.Llm;

public static class VulkanDeviceEnumerator
{
    private static readonly Regex GpuHeader = new(@"^GPU(\d+):", RegexOptions.Compiled);
    private static readonly Regex NameLine = new(@"deviceName\s*=\s*(.+)$", RegexOptions.Compiled);
    private static readonly Regex TypeLine = new(@"deviceType\s*=\s*(\S+)", RegexOptions.Compiled);
    private static readonly Regex VendorLine = new(@"vendorID\s*=\s*0x([0-9A-Fa-f]+)", RegexOptions.Compiled);

    // Runs `vulkaninfo --summary` (ships with Vulkan runtime / SDK) and parses
    // the device list. Returns empty list if the tool is missing or fails —
    // caller should treat that as "unknown, let native default pick".
    public static IReadOnlyList<VulkanDeviceInfo> Enumerate()
    {
        var text = RunVulkanInfo();
        if (string.IsNullOrEmpty(text)) return Array.Empty<VulkanDeviceInfo>();

        var results = new List<VulkanDeviceInfo>();
        int? currentIdx = null;
        string? currentName = null;
        bool? currentDiscrete = null;
        uint? currentVendor = null;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            var hm = GpuHeader.Match(line);
            if (hm.Success)
            {
                Flush();
                currentIdx = int.Parse(hm.Groups[1].Value, CultureInfo.InvariantCulture);
                currentName = null;
                currentDiscrete = null;
                currentVendor = null;
                continue;
            }
            if (currentIdx is null) continue;

            var nm = NameLine.Match(line);
            if (nm.Success) { currentName = nm.Groups[1].Value.Trim(); continue; }

            var tm = TypeLine.Match(line);
            if (tm.Success)
            {
                var t = tm.Groups[1].Value;
                currentDiscrete = t.Contains("DISCRETE", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            var vm = VendorLine.Match(line);
            if (vm.Success && uint.TryParse(vm.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
                currentVendor = v;
        }
        Flush();

        return results;

        void Flush()
        {
            if (currentIdx is int idx && currentName is not null)
            {
                results.Add(new VulkanDeviceInfo(
                    idx,
                    currentName,
                    currentDiscrete ?? false,
                    currentVendor ?? 0));
            }
        }
    }

    // Pick a sane default device: first discrete GPU, else first GPU, else -1.
    public static int PickDefaultIndex(IReadOnlyList<VulkanDeviceInfo> devices)
    {
        if (devices.Count == 0) return -1;
        var discrete = devices.FirstOrDefault(d => d.IsDiscrete);
        return discrete?.Index ?? devices[0].Index;
    }

    private static string RunVulkanInfo()
    {
        foreach (var exe in CandidatePaths())
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = "--summary",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p is null) continue;
                var output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(3000);
                if (!string.IsNullOrWhiteSpace(output)) return output;
            }
            catch { /* try next candidate */ }
        }
        return string.Empty;
    }

    private static IEnumerable<string> CandidatePaths()
    {
        var sdk = Environment.GetEnvironmentVariable("VULKAN_SDK");
        if (!string.IsNullOrEmpty(sdk))
        {
            yield return Path.Combine(sdk, "Bin", "vulkaninfoSDK.exe");
            yield return Path.Combine(sdk, "Bin", "vulkaninfo.exe");
        }
        yield return Path.Combine(Environment.SystemDirectory, "vulkaninfo.exe");
        yield return "vulkaninfo"; // PATH lookup
    }
}
