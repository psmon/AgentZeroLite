namespace ZeroWearable.Chat;

/// <summary>
/// How to run one "chat CLI" as a child process. The reference host read these from
/// <c>appsettings.json</c>; AgentZero has no such file, so the three that were actually
/// used are built in (<see cref="CliProviderCatalog"/>) and the wearable settings pick
/// one by name.
/// </summary>
public sealed class ProviderConfig
{
    public string? Description { get; init; }
    public string Command { get; init; } = "";
    public List<string> Args { get; init; } = [];
    public List<string> SessionArgs { get; init; } = [];

    /// <summary>"arg" = prompt as the last argument; "stdin" = written to standard input.</summary>
    public string PromptVia { get; init; } = "arg";

    /// <summary>"text" = stdout is the answer; "json" = read <see cref="ResponseField"/> from it.</summary>
    public string Output { get; init; } = "text";

    public string ResponseField { get; init; } = "response";
    public int TimeoutSec { get; init; } = 120;
    public string? WorkingDir { get; init; }

    /// <summary>
    /// Whether to prepend <c>WearableSettings.ReplyStyle</c> to the prompt. False for the
    /// loopback provider: echoing the style instructions back made the watch read the
    /// system prompt aloud, which sounds exactly like being told to go configure something.
    /// </summary>
    public bool UseReplyStyle { get; init; } = true;
}

/// <summary>
/// The CLI brains the watch can be pointed at. These are whole agents in their own right —
/// the watch is just another front-end for them — which is why this path survives alongside
/// AgentZero's own agent loop.
/// </summary>
public static class CliProviderCatalog
{
    public static readonly IReadOnlyDictionary<string, ProviderConfig> All =
        new Dictionary<string, ProviderConfig>(StringComparer.OrdinalIgnoreCase)
        {
            // Offline loopback: proves the whole BLE → actor → speech path with no model
            // installed and no network. The default, so a fresh install can be tested.
            ["echo"] = new()
            {
                Description = "offline loopback (prompt in, echoed back) — no LLM needed",
                Command = "powershell",
                Args =
                [
                    "-NoProfile", "-NonInteractive", "-Command",
                    "[Console]::InputEncoding=[Text.Encoding]::UTF8; " +
                    "[Console]::OutputEncoding=[Text.Encoding]::UTF8; " +
                    "'echo: ' + [Console]::In.ReadToEnd()"
                ],
                PromptVia = "stdin",
                Output = "text",
                TimeoutSec = 20,
                UseReplyStyle = false,
            },

            ["claude"] = new()
            {
                Description = "Claude Code CLI, prompt via stdin (claude -p --output-format json)",
                Command = "cmd",
                Args = ["/c", "claude", "-p", "--output-format", "json"],
                PromptVia = "stdin",
                Output = "json",
                ResponseField = "result",
                TimeoutSec = 180,
            },

            ["netclaw"] = new()
            {
                Description = "netclaw chat -p (headless, JSON, named session per device)",
                Command = "netclaw",
                Args = ["chat", "-p", "--json", "{session-args}"],
                SessionArgs = ["--resume", "{session}"],
                PromptVia = "arg",
                Output = "json",
                ResponseField = "response",
                TimeoutSec = 120,
            },
        };

    /// <summary>Unknown names fall back to the offline loopback rather than failing to start.</summary>
    public static ProviderConfig Resolve(string? name)
        => name is not null && All.TryGetValue(name, out var config) ? config : All["echo"];

    public static string Normalize(string? name)
        => name is not null && All.ContainsKey(name) ? name : "echo";
}
