using System.Text;
using AgentOne.Agent;

namespace AgentOne.Tools.Cli;

/// <summary>
/// <c>cli_guide</c>: the built-in usage card of a well-known command-line tool
/// (<see cref="KnownCli"/>) and whether it is installed here. It reads no file
/// and runs nothing, so a route never rules it out.
/// </summary>
public sealed class KnownCliToolbelt(IReadOnlyList<KnownCliStatus>? status = null) : IToolbelt
{
    private IReadOnlyList<KnownCliStatus> Status => status ?? KnownCli.Status;

    public string Scope => "built-in usage of known command-line tools";

    public Task<ToolResult> InvokeAsync(ToolCall call, CancellationToken ct)
    {
        if (!string.Equals(call.Tool, "cli_guide", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(ToolResult.Failure($"unknown tool '{call.Tool}'"));

        var name = call.Arg("name").Trim();
        if (name.Length == 0) return Task.FromResult(ToolResult.Success(List()));

        var tool = KnownCli.Find(name);
        if (tool is null)
            return Task.FromResult(ToolResult.Failure(
                $"no built-in guide for '{name}'. Known: {string.Join(", ", KnownCli.All.Select(t => t.Name))}. " +
                $"For another tool, run `{name} --help` with run_command before guessing its flags."));

        var where = Status.FirstOrDefault(s => s.Tool == tool)?.Path;
        var sb = new StringBuilder();
        sb.AppendLine(where is null
            ? $"{tool.Name}: NOT installed on this machine. Do not install it yourself; tell the user it can be installed with: {tool.InstallHint}"
            : $"{tool.Name}: installed ({where}). Run it with run_command.");
        sb.AppendLine();
        sb.Append(tool.Card.Trim());
        return Task.FromResult(ToolResult.Success(sb.ToString()));
    }

    private string List()
    {
        var sb = new StringBuilder("Known command-line tools (cli_guide {\"name\":\"…\"} for usage):\n");
        foreach (var s in Status)
            sb.Append(s.Installed ? "  ✓ " : "  ✗ ").Append(s.Tool.Name).Append(" — ").Append(s.Tool.Summary)
              .AppendLine(s.Installed ? "" : " (not installed)");
        return sb.ToString().TrimEnd();
    }
}
