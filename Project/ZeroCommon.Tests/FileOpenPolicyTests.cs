using Agent.Common.Llm.Tools;

namespace ZeroCommon.Tests;

/// <summary>
/// <c>open_file</c> is ShellExecute, so the extension allow-list is the line between
/// "play this song" and "run this program" (M0032).
/// </summary>
[Trait("Category", "FileTools")]
public sealed class FileOpenPolicyTests
{
    [Theory]
    [InlineData("music/friday-mix.mp3", FileOpenPolicy.KindMedia)]
    [InlineData("clips/holiday.MP4", FileOpenPolicy.KindMedia)]
    [InlineData("photos/cat.png", FileOpenPolicy.KindImage)]
    [InlineData("photos/cat.JPEG", FileOpenPolicy.KindImage)]
    [InlineData("docs/report.pdf", FileOpenPolicy.KindDocument)]
    [InlineData("docs/notes.md", FileOpenPolicy.KindDocument)]
    public void Media_images_and_documents_are_openable(string path, string expectedKind)
    {
        Assert.True(FileOpenPolicy.TryClassify(path, out var kind, out var error), error);
        Assert.Equal(expectedKind, kind);
    }

    [Theory]
    [InlineData("tools/setup.exe")]
    [InlineData("desktop/Notepad.lnk")]
    [InlineData("scripts/deploy.ps1")]
    [InlineData("scripts/run.bat")]
    [InlineData("scripts/run.cmd")]
    [InlineData("web/page.html")]
    [InlineData("web/app.js")]
    [InlineData("installers/app.msi")]
    [InlineData("music/song.mp3.exe")]
    [InlineData("README")]
    [InlineData("")]
    public void Executables_scripts_and_unknown_types_are_refused(string path)
    {
        Assert.False(FileOpenPolicy.TryClassify(path, out var kind, out var error));
        Assert.Equal("", kind);
        Assert.NotEmpty(error);
    }
}
