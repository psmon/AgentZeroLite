using Agent.Common.Llm;

namespace ZeroCommon.Tests.Llm;

public sealed class GpuIndexPickerTests
{
    private static IReadOnlyList<VulkanDeviceInfo> NoDevices() => Array.Empty<VulkanDeviceInfo>();

    [Fact]
    public void Vulkan_with_discrete_picks_discrete_index_and_reports_source()
    {
        // Hybrid laptop: AMD iGPU at 0, NVIDIA dGPU at 1. PickDefaultIndex should
        // pick the discrete (NVIDIA) at 1.
        IReadOnlyList<VulkanDeviceInfo> devices =
        [
            new VulkanDeviceInfo(0, "AMD Radeon 890M",   IsDiscrete: false, VendorId: 0x1002),
            new VulkanDeviceInfo(1, "NVIDIA RTX 4060",   IsDiscrete: true,  VendorId: 0x10DE),
        ];

        var wmiCalled = false;
        var result = GpuIndexPicker.PickAuto(
            vulkanProbe: () => devices,
            wmiFallback: () => { wmiCalled = true; return 99; });

        Assert.Equal(1, result.Index);
        Assert.Equal("vulkan", result.Source);
        Assert.NotNull(result.Description);
        Assert.Contains("NVIDIA", result.Description);
        Assert.False(wmiCalled);
    }

    [Fact]
    public void Vulkan_only_integrated_picks_first_index()
    {
        // No discrete GPU — picker falls through to first device.
        IReadOnlyList<VulkanDeviceInfo> devices =
        [
            new VulkanDeviceInfo(0, "Intel UHD",  IsDiscrete: false, VendorId: 0x8086),
        ];

        var result = GpuIndexPicker.PickAuto(
            vulkanProbe: () => devices,
            wmiFallback: () => throw new InvalidOperationException("must not call WMI"));

        Assert.Equal(0, result.Index);
        Assert.Equal("vulkan", result.Source);
        Assert.Contains("Intel UHD", result.Description);
    }

    [Fact]
    public void Vulkan_empty_falls_back_to_wmi()
    {
        // vulkaninfo not installed — picker delegates to WMI helper.
        var result = GpuIndexPicker.PickAuto(
            vulkanProbe: NoDevices,
            wmiFallback: () => 1);

        Assert.Equal(1, result.Index);
        Assert.Equal("wmi", result.Source);
        Assert.Null(result.Description);
    }

    [Fact]
    public void Vulkan_probe_throws_propagates_so_caller_can_log()
    {
        // The Vulkan probe itself shouldn't throw (it catches its own errors and
        // returns empty), but if it does we surface the exception so the caller
        // can decide policy. This test pins that contract.
        Assert.Throws<InvalidOperationException>(() =>
            GpuIndexPicker.PickAuto(
                vulkanProbe: () => throw new InvalidOperationException("vulkaninfo crashed"),
                wmiFallback: () => 0));
    }

    [Fact]
    public void Vulkan_non_zero_indexed_discrete_passes_correct_index()
    {
        // Edge case: Vulkan reports discrete at non-zero index (rare but not
        // unheard of on systems where the integrated GPU is enumerated first).
        // Picker must pass the device's reported Index, not the array position.
        IReadOnlyList<VulkanDeviceInfo> devices =
        [
            new VulkanDeviceInfo(0, "Intel iGPU",  IsDiscrete: false, VendorId: 0x8086),
            new VulkanDeviceInfo(2, "NVIDIA dGPU", IsDiscrete: true,  VendorId: 0x10DE),
        ];

        var result = GpuIndexPicker.PickAuto(
            vulkanProbe: () => devices,
            wmiFallback: () => 0);

        Assert.Equal(2, result.Index);
    }
}
