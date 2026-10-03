using AgentOne.Agent;
using AgentOne.Commands;
using AgentOne.Llm.Decision;
using AgentOne.Tools;

namespace AgentOne.Tests;

/// <summary>
/// The `decide` verb and the `agent-one decide` command: one option reader,
/// one result shape, a tool the route never rules out.
/// </summary>
public class DecideToolTests
{
    private static Decision Picked(string choice, double confidence, params (string Name, double P)[] distribution) =>
        new(true, choice, confidence, distribution.ToDictionary(d => d.Name, d => d.P), "scripted", 3);

    [Theory]
    [InlineData("patch: change the call; rewrite: replace the module")]
    [InlineData("patch=change the call | rewrite=replace the module")]
    [InlineData("- patch: change the call\n- rewrite: replace the module")]
    [InlineData("1. patch: change the call\n2) rewrite: replace the module")]
    [InlineData("""{"patch":"change the call","rewrite":"replace the module"}""")]
    [InlineData("""["patch: change the call", {"name":"rewrite","description":"replace the module"}]""")]
    public void OptionsAreReadFromEveryShapeACallerWrites(string text)
    {
        Assert.True(DecisionInput.TryParseOptions(text, out var options, out var error), error);
        Assert.Equal(
            [new DecisionOption("patch", "change the call"), new DecisionOption("rewrite", "replace the module")],
            options);
    }

    [Fact]
    public void ANewlineListKeepsSemicolonsInsideDescriptions()
    {
        Assert.True(DecisionInput.TryParseOptions("a: one; still one\nb: two", out var options, out _));
        Assert.Equal("one; still one", options[0].Description);
    }

    [Fact]
    public void BareDescriptionsGetNamesMadeFromThem()
    {
        Assert.True(DecisionInput.TryParseOptions("Use the cache; Call the API again", out var options, out _));
        Assert.Equal(["use_the_cache", "call_the_api_again"], options.Select(o => o.Name));
    }

    [Fact]
    public void ASentenceWithAColonIsADescriptionNotAName()
    {
        Assert.True(DecisionInput.TryParseOptions("ship: release now\nwait until we know why: the flakes", out var options, out _));
        Assert.Equal("wait until we know why: the flakes", options[1].Description);
    }

    [Fact]
    public void AShortNameMayHaveSpaces()
    {
        Assert.True(DecisionInput.TryParseOptions("SQLite FTS5: low upkeep; Elasticsearch: more features", out var options, out _));
        Assert.Equal(["SQLite FTS5", "Elasticsearch"], options.Select(o => o.Name));
    }

    [Theory]
    [InlineData("", "no options")]
    [InlineData("only: one", "only one option")]
    [InlineData("a: x; a: y", "given twice")]
    [InlineData("{\"a\":", "do not parse")]
    public void ABadListIsRefusedWithAReason(string text, string expected)
    {
        Assert.False(DecisionInput.TryParseOptions(text, out _, out var error));
        Assert.Contains(expected, error);
    }

    [Fact]
    public void TooManyOptionsAreRefused()
    {
        var text = string.Join(';', Enumerable.Range(1, DecisionInput.MaxOptions + 1).Select(i => $"o{i}: option {i}"));
        Assert.False(DecisionInput.TryParseOptions(text, out _, out var error));
        Assert.Contains("at most", error);
    }

    [Fact]
    public async Task TheToolAsksTheEngineAgainstThePersonsWords()
    {
        var engine = new ScriptedDecisionEngine(Picked("patch", 0.82, ("patch", 0.9), ("rewrite", 0.1)));
        var belt = new DecisionToolbelt(engine, 0.6) { Request = () => "고쳐줘: Windows 경로 테스트" };

        var result = await belt.InvokeAsync(Call("Which fix?", "patch: change the call; rewrite: replace it", "fails on C:\\ paths"),
            CancellationToken.None);

        Assert.True(result.Ok, result.Text);
        Assert.Contains("decision: patch — change the call", result.Text);
        Assert.Contains("confident", result.Text);
        Assert.Equal("Which fix?", engine.Questions.Single());
        Assert.StartsWith("The user's request: 고쳐줘", engine.LastState);
        Assert.Contains("fails on C:\\ paths", engine.LastState);
        Assert.Equal(["patch", "rewrite"], engine.LastOptions!.Select(o => o.Name));
    }

    [Fact]
    public async Task ALowConfidenceIsReportedAsALean()
    {
        var engine = new ScriptedDecisionEngine(Picked("rewrite", 0.2, ("patch", 0.45), ("rewrite", 0.55)));
        var belt = new DecisionToolbelt(engine, 0.6);

        var result = await belt.InvokeAsync(Call("Which fix?", "patch: a; rewrite: b"), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Contains("a lean, not a verdict", result.Text);
    }

    [Fact]
    public async Task WithNoKeyTheToolSaysSoAndNeverCallsAnything()
    {
        var belt = new DecisionToolbelt(null, 0.6);

        var result = await belt.InvokeAsync(Call("Which?", "a: x; b: y"), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("no TypeSafe key", result.Text);
        Assert.Contains("unavailable", belt.Scope);
    }

    [Fact]
    public async Task AFailedEngineTellsTheModelToDecideItself()
    {
        var engine = new ScriptedDecisionEngine(Decision.Failed("HTTP 503"));
        var result = await new DecisionToolbelt(engine, 0.6).InvokeAsync(Call("Which?", "a: x; b: y"), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("HTTP 503", result.Text);
        Assert.Contains("Make the call yourself", result.Text);
    }

    [Fact]
    public async Task AMissingQuestionOrBadOptionsNeverReachTheEngine()
    {
        var engine = new ScriptedDecisionEngine(Picked("a", 1));
        var belt = new DecisionToolbelt(engine, 0.6);

        Assert.False((await belt.InvokeAsync(Call("", "a: x; b: y"), CancellationToken.None)).Ok);
        Assert.False((await belt.InvokeAsync(Call("Which?", "a: x"), CancellationToken.None)).Ok);
        Assert.Equal(0, engine.Calls);
    }

    [Fact]
    public void TheShapeListsEveryOfferedOptionStrongestFirst()
    {
        List<DecisionOption> options = [new("a", "x"), new("b", "y"), new("c", "z")];
        var result = DecisionInput.Shape(Picked("b", 0.7, ("a", 0.2), ("b", 0.8)), options, "q", 0.6);

        Assert.Equal(["b", "a", "c"], result.Ranked.Select(r => r.Name));
        Assert.Equal(0, result.Ranked[2].Probability);
        Assert.Equal("y", result.ChoiceDescription);
        Assert.True(result.Confident);
    }

    [Fact]
    public void DecideIsNeverRuledOutByARouteAndChangesNothing()
    {
        Assert.Equal(ToolCatalog.DecideFamily, ToolCatalog.FamilyOf("decide"));
        Assert.True(ToolCatalog.IsRouteExempt(ToolCatalog.DecideFamily));
        Assert.DoesNotContain(ToolCatalog.DecideFamily, ToolCatalog.GuardedFamilies);
        foreach (var family in new[] { ToolCatalog.FilesFamily, ToolCatalog.WebFamily, ToolCatalog.EditFamily, ToolCatalog.ExecFamily })
            Assert.False(ToolCatalog.IsRouteExempt(family));
    }

    [Fact]
    public async Task TheCommandReadsFlagsAndOptionPairs()
    {
        var (request, error, floor) = await DecideCommand.ParseAsync(
            ["Tests", "fail", "-q", "Which fix?", "-o", "patch=change the call", "-o", "rewrite=replace it", "--floor", "0.7"],
            0.6, CancellationToken.None);

        Assert.True(request is not null, error);
        Assert.Equal("Which fix?", request.Question);
        Assert.Equal("Tests fail", request.Context);
        Assert.Equal(["patch", "rewrite"], request.Options.Select(o => o.Name));
        Assert.Equal(0.7, floor);
    }

    [Fact]
    public async Task TheCommandReadsAJsonRequestAndLetsFlagsOverrideIt()
    {
        var file = Path.Combine(Path.GetTempPath(), "agent-one-decide-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        await File.WriteAllTextAsync(file,
            """{"question":"Ship?","context":"2 flaky tests","options":{"ship":"release now","wait":"fix first"}}""");

        try
        {
            var (request, error, _) = await DecideCommand.ParseAsync(["--input", file, "-q", "Ship today?"], 0.6, CancellationToken.None);

            Assert.True(request is not null, error);
            Assert.Equal("Ship today?", request.Question);
            Assert.Equal("2 flaky tests", request.Context);
            Assert.Equal(["ship", "wait"], request.Options.Select(o => o.Name));
        }
        finally { File.Delete(file); }
    }

    [Theory]
    [InlineData(new[] { "-o", "a=x", "-o", "b=y" }, "no question")]
    [InlineData(new[] { "-q", "Which?", "-o", "a=x" }, "only one option")]
    [InlineData(new[] { "-q", "Which?", "--bogus" }, "unknown option")]
    [InlineData(new[] { "-q", "Which?", "--floor", "2" }, "--floor")]
    public async Task TheCommandRefusesABadRequest(string[] args, string expected)
    {
        var (request, error, _) = await DecideCommand.ParseAsync(args, 0.6, CancellationToken.None);
        Assert.Null(request);
        Assert.Contains(expected, error);
    }

    private static ToolCall Call(string question, string options, string context = "") => new()
    {
        Tool = "decide",
        Args = new(StringComparer.OrdinalIgnoreCase) { ["question"] = question, ["options"] = options, ["context"] = context }
    };
}
