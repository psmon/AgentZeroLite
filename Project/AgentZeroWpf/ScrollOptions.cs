namespace AgentZeroWpf;

/// <summary>
/// 스크롤 캡처 진행 방향.
/// <list type="bullet">
/// <item><see cref="Down"/> — 기본. 최상단에서 시작해 아래로 스크롤하며 수집.
///   (최신이 상단, 아래로 갈수록 과거인 문서/피드형 UI)</item>
/// <item><see cref="Up"/> — 역방향. 최하단에서 시작해 위로 스크롤하며 수집.
///   (하단이 최신, 위로 갈수록 과거인 채팅형 UI)</item>
/// </list>
/// </summary>
internal enum ScrollDirection
{
    Down = 0,
    Up = 1,
}

/// <summary>스크롤 캡처 옵션.</summary>
internal sealed record ScrollOptions(
    int DelayMs = 200,
    int MaxAttempts = 500,
    int DeltaMultiplier = 3,
    DateTime? FilterStartDate = null,
    DateTime? FilterEndDate = null,
    ScrollDirection Direction = ScrollDirection.Down)
{
    /// <summary>위로 수집(역방향)하는 모드인지 여부.</summary>
    public bool IsReverse => Direction == ScrollDirection.Up;
}
