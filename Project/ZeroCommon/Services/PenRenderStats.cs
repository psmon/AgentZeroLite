namespace Agent.Common.Services;

/// <summary>
/// Parse stats returned by Analyze/Render — testable without WPF rendering.
/// </summary>
public sealed class PenRenderStats
{
    public int FrameCount { get; set; }
    public int ColorTokenCount { get; set; }
    public int TotalElements { get; set; }
    public int RenderedElements { get; set; }
    public int SkippedElements { get; set; }
    public int ErrorElements { get; set; }
    public Dictionary<string, int> TypeCounts { get; } = [];
    public List<string> Errors { get; } = [];
    public List<string> Skipped { get; } = [];

    public void CountType(string type)
    {
        TotalElements++;
        if (!TypeCounts.TryGetValue(type, out _))
            TypeCounts[type] = 0;
        TypeCounts[type]++;
    }

    public void Rendered(string type, string name)
    {
        RenderedElements++;
    }

    public void Skip(string type, string name, string reason)
    {
        SkippedElements++;
        Skipped.Add($"[{type}] {name}: {reason}");
    }

    public void Error(string type, string name, string message)
    {
        ErrorElements++;
        Errors.Add($"[{type}] {name}: {message}");
    }
}
