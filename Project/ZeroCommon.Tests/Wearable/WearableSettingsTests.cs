using System.Text.Json;
using Agent.Common.Voice;
using Agent.Common.Wearable;
using Xunit;

namespace ZeroCommon.Tests.Wearable;

/// <summary>
/// Shape tests for <see cref="WearableSettings"/> and the path half of the store. The
/// wearable host is a second process that reads these files, so the contract between the
/// GUI and the host <i>is</i> this JSON — a silently renamed field would leave the watch
/// answering with last week's settings and no error anywhere.
/// </summary>
public sealed class WearableSettingsTests
{
    [Fact]
    public void Defaults_match_product_decisions()
    {
        var s = new WearableSettings();

        Assert.False(s.Enabled);                                 // opt-in: it holds a radio
        Assert.Equal("claude-hud", s.DeviceName);
        Assert.False(s.DisableBle);
        Assert.Equal(2552, s.RemotingPort);
        Assert.Equal("127.0.0.1", s.BindAddress);                // loopback: the tunnel is in-process
        Assert.Equal("AskBot", s.SystemName);
        Assert.True(s.HudEnabled);
        Assert.Equal(8765, s.HudPort);                           // what installed Claude hooks post to
        Assert.Equal(WearableBrainNames.AgentExternal, s.Brain);  // no model in this process by default
        Assert.Equal("echo", s.CliProvider);                     // offline loopback
        Assert.Equal("", s.WorkspaceRoot);                       // file tools default-deny
        Assert.Equal(400, s.ChunkBytes);
        Assert.Equal(1200, s.MaxReplyChars);
        Assert.Equal(0, s.TalkOnConnectMs);
    }

    /// <summary>
    /// The silence gate is what stops whisper.cpp inventing "[구독 / 좋아요]" on a quiet
    /// capture and the watch then asking the model about it. A quiet room measures rms
    /// -47..-61 dBFS and speech -32..-31, so -45 has to sit in that gap.
    /// </summary>
    [Fact]
    public void Silence_gate_sits_between_a_quiet_room_and_speech()
    {
        var s = new WearableSettings();
        Assert.Equal(-45, s.SilencePeakDb);
        Assert.Equal(-45, s.SilenceRmsDb);
        Assert.InRange(s.SilenceRmsDb, -47, -32);
    }

    [Fact]
    public void Json_round_trip_preserves_all_fields()
    {
        var s = new WearableSettings
        {
            Enabled = true,
            DeviceName = "my-watch",
            DisableBle = true,
            RemotingPort = 2562,
            BindAddress = "0.0.0.0",
            SystemName = "Watch",
            HudEnabled = false,
            HudPort = 9765,
            Brain = WearableBrainNames.AgentLocal,
            CliProvider = "claude",
            LocalModelId = "gemma-4-e4b",
            WorkspaceRoot = @"C:\work",
            ReplyStyle = "be brief",
            MaxReplyChars = 400,
            ChunkBytes = 200,
            AnnounceOnConnect = "안녕하세요",
            TalkOnConnectMs = 4000,
            SilencePeakDb = -50,
            SilenceRmsDb = -52,
            MaxCaptureSeconds = 30,
        };

        var loaded = JsonSerializer.Deserialize<WearableSettings>(JsonSerializer.Serialize(s))!;

        Assert.True(loaded.Enabled);
        Assert.Equal("my-watch", loaded.DeviceName);
        Assert.True(loaded.DisableBle);
        Assert.Equal(2562, loaded.RemotingPort);
        Assert.Equal("0.0.0.0", loaded.BindAddress);
        Assert.Equal("Watch", loaded.SystemName);
        Assert.False(loaded.HudEnabled);
        Assert.Equal(9765, loaded.HudPort);
        Assert.Equal(WearableBrainNames.AgentLocal, loaded.Brain);
        Assert.Equal("claude", loaded.CliProvider);
        Assert.Equal("gemma-4-e4b", loaded.LocalModelId);
        Assert.Equal(@"C:\work", loaded.WorkspaceRoot);
        Assert.Equal("be brief", loaded.ReplyStyle);
        Assert.Equal(400, loaded.MaxReplyChars);
        Assert.Equal(200, loaded.ChunkBytes);
        Assert.Equal("안녕하세요", loaded.AnnounceOnConnect);
        Assert.Equal(4000, loaded.TalkOnConnectMs);
        Assert.Equal(-50, loaded.SilencePeakDb);
        Assert.Equal(-52, loaded.SilenceRmsDb);
        Assert.Equal(30, loaded.MaxCaptureSeconds);
    }

    [Fact]
    public void Store_round_trips_through_an_explicit_file()
    {
        // An explicit path, never WearableSettingsStore.DefaultFilePath: the default is the
        // operator's real settings and the host reads it for real.
        var path = Path.Combine(Path.GetTempPath(), $"wearable-{Guid.NewGuid():N}.json");
        try
        {
            WearableSettingsStore.Save(new WearableSettings { DeviceName = "watch-7", Enabled = true }, path);
            var loaded = WearableSettingsStore.Load(path);

            Assert.Equal("watch-7", loaded.DeviceName);
            Assert.True(loaded.Enabled);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Store_returns_defaults_for_a_missing_or_corrupt_file()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"wearable-missing-{Guid.NewGuid():N}.json");
        Assert.Equal("claude-hud", WearableSettingsStore.Load(missing).DeviceName);

        var corrupt = Path.Combine(Path.GetTempPath(), $"wearable-corrupt-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(corrupt, "{ not json");
            // A settings file someone hand-edited badly must not stop the watch from being
            // served — it falls back to the defaults, which are all safe (BLE off-by-default
            // is Enabled=false, file tools denied).
            var loaded = WearableSettingsStore.Load(corrupt);
            Assert.False(loaded.Enabled);
            Assert.Equal("", loaded.WorkspaceRoot);
        }
        finally
        {
            if (File.Exists(corrupt)) File.Delete(corrupt);
        }
    }

    [Fact]
    public void Settings_file_lives_beside_the_other_stores()
    {
        var path = WearableSettingsStore.DefaultFilePath;
        Assert.EndsWith("wearable-settings.json", path);
        Assert.Contains("AgentZeroLite", path);
    }
}

/// <summary>
/// The Whisper path convention moved into ZeroCommon so the wearable host resolves the
/// same files the GUI does. These assertions are what keep the two processes pointing at
/// one download.
/// </summary>
public sealed class WhisperModelStoreTests
{
    [Fact]
    public void Model_directory_is_the_agentzero_whisper_folder()
    {
        var dir = WhisperModelStore.ModelDirectory;
        Assert.Contains(Path.Combine(".ollama", "models", "agentzero", "whisper"), dir);
    }

    [Theory]
    [InlineData("tiny", "ggml-tiny.bin")]
    [InlineData("small", "ggml-small.bin")]
    [InlineData("medium", "ggml-medium.bin")]
    public void Known_sizes_map_to_their_ggml_file(string model, string file)
        => Assert.Equal(file, Path.GetFileName(WhisperModelStore.ModelPath(model)));

    [Fact]
    public void Unknown_size_falls_back_to_small_rather_than_throwing()
    {
        Assert.Equal("small", WhisperModelStore.Normalize("enormous"));
        Assert.Equal("small", WhisperModelStore.Normalize(null));
        Assert.Equal("ggml-small.bin", Path.GetFileName(WhisperModelStore.ModelPath("enormous")));
    }

    [Fact]
    public void A_truncated_download_counts_as_missing()
    {
        // IsDownloaded's whole job: a half-written 466 MB file must not be handed to
        // whisper.cpp, which fails deep inside the native loader instead of here.
        var dir = WhisperModelStore.ModelDirectory;
        var path = WhisperModelStore.ModelPath("small");
        if (File.Exists(path)) return;   // the operator has the real model; nothing to fake

        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllBytes(path, new byte[1024]);
            Assert.False(WhisperModelStore.IsDownloaded("small"));
        }
        finally
        {
            if (File.Exists(path) && new FileInfo(path).Length == 1024) File.Delete(path);
        }
    }
}
