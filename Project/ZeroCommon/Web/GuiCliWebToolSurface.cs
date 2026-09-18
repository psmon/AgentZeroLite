using System.Diagnostics;
using System.Text;
using Agent.Common.Llm.Tools;

namespace Agent.Common.Web;

/// <summary>
/// The GUI's Browser page, reached from another process the only way the GUI is reachable:
/// <c>AgentZeroLite.exe -cli web …</c> over WM_COPYDATA (M0032). No new IPC — the same
/// channel the terminal-side <c>bot-chat</c> reverse path uses. The exe writes the page's
/// JSON envelope to stdout; anything else (GUI not running, exe not found, no JSON) is
/// <see cref="WebSurfaceUnavailableException"/>, which is the web actor's cue to fall back.
/// </summary>
public sealed class GuiCliWebToolSurface : IWebToolSurface
{
    /// <summary>Passed as <c>--timeout</c>; must be longer than a page load and shorter than the actor's budget.</summary>
    public const int CliTimeoutMs = 45_000;

    private readonly Func<string?> _resolveExe;
    private readonly Action<string, string>? _log;

    public GuiCliWebToolSurface(Func<string?> resolveExe, Action<string, string>? log = null)
    {
        _resolveExe = resolveExe;
        _log = log;
    }

    /// <summary>
    /// Where <c>AgentZeroLite.exe</c> is relative to the wearable host: the explicit setting,
    /// else the shipped layout (<c>wearable\</c> under the app folder), else the dev layout
    /// (the sibling project's build output, Debug then Release).
    /// </summary>
    /// <summary>Setting values that mean "never use the GUI; browse headlessly even when it runs".</summary>
    public static bool IsDisabled(string? explicitPath)
        => explicitPath is not null &&
           (explicitPath.Trim().Equals("off", StringComparison.OrdinalIgnoreCase) ||
            explicitPath.Trim().Equals("none", StringComparison.OrdinalIgnoreCase));

    public static string? ResolveGuiExe(string? explicitPath, string hostBaseDir)
    {
        if (IsDisabled(explicitPath)) return null;
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath)) return explicitPath;

        var candidates = new[]
        {
            Path.Combine(hostBaseDir, "..", "AgentZeroLite.exe"),
            Path.Combine(hostBaseDir, @"..\..\..\..\AgentZeroWpf\bin\Debug\net10.0-windows\AgentZeroLite.exe"),
            Path.Combine(hostBaseDir, @"..\..\..\..\AgentZeroWpf\bin\Release\net10.0-windows\AgentZeroLite.exe"),
        };
        foreach (var candidate in candidates)
        {
            try
            {
                var full = Path.GetFullPath(candidate);
                if (File.Exists(full)) return full;
            }
            catch { }
        }
        return null;
    }

    public Task<string> SearchAsync(string query, int maxResults, CancellationToken ct)
        => RunAsync(["web", "search", query, "--max", maxResults.ToString()], ct);

    public Task<string> OpenAsync(string url, int tab, CancellationToken ct)
        => RunAsync(["web", "open", url, "--tab", tab.ToString()], ct);

    public Task<string> ReadAsync(int tab, string? mode, string? find, int maxChars, CancellationToken ct)
    {
        var args = new List<string> { "web", "read", "--tab", tab.ToString(), "--mode", mode ?? "summary", "--max-chars", maxChars.ToString() };
        if (!string.IsNullOrWhiteSpace(find))
        {
            args.Add("--find");
            args.Add(find);
        }
        return RunAsync(args, ct);
    }

    public Task<string> ListTabsAsync(CancellationToken ct)
        => RunAsync(["web", "tabs"], ct);

    private async Task<string> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var exe = _resolveExe() ?? throw new WebSurfaceUnavailableException("AgentZeroLite.exe not found");

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-cli");
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("--timeout");
        psi.ArgumentList.Add(CliTimeoutMs.ToString());

        using var process = Process.Start(psi)
            ?? throw new WebSurfaceUnavailableException($"could not start {exe}");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        var stdout = (await stdoutTask).Trim();
        var stderr = (await stderrTask).Trim();

        // The CLI prints the GUI's envelope as-is, so a refusal ({"ok":false,…}) is an
        // answer; only a non-JSON reply means the GUI itself was not there to answer.
        if (stdout.StartsWith('{')) return stdout;

        var reason = stderr.Length > 0 ? stderr : stdout;
        _log?.Invoke("debug", $"-cli {string.Join(' ', args.Take(2))} exit {process.ExitCode}: {Head(reason, 160)}");
        throw new WebSurfaceUnavailableException(reason.Length > 0 ? Head(reason, 200) : $"exit code {process.ExitCode}");
    }

    private static string Head(string s, int max) => s.Length <= max ? s : s[..max] + "...";
}
