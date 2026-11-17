using System.Text;
using System.Text.RegularExpressions;
using AgentOne.Services;

namespace AgentOne.Tools.Cli;

/// <summary>One place a tool was found.</summary>
/// <param name="Kind">Which finder found it (<see cref="IToolFinder.Kind"/>).</param>
/// <param name="What">The thing found, as it is named there (a module, an executable, a script).</param>
/// <param name="Where">Its path.</param>
/// <param name="Run">How to run it with run_command, or "" when the path is the whole answer.</param>
/// <param name="Exact">False for a near match — a different package that does the same job (faster_whisper for whisper).</param>
public sealed record ToolHit(string Kind, string What, string Where, string Run, bool Exact = true);

/// <summary>Where a search looks: the workspace, and the checkout it is a worktree of.</summary>
/// <param name="Roots">Folders searched for project-local environments and scripts, the workspace first.</param>
/// <param name="Home">The user's home, for per-user tool folders. A test points it at a temp folder.</param>
/// <param name="Path">The PATH to search.</param>
public sealed record ToolSearch(IReadOnlyList<string> Roots, string Home, string Path)
{
    public static ToolSearch For(string workspaceRoot)
    {
        var roots = new List<string> { workspaceRoot };
        // A linked worktree holds only tracked files; the environments earlier work
        // prepared (git-ignored) are in the main checkout.
        if (GitWorkspace.Detect(workspaceRoot) is { Linked: true } git && Directory.Exists(git.MainCheckout))
            roots.Add(git.MainCheckout);
        return new ToolSearch(roots,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetEnvironmentVariable("PATH") ?? "");
    }
}

/// <summary>One way of looking for a tool on this machine. Add a class, add it to <see cref="ToolLocator.Finders"/>.</summary>
public interface IToolFinder
{
    /// <summary>Short label shown with each hit: "path", "python-env", "node", "workspace".</summary>
    string Kind { get; }

    IEnumerable<ToolHit> Find(string name, ToolSearch search);
}

/// <summary>
/// "Is it already on this machine?" — asked before anything is installed.
///
/// Measured (2026-11-16, music-harness): the workspace's procedure runs Whisper
/// through its own scripts in an environment prepared months ago
/// (<c>.local/whisper-env</c>, faster-whisper). agent-one ran <c>python -m whisper</c>
/// on the system Python, got "No module named 'whisper'", asked to
/// <c>pip install openai-whisper</c>, and when that was declined told the user to
/// install it themselves. Nothing had looked. The owner's rule for this track is
/// no-install: use what is prepared.
///
/// So looking is built in, in three places that share this class: the
/// <c>find_tool</c> verb the model can call; the command gate, which refuses an
/// install of something this search finds; and the note attached to a "no such
/// module / command" failure. Each way of looking is an <see cref="IToolFinder"/>;
/// the list is expected to grow as new kinds of tools turn up.
/// </summary>
public static partial class ToolLocator
{
    /// <summary>Every way of looking, in the order their hits are reported.</summary>
    public static readonly IReadOnlyList<IToolFinder> Finders =
    [
        new PathFinder(),
        new PythonEnvironmentFinder(),
        new NodePackageFinder(),
        new WorkspaceScriptFinder(),
    ];

    private const int MaxHits = 12;

    public static IReadOnlyList<ToolHit> Find(string name, ToolSearch search, IReadOnlyList<IToolFinder>? finders = null)
    {
        name = name.Trim().Trim('"', '\'', '`');
        if (name.Length == 0) return [];

        var hits = new List<ToolHit>();
        foreach (var finder in finders ?? Finders)
        {
            try { hits.AddRange(finder.Find(name, search)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        }

        return hits
            .GroupBy(h => (h.Kind, h.Where.ToLowerInvariant(), h.What.ToLowerInvariant()))
            .Select(g => g.First())
            .OrderByDescending(h => h.Exact)
            .Take(MaxHits)
            .ToList();
    }

    /// <summary>The hits as the model reads them: where each is and the command that runs it.</summary>
    public static string Card(string name, IReadOnlyList<ToolHit> hits)
    {
        if (hits.Count == 0)
            return $"'{name}' was not found on this machine (searched: PATH, Python environments in the workspace, " +
                   "its main checkout and the user's tool folders, node packages, and the workspace's own scripts).";

        var sb = new StringBuilder($"'{name}' on this machine:\n");
        foreach (var hit in hits)
        {
            sb.Append(hit.Exact ? "  ✓ " : "  ~ ").Append('[').Append(hit.Kind).Append("] ").Append(hit.What)
              .Append(" — ").Append(Slash(hit.Where));
            if (hit.Run.Length > 0) sb.Append("\n      run: ").Append(hit.Run);
            if (!hit.Exact && hit.Kind != "workspace") sb.Append("\n      (not the same package: a related one that is already installed — prefer it over installing)");
            sb.AppendLine();
        }
        sb.Append("Use what is there. Do not install another copy.");
        return sb.ToString();
    }

    // --------------------------------------------------------------- reading commands

    /// <summary>
    /// What an install command would install, or empty: <c>pip install x</c>, <c>python -m pip install x</c>,
    /// <c>uv pip install</c>, <c>pipx install</c>, <c>conda install</c>, <c>npm install -g</c> / <c>npm i</c>,
    /// <c>winget install</c>, <c>choco install</c>, <c>scoop install</c>, <c>brew install</c>, <c>apt install</c>.
    /// </summary>
    public static IReadOnlyList<string> InstallTargets(string command)
    {
        var targets = new List<string>();
        foreach (Match match in InstallCommand().Matches(command))
        {
            foreach (var token in match.Groups["rest"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.StartsWith('-')) continue;                   // a flag
                if (token is "&&" or "||" or ";" or "|") break;
                var name = token.Trim('"', '\'');
                // winget ids (OpenAI.Whisper) and scoped npm names keep their last part.
                name = VersionSuffix().Replace(name, "");
                if (name.Contains('/') && !name.StartsWith('@')) continue; // a path or URL, not a name
                if (name.Length > 0 && !targets.Contains(name, StringComparer.OrdinalIgnoreCase)) targets.Add(name);
            }
        }
        return targets;
    }

    /// <summary>
    /// The tool a failure says is missing, or null: Python's "No module named 'x'", PowerShell's
    /// "'x' is not recognized", a shell's "x: command not found", Node's "Cannot find module 'x'".
    /// </summary>
    public static string? MissingFrom(string output)
    {
        foreach (var pattern in (Regex[])[NoModule(), NotRecognized(), CommandNotFound(), CannotFindModule()])
        {
            var match = pattern.Match(output);
            if (!match.Success) continue;
            var name = match.Groups["name"].Value.Trim().Trim('"', '\'');
            // "No module named 'a.b'" is about package a.
            if (pattern == NoModule() && name.Contains('.')) name = name[..name.IndexOf('.')];
            if (name.Length is > 0 and < 80 && !name.Contains(' ')) return name;
        }
        return null;
    }

    /// <summary>
    /// The note for a command's failure, or null when the failure names no missing tool.
    /// When the tool is nowhere on the machine the note says that too — and not to install it unasked.
    /// </summary>
    public static string? NoteForFailure(string output, ToolSearch search)
    {
        if (MissingFrom(output) is not { } name) return null;
        var hits = Find(name, search);
        return hits.Count > 0
            ? "[note] That is not a reason to install anything. " + Card(name, hits)
            : $"[note] {Card(name, hits)} Do not install it yourself: say in \"final\" what is missing and what it is " +
              "needed for, and let the user decide.";
    }

    // --------------------------------------------------------------------- names

    /// <summary>
    /// Other names the same tool goes by: the name you install is often not the name you import or run.
    /// Grown as cases turn up.
    /// </summary>
    private static readonly string[][] Aliases =
    [
        ["whisper", "openai-whisper", "faster-whisper", "faster_whisper", "whisperx"],
        ["cv2", "opencv-python", "opencv-python-headless", "opencv"],
        ["PIL", "pillow"],
        ["sklearn", "scikit-learn"],
        ["yaml", "pyyaml"],
        ["bs4", "beautifulsoup4"],
        ["playwright", "playwright-cli", "@playwright/cli"],
        ["yt-dlp", "yt_dlp"],
        ["demucs", "audio-separator", "audio_separator"],
        ["ffmpeg", "ffprobe"],
    ];

    /// <summary>The name and its aliases, normalised. The first entry is the name asked for.</summary>
    internal static List<(string Name, bool Exact)> NamesFor(string name)
    {
        var wanted = Normal(name);
        var names = new List<(string, bool)> { (wanted, true) };
        foreach (var group in Aliases)
        {
            if (!group.Any(a => Normal(a) == wanted)) continue;
            foreach (var alias in group)
                if (names.All(n => n.Item1 != Normal(alias))) names.Add((Normal(alias), false));
        }
        return names;
    }

    /// <summary>pip's rule: case-insensitive, and '-', '_' and '.' are the same character.</summary>
    internal static string Normal(string name) =>
        name.Trim().ToLowerInvariant().Replace('-', '_').Replace('.', '_');

    internal static string Slash(string path) => path.Replace('\\', '/');

    [GeneratedRegex(@"(?:^|[;&|]\s*|\s)(?:(?:python\d?|py)(?:\.exe)?\s+-m\s+pip|pip\d?|uv\s+pip|uv\s+tool|pipx|conda|mamba|npm|pnpm|yarn\s+global|winget|choco|scoop|brew|apt(?:-get)?|sudo\s+apt(?:-get)?)\s+(?:install|i|add)\s+(?<rest>[^;&|\r\n]+)", RegexOptions.IgnoreCase)]
    private static partial Regex InstallCommand();

    [GeneratedRegex(@"(?:[=<>~!]=?[\w.*]+|@[\d^~][\w.\-]*)$")]
    private static partial Regex VersionSuffix();

    [GeneratedRegex(@"No module named ['""]?(?<name>[\w.\-]+)['""]?")]
    private static partial Regex NoModule();

    [GeneratedRegex(@"(?:The term )?'(?<name>[^']+)' is not recognized", RegexOptions.IgnoreCase)]
    private static partial Regex NotRecognized();

    [GeneratedRegex(@"(?:^|\s)(?<name>[\w.\-]+): (?:command )?not found", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex CommandNotFound();

    [GeneratedRegex(@"Cannot find (?:module|package) '(?<name>[^']+)'", RegexOptions.IgnoreCase)]
    private static partial Regex CannotFindModule();
}

/// <summary>An executable on PATH.</summary>
public sealed class PathFinder : IToolFinder
{
    public string Kind => "path";

    public IEnumerable<ToolHit> Find(string name, ToolSearch search)
    {
        foreach (var (candidate, exact) in ToolLocator.NamesFor(name))
        {
            foreach (var spelling in new[] { candidate, candidate.Replace('_', '-') }.Distinct())
            {
                if (ExecutableLocator.Find(spelling, search.Path) is { } path)
                    yield return new ToolHit(Kind, spelling, path, spelling, exact);
            }
        }
    }
}

/// <summary>
/// A Python package in an environment that is not the one <c>python</c> on PATH runs: virtual
/// environments in the workspace and its main checkout (any folder with a <c>pyvenv.cfg</c>, dot
/// folders included), and the user's own — conda, pipx, uv tools, ~/.virtualenvs, per-user installs.
/// </summary>
public sealed class PythonEnvironmentFinder : IToolFinder
{
    public string Kind => "python-env";

    private const int ProjectDepth = 3;

    public IEnumerable<ToolHit> Find(string name, ToolSearch search)
    {
        var names = ToolLocator.NamesFor(name);
        foreach (var env in Environments(search))
        {
            var python = PythonOf(env);
            if (python is null) continue;

            foreach (var site in SitePackages(env))
            {
                foreach (var (candidate, exact) in names)
                {
                    var module = ModuleIn(site, candidate);
                    if (module is null) continue;
                    var py = ToolLocator.Slash(python);
                    yield return new ToolHit(Kind, module, env,
                        $"\"{py}\" your_script.py   — or   \"{py}\" -m {module} …   (this environment's python has {module})",
                        exact);
                }
            }
        }
    }

    /// <summary>The importable name under <paramref name="site"/> that matches, or null.</summary>
    private static string? ModuleIn(string site, string normal)
    {
        foreach (var entry in SafeEntries(site))
        {
            var file = Path.GetFileName(entry);
            var stem = file.EndsWith(".py", StringComparison.OrdinalIgnoreCase) ? file[..^3] : file;
            if (ToolLocator.Normal(stem) == normal && !file.EndsWith(".dist-info", StringComparison.OrdinalIgnoreCase)) return stem;

            // openai_whisper-2024.dist-info: installed under one name, imported under another.
            if (file.EndsWith(".dist-info", StringComparison.OrdinalIgnoreCase))
            {
                var dist = file[..^".dist-info".Length];
                var dash = dist.LastIndexOf('-');
                if (dash > 0 && ToolLocator.Normal(dist[..dash]) == normal) return TopLevelOf(entry) ?? dist[..dash];
            }
        }
        return null;
    }

    private static string? TopLevelOf(string distInfo)
    {
        try
        {
            var top = Path.Combine(distInfo, "top_level.txt");
            return File.Exists(top) ? File.ReadLines(top).FirstOrDefault(l => l.Trim().Length > 0)?.Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    internal static IEnumerable<string> Environments(ToolSearch search)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in search.Roots)
            foreach (var env in VenvsUnder(root, ProjectDepth))
                if (seen.Add(env)) yield return env;

        string[] userFolders =
        [
            ".virtualenvs", "venvs", ".venvs", "envs",
            "miniconda3/envs", "anaconda3/envs", "miniforge3/envs", "mambaforge/envs", ".conda/envs",
            "pipx/venvs", ".local/pipx/venvs", ".local/share/pipx/venvs",
            "AppData/Roaming/uv/tools", ".local/share/uv/tools",
        ];
        foreach (var folder in userFolders)
        {
            var dir = Path.Combine(search.Home, folder);
            if (!Directory.Exists(dir)) continue;
            foreach (var env in SafeDirectories(dir))
                if (PythonOf(env) is not null && seen.Add(env)) yield return env;
        }

        // Whole installs: conda's base, and python.org's per-user folders.
        foreach (var folder in new[] { "miniconda3", "anaconda3", "miniforge3", "AppData/Local/Programs/Python" })
        {
            var dir = Path.Combine(search.Home, folder);
            if (!Directory.Exists(dir)) continue;
            if (PythonOf(dir) is not null && seen.Add(dir)) yield return dir;
            foreach (var sub in SafeDirectories(dir))
                if (Path.GetFileName(sub).StartsWith("Python", StringComparison.OrdinalIgnoreCase) && PythonOf(sub) is not null && seen.Add(sub))
                    yield return sub;
        }
    }

    private static IEnumerable<string> VenvsUnder(string dir, int depth)
    {
        if (File.Exists(Path.Combine(dir, "pyvenv.cfg"))) { yield return dir; yield break; }
        if (depth == 0) yield break;
        foreach (var sub in SafeDirectories(dir))
        {
            var name = Path.GetFileName(sub);
            if (name is "node_modules" or ".git" or "Library" or "bin" or "obj" or "__pycache__" or "Temp") continue;
            foreach (var env in VenvsUnder(sub, depth - 1)) yield return env;
        }
    }

    private static string? PythonOf(string env)
    {
        foreach (var relative in new[] { "Scripts/python.exe", "python.exe", "bin/python3", "bin/python" })
        {
            var path = Path.Combine(env, relative);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private static IEnumerable<string> SitePackages(string env)
    {
        var windows = Path.Combine(env, "Lib", "site-packages");
        if (Directory.Exists(windows)) yield return windows;
        var lib = Path.Combine(env, "lib");
        if (!Directory.Exists(lib)) yield break;
        foreach (var version in SafeDirectories(lib))
        {
            var site = Path.Combine(version, "site-packages");
            if (Directory.Exists(site)) yield return site;
        }
    }

    private static IEnumerable<string> SafeDirectories(string dir)
    {
        try { return Directory.GetDirectories(dir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static IEnumerable<string> SafeEntries(string dir)
    {
        try { return Directory.GetFileSystemEntries(dir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }
}

/// <summary>A node package: in the workspace's (or main checkout's) node_modules, or installed globally.</summary>
public sealed class NodePackageFinder : IToolFinder
{
    public string Kind => "node";

    public IEnumerable<ToolHit> Find(string name, ToolSearch search)
    {
        var folders = new List<string>();
        foreach (var root in search.Roots) folders.Add(Path.Combine(root, "node_modules"));
        folders.Add(Path.Combine(search.Home, "AppData", "Roaming", "npm", "node_modules"));
        folders.Add(Path.Combine(search.Home, ".npm-global", "lib", "node_modules"));
        folders.Add("/usr/local/lib/node_modules");
        folders.Add("/opt/homebrew/lib/node_modules");

        foreach (var folder in folders.Where(Directory.Exists))
        {
            foreach (var (candidate, exact) in ToolLocator.NamesFor(name))
            {
                // Aliases are normalised with '_'; npm names use '-'. Scoped names keep their '@scope/'.
                foreach (var spelling in new[] { name.Trim(), candidate.Replace('_', '-') }.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var package = Path.Combine(folder, spelling.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(Path.Combine(package, "package.json"))) continue;
                    yield return new ToolHit(Kind, spelling, package,
                        $"require('{ToolLocator.Slash(package)}')   — in a script run with node (an absolute path needs no install)",
                        exact || string.Equals(spelling, name.Trim(), StringComparison.OrdinalIgnoreCase));
                }
            }
        }
    }
}

/// <summary>
/// The workspace's own scripts for the job: a folder or a script file named after the tool
/// (<c>Tools/whisper/align.py</c>). A project that wraps a tool has usually solved the
/// environment question inside the wrapper, or says how in its header.
/// </summary>
public sealed class WorkspaceScriptFinder : IToolFinder
{
    public string Kind => "workspace";

    private const int Depth = 4, MaxVisited = 4000;

    private static readonly string[] ScriptExtensions = [".py", ".js", ".cjs", ".mjs", ".ps1", ".sh", ".cs"];

    public IEnumerable<ToolHit> Find(string name, ToolSearch search)
    {
        var words = ToolLocator.NamesFor(name).Select(n => n.Name).Where(n => n.Length >= 3).ToList();
        if (words.Count == 0 || search.Roots.Count == 0) yield break;

        var root = search.Roots[0];
        var visited = 0;
        var found = 0;
        foreach (var hit in Walk(root, Depth))
        {
            yield return hit;
            if (++found >= 6) yield break;
        }

        IEnumerable<ToolHit> Walk(string dir, int depth)
        {
            if (++visited > MaxVisited) yield break;

            foreach (var file in SafeFiles(dir))
            {
                if (!ScriptExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) continue;
                var stem = ToolLocator.Normal(Path.GetFileNameWithoutExtension(file));
                var folder = ToolLocator.Normal(Path.GetFileName(dir));
                if (!words.Any(w => stem.Contains(w) || folder == w)) continue;
                var relative = ToolLocator.Slash(Path.GetRelativePath(root, file));
                yield return new ToolHit(Kind, relative, file,
                    $"read_file {relative} first — its header says how it is run and with which environment", Exact: false);
            }

            if (depth == 0) yield break;
            foreach (var sub in SafeDirectories(dir))
            {
                var folder = Path.GetFileName(sub);
                if (folder.StartsWith('.') || folder is "node_modules" or "Library" or "bin" or "obj" or "Temp" or "__pycache__" or "Assets") continue;
                foreach (var hit in Walk(sub, depth - 1)) yield return hit;
            }
        }
    }

    private static IEnumerable<string> SafeDirectories(string dir)
    {
        try { return Directory.GetDirectories(dir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static IEnumerable<string> SafeFiles(string dir)
    {
        try { return Directory.GetFiles(dir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }
}
