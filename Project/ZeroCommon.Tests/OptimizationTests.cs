using Agent.Common.Llm.Providers;

namespace ZeroCommon.Tests;

public sealed class OptimizationTests
{
    [Fact]
    public void LlmRequest_Messages_accepts_ReadOnlyList_wrapper()
    {
        var history = new List<LlmMessage>
        {
            LlmMessage.System("sys"),
            LlmMessage.User("hello"),
        };

        var request = new LlmRequest
        {
            Model = "test",
            Messages = history.AsReadOnly(),
        };

        Assert.Equal(2, request.Messages.Count);
        Assert.Equal("sys", request.Messages[0].Content);
        Assert.Equal(LlmRole.User, request.Messages[1].Role);
    }

    [Fact]
    public void LlmRequest_Messages_accepts_list_directly()
    {
        var request = new LlmRequest
        {
            Messages = [LlmMessage.User("hi")],
        };

        Assert.Single(request.Messages);
    }

    [Fact]
    public void LlmRequest_Messages_default_is_empty()
    {
        var request = new LlmRequest();
        Assert.Empty(request.Messages);
    }

    [Fact]
    public void LlmRequest_Messages_is_not_mutated_through_readonly_wrapper()
    {
        var history = new List<LlmMessage> { LlmMessage.User("a") };
        var request = new LlmRequest { Messages = history.AsReadOnly() };

        history.Add(LlmMessage.User("b"));

        Assert.Equal(2, request.Messages.Count);
    }

    [Fact]
    public void ActiveConversationsReply_accepts_HashSet_directly()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Claude", "Codex" };
        var reply = new ActiveConversationsReply(set);

        Assert.Equal(2, reply.Active.Count);
        Assert.Contains("Claude", reply.Active);
        Assert.Contains("Codex", reply.Active);
    }

    [Fact]
    public void ActiveConversationsReply_accepts_empty_HashSet()
    {
        var reply = new ActiveConversationsReply(new HashSet<string>());
        Assert.Empty(reply.Active);
    }

    [Fact]
    public void AppLogger_size_estimate_uses_string_length()
    {
        AppLogger.Clear();
        var before = AppLogger.GetAll().Length;
        AppLogger.Log("test entry");
        var after = AppLogger.GetAll().Length;

        Assert.Equal(before + 1, after);
    }

    [Fact]
    public void AppLogger_handles_multibyte_characters_without_crash()
    {
        AppLogger.Log("한글 테스트 메시지 日本語テスト 中文测试");
        var entries = AppLogger.GetAll();
        Assert.Contains(entries, e => e.Contains("한글 테스트"));
    }
}
