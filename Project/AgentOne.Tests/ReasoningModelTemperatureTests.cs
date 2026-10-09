using System.Net;
using System.Text;
using AgentOne.Llm;
using AgentOne.Services;

namespace AgentOne.Tests;

/// <summary>
/// OpenAI's reasoning models take only the default temperature. Measured
/// (2026-10-09): `agent-one run --model gpt-5-mini` against api.openai.com
/// failed with 400 "unsupported_value" because temperature 0.2 was always sent;
/// gpt-4o-mini answered. The field is now left out for those models.
/// </summary>
public class ReasoningModelTemperatureTests
{
    [Theory]
    [InlineData("gpt-5-mini", true)]
    [InlineData("gpt-5", true)]
    [InlineData("o4-mini", true)]
    [InlineData("o3", true)]
    [InlineData("openai/gpt-5-mini", true)]
    [InlineData("gpt-5.6-terra", true)]
    [InlineData("gpt-5.6-luna", true)]
    [InlineData("gpt-5.6-sol", true)]
    [InlineData("gpt-6-luna", true)]
    [InlineData("gpt-6.1-sol", true)]
    [InlineData("gpt-4.1", false)]
    [InlineData("gpt-5-chat-latest", false)]
    [InlineData("gpt-4o-mini", false)]
    [InlineData("gemma-4-e4b", false)]
    [InlineData("ollama", false)]
    public void ReasoningModelsAreRecognisedByName(string model, bool expected) =>
        Assert.Equal(expected, OpenAiCompatChatProvider.IsReasoningModel(model));

    [Theory]
    [InlineData("gpt-5-mini", false)]
    [InlineData("gpt-6-luna", false)]
    [InlineData("gpt-4o-mini", true)]
    public async Task TemperatureIsSentOnlyToModelsThatAcceptIt(string model, bool sendsTemperature)
    {
        var handler = new CapturingHandler();
        var config = new AgentConfig();
        config.TrySet("baseUrl", "http://capture.test/v1", out _);
        config.TrySet("model", model, out _);
        using var provider = new OpenAiCompatChatProvider(config, handler);

        await provider.CompleteAsync([ChatMessage.User("hi")], CancellationToken.None);

        Assert.Equal(sendsTemperature, handler.Body!.Contains("\"temperature\""));
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Body = await request.Content!.ReadAsStringAsync(ct);
            const string reply = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"OK\"}}]}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply, Encoding.UTF8, "application/json") };
        }
    }
}
