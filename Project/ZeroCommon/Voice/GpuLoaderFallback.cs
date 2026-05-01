namespace Agent.Common.Voice;

/// <summary>
/// "Try GPU, fall back to CPU on any throw, never retry GPU after a failure"
/// loader policy for native AI runtimes (Whisper.net Vulkan, LLamaSharp Vulkan, …).
///
/// Lives in ZeroCommon so it can be unit-tested headlessly with a mocked loader
/// delegate — the actual native init only runs from WPF-side callsites that
/// pass <c>WhisperFactory.FromPath</c> (or equivalent) as the loader.
/// </summary>
public sealed class GpuLoaderFallback<TFactory>
{
    private bool _stickyGpuFailed;
    private readonly Action<string>? _log;

    public GpuLoaderFallback(Action<string>? log = null)
    {
        _log = log;
    }

    /// <summary>True if a previous Load attempt threw on GPU init.</summary>
    public bool StickyGpuFailed => _stickyGpuFailed;

    public sealed record LoadResult(TFactory Factory, bool UsedGpu, int UsedGpuIndex);

    /// <summary>
    /// Load via <paramref name="loader"/>(useGpu, gpuIndex). When
    /// <paramref name="requestedUseGpu"/> is true and we haven't already
    /// recorded a GPU failure, try GPU first; on any thrown exception, set
    /// the sticky flag and retry on CPU.
    /// </summary>
    public LoadResult Load(
        Func<bool, int, TFactory> loader,
        bool requestedUseGpu,
        int requestedGpuIndex)
    {
        var attemptGpu = requestedUseGpu && !_stickyGpuFailed;

        if (!attemptGpu)
        {
            return new LoadResult(loader(false, 0), UsedGpu: false, UsedGpuIndex: 0);
        }

        try
        {
            var factory = loader(true, requestedGpuIndex);
            return new LoadResult(factory, UsedGpu: true, UsedGpuIndex: requestedGpuIndex);
        }
        catch (Exception ex)
        {
            _stickyGpuFailed = true;
            _log?.Invoke($"GPU init failed — falling back to CPU | {ex.GetType().Name}: {ex.Message}");
            return new LoadResult(loader(false, 0), UsedGpu: false, UsedGpuIndex: 0);
        }
    }
}
