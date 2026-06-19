using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Agent.Common;
using Agent.Common.Llm.Tools;
using Agent.Common.Services;
using AgentZeroAvalonia.Services;

namespace AgentZeroAvalonia.Tools;

/// <summary>
/// 실제 터미널 제어 toolbelt — <see cref="ChatOnlyToolbelt"/>를 대체한다.
/// <see cref="TerminalRegistry"/>에 등록된 cross-platform PTY 세션을 조회해
/// 에이전트의 list/read/send/send_key 도구 호출을 수행한다.
///
/// WPF의 WorkspaceTerminalToolHost와 동일한 JSON 응답 형태를 유지해 에이전트
/// 루프(ExternalAgentLoop)가 결과를 동일하게 해석하도록 한다. (first-contact
/// 자기소개 핸드셰이크는 MVP에서 생략 — 후속.)
/// </summary>
public sealed class PtyTerminalToolbelt : IAgentToolbelt
{
    public Task<string> ListTerminalsAsync(CancellationToken ct)
    {
        var tabsByGroup = TerminalRegistry.Snapshot()
            .GroupBy(e => e.Group)
            .OrderBy(g => g.Key);

        var groups = new JsonArray();
        foreach (var g in tabsByGroup)
        {
            var tabs = new JsonArray();
            foreach (var t in g.OrderBy(x => x.Tab))
            {
                tabs.Add(new JsonObject
                {
                    ["index"] = t.Tab,
                    ["title"] = t.Title,
                    ["running"] = t.Running,
                });
            }
            groups.Add(new JsonObject
            {
                ["index"] = g.Key,
                ["name"] = $"group{g.Key}",
                ["tabs"] = tabs,
            });
        }

        var root = new JsonObject { ["groups"] = groups };
        return Task.FromResult(root.ToJsonString());
    }

    public Task<string> ReadTerminalAsync(int group, int tab, int lastN, CancellationToken ct)
    {
        if (!TerminalRegistry.TryResolve(group, tab, out var session) || session is null)
            return Task.FromResult(ErrorJson($"terminal [{group}:{tab}] not found"));

        try
        {
            string text;
            if (lastN > 0)
            {
                int total = session.OutputLength;
                int start = System.Math.Max(0, total - lastN);
                int len = total - start;
                text = len > 0 ? session.ReadOutput(start, len) : "";
            }
            else
            {
                text = session.GetConsoleText();
            }

            text = ApprovalParser.StripAnsiCodes(text);
            var resp = new JsonObject
            {
                ["ok"] = true,
                ["group_index"] = group,
                ["tab_index"] = tab,
                ["length"] = text.Length,
                ["text"] = text,
            };
            return Task.FromResult(resp.ToJsonString());
        }
        catch (System.Exception ex)
        {
            AppLogger.Log($"[AIMODE] read_terminal FAILED [{group}:{tab}] {ex.GetType().Name}: {ex.Message}");
            return Task.FromResult(ErrorJson($"Read failed: {ex.Message}"));
        }
    }

    public Task<bool> SendToTerminalAsync(int group, int tab, string text, CancellationToken ct)
    {
        if (!TerminalRegistry.TryResolve(group, tab, out var session) || session is null)
            return Task.FromResult(false);
        session.WriteAndSubmit(text);
        return Task.FromResult(true);
    }

    public Task<bool> SendKeyAsync(int group, int tab, string key, CancellationToken ct)
    {
        if (!TerminalRegistry.TryResolve(group, tab, out var session) || session is null)
            return Task.FromResult(false);

        // 토큰 별칭 → TerminalControl 매핑 (IAgentToolbelt 문서의 키 집합).
        var ctrl = key.ToLowerInvariant() switch
        {
            "cr" or "lf" or "crlf" => TerminalControl.Enter,
            "esc" => TerminalControl.Escape,
            "tab" => TerminalControl.Tab,
            "shifttab" or "backtab" => TerminalControl.BackTab,
            "backspace" => TerminalControl.Backspace,
            "del" => TerminalControl.Delete,
            "ctrlc" => TerminalControl.Interrupt,
            "up" => TerminalControl.UpArrow,
            "down" => TerminalControl.DownArrow,
            "left" => TerminalControl.LeftArrow,
            "right" => TerminalControl.RightArrow,
            _ => (TerminalControl?)null,
        };
        if (ctrl is null) return Task.FromResult(false);
        session.SendControl(ctrl.Value);
        return Task.FromResult(true);
    }

    private static string ErrorJson(string message)
        => new JsonObject { ["ok"] = false, ["error"] = message }.ToJsonString();
}
