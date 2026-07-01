using System.Text;
using Agent.Common.Voice;
using Xunit;

namespace ZeroCommon.Tests.Voice;

// ───────────────────────────────────────────────────────────
// Native SuperTonic ONNX TTS — headless coverage.
//
// The ~398 MB model is NOT available in CI, so these tests exercise the
// pure logic that a native port has to get exactly right: text
// normalization, Unicode-indexer tokenization, WAV assembly, chunking,
// and language coercion. Full synthesis is a manual E2E check (Voice tab).
//
// Context: replaces the pip/Python SuperTonicTts (M0020). The normalization
// here mirrors Supertone's official csharp/Helper.cs byte-for-byte — any
// drift silently degrades pronunciation, so it's asserted explicitly.
// ───────────────────────────────────────────────────────────
public sealed class SuperTonicOnnxTtsTests
{
    // ── Text normalization ──────────────────────────────────

    [Fact]
    public void Preprocess_WrapsWithLanguageTags()
    {
        var result = SuperTonicText.Preprocess("hello", "en");
        Assert.StartsWith("<en>", result);
        Assert.EndsWith("</en>", result);
    }

    [Fact]
    public void Preprocess_AddsTrailingPeriodWhenNoTerminalPunctuation()
    {
        var result = SuperTonicText.Preprocess("hello world", "en");
        Assert.Equal("<en>hello world.</en>", result);
    }

    [Fact]
    public void Preprocess_KeepsExistingTerminalPunctuation()
    {
        Assert.Equal("<en>What?</en>", SuperTonicText.Preprocess("What?", "en"));
        Assert.Equal("<en>Yes!</en>", SuperTonicText.Preprocess("Yes!", "en"));
    }

    [Fact]
    public void Preprocess_ReplacesSmartQuotesAndDashes()
    {
        // Curly quotes → straight, em/en dash → hyphen.
        var result = SuperTonicText.Preprocess("“hi” — ok", "en");
        Assert.Contains("\"hi\"", result);
        Assert.Contains("- ok", result);
        Assert.DoesNotContain("“", result);
        Assert.DoesNotContain("—", result);
    }

    [Fact]
    public void Preprocess_RemovesEmojis()
    {
        var result = SuperTonicText.Preprocess("hi 😀 there", "en");
        Assert.DoesNotContain("\uD83D", result);
        Assert.DoesNotContain("\uDE00", result);
        Assert.Contains("hi", result);
        Assert.Contains("there", result);
    }

    [Fact]
    public void Preprocess_CollapsesWhitespace()
    {
        var result = SuperTonicText.Preprocess("a    b\t\tc", "en");
        Assert.Equal("<en>a b c.</en>", result);
    }

    [Fact]
    public void Preprocess_FixesSpaceBeforePunctuation()
    {
        var result = SuperTonicText.Preprocess("hello , world", "en");
        Assert.Equal("<en>hello, world.</en>", result);
    }

    [Fact]
    public void Preprocess_UnknownLanguageCoercesToNa()
    {
        // "zh" is not in Supertonic-3's supported set — must not throw.
        var result = SuperTonicText.Preprocess("nihao", "zh");
        Assert.StartsWith("<na>", result);
        Assert.EndsWith("</na>", result);
    }

    [Fact]
    public void Preprocess_KoreanIsNfkdDecomposedThenTagged()
    {
        // The reference applies NFKD (FormKD), which decomposes precomposed
        // Hangul syllables into conjoining jamo — the form the unicode indexer
        // is built against. Assert against the NFKD body rather than the
        // precomposed source.
        var body = "안녕하세요".Normalize(NormalizationForm.FormKD);
        var result = SuperTonicText.Preprocess("안녕하세요", "ko");
        Assert.Equal($"<ko>{body}.</ko>", result);
    }

    // ── Language coercion ───────────────────────────────────

    [Theory]
    [InlineData("ko", "ko")]
    [InlineData("en", "en")]
    [InlineData("na", "na")]
    [InlineData("zh", "na")]   // unsupported
    [InlineData("", "na")]     // empty
    [InlineData(null, "na")]   // null
    public void LanguageCoerce_MapsToSupportedOrNa(string? input, string expected)
        => Assert.Equal(expected, SuperTonicLanguages.Coerce(input));

    // ── Tokenizer ───────────────────────────────────────────

    [Fact]
    public void Tokenizer_MapsCodePointsThroughIndexer()
    {
        // Build an indexer array where index == code point, value == cp * 2.
        var indexer = new long[128];
        for (int i = 0; i < indexer.Length; i++) indexer[i] = i * 2L;
        var tok = SuperTonicTokenizer.FromArray(indexer);

        var ids = tok.Encode("AB");
        Assert.Equal(2, ids.Length);
        Assert.Equal('A' * 2L, ids[0]);
        Assert.Equal('B' * 2L, ids[1]);
    }

    [Fact]
    public void Tokenizer_OutOfRangeCodePointMapsToZero()
    {
        var tok = SuperTonicTokenizer.FromArray(new long[] { 5, 6, 7 });
        var ids = tok.Encode("Ѐ"); // code point 1024, beyond indexer length
        Assert.Single(ids);
        Assert.Equal(0L, ids[0]);
    }

    // ── WAV assembly ────────────────────────────────────────

    [Fact]
    public void ToWavBytes_WritesValidPcm16MonoHeader()
    {
        var samples = new float[] { 0f, 0.5f, -0.5f, 1f, -1f };
        var wav = SuperTonicWav.ToWavBytes(samples, 44100);

        // 44-byte canonical header + 2 bytes/sample.
        Assert.Equal(44 + samples.Length * 2, wav.Length);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal("fmt ", Encoding.ASCII.GetString(wav, 12, 4));
        Assert.Equal("data", Encoding.ASCII.GetString(wav, 36, 4));

        Assert.Equal((short)1, BitConverter.ToInt16(wav, 20));       // PCM
        Assert.Equal((short)1, BitConverter.ToInt16(wav, 22));       // mono
        Assert.Equal(44100, BitConverter.ToInt32(wav, 24));          // sample rate
        Assert.Equal((short)16, BitConverter.ToInt16(wav, 34));      // bits/sample
        Assert.Equal(samples.Length * 2, BitConverter.ToInt32(wav, 40)); // data size
    }

    [Fact]
    public void ToWavBytes_ClampsAndScalesSamples()
    {
        var wav = SuperTonicWav.ToWavBytes(new float[] { 2f, -2f, 0f }, 44100);
        Assert.Equal((short)32767, BitConverter.ToInt16(wav, 44));       // +2 clamped to +1
        Assert.Equal((short)(-1f * 32767), BitConverter.ToInt16(wav, 46)); // -2 clamped to -1
        Assert.Equal((short)0, BitConverter.ToInt16(wav, 48));
    }

    // ── Chunking ────────────────────────────────────────────

    [Fact]
    public void Chunk_ShortTextReturnsSingleChunk()
    {
        var chunks = SuperTonicText.Chunk("Just one sentence.", "en");
        Assert.Single(chunks);
    }

    [Fact]
    public void Chunk_SplitsLongEnglishBySentences()
    {
        var sentence = string.Concat(Enumerable.Repeat("word ", 50)) + "end."; // ~255 chars
        var text = sentence + " " + sentence; // exceeds 300 → must split
        var chunks = SuperTonicText.Chunk(text, "en");
        Assert.True(chunks.Count >= 2);
        Assert.All(chunks, c => Assert.True(c.Length <= 300));
    }

    // ── Model store ─────────────────────────────────────────

    [Fact]
    public void ModelStore_ReportsMissingWhenDirAbsent()
    {
        Assert.False(SuperTonicModelStore.IsModelPresent(
            Path.Combine(Path.GetTempPath(), "agentzero-supertonic-missing-" + Guid.NewGuid().ToString("N"))));
    }

    [Fact]
    public void ModelStore_ResolvesDefaultWhenSettingEmpty()
    {
        var v = new VoiceSettings { SupertonicModelDir = "" };
        Assert.Equal(SuperTonicModelStore.DefaultModelDirectory, SuperTonicModelStore.ResolveModelDir(v));
    }

    [Fact]
    public void ModelStore_VoiceStylePathUsesVoiceStylesSubdir()
    {
        var path = SuperTonicModelStore.VoiceStylePath(@"C:\m", "F1");
        Assert.Equal(Path.Combine(@"C:\m", "voice_styles", "F1.json"), path);
    }

    [Fact]
    public async Task Provider_ThrowsHelpfulErrorWhenModelMissing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "agentzero-supertonic-absent-" + Guid.NewGuid().ToString("N"));
        var tts = new SuperTonicOnnxTts(dir);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => tts.SynthesizeAsync("hello", "F1"));
        Assert.Contains("Download Model", ex.Message);
    }
}
