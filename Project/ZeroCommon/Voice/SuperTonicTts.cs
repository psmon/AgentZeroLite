using System.Diagnostics;
using System.IO;
using System.Text;

namespace Agent.Common.Voice;

/// <summary>
/// Local TTS via the SuperTonic Python library (pip install supertonic — Supertone Inc).
/// On-device ONNX runtime, ~99M params, 10 builtin voices (M1..M5, F1..F5),
/// 31 languages incl. Korean. M0020 — first AgentZero provider that shells out
/// to a pip-installed package rather than calling a .NET library or HTTP API.
///
/// Subprocess shape: <c>{python} -c "&lt;SynthesisScript&gt;" text voice lang out steps</c>.
/// We use <c>python -c &lt;script&gt;</c> + the library API (<c>from supertonic
/// import TTS</c>) because the <c>supertonic</c> package ships no
/// <c>__main__.py</c> — <c>python -m supertonic</c> fails with "package and
/// cannot be directly executed". The CLI entry-point script (<c>supertonic.exe</c>
/// in the interpreter's Scripts dir) does exist, but resolving its path from
/// an arbitrary user-supplied <c>python.exe</c> is fragile across
/// per-user / per-machine / Microsoft Store / venv layouts. Inline script
/// gives us one robust path that works wherever the chosen interpreter has
/// the package installed.
/// </summary>
public sealed class SuperTonicTts : ITextToSpeech
{
    public string ProviderName => "Supertonic";
    public string AudioFormat => "wav";

    /// <summary>Builtin voice ids shipped with Supertonic-3 (model card).</summary>
    public static readonly string[] BuiltinVoices =
        ["M1", "M2", "M3", "M4", "M5", "F1", "F2", "F3", "F4", "F5"];

    /// <summary>
    /// ONNX inference steps, 5..12 (Supertonic default = 8). Higher = better
    /// quality, lower = faster. Mirrors the model's <c>--steps</c> flag.
    /// </summary>
    public int Steps { get; set; } = 8;

    /// <summary>
    /// BCP-47 short tag the CLI accepts (ko / en / ja / …). Empty falls back
    /// to <c>"na"</c> per the upstream documentation's "language not available"
    /// behaviour, which auto-detects from script.
    /// </summary>
    public string Language { get; set; } = "";

    private readonly string _pythonExe;
    private readonly IProcessRunner _runner;

    public SuperTonicTts(string pythonExe = "python", IProcessRunner? runner = null)
    {
        _pythonExe = string.IsNullOrWhiteSpace(pythonExe) ? "python" : pythonExe;
        _runner = runner ?? DefaultProcessRunner.Instance;
    }

    public Task<IReadOnlyList<string>> GetAvailableVoicesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>(BuiltinVoices);

    /// <summary>
    /// Resolved interpreter info from the last <see cref="EnsureReadyAsync"/>
    /// call. Lets the caller (Voice settings panel) log the actual
    /// <c>sys.executable</c> and version after a probe — critical for
    /// diagnosing the "two Pythons installed, wrong one on PATH" case where
    /// the resolved interpreter doesn't have the package the user installed
    /// elsewhere.
    /// </summary>
    public string? LastResolvedExecutable { get; private set; }
    public string? LastResolvedVersion { get; private set; }

    // Probe script — single subprocess call returns both sys.executable
    // (the *actual* python.exe path PATH resolved to) and sys.version on
    // separate lines. One call instead of two so phase 1 is still cheap.
    private const string ProbeScript =
        "import sys; print(sys.executable); print(sys.version.split()[0])";

    /// <summary>
    /// One-time prerequisite check. Two-phase probe so we can distinguish
    /// "Python isn't reachable at all" from "Python is fine but supertonic
    /// isn't installed in *this* interpreter" — the second case is invisible
    /// to a single-shot <c>pip show</c> because the Windows Store stub at
    /// <c>%LOCALAPPDATA%\Microsoft\WindowsApps\python.exe</c> exits non-zero
    /// silently and would look identical to "package missing".
    ///
    /// Phase 1: <c>{python} -c "&lt;ProbeScript&gt;"</c> — reports
    /// <c>sys.executable</c> + version. Recording the resolved exe path is
    /// essential because Windows can have multiple Pythons (Store, official,
    /// pyenv, conda) and the user often installs the package into a different
    /// one than PATH resolves <c>python</c> to.
    /// Phase 2: <c>{python} -m pip show supertonic</c> — confirms the package
    /// is installed in the same interpreter's site-packages.
    /// </summary>
    public async Task<bool> EnsureReadyAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        LastResolvedExecutable = null;
        LastResolvedVersion = null;
        var displayName = string.IsNullOrWhiteSpace(_pythonExe) ? "python" : _pythonExe;
        progress?.Report($"Probing interpreter: {displayName} …");

        // ── Phase 1: Python itself reachable? Resolve sys.executable. ─
        string resolvedExe;
        string pythonVersion;
        try
        {
            var probe = await _runner.RunAsync(
                _pythonExe,
                new[] { "-c", ProbeScript },
                stdin: null,
                workingDir: null,
                ct);
            if (probe.ExitCode != 0)
            {
                progress?.Report(
                    $"Python not reachable as '{displayName}' (exit {probe.ExitCode}). " +
                    "If you just installed Python, restart AgentZero so PATH refreshes, " +
                    "or paste the full path (e.g. C:\\Users\\<you>\\AppData\\Local\\Programs\\Python\\Python312\\python.exe).");
                return false;
            }
            var lines = probe.StdOut
                .Split('\n')
                .Select(l => l.Trim())
                .Where(l => !string.IsNullOrEmpty(l))
                .ToArray();
            if (lines.Length < 2)
            {
                progress?.Report(
                    $"'{displayName}' responded but didn't return sys.executable / version. " +
                    $"Raw output: {Truncate(probe.StdOut + probe.StdErr, 200)}");
                return false;
            }
            resolvedExe = lines[0];
            pythonVersion = lines[1];
            LastResolvedExecutable = resolvedExe;
            LastResolvedVersion = pythonVersion;
        }
        catch (Exception ex)
        {
            progress?.Report(
                $"Cannot launch '{displayName}': {ex.Message}. " +
                "Set the full python.exe path or restart AgentZero after installing Python.");
            return false;
        }

        // ── Phase 2: supertonic installed in *this* interpreter? ──────
        progress?.Report($"Python {pythonVersion} at {resolvedExe} — checking supertonic …");
        try
        {
            var res = await _runner.RunAsync(
                _pythonExe,
                new[] { "-m", "pip", "show", "supertonic" },
                stdin: null,
                workingDir: null,
                ct);
            if (res.ExitCode != 0 || string.IsNullOrEmpty(res.StdOut))
            {
                progress?.Report(
                    $"Python {pythonVersion} at {resolvedExe} — supertonic is NOT installed in this interpreter. " +
                    $"Run: \"{resolvedExe}\" -m pip install supertonic " +
                    "(or change the Python field above to the interpreter where you installed it).");
                return false;
            }
            var versionLine = res.StdOut
                .Split('\n')
                .Select(l => l.Trim())
                .FirstOrDefault(l => l.StartsWith("Version:", StringComparison.OrdinalIgnoreCase));
            progress?.Report(versionLine is null
                ? $"Python {pythonVersion} at {resolvedExe} · Supertonic installed."
                : $"Python {pythonVersion} at {resolvedExe} · Supertonic {versionLine}");
            return true;
        }
        catch (Exception ex)
        {
            progress?.Report($"pip show failed: {ex.Message}");
            return false;
        }
    }

    public async Task<byte[]> SynthesizeAsync(string text, string voice, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        var voiceId = string.IsNullOrWhiteSpace(voice) ? "F1" : voice.Trim();
        var lang = string.IsNullOrWhiteSpace(Language) ? "na" : Language.Trim();
        var steps = Math.Clamp(Steps, 5, 12);

        var tempWav = Path.Combine(Path.GetTempPath(), $"agentzero-supertonic-{Guid.NewGuid():N}.wav");
        var args = BuildArgs(text, voiceId, lang, steps, tempWav);

        try
        {
            var res = await _runner.RunAsync(_pythonExe, args, stdin: null, workingDir: null, ct);
            if (res.ExitCode != 0)
                throw new InvalidOperationException(
                    $"supertonic exited with code {res.ExitCode}. stderr: {Truncate(res.StdErr, 1500)}" +
                    $"{ExplainStderr(res.StdErr)}{DumpStderrAndCite(res.StdErr, "synthesize")}");

            if (!File.Exists(tempWav))
                throw new InvalidOperationException(
                    $"supertonic exited 0 but produced no audio at {tempWav}. stderr: {Truncate(res.StdErr, 1500)}" +
                    $"{ExplainStderr(res.StdErr)}{DumpStderrAndCite(res.StdErr, "synthesize")}");

            return await File.ReadAllBytesAsync(tempWav, ct);
        }
        finally
        {
            try { if (File.Exists(tempWav)) File.Delete(tempWav); } catch { }
        }
    }

    /// <summary>Resolves to <c>%USERPROFILE%\.cache\supertonic3\</c> — the HF Hub cache root the library writes to.</summary>
    public static string CacheDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".cache", "supertonic3");

    /// <summary>
    /// Deletes the local Supertonic model cache so the next <see cref="PrewarmModelAsync"/>
    /// re-downloads everything from scratch. M0020 follow-up #7 — exposed for the
    /// "Start fresh" checkbox in <c>ModelDownloadDialog</c>; useful when a
    /// previous cancel left locked partial files that trip <c>shutil.move</c>.
    /// Best-effort: a busy file (antivirus / another process) yields a thrown
    /// exception the caller can surface.
    /// </summary>
    public static void ClearCacheDirectory()
    {
        if (Directory.Exists(CacheDirectory))
            Directory.Delete(CacheDirectory, recursive: true);
    }

    /// <summary>
    /// Settings-time one-shot — explicitly pre-download the ~400 MB Supertonic
    /// model into <see cref="CacheDirectory"/> so subsequent <see cref="SynthesizeAsync"/>
    /// calls never have to do network I/O under a UI timeout.
    ///
    /// When the injected runner implements <see cref="IProcessStreamingRunner"/>,
    /// stderr is consumed line-by-line through <see cref="SuperTonicProgressParser"/>
    /// and forwarded as <see cref="ModelDownloadStatus"/> updates (live progress
    /// bar in the dialog). Otherwise falls back to a single terminal report.
    ///
    /// Operator history: this exists because (#6) operator's WebDev TTS test was
    /// failing on first-run since synthesis itself triggered the download, and
    /// (#7) the same operator then wanted real progress + cancel/resume — both
    /// solved by isolating the download as a Settings-time gesture with live
    /// updates and a "Start fresh" wipe via <see cref="ClearCacheDirectory"/>.
    /// </summary>
    public async Task<bool> PrewarmModelAsync(IProgress<ModelDownloadStatus>? progress = null, CancellationToken ct = default)
    {
        progress?.Report(new ModelDownloadStatus(
            Caption: "Downloading Supertonic model",
            Detail: "Starting — first run pulls ~400 MB to " + CacheDirectory,
            PercentComplete: null,
            IsTerminal: false,
            IsSuccess: false));

        Action<string>? onStderr = progress is null ? null : line =>
        {
            var parsed = SuperTonicProgressParser.Parse(line);
            if (parsed is not null) progress.Report(parsed);
        };

        try
        {
            ProcessRunResult res;
            if (_runner is IProcessStreamingRunner streaming && onStderr is not null)
            {
                res = await streaming.RunStreamingAsync(
                    _pythonExe,
                    new[] { "-c", PrewarmScript },
                    onStderr,
                    onStdoutLine: null,
                    ct);
            }
            else
            {
                res = await _runner.RunAsync(
                    _pythonExe,
                    new[] { "-c", PrewarmScript },
                    stdin: null, workingDir: null, ct);
            }

            if (res.ExitCode != 0)
            {
                progress?.Report(new ModelDownloadStatus(
                    Caption: "Download failed",
                    Detail: $"exit {res.ExitCode} — {Truncate(res.StdErr, 1500)}" +
                            $"{ExplainStderr(res.StdErr)}" +
                            $"{DumpStderrAndCite(res.StdErr, "prewarm")}",
                    PercentComplete: null,
                    IsTerminal: true,
                    IsSuccess: false));
                return false;
            }

            progress?.Report(new ModelDownloadStatus(
                Caption: "Supertonic model cached locally",
                Detail: "Synthesis will be offline-fast from now on.",
                PercentComplete: 100,
                IsTerminal: true,
                IsSuccess: true));
            return true;
        }
        catch (OperationCanceledException)
        {
            progress?.Report(new ModelDownloadStatus(
                Caption: "Download cancelled",
                Detail: "Partial files may remain in the cache. Tick 'Start fresh' before retrying if subsequent attempts fail.",
                PercentComplete: null,
                IsTerminal: true,
                IsSuccess: false));
            throw;
        }
        catch (Exception ex)
        {
            progress?.Report(new ModelDownloadStatus(
                Caption: "Download crashed",
                Detail: ex.Message,
                PercentComplete: null,
                IsTerminal: true,
                IsSuccess: false));
            return false;
        }
    }

    /// <summary>
    /// Tack a human-actionable hint onto stderr when we recognise a known
    /// Windows failure mode. Keeps callers from having to teach this in every
    /// catch site.
    /// </summary>
    private static string ExplainStderr(string stderr)
    {
        if (string.IsNullOrEmpty(stderr)) return "";
        if (stderr.Contains("PermissionError", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("WinError 5", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("Access is denied", StringComparison.OrdinalIgnoreCase))
        {
            return " — model cache lock detected (likely antivirus or a previous cancelled download). " +
                   "Try: close other supertonic processes, delete %USERPROFILE%\\.cache\\supertonic3\\, " +
                   "then click Download Model in Settings → Voice → Supertonic.";
        }
        if (stderr.Contains("ModuleNotFoundError", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("No module named", StringComparison.OrdinalIgnoreCase))
        {
            return " — supertonic package missing in this interpreter. Re-run Check Install in Settings.";
        }
        if (stderr.Contains("429", StringComparison.OrdinalIgnoreCase)
            && stderr.Contains("HfHub", StringComparison.OrdinalIgnoreCase))
        {
            return " — HuggingFace Hub rate-limited the anonymous request (HTTP 429). " +
                   "Set the HF_TOKEN environment variable to a free huggingface.co token and restart AgentZero, " +
                   "or wait ~60 minutes for the limit to reset.";
        }
        if (stderr.Contains("HfHubHTTPError", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("HTTPError", StringComparison.OrdinalIgnoreCase))
        {
            return " — HuggingFace Hub returned an HTTP error. Check internet/proxy; " +
                   "if behind a corporate proxy, set HTTPS_PROXY env var for the interpreter.";
        }
        if (stderr.Contains("ConnectionError", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("ReadTimeoutError", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("ConnectTimeout", StringComparison.OrdinalIgnoreCase))
        {
            return " — network connection error during HF Hub download. " +
                   "Retry with 'Start fresh' unchecked (HF Hub library resumes partial files automatically), or check network/firewall.";
        }
        if (stderr.Contains("OSError", StringComparison.OrdinalIgnoreCase)
            && stderr.Contains("No space left", StringComparison.OrdinalIgnoreCase))
        {
            return " — out of disk space on the drive holding %USERPROFILE%\\.cache\\supertonic3\\. Free up ~500 MB and retry.";
        }
        return "";
    }

    /// <summary>
    /// Inline Python that just instantiates <see cref="TTS"/> with
    /// <c>auto_download=True</c> and pokes the voice loader. M0020 follow-up
    /// #6: separating the ~400 MB first-run download from real synthesis
    /// dodges the failure mode where a synthesis call's cancellation kills
    /// the download mid-flight and leaves locked partial files in
    /// <c>~/.cache/supertonic3/</c> that later <c>shutil.move</c> can't
    /// overwrite (PermissionError / WinError 5 from antivirus or stale
    /// handles). Runs to completion as an explicit user gesture instead.
    /// </summary>
    // Script must stay ASCII-only — Python on a Korean Windows host defaults
    // stdout/stderr to cp949 unless PYTHONIOENCODING is set, and any non-ASCII
    // character (em-dash, ellipsis, etc.) in a print() crashes with
    // UnicodeEncodeError. We also pass PYTHONIOENCODING=utf-8 via env var
    // (see DefaultProcessRunner.NewPsi) but keeping the script ASCII is
    // belt-and-braces — protects against future operators dropping the env
    // var or running the script another way.
    internal const string PrewarmScript =
        "import sys\n" +
        "from supertonic import TTS\n" +
        "print('[supertonic] loading TTS + downloading model if needed (~400 MB on first run)...', flush=True)\n" +
        "tts = TTS(auto_download=True)\n" +
        "_ = tts.get_voice_style(voice_name='F1')\n" +
        "print('[supertonic] cache warm - synthesis is now offline-fast.', flush=True)\n";

    /// <summary>
    /// Inline Python that drives <c>supertonic.TTS</c>. Reads text/voice/lang/out/steps
    /// from <c>sys.argv[1:6]</c> so we never have to escape user text into the
    /// script body itself — Process.StartInfo.ArgumentList handles quoting per
    /// argument. Saves WAV via the library's own <c>tts.save_audio</c> (uses
    /// soundfile internally — no extra dependency).
    ///
    /// <c>auto_download=True</c> stays here as a safety net in case the user
    /// skipped Prewarm, but the recommended flow is to click Download Model
    /// once first so synthesis never has to do network I/O under a UI timeout.
    /// </summary>
    internal const string SynthesisScript =
        "import sys\n" +
        "from supertonic import TTS\n" +
        "text, voice, lang, out, steps = sys.argv[1:6]\n" +
        "tts = TTS(auto_download=True)\n" +
        "style = tts.get_voice_style(voice_name=voice)\n" +
        "kwargs = dict(text=text, voice_style=style, total_steps=int(steps))\n" +
        "if lang and lang != 'na': kwargs['lang'] = lang\n" +
        "wav, _ = tts.synthesize(**kwargs)\n" +
        "tts.save_audio(wav, out)\n";

    /// <summary>
    /// Command-line builder, factored out so tests can verify quoting without
    /// spawning a real process. Returns the full argv array passed to the
    /// chosen Python interpreter.
    /// </summary>
    internal static string[] BuildArgs(string text, string voice, string lang, int steps, string outputPath)
    {
        return new[]
        {
            "-c", SynthesisScript,
            text,
            voice,
            lang,
            outputPath,
            steps.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
    }

    // Keep the TAIL of long stderr, not the head — Python tracebacks live at
    // the bottom (line containing the actual exception), tqdm progress lines
    // dominate the top and are useless for diagnosis.
    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.Length <= max) return s;
        return "…(head trimmed)…\n" + s.Substring(s.Length - max);
    }

    /// <summary>
    /// Writes the full stderr to <c>%LOCALAPPDATA%\AgentZeroLite\logs\supertonic-last-error.log</c>
    /// when a Supertonic subprocess fails. Lets the operator share the full
    /// traceback without having to scrape the dialog or transcribe long lines.
    /// Best-effort: file IO errors are swallowed silently.
    /// </summary>
    private static string DumpStderrAndCite(string stderr, string scenario)
    {
        if (string.IsNullOrEmpty(stderr)) return "";
        try
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AgentZeroLite", "logs");
            Directory.CreateDirectory(logDir);
            var dumpPath = Path.Combine(logDir, "supertonic-last-error.log");
            File.WriteAllText(dumpPath,
                $"# Supertonic {scenario} @ {DateTime.Now:O}\n" +
                $"# Length: {stderr.Length} chars\n\n{stderr}");
            return $" Full stderr → {dumpPath}";
        }
        catch { return ""; }
    }
}

/// <summary>
/// Subprocess invocation seam. <see cref="DefaultProcessRunner"/> shells out
/// via <see cref="Process"/>; tests inject a fake to record argv without
/// spawning anything. Lives next to <see cref="SuperTonicTts"/> because it's
/// the only consumer today — promote to its own file when a second caller
/// appears.
/// </summary>
public interface IProcessRunner
{
    Task<ProcessRunResult> RunAsync(
        string fileName,
        IReadOnlyList<string> args,
        string? stdin,
        string? workingDir,
        CancellationToken ct);
}

/// <summary>
/// Streaming extension of <see cref="IProcessRunner"/> — fires the supplied
/// callbacks once per line written to stderr/stdout, instead of waiting for
/// the process to exit. Lets the prewarm path show tqdm progress live in the
/// download dialog. Optional: callers that get an <see cref="IProcessRunner"/>
/// only fall back to <see cref="RunAsync"/>.
/// </summary>
public interface IProcessStreamingRunner : IProcessRunner
{
    Task<ProcessRunResult> RunStreamingAsync(
        string fileName,
        IReadOnlyList<string> args,
        Action<string>? onStderrLine,
        Action<string>? onStdoutLine,
        CancellationToken ct);
}

public sealed record ProcessRunResult(int ExitCode, string StdOut, string StdErr);

internal sealed class DefaultProcessRunner : IProcessRunner, IProcessStreamingRunner
{
    public static readonly DefaultProcessRunner Instance = new();

    public async Task<ProcessRunResult> RunAsync(
        string fileName,
        IReadOnlyList<string> args,
        string? stdin,
        string? workingDir,
        CancellationToken ct)
    {
        var psi = NewPsi(fileName, args, stdin, workingDir);
        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start process: {fileName}");

        if (stdin is not null)
        {
            await p.StandardInput.WriteAsync(stdin.AsMemory(), ct);
            p.StandardInput.Close();
        }

        var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = p.StandardError.ReadToEndAsync(ct);

        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { TryKillTree(p); throw; }

        return new ProcessRunResult(p.ExitCode, await stdoutTask, await stderrTask);
    }

    public async Task<ProcessRunResult> RunStreamingAsync(
        string fileName,
        IReadOnlyList<string> args,
        Action<string>? onStderrLine,
        Action<string>? onStdoutLine,
        CancellationToken ct)
    {
        var psi = NewPsi(fileName, args, stdin: null, workingDir: null);
        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start process: {fileName}");

        var stdoutSb = new StringBuilder();
        var stderrSb = new StringBuilder();

        // Pump stdout/stderr concurrently on background tasks so a long
        // download doesn't deadlock if the child fills one buffer.
        var stdoutPump = PumpAsync(p.StandardOutput, stdoutSb, onStdoutLine, ct);
        var stderrPump = PumpAsync(p.StandardError, stderrSb, onStderrLine, ct);

        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { TryKillTree(p); throw; }

        // Drain whatever the child wrote between exit and pump end.
        try { await Task.WhenAll(stdoutPump, stderrPump); } catch { }

        return new ProcessRunResult(p.ExitCode, stdoutSb.ToString(), stderrSb.ToString());
    }

    private static async Task PumpAsync(
        StreamReader reader, StringBuilder accumulator, Action<string>? onLine, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) return; // EOF
            accumulator.AppendLine(line);
            try { onLine?.Invoke(line); } catch { /* progress callbacks shouldn't break the pump */ }
        }
    }

    private static ProcessStartInfo NewPsi(
        string fileName, IReadOnlyList<string> args, string? stdin, string? workingDir)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (!string.IsNullOrEmpty(workingDir)) psi.WorkingDirectory = workingDir;
        // Force Python to write stdout/stderr as UTF-8 instead of the
        // locale-default (cp949 on Korean Windows). Without this, any non-
        // ASCII print() crashes the script — the M0020 follow-up #10 user
        // had supertonic download succeed (26/26 files) only for the final
        // status print to throw UnicodeEncodeError because the line had an
        // em-dash. PYTHONLEGACYWINDOWSSTDIO=0 cooperates with this on 3.7+.
        psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
        psi.EnvironmentVariables["PYTHONLEGACYWINDOWSSTDIO"] = "0";
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    private static void TryKillTree(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
    }
}
