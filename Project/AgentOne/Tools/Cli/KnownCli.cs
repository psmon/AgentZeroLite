namespace AgentOne.Tools.Cli;

/// <param name="Name">What the model calls it in cli_guide.</param>
/// <param name="Commands">The executables that mean it is installed, first found wins.</param>
/// <param name="Summary">One line for the system prompt.</param>
/// <param name="Card">The usage the model would otherwise go and search the web for.</param>
/// <param name="InstallHint">What to tell the user when it is missing. The agent never installs it itself.</param>
/// <param name="PythonModule">
/// The module it also runs as (`python -m yt_dlp`) when only pip installed it and no
/// executable is on PATH. Measured (2026-11-06): yt-dlp 2026.06.09 was installed that
/// way, the probe looked for an executable, and the prompt said it was missing.
/// </param>
public sealed record KnownCliTool(string Name, string[] Commands, string Summary, string Card, string InstallHint,
    string? PythonModule = null);

/// <param name="Path">Where it was found, or null when it is not installed.</param>
/// <param name="Run">How to start it when that is not its name — "python -m yt_dlp" — or null.</param>
public sealed record KnownCliStatus(KnownCliTool Tool, string? Path, string? Run = null)
{
    public bool Installed => Path is not null;

    /// <summary>What to type to run it.</summary>
    public string Command => Run ?? Tool.Name;
}

/// <summary>
/// Command-line tools the agent should be able to use without learning them
/// first. Measured (2026-11-05, music-harness): told "use the Playwright CLI in
/// CDP mode", the model ran one web_search, wrote a script that `require`d a
/// package the folder did not have, and asked the person to restart Chrome by
/// hand — while `playwright-cli` sat installed on PATH and a debuggable Chrome
/// was already listening. What a small model is not told it has, it does not
/// have; and research into a tool's flags is the step it fails most.
///
/// So the knowledge is built in: each tool here has a probe (is it on PATH?)
/// and a short card of the commands that matter. The system prompt names the
/// installed ones; <c>cli_guide</c> returns a card. Running a tool still goes
/// through <c>run_command</c> and its gate — a card teaches, it grants nothing.
/// The browser is the exception that became verbs (browser_*), because driving
/// a page takes many small calls a model should not have to spell as commands.
/// </summary>
public static class KnownCli
{
    public static readonly KnownCliTool[] All =
    [
        new("playwright-cli", ["playwright-cli"],
            "drives a real browser — prefer the browser_* tools, which use it for you",
            """
            playwright-cli (@playwright/cli) — Microsoft's browser CLI for agents.
            You normally do NOT call it yourself: browser_open / browser_snapshot / browser_read / browser_find /
            browser_click / browser_fill / browser_press / browser_tabs drive it, attach to the user's Chrome over
            CDP when it listens on the debugging port, and clean up after the session.
            Only for something those verbs cannot do, through run_command, on the same session:
              playwright-cli -s=<session> screenshot            save a PNG of the current tab
              playwright-cli -s=<session> pdf                   save the page as PDF
              playwright-cli -s=<session> go-back | reload
              playwright-cli -s=<session> cookie-list
              playwright-cli list                               the open browser sessions
            Never `close` an attached session (it can close the user's own browser) — `detach` it.
            Never write a Node script that require()s playwright for something the verbs can do.
            """,
            "npm install -g @playwright/cli"),

        new("gh", ["gh"],
            "GitHub from the command line: issues, pull requests, releases, API",
            """
            gh — GitHub CLI (already authenticated if `gh auth status` says so).
              gh auth status
              gh repo view [owner/repo] [--json name,description,url]
              gh issue list [--state open] [--limit 20] / gh issue view <n> [--comments]
              gh pr list / gh pr view <n> [--json title,state,files] / gh pr diff <n> / gh pr checks <n>
              gh run list [--limit 10] / gh run view <id> --log-failed
              gh release list / gh release view <tag>
              gh api repos/<owner>/<repo>/contents/<path>        any REST endpoint; --jq '<expr>' filters
            Creating or commenting (gh issue create, gh pr create/merge, gh release create) changes GitHub for
            everyone — say what you will do and let the gate ask the user.
            """,
            "winget install GitHub.cli  (or https://cli.github.com)"),

        new("git", ["git"],
            "version control of the workspace",
            """
            git — read freely, change carefully.
              git status --short / git log --oneline -n 20 / git diff [--stat] [<path>]
              git show <commit> --stat / git blame -L <a>,<b> <file>
              git branch -a / git worktree list
            Changing history or remotes (commit, push, reset, rebase, checkout over changes, config) is the user's
            call: those always go to the user for approval.
            """,
            "https://git-scm.com/downloads"),

        new("ffmpeg", ["ffmpeg"],
            "convert, cut, join and inspect audio/video (ffprobe for details)",
            """
            ffmpeg / ffprobe — audio and video.
              ffprobe -v error -show_entries format=duration:stream=codec_name,width,height -of json in.mp4
              ffmpeg -y -i in.wav -b:a 192k out.mp3                        convert
              ffmpeg -y -ss 00:00:30 -t 15 -i in.mp3 -c copy clip.mp3       cut 15 s from 0:30 (no re-encode)
              ffmpeg -y -i in.mp4 -vn -acodec copy out.m4a                  extract the audio track
              ffmpeg -y -i video.mp4 -i audio.mp3 -map 0:v -map 1:a -c:v copy -shortest out.mp4   replace audio
              ffmpeg -y -f concat -safe 0 -i list.txt -c copy joined.mp3    join (list.txt: file 'a.mp3' per line)
              ffmpeg -y -i in.mp4 -vf "fps=1,scale=640:-1" frames/%04d.png  frames
            Always pass -y (it otherwise waits for an answer that never comes) and write outputs inside the workspace.
            """,
            "winget install Gyan.FFmpeg"),

        new("yt-dlp", ["yt-dlp"],
            "YouTube and video-site metadata and downloads",
            """
            yt-dlp — facts about a video or channel without a browser. A channel's WHOLE list comes back in one
            call, where a browser page shows only the first screenful.
              yt-dlp --flat-playlist --print "%(id)s %(title)s" "<channel-url>/videos"     every video of a channel
              yt-dlp --skip-download --print "%(upload_date)s %(title)s" <video-url>        when it was uploaded
              yt-dlp -j --skip-download <url>                                               everything, as JSON
              yt-dlp -x --audio-format mp3 -o "%(title)s.%(ext)s" <url>                    audio only
            Titles come back machine-translated into English unless you ask for the channel's language:
              add --add-header "Accept-Language:ko-KR" (or the language the user writes in).
            --flat-playlist is fast but leaves upload_date empty; ask per video for dates.
            Respect the site's terms; download only what the user asked for.
            """,
            "winget install yt-dlp.yt-dlp  (or pip install yt-dlp)", PythonModule: "yt_dlp"),

        new("node", ["node"],
            "runs JavaScript (.js/.cjs/.mjs) and the workspace's npm scripts (npm, npx)",
            """
            node / npm / npx
              node script.cjs [args]            run a script in the workspace
              npm run <script>                  a script from package.json (read package.json first)
              npm ls --depth=0                  what is installed here
              node -e "console.log(process.version)"
            Do not `npm install` unless the user asked for it — use what the workspace already has.
            """,
            "https://nodejs.org"),

        new("python", ["python", "python3", "py"],
            "runs Python scripts",
            """
            python (Windows may also have `py`; uv runs a project's own environment)
              python script.py [args]
              python -m json.tool file.json     pretty-print JSON
              uv run script.py                  inside a uv project (pyproject.toml present)
            Do not `pip install` unless the user asked for it.
            """,
            "https://www.python.org/downloads"),

        new("dotnet", ["dotnet"],
            ".NET SDK: build, test and run C# projects",
            """
            dotnet
              dotnet build <proj.csproj|sln> [-c Release]
              dotnet test <tests.csproj> [--filter "FullyQualifiedName~Name"]
              dotnet run --project <proj.csproj> -- <args>
              dotnet --list-sdks
            """,
            "https://dotnet.microsoft.com/download"),

        new("docker", ["docker"],
            "containers: list, logs, run",
            """
            docker
              docker ps [-a] / docker images / docker logs --tail 100 <container>
              docker compose ps / docker compose logs --tail 100 [service]
            Starting, stopping or removing containers changes the machine — let the gate ask the user.
            """,
            "https://www.docker.com/products/docker-desktop"),

        new("rg", ["rg"],
            "ripgrep: fast regex search across files (the grep tool is plain text)",
            """
            rg (ripgrep) — when a regex is really needed; the grep tool is plain text.
              rg -n "pattern" [path]            -i ignore case, -g "*.cs" filter, -l files only, -C 2 context
            """,
            "winget install BurntSushi.ripgrep.MSVC"),

        new("curl", ["curl"],
            "raw HTTP requests to APIs (web_read is for pages)",
            """
            curl — an API, a header, a download.
              curl -sS <url>                    GET
              curl -sS -I <url>                 headers only
              curl -sS -L -o file.bin <url>     download into the workspace
            Sending data (POST/PUT) somewhere is the user's call.
            """,
            "https://curl.se"),

        new("jq", ["jq"],
            "query and reshape JSON",
            """
            jq
              jq '.items[] | {id, title}' file.json
              <command> | jq -r '.name'
            """,
            "winget install jqlang.jq")
    ];

    private static readonly Lazy<IReadOnlyList<KnownCliStatus>> Detected = new(() => Detect(ExecutableLocator.Find, PythonHasModule));

    /// <summary>Every known tool and where it is, probed once per process.</summary>
    public static IReadOnlyList<KnownCliStatus> Status => Detected.Value;

    /// <summary>Test seam: the same probe over any lookup.</summary>
    /// <param name="hasModule">Whether that python (full path) can run that module; null skips module probes.</param>
    internal static IReadOnlyList<KnownCliStatus> Detect(Func<string, string?> find, Func<string, string, bool>? hasModule = null)
    {
        string? python = null;
        var pythonLooked = false;
        var list = new List<KnownCliStatus>();
        foreach (var tool in All)
        {
            var path = tool.Commands.Select(find).FirstOrDefault(p => p is not null);
            if (path is null && tool.PythonModule is { } module && hasModule is not null)
            {
                if (!pythonLooked) { python = find("python") ?? find("python3") ?? find("py"); pythonLooked = true; }
                if (python is not null && hasModule(python, module))
                {
                    list.Add(new KnownCliStatus(tool, python, $"python -m {module}"));
                    continue;
                }
            }
            list.Add(new KnownCliStatus(tool, path));
        }
        return list;
    }

    /// <summary>`python -m &lt;module&gt; --version` exits 0 within a few seconds. Never fatal.</summary>
    private static bool PythonHasModule(string python, string module)
    {
        try
        {
            var info = new System.Diagnostics.ProcessStartInfo(python)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var arg in new[] { "-m", module, "--version" }) info.ArgumentList.Add(arg);
            using var process = System.Diagnostics.Process.Start(info);
            if (process is null) return false;
            process.StandardInput.Close();
            _ = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(8000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return false;
            }
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    public static KnownCliTool? Find(string name)
    {
        var key = name.Trim().ToLowerInvariant();
        if (key.EndsWith(".exe")) key = key[..^4];
        return All.FirstOrDefault(t => t.Name == key || t.Commands.Contains(key)
                                       || (key is "playwright" or "@playwright/cli" && t.Name == "playwright-cli")
                                       || (key is "ffprobe" && t.Name == "ffmpeg")
                                       || (key is "npm" or "npx" && t.Name == "node")
                                       || (key is "uv" or "pip" && t.Name == "python")
                                       || (key is "ripgrep" && t.Name == "rg")
                                       || (key is "youtube-dl" or "yt_dlp" && t.Name == "yt-dlp"));
    }

    /// <summary>The system prompt's line per installed tool, or empty when none is.</summary>
    public static string PromptLines(IReadOnlyList<KnownCliStatus> status) =>
        string.Join("\n", status.Where(s => s.Installed).Select(s =>
            $"  {s.Tool.Name} — {s.Tool.Summary}" + (s.Run is { } run ? $" (installed as a module: run it as `{run}`)" : "")));
}
