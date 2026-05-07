namespace AgentZeroWpf.OsControl;

/// <summary>
/// Gate for OS-control verbs that simulate user input (mouse clicks, mouse
/// moves, mouse wheel, key presses). Read-only verbs (window enumeration,
/// screenshot, element-tree, text-capture) are unconditional; input
/// simulation requires explicit opt-in to keep the prompt-injection attack
/// surface tight.
///
/// Two independent enabler signals — either is sufficient:
///   1. CLI: --allow-input flag on the command line. Per-call, no
///      persistence — the operator must repeat the flag every invocation.
///   2. LLM: environment variable <c>AGENTZERO_OS_INPUT_ALLOWED=1</c>
///      (process-scoped). The GUI Settings panel is the intended way to
///      flip this for the running app; CI / unattended scripts can also
///      set it directly.
///
/// We deliberately do NOT reuse the EF settings DB here — input simulation
/// is sensitive enough that a flat env var that doesn't survive a process
/// restart is the right default.
/// </summary>
internal static class OsApprovalGate
{
    public const string EnvVarName = "AGENTZERO_OS_INPUT_ALLOWED";

    public static bool IsInputAllowedByEnv()
    {
        var v = Environment.GetEnvironmentVariable(EnvVarName);
        if (string.IsNullOrWhiteSpace(v)) return false;
        return v.Equals("1", StringComparison.OrdinalIgnoreCase)
            || v.Equals("true", StringComparison.OrdinalIgnoreCase)
            || v.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    public static string DenialMessage =>
        $"OS input simulation is gated. Set environment variable {EnvVarName}=1 (LLM/GUI) "
        + "or pass --allow-input on the CLI verb to enable.";
}
