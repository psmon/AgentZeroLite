using Agent.Common.Voice;

namespace ZeroCommon.Tests.Voice;

/// <summary>
/// Headless coverage for the pip-based Supertonic provider (M0020).
/// All filesystem + subprocess calls are mocked through <see cref="FakeProcessRunner"/>
/// so the test never requires Python or the supertonic package installed —
/// the suite must stay green on a fresh CI image.
/// </summary>
public sealed class SuperTonicTtsTests
{
    [Fact]
    public void BuiltinVoices_lists_ten_voices_in_M_then_F_order()
    {
        Assert.Equal(10, SuperTonicTts.BuiltinVoices.Length);
        Assert.Equal(new[] { "M1", "M2", "M3", "M4", "M5", "F1", "F2", "F3", "F4", "F5" },
            SuperTonicTts.BuiltinVoices);
    }

    [Fact]
    public async Task GetAvailableVoicesAsync_returns_builtins()
    {
        var tts = new SuperTonicTts();
        var voices = await tts.GetAvailableVoicesAsync();
        Assert.Equal(SuperTonicTts.BuiltinVoices, voices);
    }

    [Fact]
    public void Builds_argv_via_python_dash_c_with_library_script()
    {
        var args = SuperTonicTts.BuildArgs(
            text: "안녕하세요, 수퍼토닉입니다.",
            voice: "F3",
            lang: "ko",
            steps: 8,
            outputPath: @"C:\Temp\out.wav");

        // -c invocation — supertonic package has no __main__.py so -m doesn't work.
        Assert.Equal("-c", args[0]);
        Assert.Contains("from supertonic import TTS", args[1]);
        Assert.Contains("tts.synthesize", args[1]);
        Assert.Contains("save_audio", args[1]);

        // text / voice / lang / out / steps in that order (script reads sys.argv[1:6]).
        Assert.Equal("안녕하세요, 수퍼토닉입니다.", args[2]);
        Assert.Equal("F3", args[3]);
        Assert.Equal("ko", args[4]);
        Assert.Equal(@"C:\Temp\out.wav", args[5]);
        Assert.Equal("8", args[6]);
    }

    [Fact]
    public async Task EnsureReadyAsync_returns_true_when_python_and_pip_show_succeed()
    {
        var runner = new FakeProcessRunner();
        runner.ProbeReturnsExe("C:\\Python312\\python.exe", "3.12.5");
        runner.Responses["-m pip show supertonic"] = new ProcessRunResult(
            0, "Name: supertonic\nVersion: 1.2.3\nSummary: Lightning-fast on-device TTS\n", "");
        var progress = new SyncProgress();
        var tts = new SuperTonicTts("python", runner);

        var ok = await tts.EnsureReadyAsync(progress);

        Assert.True(ok);
        // Success message should include resolved exe path + Python version + Supertonic
        // version so the user can sanity-check WHICH interpreter the package landed in.
        Assert.Contains(progress.Messages,
            m => m.Contains("C:\\Python312\\python.exe") && m.Contains("3.12.5") && m.Contains("1.2.3"));
        Assert.Equal("C:\\Python312\\python.exe", tts.LastResolvedExecutable);
        Assert.Equal("3.12.5", tts.LastResolvedVersion);
    }

    [Fact]
    public async Task EnsureReadyAsync_distinguishes_python_unreachable_from_missing_package()
    {
        // Phase 1 fails: probe script exits with 9009 (Windows Store stub behaviour).
        var runner = new FakeProcessRunner();
        runner.ProbeFails(9009);
        var progress = new SyncProgress();
        var tts = new SuperTonicTts("python", runner);

        var ok = await tts.EnsureReadyAsync(progress);

        Assert.False(ok);
        Assert.Contains(progress.Messages,
            m => m.Contains("Python not reachable") && m.Contains("restart AgentZero"));
        // Critically: we should NOT have advanced to phase 2 and falsely claim
        // "supertonic not installed" when the real problem is python itself.
        Assert.DoesNotContain(progress.Messages, m => m.Contains("pip install supertonic"));
        Assert.Null(tts.LastResolvedExecutable);
    }

    [Fact]
    public async Task EnsureReadyAsync_records_resolved_exe_path_for_diagnostics()
    {
        // The mission-critical case from operator's app-log.txt: user has
        // multiple Pythons installed (3.14 on PATH, 3.12 with supertonic).
        // Phase 1 must capture sys.executable so the operator can see WHICH
        // python landed the probe — the previous version logged python=''
        // and lost that information.
        var wrongPython = "C:\\Users\\psmon\\AppData\\Local\\Python\\pythoncore-3.14-64\\python.exe";
        var runner = new FakeProcessRunner();
        runner.ProbeReturnsExe(wrongPython, "3.14.0");
        runner.Responses["-m pip show supertonic"] = new ProcessRunResult(1, "", "No module named supertonic");
        var progress = new SyncProgress();
        var tts = new SuperTonicTts("python", runner);

        var ok = await tts.EnsureReadyAsync(progress);

        Assert.False(ok);
        Assert.Equal(wrongPython, tts.LastResolvedExecutable);
        Assert.Equal("3.14.0", tts.LastResolvedVersion);
        // The failure message must name the actual exe path so user knows
        // which interpreter to either install into or stop pointing at.
        Assert.Contains(progress.Messages, m => m.Contains(wrongPython));
    }

    [Fact]
    public async Task EnsureReadyAsync_reports_missing_package_when_python_works_but_pip_show_fails()
    {
        var runner = new FakeProcessRunner();
        runner.ProbeReturnsExe("C:\\Python312\\python.exe", "3.12.5");
        runner.Responses["-m pip show supertonic"] = new ProcessRunResult(1, "", "WARNING: Package(s) not found: supertonic\n");
        var progress = new SyncProgress();
        var tts = new SuperTonicTts("python", runner);

        var ok = await tts.EnsureReadyAsync(progress);

        Assert.False(ok);
        Assert.Contains(progress.Messages,
            m => m.Contains("supertonic is NOT installed") && m.Contains("pip install supertonic"));
    }

    [Fact]
    public async Task EnsureReadyAsync_rejects_phase1_output_without_executable_and_version_lines()
    {
        // Defensive: a stub that returns exit 0 with garbage stdout (some
        // Windows shims do this) must NOT advance to phase 2 — we have no
        // confidence the python is real.
        var runner = new FakeProcessRunner();
        runner.Responses[$"-c {SuperTonicTtsProbeScript}"] = new ProcessRunResult(0, "garbage\n", "");
        var progress = new SyncProgress();
        var tts = new SuperTonicTts("python", runner);

        var ok = await tts.EnsureReadyAsync(progress);

        Assert.False(ok);
        Assert.Contains(progress.Messages, m => m.Contains("didn't return sys.executable"));
    }

    // Mirror of SuperTonicTts.ProbeScript — kept here so the test reads the
    // exact key the runner will dispatch on. If the probe script changes,
    // this constant must change in lockstep.
    private const string SuperTonicTtsProbeScript =
        "import sys; print(sys.executable); print(sys.version.split()[0])";

    [Fact]
    public async Task EnsureReadyAsync_returns_false_when_runner_throws_on_python_probe()
    {
        var runner = new FakeProcessRunner { ThrowOnRun = new System.ComponentModel.Win32Exception("not found") };
        var progress = new SyncProgress();
        var tts = new SuperTonicTts("python-missing", runner);

        var ok = await tts.EnsureReadyAsync(progress);

        Assert.False(ok);
        Assert.Contains(progress.Messages,
            m => m.Contains("Cannot launch 'python-missing'") && m.Contains("restart AgentZero"));
    }

    private sealed class SyncProgress : IProgress<string>
    {
        public List<string> Messages { get; } = new();
        public void Report(string value) => Messages.Add(value);
    }

    [Fact]
    public async Task SynthesizeAsync_passes_voice_lang_steps_to_runner_and_reads_wav()
    {
        var runner = new FakeProcessRunner
        {
            ExitCode = 0,
            OnRun = (file, args, _, _) =>
            {
                // Find the temp wav path the provider chose — under -c invocation
                // it's the arg ending in .wav (not necessarily last after the steps
                // value moved to the end of argv). Drop a fake WAV so SynthesizeAsync
                // can read it back.
                var outPath = args.First(a => a.EndsWith(".wav", StringComparison.OrdinalIgnoreCase));
                System.IO.File.WriteAllBytes(outPath, new byte[] { 1, 2, 3, 4 });
            },
        };
        var tts = new SuperTonicTts("python", runner) { Steps = 10, Language = "ko" };

        var audio = await tts.SynthesizeAsync("hello", "M2");

        Assert.Equal(new byte[] { 1, 2, 3, 4 }, audio);
        Assert.NotNull(runner.LastArgs);
        // -c <script> text voice lang outPath steps — verify each positional
        Assert.Equal("-c", runner.LastArgs![0]);
        Assert.Equal("hello", runner.LastArgs![2]);
        Assert.Equal("M2", runner.LastArgs![3]);
        Assert.Equal("ko", runner.LastArgs![4]);
        Assert.EndsWith(".wav", runner.LastArgs![5]);
        Assert.Equal("10", runner.LastArgs![6]);
    }

    [Fact]
    public async Task SynthesizeAsync_defaults_voice_F1_and_language_na_when_unset()
    {
        var runner = new FakeProcessRunner
        {
            ExitCode = 0,
            OnRun = (_, args, _, _) => System.IO.File.WriteAllBytes(
                args.First(a => a.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)), new byte[] { 0 }),
        };
        var tts = new SuperTonicTts("python", runner); // Language unset

        await tts.SynthesizeAsync("ping", voice: "");

        // voice (arg[3]) defaults to F1; lang (arg[4]) defaults to "na".
        Assert.Equal("F1", runner.LastArgs![3]);
        Assert.Equal("na", runner.LastArgs![4]);
    }

    [Fact]
    public async Task SynthesizeAsync_clamps_steps_to_valid_range()
    {
        var runner = new FakeProcessRunner
        {
            ExitCode = 0,
            OnRun = (_, args, _, _) => System.IO.File.WriteAllBytes(
                args.First(a => a.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)), new byte[] { 0 }),
        };
        var tts = new SuperTonicTts("python", runner) { Steps = 99 };

        await tts.SynthesizeAsync("ping", "M1");

        // Clamp upper bound 12 — steps is the last positional arg under -c invocation.
        Assert.Equal("12", runner.LastArgs![^1]);
    }

    [Fact]
    public async Task SynthesizeAsync_returns_empty_when_text_blank()
    {
        var runner = new FakeProcessRunner();
        var tts = new SuperTonicTts("python", runner);

        var audio = await tts.SynthesizeAsync("   ", "F1");

        Assert.Empty(audio);
        Assert.Null(runner.LastArgs); // no subprocess invoked
    }

    [Fact]
    public async Task SynthesizeAsync_throws_when_supertonic_exits_nonzero()
    {
        var runner = new FakeProcessRunner
        {
            ExitCode = 2,
            StdErr = "ModuleNotFoundError: supertonic",
        };
        var tts = new SuperTonicTts("python", runner);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => tts.SynthesizeAsync("hi", "F1"));
        Assert.Contains("exited with code 2", ex.Message);
        Assert.Contains("ModuleNotFoundError", ex.Message);
    }

    [Fact]
    public async Task SynthesizeAsync_throws_when_supertonic_returns_zero_but_no_audio_file()
    {
        var runner = new FakeProcessRunner { ExitCode = 0 }; // doesn't write the temp file
        var tts = new SuperTonicTts("python", runner);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => tts.SynthesizeAsync("hi", "F1"));
    }

    /// <summary>
    /// Test-only IProcessRunner. Two ways to script behaviour: (a) per-argv
    /// <see cref="Responses"/> dictionary keyed by space-joined args — used by
    /// the EnsureReadyAsync two-phase tests so phase 1 (python -V) and phase 2
    /// (pip show) can return different things; (b) single-shot <see cref="ExitCode"/>
    /// / <see cref="StdOut"/> / <see cref="OnRun"/> — used by the SynthesizeAsync
    /// tests which only invoke the runner once.
    /// </summary>
    [Fact]
    public async Task PrewarmModelAsync_runs_isolated_download_script()
    {
        var runner = new FakeProcessRunner { ExitCode = 0, StdOut = "[supertonic] cache warm\n" };
        var progress = new StatusCapture();
        var tts = new SuperTonicTts("python", runner);

        var ok = await tts.PrewarmModelAsync(progress);

        Assert.True(ok);
        // Must invoke the Prewarm script, NOT the synthesis script (separation
        // is the whole point — synthesis should never trigger a download).
        Assert.Equal("-c", runner.LastArgs![0]);
        Assert.Contains("auto_download=True", runner.LastArgs![1]);
        Assert.Contains("get_voice_style", runner.LastArgs![1]);
        Assert.DoesNotContain("synthesize", runner.LastArgs![1]);
        // Terminal status must be success + 100%.
        var terminal = progress.Statuses.Last();
        Assert.True(terminal.IsTerminal);
        Assert.True(terminal.IsSuccess);
        Assert.Equal(100, terminal.PercentComplete);
        Assert.Contains("cached locally", terminal.Caption);
    }

    [Fact]
    public async Task PrewarmModelAsync_returns_false_with_actionable_message_on_permission_error()
    {
        var runner = new FakeProcessRunner
        {
            ExitCode = 1,
            StdErr = "PermissionError: [WinError 5] Access is denied: 'C:\\Users\\me\\.cache\\supertonic3\\foo'",
        };
        var progress = new StatusCapture();
        var tts = new SuperTonicTts("python", runner);

        var ok = await tts.PrewarmModelAsync(progress);

        Assert.False(ok);
        var terminal = progress.Statuses.Last();
        Assert.True(terminal.IsTerminal);
        Assert.False(terminal.IsSuccess);
        Assert.Contains("cache lock detected", terminal.Detail);
        Assert.Contains(".cache\\supertonic3", terminal.Detail);
    }

    [Fact]
    public async Task PrewarmModelAsync_streams_tqdm_progress_through_status_when_streaming_runner()
    {
        // FakeStreamingRunner pushes synthetic tqdm lines through onStderrLine
        // as the prewarm "subprocess" runs. The prewarm path must forward each
        // parsed status to the IProgress so the dialog's progress bar advances.
        var runner = new FakeStreamingRunner
        {
            ExitCode = 0,
            StderrLines =
            {
                "Fetching 26 files:   0%|          | 0/26 [00:00<?, ?it/s]",
                "Fetching 26 files:  23%|##3       | 6/26 [00:04<00:15,  1.32it/s]",
                "Fetching 26 files: 100%|##########| 26/26 [00:09<00:00,  2.62it/s]",
            },
        };
        var progress = new StatusCapture();
        var tts = new SuperTonicTts("python", runner);

        var ok = await tts.PrewarmModelAsync(progress);

        Assert.True(ok);
        // We should have at least one mid-progress status with the 23% reading
        // (initial banner + 3 tqdm + terminal = 5 statuses).
        Assert.Contains(progress.Statuses, s =>
            s.PercentComplete == 23 &&
            !s.IsTerminal &&
            s.Detail.Contains("6 / 26"));
        // And a 100% terminal success at the end.
        Assert.True(progress.Statuses.Last().IsSuccess);
        Assert.Equal(100, progress.Statuses.Last().PercentComplete);
    }

    private sealed class StatusCapture : IProgress<ModelDownloadStatus>
    {
        public List<ModelDownloadStatus> Statuses { get; } = new();
        public void Report(ModelDownloadStatus value) => Statuses.Add(value);
    }

    private sealed class FakeStreamingRunner : IProcessStreamingRunner
    {
        public int ExitCode { get; set; }
        public List<string> StderrLines { get; } = new();
        public string StdOut { get; set; } = "";

        public Task<ProcessRunResult> RunAsync(string fileName, IReadOnlyList<string> args,
            string? stdin, string? workingDir, CancellationToken ct)
            => throw new InvalidOperationException("Streaming path expected — RunAsync should not be called.");

        public Task<ProcessRunResult> RunStreamingAsync(string fileName, IReadOnlyList<string> args,
            Action<string>? onStderrLine, Action<string>? onStdoutLine, CancellationToken ct)
        {
            foreach (var line in StderrLines) onStderrLine?.Invoke(line);
            return Task.FromResult(new ProcessRunResult(ExitCode, StdOut, string.Join('\n', StderrLines)));
        }
    }

    [Fact]
    public async Task SynthesizeAsync_error_includes_permission_error_hint()
    {
        var runner = new FakeProcessRunner
        {
            ExitCode = 1,
            StdErr = "Traceback ...\nPermissionError: [WinError 5] Access is denied",
        };
        var tts = new SuperTonicTts("python", runner);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => tts.SynthesizeAsync("hi", "F1"));
        Assert.Contains("cache lock detected", ex.Message);
        Assert.Contains("Download Model", ex.Message);
    }

    [Fact]
    public async Task SynthesizeAsync_error_includes_module_missing_hint()
    {
        var runner = new FakeProcessRunner
        {
            ExitCode = 1,
            StdErr = "ModuleNotFoundError: No module named 'supertonic'",
        };
        var tts = new SuperTonicTts("python", runner);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => tts.SynthesizeAsync("hi", "F1"));
        Assert.Contains("supertonic package missing", ex.Message);
        Assert.Contains("Check Install", ex.Message);
    }

    private sealed class FakeProcessRunner : IProcessRunner
    {
        public int ExitCode { get; set; }
        public string StdOut { get; set; } = "";
        public string StdErr { get; set; } = "";
        public Exception? ThrowOnRun { get; set; }
        public Action<string, IReadOnlyList<string>, string?, string?>? OnRun { get; set; }
        public Dictionary<string, ProcessRunResult> Responses { get; } = new();

        public string? LastFileName { get; private set; }
        public IReadOnlyList<string>? LastArgs { get; private set; }

        /// <summary>Script phase 1 success — returns the given sys.executable + version on stdout.</summary>
        public void ProbeReturnsExe(string executable, string version)
        {
            Responses[$"-c {SuperTonicTtsProbeScript}"] =
                new ProcessRunResult(0, $"{executable}\n{version}\n", "");
        }

        /// <summary>Script phase 1 failure — non-zero exit with empty output (Store stub style).</summary>
        public void ProbeFails(int exitCode)
        {
            Responses[$"-c {SuperTonicTtsProbeScript}"] = new ProcessRunResult(exitCode, "", "");
        }

        public Task<ProcessRunResult> RunAsync(
            string fileName,
            IReadOnlyList<string> args,
            string? stdin,
            string? workingDir,
            CancellationToken ct)
        {
            LastFileName = fileName;
            LastArgs = args;
            if (ThrowOnRun is not null) throw ThrowOnRun;
            OnRun?.Invoke(fileName, args, stdin, workingDir);

            var key = string.Join(' ', args);
            if (Responses.TryGetValue(key, out var scripted))
                return Task.FromResult(scripted);

            return Task.FromResult(new ProcessRunResult(ExitCode, StdOut, StdErr));
        }
    }
}
