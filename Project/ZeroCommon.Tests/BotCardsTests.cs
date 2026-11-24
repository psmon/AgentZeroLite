// The bot transcript as components (Agents/BotCards, after A2UI): what the agent produced is
// turned into a small catalog of nodes instead of being printed as the JSON it travelled in.

using Agent.Common.Agents;
using Agent.Common.Llm.Tools;

namespace ZeroCommon.Tests;

public sealed class BotMarkdownTests
{
    [Fact]
    public void An_answer_becomes_heading_paragraph_list_and_code()
    {
        var nodes = BotMarkdown.Parse("""
            ## 워크스페이스 파일 목록
            총 **59개** 항목입니다.

            - 최상위 폴더: `.claude/`, `tools/`
            - 최상위 파일: `agent.md`

            1. 먼저 빌드
            2. 그다음 테스트

            ```powershell
            dotnet build
            ```
            """.Replace("            ", ""));

        Assert.Collection(nodes,
            n => Assert.Equal(new BotText("워크스페이스 파일 목록", BotTextVariant.Heading), n),
            n => Assert.Equal(new BotText("총 59개 항목입니다."), n),
            n =>
            {
                var list = Assert.IsType<BotList>(n);
                Assert.False(list.Ordered);
                Assert.Equal(["최상위 폴더: .claude/, tools/", "최상위 파일: agent.md"], list.Items);
            },
            n =>
            {
                var list = Assert.IsType<BotList>(n);
                Assert.True(list.Ordered);
                Assert.Equal(["1.", "2."], list.Rows.Select(r => r.Marker));
            },
            n => Assert.Equal(new BotText("dotnet build", BotTextVariant.Code), n));
    }

    [Fact]
    public void Plain_sentences_stay_one_paragraph_with_their_line_breaks()
    {
        var nodes = BotMarkdown.Parse("첫 줄입니다.\n둘째 줄입니다.");

        Assert.Equal(new BotText("첫 줄입니다.\n둘째 줄입니다."), Assert.Single(nodes));
    }

    [Fact]
    public void Text_inside_a_code_fence_is_left_exactly_as_written()
    {
        var nodes = BotMarkdown.Parse("```\n- not a list\n**not bold** `x`\n```");

        Assert.Equal(new BotText("- not a list\n**not bold** `x`", BotTextVariant.Code), Assert.Single(nodes));
    }

    [Fact]
    public void Marks_go_but_names_and_code_spans_keep_every_character()
    {
        var nodes = BotMarkdown.Parse("**중요**: `2**10` 은 1024, 파일은 __init__.py 와 `__main__.py`");

        Assert.Equal(new BotText("중요: 2**10 은 1024, 파일은 __init__.py 와 __main__.py"), Assert.Single(nodes));
    }

    [Fact]
    public void A_cut_never_leaves_half_an_emoji()
    {
        var args = "{\"query\":\"" + new string('a', 79) + "😀 tail\"}";

        Assert.Equal(new string('a', 79) + "…", BotJson.Headline(args));
    }

    [Fact]
    public void An_unclosed_fence_and_empty_input_do_not_lose_or_invent_anything()
    {
        Assert.Empty(BotMarkdown.Parse("  \n "));
        Assert.Equal(new BotText("half", BotTextVariant.Code), Assert.Single(BotMarkdown.Parse("```\nhalf")));
    }
}

public sealed class BotJsonTests
{
    [Fact]
    public void Arguments_become_labelled_fields()
    {
        var nodes = BotJson.ToNodes("""{"query":"서울 오늘 날씨","max_results":5,"grayscale":true}""");

        Assert.Equal(
            [new BotField("query", "서울 오늘 날씨"), new BotField("max results", "5"), new BotField("grayscale", "yes")],
            nodes);
    }

    [Fact]
    public void A_result_shows_its_error_and_not_the_envelope_flag()
    {
        const string result = """{"ok":false,"error":"notes not available in this host"}""";

        Assert.True(BotJson.LooksFailed(result));
        Assert.Equal(new BotField("error", "notes not available in this host"), Assert.Single(BotJson.ToNodes(result)));
        Assert.False(BotJson.LooksFailed("""{"ok":true,"results":[]}"""));
    }

    [Fact]
    public void An_array_of_objects_is_a_list_of_one_line_summaries_with_a_count()
    {
        var results = string.Join(",", Enumerable.Range(1, 11).Select(i =>
            $$"""{"title":"Result {{i}}","url":"https://e.com/{{i}}","snippet":"long text"}"""));

        var nodes = BotJson.ToNodes($$"""{"ok":true,"results":[{{results}}]}""");

        Assert.Equal(new BotText("results (11)", BotTextVariant.Caption), nodes[0]);
        var list = Assert.IsType<BotList>(nodes[1]);
        Assert.Equal("Result 1 — https://e.com/1", list.Items[0]);
        Assert.Equal(9, list.Items.Count);                 // 8 shown + the "more" line
        Assert.Equal("… +3 more", list.Items[^1]);
    }

    [Fact]
    public void Long_text_becomes_a_paragraph_with_real_line_breaks()
    {
        var nodes = BotJson.ToNodes("""{"path":"notes/a.md","content":"첫 줄\n둘째 줄\n셋째 줄"}""");

        Assert.Equal(new BotField("path", "notes/a.md"), nodes[0]);
        Assert.Equal(new BotText("content", BotTextVariant.Caption), nodes[1]);
        Assert.Equal(new BotText("첫 줄\n둘째 줄\n셋째 줄"), nodes[2]);
    }

    [Fact]
    public void What_is_not_json_is_shown_as_it_is_and_never_throws()
    {
        Assert.Equal(new BotText("plain words"), Assert.Single(BotJson.ToNodes("plain words")));
        Assert.Equal(new BotText("""{"tool":"send_to_ter""", BotTextVariant.Code),
            Assert.Single(BotJson.ToNodes("""{"tool":"send_to_ter""")));
        Assert.Empty(BotJson.ToNodes(null));
        Assert.False(BotJson.LooksFailed("not json"));
    }

    [Theory]
    [InlineData("""{"query":"서울 날씨","max_results":5}""", "서울 날씨")]
    [InlineData("""{"group":1,"tab":0,"text":"hello\nworld"}""", "hello world")]
    [InlineData("""{"hwnd":123}""", "")]
    [InlineData("{}", "")]
    public void Headline_is_the_argument_a_person_would_name_the_call_by(string args, string expected)
        => Assert.Equal(expected, BotJson.Headline(args));

    [Fact]
    public void A_screen_read_is_summarised_by_its_size_and_last_line()
        => Assert.Equal("3 line(s) · last: › Ask Codex to do anything",
            BotScreen.Summary("• 안녕하세요\n\n  Worked for 3s\n› Ask Codex to do anything\n"));
}

public sealed class TerminalReadResultTests
{
    [Fact]
    public void A_host_envelope_is_unwrapped_instead_of_nested_in_a_string()
    {
        // What both hosts return — and what used to be wrapped once more as "text".
        const string host = """{"ok":true,"group_index":1,"tab_index":1,"length":9,"text":"• 안녕\n› Ask"}""";

        var result = TerminalReadResult.Normalize(host);

        Assert.Equal("""{"ok":true,"text":"• 안녕\n› Ask"}""", result);
        var (ok, text) = ToolTurnPresenter.TryExtractReadTerminalText(result);
        Assert.True(ok);
        Assert.Equal("• 안녕\n› Ask", text);     // a real line break, no quoted JSON
    }

    [Fact]
    public void A_note_survives_and_a_failure_stays_a_failure()
    {
        Assert.Equal("""{"ok":true,"text":"x","note":"peer cannot run its CLI"}""",
            TerminalReadResult.Normalize("""{"ok":true,"text":"x","note":"peer cannot run its CLI"}"""));
        Assert.Equal("""{"ok":false,"error":"Terminal [9:9] is not started."}""",
            TerminalReadResult.Normalize("""{"ok":false,"error":"Terminal [9:9] is not started."}"""));
    }

    [Theory]
    [InlineData("agent reply: done")]
    [InlineData("{ not json at all")]
    [InlineData("""{"some":"json the screen happens to show"}""")]
    [InlineData("""{"ok":true,"count":3}""")]
    [InlineData("")]
    public void Plain_screen_text_is_wrapped_as_before(string screen)
    {
        var (ok, text) = ToolTurnPresenter.TryExtractReadTerminalText(TerminalReadResult.Normalize(screen));

        Assert.True(ok);
        Assert.Equal(screen, text);
    }
}
