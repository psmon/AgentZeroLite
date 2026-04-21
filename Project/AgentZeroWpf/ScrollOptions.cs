namespace AgentZeroWpf;

/// <summary>스크롤 캡처 옵션.</summary>
internal sealed record ScrollOptions(
    int DelayMs = 200,
    int MaxAttempts = 500,
    int DeltaMultiplier = 3,
    DateTime? FilterStartDate = null,
    DateTime? FilterEndDate = null);
