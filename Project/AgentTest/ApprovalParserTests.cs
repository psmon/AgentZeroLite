using System.IO;
using AgentZeroWpf;

namespace AgentTest;

public class ApprovalParserTests
{
    // ==========================================================
    //  1. Approval Pattern Detection
    // ==========================================================

    [Theory]
    [InlineData("Do you want to proceed?")]
    [InlineData("This command requires approval")]
    [InlineData("Yes, and don't ask again for: grep:*")]
    [InlineData("DO YOU WANT TO PROCEED?")]  // case insensitive
    public void ContainsApprovalPattern_Positive(string text)
    {
        Assert.True(ApprovalParser.ContainsApprovalPattern(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("normal terminal output here")]
    [InlineData("Processing files...")]
    public void ContainsApprovalPattern_Negative(string text)
    {
        Assert.False(ApprovalParser.ContainsApprovalPattern(text));
    }

    // ==========================================================
    //  2. ANSI Code Stripping
    // ==========================================================

    [Fact]
    public void StripAnsiCodes_RemovesColorCodes()
    {
        var input = "\x1B[38;2;255;255;255mHello\x1B[0m World";
        var result = ApprovalParser.StripAnsiCodes(input);
        Assert.Equal("Hello World", result);
    }

    [Fact]
    public void StripAnsiCodes_ConvertsCursorPositionToNewline()
    {
        var input = "Line1\x1B[5;1HLine2";
        var result = ApprovalParser.StripAnsiCodes(input);
        Assert.Equal("Line1\nLine2", result);
    }

    [Fact]
    public void StripAnsiCodes_HandlesComplexSequences()
    {
        // From real log: SkillSync raw output with multiple escape sequences
        var input = "\x1B[?2026h\x1B[?2026l\x1B[38;2;80;80;80m\x1B[48;2;55;55;55m\x1B[5;1H> /skills\x1B[m";
        var result = ApprovalParser.StripAnsiCodes(input);
        Assert.Contains("/skills", result);
        // Note: \x1B[?2026h/l uses '?' which current regex handles, but cursor-position
        // sequence \x1B[5;1H is converted to newline. Verify no raw ESC remains.
        // Current behavior: the \x1B in "\x1B[?2026h" IS stripped by the general pattern.
        // The result starts with \n from cursor-position conversion.
        Assert.DoesNotContain("\x1B[", result);
    }

    // ==========================================================
    //  3. Parse: Pattern A — 3-option (Yes / Don't ask again / No)
    // ==========================================================

    [Fact]
    public void Parse_ThreeOption_Grep()
    {
        // Real log: [23:01:36] optionRegion
        var text = "proceed?\n>1.Yes\n   2. Yes, and don't ask again for: grep:*\n   3. No\n\n ";
        var result = ApprovalParser.ParseApprovalPrompt(text);

        Assert.NotNull(result);
        Assert.Equal(3, result.Options.Count);
        Assert.Equal("Yes", result.Options[0].Text);
        Assert.Contains("grep", result.Options[1].Text);
        Assert.Equal("No", result.Options[2].Text);
    }

    [Fact]
    public void Parse_ThreeOption_GitCommit()
    {
        // Real log: [21:28:43]
        var text = "proceed?\n > 1. Yes\n   2. Yes, and don't ask again for git add and git commit -m ' commands in D:\\Code\\AI\\CodeMap\\CodeScan\n   3. No\n\n ";
        var result = ApprovalParser.ParseApprovalPrompt(text);

        Assert.NotNull(result);
        Assert.Equal(3, result.Options.Count);
        Assert.Contains("git add", result.Options[1].Text);
    }

    [Fact]
    public void Parse_ThreeOption_GitPush()
    {
        // Real log: [21:28:48]
        var text = "proceed?\n>1.Yes\n2.Yes,anddon'taskagainfor:gitpush:*\n3.No\n";
        var result = ApprovalParser.ParseApprovalPrompt(text);

        Assert.NotNull(result);
        Assert.Equal(3, result.Options.Count);
        Assert.Contains("gitpush", result.Options[1].Text);
    }

    [Fact]
    public void Parse_ThreeOption_DotnetNew()
    {
        // Real log: [22:04:42]
        var text = "proceed?\n > 1. Yes\n   2. Yes, and don't ask again for: dotnet new:*\n   3. No\n\n ";
        var result = ApprovalParser.ParseApprovalPrompt(text);

        Assert.NotNull(result);
        Assert.Equal(3, result.Options.Count);
        Assert.Contains("dotnet new", result.Options[1].Text);
    }

    [Fact]
    public void Parse_ThreeOption_DotnetTest()
    {
        // Real log: [22:07:00]
        var text = "proceed?\n > 1. Yes\n   2. Yes, and don't ask again for: dotnet test:*\n   3. No\n\n ";
        var result = ApprovalParser.ParseApprovalPrompt(text);

        Assert.NotNull(result);
        Assert.Equal(3, result.Options.Count);
        Assert.Contains("dotnet test", result.Options[1].Text);
    }

    // ==========================================================
    //  4. Parse: Pattern B — Allow reading from path
    // ==========================================================

    [Fact]
    public void Parse_AllowReading_CodeMap()
    {
        // Real log: [21:55:43]
        var text = "proceed?            \n> 1. Yes       \n2.Yes,allowreadingfromCodeMap\\fromthisproject\n   3. No\n\n ";
        var result = ApprovalParser.ParseApprovalPrompt(text);

        Assert.NotNull(result);
        Assert.Equal(3, result.Options.Count);
        Assert.Contains("CodeMap", result.Options[1].Text);
    }

    [Fact]
    public void Parse_AllowReading_DotClaude()
    {
        // Real log: [21:57:51]
        var text = "proceed?            \n> 1. Yes       \n2.Yes,allowreadingfrom.claude\\and.claude\\fromthisproject\n   3. No\n\n ";
        var result = ApprovalParser.ParseApprovalPrompt(text);

        Assert.NotNull(result);
        Assert.Equal(3, result.Options.Count);
        Assert.Contains(".claude", result.Options[1].Text);
    }

    [Fact]
    public void Parse_AllowReading_Tmp()
    {
        // Real log: [22:00:29]
        var text = "proceed?                       \n>1.Yes\n2. Yes,allowreadingfromtmp\\fromthisproject\n  3. No\n\n ";
        var result = ApprovalParser.ParseApprovalPrompt(text);

        Assert.NotNull(result);
        Assert.Equal(3, result.Options.Count);
        Assert.Contains("tmp", result.Options[1].Text);
    }

    // ==========================================================
    //  5. Parse: Pattern C — Simple Yes/No
    // ==========================================================

    [Fact]
    public void Parse_TwoOption_YesNo_Newline()
    {
        // Real log: [21:59:08]
        var text = "proceed?\n > 1. Yes\n   2. No\n\n ";
        var result = ApprovalParser.ParseApprovalPrompt(text);

        Assert.NotNull(result);
        Assert.Equal(2, result.Options.Count);
        Assert.Equal("Yes", result.Options[0].Text);
        Assert.Equal("No", result.Options[1].Text);
    }

    [Fact]
    public void Parse_TwoOption_YesNo_Compact()
    {
        // Real log: [21:59:33]
        var text = "proceed?\n>1.Yes\n2.No\n\n ";
        var result = ApprovalParser.ParseApprovalPrompt(text);

        Assert.NotNull(result);
        Assert.Equal(2, result.Options.Count);
        Assert.Equal("Yes", result.Options[0].Text);
        Assert.Equal("No", result.Options[1].Text);
    }

    // ==========================================================
    //  6. Parse: Inline/space-separated pattern (Pattern B spaces)
    // ==========================================================

    [Fact]
    public void Parse_InlineSpaceSeparated()
    {
        // Space-separated format: "> 1. Yes              2. Yes,...              3. No"
        var text = "proceed?            > 1. Yes              2. Yes, allow reading from ICON/ during this session              3. No           \n";
        var result = ApprovalParser.ParseApprovalPrompt(text);

        Assert.NotNull(result);
        Assert.True(result.Options.Count >= 2, $"Expected >=2 options, got {result.Options.Count}");
    }

    // ==========================================================
    //  7. Command Extraction
    // ==========================================================

    [Fact]
    public void Parse_ExtractsCommandFromRequiresApproval()
    {
        // FIXED: Now correctly extracts the line BEFORE "This command requires approval"
        var text = "Find Teams window in window list\nThis command requires approval\n\nDo you want to proceed?\n>1.Yes\n2.No\n3.No\n";
        var result = ApprovalParser.ParseApprovalPrompt(text);

        Assert.NotNull(result);
        Assert.Contains("Find Teams window in window list", result.Command);
    }

    [Fact]
    public void Parse_ExtractsRunShellCommand()
    {
        // When "Run shell command ..." is on the same line as "requires approval"
        var text = "Run shell command dotnet test requires approval\n\nDo you want to proceed?\n>1.Yes\n2.No\n";
        var result = ApprovalParser.ParseApprovalPrompt(text);

        Assert.NotNull(result);
        Assert.Contains("dotnet test", result.Command);
    }

    [Fact]
    public void Parse_CommandEmpty_WhenNoPrecedingText()
    {
        // Real scenario: most auto-approve cases have empty cmd
        var text = "proceed?\n>1.Yes\n2.No\n";
        var result = ApprovalParser.ParseApprovalPrompt(text);

        Assert.NotNull(result);
        Assert.Equal("", result.Command);
    }

    // ==========================================================
    //  8. Esc Boundary Truncation
    // ==========================================================

    [Fact]
    public void Parse_TruncatesAtEscToCancel()
    {
        var text = "proceed?\n>1.Yes\n2.No\nEsc to cancel · Tab to amend\nSome other text with 4. option";
        var result = ApprovalParser.ParseApprovalPrompt(text);

        Assert.NotNull(result);
        Assert.Equal(2, result.Options.Count);
        // Should NOT find option 4 after "Esc to cancel"
        Assert.DoesNotContain(result.Options, o => o.Number == 4);
    }

    // ==========================================================
    //  9. Fallback Scenario
    // ==========================================================

    [Fact]
    public void Parse_FallbackWhenNoOptionsFound()
    {
        // Text has "proceed" but no numbered options
        var text = "Do you want to proceed? Some garbled text without numbers";
        var result = ApprovalParser.ParseApprovalPrompt(text);

        Assert.NotNull(result);
        Assert.True(result.IsFallback);
        Assert.Equal(3, result.Options.Count);
        Assert.Equal("Yes", result.Options[0].Text);
        Assert.Equal("Yes, and don't ask again", result.Options[1].Text);
        Assert.Equal("No", result.Options[2].Text);
    }

    // ==========================================================
    //  10. Duplicate Option Dedup
    // ==========================================================

    [Fact]
    public void Parse_DeduplicatesSameOptionNumber()
    {
        // Real issue: [20:08:28] — overlapping ConPTY renders created duplicate options
        var text = "proceed?            > 1. Yes              2. Yes, allow reading from ICON/ during this session              3. No           \n" +
                   "proceed?            > 1. Yes              2. Yes, allow reading from ICON/ during this session              3. No           \n" +
                   "Esc to cancel · Tab to amend";
        var result = ApprovalParser.ParseApprovalPrompt(text);

        Assert.NotNull(result);
        // Each option number should appear only once
        var numbers = result.Options.Select(o => o.Number).ToList();
        Assert.Equal(numbers.Distinct().Count(), numbers.Count);
    }

    // ==========================================================
    //  11. Buffer Management (Sliding Window)
    // ==========================================================

    [Fact]
    public void AppendToBuffer_TrimsToMaxLength()
    {
        var buffer = new string('A', 3000);
        var newChunk = new string('B', 2000);
        var result = ApprovalParser.AppendToBuffer(buffer, newChunk, maxLen: 4000);

        Assert.Equal(4000, result.Length);
        // Should keep the tail (more B's than A's since tail is preserved)
        Assert.EndsWith("B", result);
    }

    [Fact]
    public void AppendToBuffer_NoTrimWhenUnderLimit()
    {
        var buffer = "Hello";
        var newChunk = " World";
        var result = ApprovalParser.AppendToBuffer(buffer, newChunk, maxLen: 4000);

        Assert.Equal("Hello World", result);
    }

    // ==========================================================
    //  12. Duplicate Detection in Buffer (re-trigger scenario)
    // ==========================================================

    [Fact]
    public void DuplicateDetection_SamePromptRedetected()
    {
        // Simulates the dotnet test:* scenario: same prompt stays in buffer
        // and gets detected 4 times (22:07:00, 22:07:12, 22:07:30, 22:09:57)
        var approvalText = "proceed?\n > 1. Yes\n   2. Yes, and don't ask again for: dotnet test:*\n   3. No\n\n ";

        // First detection
        var buffer = approvalText;
        Assert.True(ApprovalParser.ContainsApprovalPattern(buffer));
        var result1 = ApprovalParser.ParseApprovalPrompt(buffer);
        Assert.NotNull(result1);
        Assert.Equal(3, result1.Options.Count);

        // After detection, buffer should be cleared (simulate)
        buffer = "";

        // New terminal output that doesn't contain approval
        buffer = ApprovalParser.AppendToBuffer(buffer, "some normal output\n");
        Assert.False(ApprovalParser.ContainsApprovalPattern(buffer));

        // Approval prompt re-appears (new chunk from ConPTY re-render)
        buffer = ApprovalParser.AppendToBuffer(buffer, approvalText);
        Assert.True(ApprovalParser.ContainsApprovalPattern(buffer));

        // This is the "re-detection" bug — same prompt matched again
        var result2 = ApprovalParser.ParseApprovalPrompt(buffer);
        Assert.NotNull(result2);
        Assert.Equal(3, result2.Options.Count);
    }

    [Fact]
    public void DuplicateDetection_BufferClearPreventsRetrigger()
    {
        // "proceed?" alone doesn't match — need "Do you want to proceed" or "requires approval"
        var approvalText = "Do you want to proceed?\n > 1. Yes\n   2. No\n\n ";

        var buffer = approvalText;
        Assert.True(ApprovalParser.ContainsApprovalPattern(buffer));

        // Simulate: after detection, buffer is cleared
        buffer = "";
        Assert.False(ApprovalParser.ContainsApprovalPattern(buffer));

        // Non-approval output comes in
        buffer = ApprovalParser.AppendToBuffer(buffer, "Task completed successfully.\nFile written.\n");
        Assert.False(ApprovalParser.ContainsApprovalPattern(buffer));
    }

    [Fact]
    public void DuplicateDetection_OverlappingBufferChunks()
    {
        // Simulates ConPTY sending overlapping chunks that contain partial approval text
        var buffer = "";

        // Chunk 1: partial approval text
        buffer = ApprovalParser.AppendToBuffer(buffer, "Do you want to ");
        Assert.False(ApprovalParser.ContainsApprovalPattern(buffer));

        // Chunk 2: completes the pattern
        buffer = ApprovalParser.AppendToBuffer(buffer, "proceed?\n>1.Yes\n2.No\n");
        Assert.True(ApprovalParser.ContainsApprovalPattern(buffer));

        var result = ApprovalParser.ParseApprovalPrompt(buffer);
        Assert.NotNull(result);
        Assert.Equal(2, result.Options.Count);
    }

    // ==========================================================
    //  13. Real-World Full Buffer Scenarios (from logs)
    // ==========================================================

    [Fact]
    public void FullScenario_GrepCommandApproval()
    {
        // Simulates the complete flow: raw terminal -> strip ANSI -> detect -> parse
        var rawTerminal = "\x1B[38;2;255;255;255mThis command requires approval\x1B[0m\n\n" +
                          "Do you want to proceed?\n" +
                          ">1.Yes\n" +
                          "   2. Yes, and don't ask again for: grep:*\n" +
                          "   3. No\n\n" +
                          "Esc to cancel · Tab to amend";

        var clean = ApprovalParser.StripAnsiCodes(rawTerminal);
        Assert.True(ApprovalParser.ContainsApprovalPattern(clean));

        var result = ApprovalParser.ParseApprovalPrompt(clean);
        Assert.NotNull(result);
        Assert.Equal(3, result.Options.Count);
        Assert.Equal(1, result.Options[0].Number);
        Assert.Equal(2, result.Options[1].Number);
        Assert.Equal(3, result.Options[2].Number);
        Assert.Contains("grep", result.Options[1].Text);
    }

    [Fact]
    public void FullScenario_GitPushWithCommandExtraction()
    {
        var text = "git push origin main               Push to remote main                            This command requires approval\n\n" +
                   "Do you want to proceed?\n" +
                   ">1.Yes\n" +
                   "2.Yes,anddon'taskagainfor:gitpush:*\n" +
                   "3.No\n";

        var result = ApprovalParser.ParseApprovalPrompt(text);
        Assert.NotNull(result);
        Assert.Equal(3, result.Options.Count);
        // Command should be extracted (not empty)
        Assert.NotEmpty(result.Command);
    }

    [Fact]
    public void FullScenario_WhitespaceCollapsedOptions()
    {
        // ConPTY strips spaces, creating collapsed text
        var text = "requires approval                                                                \n\n" +
                   "Doyouwanttoproceed?             \n" +
                   ">1.Yes\n" +
                   "2.Yes,anddon'taskagainfor:AgentZero.ps1get-window-info:*\n" +
                   "3.No\n" +
                   "Esctocancel·Tab";

        var result = ApprovalParser.ParseApprovalPrompt(text);
        Assert.NotNull(result);
        // Should still find options even with collapsed whitespace
        Assert.True(result.Options.Count >= 2, $"Expected >=2 options, got {result.Options.Count}");
    }

    [Fact]
    public void FullScenario_ImageGenApproval()
    {
        // Real log: [20:03:07]
        var text = "proceed?\n>1. Yes\n  2. Yes, and don't ask again for image-gen in D:\\MYNOTE\n3.No\nEsc to cancel · Tab to amend";
        var result = ApprovalParser.ParseApprovalPrompt(text);

        Assert.NotNull(result);
        Assert.True(result.Options.Count >= 2);
        Assert.Contains(result.Options, o => o.Text.Contains("image-gen"));
    }

    [Fact]
    public void FullScenario_GrepWithComplexPattern()
    {
        // Real log: [21:59:15]
        var text = "proceed?       \n>1.Yes\n" +
                   "2.Yes,anddon'taskagainfor:grep-o'\"tool_name\":\"[^\"]*\"'~/.claude/projects/D--Code-AI-CodeMap-CodeScan/*.jsonl\n" +
                   "3.No\n";
        var result = ApprovalParser.ParseApprovalPrompt(text);

        Assert.NotNull(result);
        Assert.Equal(3, result.Options.Count);
        Assert.Contains("grep", result.Options[1].Text);
    }

    // ==========================================================
    //  14. Edge Cases
    // ==========================================================

    [Fact]
    public void Parse_EmptyString()
    {
        var result = ApprovalParser.ParseApprovalPrompt("");
        Assert.NotNull(result);
        // Fallback should kick in
        Assert.Equal(3, result.Options.Count);
    }

    [Fact]
    public void Parse_OnlyRequiresApproval_NoOptions()
    {
        var text = "This command requires approval";
        var result = ApprovalParser.ParseApprovalPrompt(text);
        Assert.NotNull(result);
        // Fallback
        Assert.Equal(3, result.Options.Count);
    }

    [Fact]
    public void Parse_OptionNumberOutOfRange_Ignored()
    {
        var text = "proceed?\n>6.Invalid\n7.AlsoInvalid\n";
        var result = ApprovalParser.ParseApprovalPrompt(text);
        Assert.NotNull(result);
        // Options 6,7 are outside 1-5 range → fallback
        Assert.Equal(3, result.Options.Count);
        Assert.Equal("Yes", result.Options[0].Text);
    }

    [Fact]
    public void Buffer_ExactlyAtMaxLength()
    {
        var buffer = new string('X', 4000);
        var result = ApprovalParser.AppendToBuffer(buffer, "", maxLen: 4000);
        Assert.Equal(4000, result.Length);
    }

    [Fact]
    public void Buffer_OneOverMaxTrims()
    {
        var buffer = new string('X', 4000);
        var result = ApprovalParser.AppendToBuffer(buffer, "Y", maxLen: 4000);
        Assert.Equal(4000, result.Length);
        Assert.EndsWith("Y", result);
    }

    // ==========================================================
    //  15. Command Extraction Improvements
    // ==========================================================

    [Fact]
    public void Parse_CommandFromLineBeforeThisCommand()
    {
        // "This command requires approval" is a boilerplate line;
        // the actual command description is the line BEFORE it
        var text = "git push origin main\nThis command requires approval\n\nDo you want to proceed?\n>1.Yes\n2.No\n";
        var result = ApprovalParser.ParseApprovalPrompt(text);

        Assert.NotNull(result);
        Assert.Equal("git push origin main", result.Command);
    }

    [Fact]
    public void Parse_CommandFromSameLineAsRequiresApproval()
    {
        // When "requires approval" is on the same line as the command (no "This command" prefix)
        var text = "git push origin main               Push to remote main                            This command requires approval\n\nDo you want to proceed?\n>1.Yes\n2.No\n";
        var result = ApprovalParser.ParseApprovalPrompt(text);

        Assert.NotNull(result);
        // "This command" prefix triggers look-back to previous line
        Assert.NotEmpty(result.Command);
    }

    [Fact]
    public void Parse_CommandWithRunShellCommandPrefix()
    {
        var text = "Run shell command dotnet build requires approval\n\nDo you want to proceed?\n>1.Yes\n2.No\n";
        var result = ApprovalParser.ParseApprovalPrompt(text);

        Assert.NotNull(result);
        Assert.Contains("dotnet build", result.Command);
    }

    // ==========================================================
    //  16. Fingerprint (Deduplication)
    // ==========================================================

    [Fact]
    public void Fingerprint_SamePromptReturnsSameValue()
    {
        var text1 = "Do you want to proceed?\n>1. Yes\n   2. Yes, and don't ask again for: dotnet test:*\n   3. No\n\n ";
        var text2 = "Do you want to proceed?\n>1. Yes\n   2. Yes, and don't ask again for: dotnet test:*\n   3. No\n\n ";

        var fp1 = ApprovalParser.GetApprovalFingerprint(text1);
        var fp2 = ApprovalParser.GetApprovalFingerprint(text2);

        Assert.NotNull(fp1);
        Assert.Equal(fp1, fp2);
    }

    [Fact]
    public void Fingerprint_SameWhitespace_SameResult()
    {
        // Same rendering with minor whitespace differences (spaces vs newlines)
        var text1 = "proceed?\n > 1. Yes\n   2. Yes, and don't ask again for: dotnet test:*\n   3. No\n\n ";
        var text2 = "proceed?\n > 1. Yes\n   2. Yes, and don't ask again for: dotnet test:*\n   3. No\n";

        var fp1 = ApprovalParser.GetApprovalFingerprint(text1);
        var fp2 = ApprovalParser.GetApprovalFingerprint(text2);

        Assert.NotNull(fp1);
        Assert.Equal(fp1, fp2);
    }

    [Fact]
    public void Fingerprint_ConPTYCollapsedVsNormal_AreDifferent()
    {
        // ConPTY-collapsed text (no spaces) vs normal text are different renderings.
        // This is expected — fingerprint catches re-detection of the SAME rendering,
        // not different renderings of the same logical prompt.
        var text1 = "proceed?\n > 1. Yes\n   2. Yes, and don't ask again for: dotnet test:*\n   3. No\n";
        var text2 = "proceed?\n>1.Yes\n2.Yes,anddon'taskagainfor:dotnettest:*\n3.No\n";

        var fp1 = ApprovalParser.GetApprovalFingerprint(text1);
        var fp2 = ApprovalParser.GetApprovalFingerprint(text2);

        Assert.NotNull(fp1);
        Assert.NotNull(fp2);
        // Different renderings produce different fingerprints — this is OK
        // The primary dedup target is the same rendering repeated in buffer
        Assert.NotEqual(fp1, fp2);
    }

    [Fact]
    public void Fingerprint_DifferentPrompts_DifferentValues()
    {
        var text1 = "proceed?\n>1.Yes\n2.Yes, and don't ask again for: grep:*\n3.No\n";
        var text2 = "proceed?\n>1.Yes\n2.Yes, and don't ask again for: dotnet test:*\n3.No\n";

        var fp1 = ApprovalParser.GetApprovalFingerprint(text1);
        var fp2 = ApprovalParser.GetApprovalFingerprint(text2);

        Assert.NotNull(fp1);
        Assert.NotNull(fp2);
        Assert.NotEqual(fp1, fp2);
    }

    [Fact]
    public void Fingerprint_NoApproval_ReturnsNull()
    {
        var text = "normal terminal output with no approval prompt";
        var fp = ApprovalParser.GetApprovalFingerprint(text);
        Assert.Null(fp);
    }

    [Fact]
    public void Fingerprint_TruncatesAtEscBoundary()
    {
        var text1 = "proceed?\n>1.Yes\n2.No\nEsc to cancel · Tab to amend\nExtra text";
        var text2 = "proceed?\n>1.Yes\n2.No\nEsc to cancel · Tab to amend\nDifferent extra text";

        var fp1 = ApprovalParser.GetApprovalFingerprint(text1);
        var fp2 = ApprovalParser.GetApprovalFingerprint(text2);

        Assert.NotNull(fp1);
        Assert.Equal(fp1, fp2);
    }

    [Fact]
    public void Fingerprint_WithPrecedingBufferContent()
    {
        // Buffer contains prior output before the approval prompt
        var text1 = "lots of prior output...\nDo you want to proceed?\n>1.Yes\n2.No\n";
        var text2 = "different prior output...\nDo you want to proceed?\n>1.Yes\n2.No\n";

        var fp1 = ApprovalParser.GetApprovalFingerprint(text1);
        var fp2 = ApprovalParser.GetApprovalFingerprint(text2);

        Assert.NotNull(fp1);
        // Fingerprint starts from "proceed" so prior content doesn't matter
        Assert.Equal(fp1, fp2);
    }

    // ==========================================================
    //  17. False Positive: Diff/code content containing approval keywords
    // ==========================================================
    //  Real issue from captured logs:
    //  When Claude outputs a diff that contains strings like
    //  "requires approval" or "This command" as code content,
    //  the watcher falsely detects it as an approval prompt.
    //  e.g. [Auto-Approve] "This command   (from diff line)

    [Fact]
    public void FalsePositive_DiffContainingRequiresApproval()
    {
        // A diff that mentions "requires approval" as code, not an actual prompt
        var buffer =
            "  42 -        // Extract command: look for the line before \"This command\n" +
            "  43 -        var reqIdx = text.IndexOf(\"requires approval\", StringComparison.OrdinalIgnoreCase);\n" +
            "  44 +        // FIXED: Now correctly extracts the line BEFORE\n" +
            "  45 +        var reqIdx = text.IndexOf(\"requires approval\", StringComparison.OrdinalIgnoreCase);\n";

        // Pattern detection matches because the keyword is present
        Assert.True(ApprovalParser.ContainsApprovalPattern(buffer));

        // But parsing finds no numbered options → IsFallback = true
        var result = ApprovalParser.ParseApprovalPrompt(buffer);
        Assert.NotNull(result);
        Assert.True(result.IsFallback);
        Assert.Equal(3, result.Options.Count);
    }

    [Fact]
    public void FalsePositive_DiffWithQuotedThisCommand()
    {
        // Real captured log: [Auto-Approve] "This command
        var buffer =
            "222 +        // Current behavior: \"This command\" is extracted because the parser finds the line\n" +
            "223 +        // containing \"requires approval\" and takes that whole line\n" +
            "224 +        Assert.Equal(\"This command\", result.Command);\n";

        var result = ApprovalParser.ParseApprovalPrompt(buffer);
        Assert.NotNull(result);
        Assert.True(result.IsFallback);
    }

    [Fact]
    public void FalsePositive_AutoApproveLogLineInBuffer()
    {
        // Real captured: [Auto-Approve] 292 -_recentBuffer.Contains("
        var buffer =
            "[Auto-Approve] 86 +- Command extraction from \"requires approval\"\n" +
            "[Auto-Approve] dotnet test\\nThis command\n" +
            "Some other terminal output here\n";

        var result = ApprovalParser.ParseApprovalPrompt(buffer);
        Assert.NotNull(result);
        Assert.True(result.IsFallback);
    }

    [Fact]
    public void FalsePositive_RealApprovalVsDiffContent()
    {
        // Real approval has numbered options → IsFallback = false
        // Diff content has no options → IsFallback = true
        var realApproval = "This command requires approval\n\nDo you want to proceed?\n>1. Yes\n   2. No\n\n";
        var diffContent = "  42 -        var reqIdx = text.IndexOf(\"requires approval\");\n  43 +        var reqIdx = text.IndexOf(\"requires approval\");\n";

        var realResult = ApprovalParser.ParseApprovalPrompt(realApproval);
        var diffResult = ApprovalParser.ParseApprovalPrompt(diffContent);

        Assert.NotNull(realResult);
        Assert.NotNull(diffResult);

        // Real approval: parsed options, NOT fallback
        Assert.False(realResult.IsFallback);
        Assert.Equal(2, realResult.Options.Count);

        // Diff content: fallback, would be skipped by auto-approve
        Assert.True(diffResult.IsFallback);
        Assert.Equal(3, diffResult.Options.Count);
    }

    [Fact]
    public void FalsePositive_RealPromptIsNotFallback()
    {
        // Verify all real prompt patterns from earlier tests are NOT fallback
        var texts = new[]
        {
            "proceed?\n>1.Yes\n   2. Yes, and don't ask again for: grep:*\n   3. No\n\n ",
            "proceed?\n > 1. Yes\n   2. No\n\n ",
            "proceed?\n > 1. Yes\n   2. Yes, and don't ask again for: dotnet test:*\n   3. No\n\n ",
        };

        foreach (var text in texts)
        {
            var result = ApprovalParser.ParseApprovalPrompt(text);
            Assert.NotNull(result);
            Assert.False(result.IsFallback, $"Real prompt should not be fallback: {text[..Math.Min(40, text.Length)]}...");
        }
    }

    // ==========================================================
    //  18. Edit Approval Prompt ("Do you want to make this edit")
    //      Real failure: auto-approve didn't fire for Edit tool
    //      because ContainsApprovalPattern doesn't detect
    //      "Do you want to make this edit to ..." pattern.
    //      Test data in TestData/*.txt
    // ==========================================================

    private static string LoadTestData(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", fileName);
        return File.ReadAllText(path);
    }

    [Fact]
    public void EditApproval_ContainsPattern_ClipboardCase()
    {
        // "Do you want to make this edit to AgentBotWindow.xaml?" is NOT detected
        // by current ContainsApprovalPattern (expects "proceed" / "requires approval" / "don't ask again")
        var text = LoadTestData("edit_approval_xaml_clipboard.txt");
        var detected = ApprovalParser.ContainsApprovalPattern(text);

        // EXPECTED TO FAIL: current logic does not recognize "make this edit" pattern
        Assert.True(detected, "ContainsApprovalPattern should detect 'Do you want to make this edit' pattern");
    }

    [Fact]
    public void EditApproval_ContainsPattern_MultilineCase()
    {
        var text = LoadTestData("edit_approval_textbox_multiline.txt");
        var detected = ApprovalParser.ContainsApprovalPattern(text);

        // EXPECTED TO FAIL: same root cause
        Assert.True(detected, "ContainsApprovalPattern should detect 'Do you want to make this edit' pattern");
    }

    [Fact]
    public void EditApproval_ParseOptions_ClipboardCase()
    {
        // Even if detection worked, verify option parsing handles this format
        var text = LoadTestData("edit_approval_xaml_clipboard.txt");
        var result = ApprovalParser.ParseApprovalPrompt(text);

        Assert.NotNull(result);
        // Current behavior: since there's no "proceed" keyword, parser starts from index 0
        // and may or may not find the numbered options depending on regex matching
        // With the edit prompt format, options are:
        //   > 1. Yes
        //   2. Yes, allow all edits during this session (shift+tab)
        //   3. No
        Assert.True(result.Options.Count >= 2,
            $"Expected >=2 options from edit approval, got {result.Options.Count}. IsFallback={result.IsFallback}");
        Assert.False(result.IsFallback, "Edit approval with numbered options should not be fallback");
    }

    [Fact]
    public void EditApproval_ParseOptions_MultilineCase()
    {
        var text = LoadTestData("edit_approval_textbox_multiline.txt");
        var result = ApprovalParser.ParseApprovalPrompt(text);

        Assert.NotNull(result);
        Assert.True(result.Options.Count >= 2,
            $"Expected >=2 options from edit approval, got {result.Options.Count}. IsFallback={result.IsFallback}");
        Assert.False(result.IsFallback, "Edit approval with numbered options should not be fallback");
    }

    [Theory]
    [InlineData("Do you want to make this edit to AgentBotWindow.xaml?")]
    [InlineData("Do you want to make this edit to MainWindow.xaml.cs?")]
    [InlineData("Do you want to make this edit to foo.txt?")]
    public void EditApproval_ContainsPattern_InlineVariants(string text)
    {
        // EXPECTED TO FAIL: current logic does not cover "make this edit" keyword
        Assert.True(ApprovalParser.ContainsApprovalPattern(text),
            $"Should detect edit approval pattern: {text}");
    }

    [Fact]
    public void EditApproval_OptionText_AllowAllEdits()
    {
        // The edit approval uses "Yes, allow all edits during this session" instead of "don't ask again"
        var text = " Do you want to make this edit to AgentBotWindow.xaml?\n\n" +
                   " > 1. Yes\n\n" +
                   "   2. Yes, allow all edits during this session (shift+tab)\n\n" +
                   "   3. No\n\n\n\n" +
                   " Esc to cancel · Tab to amend\n";
        var result = ApprovalParser.ParseApprovalPrompt(text);

        Assert.NotNull(result);
        Assert.Equal(3, result.Options.Count);
        Assert.Equal("Yes", result.Options[0].Text);
        Assert.Contains("allow all edits", result.Options[1].Text);
        Assert.Equal("No", result.Options[2].Text);
        Assert.False(result.IsFallback);
    }

    [Fact]
    public void EditApproval_Fingerprint_Stable()
    {
        var text1 = " Do you want to make this edit to AgentBotWindow.xaml?\n > 1. Yes\n   2. Yes, allow all edits during this session (shift+tab)\n   3. No\n\n Esc to cancel\n";
        var text2 = " Do you want to make this edit to AgentBotWindow.xaml?\n > 1. Yes\n   2. Yes, allow all edits during this session (shift+tab)\n   3. No\n\n Esc to cancel\n";

        var fp1 = ApprovalParser.GetApprovalFingerprint(text1);
        var fp2 = ApprovalParser.GetApprovalFingerprint(text2);

        // EXPECTED TO FAIL: GetApprovalFingerprint looks for "proceed" or "requires approval"
        // and "make this edit" won't match either, so fingerprint will be null
        Assert.NotNull(fp1);
        Assert.Equal(fp1, fp2);
    }

    // ==========================================================
    //  19. InferCommandFromContext — unknown command resolution
    // ==========================================================

    [Fact]
    public void Infer_ShellCommand_DontAskAgainForColon()
    {
        // "Yes, and don't ask again for: dotnet test:*" → "dotnet test"
        var text = "proceed?\n>1. Yes\n   2. Yes, and don't ask again for: dotnet test:*\n   3. No\n";
        var result = ApprovalParser.ParseApprovalPrompt(text);

        Assert.NotNull(result);
        Assert.Equal("dotnet test", result.Command);
    }

    [Fact]
    public void Infer_ShellCommand_GrepComplex()
    {
        // "Yes,anddon'taskagainfor:grep-o'...':*" → collapsed whitespace
        var text = "proceed?\n>1.Yes\n2.Yes,anddon'taskagainfor:grep-o'\"tool_name\":\"[^\"]*\"'~/.claude/projects/*.jsonl\n3.No\n";
        var result = ApprovalParser.ParseApprovalPrompt(text);

        Assert.NotNull(result);
        Assert.False(string.IsNullOrEmpty(result.Command), "Should infer grep command from option text");
    }

    [Fact]
    public void Infer_CompoundCommand_GitAddCommit()
    {
        // "Yes, and don't ask again for git add and git commit -m ' commands in D:\Code\AI\..."
        var text = "proceed?\n > 1. Yes\n   2. Yes, and don't ask again for git add and git commit -m ' commands in D:\\Code\\AI\\CodeMap\\CodeScan\n   3. No\n\n ";
        var result = ApprovalParser.ParseApprovalPrompt(text);

        Assert.NotNull(result);
        Assert.Contains("git add", result.Command);
        Assert.Contains("CodeScan", result.Command);
    }

    [Fact]
    public void Infer_FileRead_FromThisProject()
    {
        // "Yes, allow reading from tmp\ from this project"
        var text = "proceed?\n>1.Yes\n2.Yes,allowreadingfromtmp\\fromthisproject\n3.No\n";
        var result = ApprovalParser.ParseApprovalPrompt(text);

        Assert.NotNull(result);
        Assert.StartsWith("Read:", result.Command);
        Assert.Contains("tmp", result.Command);
    }

    [Fact]
    public void Infer_FileRead_DuringThisSession()
    {
        // "Yes, allow reading from ICON/ during this session"
        var text = "proceed?            > 1. Yes              2. Yes, allow reading from ICON/ during this session              3. No           \n";
        var result = ApprovalParser.ParseApprovalPrompt(text);

        Assert.NotNull(result);
        Assert.StartsWith("Read:", result.Command);
        Assert.Contains("ICON", result.Command);
    }

    [Fact]
    public void Infer_EditFile_MakeThisEdit()
    {
        // "Do you want to make this edit to AgentBotWindow.xaml?"
        var text = " Do you want to make this edit to AgentBotWindow.xaml?\n\n" +
                   " > 1. Yes\n" +
                   "   2. Yes, allow all edits during this session (shift+tab)\n" +
                   "   3. No\n\n" +
                   " Esc to cancel · Tab to amend\n";
        var result = ApprovalParser.ParseApprovalPrompt(text);

        Assert.NotNull(result);
        Assert.Equal("Edit: AgentBotWindow.xaml", result.Command);
    }

    [Fact]
    public void Infer_EditFile_AllowAllEdits()
    {
        // Option 2 = "allow all edits during this session" without filename in buffer
        var text = "proceed?\n > 1. Yes\n   2. Yes, allow all edits during this session\n   3. No\n";
        var result = ApprovalParser.ParseApprovalPrompt(text);

        Assert.NotNull(result);
        Assert.Equal("Edit file", result.Command);
    }

    [Fact]
    public void Infer_SimpleYesNo_StaysEmpty()
    {
        // Only Yes/No options — no context to infer from
        var text = "proceed?\n>1.Yes\n2.No\n";
        var result = ApprovalParser.ParseApprovalPrompt(text);

        Assert.NotNull(result);
        Assert.Equal("", result.Command);
    }

    [Fact]
    public void Infer_ImageGenCommand()
    {
        // "Yes, and don't ask again for image-gen in D:\MYNOTE"
        var text = "proceed?\n>1. Yes\n  2. Yes, and don't ask again for image-gen in D:\\MYNOTE\n3.No\nEsc to cancel";
        var result = ApprovalParser.ParseApprovalPrompt(text);

        Assert.NotNull(result);
        Assert.Contains("image-gen", result.Command);
    }

    [Fact]
    public void Infer_DoesNotOverrideExistingCommand()
    {
        // When command is already extracted, InferCommandFromContext should NOT be called
        var text = "Find Teams window in window list\nThis command requires approval\n\nDo you want to proceed?\n>1.Yes\n2.No\n3.No\n";
        var result = ApprovalParser.ParseApprovalPrompt(text);

        Assert.NotNull(result);
        Assert.Contains("Find Teams window", result.Command);
    }

    // ==========================================================
    //  20. Separator-based detection (─ solid line pattern)
    //      Real failure: Bash/Read approval prompts not detected
    //      because keyword matching fails on ConPTY-rendered output.
    //      Fix: detect ─── separator + numbered options as trigger.
    // ==========================================================

    private static string MakeSolidSeparator(int len = 80) => new('\u2500', len);
    private static string MakeDashedSeparator(int len = 80) => new('\u254C', len);

    [Fact]
    public void SeparatorDetection_BashCommand_Detected()
    {
        // Real case: Bash command approval with ─── separator
        var text =
            MakeSolidSeparator() + " Bash command\n\n" +
            "   ls -la \"D:/code/AI/agent-win/.claude/skills/agent-zero/\"\n\n" +
            "   List source agent-zero skills\n\n" +
            " Do you want to proceed?\n\n" +
            " > 1. Yes\n" +
            "   2. Yes, allow reading from agent-zero\\ from this project\n" +
            "   3. No\n\n" +
            " Esc to cancel\n";

        Assert.True(ApprovalParser.ContainsApprovalPattern(text));

        var result = ApprovalParser.ParseApprovalPrompt(text);
        Assert.NotNull(result);
        Assert.False(result.IsFallback);
        Assert.Equal(3, result.Options.Count);
        Assert.Equal("Yes", result.Options[0].Text);
    }

    [Fact]
    public void SeparatorDetection_BashCommand_NoKeywords_StillDetected()
    {
        // Separator + numbered options, but NO "requires approval" or "Do you want to proceed"
        // (simulates ConPTY stripping/corrupting keyword text)
        var text =
            MakeSolidSeparator() + " Bash command\n\n" +
            "   dotnet --list-runtimes 2>&1\n" +
            "   List installed runtimes\n\n" +
            " > 1. Yes\n" +
            "   2. Yes, and don't ask again for: dotnet:*\n" +
            "   3. No\n\n";

        Assert.True(ApprovalParser.ContainsApprovalPattern(text));
    }

    [Fact]
    public void SeparatorDetection_ReadFile_Detected()
    {
        // Real case: Read file approval
        var text =
            MakeSolidSeparator() + " Read file\n\n" +
            "   Read(D:\\code\\AI\\agent-win\\.claude\\skills\\agent-zero\\SKILL.md)\n\n" +
            " Do you want to proceed?\n\n" +
            " > 1. Yes\n" +
            "   2. Yes, allow reading from agent-zero/ during this session\n" +
            "   3. No\n\n" +
            " Esc to cancel · Tab to amend\n";

        Assert.True(ApprovalParser.ContainsApprovalPattern(text));

        var result = ApprovalParser.ParseApprovalPrompt(text);
        Assert.NotNull(result);
        Assert.False(result.IsFallback);
        Assert.Equal(3, result.Options.Count);
    }

    [Fact]
    public void SeparatorDetection_ExtractCommand_BashCommand()
    {
        var text =
            MakeSolidSeparator() + " Bash command\n\n" +
            "   dotnet --list-runtimes 2>&1\n\n" +
            " > 1. Yes\n" +
            "   2. Yes, and don't ask again for: dotnet:*\n" +
            "   3. No\n";

        var result = ApprovalParser.ParseApprovalPrompt(text);
        Assert.NotNull(result);
        // Command should be extracted from after separator
        Assert.False(string.IsNullOrEmpty(result.Command));
        Assert.Contains("Bash", result.Command);
    }

    [Fact]
    public void SeparatorDetection_ShortSeparator_NotDetected()
    {
        // ─ line shorter than threshold should NOT trigger
        var text = new string('\u2500', 10) + " Bash command\n> 1. Yes\n2. No\n";
        Assert.False(ApprovalParser.ContainsSolidSeparator(text));
    }

    [Fact]
    public void SeparatorDetection_NoOptions_NotDetected()
    {
        // ─── line but no numbered options → not an approval prompt
        var text = MakeSolidSeparator() + " Some heading\n\nJust normal text here\n";
        Assert.False(ApprovalParser.ContainsApprovalPattern(text));
    }

    [Fact]
    public void SeparatorDetection_Fingerprint_Works()
    {
        var text =
            MakeSolidSeparator() + " Bash command\n\n" +
            "   dotnet build\n\n" +
            " > 1. Yes\n" +
            "   2. Yes, and don't ask again for: dotnet:*\n" +
            "   3. No\n";

        var fp = ApprovalParser.GetApprovalFingerprint(text);
        Assert.NotNull(fp);
    }

    [Fact]
    public void SeparatorDetection_Fingerprint_StableAcrossCalls()
    {
        var text1 =
            MakeSolidSeparator() + " Read file\n" +
            " > 1. Yes\n   2. No\n   3. No\n";
        var text2 =
            MakeSolidSeparator() + " Read file\n" +
            " > 1. Yes\n   2. No\n   3. No\n";

        var fp1 = ApprovalParser.GetApprovalFingerprint(text1);
        var fp2 = ApprovalParser.GetApprovalFingerprint(text2);
        Assert.NotNull(fp1);
        Assert.Equal(fp1, fp2);
    }

    // ==========================================================
    //  21. Dashed block (╌) stripping for Edit approval
    //      Real failure: Edit approval includes diff/code blocks
    //      delimited by ╌╌╌ lines, polluting option parsing.
    // ==========================================================

    [Fact]
    public void StripDashedBlocks_RemovesDiffContent()
    {
        var text =
            " Edit file\n" +
            " Project\\AgentZeroWpf\\UI\\APP\\AgentBotWindow.xaml\n" +
            MakeDashedSeparator() + "\n" +
            " 130                                Foreground=\"{StaticResource TextDim}\"\n" +
            " 131                                ToolTip=\"Auto-approve\"\n" +
            " 132 +                    <Border Width=\"1\"/>\n" +
            MakeDashedSeparator() + "\n" +
            " Do you want to make this edit to AgentBotWindow.xaml?\n" +
            " > 1. Yes\n" +
            "   2. Yes, allow all edits during this session (shift+tab)\n" +
            "   3. No\n";

        var stripped = ApprovalParser.StripDashedBlocks(text);

        // Diff content should be removed
        Assert.DoesNotContain("Foreground", stripped);
        Assert.DoesNotContain("Border Width", stripped);
        // Approval content should remain
        Assert.Contains("make this edit", stripped);
        Assert.Contains("1. Yes", stripped);
        Assert.Contains("3. No", stripped);
    }

    [Fact]
    public void StripDashedBlocks_MultipleDiffBlocks()
    {
        var text =
            " Edit file\n" +
            " ITerminalSession.cs\n" +
            MakeDashedSeparator() + "\n" +
            " 51      Interrupt,\n" +
            " 52      Escape,\n" +
            " 53      Enter,\n" +
            " 54 +    Tab,\n" +
            MakeDashedSeparator() + "\n" +
            " 55      DownArrow,\n" +  // between blocks — kept
            MakeDashedSeparator() + "\n" +
            " 56      UpArrow,\n" +
            " 57      ClearScreen,\n" +
            MakeDashedSeparator() + "\n" +
            " Do you want to make this edit?\n" +
            " > 1. Yes\n" +
            "   2. Yes, allow all edits during this session\n" +
            "   3. No\n";

        var stripped = ApprovalParser.StripDashedBlocks(text);

        // First diff block removed
        Assert.DoesNotContain("Interrupt", stripped);
        Assert.DoesNotContain("Tab,", stripped);
        // Between blocks — kept
        Assert.Contains("DownArrow", stripped);
        // Second diff block removed
        Assert.DoesNotContain("UpArrow", stripped);
        // Options kept
        Assert.Contains("1. Yes", stripped);
    }

    [Fact]
    public void StripDashedBlocks_NoDashes_Unchanged()
    {
        var text = "Normal text\nWith no dashed lines\n> 1. Yes\n2. No\n";
        var stripped = ApprovalParser.StripDashedBlocks(text);
        Assert.Equal(text, stripped);
    }

    // ==========================================================
    //  Buffer-overflow reproducer:
    //  When the 4000-char sliding buffer cuts off the OPENING ╌╌╌
    //  marker, only the CLOSING marker survives. The current logic
    //  starts with inBlock=false, so it KEEPS the orphaned diff tail
    //  and STRIPS the actual prompt that follows the close marker.
    //  This is the root cause of intermittent Edit-file approval
    //  failures observed in the wild.
    // ==========================================================

    [Fact]
    public void StripDashedBlocks_OrphanedClosingMarker_PreservesPrompt()
    {
        // Buffer captured only the tail: a few diff lines, the closing
        // marker, then the actual prompt. The opening marker was sliced.
        var text =
            " 13 -        Icon=\"/agentzero.ico\">\n" +
            " 14 +        StateChanged=\"OnWindowStateChanged\">\n" +
            " 16      <Window.Resources>\n" +
            MakeDashedSeparator() + "\n" +
            " Do you want to make this edit to AgentBotWindow.xaml?\n" +
            " > 1. Yes\n" +
            "   2. Yes, allow all edits during this session (shift+tab)\n" +
            "   3. No\n";

        var stripped = ApprovalParser.StripDashedBlocks(text);

        // Orphaned diff tail must be removed (it was inside a block)
        Assert.DoesNotContain("StateChanged", stripped);
        Assert.DoesNotContain("Icon=", stripped);
        // Prompt must survive (it's outside the block)
        Assert.Contains("make this edit", stripped);
        Assert.Contains("1. Yes", stripped);
        Assert.Contains("3. No", stripped);
    }

    [Fact]
    public void EditApproval_BufferOverflowOpeningSliced_StillParses()
    {
        // Real failure mode: long diff overflows the 4000-char buffer,
        // opening ╌╌╌ is sliced off → ParseApprovalPrompt must still
        // recover 3 options from the prompt that follows the closing marker.
        var text =
            " 13 -        Icon=\"/agentzero.ico\">\n" +
            " 14 +        StateChanged=\"OnWindowStateChanged\">\n" +
            " 16      <Window.Resources>\n" +
            " 17          <SolidColorBrush x:Key=\"BgDeep\" Color=\"#1E1E1E\"/>\n" +
            MakeDashedSeparator() + "\n" +
            " Do you want to make this edit to AgentBotWindow.xaml?\n\n" +
            " > 1. Yes\n" +
            "   2. Yes, allow all edits during this session (shift+tab)\n" +
            "   3. No\n\n" +
            " Esc to cancel · Tab to amend\n";

        Assert.True(ApprovalParser.ContainsApprovalPattern(text));

        var result = ApprovalParser.ParseApprovalPrompt(text);
        Assert.NotNull(result);
        Assert.False(result.IsFallback,
            $"Expected real parse with 3 options, got fallback. Options.Count={result.Options.Count}");
        Assert.Equal(3, result.Options.Count);
        Assert.Equal("Yes", result.Options[0].Text);
        Assert.Contains("allow all edits", result.Options[1].Text);
        Assert.Equal("No", result.Options[2].Text);
    }

    [Fact]
    public void EventStream_BufferOverflowOpeningSliced_FiresEvent()
    {
        // End-to-end: feed a buffer-overflow shaped chunk through
        // AgentEventStream and verify it still emits ApprovalRequested
        // with 3 options (not fallback).
        var session = new FakeTerminalSession();
        using var stream = new AgentEventStream(session);

        var events = new List<AgentEvent>();
        stream.EventReceived += e => events.Add(e);

        session.SimulateOutput(
            " 13 -        Icon=\"/agentzero.ico\">\n" +
            " 14 +        StateChanged=\"OnWindowStateChanged\">\n" +
            " 16      <Window.Resources>\n" +
            MakeDashedSeparator() + "\n" +
            " Do you want to make this edit to AgentBotWindow.xaml?\n\n" +
            " > 1. Yes\n" +
            "   2. Yes, allow all edits during this session (shift+tab)\n" +
            "   3. No\n\n" +
            " Esc to cancel · Tab to amend\n");

        Assert.Single(events);
        var approval = Assert.IsType<ApprovalRequested>(events[0]);
        Assert.Equal(3, approval.Options.Count);
        Assert.False(approval.IsFallback);
    }

    [Fact]
    public void EditApproval_WithDiffBlocks_OptionsParsedCorrectly()
    {
        // Full real scenario: Edit approval with surrounding diff content
        var text =
            MakeSolidSeparator() + " Edit file\n\n" +
            " Project\\AgentZeroWpf\\UI\\APP\\AgentBotWindow.xaml\n" +
            MakeDashedSeparator() + "\n" +
            " 130                                Foreground=\"{StaticResource TextDim}\" VerticalAlignment=\"Center\"\n" +
            " 131                                ToolTip=\"Auto-approve terminal prompts\"\n" +
            " 132                                Checked=\"OnAutoApproveChanged\" Unchecked=\"OnAutoApproveChanged\"/>\n" +
            " 133 +                    <Border Width=\"1\" Background=\"{StaticResource BorderDim}\" Margin=\"6,2\"/>\n" +
            " 134 +                    <CheckBox x:Name=\"chkKeyForward\" IsChecked=\"True\"\n" +
            " 135 +                              Content=\"Key\" FontFamily=\"Consolas\" FontSize=\"10\"\n" +
            " 136 +                              Foreground=\"{StaticResource TextDim}\" VerticalAlignment=\"Center\"\n" +
            " 137 +                              ToolTip=\"Forward arrow/ESC/TAB keys to active terminal\"/>\n" +
            " 138                  </StackPanel>\n" +
            " 139                  <TextBlock Text=\"&#xE756;\" FontFamily=\"Segoe MDL2 Assets\" FontSize=\"12\"\n" +
            " 140                             Foreground=\"{StaticResource PurpleBrush}\" VerticalAlignment=\"Center\"\n" +
            MakeDashedSeparator() + "\n" +
            MakeDashedSeparator() + "\n" +
            " 51      Interrupt,    // Ctrl+C  (\\x03)\n" +
            " 52      Escape,       // ESC     (\\x1b)\n" +
            " 53      Enter,        // CR      (\\r)\n" +
            " 54 +    Tab,          // Tab     (\\t)\n" +
            " 55      DownArrow,    // ESC[B\n" +
            " 56      UpArrow,      // ESC[A\n" +
            " 57      ClearScreen,  // ESC[2J ESC[H\n" +
            MakeDashedSeparator() + "\n" +
            " Do you want to make this edit to ITerminalSession.cs?\n\n" +
            " > 1. Yes\n" +
            "   2. Yes, allow all edits during this session (shift+tab)\n" +
            "   3. No\n\n" +
            " Esc to cancel · Tab to amend\n";

        // Detection should work
        Assert.True(ApprovalParser.ContainsApprovalPattern(text));

        // Parse should find correct options (not polluted by diff line numbers)
        var result = ApprovalParser.ParseApprovalPrompt(text);
        Assert.NotNull(result);
        Assert.False(result.IsFallback);
        Assert.Equal(3, result.Options.Count);
        Assert.Equal("Yes", result.Options[0].Text);
        Assert.Contains("allow all edits", result.Options[1].Text);
        Assert.Equal("No", result.Options[2].Text);

        // Command should be extracted (Edit: filename or "Edit file")
        Assert.False(string.IsNullOrEmpty(result.Command));
    }

    // ==========================================================
    //  22. E2E: FakeTerminalSession + AgentEventStream with
    //      separator-based approval prompts
    // ==========================================================

    [Fact]
    public void EventStream_BashApproval_SeparatorBased_Detected()
    {
        var session = new FakeTerminalSession();
        using var stream = new AgentEventStream(session);

        var events = new List<AgentEvent>();
        stream.EventReceived += e => events.Add(e);

        // Simulate ConPTY output arriving in two chunks:
        // 1) separator + command description
        // 2) full approval prompt with all options
        session.SimulateOutput(MakeSolidSeparator() + " Bash command\n\n" +
            "   dotnet --list-runtimes 2>&1\n" +
            "   List installed runtimes\n\n");
        session.SimulateOutput(
            " Do you want to proceed?\n\n" +
            " > 1. Yes\n" +
            "   2. Yes, and don't ask again for: dotnet:*\n" +
            "   3. No\n\n" +
            " Esc to cancel\n");

        Assert.Single(events);
        var approval = Assert.IsType<ApprovalRequested>(events[0]);
        Assert.Equal(3, approval.Options.Count);
        Assert.False(approval.IsFallback);
    }

    [Fact]
    public void EventStream_ReadApproval_SeparatorBased_Detected()
    {
        var session = new FakeTerminalSession();
        using var stream = new AgentEventStream(session);

        var events = new List<AgentEvent>();
        stream.EventReceived += e => events.Add(e);

        session.SimulateOutput(
            MakeSolidSeparator() + " Read file\n\n" +
            "   Read(D:\\code\\AI\\agent-win\\.claude\\skills\\SKILL.md)\n\n" +
            " Do you want to proceed?\n\n" +
            " > 1. Yes\n" +
            "   2. Yes, allow reading from agent-zero/ during this session\n" +
            "   3. No\n\n" +
            " Esc to cancel\n");

        Assert.Single(events);
        var approval = Assert.IsType<ApprovalRequested>(events[0]);
        Assert.Equal(3, approval.Options.Count);
    }

    [Fact]
    public void EventStream_EditApproval_WithDiff_ParsedCleanly()
    {
        var session = new FakeTerminalSession();
        using var stream = new AgentEventStream(session);

        var events = new List<AgentEvent>();
        stream.EventReceived += e => events.Add(e);

        // Edit approval with diff blocks that should be stripped
        session.SimulateOutput(
            MakeSolidSeparator() + " Edit file\n\n" +
            " AgentBotWindow.xaml\n" +
            MakeDashedSeparator() + "\n" +
            " 130    Foreground=\"TextDim\"\n" +
            " 131 +  <Border Width=\"1\"/>\n" +
            " 132    </StackPanel>\n" +
            MakeDashedSeparator() + "\n" +
            " Do you want to make this edit to AgentBotWindow.xaml?\n\n" +
            " > 1. Yes\n" +
            "   2. Yes, allow all edits during this session (shift+tab)\n" +
            "   3. No\n\n" +
            " Esc to cancel\n");

        Assert.Single(events);
        var approval = Assert.IsType<ApprovalRequested>(events[0]);
        Assert.Equal(3, approval.Options.Count);
        Assert.False(approval.IsFallback);
        Assert.False(string.IsNullOrEmpty(approval.Command));
    }

    [Fact]
    public void EventStream_SeparatorOnly_NoOptions_NoEvent()
    {
        var session = new FakeTerminalSession();
        using var stream = new AgentEventStream(session);

        var events = new List<AgentEvent>();
        stream.EventReceived += e => events.Add(e);

        // Separator line without numbered options — should not fire
        session.SimulateOutput(MakeSolidSeparator() + " Some heading\n\nJust normal text\n");

        Assert.Empty(events);
    }
}
