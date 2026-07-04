using Agent.Common.Vision;
using Xunit;

namespace ZeroCommon.Tests.Vision;

// Headless coverage for the vision model store — the Florence-2 ONNX bundle
// itself is not in CI, so these assert the path convention + the sentinel-marker
// presence contract that IsModelPresent keys off (a half-written cache must read
// as absent). Full download + inference is the manual E2E check (Vision tab).
public sealed class VisionSettingsStoreTests
{
    [Fact]
    public void ResolveModelDir_FallsBackToDefault_WhenEmpty()
    {
        var s = new VisionSettings { ModelDir = "" };
        Assert.Equal(VisionSettingsStore.DefaultModelDirectory, VisionSettingsStore.ResolveModelDir(s));
    }

    [Fact]
    public void ResolveModelDir_UsesOverride_WhenSet()
    {
        var s = new VisionSettings { ModelDir = @"C:\custom\vision" };
        Assert.Equal(@"C:\custom\vision", VisionSettingsStore.ResolveModelDir(s));
    }

    [Fact]
    public void IsModelPresent_TrueOnlyAfterMarkerWritten()
    {
        var dir = Path.Combine(Path.GetTempPath(), "az-vision-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // Files may exist but without the completion marker it's "not ready".
            Assert.False(VisionSettingsStore.IsModelPresent(dir));
            File.WriteAllText(VisionSettingsStore.ReadyMarkerPath(dir), "done");
            Assert.True(VisionSettingsStore.IsModelPresent(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void IsModelPresent_FalseForEmptyOrMissingDir()
    {
        Assert.False(VisionSettingsStore.IsModelPresent(""));
        Assert.False(VisionSettingsStore.IsModelPresent(
            Path.Combine(Path.GetTempPath(), "az-vision-missing-" + Guid.NewGuid().ToString("N"))));
    }
}
