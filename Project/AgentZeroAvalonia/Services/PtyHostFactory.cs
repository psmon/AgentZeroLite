namespace AgentZeroAvalonia.Services;

/// <summary>
/// Which PTY backend this OS gets (M0034 placeholder; the hosts themselves arrive in
/// M0035): Windows → the ConPTY host copied from the WPF app, macOS/Linux → Porta.Pty.
/// </summary>
internal static class PtyHostFactory
{
    /// <summary>The backend name, or null with a reason when none applies.</summary>
    public static string? TryCreate(out string? reason)
    {
        reason = null;
        if (OperatingSystem.IsWindows()) return "ConPTY (kernel32, WPF host copy) — wired in M0035";
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux()) return "Porta.Pty (forkpty) — wired in M0035";
        reason = $"unsupported OS: {Environment.OSVersion}";
        return null;
    }
}
