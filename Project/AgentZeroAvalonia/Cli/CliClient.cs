using System.Text.Json;
using Agent.Common.Platform;

namespace AgentZeroAvalonia.Cli;

/// <summary>
/// The CLI's side of the pipe (M0034/M0039). Sends one request JSON, prints a diagnosis
/// when nobody answers. The request objects are the WPF host's WM_COPYDATA payloads
/// verbatim, so the printers in <see cref="CliMain"/> are ports of the WPF ones.
/// </summary>
internal sealed class CliClient
{
    private readonly ICliIpcBridge _bridge = CliIpcBridge.Create();

    public bool NoWait { get; set; }
    public int TimeoutMs { get; set; } = 5000;

    /// <summary>Returns the reply JSON, or null after printing why there is none.</summary>
    public string? Send(string requestJson)
    {
        var reply = _bridge.SendRequest(requestJson, TimeoutMs);
        if (reply is null)
        {
            Console.Error.WriteLine("Error: AgentZero Lite GUI is not running (no IPC endpoint on pipe " +
                                    $"'{CliIpcBridge.DefaultPipeName}').");
            Console.Error.WriteLine("Start AgentZeroLite (Avalonia) first, then retry.");
            if (OperatingSystem.IsWindows())
                Console.Error.WriteLine("Note: the WPF build answers over WM_COPYDATA, not this pipe — use its own exe for that GUI.");
            return null;
        }
        return reply;
    }

    public static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    public static string Str(JsonElement e, string name, string fallback = "")
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;

    public static bool Ok(JsonElement e) => e.TryGetProperty("ok", out var v) && v.ValueKind == JsonValueKind.True;
}
