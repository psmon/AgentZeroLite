using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agent.Common.Platform;
using Agent.Common.Voice;
using Agent.Common.Voice.KittenTts2;
using Agent.Common.Voice.KittenTts2.TextNorm;
using Xunit.Abstractions;

namespace ZeroCommon.Tests.Voice;

// ───────────────────────────────────────────────────────────
// KittenTTS 2 — the parts that must match kittenml exactly without the 2.9 GB model: text normalization
// (a C# port of kitten_text_processing's FST runtime, against Python's output for 78 texts), chunking and
// joining, the bundle store / installer, and engine selection. Synthesis itself was measured end to end
// (wearable --speak, Whisper hears it back); it needs the bundle and a Vulkan device.
// ───────────────────────────────────────────────────────────
public sealed class KittenTts2Tests(ITestOutputHelper output)
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "Voice", "KittenTts2Data");

    [Fact]
    public void Normalization_matches_Python_for_every_test_text()
    {
        var text = new KittenText(new Normalizer(Path.Combine(Data, "textnorm-en")));
        var cases = JsonNode.Parse(File.ReadAllText(Path.Combine(Data, "normalize_expected.json")))!.AsArray();
        var diffs = new List<string>();
        foreach (var c in cases)
        {
            var input = (string)c!["input"]!;
            var expected = (string)c["expected"]!;
            var got = text.Normalize(input);
            if (got != expected) diffs.Add($"in: {input}\n py: {expected}\n c#: {got}");
        }
        foreach (var d in diffs) output.WriteLine(d);
        Assert.Equal(78, cases.Count);
        Assert.Empty(diffs);
    }

    [Theory]
    [InlineData("It costs $12.50 at 3 PM.", "It costs twelve dollars fifty cents at three PM.")]
    [InlineData("안녕하세요, 에이전트 제로입니다. 오늘 서울은 맑습니다.", "안녕하세요, 에이전트제로입니다. 오늘서울은맑습니다.")]
    public void Normalization_examples(string input, string expected)
    {
        var text = new KittenText(new Normalizer(Path.Combine(Data, "textnorm-en")));
        Assert.Equal(expected, text.Normalize(input));
    }

    [Fact]
    public void Expression_tags_switch_on_the_emotion_control()
    {
        Assert.True(KittenText.HasExpressionTag("[joyful] We won"));
        Assert.True(KittenText.HasExpressionTag("so (((much))) <sigh>"));
        Assert.False(KittenText.HasExpressionTag("section [3] and x < 5"));
    }

    // ── chunking / joining ──

    [Fact]
    public void Short_text_is_one_chunk_as_is()
    {
        Assert.Equal(["Hello there"], KittenTts2Text.Split("  Hello there "));
        Assert.Empty(KittenTts2Text.Split("   "));
    }

    [Fact]
    public void Long_text_packs_sentences_under_the_limit_and_ends_each_chunk_with_a_mark()
    {
        var sentence = "This sentence is about forty five characters. ";
        var chunks = KittenTts2Text.Split(string.Concat(Enumerable.Repeat(sentence, 20)));
        Assert.True(chunks.Count >= 2);
        Assert.All(chunks, c => Assert.True(c.Length <= (int)(KittenTts2Text.ChunkChars * 1.4)));
        Assert.All(chunks, c => Assert.Contains(c[^1], ".!?,;:"));
    }

    [Theory]
    [InlineData("a, b, c", ", ", 7, 4)]
    [InlineData("a, b, c", ", ", 5, 1)]     // ", " at 4 would end at 6 > 5
    [InlineData("a, b, c", ", ", 1, -1)]
    [InlineData("abc", "; ", 3, -1)]
    public void LastIndex_is_python_rfind_with_an_end(string s, string sub, int end, int expected) =>
        Assert.Equal(expected, KittenTts2Text.LastIndex(s, sub, end));

    [Fact]
    public void Join_trims_edge_silence_and_puts_one_gap_between_chunks()
    {
        const int rate = 24000;
        float[] Tone(int ms) => Enumerable.Range(0, rate * ms / 1000).Select(i => 0.5f * MathF.Sin(i * 0.1f)).ToArray();
        var quiet = new float[rate / 2];
        var a = quiet.Concat(Tone(300)).Concat(quiet).ToArray();
        var joined = KittenTts2Text.Join([a, a], rate);
        var expectedMax = 2 * (rate * 340 / 1000) + (int)(rate * KittenTts2Text.ChunkGapSeconds);
        Assert.InRange(joined.Length, (int)(rate * 0.75), expectedMax);
        Assert.Equal(0f, joined[0]);                                     // faded in
        Assert.Same(a, KittenTts2Text.Join([a], rate));                  // one chunk is returned untouched
    }

    // ── store / installer / engine ──

    [Fact]
    public void A_bundle_installs_from_a_folder_and_a_tampered_file_is_refused()
    {
        var root = Directory.CreateTempSubdirectory("kt2-root").FullName;
        var source = Directory.CreateTempSubdirectory("kt2-src").FullName;
        try
        {
            using var _ = AppPaths.OverrideRoot(root);
            WriteBundle(source, ("gguf/lm.gguf", "weights"), ("assets/voices/Bruno.json", "{}"));
            Assert.False(KittenTts2ModelStore.IsPresent(KittenTts2ModelStore.DefaultDirectory));

            var ok = KittenTts2ModelInstaller.InstallFromFolderAsync(source, new Progress<ModelDownloadStatus>(), default).Result;
            Assert.True(ok);
            Assert.True(KittenTts2ModelStore.IsPresent(KittenTts2ModelStore.DefaultDirectory));
            Assert.Equal(["Bruno"], KittenTts2ModelStore.Voices(KittenTts2ModelStore.DefaultDirectory));

            // Same size, different bytes: the hash catches it and nothing is replaced.
            File.WriteAllText(Path.Combine(source, "gguf", "lm.gguf"), "WEIGHTS");
            Directory.Delete(KittenTts2ModelStore.DefaultDirectory, true);
            var failed = KittenTts2ModelInstaller.InstallFromFolderAsync(source, new Progress<ModelDownloadStatus>(), default).Result;
            Assert.False(failed);
            Assert.False(File.Exists(Path.Combine(KittenTts2ModelStore.DefaultDirectory, "gguf", "lm.gguf")));
            Assert.False(KittenTts2ModelStore.IsPresent(KittenTts2ModelStore.DefaultDirectory));
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(source, true);
        }
    }

    [Fact]
    public void Settings_pick_the_engine_and_an_uninstalled_bundle_is_reported_not_loaded()
    {
        var root = Directory.CreateTempSubdirectory("kt2-engine").FullName;
        try
        {
            using var _ = AppPaths.OverrideRoot(root);
            using var engine = VoiceEngines.Create(new VoiceSettings
            {
                TtsProvider = TtsProviderNames.KittenTts2, Kitten2Voice = "bruno", Kitten2Language = "en",
            })!;
            // Off Windows the engine says so before it looks for a bundle (CI's macOS and Linux jobs run this too).
            var unavailable = OperatingSystem.IsWindows() ? "not installed" : "Windows only";
            Assert.IsType<KittenTts2VoiceEngine>(engine);
            Assert.False(engine.Available);
            Assert.Contains(unavailable, engine.Status);
            Assert.Equal("Bruno", engine.DefaultVoice);
            Assert.Equal("en", engine.DefaultLanguage);
            Assert.Throws<InvalidOperationException>(() => engine.Synthesize("hi"));

            Assert.IsType<SupertonicVoiceEngine>(VoiceEngines.Create(new VoiceSettings { TtsProvider = TtsProviderNames.Supertonic }));
            Assert.Null(VoiceEngines.Create(new VoiceSettings { TtsProvider = "KittenTTS" }));   // the 0.8 provider is gone
            Assert.DoesNotContain("KittenTTS", PortableVoiceRuntime.TtsProviders);
            Assert.Null(VoiceEngines.Create(new VoiceSettings { TtsProvider = TtsProviderNames.OpenAITts }));
            Assert.Contains(TtsProviderNames.KittenTts2, PortableVoiceRuntime.TtsProviders);
            Assert.Null(PortableVoiceRuntime.CreateTts(new VoiceSettings { TtsProvider = TtsProviderNames.KittenTts2 }, out var why));
            Assert.Contains(unavailable, why);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Download_fetches_flattened_assets_resumes_a_part_file_and_refuses_bad_bytes()
    {
        var root = Directory.CreateTempSubdirectory("kt2-dl").FullName;
        var source = Directory.CreateTempSubdirectory("kt2-dl-src").FullName;
        try
        {
            using var _ = AppPaths.OverrideRoot(root);
            var big = string.Concat(Enumerable.Repeat("0123456789", 5000));
            WriteBundle(source, ("gguf/lm.gguf", big), ("assets/voices/Bruno.json", "{}"));
            var server = new FakeRelease(source);
            var target = KittenTts2ModelStore.DefaultDirectory;

            // An interrupted earlier run left half of the big file.
            Directory.CreateDirectory(Path.Combine(target, "gguf"));
            File.WriteAllText(Path.Combine(target, "gguf", "lm.gguf.part"), big[..20000]);
            Assert.True(KittenTts2ModelDownloader.DownloadAsync(new Progress<ModelDownloadStatus>(), default,
                "https://example.test/rel/", server).Result);
            Assert.True(KittenTts2ModelStore.IsPresent(target));
            Assert.Equal(big, File.ReadAllText(Path.Combine(target, "gguf", "lm.gguf")));
            Assert.Contains("bytes=20000-", server.Ranges);
            Assert.Contains("assets.voices.Bruno.json", server.Requested);   // '/' -> '.' in the release

            // Second run: everything verified in place, nothing fetched but the manifest.
            server.Requested.Clear();
            Assert.True(KittenTts2ModelDownloader.DownloadAsync(new Progress<ModelDownloadStatus>(), default,
                "https://example.test/rel/", server).Result);
            Assert.Equal(["manifest.json"], server.Requested);

            // A corrupted asset is removed, not installed.
            Directory.Delete(target, true);
            server.Corrupt = "gguf.lm.gguf";
            Assert.False(KittenTts2ModelDownloader.DownloadAsync(new Progress<ModelDownloadStatus>(), default,
                "https://example.test/rel/", server).Result);
            Assert.False(File.Exists(Path.Combine(target, "gguf", "lm.gguf")));
            Assert.False(File.Exists(Path.Combine(target, "gguf", "lm.gguf.part")));
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(source, true);
        }
    }

    [Fact]
    public void A_file_split_into_numbered_assets_is_joined_and_resumed_mid_piece()
    {
        var root = Directory.CreateTempSubdirectory("kt2-split").FullName;
        var source = Directory.CreateTempSubdirectory("kt2-split-src").FullName;
        try
        {
            using var _ = AppPaths.OverrideRoot(root);
            var big = string.Concat(Enumerable.Range(0, 2500).Select(i => $"{i:0000}"));   // 10,000 bytes
            WriteBundle(source, ("decoder/d.onnx_data", big));
            // Re-publish it as 3 pieces of 4,000 bytes (the last 2,000).
            var manifestPath = Path.Combine(source, KittenTts2ModelStore.ManifestFile);
            var m = JsonNode.Parse(File.ReadAllText(manifestPath))!;
            m["files"]![0]!["parts"] = 3;
            m["files"]![0]!["part_size"] = 4000;
            File.WriteAllText(manifestPath, m.ToJsonString());
            var file = JsonSerializer.Deserialize<KittenTts2Manifest>(File.ReadAllText(manifestPath))!.Files[0];
            Assert.Equal([("decoder.d.onnx_data.001", 4000L), ("decoder.d.onnx_data.002", 4000L), ("decoder.d.onnx_data.003", 2000L)],
                KittenTts2ModelDownloader.Segments(file));

            var server = new FakeRelease(source) { PartSize = 4000 };
            var target = KittenTts2ModelStore.DefaultDirectory;
            Directory.CreateDirectory(Path.Combine(target, "decoder"));
            File.WriteAllText(Path.Combine(target, "decoder", "d.onnx_data.part"), big[..5000]);   // piece 1 + 1,000 of piece 2

            Assert.True(KittenTts2ModelDownloader.DownloadAsync(new Progress<ModelDownloadStatus>(), default,
                "https://example.test/rel/", server).Result);
            Assert.Equal(big, File.ReadAllText(Path.Combine(target, "decoder", "d.onnx_data")));
            Assert.DoesNotContain("decoder.d.onnx_data.001", server.Requested);   // already on disk
            Assert.Contains("bytes=1000-", server.Ranges);                       // resumed inside piece 2
            Assert.Contains("decoder.d.onnx_data.003", server.Requested);
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(source, true);
        }
    }

    /// <summary>A GitHub Release stand-in: flattened asset names, Range support, optional corruption.</summary>
    private sealed class FakeRelease(string bundle) : HttpMessageHandler
    {
        public List<string> Requested { get; } = [];
        public List<string> Ranges { get; } = [];
        public string? Corrupt { get; set; }
        public int PartSize { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var asset = Uri.UnescapeDataString(request.RequestUri!.Segments[^1]);
            Requested.Add(asset);
            var piece = -1;
            var name = asset;
            if (PartSize > 0 && System.Text.RegularExpressions.Regex.Match(asset, @"^(.*)\.(\d{3})$") is { Success: true } pm)
            {
                name = pm.Groups[1].Value;
                piece = int.Parse(pm.Groups[2].Value) - 1;
            }
            var path = name == KittenTts2ModelStore.ManifestFile ? name
                : Directory.EnumerateFiles(bundle, "*", SearchOption.AllDirectories)
                    .Select(f => Path.GetRelativePath(bundle, f).Replace('\\', '/'))
                    .FirstOrDefault(p => KittenTts2ModelDownloader.AssetName(p) == name);
            if (path is null) return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
            var bytes = File.ReadAllBytes(Path.Combine(bundle, path));
            if (piece >= 0) bytes = bytes[(piece * PartSize)..Math.Min(bytes.Length, (piece + 1) * PartSize)];
            if (asset == Corrupt) bytes[^1] ^= 0xFF;
            var from = 0;
            if (request.Headers.Range?.Ranges.FirstOrDefault()?.From is long f)
            {
                Ranges.Add($"bytes={f}-");
                from = (int)f;
            }
            var response = new HttpResponseMessage(from > 0 ? System.Net.HttpStatusCode.PartialContent : System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes[from..]),
            };
            return Task.FromResult(response);
        }
    }

    private static void WriteBundle(string dir, params (string Path, string Body)[] files)
    {
        var entries = new List<object>();
        foreach (var (path, body) in files)
        {
            var full = Path.Combine(dir, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, body);
            entries.Add(new
            {
                path, size = new FileInfo(full).Length,
                sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(full))),
            });
        }
        File.WriteAllText(Path.Combine(dir, KittenTts2ModelStore.ManifestFile), JsonSerializer.Serialize(new
        {
            name = KittenTts2ModelStore.BundleName, version = 1, alpha = 0.00390625, sample_rate = 24000,
            voices = new[] { "Bruno" }, decoder = "decoder/d.onnx", files = entries, total_bytes = 14,
        }));
    }
}
