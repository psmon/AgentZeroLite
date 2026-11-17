using System.Diagnostics;
using System.Text.RegularExpressions;

namespace AgentOne.Services;

/// <summary>What git says about the folder a session works in.</summary>
/// <param name="Top">The checkout's top folder — where <c>.git</c> is.</param>
/// <param name="MainCheckout">The repository's main checkout. Equal to <paramref name="Top"/> unless this is a linked worktree.</param>
/// <param name="Linked">True for a linked worktree (<c>git worktree add</c>), false for the main checkout.</param>
/// <param name="Branch">The branch checked out, or a short commit id when detached.</param>
/// <param name="Others">The repository's other checkouts (main and linked), as paths.</param>
public sealed record GitWorkspaceInfo(string Top, string MainCheckout, bool Linked, string Branch, IReadOnlyList<string> Others);

/// <summary>
/// Which checkout of a repository a session is in, and a way to start one in a
/// checkout of its own. Measured (2026-11-16, music-harness): the workspace was
/// in the middle of somebody's work — a feature branch with dozens of modified
/// files — and agent-one had no notion of a checkout at all. Its root is fixed
/// when the session starts and nothing is written outside it, so "work apart
/// from what is there" could not be asked of the model: the separation has to
/// exist before the first turn. <c>--worktree &lt;name&gt;</c> makes it.
///
/// Detection reads git's own files and starts no process, so a machine without
/// git still gets a prompt; only creating a worktree runs git.
/// </summary>
public static partial class GitWorkspace
{
    /// <summary>Branches agent-one creates are namespaced, so they are recognisable and never collide with the person's.</summary>
    public const string BranchPrefix = "agent-one/";

    /// <summary>Null when <paramref name="root"/> is not inside a git checkout.</summary>
    public static GitWorkspaceInfo? Detect(string root)
    {
        try
        {
            for (var dir = new DirectoryInfo(Path.GetFullPath(root)); dir is not null; dir = dir.Parent)
            {
                var dotGit = Path.Combine(dir.FullName, ".git");
                // A folder named .git with no HEAD is not a repository (measured: a stray empty
                // one in a home folder made every folder under it "a checkout on branch (unknown)").
                // git walks on past it; so does this.
                if (Directory.Exists(dotGit) && !File.Exists(Path.Combine(dotGit, "HEAD"))) continue;
                if (Directory.Exists(dotGit))
                    return Describe(Trim(dir.FullName), Trim(dir.FullName), dotGit, dotGit, linked: false);

                if (!File.Exists(dotGit)) continue;

                // A linked worktree (or a submodule) keeps a one-line file: "gitdir: <path>".
                var line = File.ReadAllText(dotGit).Trim();
                if (!line.StartsWith("gitdir:", StringComparison.Ordinal)) return null;
                var gitDir = Path.GetFullPath(line["gitdir:".Length..].Trim(), dir.FullName);

                // Only a worktree's git dir names a common dir; a submodule's is a repository of its own.
                var commonFile = Path.Combine(gitDir, "commondir");
                if (!File.Exists(commonFile))
                    return Describe(Trim(dir.FullName), Trim(dir.FullName), gitDir, gitDir, linked: false);

                var common = Path.GetFullPath(File.ReadAllText(commonFile).Trim(), gitDir);
                var main = string.Equals(Path.GetFileName(Trim(common)), ".git", StringComparison.OrdinalIgnoreCase)
                    ? Trim(Path.GetDirectoryName(Trim(common)) ?? common)
                    : Trim(common); // a bare repository has no main checkout; its folder stands in
                return Describe(Trim(dir.FullName), main, gitDir, common, linked: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }

        return null;
    }

    private static GitWorkspaceInfo Describe(string top, string main, string gitDir, string commonDir, bool linked)
    {
        var others = new List<string>();
        if (linked) others.Add(main);

        var worktrees = Path.Combine(commonDir, "worktrees");
        if (Directory.Exists(worktrees))
        {
            foreach (var entry in Directory.GetDirectories(worktrees))
            {
                // Each holds "gitdir": the path of that worktree's own .git file.
                var pointer = Path.Combine(entry, "gitdir");
                if (!File.Exists(pointer)) continue;
                var path = Trim(Path.GetDirectoryName(File.ReadAllText(pointer).Trim()) ?? "");
                if (path.Length == 0 || SamePath(path, top) || !Directory.Exists(path)) continue;
                others.Add(Trim(Path.GetFullPath(path)));
            }
        }

        return new GitWorkspaceInfo(top, main, linked, ReadBranch(gitDir), others);
    }

    private static string ReadBranch(string gitDir)
    {
        try
        {
            var head = File.ReadAllText(Path.Combine(gitDir, "HEAD")).Trim();
            const string prefix = "ref: refs/heads/";
            if (head.StartsWith(prefix, StringComparison.Ordinal)) return head[prefix.Length..];
            return head.Length >= 8 ? head[..8] + " (detached)" : "(unknown)";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "(unknown)"; }
    }

    /// <summary>
    /// The lines the system prompt carries about the checkout, or "" outside git.
    /// A linked worktree is told whose the main checkout is; the main checkout is
    /// told how a person keeps agent-one's work apart, since the model cannot.
    /// </summary>
    public static string PromptLines(string root)
    {
        var info = Detect(root);
        if (info is null) return "";

        if (info.Linked)
            return
                $"- Git: this workspace is a LINKED WORKTREE on branch '{info.Branch}'. The repository's main checkout is\n" +
                $"  {Slash(info.MainCheckout)} — the user's own working copy, on its own branch, possibly mid-work. Everything you\n" +
                "  write and commit belongs here, on this branch. Never run a git command that changes the main checkout or\n" +
                "  another worktree (git -C elsewhere, checkout / switch / reset / stash / merge there), never switch this\n" +
                "  worktree to another branch, and never push unless the user asks.\n" +
                "  Files git ignores (generated media, caches, downloads) were not copied here: READ them in the main\n" +
                "  checkout by absolute path when earlier work is needed; whatever you make new is made here.";

        var others = info.Others.Count == 0
            ? ""
            : $" It has {info.Others.Count} linked worktree(s): {string.Join(", ", info.Others.Take(4).Select(Slash))}.";
        return
            $"- Git: this workspace is the repository's main checkout, on branch '{info.Branch}'.{others}\n" +
            "  You cannot move to another checkout mid-session. If the user wants the work kept apart from what is\n" +
            "  here, tell them to start agent-one with --worktree <name> (it creates a worktree and works there).";
    }

    /// <summary>
    /// The worktree <paramref name="name"/> of the repository <paramref name="root"/> is in,
    /// created when it does not exist: <c>&lt;main&gt;-worktrees/&lt;name&gt;</c> on branch
    /// <c>agent-one/&lt;name&gt;</c>, starting from the commit <paramref name="root"/> has checked out.
    /// Asking again — from the main checkout or from inside the worktree itself — returns the same folder.
    /// </summary>
    public static bool TryEnsureWorktree(string root, string name, out string path, out string note, out string error)
    {
        path = note = error = "";

        if (!NameRule().IsMatch(name))
        {
            error = $"--worktree name '{name}' — use letters, digits, '.', '_' or '-' (it becomes a folder and a branch)";
            return false;
        }

        var info = Detect(root);
        if (info is null)
        {
            error = $"--worktree needs a git repository; {root} is not inside one";
            return false;
        }

        var target = Trim(Path.Combine(Path.GetDirectoryName(info.MainCheckout) ?? info.MainCheckout,
            Path.GetFileName(info.MainCheckout) + "-worktrees", name));
        var branch = BranchPrefix + name;

        if (Directory.Exists(target))
        {
            var existing = Detect(target);
            if (existing is { Linked: true } && SamePath(existing.Top, target) && SamePath(existing.MainCheckout, info.MainCheckout))
            {
                path = target;
                note = $"worktree: {target} (branch {existing.Branch}) — existing" + CarriedNote(Carry(info.MainCheckout, target));
                return true;
            }
            if (Directory.EnumerateFileSystemEntries(target).Any())
            {
                error = $"--worktree: {target} exists and is not a worktree of {info.MainCheckout}";
                return false;
            }
        }

        // The commit the caller is on, not the main checkout's: asked from a feature
        // branch's checkout, the work starts from that branch. Uncommitted changes stay behind.
        var from = Run(info.Top, ["rev-parse", "HEAD"]);
        if (from.Code != 0)
        {
            error = "--worktree: " + (from.Code == -1 ? "git is not installed or not on PATH" : "this repository has no commit yet");
            return false;
        }

        var hasBranch = Run(info.MainCheckout, ["rev-parse", "--verify", "--quiet", "refs/heads/" + branch]).Code == 0;
        string[] add = hasBranch
            ? ["worktree", "add", target, branch]
            : ["worktree", "add", "-b", branch, target, from.Out.Trim()];
        var made = Run(info.MainCheckout, add);
        if (made.Code != 0)
        {
            error = "--worktree: git worktree add failed — " + Last(made.Err.Length > 0 ? made.Err : made.Out);
            return false;
        }

        path = target;
        note = $"worktree: {target} (branch {branch}) — created from {info.Branch}" +
               (hasBranch ? ", on the existing branch" : "") +
               "; uncommitted changes and git-ignored files stay in " + info.Top +
               CarriedNote(Carry(info.MainCheckout, target));
        return true;
    }

    /// <summary>
    /// Local configuration a checkout keeps out of git by convention — keys and endpoints
    /// the project's own scripts read from the repository root.
    /// </summary>
    internal static readonly string[] CarriedNames = [".env", ".env.local", ".secret", ".secrets"];

    /// <summary>No single carried entry may be larger than this; a folder that size is not configuration.</summary>
    private const long CarryCapBytes = 2 * 1024 * 1024;

    /// <summary>
    /// Copies the main checkout's local configuration into a worktree that lacks it.
    ///
    /// Measured (2026-11-16, music-harness): the workspace's image scripts read
    /// <c>.secret/local-image.json</c> from the repository root. A worktree holds only what
    /// git tracks, so every such script fails there with "no key" — and the way round it
    /// that a model finds is to read the key out of the main checkout into its own
    /// context, which is a secret in a conversation log. So the file travels by copy,
    /// never through the model: only names from <see cref="CarriedNames"/>, only when the
    /// worktree's own git rules ignore them (a carried secret must not become committable),
    /// only when small, and never over something already there.
    /// </summary>
    internal static List<string> Carry(string main, string worktree)
    {
        var carried = new List<string>();
        foreach (var name in CarriedNames)
        {
            try
            {
                var from = Path.Combine(main, name);
                var to = Path.Combine(worktree, name);
                var isDir = Directory.Exists(from);
                if (!isDir && !File.Exists(from)) continue;
                if (Directory.Exists(to) || File.Exists(to)) continue;
                if (Run(worktree, ["check-ignore", "-q", isDir ? name + "/" : name]).Code != 0) continue;

                var files = isDir
                    ? Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories).Take(201).ToList()
                    : [from];
                if (files.Count > 200 || files.Sum(f => new FileInfo(f).Length) > CarryCapBytes) continue;

                foreach (var file in files)
                {
                    var dest = isDir ? Path.Combine(to, Path.GetRelativePath(from, file)) : to;
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(file, dest, overwrite: false);
                }
                carried.Add(name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return carried;
    }

    private static string CarriedNote(List<string> carried) =>
        carried.Count == 0 ? "" : $"; local configuration copied (git-ignored, not committed): {string.Join(", ", carried)}";

    private static (int Code, string Out, string Err) Run(string cwd, string[] args)
    {
        try
        {
            var start = new ProcessStartInfo("git")
            {
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in args) start.ArgumentList.Add(arg);

            using var process = Process.Start(start);
            if (process is null) return (-1, "", "");
            process.StandardInput.Close();
            var stderr = process.StandardError.ReadToEndAsync();
            var stdout = process.StandardOutput.ReadToEnd();
            // A large checkout takes minutes; a hung one must not hold the start forever.
            if (!process.WaitForExit(TimeSpan.FromMinutes(15)))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return (-2, stdout, "git did not finish in 15 minutes");
            }
            return (process.ExitCode, stdout, stderr.GetAwaiter().GetResult());
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return (-1, "", ex.Message);
        }
    }

    private static string Last(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? "(no output)" : lines[^1];
    }

    private static string Trim(string path) => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string Slash(string path) => path.Replace('\\', '/');

    public static bool SamePath(string a, string b) =>
        string.Equals(Trim(Path.GetFullPath(a)), Trim(Path.GetFullPath(b)),
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex NameRule();
}
