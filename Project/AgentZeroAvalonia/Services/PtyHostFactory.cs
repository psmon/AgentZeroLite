using Agent.Common.Services;

namespace AgentZeroAvalonia.Services;

/// <summary>
/// Which PTY backend this OS gets (M0035): Windows → <see cref="ConPtyHost"/> (the ConPTY
/// copy from the WPF app), macOS / Linux → <see cref="PortaPtyHost"/>. The choice is made
/// once, here, so the terminal control and the self-test spawn through the same door.
/// </summary>
internal static class PtyHostFactory
{
    public static string BackendName =>
        OperatingSystem.IsWindows() ? "ConPTY (kernel32, WPF host copy)"
        : OperatingSystem.IsMacOS() || OperatingSystem.IsLinux() ? "Porta.Pty (forkpty)"
        : "none";

    public static bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();

    /// <summary>Spawn <paramref name="spec"/> at <paramref name="cols"/>×<paramref name="rows"/>. Throws on failure — the caller logs and shows the reason.</summary>
    public static IPtyHost Start(TerminalLaunchSpec spec, int cols, int rows)
    {
        var cwd = !string.IsNullOrEmpty(spec.Cwd) && Directory.Exists(spec.Cwd) ? spec.Cwd : null;
        if (OperatingSystem.IsWindows())
        {
            var host = new ConPtyHost();
            host.Start(spec.CommandLine, cwd, spec.Env, cols, rows);
            return host;
        }
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
        {
            var host = new PortaPtyHost();
            host.Start(cwd is null ? spec with { Cwd = Environment.CurrentDirectory } : spec, cols, rows);
            return host;
        }
        throw new PlatformNotSupportedException($"No PTY backend for {Environment.OSVersion}");
    }

    /// <summary>The spec a self-test uses: a shell that echoes a marker and exits.</summary>
    public static TerminalLaunchSpec EchoSpec(string marker)
    {
        if (OperatingSystem.IsWindows())
        {
            var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            return new TerminalLaunchSpec(cmd, new[] { "/c", "echo", marker }, Environment.CurrentDirectory,
                TerminalEnvironment.Build(), "selftest");
        }
        return new TerminalLaunchSpec("/bin/sh", new[] { "-c", "echo " + marker }, Environment.CurrentDirectory,
            TerminalEnvironment.BuildPosix(), "selftest");
    }
}
