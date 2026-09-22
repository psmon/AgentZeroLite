using AgentOne.Llm;
using AgentOne.Services;
using AgentOne.Tui;

namespace AgentOne.Tests;

/// <summary>
/// The settings screen's rules. The model takes real ConsoleKeyInfo values, so
/// these tests cover the key map itself — not a paraphrase of it — without ever
/// opening a terminal.
/// </summary>
[Collection(AgentOneHomeCollection.Name)]
public class ConfigTuiModelTests : IDisposable
{
    private readonly string _home;
    private readonly string? _previous;

    public ConfigTuiModelTests()
    {
        _previous = Environment.GetEnvironmentVariable(AppPaths.HomeEnvVar);
        _home = Path.Combine(Path.GetTempPath(), "agent-one-tui-" + Guid.NewGuid().ToString("N")[..8]);
        Environment.SetEnvironmentVariable(AppPaths.HomeEnvVar, _home);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppPaths.HomeEnvVar, _previous);
        try { Directory.Delete(_home, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private static ConfigTuiModel New() => new(new AgentConfig());

    private static ConsoleKeyInfo Key(ConsoleKey key) => new('\0', key, false, false, false);
    private static ConsoleKeyInfo Ch(char c) => new(c, ConsoleKey.NoName, false, false, false);

    private static void Type(ConfigTuiModel model, string text)
    {
        foreach (var c in text) model.HandleKey(Ch(c));
    }

    private static void SelectKey(ConfigTuiModel model, string key)
    {
        while (model.SelectedKey != key) model.HandleKey(Key(ConsoleKey.DownArrow));
    }

    [Fact]
    public void StartsOnTheFirstKeyAndIsClean()
    {
        var model = New();
        Assert.Equal(AgentConfig.Keys[0], model.SelectedKey);
        Assert.False(model.Dirty);
        Assert.False(model.Editing);
    }

    [Fact]
    public void ArrowsMoveAndWrapAround()
    {
        var model = New();
        var last = AgentConfig.Keys[^1];

        model.HandleKey(Key(ConsoleKey.UpArrow));
        Assert.Equal(last, model.SelectedKey);

        model.HandleKey(Key(ConsoleKey.DownArrow));
        Assert.Equal(AgentConfig.Keys[0], model.SelectedKey);
    }

    [Fact]
    public void HomeAndEndJumpToTheEnds()
    {
        var model = New();
        model.HandleKey(Key(ConsoleKey.End));
        Assert.Equal(AgentConfig.Keys[^1], model.SelectedKey);

        model.HandleKey(Key(ConsoleKey.Home));
        Assert.Equal(AgentConfig.Keys[0], model.SelectedKey);
    }

    [Fact]
    public void EnterEditsAndPrefillsTheCurrentValue()
    {
        // A free-text key: `model` is the one row where Enter opens the picker
        // instead (see ModelPickerTests).
        var model = New();
        SelectKey(model, "apiKeyEnv");
        model.HandleKey(Key(ConsoleKey.Enter));

        Assert.True(model.Editing);
        Assert.Equal("OPENAI_API_KEY", model.EditBuffer);
    }

    [Fact]
    public void TypingThenEnterCommitsTheValue()
    {
        var model = New();
        SelectKey(model, "apiKeyEnv");
        model.HandleKey(Key(ConsoleKey.Enter));

        // Clear the prefill, then type a new value.
        for (int i = 0; i < 20; i++) model.HandleKey(Key(ConsoleKey.Backspace));
        Type(model, "MY_LOCAL_KEY");
        model.HandleKey(Key(ConsoleKey.Enter));

        Assert.False(model.Editing);
        Assert.Equal("MY_LOCAL_KEY", model.Value("apiKeyEnv"));
        Assert.True(model.Dirty);
    }

    [Fact]
    public void EscapeAbandonsTheEditAndKeepsTheOldValue()
    {
        var model = New();
        SelectKey(model, "apiKeyEnv");
        model.HandleKey(Key(ConsoleKey.Enter));
        Type(model, "-typo");
        model.HandleKey(Key(ConsoleKey.Escape));

        Assert.False(model.Editing);
        Assert.Equal("OPENAI_API_KEY", model.Value("apiKeyEnv"));
        Assert.False(model.Dirty);
    }

    [Fact]
    public void InvalidValueKeepsYouInEditModeWithTheBufferIntact()
    {
        var model = New();
        SelectKey(model, "maxSteps");
        model.HandleKey(Key(ConsoleKey.Enter));
        for (int i = 0; i < 5; i++) model.HandleKey(Key(ConsoleKey.Backspace));
        Type(model, "999");
        model.HandleKey(Key(ConsoleKey.Enter));

        Assert.True(model.Editing);            // still editing — fix it, don't retype
        Assert.Equal("999", model.EditBuffer);
        Assert.StartsWith("✗", model.Status);
        Assert.Equal("8", model.Value("maxSteps"));
    }

    [Fact]
    public void ControlCharactersNeverEnterTheBuffer()
    {
        var model = New();
        model.HandleKey(Key(ConsoleKey.Enter));
        var before = model.EditBuffer;

        model.HandleKey(new ConsoleKeyInfo('\t', ConsoleKey.Tab, false, false, false));
        model.HandleKey(new ConsoleKeyInfo('\0', ConsoleKey.F5, false, false, false));

        Assert.Equal(before, model.EditBuffer);
    }

    [Theory]
    [InlineData("provider", "echo", "openai")]
    [InlineData("saveSessions", "true", "false")]
    public void ArrowsCycleEnumerableKeys(string key, string from, string to)
    {
        var model = New();
        SelectKey(model, key);
        Assert.Equal(from, model.Value(key));

        model.HandleKey(Key(ConsoleKey.RightArrow));
        Assert.Equal(to, model.Value(key));

        model.HandleKey(Key(ConsoleKey.LeftArrow));
        Assert.Equal(from, model.Value(key));
    }

    [Fact]
    public void CyclingAFreeTextKeySaysSoInsteadOfChangingIt()
    {
        var model = New();
        SelectKey(model, "model");
        model.HandleKey(Key(ConsoleKey.RightArrow));

        Assert.Equal("gpt-4o-mini", model.Value("model"));
        Assert.Contains("free text", model.Status);
    }

    [Fact]
    public void SaveWritesToDiskAndClearsDirty()
    {
        var model = New();
        SelectKey(model, "provider");
        model.HandleKey(Key(ConsoleKey.RightArrow));
        Assert.True(model.Dirty);

        model.HandleKey(Key(ConsoleKey.S));

        Assert.False(model.Dirty);
        Assert.Equal("openai", ConfigStore.Load().Provider);
    }

    [Fact]
    public void ReloadDiscardsUnsavedEdits()
    {
        var model = New();
        SelectKey(model, "provider");
        model.HandleKey(Key(ConsoleKey.RightArrow));
        model.HandleKey(Key(ConsoleKey.R));

        Assert.Equal("echo", model.Value("provider"));
        Assert.False(model.Dirty);
    }

    [Fact]
    public void DefaultsAreRestoredInMemoryAndStillNeedSaving()
    {
        var model = New();
        SelectKey(model, "provider");
        model.HandleKey(Key(ConsoleKey.RightArrow));
        model.HandleKey(Key(ConsoleKey.S));          // openai is now on disk
        model.HandleKey(Key(ConsoleKey.D));

        Assert.Equal("echo", model.Value("provider"));
        Assert.True(model.Dirty);
        Assert.Equal("openai", ConfigStore.Load().Provider);   // disk untouched until save
    }

    [Fact]
    public void QuittingCleanExitsAtOnce()
    {
        var model = New();
        Assert.Equal(TuiEffect.Quit, model.HandleKey(Key(ConsoleKey.Q)));
    }

    [Fact]
    public void QuittingDirtyNeedsConfirmation()
    {
        var model = New();
        SelectKey(model, "provider");
        model.HandleKey(Key(ConsoleKey.RightArrow));

        Assert.Equal(TuiEffect.None, model.HandleKey(Key(ConsoleKey.Q)));
        Assert.Contains("unsaved", model.Status);

        Assert.Equal(TuiEffect.Quit, model.HandleKey(Key(ConsoleKey.Q)));
    }

    [Fact]
    public void AnyOtherKeyDisarmsTheDiscardConfirmation()
    {
        var model = New();
        SelectKey(model, "provider");
        model.HandleKey(Key(ConsoleKey.RightArrow));
        model.HandleKey(Key(ConsoleKey.Q));          // armed
        model.HandleKey(Key(ConsoleKey.DownArrow));  // changed my mind

        Assert.Equal(TuiEffect.None, model.HandleKey(Key(ConsoleKey.Q)));
    }

    [Fact]
    public void SavingDisarmsTheDiscardConfirmation()
    {
        var model = New();
        SelectKey(model, "provider");
        model.HandleKey(Key(ConsoleKey.RightArrow));
        model.HandleKey(Key(ConsoleKey.Q));
        model.HandleKey(Key(ConsoleKey.S));

        Assert.False(model.QuitArmed);
        Assert.Equal(TuiEffect.Quit, model.HandleKey(Key(ConsoleKey.Q)));
    }

    [Fact]
    public async Task TestKeyRunsTheProbeAndReportsItsMessage()
    {
        var model = New();
        model.ConnectionTest = (_, _) => Task.FromResult("✓ reached it");

        Assert.Equal(TuiEffect.RunTest, model.HandleKey(Key(ConsoleKey.T)));
        Assert.True(model.Busy);
        Assert.Contains("testing", model.Status);

        model.CompleteTest(await model.ConnectionTest(model.Config, CancellationToken.None));

        Assert.False(model.Busy);
        Assert.Equal("✓ reached it", model.Status);
    }

    [Fact]
    public void KeysAreIgnoredWhileAProbeIsInFlightExceptQuit()
    {
        var model = New();
        model.HandleKey(Key(ConsoleKey.T));

        var selected = model.SelectedKey;
        model.HandleKey(Key(ConsoleKey.DownArrow));
        Assert.Equal(selected, model.SelectedKey);

        Assert.Equal(TuiEffect.Quit, model.HandleKey(Key(ConsoleKey.Q)));
    }

    [Fact]
    public void EditModeLetsSAndQBeTypedRatherThanActingAsCommands()
    {
        var model = New();
        SelectKey(model, "apiKeyEnv");
        model.HandleKey(Key(ConsoleKey.Enter));
        for (int i = 0; i < 20; i++) model.HandleKey(Key(ConsoleKey.Backspace));
        Type(model, "qs");

        Assert.True(model.Editing);
        Assert.Equal("qs", model.EditBuffer);
    }

    [Fact]
    public void EveryKeyHasAHint()
    {
        var model = New();
        foreach (var key in AgentConfig.Keys)
        {
            SelectKey(model, key);
            Assert.False(string.IsNullOrWhiteSpace(model.Hint()), key);
        }
    }

    [Fact]
    public void LoadPicksUpWhatIsOnDisk()
    {
        var config = new AgentConfig();
        config.TrySet("model", "stored-model", out _);
        ConfigStore.Save(config);

        var model = ConfigTuiModel.Load();
        Assert.Equal("stored-model", model.Value("model"));
        Assert.False(model.Dirty);
    }
}
