using System.IO;
using System.Linq;
using Agent.Common.Data.Entities;
using Agent.Common.Services;

namespace ZeroCommon.Tests;

/// <summary>
/// The rules behind "is Claude/Codex on this machine, and how would we put it there".
/// All of it is pinned on a machine that has neither installer, because the choice
/// between winget and npm is a decision about the OS, not about what happens to be
/// present when the suite runs.
/// </summary>
[Trait("Category", "AgentCli")]
public sealed class AgentCliToolsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aztest-clitool-" + Guid.NewGuid().ToString("n"));

    public AgentCliToolsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ── matching a definition to a tool ──────────────────────────────────────

    [Theory]
    [InlineData("powershell.exe", "-NoExit -Command claude", "Claude")]
    [InlineData("powershell.exe", "-NoExit -Command codex", "Codex")]
    [InlineData("/bin/zsh", "-l -c \"codex; exec zsh -l\"", "Codex")]
    [InlineData("claude", null, "Claude")]
    public void A_definition_that_launches_an_agent_is_matched_to_it(string exe, string? args, string expected)
    {
        var tool = AgentCliTools.Match(new CliDefinition { ExePath = exe, Arguments = args });
        Assert.NotNull(tool);
        Assert.Equal(expected, tool!.Name);
    }

    [Theory]
    [InlineData("cmd.exe", null)]
    [InlineData("powershell.exe", null)]
    [InlineData("/bin/zsh", "-l")]
    // A folder named after the tool is not the tool — offering to install winget packages
    // because a path contained the letters would be worse than offering nothing.
    [InlineData("powershell.exe", "-NoExit -Command C:\\codex-notes\\open.ps1")]
    public void A_plain_shell_is_matched_to_nothing(string exe, string? args)
    {
        Assert.Null(AgentCliTools.Match(new CliDefinition { ExePath = exe, Arguments = args }));
    }

    [Fact]
    public void Every_built_in_tool_names_a_package_for_both_routes()
    {
        Assert.NotEmpty(AgentCliTools.All);
        foreach (var tool in AgentCliTools.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(tool.Command));
            Assert.False(string.IsNullOrWhiteSpace(tool.WingetId));
            Assert.StartsWith("@", tool.NpmPackage);     // both publish under a scope
            Assert.StartsWith("https://", tool.DocsUrl); // the fallback when neither route works
        }
    }

    // ── locating the executable ──────────────────────────────────────────────

    [Fact]
    public void Locate_finds_a_command_by_extension_on_the_given_path()
    {
        // npm writes a .cmd shim and winget an .exe, so the probe must try both rather
        // than assuming the shape of the install that happened.
        File.WriteAllText(Path.Combine(_dir, "codex.cmd"), "");
        var found = AgentCliTools.Locate("codex", new[] { _dir }, new[] { ".exe", ".cmd" });
        Assert.Equal(Path.Combine(_dir, "codex.cmd"), found);
    }

    [Fact]
    public void Locate_returns_null_when_only_an_unlisted_extension_exists()
    {
        File.WriteAllText(Path.Combine(_dir, "codex.txt"), "");
        Assert.Null(AgentCliTools.Locate("codex", new[] { _dir }, new[] { ".exe", ".cmd" }));
    }

    [Fact]
    public void Locate_survives_a_path_entry_that_is_not_a_usable_directory()
    {
        // A real PATH contains quoted entries, missing folders and the occasional
        // illegal character; one bad entry must not hide a tool that is on the next one.
        File.WriteAllText(Path.Combine(_dir, "claude.exe"), "");
        var entries = new[] { "", "   ", "C:\\nope\\does-not-exist", "\"" + _dir + "\"" };
        Assert.NotNull(AgentCliTools.Locate("claude", entries, new[] { ".exe" }));
    }

    [Fact]
    public void Posix_extensions_are_the_empty_one_only()
    {
        Assert.Equal(new[] { "" }, AgentCliTools.DefaultExtensions(isWindows: false).ToArray());
    }

    [Fact]
    public void Windows_extensions_include_an_extensionless_candidate()
    {
        var exts = AgentCliTools.DefaultExtensions(isWindows: true).ToList();
        var lower = exts.Select(e => e.ToLowerInvariant()).ToList();
        Assert.Contains("", exts);            // an extensionless file on PATH still runs
        Assert.Contains(".exe", lower);
        Assert.Contains(".cmd", lower);       // npm's shim
        // PATHEXT does not list .ps1, but the built-in definitions run inside PowerShell,
        // which resolves one — so a .ps1-only install must not read as missing.
        Assert.Contains(".ps1", lower);
    }

    // ── the install plan ─────────────────────────────────────────────────────

    [Fact]
    public void Windows_installs_through_winget_with_the_verified_package_id()
    {
        var plan = AgentCliTools.PlanInstall(AgentCliTools.Codex, isWindows: true);
        Assert.Equal("winget", plan.Exe);
        Assert.Contains("--id OpenAI.Codex", plan.Arguments);
        Assert.Contains("--exact", plan.Arguments);
        // Output is captured rather than shown in a console, so an interactive prompt
        // would hang the install forever instead of failing.
        Assert.Contains("--disable-interactivity", plan.Arguments);
        Assert.Contains("--accept-package-agreements", plan.Arguments);
    }

    [Fact]
    public void Other_platforms_install_through_npm()
    {
        var plan = AgentCliTools.PlanInstall(AgentCliTools.Claude, isWindows: false);
        Assert.Equal("npm", plan.Exe);
        Assert.Equal("install -g @anthropic-ai/claude-code", plan.Arguments);
    }

    [Fact]
    public void A_plan_that_cannot_run_names_the_missing_installer_and_the_manual_route()
    {
        // Whichever installer this machine lacks, the unusable plan must say which one it
        // was — "install failed" with an exit code is not something a person can act on.
        var plans = new[]
        {
            AgentCliTools.PlanInstall(AgentCliTools.Claude, isWindows: true),
            AgentCliTools.PlanInstall(AgentCliTools.Claude, isWindows: false),
        };

        foreach (var plan in plans)
        {
            if (plan.CanRun) continue;
            Assert.Contains(plan.Exe, plan.Problem!);
            Assert.Contains(AgentCliTools.Claude.DocsUrl, plan.Problem!);
        }
    }

    [Fact]
    public async Task Running_an_unusable_plan_reports_the_problem_and_starts_nothing()
    {
        var plan = new AgentCliInstallPlan("winget", "install --id X", Problem: "winget is not available.");
        var lines = new List<string>();

        var exit = await AgentCliTools.RunInstallAsync(plan, lines.Add);

        Assert.Equal(-1, exit);
        Assert.Equal(new[] { "winget is not available." }, lines);
    }

    [Fact]
    public async Task Running_a_plan_whose_installer_does_not_exist_fails_instead_of_throwing()
    {
        var plan = new AgentCliInstallPlan("agentzero-no-such-installer", "install");
        var lines = new List<string>();

        var exit = await AgentCliTools.RunInstallAsync(plan, lines.Add);

        Assert.Equal(-1, exit);
        Assert.Contains(lines, l => l.Contains("agentzero-no-such-installer"));
    }
}
