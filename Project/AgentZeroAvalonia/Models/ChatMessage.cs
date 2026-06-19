namespace AgentZeroAvalonia.Models;

public enum ChatRole { User, Assistant, System }

/// <summary>채팅 트랜스크립트 한 줄. 불변 — UI는 컬렉션 추가/교체로만 갱신.</summary>
public sealed class ChatMessage
{
    public ChatRole Role { get; init; }
    public required string Text { get; set; }

    public bool IsUser => Role == ChatRole.User;
    public bool IsAssistant => Role == ChatRole.Assistant;
    public bool IsSystem => Role == ChatRole.System;

    public string RoleLabel => Role switch
    {
        ChatRole.User => "나",
        ChatRole.Assistant => "에이전트",
        _ => "시스템",
    };
}
