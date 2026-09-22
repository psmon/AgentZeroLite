using AgentOne.Services;
using AgentOne.Tui;

namespace AgentOne.Tests;

/// <summary>
/// The Smart step: a second service, a second key, and a health check that
/// makes one real call rather than checking that a file exists. Smart mode
/// itself does not exist yet — this is the setting that will feed it.
/// </summary>
[Collection(AgentOneHomeCollection.Name)]
public class SmartStepTests : IDisposable
{
    private readonly string _home;
    private readonly string? _previous;
    private readonly string? _previousEnv;

    public SmartStepTests()
    {
        _previous = Environment.GetEnvironmentVariable(AppPaths.HomeEnvVar);
        _previousEnv = Environment.GetEnvironmentVariable("TYPESAFE_API_KEY");
        Environment.SetEnvironmentVariable("TYPESAFE_API_KEY", null);

        _home = Path.Combine(Path.GetTempPath(), "agent-one-smart-" + Guid.NewGuid().ToString("N")[..8]);
        Environment.SetEnvironmentVariable(AppPaths.HomeEnvVar, _home);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppPaths.HomeEnvVar, _previous);
        Environment.SetEnvironmentVariable("TYPESAFE_API_KEY", _previousEnv);
        try { Directory.Delete(_home, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private static ConsoleKeyInfo Key(ConsoleKey key) => new('\0', key, false, false, false);
    private static ConsoleKeyInfo Ch(char c) => new(c, ConsoleKey.NoName, false, false, false);

    private static ConfigTuiModel OnSmartStep()
    {
        var model = new ConfigTuiModel(new AgentConfig());
        model.JumpToStep(ConfigStep.Smart);
        return model;
    }

    private static void SelectField(ConfigTuiModel model, string field)
    {
        while (model.SelectedKey != field) model.HandleKey(Key(ConsoleKey.DownArrow));
    }

    // --- two keys, one file ----------------------------------------------

    [Fact]
    public void StoringTheTypeSafeKeyDoesNotDeleteTheProviderKey()
    {
        CredentialStore.Save("sk-provider-key-value");
        CredentialStore.Save("ts-typesafe-key-value", CredentialStore.Slot.Jev);

        Assert.Equal("sk-provider-key-value", CredentialStore.Load());
        Assert.Equal("ts-typesafe-key-value", CredentialStore.Load(CredentialStore.Slot.Jev));
    }

    [Fact]
    public void ClearingOneKeyKeepsTheOther()
    {
        CredentialStore.Save("sk-provider-key-value");
        CredentialStore.Save("ts-typesafe-key-value", CredentialStore.Slot.Jev);

        CredentialStore.Clear(CredentialStore.Slot.Jev);

        Assert.Equal("sk-provider-key-value", CredentialStore.Load());
        Assert.Null(CredentialStore.Load(CredentialStore.Slot.Jev));
    }

    [Fact]
    public void NeitherKeyEverReachesConfigJson()
    {
        CredentialStore.Save("sk-provider-secret", CredentialStore.Slot.Provider);
        CredentialStore.Save("ts-typesafe-secret", CredentialStore.Slot.Jev);
        ConfigStore.Save(new AgentConfig());

        var config = File.ReadAllText(AppPaths.ConfigPath);
        Assert.DoesNotContain("sk-provider-secret", config);
        Assert.DoesNotContain("ts-typesafe-secret", config);
    }

    // --- the step ---------------------------------------------------------

    [Fact]
    public void TheStackHasAFourthStep()
    {
        Assert.Equal(["Connection", "Model", "Options", "Smart"], ConfigTuiModel.StepTitles);
        Assert.Contains(ConfigTuiModel.JevApiKeyField, ConfigTuiModel.StepFields[3]);
    }

    [Fact]
    public void TheSmartStepIsReachedByTabbingPastOptions()
    {
        var model = new ConfigTuiModel(new AgentConfig());
        model.JumpToStep(ConfigStep.Options);

        model.HandleKey(Key(ConsoleKey.Tab));

        Assert.Equal(ConfigStep.Smart, model.Step);
    }

    [Fact]
    public void SmartIsTheLastStep()
    {
        var model = OnSmartStep();
        model.HandleKey(Key(ConsoleKey.Tab));

        Assert.Equal(ConfigStep.Smart, model.Step);
        Assert.Contains("last step", model.Status);
    }

    [Fact]
    public void TheKeyRowShowsAMaskAndIsMarkedMissingUntilItIsSet()
    {
        var model = OnSmartStep();
        Assert.False(model.SmartKeyPresent);
        Assert.Equal("(none)", model.Value(ConfigTuiModel.JevApiKeyField));

        CredentialStore.Save("ts-abcdefghijklmnop", CredentialStore.Slot.Jev);

        var reloaded = OnSmartStep();
        Assert.True(reloaded.SmartKeyPresent);
        Assert.DoesNotContain("abcdefghij", reloaded.Value(ConfigTuiModel.JevApiKeyField));
    }

    [Fact]
    public void ATypedTypeSafeKeyIsHeldUntilSaveAndThenStoredInItsOwnSlot()
    {
        var model = OnSmartStep();
        SelectField(model, ConfigTuiModel.JevApiKeyField);

        model.HandleKey(Key(ConsoleKey.Enter));
        foreach (var c in "ts-typed-in-the-tui") model.HandleKey(Ch(c));
        model.HandleKey(Key(ConsoleKey.Enter));

        Assert.True(model.Dirty);
        Assert.True(model.SmartKeyPresent);                                  // pending counts
        Assert.Null(CredentialStore.Load(CredentialStore.Slot.Jev));         // not written yet

        model.Save();

        Assert.Equal("ts-typed-in-the-tui", CredentialStore.Load(CredentialStore.Slot.Jev));
        Assert.Null(CredentialStore.Load());                                 // the other slot untouched
        Assert.False(model.Dirty);
    }

    [Fact]
    public void BothKeysCanBeTypedInOneVisitAndSaveWritesBoth()
    {
        var model = new ConfigTuiModel(new AgentConfig());

        SelectField(model, ConfigTuiModel.ApiKeyField);
        model.HandleKey(Key(ConsoleKey.Enter));
        foreach (var c in "sk-one") model.HandleKey(Ch(c));
        model.HandleKey(Key(ConsoleKey.Enter));

        model.JumpToStep(ConfigStep.Smart);
        SelectField(model, ConfigTuiModel.JevApiKeyField);
        model.HandleKey(Key(ConsoleKey.Enter));
        foreach (var c in "ts-two") model.HandleKey(Ch(c));
        model.HandleKey(Key(ConsoleKey.Enter));

        model.Save();

        Assert.Equal("sk-one", CredentialStore.Load());
        Assert.Equal("ts-two", CredentialStore.Load(CredentialStore.Slot.Jev));
    }

    [Fact]
    public void TheEnvironmentVariableCountsAsAKey()
    {
        Environment.SetEnvironmentVariable("TYPESAFE_API_KEY", "ts-from-the-environment");
        Assert.True(OnSmartStep().SmartKeyPresent);
    }

    // --- the health check --------------------------------------------------

    [Fact]
    public async Task HPerformsTheCheckAndReportsWhatCameBack()
    {
        var model = OnSmartStep();
        model.SmartCheck = (_, _) => Task.FromResult("✓ jev-1.13.0 · 180 ms");

        Assert.Equal(TuiEffect.CheckSmart, model.HandleKey(Key(ConsoleKey.H)));
        Assert.True(model.Busy);

        model.CompleteTest(await model.SmartCheck(model.Config, CancellationToken.None));

        Assert.False(model.Busy);
        Assert.StartsWith("✓", model.Status);
    }

    [Fact]
    public void HDoesNothingOnTheOtherSteps()
    {
        var model = new ConfigTuiModel(new AgentConfig());     // Connection
        Assert.Equal(TuiEffect.None, model.HandleKey(Key(ConsoleKey.H)));
        Assert.False(model.Busy);
    }

    [Fact]
    public async Task WithNoKeyTheCheckSaysSoWithoutCallingAnything()
    {
        var config = new AgentConfig();
        config.TrySet("jevBaseUrl", "http://127.0.0.1:1/v1", out _);   // would refuse instantly if called

        var message = await ConfigTuiProbe.CheckSmartAsync(config, CancellationToken.None);

        Assert.StartsWith("✗", message);
        Assert.Contains("no TypeSafe key", message);
    }

    [Fact]
    public async Task AnUnreachableEndpointFailsCleanly()
    {
        CredentialStore.Save("ts-some-key", CredentialStore.Slot.Jev);

        var config = new AgentConfig();
        config.TrySet("jevBaseUrl", "http://127.0.0.1:1/v1", out _);
        config.TrySet("timeoutSeconds", "5", out _);

        var message = await ConfigTuiProbe.CheckSmartAsync(config, CancellationToken.None);

        Assert.StartsWith("✗", message);
        Assert.Contains("cannot reach", message);
    }

    // --- config ------------------------------------------------------------

    [Theory]
    [InlineData("jevBaseUrl", "https://api.typesafe.ai/v1")]
    [InlineData("jevModel", "jev-latest")]
    public void TheSmartSettingsHaveWorkingDefaults(string key, string expected)
    {
        Assert.Equal(expected, new AgentConfig().Get(key));
    }

    [Fact]
    public void AnInvalidJevBaseUrlIsRefused()
    {
        var config = new AgentConfig();
        Assert.False(config.TrySet("jevBaseUrl", "not-a-url", out var error));
        Assert.NotEqual("", error);
    }

    // --- the toggle, now that there is a loop behind it --------------------

    [Fact]
    public void SmartModeIsOffByDefault()
    {
        Assert.Equal("off", new AgentConfig().Get("smartMode"));
    }

    [Fact]
    public void TurningItOnWithNoKeyIsRefusedOutright()
    {
        var model = OnSmartStep();
        SelectField(model, "smartMode");

        var effect = model.HandleKey(Key(ConsoleKey.RightArrow));

        Assert.Equal(TuiEffect.None, effect);        // nothing to check
        Assert.Equal("off", model.Value("smartMode"));
        Assert.Contains("no TypeSafe key", model.Status);
    }

    [Fact]
    public void TurningItOnWithAKeyRunsTheCheckFirst()
    {
        CredentialStore.Save("ts-a-key", CredentialStore.Slot.Jev);
        var model = OnSmartStep();
        SelectField(model, "smartMode");

        var effect = model.HandleKey(Key(ConsoleKey.RightArrow));

        Assert.Equal(TuiEffect.CheckSmart, effect);
        Assert.True(model.Busy);
        Assert.Equal("off", model.Value("smartMode"));   // not on until the check answers
    }

    [Fact]
    public void AFailedCheckLeavesItOff()
    {
        CredentialStore.Save("ts-a-key", CredentialStore.Slot.Jev);
        var model = OnSmartStep();
        SelectField(model, "smartMode");
        model.HandleKey(Key(ConsoleKey.RightArrow));

        model.CompleteTest("✗ HTTP 401 — the TypeSafe key was rejected");

        Assert.Equal("off", model.Value("smartMode"));
        Assert.Contains("stays off", model.Status);
        Assert.False(model.Busy);
    }

    [Fact]
    public void APassedCheckTurnsItOn()
    {
        CredentialStore.Save("ts-a-key", CredentialStore.Slot.Jev);
        var model = OnSmartStep();
        SelectField(model, "smartMode");
        model.HandleKey(Key(ConsoleKey.RightArrow));

        model.CompleteTest("✓ jev-1.13.0 · 180 ms");

        Assert.Equal("on", model.Value("smartMode"));
        Assert.StartsWith("✓", model.Status);
        Assert.True(model.Dirty);                        // still needs saving
    }

    [Fact]
    public void TurningItOffNeedsNoCheck()
    {
        CredentialStore.Save("ts-a-key", CredentialStore.Slot.Jev);
        var model = OnSmartStep();
        SelectField(model, "smartMode");
        model.HandleKey(Key(ConsoleKey.RightArrow));
        model.CompleteTest("✓ fine");

        var effect = model.HandleKey(Key(ConsoleKey.LeftArrow));

        Assert.Equal(TuiEffect.None, effect);
        Assert.Equal("off", model.Value("smartMode"));
    }

    [Fact]
    public void AnOrdinaryCheckStillJustReportsItself()
    {
        CredentialStore.Save("ts-a-key", CredentialStore.Slot.Jev);
        var model = OnSmartStep();

        model.HandleKey(Key(ConsoleKey.H));
        model.CompleteTest("✓ jev-1.13.0 · 180 ms");

        Assert.Equal("off", model.Value("smartMode"));   // h alone does not enable it
        Assert.Equal("✓ jev-1.13.0 · 180 ms", model.Status);
    }

    [Theory]
    [InlineData("0.6", "0.60")]
    [InlineData("0", "0.00")]
    [InlineData("1", "1.00")]
    public void TheConfidenceFloorAcceptsTheRange(string value, string expected)
    {
        var config = new AgentConfig();
        Assert.True(config.TrySet("jevConfidenceFloor", value, out _));
        Assert.Equal(expected, config.Get("jevConfidenceFloor"));
    }

    [Theory]
    [InlineData("-0.1")]
    [InlineData("1.5")]
    [InlineData("high")]
    public void AFloorOutsideZeroToOneIsRefused(string value)
    {
        Assert.False(new AgentConfig().TrySet("jevConfidenceFloor", value, out _));
    }
}
