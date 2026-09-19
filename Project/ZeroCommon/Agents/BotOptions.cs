namespace Agent.Common.Agents;

/// <summary>
/// The AgentBot's collapsible options bar, as state rather than checkboxes (M0041).
/// Ported from the WPF host's <c>AgentBotWindow</c> options panel so both hosts can
/// share the clamping rules and so they are testable without a UI.
/// </summary>
/// <remarks>
/// The WPF panel also carries a "Thinking" (ReAct vs legacy FnCall) checkbox. It is
/// declared in XAML and read by nothing, so there is no behaviour to share and it is
/// deliberately absent here.
/// </remarks>
public sealed class BotOptions
{
    /// <summary>Largest auto-approve delay the WPF input accepts.</summary>
    public const int MaxAutoApproveDelaySeconds = 30;

    /// <summary>Approval prompts are answered without asking the user.</summary>
    public bool AutoApprove { get; set; }

    /// <summary>Seconds to wait before answering. 0 means answer at once.</summary>
    public int AutoApproveDelaySeconds { get; set; }

    /// <summary>System notices (auto-approve lines, session banners) stay out of the transcript.</summary>
    public bool HideSystemMessages { get; set; } = true;

    /// <summary>
    /// Parses what the user typed into the delay box. Returns null when the text is not a
    /// number or falls outside 0..<see cref="MaxAutoApproveDelaySeconds"/> — the WPF handler
    /// simply keeps the previous value in that case, so callers should do the same.
    /// </summary>
    public static int? TryParseDelay(string? text)
    {
        if (!int.TryParse(text, out var value)) return null;
        if (value < 0 || value > MaxAutoApproveDelaySeconds) return null;
        return value;
    }
}
