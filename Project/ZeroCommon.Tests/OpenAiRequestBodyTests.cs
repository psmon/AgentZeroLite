using Agent.Common.Llm.Providers;
using Xunit;

namespace ZeroCommon.Tests;

/// <summary>
/// OpenAI's reasoning models reject <c>max_tokens</c> and any non-default temperature
/// (measured against api.openai.com: gpt-5-mini and o4-mini answered 400 to both).
/// LM Studio / Ollama keep the classic names.
/// </summary>
public class OpenAiRequestBodyTests
{
    private static LlmRequest Req(string model) => new()
    {
        Model = model,
        Messages = [LlmMessage.User("hi")],
        Temperature = 0.7f,
        MaxTokens = 512,
    };

    [Theory]
    [InlineData("gpt-5-mini")]
    [InlineData("gpt-5")]
    [InlineData("o4-mini")]
    [InlineData("o3")]
    [InlineData("gpt-5.6-terra")]
    [InlineData("gpt-5.6-luna")]
    [InlineData("gpt-5.6-sol")]
    [InlineData("gpt-6-luna")]
    [InlineData("gpt-6.1-sol")]
    public void OpenAi_reasoning_model_gets_max_completion_tokens_and_no_temperature(string model)
    {
        var body = OpenAiCompatibleProvider.BuildRequestBody(ExternalProviderNames.OpenAI, Req(model), stream: false);
        Assert.Equal(512, body["max_completion_tokens"]);
        Assert.False(body.ContainsKey("max_tokens"));
        Assert.False(body.ContainsKey("temperature"));
    }

    [Theory]
    [InlineData("gpt-4o-mini")]
    [InlineData("gpt-4.1")]
    [InlineData("gpt-5-chat-latest")]
    public void OpenAi_chat_model_keeps_temperature_and_uses_max_completion_tokens(string model)
    {
        var body = OpenAiCompatibleProvider.BuildRequestBody(ExternalProviderNames.OpenAI, Req(model), stream: false);
        Assert.Equal(512, body["max_completion_tokens"]);
        Assert.Equal(0.7f, body["temperature"]);
    }

    [Theory]
    [InlineData("LMStudio")]
    [InlineData("Ollama")]
    public void Local_servers_keep_max_tokens(string provider)
    {
        var body = OpenAiCompatibleProvider.BuildRequestBody(provider, Req("o4-mini"), stream: true);
        Assert.Equal(512, body["max_tokens"]);
        Assert.False(body.ContainsKey("max_completion_tokens"));
        Assert.Equal(0.7f, body["temperature"]);
    }
}
