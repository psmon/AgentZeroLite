using Agent.Common.Llm.Providers;
using Xunit.Abstractions;

namespace ZeroCommon.Tests;

/// <summary>
/// Online smoke tests for the Webnori free-tier provider. These are the
/// canonical "does the wire still work" tests — much cheaper than spinning
/// up a 5 GB local Gemma 4. Maintainer's dev box can't run the local model
/// reliably, so this is the default LLM-touching path for CI.
///
/// <para><b>Skipped by default</b> via <c>WEBNORI_SMOKE</c> env var so
/// builds without internet stay green. Run locally with:</para>
/// <code>
/// $env:WEBNORI_SMOKE="1"; dotnet test Project/ZeroCommon.Tests --filter "Category=Online"
/// </code>
/// </summary>
[Trait("Category", "Online")]
[Trait("Provider", "Webnori")]
public sealed class WebnoriExternalSmokeTests
{
    private readonly ITestOutputHelper _output;

    public WebnoriExternalSmokeTests(ITestOutputHelper output) => _output = output;

    private static bool Enabled => Environment.GetEnvironmentVariable("WEBNORI_SMOKE") == "1";

    [Fact]
    public async Task Hello_completion_round_trips_under_30s()
    {
        if (!Enabled)
        {
            _output.WriteLine("Skipped: set WEBNORI_SMOKE=1 to enable.");
            return;
        }

        using var provider = (OpenAiCompatibleProvider)LlmProviderFactory.CreateWebnori(
            timeout: TimeSpan.FromSeconds(30));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var resp = await provider.CompleteAsync(new LlmRequest
        {
            Model = WebnoriDefaults.DefaultModel,
            Messages = [LlmMessage.User("Reply with exactly one word: hello")],
            Temperature = 0.0f,
            MaxTokens = 16,
        }, cts.Token);
        sw.Stop();

        _output.WriteLine($"Webnori responded in {sw.ElapsedMilliseconds}ms: \"{resp.Text}\"");
        Assert.False(string.IsNullOrWhiteSpace(resp.Text), $"Empty response (finish={resp.FinishReason})");
        Assert.True(sw.ElapsedMilliseconds < 30_000, "Webnori should respond within the 30s budget");
    }

    [Fact]
    public async Task ListModels_returns_at_least_one_entry()
    {
        if (!Enabled)
        {
            _output.WriteLine("Skipped: set WEBNORI_SMOKE=1 to enable.");
            return;
        }

        using var provider = (OpenAiCompatibleProvider)LlmProviderFactory.CreateWebnori(
            timeout: TimeSpan.FromSeconds(15));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var models = await provider.ListModelsAsync(cts.Token);
        _output.WriteLine($"Webnori listed {models.Count} model(s): {string.Join(", ", models.Select(m => m.Id))}");
        Assert.NotEmpty(models);
    }
}
