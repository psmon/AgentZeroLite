using AgentOne.Services;
using AgentOne.Tui;

namespace AgentOne.Tests;

/// <summary>
/// Where the API key lives and how it is found. These exist because of a real
/// incident: the settings screen offered only `apiKeyEnv`, someone pasted their
/// key into it, and the only symptom was a 401 that blamed nothing in
/// particular. The key now has a field of its own, and the variable-name field
/// refuses to hold a secret.
/// </summary>
[Collection(AgentOneHomeCollection.Name)]
public class ApiKeyTests : IDisposable
{
    private readonly string _home;
    private readonly string? _previous;

    public ApiKeyTests()
    {
        _previous = Environment.GetEnvironmentVariable(AppPaths.HomeEnvVar);
        _home = Path.Combine(Path.GetTempPath(), "agent-one-key-" + Guid.NewGuid().ToString("N")[..8]);
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

    // --- the field that started it ---------------------------------------

    [Theory]
    [InlineData("OPENAI_API_KEY")]
    [InlineData("A1_KEY")]
    [InlineData("_private")]
    [InlineData("KEY2")]
    public void RealVariableNamesAreAccepted(string name)
    {
        Assert.True(ApiKey.LooksLikeVariableName(name));

        var config = new AgentConfig();
        Assert.True(config.TrySet("apiKeyEnv", name, out var error), error);
    }

    [Theory]
    [InlineData("sk-lm-EwcSPvgd:jlcCfY7JcUMVn4PMKpvc")]   // the shape that caused this
    [InlineData("sk-proj-abc123")]
    [InlineData("has space")]
    [InlineData("2LEADING_DIGIT")]
    [InlineData("")]
    public void APastedKeyIsRefusedByTheVariableNameField(string pasted)
    {
        Assert.False(ApiKey.LooksLikeVariableName(pasted));

        var config = new AgentConfig();
        Assert.False(config.TrySet("apiKeyEnv", pasted, out var error));
        Assert.NotEqual("", error);
    }

    [Fact]
    public void TheRefusalPointsAtWhereTheKeyShouldGo()
    {
        var config = new AgentConfig();
        config.TrySet("apiKeyEnv", "sk-lm-whatever", out var error);

        Assert.Contains("NAME", error);
        Assert.Contains("auth set", error);
    }

    [Fact]
    public void AConfigFileCarryingAPastedKeyLoadsWithAWarning()
    {
        Directory.CreateDirectory(_home);
        File.WriteAllText(AppPaths.ConfigPath,
            """{"provider":"openai","apiKeyEnv":"sk-lm-EwcSPvgd:jlcCfY7JcUMVn4PMKpvc"}""");

        ConfigStore.Load(out var warning);

        Assert.Contains("not a variable name", warning);
        Assert.Contains("auth import", warning);
    }

    // --- the store --------------------------------------------------------

    [Fact]
    public void AStoredKeyComesBack()
    {
        CredentialStore.Save("sk-test-1234567890");
        Assert.Equal("sk-test-1234567890", CredentialStore.Load());
    }

    [Fact]
    public void NoFileMeansNoKeyRatherThanAnError()
    {
        Assert.Null(CredentialStore.Load());
    }

    [Fact]
    public void ClearForgetsIt()
    {
        CredentialStore.Save("sk-test-1234567890");
        CredentialStore.Clear();
        Assert.Null(CredentialStore.Load());
    }

    [Fact]
    public void TheKeyIsNeverWrittenToConfigJson()
    {
        CredentialStore.Save("sk-secret-value-here");
        ConfigStore.Save(new AgentConfig());

        Assert.DoesNotContain("sk-secret", File.ReadAllText(AppPaths.ConfigPath));
    }

    [Theory]
    [InlineData(null, "(none)")]
    [InlineData("", "(none)")]
    [InlineData("short", "•••••")]
    [InlineData("sk-lm-EwcSPvgd", "sk-lm-…vgd")]
    public void MaskingShowsEnoughToRecogniseAndNotEnoughToUse(string? key, string expected)
    {
        var masked = CredentialStore.Mask(key);
        Assert.Equal(expected.Length > 0, masked.Length > 0);
        if (key is { Length: > 12 })
        {
            Assert.DoesNotContain(key[6..^4], masked);
            Assert.StartsWith(key[..6], masked);
        }
    }

    // --- resolution order -------------------------------------------------

    [Fact]
    public void TheStoredKeyWinsOverTheEnvironment()
    {
        var config = new AgentConfig();
        config.TrySet("apiKeyEnv", "AGENT_ONE_TEST_KEY", out _);
        Environment.SetEnvironmentVariable("AGENT_ONE_TEST_KEY", "from-env");
        CredentialStore.Save("from-store");

        try
        {
            var resolved = ApiKey.Resolve(config);
            Assert.Equal("from-store", resolved.Value);
            Assert.Equal("credentials.json", resolved.Source);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENT_ONE_TEST_KEY", null);
        }
    }

    [Fact]
    public void TheEnvironmentIsTheFallback()
    {
        var config = new AgentConfig();
        config.TrySet("apiKeyEnv", "AGENT_ONE_TEST_KEY", out _);
        Environment.SetEnvironmentVariable("AGENT_ONE_TEST_KEY", "from-env");

        try
        {
            var resolved = ApiKey.Resolve(config);
            Assert.Equal("from-env", resolved.Value);
            Assert.Equal("$AGENT_ONE_TEST_KEY", resolved.Source);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENT_ONE_TEST_KEY", null);
        }
    }

    [Fact]
    public void NeitherMeansNotFound()
    {
        var config = new AgentConfig();
        config.TrySet("apiKeyEnv", "AGENT_ONE_DEFINITELY_UNSET", out _);

        var resolved = ApiKey.Resolve(config);

        Assert.False(resolved.Found);
        Assert.Equal("none", resolved.Source);
        Assert.Contains("auth set", ApiKey.WhereToPutIt(config));
    }

    // --- the screen -------------------------------------------------------

    [Fact]
    public void TheConnectionStepOffersAKeyRow()
    {
        Assert.Contains(ConfigTuiModel.ApiKeyField, ConfigTuiModel.StepFields[0]);
    }

    [Fact]
    public void TheKeyRowShowsAMaskNotTheKey()
    {
        CredentialStore.Save("sk-test-abcdefghijklmnop");
        var model = new ConfigTuiModel(new AgentConfig());

        var shown = model.Value(ConfigTuiModel.ApiKeyField);

        Assert.DoesNotContain("abcdefghij", shown);
        Assert.Contains("…", shown);
    }

    [Fact]
    public void EditingTheKeyRowStartsEmptyRatherThanFromTheMask()
    {
        CredentialStore.Save("sk-test-abcdefghijklmnop");
        var model = new ConfigTuiModel(new AgentConfig());
        while (model.SelectedKey != ConfigTuiModel.ApiKeyField) model.HandleKey(Key(ConsoleKey.DownArrow));

        model.HandleKey(Key(ConsoleKey.Enter));

        Assert.True(model.Editing);
        Assert.Equal("", model.EditBuffer);
    }

    [Fact]
    public void ATypedKeyIsHeldUntilSaveAndThenStored()
    {
        var model = new ConfigTuiModel(new AgentConfig());
        while (model.SelectedKey != ConfigTuiModel.ApiKeyField) model.HandleKey(Key(ConsoleKey.DownArrow));

        model.HandleKey(Key(ConsoleKey.Enter));
        foreach (var c in "sk-typed-into-the-tui") model.HandleKey(Ch(c));
        model.HandleKey(Key(ConsoleKey.Enter));

        Assert.True(model.Dirty);
        Assert.Null(CredentialStore.Load());            // not written yet

        model.Save();

        Assert.Equal("sk-typed-into-the-tui", CredentialStore.Load());
        Assert.False(model.Dirty);
    }

    [Fact]
    public void ReloadDropsAKeyThatWasTypedButNotSaved()
    {
        var model = new ConfigTuiModel(new AgentConfig());
        while (model.SelectedKey != ConfigTuiModel.ApiKeyField) model.HandleKey(Key(ConsoleKey.DownArrow));

        model.HandleKey(Key(ConsoleKey.Enter));
        foreach (var c in "sk-abandoned") model.HandleKey(Ch(c));
        model.HandleKey(Key(ConsoleKey.Enter));

        model.Reload();

        Assert.False(model.Dirty);
        Assert.Null(CredentialStore.Load());
    }

    [Fact]
    public void AnEmptyEntryLeavesTheKeyAlone()
    {
        CredentialStore.Save("sk-existing-key-value");
        var model = new ConfigTuiModel(new AgentConfig());
        while (model.SelectedKey != ConfigTuiModel.ApiKeyField) model.HandleKey(Key(ConsoleKey.DownArrow));

        model.HandleKey(Key(ConsoleKey.Enter));
        model.HandleKey(Key(ConsoleKey.Enter));         // accept nothing

        Assert.False(model.Dirty);
        Assert.Equal("sk-existing-key-value", CredentialStore.Load());
    }
}
