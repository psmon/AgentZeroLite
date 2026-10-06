using Agent.Common.Llm;
using Xunit;

namespace ZeroCommon.Tests;

/// <summary>The AI-answer report (Microsoft Store policy 11.16): what the person reviews and sends.</summary>
public class AiContentReportTests
{
    private static AiContentReport Report(string content, string note = "it insulted me") =>
        new(AiReportCategory.Inappropriate, note, content, "Ollama · gemma4", "0.25.1", "Windows 11");

    [Fact]
    public void Mailto_goes_to_support_with_subject_and_quoted_answer()
    {
        var uri = Report("답변 & 내용 #1").MailtoUri();

        Assert.StartsWith("mailto:" + AiContentReport.SupportEmail + "?subject=", uri);
        var decoded = Uri.UnescapeDataString(uri);
        Assert.Contains("[AI content report] Inappropriate or offensive", decoded);
        Assert.Contains("답변 & 내용 #1", decoded);
        Assert.Contains("it insulted me", decoded);
        Assert.Contains("Ollama · gemma4", decoded);
        // Reserved characters in the answer must not break the query string.
        Assert.DoesNotContain("& 내용", uri);
    }

    [Fact]
    public void Issue_link_opens_the_new_issue_page_prefilled()
    {
        var uri = Report("bad answer").IssueUri();

        Assert.StartsWith(AiContentReport.IssuesUrl + "?title=", uri);
        Assert.Contains("bad answer", Uri.UnescapeDataString(uri));
    }

    [Fact]
    public void A_long_answer_is_cut_so_the_link_stays_usable()
    {
        var body = Report(new string('x', 10_000)).Body();

        Assert.Contains("… (cut)", body);
        Assert.True(body.Length < AiContentReport.MaxQuotedChars + 600, body.Length.ToString());
    }

    [Fact]
    public void Missing_note_and_model_are_said_not_left_blank()
    {
        var body = new AiContentReport(AiReportCategory.Other, " ", "x", null, "1", "os").Body();

        Assert.Contains("(no note)", body);
        Assert.Contains("(not set)", body);
    }
}
