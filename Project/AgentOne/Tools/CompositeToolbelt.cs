using AgentOne.Agent;

namespace AgentOne.Tools;

/// <summary>
/// Routes each verb to the belt that owns it, so a capability is added by
/// writing one more <see cref="IToolbelt"/> rather than by growing one switch
/// until nobody can see what the agent is allowed to touch.
///
/// Ownership comes from <see cref="ToolCatalog"/>: a verb's spec names its
/// family, and a test asserts every family here has a belt.
/// </summary>
public sealed class CompositeToolbelt : IToolbelt, IDisposable
{
    private readonly Dictionary<string, IToolbelt> _byFamily;

    public CompositeToolbelt(params (string Family, IToolbelt Belt)[] belts)
    {
        _byFamily = belts.ToDictionary(b => b.Family, b => b.Belt, StringComparer.OrdinalIgnoreCase);
        Scope = string.Join(" · ", belts.Select(b => $"{b.Family}: {b.Belt.Scope}"));
    }

    public string Scope { get; }

    public async Task<ToolResult> InvokeAsync(ToolCall call, CancellationToken ct)
    {
        var spec = ToolCatalog.Find(call.Tool);

        if (spec is null)
            return ToolResult.Failure(
                $"unknown tool '{call.Tool}'. Available: {string.Join(", ", ToolCatalog.All.Select(t => t.Name))}");

        if (!_byFamily.TryGetValue(spec.Family, out var belt))
            return ToolResult.Failure($"'{call.Tool}' is not available in this session");

        return await belt.InvokeAsync(call, ct);
    }

    public void Dispose()
    {
        foreach (var belt in _byFamily.Values) (belt as IDisposable)?.Dispose();
    }
}
