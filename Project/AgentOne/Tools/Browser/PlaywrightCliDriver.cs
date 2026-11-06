using AgentOne.Tools.Cli;

namespace AgentOne.Tools.Browser;

/// <summary>What the browser belt needs from playwright-cli — a seam so the belt is testable without a browser.</summary>
public interface IBrowserDriver
{
    /// <summary>Null when the driver can run; otherwise why not, in a sentence for the model.</summary>
    string? Missing { get; }

    /// <summary>Folder the CLI runs in: its snapshot files land there, never in the workspace.</summary>
    string WorkingDirectory { get; }

    Task<CliRun> RunAsync(IReadOnlyList<string> args, CancellationToken ct);

    /// <summary>True when a Chrome DevTools endpoint answers at <paramref name="cdpUrl"/>.</summary>
    Task<bool> CdpAliveAsync(string cdpUrl, CancellationToken ct);
}

/// <summary>
/// Runs <c>playwright-cli</c> (Microsoft's @playwright/cli) found on PATH.
///
/// On Windows npm installs it as a <c>.cmd</c> shim, and a batch file cannot
/// take arbitrary text safely through cmd.exe's quoting — a model's search
/// text would be parsed by the shell. So the shim is read for what it points
/// at, <c>node_modules/@playwright/cli/playwright-cli.js</c> beside it, and
/// node runs that with a real argument list. Elsewhere the command is the file.
/// </summary>
public sealed class PlaywrightCliDriver : IBrowserDriver
{
    private static readonly HttpClient Probe = new() { Timeout = TimeSpan.FromSeconds(2) };

    private readonly string _exe;
    private readonly string[] _prefix;
    private readonly TimeSpan _timeout;

    public PlaywrightCliDriver(string workingDirectory, TimeSpan timeout)
    {
        WorkingDirectory = workingDirectory;
        _timeout = timeout;
        (_exe, _prefix, Missing) = Locate();
    }

    public string? Missing { get; }

    public string WorkingDirectory { get; }

    public async Task<CliRun> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (Missing is not null) return new CliRun(-1, "", Missing, false);
        Directory.CreateDirectory(WorkingDirectory);
        return await CliRunner.RunAsync(_exe, _prefix.Concat(args), WorkingDirectory, _timeout, ct);
    }

    public async Task<bool> CdpAliveAsync(string cdpUrl, CancellationToken ct)
    {
        try
        {
            using var response = await Probe.GetAsync(cdpUrl.TrimEnd('/') + "/json/version", ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException)
        {
            ct.ThrowIfCancellationRequested();
            return false;
        }
    }

    internal const string NotInstalled =
        "playwright-cli is not installed on this machine, so the browser tools cannot run. Tell the user it installs with " +
        "`npm install -g @playwright/cli` — do not install it yourself, and do not write a Playwright script instead.";

    private static (string Exe, string[] Prefix, string? Missing) Locate()
    {
        var found = ExecutableLocator.Find("playwright-cli");
        if (found is null) return ("", [], NotInstalled);

        if (!OperatingSystem.IsWindows() || !found.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
            return (found, [], null);

        var dir = Path.GetDirectoryName(found)!;
        var script = Path.Combine(dir, "node_modules", "@playwright", "cli", "playwright-cli.js");
        if (!File.Exists(script)) return ("cmd.exe", ["/d", "/s", "/c", found], null);

        // The shim prefers a node.exe beside it, then the one on PATH — same here.
        var node = File.Exists(Path.Combine(dir, "node.exe")) ? Path.Combine(dir, "node.exe") : ExecutableLocator.Find("node");
        return node is null
            ? ("", [], "node is not on PATH, and playwright-cli needs it. Tell the user to install Node.js (https://nodejs.org).")
            : (node, [script], null);
    }
}
