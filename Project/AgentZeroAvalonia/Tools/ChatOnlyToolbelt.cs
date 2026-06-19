using System.Threading;
using System.Threading.Tasks;
using Agent.Common.Llm.Tools;

namespace AgentZeroAvalonia.Tools;

/// <summary>
/// 코어 단계용 최소 toolbelt — 터미널/OS 도구 없이 순수 채팅만 지원한다.
///
/// WPF의 <c>WorkspaceTerminalToolHost</c>는 워크스페이스/터미널 액터 토폴로지에
/// 묶여 있어 cross-platform 터미널(macOS pty)이 준비되기 전에는 쓸 수 없다.
/// 여기서는 터미널 도구 4종을 "없음"으로 응답해, 에이전트가 도구를 호출할
/// 대상이 없으니 곧장 사용자에게 답하도록 둔다. OS 도구는 인터페이스의
/// 기본 구현("not available")을 그대로 사용한다.
/// </summary>
public sealed class ChatOnlyToolbelt : IAgentToolbelt
{
    public Task<string> ListTerminalsAsync(CancellationToken ct)
        => Task.FromResult("{\"groups\":[]}");

    public Task<string> ReadTerminalAsync(int group, int tab, int lastN, CancellationToken ct)
        => Task.FromResult(string.Empty);

    public Task<bool> SendToTerminalAsync(int group, int tab, string text, CancellationToken ct)
        => Task.FromResult(false);

    public Task<bool> SendKeyAsync(int group, int tab, string key, CancellationToken ct)
        => Task.FromResult(false);
}
