using System.Diagnostics;
using Agent.Common.Data.Entities;
using Agent.Common.Module;

namespace Agent.Common.Services;

/// <summary>
/// One agent CLI the app ships a built-in definition for, and the two facts needed to
/// put it on the machine: the winget package (Windows) and the npm package (everywhere
/// else). The built-in rows launch these through a shell, so a definition can exist for
/// a tool that was never installed — the tab then opens and prints
/// "claude : The term 'claude' is not recognized", which looks like a broken app rather
/// than a missing program. Naming the tool here is what lets the settings page say
/// which it is and offer to fetch it.
/// </summary>
/// <param name="Name">The built-in definition's name, and what the UI calls the tool.</param>
/// <param name="Command">The executable the shell resolves through PATH.</param>
/// <param name="WingetId">
/// The winget package id, verified against the winget community repo rather than guessed —
/// an id that does not exist fails with the same "no package found" as a typo.
/// </param>
/// <param name="NpmPackage">The npm package, which is the official install path off Windows.</param>
/// <param name="DocsUrl">Where a person goes when both routes fail.</param>
public sealed record AgentCliTool(
    string Name,
    string Command,
    string WingetId,
    string NpmPackage,
    string DocsUrl);

/// <summary>Where a tool was found, and whether the tabs this process launches can see it.</summary>
/// <param name="Installed">A matching executable exists on one of the searched paths.</param>
/// <param name="ResolvedPath">The executable found, for the status line.</param>
/// <param name="OnProcessPath">
/// Whether it is on <em>this process's</em> PATH. A terminal tab is a child of this
/// process and inherits a built environment (<c>TerminalEnvironment</c>), so this — not
/// the presence of a file somewhere — is what decides whether a new tab will find the
/// command. It is false right after an install, because a running process never sees an
/// installer's PATH edit.
/// </param>
public sealed record AgentCliToolState(bool Installed, string? ResolvedPath, bool OnProcessPath)
{
    public static readonly AgentCliToolState Missing = new(false, null, false);
}

/// <summary>
/// What to run to install a tool, as data. The plan is built without running anything so
/// the UI can show the exact command before a person agrees to it, and so the choice
/// between winget and npm is unit-testable on a machine that has neither.
/// </summary>
/// <param name="Exe">The installer to launch.</param>
/// <param name="Arguments">Its arguments.</param>
/// <param name="Problem">
/// Non-null when the plan cannot run here — the installer itself is missing. Naming it
/// ("winget is not on PATH") is the difference between a fixable message and a silent
/// "install failed".
/// </param>
public sealed record AgentCliInstallPlan(string Exe, string Arguments, string? Problem = null)
{
    public string CommandLine => (Exe + " " + Arguments).Trim();
    public bool CanRun => Problem is null;
}

/// <summary>
/// The catalog, the "is it there?" probe and the install plan for the agent CLIs the app
/// seeds definitions for (<see cref="Data.AppDbContext.EnsureDefaultCliDefinitions"/>).
/// Deliberately free of any UI and of any process launch except
/// <see cref="RunInstallAsync"/>, so the rules can be pinned headlessly.
/// </summary>
public static class AgentCliTools
{
    public static readonly AgentCliTool Claude = new(
        Name: "Claude",
        Command: "claude",
        WingetId: "Anthropic.ClaudeCode",
        NpmPackage: "@anthropic-ai/claude-code",
        DocsUrl: "https://docs.claude.com/en/docs/claude-code/setup");

    public static readonly AgentCliTool Codex = new(
        Name: "Codex",
        Command: "codex",
        WingetId: "OpenAI.Codex",
        NpmPackage: "@openai/codex",
        DocsUrl: "https://github.com/openai/codex");

    public static IReadOnlyList<AgentCliTool> All { get; } = new[] { Claude, Codex };

    /// <summary>
    /// The tool a definition drives, or null for a plain shell. Matched on the same
    /// token rule the reduced-motion flag uses (<see cref="CliToolMention"/>), so a row
    /// offered an install button is exactly a row that would get the flag.
    /// </summary>
    public static AgentCliTool? Match(string? exePath, string? arguments) =>
        All.FirstOrDefault(t => CliToolMention.Mentions(exePath, arguments, t.Command));

    public static AgentCliTool? Match(CliDefinition? definition) =>
        definition is null ? null : Match(definition.ExePath, definition.Arguments);

    // ── probe ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Find <paramref name="command"/> on the given directories. Pure — the tests hand
    /// it a fake PATH. An explicit scan is used instead of launching
    /// <c>where</c>/<c>which</c> because the answer is needed while a settings page is
    /// being drawn, and because a child process started to answer it would inherit the
    /// same PATH we are trying to inspect anyway.
    /// </summary>
    public static string? Locate(string command, IEnumerable<string> pathEntries, IEnumerable<string>? extensions = null)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var exts = (extensions ?? DefaultExtensions()).ToList();

        foreach (var dir in pathEntries)
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            foreach (var ext in exts)
            {
                string candidate;
                try { candidate = Path.Combine(dir.Trim().Trim('"'), command + ext); }
                catch { continue; }   // a PATH entry with invalid characters is not an error here
                try { if (File.Exists(candidate)) return candidate; }
                catch { }
            }
        }
        return null;
    }

    /// <summary>
    /// Probe a tool the way a new terminal tab would, plus the places an installer that
    /// just finished is likely to have written to.
    /// </summary>
    public static AgentCliToolState Probe(AgentCliTool tool, bool? isWindows = null)
    {
        var windows = isWindows ?? OperatingSystem.IsWindows();
        var processPath = SplitPath(Environment.GetEnvironmentVariable("PATH")).ToList();

        var onProcessPath = Locate(tool.Command, processPath, DefaultExtensions(windows));
        if (onProcessPath is not null) return new AgentCliToolState(true, onProcessPath, true);

        // Not on our PATH — but "we just installed it" and "it isn't there" are very
        // different answers and used to look identical, because a running process keeps
        // the PATH it started with. So the wider set is searched too, and the caller is
        // told the tabs cannot see it yet.
        var elsewhere = Locate(tool.Command, FreshPathEntries(windows), DefaultExtensions(windows));
        return elsewhere is null ? AgentCliToolState.Missing : new AgentCliToolState(true, elsewhere, false);
    }

    /// <summary>
    /// PATH as the machine now has it, plus the bin folders winget shims and npm globals
    /// land in. Read outside the process environment so an install done a moment ago is
    /// visible without restarting the app.
    /// </summary>
    public static IEnumerable<string> FreshPathEntries(bool? isWindows = null)
    {
        var windows = isWindows ?? OperatingSystem.IsWindows();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        foreach (var entry in Candidates())
        {
            if (string.IsNullOrWhiteSpace(entry)) continue;
            if (seen.Add(entry)) yield return entry;
        }

        IEnumerable<string> Candidates()
        {
            foreach (var p in SplitPath(Environment.GetEnvironmentVariable("PATH"))) yield return p;

            if (windows)
            {
                // The User/Machine targets are the registry PATH — what a shell started
                // now would get. Windows-only in the BCL, hence the guard and the catch.
                foreach (var target in new[] { EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
                {
                    string? value = null;
                    try { value = Environment.GetEnvironmentVariable("PATH", target); } catch { }
                    foreach (var p in SplitPath(value)) yield return p;
                }

                yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links");
                yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm");
                yield return Path.Combine(home, ".local", "bin");
            }
            else
            {
                yield return "/opt/homebrew/bin";
                yield return "/usr/local/bin";
                yield return Path.Combine(home, ".local", "bin");
                yield return Path.Combine(home, ".npm-global", "bin");
            }
        }
    }

    /// <summary>
    /// The extensions to try. On Windows a command is <c>claude.cmd</c> as often as
    /// <c>claude.exe</c> — npm writes a <c>.cmd</c> shim and winget a <c>.exe</c> — so
    /// PATHEXT is honoured rather than assuming one of them. <c>.ps1</c> is added on top
    /// of PATHEXT, which does not list it: the built-in definitions run the agent
    /// <em>inside PowerShell</em>, and PowerShell resolves a script shim that cmd would
    /// not, so a tool installed with only a <c>.ps1</c> would work in the tab while
    /// reading as missing here.
    /// </summary>
    public static IEnumerable<string> DefaultExtensions(bool? isWindows = null)
    {
        if (!(isWindows ?? OperatingSystem.IsWindows())) return new[] { "" };

        var pathext = Environment.GetEnvironmentVariable("PATHEXT");
        var exts = string.IsNullOrWhiteSpace(pathext)
            ? new List<string> { ".com", ".exe", ".bat", ".cmd" }
            : pathext.Split(';', StringSplitOptions.RemoveEmptyEntries)
                     .Select(e => e.Trim().StartsWith('.') ? e.Trim() : "." + e.Trim())
                     .ToList();
        if (!exts.Any(e => string.Equals(e, ".ps1", StringComparison.OrdinalIgnoreCase))) exts.Add(".ps1");
        exts.Add("");   // an extensionless file on PATH still runs from a shell
        return exts;
    }

    private static IEnumerable<string> SplitPath(string? path) =>
        string.IsNullOrWhiteSpace(path)
            ? Array.Empty<string>()
            : path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

    // ── install ─────────────────────────────────────────────────────────────

    /// <summary>
    /// How to install this tool here: winget on Windows, npm elsewhere (the route both
    /// projects publish for macOS and Linux). The prerequisite is probed so a machine
    /// without the installer gets told which one is missing instead of an exit code.
    /// </summary>
    public static AgentCliInstallPlan PlanInstall(AgentCliTool tool, bool? isWindows = null)
    {
        var windows = isWindows ?? OperatingSystem.IsWindows();
        var extensions = DefaultExtensions(windows);
        var entries = FreshPathEntries(windows).ToList();

        if (windows)
        {
            var problem = Locate("winget", entries, extensions) is null
                ? "winget is not available. Install 'App Installer' from the Microsoft Store, or install "
                  + tool.Name + " yourself: " + tool.DocsUrl
                : null;

            // --disable-interactivity matters because the output is captured, not shown in
            // a console: a winget prompt would wait for a keystroke that can never
            // arrive, and the install would hang instead of failing. The two --accept
            // flags remove the agreements that would otherwise be that prompt.
            return new AgentCliInstallPlan(
                "winget",
                $"install --id {tool.WingetId} --exact --source winget "
                + "--accept-source-agreements --accept-package-agreements --disable-interactivity",
                problem);
        }

        var npmMissing = Locate("npm", entries, extensions) is null
            ? "npm is not available. Install Node.js (nodejs.org) first, or install "
              + tool.Name + " yourself: " + tool.DocsUrl
            : null;

        return new AgentCliInstallPlan("npm", $"install -g {tool.NpmPackage}", npmMissing);
    }

    /// <summary>
    /// Run an install plan, reporting each output line as it arrives. Both streams are
    /// captured and merged because installers report progress on either and a person
    /// watching a long download needs to see something; stdin is redirected and closed so
    /// anything that still decides to ask a question fails fast rather than hanging.
    /// </summary>
    /// <returns>The installer's exit code, or -1 when it could not be started.</returns>
    public static async Task<int> RunInstallAsync(
        AgentCliInstallPlan plan,
        Action<string>? onOutput = null,
        CancellationToken cancellationToken = default)
    {
        if (!plan.CanRun)
        {
            onOutput?.Invoke(plan.Problem!);
            return -1;
        }

        var psi = new ProcessStartInfo(plan.Exe, plan.Arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is { } line) onOutput?.Invoke(line); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is { } line) onOutput?.Invoke(line); };

        try
        {
            if (!process.Start())
            {
                onOutput?.Invoke($"Could not start {plan.Exe}.");
                return -1;
            }
        }
        catch (Exception ex)
        {
            onOutput?.Invoke($"Could not start {plan.Exe}: {ex.Message}");
            return -1;
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try { process.StandardInput.Close(); } catch { }

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            onOutput?.Invoke("Cancelled.");
            return -1;
        }

        return process.ExitCode;
    }
}
