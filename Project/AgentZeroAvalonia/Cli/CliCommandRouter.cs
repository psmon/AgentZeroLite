using System.Text.Json;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Agent.Common;
using Agent.Common.Module;
using Agent.Common.Platform;

namespace AgentZeroAvalonia.Cli;

/// <summary>
/// Dispatches one CLI request to the running GUI (M0034 skeleton: <c>status</c> and
/// <c>close-win</c>; the terminal, bot and web verbs are wired in M0039 as the pieces
/// they need appear). Runs on the UI thread; replies are the WPF host's JSON shapes.
/// </summary>
internal sealed class CliCommandRouter
{
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;

    /// <summary>Set by the main window once terminals exist (M0035+).</summary>
    public Func<int>? GroupCount { get; set; }
    public Func<int>? TerminalCount { get; set; }

    public CliCommandRouter(IClassicDesktopStyleApplicationLifetime desktop) => _desktop = desktop;

    public Task<string> HandleAsync(string json, CancellationToken ct)
    {
        string command;
        try
        {
            using var doc = JsonDocument.Parse(json);
            command = doc.RootElement.TryGetProperty("command", out var c) ? c.GetString() ?? "" : "";
        }
        catch (JsonException ex)
        {
            return Task.FromResult(CliIpcProtocol.ErrorJson("bad request json: " + ex.Message));
        }

        switch (command)
        {
            case "status":
                return Task.FromResult(StatusJson());

            case "close-win":
                Dispatcher.UIThread.Post(() => _desktop.Shutdown(), DispatcherPriority.Background);
                return Task.FromResult("{\"ok\":true}");

            default:
                return Task.FromResult(CliIpcProtocol.ErrorJson($"unknown command '{command}' (not ported to the Avalonia host yet)"));
        }
    }

    private string StatusJson()
    {
        var groups = GroupCount?.Invoke() ?? 0;
        var terminals = TerminalCount?.Invoke() ?? 0;
        return "{\"ok\":true,\"running\":true,\"host\":\"avalonia\"" +
               $",\"version\":\"{CliIpcProtocol.Escape(AppVersionProvider.GetDisplayVersion())}\"" +
               $",\"os\":\"{CliIpcProtocol.Escape(Environment.OSVersion.ToString())}\"" +
               $",\"groups\":{groups},\"terminals\":{terminals}}}";
    }
}
