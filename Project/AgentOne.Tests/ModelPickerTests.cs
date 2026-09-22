using AgentOne.Llm;
using AgentOne.Services;
using AgentOne.Tui;

namespace AgentOne.Tests;

/// <summary>
/// Picking a model from what the endpoint actually offers. The listing is also
/// the health check, so the failure paths matter as much as the happy one: an
/// empty or rejected list has to say the key or the URL is wrong, and it must
/// never leave the screen stuck.
/// </summary>
[Collection(AgentOneHomeCollection.Name)]
public class ModelPickerTests : IDisposable
{
    private readonly string _home;
    private readonly string? _previous;

    public ModelPickerTests()
    {
        _previous = Environment.GetEnvironmentVariable(AppPaths.HomeEnvVar);
        _home = Path.Combine(Path.GetTempPath(), "agent-one-pick-" + Guid.NewGuid().ToString("N")[..8]);
        Environment.SetEnvironmentVariable(AppPaths.HomeEnvVar, _home);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppPaths.HomeEnvVar, _previous);
        try { Directory.Delete(_home, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private static ConsoleKeyInfo Key(ConsoleKey key) => new('\0', key, false, false, false);

    /// <summary>On the Model step, with the automatic fetch already consumed.</summary>
    private static ConfigTuiModel OnModelStep()
    {
        var model = new ConfigTuiModel(new AgentConfig());
        model.JumpToStep(ConfigStep.Model);
        model.CompleteModelFetch(ModelCatalogResult.Failure("not fetched in this test"));
        return model;
    }

    private static ModelCatalogResult Listing(params string[] models) =>
        ModelCatalogResult.Success(models, $"{models.Length} models from http://test/v1/models");

    [Fact]
    public void ArrivingAtTheModelStepAsksTheEndpoint()
    {
        var model = new ConfigTuiModel(new AgentConfig());

        Assert.Equal(TuiEffect.FetchModels, model.JumpToStep(ConfigStep.Model));
        Assert.True(model.Busy);
        Assert.Equal(ConfigStep.Model, model.Step);
    }

    [Fact]
    public void TabFromTheConnectionStepIsWhatTriggersIt()
    {
        var model = new ConfigTuiModel(new AgentConfig());

        Assert.Equal(TuiEffect.FetchModels, model.HandleKey(Key(ConsoleKey.Tab)));
        Assert.Equal(ConfigStep.Model, model.Step);
    }

    [Fact]
    public void EnterOnTheModelStepTypesAnIdByHand()
    {
        var model = OnModelStep();

        model.HandleKey(Key(ConsoleKey.Enter));

        Assert.True(model.Editing);
        Assert.Equal("gpt-4o-mini", model.EditBuffer);
    }

    [Fact]
    public void LListsFromAnyRow()
    {
        var model = new ConfigTuiModel(new AgentConfig());     // sitting on `provider`
        Assert.Equal(TuiEffect.FetchModels, model.HandleKey(Key(ConsoleKey.L)));
        Assert.True(model.Busy);
    }

    [Fact]
    public void AListingOpensThePickerWithTheCurrentModelHighlighted()
    {
        var model = OnModelStep();
        model.HandleKey(Key(ConsoleKey.Enter));
        model.CompleteModelFetch(Listing("a-model", "gpt-4o-mini", "z-model"));

        Assert.False(model.Busy);
        Assert.True(model.Picking);
        Assert.Equal("gpt-4o-mini", model.PickOptions[model.PickIndex]);   // the configured one
        Assert.StartsWith("✓", model.Status);
    }

    [Fact]
    public void TheManualEntryIsAlwaysOfferedLast()
    {
        var model = OnModelStep();
        model.CompleteModelFetch(Listing("only-one"));

        Assert.Equal(ConfigTuiModel.PickManualEntry, model.PickOptions[^1]);
        Assert.Equal(2, model.PickOptions.Count);
    }

    [Fact]
    public void ChoosingAModelSetsItAndKeepsTheListUp()
    {
        var model = OnModelStep();
        model.CompleteModelFetch(Listing("a-model", "gpt-4o-mini", "z-model"));

        model.HandleKey(Key(ConsoleKey.UpArrow));      // gpt-4o-mini -> a-model
        model.HandleKey(Key(ConsoleKey.Enter));

        Assert.Equal("a-model", model.Value("model"));
        Assert.True(model.Dirty);

        // The list is this step's body — it stays, with the choice now current,
        // so a mis-pick costs one keystroke rather than another round trip.
        Assert.True(model.Picking);
        Assert.Equal("a-model", model.PickOptions[model.PickIndex]);
    }

    [Fact]
    public void EscapeGoesBackAStepWithoutTouchingTheModel()
    {
        var model = OnModelStep();
        model.CompleteModelFetch(Listing("a-model", "gpt-4o-mini"));

        model.HandleKey(Key(ConsoleKey.UpArrow));       // move, but do not choose
        model.HandleKey(Key(ConsoleKey.Escape));

        Assert.Equal(ConfigStep.Connection, model.Step);
        Assert.Equal("gpt-4o-mini", model.Value("model"));
        Assert.False(model.Dirty);
    }

    [Fact]
    public void EscapeOnTheFirstStepStillQuits()
    {
        var model = new ConfigTuiModel(new AgentConfig());
        Assert.Equal(TuiEffect.Quit, model.HandleKey(Key(ConsoleKey.Escape)));
    }

    [Fact]
    public void ChoosingManualEntryFallsThroughToTheTextEditor()
    {
        var model = OnModelStep();
        model.CompleteModelFetch(Listing("a-model"));

        model.HandleKey(Key(ConsoleKey.End));          // the manual entry
        model.HandleKey(Key(ConsoleKey.Enter));

        Assert.True(model.Editing);
        Assert.Equal("gpt-4o-mini", model.EditBuffer); // prefilled with the current value
    }

    [Fact]
    public void PickerSelectionWrapsAround()
    {
        var model = OnModelStep();
        model.CompleteModelFetch(Listing("a", "b"));   // 3 rows with the manual entry

        model.HandleKey(Key(ConsoleKey.Home));
        model.HandleKey(Key(ConsoleKey.UpArrow));
        Assert.Equal(model.PickOptions.Count - 1, model.PickIndex);

        model.HandleKey(Key(ConsoleKey.DownArrow));
        Assert.Equal(0, model.PickIndex);
    }

    // --- the window the page draws ---------------------------------------

    [Fact]
    public void AShortListIsShownWhole()
    {
        var model = OnModelStep();
        model.CompleteModelFetch(Listing("a", "b"));    // 3 rows with the manual entry

        Assert.Equal((0, 3), model.PickWindow(8));
    }

    [Fact]
    public void ALongListIsWindowedAroundTheSelection()
    {
        var model = OnModelStep();
        model.CompleteModelFetch(Listing(Enumerable.Range(1, 40).Select(i => $"m{i:00}").ToArray()));

        // At the top the window starts at the top.
        model.HandleKey(Key(ConsoleKey.Home));
        Assert.Equal((0, 8), model.PickWindow(8));

        // At the bottom it stops at the bottom — never past the end.
        model.HandleKey(Key(ConsoleKey.End));
        var (first, count) = model.PickWindow(8);
        Assert.Equal(model.PickOptions.Count - 8, first);
        Assert.Equal(8, count);
        Assert.True(first + count <= model.PickOptions.Count);
    }

    [Fact]
    public void TheWindowAlwaysContainsTheSelection()
    {
        var model = OnModelStep();
        model.CompleteModelFetch(Listing(Enumerable.Range(1, 40).Select(i => $"m{i:00}").ToArray()));

        for (int step = 0; step < model.PickOptions.Count + 3; step++)
        {
            var (first, count) = model.PickWindow(8);
            Assert.InRange(model.PickIndex, first, first + count - 1);
            model.HandleKey(Key(ConsoleKey.DownArrow));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ARidiculousHeightYieldsAnEmptyWindowRatherThanThrowing(int height)
    {
        var model = OnModelStep();
        model.CompleteModelFetch(Listing("a", "b"));

        Assert.Equal((0, 0), model.PickWindow(height));
    }

    [Fact]
    public void TheWindowIsEmptyWhenNothingIsBeingPicked()
    {
        var model = OnModelStep();
        Assert.Equal((0, 0), model.PickWindow(8));
    }

    // --- the health-check half -------------------------------------------

    [Fact]
    public void ARejectedKeyIsReportedAsSuchAndOpensNoPicker()
    {
        var model = OnModelStep();
        model.HandleKey(Key(ConsoleKey.Enter));
        model.CompleteModelFetch(ModelCatalogResult.Failure(
            "HTTP 401 — the endpoint rejected the key in $OPENAI_API_KEY"));

        Assert.False(model.Busy);
        Assert.False(model.Picking);
        Assert.StartsWith("✗", model.Status);
        Assert.Contains("401", model.Status);
        Assert.Contains("OPENAI_API_KEY", model.Status);
    }

    [Fact]
    public void AnEmptyListIsAFailureNotAnEmptyPicker()
    {
        var model = OnModelStep();
        model.CompleteModelFetch(ModelCatalogResult.Success([], "answered, but listed no models"));

        Assert.False(model.Picking);
        Assert.StartsWith("✗", model.Status);
    }

    [Fact]
    public void EveryFailureNamesBothSuspects()
    {
        var model = OnModelStep();
        model.CompleteModelFetch(ModelCatalogResult.Failure("cannot reach http://nope/v1/models"));

        Assert.Contains("baseUrl", model.Status);
        Assert.Contains("$" + model.Config.ApiKeyEnv, model.Status);
    }

    [Fact]
    public void AFailedListingLeavesTheStepUsable()
    {
        var model = OnModelStep();
        model.CompleteModelFetch(ModelCatalogResult.Failure("boom"));

        Assert.False(model.Busy);
        Assert.False(model.Picking);

        // Typing an id by hand still works, and so does stepping back.
        model.HandleKey(Key(ConsoleKey.E));
        Assert.True(model.Editing);
        model.HandleKey(Key(ConsoleKey.Escape));

        model.HandleKey(Key(ConsoleKey.B));
        Assert.Equal(ConfigStep.Connection, model.Step);
    }

    [Fact]
    public async Task TheEchoProviderListsItselfSoTheFlowWorksOffline()
    {
        var config = new AgentConfig();                // provider = echo
        var result = await ConfigTuiProbe.ListModelsAsync(config, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(["echo"], result.Models);
    }

    [Fact]
    public async Task ListingAnUnreachableEndpointFailsWithoutThrowing()
    {
        var config = new AgentConfig();
        Assert.True(config.TrySet("provider", "openai", out _));
        Assert.True(config.TrySet("baseUrl", "http://127.0.0.1:1/v1", out _));   // nothing listens there
        Assert.True(config.TrySet("timeoutSeconds", "2", out _));

        var result = await ConfigTuiProbe.ListModelsAsync(config, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Empty(result.Models);
        Assert.NotEqual("", result.Message);
    }
}
