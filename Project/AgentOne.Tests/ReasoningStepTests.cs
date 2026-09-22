using AgentOne.Services;
using AgentOne.Tui;

namespace AgentOne.Tests;

/// <summary>
/// The Reasoning step: a second, stronger model that hard questions escalate
/// to. Its endpoint and key default to the Connection step's, its model is
/// picked from the endpoint like the everyday one, and an empty model means
/// "never escalate".
/// </summary>
[Collection(AgentOneHomeCollection.Name)]
public class ReasoningStepTests : IDisposable
{
    private readonly string _home;
    private readonly string? _previous;

    public ReasoningStepTests()
    {
        _previous = Environment.GetEnvironmentVariable(AppPaths.HomeEnvVar);
        _home = Path.Combine(Path.GetTempPath(), "agent-one-reason-" + Guid.NewGuid().ToString("N")[..8]);
        Environment.SetEnvironmentVariable(AppPaths.HomeEnvVar, _home);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppPaths.HomeEnvVar, _previous);
        try { Directory.Delete(_home, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private static ConsoleKeyInfo Key(ConsoleKey key) => new('\0', key, false, false, false);
    private static ConsoleKeyInfo Ch(char c) => new(c, ConsoleKey.NoName, false, false, false);

    private static ConfigTuiModel OnReasoningStep(AgentConfig? config = null)
    {
        var model = new ConfigTuiModel(config ?? new AgentConfig());
        model.JumpToStep(ConfigStep.Reasoning);
        return model;
    }

    // --- the config -------------------------------------------------------

    [Fact]
    public void TheDerivedConfigInheritsEndpointAndKeyWhenTheStepLeftThemEmpty()
    {
        var config = new AgentConfig();
        config.TrySet("baseUrl", "https://gateway.example.com/v1", out _);
        config.TrySet("reasoningModel", "big-model", out _);

        var derived = config.ForReasoning();

        Assert.Equal("https://gateway.example.com/v1", derived.BaseUrl);
        Assert.Equal("big-model", derived.Model);
        Assert.Equal(CredentialStore.Slot.Reasoning, derived.KeySlot);
        Assert.Equal(CredentialStore.Slot.Provider, config.KeySlot);      // the original is untouched
    }

    [Fact]
    public void ItsOwnEndpointWinsWhenSet()
    {
        var config = new AgentConfig();
        config.TrySet("reasoningBaseUrl", "https://big.example.com/v1/", out _);
        config.TrySet("reasoningModel", "big-model", out _);

        Assert.Equal("https://big.example.com/v1", config.ForReasoning().BaseUrl);
    }

    [Fact]
    public void AnEmptyModelMeansNothingIsEscalated()
    {
        var config = new AgentConfig();
        Assert.False(config.HasReasoningModel);

        config.TrySet("reasoningModel", "  big-model ", out _);
        Assert.True(config.HasReasoningModel);
        Assert.Equal("big-model", config.ReasoningModel);

        Assert.True(config.TrySet("reasoningModel", "", out _));
        Assert.False(config.HasReasoningModel);
    }

    [Fact]
    public void TheEndpointMustBeAUrlOrEmpty()
    {
        var config = new AgentConfig();

        Assert.True(config.TrySet("reasoningBaseUrl", "", out _));
        Assert.False(config.TrySet("reasoningBaseUrl", "not a url", out var error));
        Assert.Contains("absolute URL", error);
    }

    [Fact]
    public void TheReasoningKeyFallsBackToTheProviderKey()
    {
        CredentialStore.Save("sk-provider", CredentialStore.Slot.Provider);
        var config = new AgentConfig();
        config.TrySet("reasoningModel", "big-model", out _);

        var resolved = ApiKey.Resolve(config.ForReasoning());
        Assert.Equal("sk-provider", resolved.Value);

        CredentialStore.Save("sk-reasoning", CredentialStore.Slot.Reasoning);
        resolved = ApiKey.Resolve(config.ForReasoning());
        Assert.Equal("sk-reasoning", resolved.Value);
        Assert.Contains("reasoning", resolved.Source);

        // The everyday config never sees the reasoning key.
        Assert.Equal("sk-provider", ApiKey.Resolve(config).Value);
    }

    [Fact]
    public void ClearingTheReasoningKeyLeavesTheOthers()
    {
        CredentialStore.Save("sk-provider", CredentialStore.Slot.Provider);
        CredentialStore.Save("sk-reasoning", CredentialStore.Slot.Reasoning);

        CredentialStore.Clear(CredentialStore.Slot.Reasoning);

        Assert.Null(CredentialStore.Load(CredentialStore.Slot.Reasoning));
        Assert.Equal("sk-provider", CredentialStore.Load(CredentialStore.Slot.Provider));
    }

    // --- the step ---------------------------------------------------------

    [Fact]
    public void TheStepSitsBetweenModelAndOptions()
    {
        var model = new ConfigTuiModel(new AgentConfig());
        model.JumpToStep(ConfigStep.Reasoning);

        Assert.Equal(ConfigStep.Reasoning, model.Step);
        Assert.Equal(["reasoningBaseUrl", "reasoningApiKey", "reasoningModel"], model.Fields);

        model.HandleKey(Key(ConsoleKey.Tab));
        Assert.Equal(ConfigStep.Options, model.Step);
    }

    [Fact]
    public void AnUnsetKeyRowSaysItUsesTheProviderKey()
    {
        var model = OnReasoningStep();

        Assert.Equal("(same as provider key)", model.Value(ConfigTuiModel.ReasoningApiKeyField));
    }

    [Fact]
    public void ProbesOnThisStepTargetTheReasoningModel()
    {
        var config = new AgentConfig();
        config.TrySet("reasoningModel", "big-model", out _);
        var model = OnReasoningStep(config);

        Assert.Equal("big-model", model.ProbeTarget.Model);
        Assert.Equal(CredentialStore.Slot.Reasoning, model.ProbeTarget.KeySlot);

        model.JumpToStep(ConfigStep.Connection);
        Assert.Same(model.Config, model.ProbeTarget);
    }

    [Fact]
    public void TestingWithoutAModelIsRefusedWithAHint()
    {
        var model = OnReasoningStep();

        Assert.Equal(TuiEffect.None, model.HandleKey(Key(ConsoleKey.T)));
        Assert.StartsWith("✗", model.Status);
        Assert.False(model.Busy);
    }

    [Fact]
    public void EnterOnTheModelRowAsksTheEndpointAndAPickLandsInReasoningModel()
    {
        var model = OnReasoningStep();
        model.HandleKey(Key(ConsoleKey.End));                       // reasoningModel
        Assert.Equal(ConfigTuiModel.ReasoningModelField, model.SelectedKey);

        Assert.Equal(TuiEffect.FetchModels, model.HandleKey(Key(ConsoleKey.Enter)));
        Assert.True(model.Busy);

        model.CompleteModelFetch(Llm.ModelCatalogResult.Success(["small", "big"], "2 models"));
        Assert.True(model.Picking);
        Assert.Equal(ConfigStep.Reasoning, model.Step);             // an overlay, not a step change

        model.HandleKey(Key(ConsoleKey.DownArrow));
        model.HandleKey(Key(ConsoleKey.Enter));
        Assert.Equal("big", model.Value(ConfigTuiModel.ReasoningModelField));
        Assert.Equal("gpt-4o-mini", model.Value("model"));          // the everyday model is untouched

        model.HandleKey(Key(ConsoleKey.Escape));
        Assert.False(model.Picking);
        Assert.Equal(ConfigStep.Reasoning, model.Step);
    }

    [Fact]
    public void ETypesAModelIdByHand()
    {
        var model = OnReasoningStep();
        model.HandleKey(Key(ConsoleKey.End));

        model.HandleKey(Key(ConsoleKey.E));
        Assert.True(model.Editing);
        foreach (var c in "o3") model.HandleKey(Ch(c));
        model.HandleKey(Key(ConsoleKey.Enter));

        Assert.Equal("o3", model.Config.ReasoningModel);
        Assert.True(model.Dirty);
    }

    [Fact]
    public void TheReasoningKeyIsTypedMaskedAndStoredOnSave()
    {
        var model = OnReasoningStep();
        model.HandleKey(Key(ConsoleKey.DownArrow));                 // reasoningApiKey
        Assert.Equal(ConfigTuiModel.ReasoningApiKeyField, model.SelectedKey);

        model.HandleKey(Key(ConsoleKey.Enter));
        foreach (var c in "sk-reasoning-secret-value") model.HandleKey(Ch(c));
        model.HandleKey(Key(ConsoleKey.Enter));

        Assert.DoesNotContain("secret", model.Value(ConfigTuiModel.ReasoningApiKeyField));
        Assert.Null(CredentialStore.Load(CredentialStore.Slot.Reasoning));

        model.Save();
        Assert.Equal("sk-reasoning-secret-value", CredentialStore.Load(CredentialStore.Slot.Reasoning));
    }
}
