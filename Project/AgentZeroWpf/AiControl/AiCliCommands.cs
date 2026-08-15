using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Agent.Common.Voice;
using AgentZeroWpf.Services.Voice;
using NAudio.Wave;

namespace AgentZeroWpf.AiControl;

/// <summary>
/// CLI dispatcher for the <c>ai</c> command group — exposes the voice features
/// the user already configured in the AgentZero window (TTS, STT) from any
/// terminal. Verbs are lowercase kebab-case (matches the rest of CliHandler).
///
/// Design mirrors <see cref="OsControl.OsCliCommands"/>: everything runs
/// <b>in-process</b> in the CLI invocation, no GUI / WM_COPYDATA round-trip.
/// Providers, voices and devices are read from the same JSON side-car file the
/// GUI writes (<c>%LOCALAPPDATA%\AgentZeroLite\voice-settings.json</c>) via
/// <see cref="VoiceSettingsStore"/>. So configuration is done once in the GUI
/// (Settings → Voice) and every CLI call just drives it.
///
/// Voice I/O is deliberately flexible per the feature request:
///   • TTS output — a WAV file (<c>--out</c>) OR the speaker sound-stream
///     (default / <c>--speaker</c>, with <c>--device</c> to pick the endpoint).
///   • STT input  — a WAV file (<c>--in</c>) OR the microphone input source
///     (<c>--mic</c>, with <c>--device</c> / <c>--seconds</c>).
///
/// (An on-device LLM <c>ask</c> verb was prototyped here but withdrawn: loading
/// a multi-GB GGUF per CLI invocation is too slow for a short-lived command.)
/// </summary>
internal static class AiCliCommands
{
    public static int Dispatch(string[] args)
    {
        // -cli runs ON the WPF Dispatcher thread but never pumps its message
        // loop. Libraries that capture SynchronizationContext.Current — NAudio's
        // PlaybackStopped event, awaited continuations resumed via .GetResult() —
        // would post to that dead context and hang forever (audio plays but the
        // completion callback never fires; STT continuations never resume).
        // Detach it so events/continuations run inline on the calling thread.
        SynchronizationContext.SetSynchronizationContext(null);

        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        var verb = args[0].ToLowerInvariant();
        var rest = args.AsSpan(1).ToArray();

        return verb switch
        {
            "help" or "--help" or "-h" => PrintUsageOk(),
            "voices"                    => Voices(rest),
            "devices"                   => Devices(rest),
            "tts"                       => Tts(rest),
            "stt"                       => Stt(rest),
            _ => UnknownVerb(verb),
        };
    }

    // ============================================================ TTS: voices

    private static int Voices(string[] _)
    {
        var v = VoiceSettingsStore.Load();
        var tts = VoiceRuntimeFactory.BuildTts(v);
        if (tts is null)
        {
            Console.Error.WriteLine($"TTS provider is '{v.TtsProvider}' (no synthesizer). " +
                                    "Set one in the AgentZero window: Settings → Voice.");
            return 1;
        }

        Console.WriteLine($"TTS provider : {tts.ProviderName}   (audio format: {tts.AudioFormat})");
        try
        {
            var voices = tts.GetAvailableVoicesAsync().GetAwaiter().GetResult();
            var current = ResolveDefaultVoice(v);
            var isSupertonic = v.TtsProvider == TtsProviderNames.Supertonic;
            Console.WriteLine($"Available voices ({voices.Count}):");
            foreach (var voice in voices)
                Console.WriteLine($"  {(voice == current ? "*" : " ")} {voice}{VoiceHint(voice, isSupertonic)}");
            Console.WriteLine();
            if (!string.IsNullOrEmpty(current))
                Console.WriteLine($"  '*' = current voice ({current}).");
            Console.WriteLine("  Pick one per call:   ai tts \"안녕하세요\" --voice F3 --speaker");
            if (isSupertonic)
                Console.WriteLine("  Supertonic: M1–M5 = male, F1–F5 = female. Change the saved default in Settings → Voice.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    /// <summary>Human hint next to a voice id — gender for Supertonic's M#/F# ids, nothing otherwise.</summary>
    private static string VoiceHint(string voice, bool isSupertonic)
    {
        if (!isSupertonic || voice.Length < 2) return "";
        return char.ToUpperInvariant(voice[0]) switch
        {
            'M' when char.IsDigit(voice[1]) => "   (male)",
            'F' when char.IsDigit(voice[1]) => "   (female)",
            _ => "",
        };
    }

    // =============================================================== TTS: tts

    private static int Tts(string[] args)
    {
        string? outPath = null;
        string? voice = null;
        int outDevice = -1;
        float? speed = null;
        var textParts = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--out" when i + 1 < args.Length:
                    outPath = args[++i]; break;
                case "--voice" when i + 1 < args.Length:
                    voice = args[++i]; break;
                case "--device" when i + 1 < args.Length && int.TryParse(args[i + 1], out var dn):
                    outDevice = dn; i++; break;
                case "--speed" when i + 1 < args.Length
                        && float.TryParse(args[i + 1], System.Globalization.NumberStyles.Float,
                                          System.Globalization.CultureInfo.InvariantCulture, out var sp):
                    speed = Math.Clamp(sp, 0.7f, 2.0f); i++; break;
                case "--speaker":
                    break; // speaker is the default when no --out; flag is accepted for clarity
                default:
                    textParts.Add(args[i]); break;
            }
        }

        var text = string.Join(' ', textParts).Trim();
        if (text.Length == 0 && Console.IsInputRedirected)
            text = Console.In.ReadToEnd().Trim();
        if (text.Length == 0)
        {
            Console.Error.WriteLine("Usage: ai tts <text> [--voice V] [--speed S] [--out FILE.wav | --speaker] [--device N]");
            return 1;
        }

        var v = VoiceSettingsStore.Load();
        // --speed overrides the configured Supertonic speed for THIS call only
        // (0.7 slow … 2.0 fast; lower = slower). Not persisted. Ignored by the
        // non-Supertonic providers, which don't take a speed knob.
        if (speed.HasValue)
            v.SupertonicSpeed = speed.Value;
        var tts = VoiceRuntimeFactory.BuildTts(v);
        if (tts is null)
        {
            Console.Error.WriteLine($"TTS provider is '{v.TtsProvider}' (no synthesizer). " +
                                    "Set one in the AgentZero window: Settings → Voice.");
            return 1;
        }

        var useVoice = voice ?? ResolveDefaultVoice(v);
        var speedNote = v.TtsProvider == TtsProviderNames.Supertonic ? $", speed={v.SupertonicSpeed:0.##}" : "";
        byte[] audio;
        try
        {
            Console.Error.WriteLine($"[ai] synthesizing via {tts.ProviderName} " +
                                    $"(voice={(string.IsNullOrEmpty(useVoice) ? "default" : useVoice)}{speedNote}) …");
            audio = tts.SynthesizeAsync(text, useVoice).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: synthesis failed: {ex.Message}");
            return 1;
        }

        if (audio.Length == 0)
        {
            Console.Error.WriteLine("Error: synthesizer returned no audio.");
            return 1;
        }

        // ── File output ──
        if (outPath is not null)
        {
            try
            {
                var full = Path.GetFullPath(outPath);
                var dir = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllBytes(full, audio);
                Console.WriteLine($"Wrote {audio.Length} bytes ({tts.AudioFormat}) → {full}");
                if (!string.Equals(tts.AudioFormat, "wav", StringComparison.OrdinalIgnoreCase))
                    Console.Error.WriteLine($"[ai] note: audio container is '{tts.AudioFormat}', not wav — name the file accordingly.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: write failed: {ex.Message}");
                return 1;
            }
        }

        // ── Speaker (sound-stream) output ──
        return PlayToSpeaker(audio, tts.AudioFormat, outDevice);
    }

    private static int PlayToSpeaker(byte[] audio, string format, int deviceNumber)
    {
        try
        {
            var effective = VoicePlaybackService.DetectFormat(audio, format);
            using var src = VoicePlaybackService.CreateSource(audio, effective);
            using var output = new WaveOutEvent { DeviceNumber = deviceNumber };
            output.Init(src);
            output.Play();
            Console.Error.WriteLine($"[ai] playing {audio.Length} bytes ({effective}) on output device {deviceNumber} …");
            // Poll PlaybackState rather than waiting on PlaybackStopped: the state
            // flips to Stopped on NAudio's own thread when the source ends, needing
            // no SynchronizationContext — robust in a CLI with no message pump.
            while (output.PlaybackState == PlaybackState.Playing)
                Thread.Sleep(100);
            Console.WriteLine("Playback complete.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: playback failed: {ex.Message}");
            return 1;
        }
    }

    // =============================================================== STT: stt

    private static int Stt(string[] args)
    {
        string? inPath = null;
        bool mic = false;
        int seconds = 5;
        string? lang = null;
        int device = -1;
        bool deviceSet = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--in" when i + 1 < args.Length:
                    inPath = args[++i]; break;
                case "--mic":
                    mic = true; break;
                case "--seconds" when i + 1 < args.Length && int.TryParse(args[i + 1], out var sec):
                    seconds = Math.Clamp(sec, 1, 120); i++; break;
                case "--lang" when i + 1 < args.Length:
                    lang = args[++i]; break;
                case "--device" when i + 1 < args.Length && int.TryParse(args[i + 1], out var dn):
                    device = dn; deviceSet = true; i++; break;
            }
        }

        if (inPath is null && !mic)
        {
            Console.Error.WriteLine("Usage: ai stt (--in FILE.wav | --mic [--seconds N] [--device N]) [--lang XX]");
            return 1;
        }
        if (inPath is not null && mic)
        {
            Console.Error.WriteLine("Error: choose one input source — either --in or --mic, not both.");
            return 1;
        }

        var v = VoiceSettingsStore.Load();
        var stt = VoiceRuntimeFactory.BuildStt(v);
        if (stt is null)
        {
            Console.Error.WriteLine($"STT provider '{v.SttProvider}' is not available. " +
                                    "Configure it in the AgentZero window: Settings → Voice.");
            return 1;
        }
        var useLang = lang ?? v.SttLanguage;

        // ── Acquire 16 kHz / 16-bit / mono PCM (the shape every STT provider wants) ──
        byte[] pcm;
        if (inPath is not null)
        {
            if (!File.Exists(inPath))
            {
                Console.Error.WriteLine($"Error: file not found: {inPath}");
                return 1;
            }
            try
            {
                var wavBytes = File.ReadAllBytes(inPath);
                pcm = WavToPcm.To16kMono(wavBytes);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: could not decode '{inPath}' as WAV: {ex.Message}");
                return 1;
            }
            if (pcm.Length == 0)
            {
                Console.Error.WriteLine("Error: decoded audio was empty.");
                return 1;
            }
        }
        else
        {
            int devNo = deviceSet ? device : VoiceRuntimeFactory.ParseDeviceNumber(v.InputDeviceId);
            if (devNo < 0) devNo = 0;
            pcm = RecordFromMic(devNo, seconds);
            if (pcm.Length == 0)
            {
                Console.Error.WriteLine("Error: no audio captured from the microphone.");
                return 1;
            }
        }

        try
        {
            var ready = stt.EnsureReadyAsync(new Progress<string>(m => Console.Error.WriteLine($"[ai] {m}")))
                           .GetAwaiter().GetResult();
            if (!ready)
            {
                Console.Error.WriteLine($"Error: STT provider '{stt.ProviderName}' is not ready (model/credentials). " +
                                        "Check the AgentZero window: Settings → Voice.");
                return 1;
            }

            Console.Error.WriteLine($"[ai] transcribing {pcm.Length} bytes (~{pcm.Length / 32000.0:0.0}s @16k) " +
                                    $"via {stt.ProviderName} (lang={useLang}) …");
            var textOut = stt.TranscribeAsync(pcm, useLang).GetAwaiter().GetResult();
            Console.WriteLine(textOut);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: transcription failed: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Record raw PCM from a microphone input source for a fixed duration. Uses
    /// the mic capture service's <c>FrameAvailable</c> stream directly (not the
    /// VAD-gated buffer) so the full window is captured regardless of speech
    /// segmentation — the CLI wants deterministic "record N seconds" behavior.
    /// </summary>
    private static byte[] RecordFromMic(int deviceNumber, int seconds)
    {
        var collected = new List<byte>();
        using var cap = new VoiceCaptureService();
        cap.FrameAvailable += f =>
        {
            lock (collected) { collected.AddRange(f.Pcm16k); }
        };
        Console.Error.WriteLine($"[ai] recording {seconds}s from input device {deviceNumber} … speak now.");
        try
        {
            cap.Start(deviceNumber);
            Thread.Sleep(seconds * 1000);
        }
        finally
        {
            cap.Stop();
        }
        lock (collected) { return collected.ToArray(); }
    }

    // =========================================================== voice devices

    private static int Devices(string[] _)
    {
        Console.WriteLine("Input devices (microphone — use with 'ai stt --mic --device N'):");
        var inputs = VoiceCaptureService.ListDevices();
        if (inputs.Count == 0) Console.WriteLine("  (none)");
        foreach (var d in inputs)
            Console.WriteLine($"  [{d.DeviceNumber}] {d.Name}");
        Console.WriteLine();

        Console.WriteLine("Output devices (speaker — use with 'ai tts --speaker --device N'):");
        Console.WriteLine("  [-1] (system default)");
        try
        {
            for (int i = 0; i < WaveOut.DeviceCount; i++)
            {
                var caps = WaveOut.GetCapabilities(i);
                Console.WriteLine($"  [{i}] {caps.ProductName}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  (output enumeration failed: {ex.Message})");
        }
        return 0;
    }

    // =============================================================== helpers

    /// <summary>Configured default voice for the active TTS provider, or "" (provider default).</summary>
    private static string ResolveDefaultVoice(VoiceSettings v)
    {
        if (v.TtsProvider == TtsProviderNames.Supertonic && !string.IsNullOrWhiteSpace(v.SupertonicVoice))
            return v.SupertonicVoice;
        return string.IsNullOrWhiteSpace(v.TtsVoice) ? "" : v.TtsVoice;
    }

    private static int UnknownVerb(string v)
    {
        Console.Error.WriteLine($"Unknown ai verb: {v}");
        Console.WriteLine();
        PrintUsage();
        return 1;
    }

    private static int PrintUsageOk()
    {
        PrintUsage();
        return 0;
    }

    public static void PrintUsage()
    {
        Console.WriteLine("Usage: AgentZeroLite.exe -cli ai <verb> [args]");
        Console.WriteLine();
        Console.WriteLine("Drives the voice AI configured in the AgentZero window — set the provider / voice");
        Console.WriteLine("in Settings → Voice. This CLI just uses what's configured.");
        Console.WriteLine();
        Console.WriteLine("Voice — TTS (text → speech):");
        Console.WriteLine("  voices                                   List voices for the active TTS provider");
        Console.WriteLine("  tts <text> [--voice V] [--speed S]       Synthesize speech. Default plays on the speaker;");
        Console.WriteLine("             [--out FILE.wav] [--speaker]      --out writes a WAV file instead. --device picks the speaker.");
        Console.WriteLine("             [--device N]                      --speed 0.7..2.0 (lower = slower; Supertonic only).");
        Console.WriteLine();
        Console.WriteLine("Voice — STT (speech → text):");
        Console.WriteLine("  stt --in FILE.wav [--lang XX]            Transcribe an audio file");
        Console.WriteLine("  stt --mic [--seconds N] [--device N]     Record from a microphone input source, then transcribe");
        Console.WriteLine("            [--lang XX]");
        Console.WriteLine();
        Console.WriteLine("Devices:");
        Console.WriteLine("  devices                                  List microphone (input) and speaker (output) devices");
        Console.WriteLine();
        Console.WriteLine("Notes:");
        Console.WriteLine("  * No GUI needed — runs in-process against the configured voice provider.");
        Console.WriteLine("  * Transcripts go to stdout; progress + errors go to stderr, so you can pipe cleanly.");
    }
}
