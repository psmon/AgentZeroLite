using System.Text.Json;

namespace Agent.Common.Agents;

/// <summary>
/// Spots, on a peer terminal's screen, that the agent there could not run a command at all —
/// so it cannot call <c>bot-chat</c> either and its replies exist only on that screen.
///
/// <para>Measured (2026-11-24, Codex TUI 0.166 with app-server daemon 0.172, Windows
/// <c>sandbox = "elevated"</c>): every command — the handshake's <c>-cli help</c> included —
/// ended in "Failed to create unified exec process: helper_sandbox_lock_failed". Codex's daemon
/// refreshes its sandbox setup before a command, and the helper could not change the ACL of
/// <c>~/.codex/.sandbox-bin</c> (created by an elevated setup months earlier, owned by
/// Administrators; error 5). Nothing AgentZero does causes or can repair that, but the bot
/// spent a minute per message waiting for a callback that could never come. With this note in
/// the <c>read_terminal</c> result it reads that peer from the screen at once and tells the
/// user why.</para>
/// </summary>
public static class PeerCliFault
{
    private static readonly string[] ScreenMarkers =
    [
        "helper_sandbox_",
        "Failed to create unified exec process",
    ];

    /// <summary>A note for the model when <paramref name="screen"/> shows the fault, else null.</summary>
    public static string? NoteFor(string? screen, string? codexHome = null, DateTime? nowUtc = null)
    {
        if (string.IsNullOrEmpty(screen)) return null;
        if (!ScreenMarkers.Any(m => screen.Contains(m, StringComparison.OrdinalIgnoreCase))) return null;

        var detail = RecentCodexSetupError(codexHome, nowUtc) is { } e ? $" ({e.Code}: {e.Message})" : "";
        return "This peer could not run shell commands: its own sandbox setup failed" + detail + ". "
             + "It CANNOT call bot-chat, so no peer signal will come from it — its replies are only on this screen: "
             + "use wait(10) + read_terminal for this peer. This is a fault in the peer's installation (Codex: the "
             + "Windows sandbox setup needs to be run again with administrator rights, e.g. after updating Codex), "
             + "not in the message you sent. Mention it once in your done message.";
    }

    public readonly record struct SetupError(string Code, string Message, DateTime AtUtc);

    /// <summary>
    /// Codex's last recorded sandbox setup error (<c>.sandbox/setup_error.json</c>) when it was
    /// written within the last <paramref name="withinMinutes"/> minutes; null otherwise.
    /// </summary>
    public static SetupError? RecentCodexSetupError(string? codexHome = null, DateTime? nowUtc = null, int withinMinutes = 30)
    {
        try
        {
            var home = codexHome
                ?? Environment.GetEnvironmentVariable("CODEX_HOME")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            var file = Path.Combine(home, ".sandbox", "setup_error.json");
            if (!File.Exists(file)) return null;
            var at = File.GetLastWriteTimeUtc(file);
            if ((nowUtc ?? DateTime.UtcNow) - at > TimeSpan.FromMinutes(withinMinutes)) return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;
            var code = root.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "";
            var message = root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            return code.Length == 0 && message.Length == 0 ? null : new SetupError(code, message, at);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
