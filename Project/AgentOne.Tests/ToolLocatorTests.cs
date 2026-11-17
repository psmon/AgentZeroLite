using AgentOne.Agent;
using AgentOne.Tools;
using AgentOne.Tools.Cli;

namespace AgentOne.Tests;

/// <summary>
/// Measured (2026-11-16, music-harness): the workspace runs Whisper through its own
/// scripts in an environment prepared long ago (.local/whisper-env, faster-whisper).
/// agent-one ran `python -m whisper` on the system Python, asked to
/// `pip install openai-whisper`, and when that was declined told the user to install
/// it. Nothing had looked. Now looking is built in, and an install of what is
/// already on the machine is refused with the way to run it.
/// </summary>
public class ToolLocatorTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "agent-one-locator-" + Guid.NewGuid().ToString("N")[..8]);
    private string Root => Path.Combine(_base, "work");
    private string Home => Path.Combine(_base, "home");
    private string Bin => Path.Combine(_base, "bin");

    public ToolLocatorTests()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Home);
        Directory.CreateDirectory(Bin);
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private ToolSearch Search(params string[] moreRoots) => new([Root, .. moreRoots], Home, Bin);

    /// <summary>A virtual environment with the given importable packages (and dist-info names as "dist=top").</summary>
    private static string MakeVenv(string dir, params string[] packages)
    {
        var site = Path.Combine(dir, "Lib", "site-packages");
        Directory.CreateDirectory(site);
        Directory.CreateDirectory(Path.Combine(dir, "Scripts"));
        File.WriteAllText(Path.Combine(dir, "pyvenv.cfg"), "home = somewhere");
        File.WriteAllText(Path.Combine(dir, "Scripts", "python.exe"), "");
        foreach (var package in packages)
        {
            if (package.Contains('='))
            {
                var (dist, top) = (package.Split('=')[0], package.Split('=')[1]);
                Directory.CreateDirectory(Path.Combine(site, dist + "-1.0.dist-info"));
                File.WriteAllText(Path.Combine(site, dist + "-1.0.dist-info", "top_level.txt"), top + "\n");
                Directory.CreateDirectory(Path.Combine(site, top));
            }
            else Directory.CreateDirectory(Path.Combine(site, package));
        }
        return dir;
    }

    [Fact]
    public void Nothing_anywhere_is_said_plainly_with_where_it_looked()
    {
        var hits = ToolLocator.Find("whisper", Search());

        Assert.Empty(hits);
        Assert.Contains("was not found on this machine", ToolLocator.Card("whisper", hits));
    }

    [Fact]
    public void A_module_in_a_dot_folder_environment_of_the_workspace_is_found_with_its_python()
    {
        MakeVenv(Path.Combine(Root, ".local", "audio-env"), "librosa");

        var hit = Assert.Single(ToolLocator.Find("librosa", Search()));

        Assert.Equal("python-env", hit.Kind);
        Assert.True(hit.Exact);
        Assert.Contains(".local/audio-env/Scripts/python.exe", hit.Run);
        Assert.Contains("-m librosa", hit.Run);
    }

    [Fact]
    public void A_related_package_already_installed_answers_for_the_one_asked_for()
    {
        // whisper asked, faster-whisper prepared — in the main checkout a worktree was made from.
        var main = Path.Combine(_base, "main");
        MakeVenv(Path.Combine(main, ".local", "whisper-env"), "faster_whisper");

        var hits = ToolLocator.Find("openai-whisper", Search(main));

        var hit = Assert.Single(hits);
        Assert.Equal("faster_whisper", hit.What);
        Assert.False(hit.Exact);
        var card = ToolLocator.Card("openai-whisper", hits);
        Assert.Contains("related one that is already installed", card);
        Assert.Contains("Do not install another copy", card);
    }

    [Fact]
    public void A_package_installed_under_one_name_and_imported_under_another_is_found_by_either()
    {
        MakeVenv(Path.Combine(Home, ".virtualenvs", "cv"), "opencv_python=cv2");

        Assert.Equal("cv2", Assert.Single(ToolLocator.Find("opencv-python", Search()), h => h.Kind == "python-env" && h.Exact).What);
        Assert.Contains(ToolLocator.Find("cv2", Search()), h => h.What == "cv2");
    }

    [Fact]
    public void An_executable_on_the_path_and_a_global_node_package_are_found()
    {
        File.WriteAllText(Path.Combine(Bin, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg"), "");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(Path.Combine(Bin, "ffmpeg"), UnixFileMode.UserRead | UnixFileMode.UserExecute);
        var package = Path.Combine(Home, "AppData", "Roaming", "npm", "node_modules", "playwright");
        Directory.CreateDirectory(package);
        File.WriteAllText(Path.Combine(package, "package.json"), "{}");

        Assert.Contains(ToolLocator.Find("ffmpeg", Search()), h => h.Kind == "path" && h.Exact);
        var node = Assert.Single(ToolLocator.Find("playwright", Search()), h => h.Kind == "node");
        Assert.Contains("require('", node.Run);
    }

    [Fact]
    public void The_workspaces_own_scripts_for_the_tool_are_pointed_at()
    {
        Directory.CreateDirectory(Path.Combine(Root, "stage", "Tools", "whisper"));
        File.WriteAllText(Path.Combine(Root, "stage", "Tools", "whisper", "align.py"), "\"\"\"usage\"\"\"");
        File.WriteAllText(Path.Combine(Root, "stage", "Tools", "unrelated.py"), "");

        var hit = Assert.Single(ToolLocator.Find("whisper", Search()));

        Assert.Equal("workspace", hit.Kind);
        Assert.Equal("stage/Tools/whisper/align.py", hit.What);
        Assert.Contains("read_file stage/Tools/whisper/align.py", hit.Run);
    }

    [Theory]
    [InlineData("pip install openai-whisper", "openai-whisper")]
    [InlineData("python -m pip install --upgrade faster-whisper==1.2.1", "faster-whisper")]
    [InlineData("cd tools; npm install -g @playwright/cli@latest", "@playwright/cli@latest")]
    [InlineData("winget install -e --id Gyan.FFmpeg", "Gyan.FFmpeg")]
    [InlineData("uv pip install librosa soundfile", "librosa,soundfile")]
    public void An_install_command_says_what_it_would_install(string command, string expected)
    {
        var targets = ToolLocator.InstallTargets(command);
        Assert.Equal(expected.Replace("@latest", ""), string.Join(",", targets).Replace("@latest", ""));
    }

    [Theory]
    [InlineData("ls -la", null)]
    [InlineData("git status", null)]
    [InlineData("node install.js", null)]
    public void Other_commands_install_nothing(string command, string? _)
    {
        Assert.Empty(ToolLocator.InstallTargets(command));
    }

    [Theory]
    [InlineData("ModuleNotFoundError: No module named 'whisper'", "whisper")]
    [InlineData("No module named faster_whisper.transcribe", "faster_whisper")]
    [InlineData("The term 'ffprobe' is not recognized as a name of a cmdlet", "ffprobe")]
    [InlineData("bash: yt-dlp: command not found", "yt-dlp")]
    [InlineData("Error: Cannot find module 'playwright'", "playwright")]
    public void A_failure_names_the_tool_that_is_missing(string output, string name)
    {
        Assert.Equal(name, ToolLocator.MissingFrom(output));
    }

    [Fact]
    public void A_missing_module_failure_comes_back_with_where_it_is_and_never_with_install()
    {
        MakeVenv(Path.Combine(Root, ".local", "whisper-env"), "faster_whisper");

        var note = ToolLocator.NoteForFailure("exit code 1\n[stderr]\nNo module named 'whisper'", Search())!;

        Assert.Contains("not a reason to install", note);
        Assert.Contains("whisper-env/Scripts/python.exe", note);

        var absent = ToolLocator.NoteForFailure("No module named 'nowhere_at_all'", Search())!;
        Assert.Contains("Do not install it yourself", absent);
        Assert.Null(ToolLocator.NoteForFailure("exit code 1: file not found: take.mp3", Search()));
    }

    [Fact]
    public async Task The_verb_answers_with_the_card()
    {
        MakeVenv(Path.Combine(Root, ".local", "whisper-env"), "faster_whisper");
        var belt = new KnownCliToolbelt(root: Root) { Search = Search() };

        var result = await belt.InvokeAsync(new ToolCall { Tool = "find_tool", Args = { ["name"] = "whisper" } }, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Contains("faster_whisper", result.Text);
        Assert.False((await belt.InvokeAsync(new ToolCall { Tool = "find_tool" }, CancellationToken.None)).Ok);
    }

    [Fact]
    public async Task The_loop_attaches_the_note_to_the_failed_command()
    {
        MakeVenv(Path.Combine(Root, ".local", "whisper-env"), "faster_whisper");
        var provider = new ScriptedChatProvider(
            """{"tool":"run_command","args":{"command":"python -m whisper take.mp3"}}""",
            ToolCall.Final("stopped").ToJson());
        var loop = new AgentLoop(provider, new FailingShell("exit code 1\n[stderr]\nNo module named 'whisper'"), maxSteps: 4) { Root = Root };

        await loop.RunAsync("transcribe it");

        var back = provider.Calls[1][^1].Content;
        Assert.Contains("No module named 'whisper'", back);
        Assert.Contains("whisper-env", back);
        Assert.Contains("Do not install another copy", back);
    }

    private sealed class FailingShell(string output) : IToolbelt
    {
        public string Scope => "test";
        public Task<ToolResult> InvokeAsync(ToolCall call, CancellationToken ct) => Task.FromResult(ToolResult.Failure(output));
    }
}
