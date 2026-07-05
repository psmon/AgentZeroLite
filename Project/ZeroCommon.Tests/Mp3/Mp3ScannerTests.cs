using Agent.Common.Mp3;
using Xunit;

namespace ZeroCommon.Tests.Mp3;

// Headless coverage for the MP3 scan helpers (M0029). TagLib parsing needs
// real MP3 fixtures, so these cover the pure parts the scan job leans on:
// filename→title cleanup, the mp3.local relative-URL path (incl. the
// path-escape guard), and the LLM classification hint.
public sealed class Mp3ScannerTests
{
    [Theory]
    [InlineData("01. IU - 좋은 날.mp3", "IU - 좋은 날")]
    [InlineData("03 - Butter.mp3", "Butter")]
    [InlineData("1-02 Nocturne op.9 no.2.mp3", "Nocturne op.9 no.2")]
    [InlineData("my_song_title.mp3", "my song title")]
    [InlineData("NoNumber.mp3", "NoNumber")]
    public void TitleFromFileName_StripsTrackNumbersAndUnderscores(string fileName, string expected)
        => Assert.Equal(expected, Mp3Scanner.TitleFromFileName(fileName));

    [Fact]
    public void TitleFromFileName_AllDigitsName_DoesNotGoEmpty()
        // "01.mp3" — stripping the "track number" would leave nothing; keep the stem.
        => Assert.Equal("01", Mp3Scanner.TitleFromFileName("01.mp3"));

    [Theory]
    [InlineData(@"C:\music", @"C:\music\kpop\a.mp3", "kpop/a.mp3")]
    [InlineData(@"C:\music", @"C:\music\a.mp3", "a.mp3")]
    [InlineData(@"C:\music", @"C:\other\a.mp3", "")]          // outside root → unplayable
    [InlineData(@"C:\music", @"C:\music\..\secret\a.mp3", "")] // escape attempt → ""
    [InlineData("", @"C:\music\a.mp3", "")]
    public void ToRelativePath_MapsUnderRootOnly(string root, string full, string expected)
        => Assert.Equal(expected, Mp3Scanner.ToRelativePath(root, full));

    [Fact]
    public void BuildClassifyHint_UsesTagFieldsThenPathFacts()
    {
        var f = new Mp3ScannedFile(
            FilePath: @"C:\music\OST\포뇨 주제가.mp3", RelativePath: "OST/포뇨 주제가.mp3",
            FileName: "포뇨 주제가.mp3", FolderName: "OST",
            Title: "포뇨 주제가", Artist: "후지오카 후지마키", Album: "",
            TagGenre: "Soundtrack", DurationSeconds: 161, FileSizeBytes: 1);
        var hint = Mp3Scanner.BuildClassifyHint(f);
        Assert.Contains("제목: 포뇨 주제가", hint);
        Assert.Contains("아티스트: 후지오카 후지마키", hint);
        Assert.Contains("태그장르: Soundtrack", hint);
        Assert.Contains("폴더명: OST", hint);
        Assert.DoesNotContain("앨범:", hint);   // empty fields are omitted
    }

    [Fact]
    public void ReadFile_TaglessFile_FallsBackToFileNameTitle()
    {
        // Not a real MP3 — TagLib throws, ReadFile must survive and fall back.
        var dir = Path.Combine(Path.GetTempPath(), "az-mp3-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "발라드"));
        var path = Path.Combine(dir, "발라드", "02 - 가짜곡.mp3");
        File.WriteAllBytes(path, new byte[] { 0x00, 0x01, 0x02 });
        try
        {
            var f = Mp3Scanner.ReadFile(dir, path);
            Assert.Equal("가짜곡", f.Title);
            Assert.Equal("발라드", f.FolderName);
            Assert.Equal("발라드/02 - 가짜곡.mp3", f.RelativePath);
            Assert.Equal(3, f.FileSizeBytes);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void EnumerateMp3Files_RecursesAndSorts()
    {
        var dir = Path.Combine(Path.GetTempPath(), "az-mp3-enum-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        File.WriteAllText(Path.Combine(dir, "b.mp3"), "");
        File.WriteAllText(Path.Combine(dir, "sub", "a.mp3"), "");
        File.WriteAllText(Path.Combine(dir, "not-music.txt"), "");
        try
        {
            var files = Mp3Scanner.EnumerateMp3Files(dir);
            Assert.Equal(2, files.Count);
            Assert.All(files, f => Assert.EndsWith(".mp3", f));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
