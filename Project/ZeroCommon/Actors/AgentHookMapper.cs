namespace Agent.Common.Actors;

/// <summary>
/// Pure mapping from an external agent CLI's hook event (Claude Code hook
/// names) to the AgentZero <see cref="AgentLoopPhase"/> FSM + a short display
/// text (mission W1, orca-adoption). Lives in ZeroCommon so it is headlessly
/// testable and WPF-free.
///
/// This replaces terminal-output scraping (<c>ApprovalParser</c> /
/// <c>AgentEventStream</c>) as the state source: a hook script installed into
/// the agent CLI reports the real event via <c>-cli agent-hook</c>, and this
/// mapper turns it into the same <see cref="AgentLoopProgress"/> the UI already
/// renders. Scraping stays only as a fallback.
/// </summary>
public static class AgentHookMapper
{
    /// <summary>
    /// Resolves an <see cref="AgentHookEvent"/> to a phase + human text. When
    /// the hook script supplied an explicit <see cref="AgentHookEvent.StateOverride"/>
    /// that parses to a known phase, it wins; otherwise the phase is derived
    /// from the Claude Code hook name.
    /// </summary>
    public static (AgentLoopPhase Phase, string Text) Resolve(AgentHookEvent evt)
    {
        if (!string.IsNullOrWhiteSpace(evt.StateOverride)
            && TryParsePhase(evt.StateOverride!, out var overridePhase))
        {
            return (overridePhase, ComposeText(overridePhase, evt.HookEvent, evt.Detail));
        }

        var phase = MapEventToPhase(evt.HookEvent);
        return (phase, ComposeText(phase, evt.HookEvent, evt.Detail));
    }

    /// <summary>Maps a Claude Code hook event name to an FSM phase.</summary>
    public static AgentLoopPhase MapEventToPhase(string hookEvent)
        => (hookEvent ?? "").Trim().ToLowerInvariant() switch
        {
            "userpromptsubmit" => AgentLoopPhase.Thinking,
            "sessionstart"     => AgentLoopPhase.Idle,
            "pretooluse"       => AgentLoopPhase.Acting,
            "posttooluse"      => AgentLoopPhase.Acting,
            "subagentstop"     => AgentLoopPhase.Acting,
            "precompact"       => AgentLoopPhase.Generating,
            "notification"     => AgentLoopPhase.Thinking,
            "stop"             => AgentLoopPhase.Done,
            "sessionend"       => AgentLoopPhase.Done,
            _                  => AgentLoopPhase.Thinking,
        };

    /// <summary>Parses a phase override string ("acting", "done", ...) case-insensitively.</summary>
    public static bool TryParsePhase(string state, out AgentLoopPhase phase)
        => System.Enum.TryParse(state?.Trim(), ignoreCase: true, out phase);

    private static string ComposeText(AgentLoopPhase phase, string hookEvent, string detail)
    {
        var baseText = phase switch
        {
            AgentLoopPhase.Idle       => "세션 준비",
            AgentLoopPhase.Thinking   => "생각 중",
            AgentLoopPhase.Generating => "생성 중",
            AgentLoopPhase.Acting     => "도구 실행",
            AgentLoopPhase.Done       => "완료",
            AgentLoopPhase.Error      => "오류",
            _                         => hookEvent,
        };
        return string.IsNullOrWhiteSpace(detail) ? baseText : $"{baseText} — {detail}";
    }
}
