using Avalonia.Threading;
using Agent.Common;
using Agent.Common.Platform;

namespace AgentZeroAvalonia.Cli;

/// <summary>
/// The GUI's side of the pipe (M0034). Every request hops to the UI thread — the
/// terminals, the workspace list and the bot live there — through
/// <see cref="Dispatcher.UIThread"/>; the pipe's accept loop never waits on a verb.
/// </summary>
internal sealed class CliServer : IDisposable
{
    private readonly IDisposable _handle;

    private CliServer(IDisposable handle) => _handle = handle;

    public static CliServer Start(CliCommandRouter router)
    {
        var bridge = CliIpcBridge.Create();
        var handle = bridge.StartServer(async (json, ct) =>
        {
            AppLogger.Log($"[IPC] pipe request | {Head(json, 200)}");
            return await Dispatcher.UIThread.InvokeAsync(() => router.HandleAsync(json, ct));
        });
        AppLogger.Log($"[IPC] pipe server listening on '{CliIpcBridge.DefaultPipeName}'");
        return new CliServer(handle);
    }

    public void Dispose() => _handle.Dispose();

    private static string Head(string s, int max) => s.Length <= max ? s : s[..max] + "...";
}
