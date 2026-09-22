using System.Diagnostics;
using AgentOne.Llm;
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
