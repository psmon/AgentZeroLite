using System.Diagnostics;
using AgentOne.Agent;
using AgentOne.Commands;
using AgentOne.Services;
using AgentOne.Tools;

namespace AgentOne.Tests;

/// <summary>
/// A session knows which checkout it is in, and <c>--worktree</c> starts it in one
/// of its own — the separation agent-one cannot make mid-session, because its root
/// is fixed and nothing is written outside it.
/// </summary>
[Collection(AgentOneHomeCollection.Name)] // AgentOptions.TryParse reads the config under AGENT_ONE_HOME
public sealed class GitWorkspaceTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "agent-one-git-" + Guid.NewGuid().ToString("N")[..8]);
    private string Repo => Path.Combine(_base, "repo");

    public GitWorkspaceTests() => Directory.CreateDirectory(Repo);

    public void Dispose()
    {
        try
        {
            // git marks its object files read-only.
            foreach (var file in Directory.EnumerateFiles(_base, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_base, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static bool Git(string cwd, params string[] args)
    {
        try
        {
            var start = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    /// <summary>A repository with one commit on <c>work</c>, an ignored file and an uncommitted edit. False without git.</summary>
    private bool MakeRepo()
    {
        if (!Git(Repo, "init", "-q", "-b", "work")) return false;
        File.WriteAllText(Path.Combine(Repo, "tracked.txt"), "one");
        File.WriteAllText(Path.Combine(Repo, ".gitignore"), "out/\n");
        Directory.CreateDirectory(Path.Combine(Repo, "out"));
        File.WriteAllText(Path.Combine(Repo, "out", "take.txt"), "made earlier");
        Assert.True(Git(Repo, "add", "."));
        Assert.True(Git(Repo, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "first"));
        File.WriteAllText(Path.Combine(Repo, "tracked.txt"), "the person's unsaved work");
        return true;
    }

    private static ToolResult Invoke(IToolbelt belt, string tool, string path, string? content = null)
    {
        var call = new ToolCall { Tool = tool, Args = { ["path"] = path } };
        if (content is not null) call.Args["content"] = content;
        return belt.InvokeAsync(call, CancellationToken.None).GetAwaiter().GetResult();
    }

    [Fact]
    public void A_folder_outside_git_has_no_checkout_and_no_prompt_line()
    {
        Assert.Null(GitWorkspace.Detect(_base));
        Assert.Equal("", GitWorkspace.PromptLines(_base));
        Assert.False(GitWorkspace.TryEnsureWorktree(_base, "x", out _, out _, out var error));
        Assert.Contains("git repository", error);
    }

    [Fact]
    public void The_main_checkout_is_named_with_its_branch_and_the_way_to_work_apart()
    {
        if (!MakeRepo()) return;

        var info = GitWorkspace.Detect(Path.Combine(Repo, "out"));
        Assert.NotNull(info);
        Assert.False(info!.Linked);
        Assert.Equal("work", info.Branch);
        Assert.True(GitWorkspace.SamePath(Repo, info.Top));

        var lines = GitWorkspace.PromptLines(Repo);
        Assert.Contains("main checkout, on branch 'work'", lines);
        Assert.Contains("--worktree", lines);
    }

    [Fact]
    public void Worktree_is_created_beside_the_repository_on_its_own_branch_and_leaves_the_checkout_alone()
    {
        if (!MakeRepo()) return;

        Assert.True(GitWorkspace.TryEnsureWorktree(Repo, "song-1", out var path, out var note, out var error), error);

        Assert.True(GitWorkspace.SamePath(Path.Combine(_base, "repo-worktrees", "song-1"), path));
        Assert.Contains("created from work", note);

        var linked = GitWorkspace.Detect(path)!;
        Assert.True(linked.Linked);
        Assert.Equal("agent-one/song-1", linked.Branch);
        Assert.True(GitWorkspace.SamePath(Repo, linked.MainCheckout));

        // The committed file came along; the person's uncommitted edit and the ignored output did not, and are untouched.
        Assert.Equal("one", File.ReadAllText(Path.Combine(path, "tracked.txt")));
        Assert.False(Directory.Exists(Path.Combine(path, "out")));
        Assert.Equal("the person's unsaved work", File.ReadAllText(Path.Combine(Repo, "tracked.txt")));
        Assert.Equal("work", GitWorkspace.Detect(Repo)!.Branch);
        Assert.Contains(GitWorkspace.Detect(Repo)!.Others, other => GitWorkspace.SamePath(other, path));
    }

    [Fact]
    public void Asking_again_from_the_checkout_or_from_inside_the_worktree_gives_the_same_folder()
    {
        if (!MakeRepo()) return;
        Assert.True(GitWorkspace.TryEnsureWorktree(Repo, "song-1", out var first, out _, out var error), error);
        File.WriteAllText(Path.Combine(first, "made-here.txt"), "kept");

        Assert.True(GitWorkspace.TryEnsureWorktree(Repo, "song-1", out var again, out var note, out error), error);
        Assert.True(GitWorkspace.SamePath(first, again));
        Assert.Contains("existing", note);

        // What `ask` hands the background session it starts: -r <worktree> --worktree <name>.
        Assert.True(GitWorkspace.TryEnsureWorktree(first, "song-1", out var inside, out _, out error), error);
        Assert.True(GitWorkspace.SamePath(first, inside));
        Assert.True(File.Exists(Path.Combine(first, "made-here.txt")));
    }

    [Fact]
    public void Local_configuration_git_ignores_travels_by_copy_and_nothing_else_does()
    {
        if (!MakeRepo()) return;
        File.WriteAllText(Path.Combine(Repo, ".gitignore"), "out/\n.secret/\n");
        Assert.True(Git(Repo, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-am", "ignore secrets"));
        Directory.CreateDirectory(Path.Combine(Repo, ".secret"));
        File.WriteAllText(Path.Combine(Repo, ".secret", "image.json"), "{\"apiKey\":\"k\"}");
        File.WriteAllText(Path.Combine(Repo, ".env"), "NOT_IGNORED=1"); // git would let this one be committed

        Assert.True(GitWorkspace.TryEnsureWorktree(Repo, "song-1", out var path, out var note, out var error), error);

        Assert.Equal("{\"apiKey\":\"k\"}", File.ReadAllText(Path.Combine(path, ".secret", "image.json")));
        Assert.False(File.Exists(Path.Combine(path, ".env")));
        Assert.Contains("local configuration copied", note);
        Assert.Contains(".secret", note);

        // An existing worktree that lacks it gets it; one that has its own keeps its own.
        File.WriteAllText(Path.Combine(path, ".secret", "image.json"), "its own");
        Assert.True(GitWorkspace.TryEnsureWorktree(Repo, "song-1", out _, out note, out error), error);
        Assert.Equal("its own", File.ReadAllText(Path.Combine(path, ".secret", "image.json")));
        Assert.DoesNotContain("copied", note);
    }

    [Fact]
    public void A_name_that_is_not_a_folder_and_branch_name_is_refused()
    {
        if (!MakeRepo()) return;
        Assert.False(GitWorkspace.TryEnsureWorktree(Repo, "../elsewhere", out _, out _, out var error));
        Assert.Contains("--worktree name", error);
        Assert.False(Directory.Exists(Path.Combine(_base, "elsewhere")));
    }

    [Fact]
    public void The_flag_moves_the_root_wherever_it_stands_among_the_arguments()
    {
        if (!MakeRepo()) return;

        Assert.True(AgentOptions.TryParse(["--worktree", "a", "-r", Repo, "hello"], out var before, out var error), error);
        Assert.True(AgentOptions.TryParse(["-r", Repo, "--worktree", "a"], out var after, out error), error);

        Assert.True(GitWorkspace.SamePath(Path.Combine(_base, "repo-worktrees", "a"), before.Root));
        Assert.True(GitWorkspace.SamePath(before.Root, after.Root));
        Assert.Contains("--worktree", before.Flags);
        Assert.Equal(["hello"], before.Positional);
    }

    [Fact]
    public void A_linked_worktree_is_told_whose_the_main_checkout_is_and_may_only_read_it()
    {
        if (!MakeRepo()) return;
        Assert.True(GitWorkspace.TryEnsureWorktree(Repo, "song-1", out var path, out _, out var error), error);

        var prompt = SystemPrompt.Build(path);
        Assert.Contains("LINKED WORKTREE on branch 'agent-one/song-1'", prompt);
        Assert.Contains(Repo.Replace('\\', '/'), prompt);
        Assert.Contains("Never run a git command that changes the main checkout", prompt);

        // The ignored output is not in the worktree; it is readable where it was made, and only readable.
        var files = new LocalFileToolbelt(path);
        var earlier = Path.Combine(Repo, "out", "take.txt").Replace('\\', '/');
        Assert.False(Invoke(files, "read_file", earlier).Ok);

        files.GrantRead(GitWorkspace.Detect(path)!.MainCheckout);
        Assert.True(Invoke(files, "read_file", earlier).Ok);
        Assert.False(Invoke(files, "write_file", Path.Combine(Repo, "new.txt").Replace('\\', '/'), "x").Ok);
        Assert.False(File.Exists(Path.Combine(Repo, "new.txt")));
    }
}
