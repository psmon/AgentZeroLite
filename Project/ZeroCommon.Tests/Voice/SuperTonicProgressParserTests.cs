using Agent.Common.Voice;

namespace ZeroCommon.Tests.Voice;

/// <summary>
/// Coverage for tqdm progress-line parsing — the live download progress that
/// the Settings → Voice → Supertonic → Download Model dialog draws from. The
/// parser must (a) recognise the real lines HF Hub emits, (b) ignore banners
/// and warnings, (c) handle no-ETA early lines, (d) clamp percent.
/// </summary>
public sealed class SuperTonicProgressParserTests
{
    [Theory]
    [InlineData("Fetching 26 files:  23%|##3       | 6/26 [00:04<00:15,  1.32it/s]",
                "Fetching 26 files", 23, "6 / 26", true, "00:04 elapsed, 00:15 left", "1.32it/s")]
    [InlineData("Fetching 26 files: 100%|##########| 26/26 [00:09<00:00,  2.62it/s]",
                "Fetching 26 files", 100, "26 / 26", true, "00:09 elapsed, 00:00 left", "2.62it/s")]
    [InlineData("Downloading model.bin:  47%|####6     | 188/400 [00:12<00:13, 16.0MB/s]",
                "Downloading model.bin", 47, "188 / 400", true, "00:12 elapsed, 00:13 left", "16.0MB/s")]
    public void Parses_full_tqdm_line(string line, string caption, int percent, string countSegment,
        bool hasEta, string etaSegment, string rateSegment)
    {
        var s = SuperTonicProgressParser.Parse(line);
        Assert.NotNull(s);
        Assert.Equal(caption, s!.Caption);
        Assert.Equal(percent, s.PercentComplete);
        Assert.Contains(countSegment, s.Detail);
        if (hasEta) Assert.Contains(etaSegment, s.Detail);
        Assert.Contains(rateSegment, s.Detail);
        Assert.False(s.IsTerminal); // Parser produces in-flight updates only — terminal is set by PrewarmModelAsync.
    }

    [Fact]
    public void Parses_early_line_without_eta_or_rate()
    {
        // First tqdm tick before any ETA can be computed.
        var s = SuperTonicProgressParser.Parse("Fetching 26 files:   0%|          | 0/26 [00:00<?, ?it/s]");
        Assert.NotNull(s);
        Assert.Equal(0, s!.PercentComplete);
        Assert.Contains("0 / 26", s.Detail);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Warning: You are sending unauthenticated requests to the HF Hub.")]
    [InlineData("[supertonic] loading TTS + downloading model if needed (~400 MB on first run)…")]
    [InlineData("Traceback (most recent call last):")]
    [InlineData("PermissionError: [WinError 5] Access is denied")]
    public void Ignores_non_progress_lines(string line)
    {
        Assert.Null(SuperTonicProgressParser.Parse(line));
    }

    [Fact]
    public void Strips_trailing_carriage_return()
    {
        // Real subprocess output sometimes carries \r when tqdm rewrites.
        var s = SuperTonicParserParseHelper("Fetching 26 files:  50%|#####     | 13/26 [00:05<00:05,  2.50it/s]\r");
        Assert.NotNull(s);
        Assert.Equal(50, s!.PercentComplete);
    }

    [Fact]
    public void Clamps_percent_above_100_to_100()
    {
        // Defensive: extremely rare tqdm overrun (counter exceeds total).
        var s = SuperTonicParserParseHelper("Fetching 26 files: 110%|##########| 28/26 [00:09<00:00,  3.00it/s]");
        Assert.NotNull(s);
        Assert.Equal(100, s!.PercentComplete);
    }

    // Parser is internal — tests live in the same assembly via InternalsVisibleTo.
    private static ModelDownloadStatus? SuperTonicParserParseHelper(string line)
        => SuperTonicProgressParser.Parse(line);
}
