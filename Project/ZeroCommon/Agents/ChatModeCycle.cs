namespace Agent.Common.Agents;

/// <summary>AgentBot input modes. Shift+Tab cycles them.</summary>
public enum ChatMode
{
    /// <summary>Text goes to the active terminal followed by Enter.</summary>
    Chat,
    /// <summary>Single control keys go to the active terminal.</summary>
    Key,
    /// <summary>The text is a request for the agent loop.</summary>
    Ai,
}

/// <summary>
/// Pure transition rules for the AgentBot mode cycle (M0033: shared copy of the WPF
/// host's <c>ChatModeCycle</c> so the Avalonia view model and its tests use the same
/// rules; the WPF copy is untouched). AI is selectable when either an LLM is loaded
/// or the saved settings point at a usable backend — the lazy load happens on the
/// first AI-mode send.
/// </summary>
public static class ChatModeCycle
{
    public static ChatMode Next(ChatMode current, bool aiAvailable) => current switch
    {
        ChatMode.Chat => ChatMode.Key,
        ChatMode.Key => aiAvailable ? ChatMode.Ai : ChatMode.Chat,
        ChatMode.Ai => ChatMode.Chat,
        _ => ChatMode.Chat,
    };

    /// <summary>A persisted AI mode only stands while AI is actually reachable.</summary>
    public static ChatMode StabilizeForBadge(ChatMode wanted, bool aiAvailable)
        => wanted == ChatMode.Ai && !aiAvailable ? ChatMode.Chat : wanted;

    public static string Label(ChatMode mode) => mode switch
    {
        ChatMode.Chat => "CHT",
        ChatMode.Key => "KEY",
        ChatMode.Ai => "AI",
        _ => "?",
    };
}
