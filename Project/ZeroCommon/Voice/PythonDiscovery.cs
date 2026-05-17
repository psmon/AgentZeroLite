using System.Text.RegularExpressions;

namespace Agent.Common.Voice;

/// <summary>
/// One discovered Python interpreter — version label + full exe path. Returned
/// in detection order (newest first, default-marked at top) so the Settings UI
/// can dropdown them with sensible defaults.
/// </summary>
public sealed record PythonInstallation(string Version, string ExecutablePath, bool IsDefault, string Source)
{
    /// <summary>Combo-friendly label. e.g. "Python 3.12 (default) — C:\…\python.exe".</summary>
    public string DisplayLabel =>
        $"Python {Version}{(IsDefault ? " (default)" : "")} — {ExecutablePath}";
}

/// <summary>
/// Enumerates Python installations on the current machine so the user can
/// pick *which* interpreter the Supertonic CLI should run under, instead of
/// silently inheriting whatever PATH resolves <c>python</c> to. M0020
/// follow-up #3 — root cause for the "supertonic installed but not found"
/// reports was always "PATH points at a different Python than the one the
/// user pip-installed into".
///
/// Primary strategy: <c>py -0p</c> (Windows Python Launcher) which reads the
/// PEP 514 registry and lists every interpreter the OS knows about.
/// Fallback when py.exe is missing: probe well-known install roots
/// (python.org per-user, python.org per-machine, Microsoft Store pythoncore).
/// </summary>
public static class PythonDiscovery
{
    public static async Task<IReadOnlyList<PythonInstallation>> EnumerateAsync(
        IProcessRunner? runner = null,
        IFileExistenceProbe? files = null,
        Func<Environment.SpecialFolder, string>? specialFolder = null,
        CancellationToken ct = default)
    {
        runner ??= DefaultProcessRunner.Instance;
        files ??= DefaultFileExistenceProbe.Instance;
        specialFolder ??= Environment.GetFolderPath;

        var found = new List<PythonInstallation>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Strategy 1: py launcher — authoritative for PEP-514-registered installs.
        try
        {
            var res = await runner.RunAsync("py", new[] { "-0p" }, null, null, ct);
            if (res.ExitCode == 0 && !string.IsNullOrEmpty(res.StdOut))
            {
                foreach (var line in res.StdOut.Split('\n'))
                {
                    var parsed = ParsePyLauncherLine(line);
                    if (parsed is null) continue;
                    if (seen.Add(parsed.ExecutablePath)) found.Add(parsed);
                }
            }
        }
        catch
        {
            // py.exe not installed — fall through to filesystem strategy.
        }

        // Strategy 2: filesystem fallback — covers users without py launcher
        // (rare) and Microsoft Store builds the launcher sometimes misses.
        foreach (var candidate in EnumerateWellKnownPaths(specialFolder))
        {
            if (!files.Exists(candidate)) continue;
            if (seen.Add(candidate))
                found.Add(new PythonInstallation(
                    Version: "?",
                    ExecutablePath: candidate,
                    IsDefault: false,
                    Source: "filesystem"));
        }

        return found;
    }

    // Sample lines (real py -0p output):
    //   " -V:3.12 *        C:\Users\me\AppData\Local\Programs\Python\Python312\python.exe"
    //   " -V:3.11          C:\Users\me\AppData\Local\Programs\Python\Python311\python.exe"
    //   " -V:3.13/PythonCore *  C:\Python313\python.exe"     (newer launcher with company tag)
    private static readonly Regex PyLauncherLine = new(
        @"^\s*-V:(?<ver>\d+\.\d+(?:\.\d+)?)(?:/\w+)?\s+(?<def>\*\s+)?(?<path>.+?\.exe)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    internal static PythonInstallation? ParsePyLauncherLine(string line)
    {
        var m = PyLauncherLine.Match(line.TrimEnd('\r'));
        if (!m.Success) return null;
        return new PythonInstallation(
            Version: m.Groups["ver"].Value,
            ExecutablePath: m.Groups["path"].Value.Trim(),
            IsDefault: m.Groups["def"].Success,
            Source: "py-launcher");
    }

    internal static IEnumerable<string> EnumerateWellKnownPaths(
        Func<Environment.SpecialFolder, string> specialFolder)
    {
        var localAppData = specialFolder(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = specialFolder(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = specialFolder(Environment.SpecialFolder.ProgramFilesX86);

        // python.org per-user installs
        if (!string.IsNullOrEmpty(localAppData))
        {
            var perUserRoot = Path.Combine(localAppData, "Programs", "Python");
            for (int minor = 8; minor <= 16; minor++)
                yield return Path.Combine(perUserRoot, $"Python3{minor}", "python.exe");

            // Microsoft Store pythoncore-*
            var storeRoot = Path.Combine(localAppData, "Python");
            for (int minor = 8; minor <= 16; minor++)
            {
                yield return Path.Combine(storeRoot, $"pythoncore-3.{minor}-64", "python.exe");
                yield return Path.Combine(storeRoot, $"pythoncore-3.{minor}-arm64", "python.exe");
            }
        }

        // python.org per-machine installs
        foreach (var root in new[] { programFiles, programFilesX86 })
        {
            if (string.IsNullOrEmpty(root)) continue;
            for (int minor = 8; minor <= 16; minor++)
                yield return Path.Combine(root, $"Python3{minor}", "python.exe");
        }
    }
}

/// <summary>
/// File existence probe — abstracted so PythonDiscovery tests can drive the
/// filesystem-fallback path without touching the real disk.
/// </summary>
public interface IFileExistenceProbe
{
    bool Exists(string path);
}

internal sealed class DefaultFileExistenceProbe : IFileExistenceProbe
{
    public static readonly DefaultFileExistenceProbe Instance = new();
    public bool Exists(string path) => File.Exists(path);
}
