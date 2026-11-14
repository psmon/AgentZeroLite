using AgentOne.Agent;
using AgentOne.Tools;

namespace AgentOne.Tests;

/// <summary>
/// Measured (2026-11-05, a Claude-skill-based repository): asked again and
/// again to "find it in the Claude skills", the model never saw one — the
/// search skipped every dot-folder, so <c>.claude/skills</c> was invisible, and
/// plugin skills live outside the root. These pin the detection and the wider
/// search: project skills, enabled plugins (and only enabled ones), the
/// workspace paths a skill names, and the order a whole-workspace search walks.
/// </summary>
public class ClaudeSkillScopeTests : IDisposable
{
    private readonly string _base;
    private readonly string _root;
    private readonly string _home;
    private readonly string _pluginDir;

    public ClaudeSkillScopeTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "agent-one-skills-" + Guid.NewGuid().ToString("N")[..8]);
        _root = Path.Combine(_base, "repo");
        _home = Path.Combine(_base, "claude-home");
        _pluginDir = Path.Combine(_home, "plugins", "cache", "market", "garden", "2.1.1");

        // The project: one skill that points at the repo's own knowledge, plus a log that also says "playwright".
        Write(Path.Combine(_root, ".claude", "skills", "image-gen", "SKILL.md"),
            "---\nname: image-gen\ndescription: >-\n  Picks the image backend\n  and calls its script.\nmetadata:\n  type: skill\n---\n\n" +
            "Read `harness/knowledge/tools/browser.md` before running `tools/gen.js`. See https://example.com/a/b too.\n");
        Write(Path.Combine(_root, ".claude", "worktrees", "feat", "x.md"), "playwright in a worktree copy\n");
        Write(Path.Combine(_root, "harness", "knowledge", "tools", "browser.md"), "Attach playwright over CDP port 9222.\n");
        Write(Path.Combine(_root, "tools", "gen.js"), "// generator\n");
        Write(Path.Combine(_root, "aaa-logs", "old.md"), "playwright was mentioned in an old log\n");
        Write(Path.Combine(_root, ".git", "config"), "playwright\n");

        // The user's Claude home: one enabled plugin, one disabled, one installed only for another project.
        Write(Path.Combine(_home, "settings.json"), """
            { "enabledPlugins": { "garden@market": true, "off@market": false, "elsewhere@market": true } }
            """);
        Write(Path.Combine(_home, "plugins", "installed_plugins.json"), $$"""
            { "version": 2, "plugins": {
                "garden@market": [ { "scope": "user", "installPath": {{Json(_pluginDir)}} } ],
                "off@market": [ { "scope": "user", "installPath": {{Json(Path.Combine(_base, "off"))}} } ],
                "elsewhere@market": [ { "scope": "project", "projectPath": {{Json(Path.Combine(_base, "other"))}}, "installPath": {{Json(Path.Combine(_base, "elsewhere"))}} } ]
            } }
            """);
        Write(Path.Combine(_pluginDir, "skills", "garden-keeper", "SKILL.md"),
            "---\nname: garden-keeper\ndescription: \"Tends harness/ — read harness/knowledge first\"\n---\nUse the playwright notes in references/cdp.md.\n");
        Write(Path.Combine(_pluginDir, "skills", "garden-keeper", "references", "cdp.md"), "plugin playwright reference\n");
        Write(Path.Combine(_base, "off", "skills", "disabled", "SKILL.md"), "---\nname: disabled\n---\n");
        Write(Path.Combine(_base, "elsewhere", "skills", "foreign", "SKILL.md"), "---\nname: foreign\n---\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static string Json(string path) => "\"" + path.Replace("\\", "\\\\") + "\"";

    private ClaudeSkillScope Detect() => ClaudeSkillScope.Detect(_root, [_home]);

    private LocalFileToolbelt Belt() => new(_root, Detect());

    private static ToolResult Run(IToolbelt belt, string tool, params (string Key, string Value)[] args)
    {
        var call = new ToolCall { Tool = tool };
        foreach (var (k, v) in args) call.Args[k] = v;
        return belt.InvokeAsync(call, CancellationToken.None).GetAwaiter().GetResult();
    }

    [Fact]
    public void FindsProjectSkillsAndOnlyThePluginsEnabledHere()
    {
        var scope = Detect();
        var names = scope.Skills.Select(s => s.Name).ToList();

        Assert.Contains("image-gen", names);
        Assert.Contains("garden-keeper", names);
        Assert.DoesNotContain("disabled", names);   // enabledPlugins: false
        Assert.DoesNotContain("foreign", names);    // installed for another project only

        var plugin = scope.Skills.Single(s => s.Name == "garden-keeper");
        Assert.Equal(SkillOriginKind.Plugin, plugin.Origin.Kind);
        Assert.Equal("garden", plugin.Origin.Label);
    }

    [Fact]
    public void ReadsFoldedAndQuotedDescriptions()
    {
        var scope = Detect();
        Assert.Equal("Picks the image backend and calls its script.", scope.Skills.Single(s => s.Name == "image-gen").Description);
        Assert.Equal("Tends harness/ — read harness/knowledge first", scope.Skills.Single(s => s.Name == "garden-keeper").Description);
    }

    [Fact]
    public void CollectsTheWorkspacePathsASkillNamesButNotUrls()
    {
        var linked = Detect().LinkedPaths.Select(p => Path.GetRelativePath(_root, p).Replace('\\', '/')).ToList();

        Assert.Contains("harness/knowledge/tools/browser.md", linked);
        Assert.Contains("tools/gen.js", linked);
        Assert.Contains("harness/knowledge", linked);   // named by the plugin's skill, exists in this repo
        Assert.DoesNotContain(linked, p => p.Contains("example.com"));
        Assert.DoesNotContain(linked, p => p.StartsWith("a/"));
    }

    [Fact]
    public void RecordFoldersAreNotLinkedAndProjectFilesComeFirst()
    {
        Write(Path.Combine(_root, "harness", "logs", "tamer", "run.md"), "playwright in a run log\n");
        Write(Path.Combine(_pluginDir, "skills", "garden-keeper", "SKILL.md"),
            "---\nname: garden-keeper\n---\nThe garden: harness/logs, harness/logs/tamer, harness/logs/tamer/run.md, harness/knowledge.\n");

        var linked = Detect().LinkedPaths.Select(p => Path.GetRelativePath(_root, p).Replace('\\', '/')).ToList();

        Assert.DoesNotContain(linked, p => p.StartsWith("harness/logs"));
        Assert.Equal("harness/knowledge/tools/browser.md", linked[0]);   // a file a project skill names
        Assert.Equal("harness/knowledge", linked[^1]);                   // a folder a plugin names
    }

    [Fact]
    public void ThePluginInstalledUnderTwoClaudeHomesIsListedOnce()
    {
        var second = Path.Combine(_base, "claude-profile");
        var secondInstall = Path.Combine(second, "plugins", "cache", "market", "garden", "2.1.1");
        Write(Path.Combine(second, "settings.json"), """{ "enabledPlugins": { "garden@market": true } }""");
        Write(Path.Combine(second, "plugins", "installed_plugins.json"),
            $$"""{ "plugins": { "garden@market": [ { "scope": "user", "installPath": {{Json(secondInstall)}} } ] } }""");
        Write(Path.Combine(secondInstall, "skills", "garden-keeper", "SKILL.md"), "---\nname: garden-keeper\n---\n");

        var scope = ClaudeSkillScope.Detect(_root, [_home, second]);

        var keeper = Assert.Single(scope.Skills, s => s.Name == "garden-keeper");
        Assert.StartsWith(_pluginDir, keeper.Path);   // the first home wins
    }

    [Fact]
    public void ProjectSettingsCanDisableAUserPlugin()
    {
        Write(Path.Combine(_root, ".claude", "settings.local.json"), """{ "enabledPlugins": { "garden@market": false } }""");
        Assert.DoesNotContain(Detect().Skills, s => s.Name == "garden-keeper");
    }

    [Fact]
    public void ListingTheRootNamesTheSkills()
    {
        var result = Run(Belt(), "list_files", ("path", "."));

        Assert.True(result.Ok);
        Assert.Contains(".claude/", result.Text);
        Assert.Contains("image-gen — Picks the image backend", result.Text);
        Assert.Contains(".claude/skills/image-gen/SKILL.md", result.Text);
        Assert.Contains("garden-keeper [plugin garden]", result.Text);
        Assert.Contains("harness/knowledge/tools/browser.md", result.Text);
    }

    [Fact]
    public void ListingASubfolderDoesNotRepeatTheSkills()
    {
        var result = Run(Belt(), "list_files", ("path", "tools"));
        Assert.DoesNotContain("Claude skills", result.Text);
    }

    [Fact]
    public void GrepFromTheRootReachesSkillsPluginsAndLinkedKnowledgeFirst()
    {
        var result = Run(Belt(), "grep", ("text", "playwright"));

        Assert.True(result.Ok);
        var lines = result.Text.Split('\n');
        int Line(string fragment) => Array.FindIndex(lines, l => l.Contains(fragment, StringComparison.Ordinal));

        var plugin = Line("references");
        var knowledge = Line("harness/knowledge/tools/browser.md");
        var log = Line("aaa-logs/old.md");
        Assert.True(plugin >= 0, result.Text);
        Assert.True(knowledge >= 0, result.Text);
        Assert.True(log > knowledge, "linked knowledge must come before an unrelated log:\n" + result.Text);
        Assert.Contains("searched Claude skills first", result.Text);

        Assert.DoesNotContain("worktrees", result.Text);   // .claude/worktrees is a checkout, not a definition
        Assert.DoesNotContain(".git", result.Text);
    }

    [Fact]
    public void FindFilesFromTheRootFindsAProjectSkill()
    {
        var result = Run(Belt(), "find_files", ("pattern", "SKILL.md"));

        Assert.Contains(".claude/skills/image-gen/SKILL.md", result.Text);
        Assert.Contains("garden-keeper", result.Text);
    }

    [Fact]
    public void APluginFolderIsReadableButNeverWritable()
    {
        var belt = Belt();
        var reference = Path.Combine(_pluginDir, "skills", "garden-keeper", "references", "cdp.md");

        var read = Run(belt, "read_file", ("path", reference));
        Assert.True(read.Ok, read.Text);
        Assert.Contains("plugin playwright reference", read.Text);

        var write = Run(belt, "write_file", ("path", reference), ("content", "x"));
        Assert.False(write.Ok);
        Assert.Contains("read-only", write.Text);

        // A disabled plugin's folder stays outside.
        var disabled = Run(belt, "read_file", ("path", Path.Combine(_base, "off", "skills", "disabled", "SKILL.md")));
        Assert.False(disabled.Ok);
    }

    [Fact]
    public void NoSkillsMeansTheOldBehaviour()
    {
        var bare = Path.Combine(_base, "bare");
        Write(Path.Combine(bare, "a.txt"), "hello\n");
        var belt = new LocalFileToolbelt(bare, ClaudeSkillScope.Detect(bare, []));

        var list = Run(belt, "list_files", ("path", "."));
        var grep = Run(belt, "grep", ("text", "hello"));

        Assert.DoesNotContain("Claude skills", list.Text);
        Assert.DoesNotContain("searched Claude skills", grep.Text);
        Assert.Contains("a.txt:1:", grep.Text);
    }
}
