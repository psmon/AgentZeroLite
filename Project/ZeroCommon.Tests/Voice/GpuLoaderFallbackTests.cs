using Agent.Common.Voice;

namespace ZeroCommon.Tests.Voice;

public sealed class GpuLoaderFallbackTests
{
    private sealed record FakeFactory(bool UsedGpu, int Index);

    [Fact]
    public void Gpu_success_returns_gpu_result_and_does_not_set_sticky()
    {
        var fallback = new GpuLoaderFallback<FakeFactory>();

        var calls = new List<(bool useGpu, int index)>();
        var result = fallback.Load(
            loader: (g, i) => { calls.Add((g, i)); return new FakeFactory(g, i); },
            requestedUseGpu: true,
            requestedGpuIndex: 1);

        Assert.True(result.UsedGpu);
        Assert.Equal(1, result.UsedGpuIndex);
        Assert.False(fallback.StickyGpuFailed);
        Assert.Equal(new[] { (true, 1) }, calls);
    }

    [Fact]
    public void Gpu_throws_falls_back_to_cpu_and_sets_sticky()
    {
        var fallback = new GpuLoaderFallback<FakeFactory>();
        var logs = new List<string>();
        var fallbackWithLog = new GpuLoaderFallback<FakeFactory>(log: logs.Add);

        var calls = new List<(bool useGpu, int index)>();
        var result = fallbackWithLog.Load(
            loader: (g, i) =>
            {
                calls.Add((g, i));
                if (g) throw new InvalidOperationException("simulated Vulkan SEH");
                return new FakeFactory(g, i);
            },
            requestedUseGpu: true,
            requestedGpuIndex: 1);

        Assert.False(result.UsedGpu);
        Assert.Equal(0, result.UsedGpuIndex);
        Assert.True(fallbackWithLog.StickyGpuFailed);
        Assert.Equal(new[] { (true, 1), (false, 0) }, calls);
        Assert.Contains(logs, msg => msg.Contains("simulated Vulkan SEH"));
    }

    [Fact]
    public void Sticky_already_failed_skips_gpu_attempt()
    {
        var fallback = new GpuLoaderFallback<FakeFactory>();

        // First call: simulate failure to flip sticky.
        fallback.Load(
            loader: (g, _) =>
            {
                if (g) throw new InvalidOperationException("first crash");
                return new FakeFactory(g, 0);
            },
            requestedUseGpu: true,
            requestedGpuIndex: 1);

        Assert.True(fallback.StickyGpuFailed);

        // Second call: loader must NOT be invoked with useGpu=true.
        var calls = new List<(bool useGpu, int index)>();
        var result = fallback.Load(
            loader: (g, i) => { calls.Add((g, i)); return new FakeFactory(g, i); },
            requestedUseGpu: true,
            requestedGpuIndex: 1);

        Assert.False(result.UsedGpu);
        Assert.Equal(new[] { (false, 0) }, calls);
    }

    [Fact]
    public void Gpu_not_requested_goes_straight_to_cpu()
    {
        var fallback = new GpuLoaderFallback<FakeFactory>();

        var calls = new List<(bool useGpu, int index)>();
        var result = fallback.Load(
            loader: (g, i) => { calls.Add((g, i)); return new FakeFactory(g, i); },
            requestedUseGpu: false,
            requestedGpuIndex: 5);

        Assert.False(result.UsedGpu);
        Assert.Equal(0, result.UsedGpuIndex);
        Assert.False(fallback.StickyGpuFailed);
        Assert.Equal(new[] { (false, 0) }, calls);
    }

    [Fact]
    public void Cpu_throw_propagates_and_does_not_set_sticky()
    {
        var fallback = new GpuLoaderFallback<FakeFactory>();

        // CPU-only call that throws — should NOT set sticky (sticky is GPU-only)
        // and should propagate the exception so the caller can react.
        Assert.Throws<InvalidOperationException>(() =>
            fallback.Load(
                loader: (_, _) => throw new InvalidOperationException("cpu broke"),
                requestedUseGpu: false,
                requestedGpuIndex: 0));

        Assert.False(fallback.StickyGpuFailed);
    }

    [Fact]
    public void Gpu_throws_then_cpu_also_throws_propagates_with_sticky_set()
    {
        // Realistic catastrophic case: GPU crashes AND CPU init also fails
        // (e.g. missing model file). The fallback should still set the sticky
        // bit (so future attempts skip GPU) and surface the CPU exception so
        // the caller can show a concrete error.
        var fallback = new GpuLoaderFallback<FakeFactory>();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            fallback.Load(
                loader: (g, _) =>
                {
                    if (g) throw new InvalidOperationException("vulkan crashed");
                    throw new InvalidOperationException("cpu also broke");
                },
                requestedUseGpu: true,
                requestedGpuIndex: 1));

        Assert.Equal("cpu also broke", ex.Message);
        Assert.True(fallback.StickyGpuFailed);
    }
}
