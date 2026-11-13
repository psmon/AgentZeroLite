using Agent.Common.Llm.Providers;

namespace ZeroCommon.Tests;

/// <summary>
/// Settings → LLM lists the server's models; only chat models belong in that list, because
/// AI mode calls /v1/chat/completions and anything else fails when picked.
/// </summary>
public class ChatModelFilterTests
{
    [Theory]
    [InlineData("gpt-4o")]
    [InlineData("gpt-4.1-mini")]
    [InlineData("gpt-5")]
    [InlineData("o3-mini")]
    [InlineData("gpt-4o-search-preview")]
    [InlineData("chatgpt-4o-latest")]
    [InlineData("google/gemma-4-e4b")]                 // LM Studio
    [InlineData("qwen3:8b")]                           // Ollama
    [InlineData("llama-3.2-3b-instruct-q4")]           // "instruct" only as a whole word
    public void Chat_models_are_kept(string id) => Assert.True(ChatModelFilter.IsChatModel(id));

    [Theory]
    [InlineData("text-embedding-3-small")]
    [InlineData("text-embedding-nomic-embed-text-v1.5")]
    [InlineData("nomic-embed-text:latest")]
    [InlineData("whisper-1")]
    [InlineData("tts-1-hd")]
    [InlineData("gpt-4o-mini-tts")]
    [InlineData("gpt-4o-transcribe")]
    [InlineData("gpt-4o-audio-preview")]
    [InlineData("gpt-realtime")]
    [InlineData("dall-e-3")]
    [InlineData("gpt-image-1")]
    [InlineData("omni-moderation-latest")]
    [InlineData("davinci-002")]
    [InlineData("babbage-002")]
    [InlineData("gpt-3.5-turbo-instruct")]
    [InlineData("o3-deep-research")]
    [InlineData("computer-use-preview")]
    [InlineData("o1-pro")]
    [InlineData("gpt-5-pro-2025-10-06")]
    [InlineData("gpt-5-codex")]
    [InlineData("codex-mini-latest")]
    [InlineData("sora-2")]
    public void Non_chat_models_are_dropped(string id) => Assert.False(ChatModelFilter.IsChatModel(id));

    [Fact]
    public void A_dated_snapshot_is_dropped_only_when_its_alias_is_listed()
    {
        var kept = ChatModelFilter.Filter(new[]
        {
            "gpt-4o", "gpt-4o-2024-08-06", "gpt-4o-2024-05-13",
            "gpt-4-0613",                    // no "gpt-4" alias listed → stays
            "gpt-3.5-turbo", "gpt-3.5-turbo-1106",
            "text-embedding-3-large", "whisper-1",
        });
        Assert.Equal(new[] { "gpt-3.5-turbo", "gpt-4-0613", "gpt-4o" }, kept);
    }
}
