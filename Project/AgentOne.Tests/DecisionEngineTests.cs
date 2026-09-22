using System.Net;
using System.Text;
using AgentOne.Llm.Decision;
using AgentOne.Services;

namespace AgentOne.Tests;

/// <summary>A stub endpoint: canned replies, and a record of what was actually sent.</summary>
internal sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
{
    public List<string> Requests { get; } = [];

    public int Calls => Requests.Count;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));

        return new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }
}

/// <summary>A handler that fails the test if it is ever reached.</summary>
internal sealed class ForbiddenHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        throw new InvalidOperationException("the engine made a network call it should not have made");
}

[Collection(AgentOneHomeCollection.Name)]
public class DecisionEngineTests : IDisposable
{
    private const string GoodBody = """
        {
          "model": "jev-1.13.0",
          "answers": {
            "decision": {
              "type": "choice",
              "choice": "search_web",
              "confidence": 0.82,
              "probabilities": { "read_local": 0.09, "search_web": 0.89, "answer_now": 0.02 }
            }
          },
          "usage": { "input_tokens": 120, "output_tokens": 18 }
        }
        """;

    private static readonly DecisionOption[] ThreeOptions =
    [
        new("read_local", "Read files in the workspace."),
        new("search_web", "Search the web."),
        new("answer_now", "Answer from what is known.")
    ];

    private readonly string _home;
    private readonly string? _previous;
    private readonly string? _previousEnv;

    public DecisionEngineTests()
    {
        _previous = Environment.GetEnvironmentVariable(AppPaths.HomeEnvVar);
        _previousEnv = Environment.GetEnvironmentVariable("TYPESAFE_API_KEY");
        Environment.SetEnvironmentVariable("TYPESAFE_API_KEY", null);

        _home = Path.Combine(Path.GetTempPath(), "agent-one-jev-" + Guid.NewGuid().ToString("N")[..8]);
        Environment.SetEnvironmentVariable(AppPaths.HomeEnvVar, _home);
        CredentialStore.Save("ts-test-key", CredentialStore.Slot.Jev);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppPaths.HomeEnvVar, _previous);
        Environment.SetEnvironmentVariable("TYPESAFE_API_KEY", _previousEnv);
        try { Directory.Delete(_home, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private static JevClient Engine(HttpMessageHandler handler) => new(new AgentConfig(), handler);

    private static Task<Decision> Choose(JevClient engine, params DecisionOption[] options) =>
        engine.ChooseAsync("the state", "Which one?", options, CancellationToken.None);

    // --- the happy path ----------------------------------------------------

    [Fact]
    public async Task TheWinnerAndTheWholeDistributionComeBack()
    {
        using var engine = Engine(new StubHandler(HttpStatusCode.OK, GoodBody));

        var decision = await Choose(engine, ThreeOptions);

        Assert.True(decision.Ok);
        Assert.Equal("search_web", decision.Choice);
        Assert.Equal(0.82, decision.Confidence, 3);
        Assert.Equal(3, decision.Probabilities.Count);
        Assert.Equal(0.89, decision.Probabilities["search_web"], 3);
    }

    [Fact]
    public async Task RankedPutsTheStrongestFirstSoARunnerUpCanBeShown()
    {
        using var engine = Engine(new StubHandler(HttpStatusCode.OK, GoodBody));

        var ranked = (await Choose(engine, ThreeOptions)).Ranked.ToList();

        Assert.Equal("search_web", ranked[0].Key);
        Assert.Equal("read_local", ranked[1].Key);
        Assert.Equal("answer_now", ranked[2].Key);
    }

    [Fact]
    public async Task TheOptionsAreSentAsCriteriaWithTheirDescriptions()
    {
        var handler = new StubHandler(HttpStatusCode.OK, GoodBody);
        using var engine = Engine(handler);

        await Choose(engine, ThreeOptions);

        var sent = handler.Requests.Single();
        Assert.Contains("\"type\":\"choice\"", sent);
        Assert.Contains("\"read_local\":\"Read files in the workspace.\"", sent);
        Assert.Contains("\"state\":\"the state\"", sent);
        Assert.Contains("\"instructions\":\"Which one?\"", sent);
    }

    // --- not a decision ----------------------------------------------------

    [Fact]
    public async Task OneOptionIsAnsweredWithoutCallingAnything()
    {
        using var engine = Engine(new ForbiddenHandler());

        var decision = await Choose(engine, new DecisionOption("only", "the only one"));

        Assert.True(decision.Ok);
        Assert.Equal("only", decision.Choice);
        Assert.False(decision.Called);
        Assert.Contains("nothing to decide", decision.Message);
    }

    [Fact]
    public async Task NoOptionsIsAFailureNotACall()
    {
        using var engine = Engine(new ForbiddenHandler());

        var decision = await engine.ChooseAsync("s", "q", [], CancellationToken.None);

        Assert.False(decision.Ok);
        Assert.Contains("no options", decision.Message);
    }

    [Fact]
    public async Task WithoutAKeyNothingIsSent()
    {
        CredentialStore.Clear(CredentialStore.Slot.Jev);
        using var engine = Engine(new ForbiddenHandler());

        var decision = await Choose(engine, ThreeOptions);

        Assert.False(decision.Ok);
        Assert.Contains("no TypeSafe key", decision.Message);
    }

    // --- refusing bad answers ---------------------------------------------

    [Fact]
    public async Task AnAnswerNamingAnOptionWeNeverOfferedIsRefused()
    {
        // Request and answer have drifted apart; routing on it would be worse
        // than failing.
        const string body = """
            {"answers":{"decision":{"type":"choice","choice":"delete_everything","confidence":1.0}}}
            """;
        using var engine = Engine(new StubHandler(HttpStatusCode.OK, body));

        var decision = await Choose(engine, ThreeOptions);

        Assert.False(decision.Ok);
        Assert.Contains("not one of the options", decision.Message);
    }

    [Fact]
    public async Task AnAnswerForADifferentQuestionIsRefused()
    {
        const string body = """{"answers":{"something_else":{"choice":"search_web"}}}""";
        using var engine = Engine(new StubHandler(HttpStatusCode.OK, body));

        var decision = await Choose(engine, ThreeOptions);

        Assert.False(decision.Ok);
        Assert.Contains("without the question", decision.Message);
    }

    [Fact]
    public async Task AnAnswerWithNoChoiceIsRefused()
    {
        const string body = """{"answers":{"decision":{"type":"choice","confidence":0.9}}}""";
        using var engine = Engine(new StubHandler(HttpStatusCode.OK, body));

        var decision = await Choose(engine, ThreeOptions);

        Assert.False(decision.Ok);
        Assert.Contains("no choice", decision.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "key was rejected")]
    [InlineData(HttpStatusCode.Forbidden, "key was rejected")]
    [InlineData(HttpStatusCode.InternalServerError, "HTTP 500")]
    [InlineData(HttpStatusCode.TooManyRequests, "HTTP 429")]
    public async Task HttpFailuresBecomeSentences(HttpStatusCode status, string expected)
    {
        using var engine = Engine(new StubHandler(status, "{}"));

        var decision = await Choose(engine, ThreeOptions);

        Assert.False(decision.Ok);
        Assert.Contains(expected, decision.Message);
    }

    [Fact]
    public async Task ANonJsonBodyIsReported()
    {
        using var engine = Engine(new StubHandler(HttpStatusCode.OK, "<html>maintenance</html>"));

        var decision = await Choose(engine, ThreeOptions);

        Assert.False(decision.Ok);
        Assert.Contains("did not return JSON", decision.Message);
    }

    [Fact]
    public async Task AServiceErrorBodyIsReported()
    {
        const string body = """{"error":{"message":"model overloaded","type":"server_error"}}""";
        using var engine = Engine(new StubHandler(HttpStatusCode.OK, body));

        var decision = await Choose(engine, ThreeOptions);

        Assert.False(decision.Ok);
        Assert.Contains("model overloaded", decision.Message);
    }

    // --- the health check --------------------------------------------------

    [Fact]
    public async Task TheHealthCheckAsksOneNoulQuestion()
    {
        const string body = """
            {"model":"jev-1.13.0","answers":{"reachable":{"type":"noul","noul":0.98}},
             "usage":{"input_tokens":30,"output_tokens":5}}
            """;
        var handler = new StubHandler(HttpStatusCode.OK, body);
        using var engine = Engine(handler);

        var check = await engine.CheckAsync(CancellationToken.None);

        Assert.True(check.Ok);
        Assert.Contains("jev-1.13.0", check.Message);
        Assert.Contains("noul 0.98", check.Message);
        Assert.Contains("\"type\":\"noul\"", handler.Requests.Single());
    }
}
