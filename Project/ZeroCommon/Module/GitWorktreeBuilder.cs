using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;

namespace Agent.Common.Module;

/// <summary>
/// git worktree management (missions W4/W7, orca-adoption). Treats a worktree as
/// a first-class workspace object (orca's core "parallel worktrees" idea). The
/// porcelain parser is pure &amp; headlessly testable; the exec wrappers shell out
/// to the <c>git</c> binary. Kept in ZeroCommon (Process is BCL, WPF-free) so
/// both the CLI and the actor layer can reuse it.
/// </summary>
public static class GitWorktreeBuilder
{
    public sealed record Worktree(string Path, string Head, string Branch, bool Bare, bool Detached);

    public sealed record GitResult(bool Ok, string StdOut, string StdErr);

    /// <summary>
    /// Parses <c>git worktree list --porcelain</c> output into structured rows.
    /// Records are separated by blank lines; keys are <c>worktree</c>, <c>HEAD</c>,
    /// <c>branch</c>, <c>bare</c>, <c>detached</c>.
    /// </summary>
    public static IReadOnlyList<Worktree> ParseWorktreeList(string porcelain)
    {
        var list = new List<Worktree>();
        if (string.IsNullOrWhiteSpace(porcelain)) return list;

        string? path = null, head = "", branch = "";
        bool bare = false, detached = false;

        void Flush()
        {
            if (path is not null)
                list.Add(new Worktree(path, head, branch, bare, detached));
            path = null; head = ""; branch = ""; bare = false; detached = false;
        }

        foreach (var raw in porcelain.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) { Flush(); continue; }

            if (line.StartsWith("worktree ", StringComparison.Ordinal)) path = line["worktree ".Length..];
            else if (line.StartsWith("HEAD ", StringComparison.Ordinal)) head = line["HEAD ".Length..];
            else if (line.StartsWith("branch ", StringComparison.Ordinal))
                branch = StripRefsHeads(line["branch ".Length..]);
            else if (line == "bare") bare = true;
            else if (line == "detached") detached = true;
        }
        Flush();
        return list;
    }

    private static string StripRefsHeads(string r)
        => r.StartsWith("refs/heads/", StringComparison.Ordinal) ? r["refs/heads/".Length..] : r;

    /// <summary>Lists worktrees for the repo containing <paramref name="repoDir"/>.</summary>
    public static async Task<IReadOnlyList<Worktree>> ListAsync(string repoDir)
    {
        var res = await RunGitAsync(repoDir, "worktree list --porcelain").ConfigureAwait(false);
        return res.Ok ? ParseWorktreeList(res.StdOut) : Array.Empty<Worktree>();
    }

    /// <summary>
    /// Adds a worktree at <paramref name="path"/>. When <paramref name="branch"/>
    /// is given, creates it (<c>-b</c>); otherwise checks out the current commit
    /// in detached mode. Returns raw git result.
    /// </summary>
    public static Task<GitResult> AddAsync(string repoDir, string path, string? branch = null)
    {
        var args = string.IsNullOrWhiteSpace(branch)
            ? $"worktree add --detach \"{path}\""
            : $"worktree add -b \"{branch}\" \"{path}\"";
        return RunGitAsync(repoDir, args);
    }

    /// <summary>Removes a worktree (optionally <paramref name="force"/>).</summary>
    public static Task<GitResult> RemoveAsync(string repoDir, string path, bool force = false)
        => RunGitAsync(repoDir, $"worktree remove {(force ? "--force " : "")}\"{path}\"");

    private static async Task<GitResult> RunGitAsync(string workingDir, string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = args,
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return new GitResult(false, "", "failed to start git");
            var outTask = proc.StandardOutput.ReadToEndAsync();
            var errTask = proc.StandardError.ReadToEndAsync();
            // ConfigureAwait(false): the CLI path calls these via GetAwaiter().
            // GetResult() on WPF's UI thread (App.OnStartup carries a
            // DispatcherSynchronizationContext), so continuations must NOT
            // capture it or the .GetResult() deadlocks.
            await proc.WaitForExitAsync().ConfigureAwait(false);
            return new GitResult(proc.ExitCode == 0, await outTask.ConfigureAwait(false), await errTask.ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            return new GitResult(false, "", ex.Message);
        }
    }
}
