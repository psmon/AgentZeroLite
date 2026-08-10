using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using Agent.Common;
using Agent.Common.Module;

namespace AgentZeroWpf.Services;

/// <summary>
/// Runs <c>git diff</c> in a workspace folder and parses the result via the
/// pure <see cref="GitDiffReader"/> (mission W3, orca-adoption). The shell-out
/// lives WPF-side; the parsing stays headlessly testable in ZeroCommon.
/// </summary>
public static class GitDiffService
{
    public sealed record DiffResult(bool Ok, string? Error, IReadOnlyList<GitDiffReader.DiffFile> Files, string RawDiff);

    /// <summary>
    /// Returns the working-tree diff (staged + unstaged) for the given repo
    /// root. <paramref name="staged"/> selects <c>--cached</c>.
    /// </summary>
    public static async Task<DiffResult> GetDiffAsync(string? repoRoot, bool staged = false)
    {
        if (string.IsNullOrWhiteSpace(repoRoot) || !System.IO.Directory.Exists(repoRoot))
            return new DiffResult(false, "no workspace folder", System.Array.Empty<GitDiffReader.DiffFile>(), "");

        // -U3 = 3 context lines. --no-color so the parser sees clean unified text.
        var args = staged
            ? "diff --cached --no-color -U3"
            : "diff HEAD --no-color -U3";

        var (ok, stdout, stderr) = await RunGitAsync(repoRoot!, args);
        if (!ok)
        {
            // `git diff HEAD` fails in a repo with no commits — fall back to
            // a plain working-tree diff so a fresh repo still shows changes.
            (ok, stdout, stderr) = await RunGitAsync(repoRoot!, "diff --no-color -U3");
            if (!ok)
                return new DiffResult(false, string.IsNullOrWhiteSpace(stderr) ? "git diff failed" : stderr.Trim(),
                    System.Array.Empty<GitDiffReader.DiffFile>(), "");
        }

        var files = GitDiffReader.Parse(stdout);
        return new DiffResult(true, null, files, stdout);
    }

    private static async Task<(bool Ok, string StdOut, string StdErr)> RunGitAsync(string workingDir, string args)
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
            if (proc is null) return (false, "", "failed to start git");

            var outTask = proc.StandardOutput.ReadToEndAsync();
            var errTask = proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync().ConfigureAwait(false);
            var stdout = await outTask.ConfigureAwait(false);
            var stderr = await errTask.ConfigureAwait(false);
            return (proc.ExitCode == 0, stdout, stderr);
        }
        catch (System.Exception ex)
        {
            AppLogger.Log($"[GitDiff] git failed: {ex.Message}");
            return (false, "", ex.Message);
        }
    }
}
