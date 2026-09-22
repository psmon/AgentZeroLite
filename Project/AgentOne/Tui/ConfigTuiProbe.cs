using System.Diagnostics;
using AgentOne.Llm;
using AgentOne.Llm.Decision;
using AgentOne.Services;

namespace AgentOne.Tui;

/// <summary>
/// The `t` key's work: send the smallest possible request through the config
/// exactly as it stands on screen, and turn whatever comes back — an answer, an
/// HTTP status, a refused connection — into one line.
///
/// This is the reason the TUI exists for the operator: changing a setting and
/// finding out whether it works should not need a separate command.
/// </summary>
public static class ConfigTuiProbe
{
    /// <summary>
    /// The `l` key's work, and what `Enter` on the model row does: ask the
    /// endpoint what it can run. One request exercises the base URL, the network
    /// path and the API key at once, so a short list is a green light and an
    /// empty one names the thing that is wrong.
    /// </summary>
    public static async Task<ModelCatalogResult> ListModelsAsync(AgentConfig config, CancellationToken ct)
    {
        IChatProvider provider;
        try
        {
            provider = ChatProviderFactory.Create(config);
        }
        catch (ChatProviderException ex)
        {
            return ModelCatalogResult.Failure(ex.Message);
        }

        using var disposable = provider as IDisposable;

        if (provider is not IModelCatalog catalog)
            return ModelCatalogResult.Failure($"the {provider.Name} provider cannot list models");

        try
        {
            return await catalog.ListModelsAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return ModelCatalogResult.Failure("listing cancelled");
        }
    }

    /// <summary>
    /// The `h` key's work on the Smart step: one real question to TypeSafe.
    /// A check that only looked for a key in a file would pass while the key was
    /// wrong, which is the failure this whole screen exists to prevent.
    /// </summary>
    public static async Task<string> CheckSmartAsync(AgentConfig config, CancellationToken ct)
    {
        using var client = new JevClient(config);

        try
        {
            var check = await client.CheckAsync(ct);
            return check.Ok ? check.Message : "✗ " + check.Message;
        }
        catch (OperationCanceledException)
        {
            return "✗ check cancelled";
        }
    }

    public static async Task<string> DefaultAsync(AgentConfig config, CancellationToken ct)
    {
        IChatProvider provider;
        try
        {
            provider = ChatProviderFactory.Create(config);
        }
        catch (ChatProviderException ex)
        {
            return "✗ " + ex.Message;
        }

        using var disposable = provider as IDisposable;
        var sw = Stopwatch.StartNew();

        try
        {
            var reply = await provider.CompleteAsync(
                [ChatMessage.System("Reply with the single word: ok"), ChatMessage.User("ping")],
                ct);

            sw.Stop();
            var head = reply.ReplaceLineEndings(" ").Trim();
            if (head.Length > 60) head = head[..60] + "…";

            return $"✓ {provider.Name} · {config.Model} · {sw.ElapsedMilliseconds} ms · replied: {head}";
        }
        catch (OperationCanceledException)
        {
            return "✗ test cancelled";
        }
        catch (ChatProviderException ex)
        {
            return "✗ " + ex.Message;
        }
    }
}
