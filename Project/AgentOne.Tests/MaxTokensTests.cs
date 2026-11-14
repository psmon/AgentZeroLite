using System.Text.Json;
using AgentOne.Llm;
using AgentOne.Services;
using Xunit;

namespace AgentOne.Tests;

/// <summary>
/// max_tokens reaches the wire when set and is omitted when not — and the reasoning model
/// gets its own cap. The default of 0 (= server default) keeps every other provider unchanged.
/// </summary>
public class MaxTokensTests
{
    [Fact]
    public void Request_includes_max_tokens_when_set()
    {
        var json = JsonSerializer.Serialize(
            new ChatCompletionRequest { Model = "m", MaxTokens = 120_000 },
            AgentOneWireJson.Default.ChatCompletionRequest);
        Assert.Contains("\"max_tokens\":120000", json);
    }

    [Fact]
    public void Request_omits_max_tokens_when_null()
    {
        var json = JsonSerializer.Serialize(
            new ChatCompletionRequest { Model = "m", MaxTokens = null },
            AgentOneWireJson.Default.ChatCompletionRequest);
        Assert.DoesNotContain("max_tokens", json);
    }

    [Fact]
    public void Config_round_trips_max_token_fields()
    {
        var cfg = JsonSerializer.Deserialize(
            "{\"maxTokens\":120000,\"reasoningMaxTokens\":250000}",
            AgentOneJson.Default.AgentConfig)!;
        Assert.Equal(120_000, cfg.MaxTokens);
        Assert.Equal(250_000, cfg.ReasoningMaxTokens);
    }

    [Fact]
    public void Config_default_max_tokens_is_zero_meaning_server_default()
    {
        Assert.Equal(0, new AgentConfig().MaxTokens);
        Assert.Equal(0, new AgentConfig().ReasoningMaxTokens);
    }

    [Fact]
    public void ForReasoning_moves_reasoning_cap_into_max_tokens()
    {
        var derived = new AgentConfig { MaxTokens = 120_000, ReasoningMaxTokens = 250_000, ReasoningModel = "r" }
            .ForReasoning();
        Assert.Equal(250_000, derived.MaxTokens);
    }
}
