namespace AgentZeroWpf.UI.APP;

/// <summary>
/// Pure transition rules for the AgentBot input mode cycle (Shift+Tab).
/// Extracted out of <see cref="AgentBotWindow"/> so the logic can be unit
/// tested without spinning up WPF — the cycle has had repeat regressions
/// (Ai-mode entry being silently swallowed by stale "must be loaded" gates),
/// so this lives behind a focused test boundary now.
/// </summary>
public static class ChatModeCycle
{
    /// <summary>
    /// Compute the next mode given the current mode and whether AI mode is
    /// currently selectable. AI is selectable if EITHER the LLM is already
    /// loaded OR the saved settings point to a model file that exists on
    /// disk (lazy-load on first AIMODE send will then pick it up).
    /// </summary>
    public static ChatMode Next(ChatMode current, bool aiAvailable)
        => current switch
        {
            ChatMode.Chat => ChatMode.Key,
            ChatMode.Key => aiAvailable ? ChatMode.Ai : ChatMode.Chat,
            ChatMode.Ai => ChatMode.Chat,
            _ => ChatMode.Chat,
        };

    /// <summary>
    /// Decide whether a persisted-or-current AI mode should be allowed to
    /// stand at badge-render time. With lazy load active, the badge only
    /// downgrades when AI mode is genuinely unreachable (no loaded LLM AND
    /// no settings/model on disk). Returns the effective mode.
    /// </summary>
    public static ChatMode StabilizeForBadge(ChatMode wanted, bool aiAvailable)
        => wanted == ChatMode.Ai && !aiAvailable ? ChatMode.Chat : wanted;
}
