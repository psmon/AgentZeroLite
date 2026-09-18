namespace Agent.Common.Agents;

/// <summary>
/// Which terminal the bot is pointed at (M0041). The header shows it continuously; the
/// transcript announces it only when it actually changes, so switching back and forth
/// between two tabs does not fill the chat with banners. Ported from the WPF host's
/// <c>ShowWelcomeMessage</c> / <c>UpdateSessionHeader</c> pair.
/// </summary>
/// <param name="Group">Workspace (CLI group) display name.</param>
/// <param name="Tab">Terminal tab title.</param>
public sealed record BotSession(string Group, string Tab)
{
    /// <summary>Identity for "have we announced this one already".</summary>
    public string Key => $"{Group}/{Tab}";

    /// <summary>The one-line transcript notice.</summary>
    public string Notice => $"[Session] {Group} / {Tab}";
}

/// <summary>Tracks which session was last announced so the notice fires once per tab.</summary>
public sealed class BotSessionAnnouncer
{
    private string? _lastKey;

    /// <summary>
    /// Returns the notice to add to the transcript, or null when this session was already
    /// announced. A null <paramref name="session"/> clears the memory, so returning to a tab
    /// after "no terminal" announces it again — which is what the user expects.
    /// </summary>
    public string? Announce(BotSession? session)
    {
        if (session is null)
        {
            _lastKey = null;
            return null;
        }

        if (_lastKey == session.Key) return null;
        _lastKey = session.Key;
        return session.Notice;
    }
}
