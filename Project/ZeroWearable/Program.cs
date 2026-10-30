using Akka.Actor;
using Akka.Configuration;
using Agent.Common.Actors;
using Agent.Common.Os;
using Agent.Common.Llm.Tools;
using Agent.Common.Mcp;
using Agent.Common.Voice;
using Agent.Common.Voice.Streaming;
using Agent.Common.Voice.Streams;
using Akka.Streams;
using Akka.Streams.Dsl;
using Agent.Common.Wearable;
using Agent.Common.Wearable.Actors;
using Agent.Common.Web;
using ZeroWearable.Actors;
using ZeroWearable.Agent;
using ZeroWearable.Ble;
using ZeroWearable.Hud;
using ZeroWearable.Voice;

namespace ZeroWearable;

/// <summary>
/// AgentZero Lite's wearable host — one process for all three watch apps.
///
/// The device has a single BLE link, so a single process owns it. AskBot rides it as an
/// Akka remoting peer (its PDUs tunnelled through <see cref="BleTunnel"/>), the Chat app
/// rides it as line protocol that <see cref="BleChatProxy"/> turns into messages for the
/// same <see cref="ChatActor"/>, and the Claude HUD app rides it as the S/E lines
/// <see cref="HudEndpoint"/> receives on :8765. All three share one conversation engine,
/// one voice and one ear.
///
/// <para><b>Why a second process.</b> The BLE central is WinRT
/// (<c>Windows.Devices.Bluetooth</c>), which needs a Windows-SDK target framework;
/// AgentZeroLite.exe stays on plain <c>net10.0-windows</c> so its output path — quoted in
/// CLAUDE.md, the agentzero-cli skill and the installer — does not move. The GUI starts and
/// stops this exe from the Wearable panel and streams the lines below into it.</para>
///
/// <para><b>Nothing is configured twice.</b> The voice, the ear and the brain come from
/// AgentZero's own stores: Settings → Voice picks the Supertonic voice and the Whisper
/// model, Settings → LLM picks Local or External and the model id. This process reads the
/// same JSON files the GUI writes and loads the same bundles from
/// <c>%LOCALAPPDATA%\AgentZeroLite\models\</c>. It never downloads anything.</para>
/// </summary>
public static class Program
{
    /// <summary>
    /// Two hosts would fight over the radio (one central can hold the device) and over the
    /// remoting port. The GUI panel also uses this to notice a host someone started by hand.
    /// </summary>
    private const string SingleInstanceMutex = @"Local\AgentZeroLite.WearableHost";

    public static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        // Before anything can fail: from here on stdout is also on disk. The panel still
        // receives the identical bytes - see HostLog for why that matters.
        var logPath = HostLog.Install(Arg(args, "--log"));

        // Before any store is loaded: the GUI seals API keys with DPAPI, and without a
        // protector a sealed key reads as "no key" — the provider is then called bare.
        global::Agent.Common.Security.SecretProtection.Protector = new Security.DpapiSettingsProtector();

        var settingsPath = Arg(args, "--config") ?? WearableSettingsStore.DefaultFilePath;
        var settings = WearableSettingsStore.Load(settingsPath);

        // Command-line overrides, applied after the file: they exist so the host can be
        // exercised from a shell without editing the settings the GUI owns.
        if (Arg(args, "--device") is { } device) settings.DeviceName = device;
        if (HasFlag(args, "--no-ble")) settings.DisableBle = true;
        if (HasFlag(args, "--no-hud")) settings.HudEnabled = false;
        if (int.TryParse(Arg(args, "--port"), out var port)) settings.RemotingPort = port;
        if (Arg(args, "--bind") is { } bind) settings.BindAddress = bind;
        if (int.TryParse(Arg(args, "--hud-port"), out var hudPort)) settings.HudPort = hudPort;
        if (Arg(args, "--announce") is { } announce) settings.AnnounceOnConnect = announce;
        if (int.TryParse(Arg(args, "--talk"), out var talkMs)) settings.TalkOnConnectMs = talkMs;
        if (Arg(args, "--brain") is { } brainName) settings.Brain = brainName;
        if (Arg(args, "--provider") is { } provider) settings.CliProvider = provider;

        var voiceSettings = VoiceSettingsStore.Load();
        // For this run only (never saved): try another engine without touching Settings → Voice.
        if (Arg(args, "--tts") is { } ttsProvider) voiceSettings.TtsProvider = ttsProvider;
        if (Arg(args, "--kitten2-dir") is { } kitten2Dir) voiceSettings.Kitten2ModelDir = kitten2Dir;
        if (Arg(args, "--kitten2-device") is { } kitten2Device) voiceSettings.Kitten2Device = Kitten2Devices.Normalize(kitten2Device);
        using var voice = new WearableVoice(voiceSettings, (level, message) => Log("voice", level, message));
        using var stt = new WearableStt(voiceSettings, settings, (level, message) => Log("stt", level, message));

        // ── One-shot diagnostics. Each exits; none of them touch the radio. ──

        if (Arg(args, "--install-kitten2") is { } kitten2Source) return InstallKitten2(kitten2Source);
        if (HasFlag(args, "--download-kitten2")) return InstallKitten2(null);
        if (Arg(args, "--speak") is { } speakText) return Speak(voice, speakText, args);
        if (Arg(args, "--hear") is { } hearPath) return Hear(stt, hearPath, args);
        if (HasFlag(args, "--download-stream-stt")) return DownloadStreamingStt(args);
        if (Arg(args, "--hear-stream") is { } streamPath) return HearStream(voiceSettings, streamPath, args);

        // Two shapes of brain: AgentZero's own agent is an actor subtree (M0032), an agent
        // CLI is a child process beside the actor system. Only one is ever non-null.
        IWearableBrain? brain = null;
        WearableAgentPlan? plan = null;
        string brainStatus;
        if (WearableBrainFactory.UsesAgentActor(settings))
            plan = WearableBrainFactory.CreateAgentPlan(settings, (level, message) => Log("brain", level, message), out brainStatus);
        else
            brain = WearableBrainFactory.CreateCliBrain(settings, (level, message) => Log("brain", level, message), out brainStatus);

        if (Arg(args, "--ask") is { } askText)
        {
            var code = Ask(brain, plan, settings, askText, Arg(args, "--session") ?? "console", Arg(args, "--lang"));
            brain?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            return code;
        }

        using var mutex = new System.Threading.Mutex(true, SingleInstanceMutex, out var owned);
        if (!owned)
        {
            Console.Error.WriteLine("[host/error] another wearable host is already running " +
                                    "(it owns the BLE link)");
            return 5;
        }

        // Classic remoting (akka.tcp://) on purpose: the 1.6 branch also carries Artery
        // (akka://, Pekko-style) but it ships disabled and its own config states the two are
        // not wire-compatible. The device's C++ client speaks classic.
        var advertise = settings.BindAddress == "0.0.0.0" ? "127.0.0.1" : settings.BindAddress;
        var hocon = ConfigurationFactory.ParseString($@"
            akka {{
              loglevel = INFO
              actor.provider = remote
              remote {{
                dot-netty.tcp {{
                  hostname = ""{settings.BindAddress}""
                  public-hostname = ""{advertise}""
                  port = {settings.RemotingPort}
                }}
              }}
            }}");

        using var system = ActorSystem.Create(settings.SystemName, hocon);

        var ask = system.ActorOf(Props.Create(() => new AskActor()), "ask");
        var agent = plan is null ? null : SpawnAgent(system, plan, settings);
        var agentName = plan?.Name;
        var chat = system.ActorOf(Props.Create(() => new ChatActor(settings, voice, stt, brain, agent, agentName)), "chat");
        stt.Preload();
        // The host runs only while the watch is in use, so warm the voice too - on the pool, like the ear: the
        // radio comes up at once and the first spoken answer does not pay the model load (KittenTTS 2: ~13 s).
        if (voice.Available)
            _ = Task.Run(() =>
            {
                try { voice.Engine!.Warm(); }
                catch (Exception ex) { Log("voice", "warn", $"preload failed: {ex.Message}"); }
            });

        Log("host", "info", $"--- run started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ---");
        if (logPath != null) Log("host", "info", $"log: {logPath}");
        Log("host", "info", $"up as akka.tcp://{settings.SystemName}@{advertise}:{settings.RemotingPort}");
        Log("host", "info", "  /user/ask    echo actor (protocol smoke test)");
        Log("host", "info", "  /user/chat   conversation actor, shared by AskBot, Chat and the proxy");
        if (agent is not null)
            Log("host", "info", "  /user/agent  AgentZero's agent (sessions + /files + /web tool actors)");
        Log("brain", "info", brain is null && plan is null ? $"off — {brainStatus}" : brainStatus);
        LogTools(settings);
        Log("voice", "info", $"out: {voice.Status}");
        if (voice.Available) Log("voice", "info", $"     voices: {string.Join(" ", voice.AvailableVoices)}");
        Log("stt", "info", $"in:  {stt.Status}");
        if (!string.IsNullOrWhiteSpace(settings.AnnounceOnConnect))
            Log("host", "info", $"announce on connect: {settings.AnnounceOnConnect}");

        BleLink? link = null;
        BleTunnel? tunnel = null;
        HudEndpoint? hud = null;
        using var stopping = new CancellationTokenSource();

        if (!settings.DisableBle)
        {
            link = new BleLink((level, message) => Log("ble", level, message));
            tunnel = new BleTunnel(link, advertise, settings.RemotingPort,
                (level, message) => Log("tunnel", level, message));

            var proxy = system.ActorOf(Props.Create(() => new BleChatProxy(link, chat)), "ble-chat");
            link.LineReceived += line => proxy.Tell(new BleChatProxy.Line(line));
            // 0xA5 microphone frames belong to the Chat app; 0xAB tunnel chunks are the
            // BleTunnel's and are already claimed there.
            link.FrameReceived += frame =>
            {
                if (frame.Length > 0 && frame[0] == BleTags.MicFrame) proxy.Tell(new BleChatProxy.MicFrame(frame));
            };
            link.Connected += () => proxy.Tell(new BleChatProxy.Greet());
            // The device that comes back after a reboot starts its request ids at 1 again and
            // has forgotten whatever it was recording. Carrying the old entry forward would
            // leave a capture buffer and a cancelled request keyed to a board that no longer
            // exists, so the link dropping is the signal to forget it.
            link.Disconnected += () => proxy.Tell(new BleChatProxy.Reset());

            if (settings.TalkOnConnectMs > 0)
            {
                link.Connected += () => _ = Task.Run(async () =>
                {
                    await Task.Delay(2500, stopping.Token);   // let the greeting settle first
                    proxy.Tell(new BleChatProxy.Talk(settings.TalkOnConnectMs));
                }, stopping.Token);
            }

            _ = Task.Run(() => KeepLinkUpAsync(link, settings.DeviceName, stopping.Token), stopping.Token);
            Log("ble", "info", $"keeping a link to '{settings.DeviceName}' " +
                               "(AskBot tunnel + Chat protocol + Claude HUD)");
        }
        else
        {
            Log("ble", "info", "disabled — only network peers can reach this host");
        }

        // The Claude HUD channel. Started outside the BLE branch on purpose: the hooks in
        // ~/.claude/hud_amoled post here regardless, and an endpoint that silently never
        // opened is indistinguishable from one that is working but has nothing to show. With
        // no link the actor reports each line as a drop, which is the answer to "are my hooks
        // even reaching this?".
        if (settings.HudEnabled)
        {
            var hudActor = system.ActorOf(Props.Create(() => new HudActor(link)), "hud");
            // The Chat app gets a greeting when the link comes up; the HUD used to get nothing,
            // so a watch that reconnected mid-session showed "waiting for sessions..." until
            // the next statusLine render - which Claude Code does on interaction, not on a
            // timer. Tell it, and it repaints from what we already know.
            if (link != null) link.Connected += () => hudActor.Tell(new HudActor.LinkUp());
            hud = new HudEndpoint(hudActor, settings.HudPort, (level, message) => Log("hud", level, message));
            if (!hud.Start())
            {
                hud.Dispose();
                hud = null;
            }
            else if (link is null)
            {
                Log("hud", "warn", "BLE is off, so HUD lines are counted and dropped rather than shown");
            }
        }
        else
        {
            Log("hud", "info", $"disabled — nothing is listening on :{settings.HudPort}");
        }

        // The line the GUI panel waits for before it calls the host started.
        Log("host", "ready", "wearable host running");

        if (Console.IsInputRedirected)
        {
            // How the GUI starts it: no console to read from, so stay up until terminated.
            // Ctrl+Break / a kill from the panel unwinds through the finally below.
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                stopping.Cancel();
                system.Terminate();
            };
            system.WhenTerminated.GetAwaiter().GetResult();
        }
        else
        {
            Console.WriteLine("Commands: <text> asks the echo actor, '? <text>' asks the brain, " +
                              "'ble' prints link stats, empty line quits.");
            while (true)
            {
                Console.Write("host> ");
                var line = Console.ReadLine();
                if (string.IsNullOrEmpty(line)) break;

                if (line.Trim() == "ble")
                {
                    Console.WriteLine(link is null
                        ? "  BLE disabled"
                        : $"  connected={link.IsConnected} {link.DeviceName} mtu={link.MaxPdu} " +
                          $"sent={link.Sent} dropped={link.Dropped} lines={link.RxLines} frames={link.RxFrames} " +
                          $"tunnel={(tunnel?.Open == true ? "open" : "closed")} " +
                          $"toAkka={tunnel?.ToAkka ?? 0} toDevice={tunnel?.ToDevice ?? 0}");
                    continue;
                }

                // The same brain the watch talks to, from the keyboard: the fastest way to see
                // what it answered without holding the watch.
                if (line.StartsWith("? "))
                {
                    if (brain is null && agent is null)
                    {
                        Console.WriteLine($"  brain is off — {brainStatus}");
                        continue;
                    }
                    try
                    {
                        var answer = agent is not null
                            ? AskAgent(system, agent, line[2..], "console", null)
                            : brain!.AskAsync(line[2..], "console", null, CancellationToken.None)
                                .GetAwaiter().GetResult();
                        Console.WriteLine($"  <- {answer}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"  brain failed: {ex.Message}");
                    }
                    continue;
                }

                var reply = ask.Ask<string>(line, TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                Console.WriteLine($"  <- {reply}");
            }
        }

        stopping.Cancel();
        hud?.Dispose();
        if (tunnel != null) tunnel.DisposeAsync().GetAwaiter().GetResult();
        link?.Dispose();
        brain?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        system.Terminate().GetAwaiter().GetResult();
        return 0;
    }

    // ── One-shot diagnostics ────────────────────────────────────────────────

    /// <summary>
    /// What Settings → Voice → KittenTTS 2 → Download / Install does, without a GUI. <c>--download-kitten2</c> fetches
    /// the bundle from AgentZero's GitHub Release (<paramref name="source"/> null); <c>--install-kitten2 &lt;folder&gt;</c>
    /// copies the bundle a folder holds (a synced cloud folder, a copy). Either way every file's SHA-256 is checked
    /// against the manifest.
    /// </summary>
    private static int InstallKitten2(string? source)
    {
        var last = "";
        var progress = new SyncProgress<ModelDownloadStatus>(p =>
        {
            var line = p.IsTerminal ? $"{p.Caption}: {p.Detail}" : $"{p.Caption} {p.PercentComplete}%";
            if (line == last) return;
            last = line;
            Console.WriteLine(line);
        });
        var ok = (source is null
            ? global::Agent.Common.Voice.KittenTts2.KittenTts2ModelDownloader.DownloadAsync(progress, CancellationToken.None)
            : global::Agent.Common.Voice.KittenTts2.KittenTts2ModelInstaller.InstallFromFolderAsync(source, progress,
                CancellationToken.None)).GetAwaiter().GetResult();
        return ok ? 0 : 4;
    }

    /// <summary>Reports on the caller's thread: a console run has no context to post to.</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    /// <summary>
    /// <c>AgentZeroWearable.exe --speak "안녕하세요" --out hello.wav</c> — synthesize with the
    /// app's configured voice and write what the watch would play (16 kHz, post-resample),
    /// so a wrong voice or a silent speaker can be told apart without the device.
    /// </summary>
    private static int Speak(WearableVoice voice, string text, string[] args)
    {
        if (!voice.Available)
        {
            Console.Error.WriteLine($"[voice/error] unavailable: {voice.Status}");
            return 3;
        }
        if (HasFlag(args, "--pc"))
        {
            // The watch's "PC" answer mode, without a watch: full rate, this machine's speakers.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var (wav, length) = voice.SynthesizeWav(text, Arg(args, "--voice"), Arg(args, "--lang"));
            Console.WriteLine($"synthesized {length.TotalSeconds:0.0} s in {sw.ElapsedMilliseconds} ms - playing on the PC");
            new PcSpeaker().PlayAsync(wav, length, CancellationToken.None).GetAwaiter().GetResult();
            return 0;
        }
        var outPath = Arg(args, "--out") ?? "speak.wav";
        // --repeat N: the same text N times in one process, so the first (model load + synthesis) and the
        // following (synthesis only) can be told apart.
        var repeat = int.TryParse(Arg(args, "--repeat"), out var r) ? Math.Max(1, r) : 1;
        Speech speech = null!;
        for (var i = 1; i <= repeat; i++)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            speech = voice.Synthesize(text, 0, Arg(args, "--voice"), Arg(args, "--lang"));
            if (repeat > 1)
                Console.WriteLine($"run {i}: {timer.ElapsedMilliseconds} ms for {speech.DurationMs} ms of audio" +
                                  $" (RTF {timer.ElapsedMilliseconds / (double)speech.DurationMs:0.00})");
        }
        File.WriteAllBytes(outPath, DeviceAudio.ToWav(speech.Pcm16));
        Console.WriteLine($"wrote {outPath}: {speech.DurationMs} ms, {speech.Frames.Count} ADPCM frames " +
                          $"at {speech.Rate} Hz");
        return 0;
    }

    /// <summary>
    /// The other half of the speech check: transcribe a WAV. Pairing it with
    /// <c>--speak</c> says whether what the watch plays is actually intelligible, instead of
    /// guessing from a duration.
    /// </summary>
    private static int Hear(WearableStt stt, string path, string[] args)
    {
        if (!stt.Available)
        {
            Console.Error.WriteLine($"[stt/error] unavailable: {stt.Status}");
            return 3;
        }
        var wav = File.ReadAllBytes(path);
        // Skip the 44-byte canonical header; these are our own files.
        var pcm = wav.Length > 44 ? wav[44..] : wav;
        var heard = stt.TranscribeAsync(pcm, Arg(args, "--lang")).GetAwaiter().GetResult();
        Console.WriteLine($"heard: {(heard.Length == 0 ? "(nothing)" : heard)}");
        return 0;
    }

    /// <summary>
    /// <c>AgentZeroWearable.exe --download-stream-stt [--stream-model zipformer-ko]</c> — install a streaming
    /// model (default: the settings' choice) from k2-fsa's release.
    /// </summary>
    private static int DownloadStreamingStt(string[] args)
    {
        var model = StreamingSttModelCatalog.Resolve(Arg(args, "--stream-model") ?? VoiceSettingsStore.Load().StreamingSttModel);
        var last = "";
        var lastPct = -10;
        var progress = new SyncProgress<ModelDownloadStatus>(p =>
        {
            // A 650 MB download reports every 200 ms; print every 10 %.
            if (!p.IsTerminal && p.PercentComplete is int pct && pct < lastPct + 10) return;
            if (p.PercentComplete is int pp) lastPct = pp;
            var line = p.IsTerminal ? $"{p.Caption}: {p.Detail}" : p.PercentComplete is null ? $"{p.Caption} {p.Detail}" : $"{p.Caption} {p.PercentComplete}% {p.Detail}";
            if (line == last) return;
            last = line;
            Console.WriteLine(line);
        });
        return StreamingSttModelDownloader.DownloadAsync(model, progress, CancellationToken.None).GetAwaiter().GetResult() ? 0 : 4;
    }

    /// <summary>
    /// <c>AgentZeroWearable.exe --hear-stream hello.wav [--lang ko] [--stream-model id] [--realtime]</c> — run a WAV
    /// through the same Akka.Streams stage the voice actor uses (<see cref="StreamingSttFlow"/>), in 50 ms frames
    /// like the microphone's, and print every partial and final with the audio position it came at. 1.5 s of
    /// silence follows the file so the endpoint can fire as it would after a real sentence. <c>--realtime</c>
    /// paces the frames at 50 ms, so the wall clock reads as a live mic would.
    /// </summary>
    private static int HearStream(VoiceSettings voiceSettings, string path, string[] args)
    {
        var model = StreamingSttModelCatalog.Resolve(Arg(args, "--stream-model") ?? voiceSettings.StreamingSttModel);
        var options = new StreamingSttOptions(EndpointSilenceSeconds: voiceSettings.ResolveStreamingEndpointMs() / 1000f);
        using var engine = new SherpaStreamingStt(model, options);
        var load = System.Diagnostics.Stopwatch.StartNew();
        if (!engine.EnsureReadyAsync(new SyncProgress<string>(m => Console.WriteLine($"[stt] {m}"))).GetAwaiter().GetResult())
            return 3;
        Console.WriteLine($"[stt] model={model.Id} load={load.Elapsed.TotalSeconds:F1}s endpoint={options.EndpointSilenceSeconds:F1}s");

        const int frameBytes = 1600; // 50 ms of 16 kHz PCM16, what WindowsAudioCapture delivers
        var pcm = WavPcm.To16kMono(File.ReadAllBytes(path));
        var padded = new byte[pcm.Length + 16000 * 2 * 3 / 2];
        pcm.CopyTo(padded, 0);
        var frames = Enumerable.Range(0, (padded.Length + frameBytes - 1) / frameBytes)
            .Select(i => new global::Agent.Common.Voice.Streams.MicFrame(
                padded.AsSpan(i * frameBytes, Math.Min(frameBytes, padded.Length - i * frameBytes)).ToArray(), 0f))
            .ToList();
        Console.WriteLine($"[stt] audio={pcm.Length / 32000.0:F2}s (+1.5s silence) frames={frames.Count}");

        var fed = 0;
        var wall = System.Diagnostics.Stopwatch.StartNew();
        var language = Arg(args, "--lang") ?? voiceSettings.SttLanguage;
        using var system = ActorSystem.Create("HearStream");
        var source = Akka.Streams.Dsl.Source.From(frames);
        if (HasFlag(args, "--realtime"))
            source = source.Throttle(1, TimeSpan.FromMilliseconds(50), 1, Akka.Streams.ThrottleMode.Shaping);
        var finals = new List<string>();
        source
            .Select(f => { Interlocked.Increment(ref fed); return f; })
            .Via(StreamingSttFlow.Create(() => engine.StartSession(language)))
            .RunForeach(u =>
            {
                var at = $"[audio {Volatile.Read(ref fed) * frameBytes / 32000.0:F2}s | wall {wall.Elapsed.TotalSeconds:F2}s]";
                Console.WriteLine(u.IsFinal ? $"{at} FINAL #{u.Segment}: {u.Text}" : $"{at} partial: {u.Text}");
                if (u.IsFinal) finals.Add(u.Text);
            }, system.Materializer())
            .GetAwaiter().GetResult();
        system.Terminate().GetAwaiter().GetResult();
        var rtf = wall.Elapsed.TotalSeconds / (padded.Length / 32000.0);
        Console.WriteLine($"heard: {(finals.Count == 0 ? "(nothing)" : string.Join(" ", finals))}");
        Console.WriteLine($"[stt] wall={wall.Elapsed.TotalSeconds:F2}s rtf={rtf:F2}");
        return 0;
    }

    /// <summary>
    /// <c>AgentZeroWearable.exe --ask "..."</c> — one question through the configured brain,
    /// no watch and no radio needed. What the Wearable panel's "Test brain" button runs.
    /// The agent brains go through the same actor subtree the watch uses (a local, non-remoting
    /// ActorSystem stands in for the host's), so this smoke test exercises the real path.
    /// </summary>
    private static int Ask(IWearableBrain? brain, WearableAgentPlan? plan, WearableSettings settings,
        string text, string session, string? language)
    {
        if (brain is null && plan is null)
        {
            Console.Error.WriteLine("[brain/error] no brain configured — see Settings → LLM");
            return 3;
        }
        Console.WriteLine($"brain: {brain?.Status ?? plan!.Status}");
        try
        {
            string answer;
            if (plan is not null)
            {
                using var system = ActorSystem.Create("AskBotConsole");
                var agent = SpawnAgent(system, plan, settings);
                LogTools(settings);
                answer = AskAgent(system, agent, text, session, language);
                system.Terminate().GetAwaiter().GetResult();
            }
            else
            {
                answer = brain!.AskAsync(text, session, language, CancellationToken.None)
                    .GetAwaiter().GetResult();
            }
            Console.WriteLine($"<- {answer}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[brain/error] {ex.Message}");
            return 4;
        }
    }

    /// <summary>
    /// The agent subtree: <c>/user/agent</c> with <c>/files</c> (allow-listed folders,
    /// open_file) and <c>/web</c> (the GUI's Browser page through <c>-cli web</c>, else a
    /// headless fetch) underneath, and one AgentLoopActor per conversation created on demand.
    /// </summary>
    private static IActorRef SpawnAgent(ActorSystem system, WearableAgentPlan plan, WearableSettings settings)
    {
        // home (the built-in, writable work folder) first, then the folders the person chose.
        var roots = new AllowedRootResolver(settings.RootsWithHome());
        Func<bool> stopKey = MediaKeys.SendStop;   // user32 stays host-side; the actor only knows a delegate
        var filesProps = Props.Create(() => new FileToolActor(roots, null, stopKey, null));

        var hostDir = AppContext.BaseDirectory;
        var guiExePath = settings.GuiExePath;
        var gui = new GuiCliWebToolSurface(
            () => GuiCliWebToolSurface.ResolveGuiExe(guiExePath, hostDir),
            (level, message) => Log("web", level, message));
        var headless = new HeadlessWebToolSurface();
        var webEnabled = settings.WebToolsEnabled;
        var webMaxChars = settings.WebMaxChars;
        var webProps = Props.Create(() => new WebToolActor(gui, headless, webEnabled, webMaxChars));

        // The desktop: windows, screenshots, mouse, keyboard, starting programs. The actor
        // holds the rules (off switch, program names only, files inside the roots, audit);
        // IOsControl is the platform (Windows today).
        var os = OsControl.Create();
        var osEnabled = settings.OsControlEnabled;
        var osProps = Props.Create(() => new OsToolActor(os, roots, osEnabled, null));

        var bindings = plan.Bindings;
        var owned = plan.Owned;
        return system.ActorOf(Props.Create(() => new WearableAgentActor(bindings, filesProps, webProps, owned, osProps)), "agent");
    }

    /// <summary>
    /// One question, synchronously, through the agent actor — for the console and
    /// <c>--ask</c>. Progress messages are printed as they arrive so a slow tool call is
    /// visible instead of a silent wait.
    /// </summary>
    private static string AskAgent(ActorSystem system, IActorRef agent, string text, string session, string? language)
    {
        var inbox = Inbox.Create(system);
        agent.Tell(new WearableAgentActor.Ask(session, 1, text, language), inbox.Receiver);
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(5);
        while (DateTime.UtcNow < deadline)
        {
            var message = inbox.Receive(deadline - DateTime.UtcNow);
            switch (message)
            {
                case WearableAgentActor.Progress p when p.Phase == AgentLoopPhase.Acting:
                    Console.WriteLine($"   .. tool {p.Text} (round {p.Round})");
                    break;
                case WearableAgentActor.Answer a:
                    if (a.Success) return a.Text.Trim().Length > 0 ? a.Text.Trim() : "I have no answer for that.";
                    throw new InvalidOperationException(a.FailureReason ?? a.Text);
            }
        }
        throw new TimeoutException("the agent did not answer within 5 minutes");
    }

    /// <summary>What the watch's agent may reach on this PC, said once at startup.</summary>
    private static void LogTools(WearableSettings settings)
    {
        var roots = new AllowedRootResolver(settings.RootsWithHome());
        Log("tools", "info", "files: " + string.Join(", ", roots.Roots.Select(r => r.Alias + (r.Writable ? " (rw)" : " (ro)")))
            + $" — home is {WearableSettings.HomeDirectory}");
        Log("tools", "info", settings.OsControlEnabled
            ? $"os: on ({OsControl.Create().Platform}) — windows, screenshot, click, keys, type, launch; audited"
            : "os: off");
        var gui = GuiCliWebToolSurface.ResolveGuiExe(settings.GuiExePath, AppContext.BaseDirectory);
        Log("tools", "info", !settings.WebToolsEnabled
            ? "web: off"
            : gui is null
                ? "web: headless only (AgentZeroLite.exe not found for the Browser page)"
                : $"web: GUI Browser page when running, else headless ({gui})");
        if (!settings.McpToolsEnabled)
        {
            Log("tools", "info", "mcp: off");
            return;
        }
        var mcpTools = WearableBrainFactory.McpToolSpecs(settings);
        var servers = mcpTools.Select(t => t.Name.Split('.')[0]).Distinct().ToList();
        Log("tools", "info", mcpTools.Count == 0
            ? $"mcp: no allowed tools in {McpSettingsStore.FilePath} (Settings → MCP → Load tools)"
            : $"mcp: {mcpTools.Count} tools from {string.Join(", ", servers)}");
    }

    /// <summary>
    /// Connects to the watch and reconnects after drops. The device advertises only while
    /// nothing is connected, so a failed attempt must let go of the handles (BleLink does) or
    /// every later scan comes back empty.
    /// </summary>
    private static async Task KeepLinkUpAsync(BleLink link, string deviceName, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!link.IsConnected)
                {
                    var hit = await link.FindByNameAsync(deviceName, 6, ct);
                    if (hit != null)
                    {
                        await link.ConnectAsync(hit.Address, hit.Name, hit.AddressType, ct);
                    }
                    else if (link.Address != 0)
                    {
                        // Absent from a scan is not the same as out of range. The watch
                        // advertises only while unconnected, and after it reboots Windows can
                        // still be holding - or re-establishing - the old link, so a device
                        // sitting on the desk shows up in no scan at all. We connected to it
                        // once, so we know its address: dial it.
                        Log("ble", "info", $"'{deviceName}' is not advertising; " +
                                           $"trying {link.AddressHex} directly");
                        await link.ConnectAsync(link.Address, deviceName, link.AddressKind, ct);
                    }
                    else
                    {
                        Log("ble", "info", $"no device named '{deviceName}' in range");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log("ble", "warn", $"connect loop: {ex.Message}");
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(4), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// <c>[category/level] message</c> — the shape the Wearable panel parses to colour a line
    /// and to notice <c>[host/ready]</c>. Flushed per line because the GUI reads this pipe.
    /// </summary>
    private static void Log(string category, string level, string message)
    {
        Console.WriteLine($"[{category}/{level}] {message}");
        Console.Out.Flush();
    }

    private static string? Arg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static bool HasFlag(string[] args, string name) => Array.IndexOf(args, name) >= 0;
}
