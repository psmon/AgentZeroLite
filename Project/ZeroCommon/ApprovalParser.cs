using System.Text.RegularExpressions;

namespace Agent.Common;

/// <summary>
/// Extracted from AgentBotWindow for testability.
/// Pure-function helpers for approval prompt detection and parsing.
/// </summary>
public static class ApprovalParser
{
    public record ApprovalOption(int Number, string Text);
    public record ApprovalPrompt(string Command, List<ApprovalOption> Options, bool IsFallback = false);

    // ─ (U+2500) = approval prompt separator line (solid)
    // ╌ (U+254C) = diff/code block delimiter (dashed) — must be stripped before parsing
    private const char SolidLine = '\u2500';   // ─
    private const char DashedLine = '\u254C';  // ╌
    private const int SeparatorMinLen = 20;    // minimum run of ─ to count as separator

    /// <summary>
    /// Detects whether the buffer contains an approval prompt pattern.
    /// Two detection paths:
    ///   1. Keyword-based (legacy): "Do you want to proceed", "requires approval", etc.
    ///   2. Separator-based (new): a long ─── line followed by text, then numbered options.
    /// </summary>
    public static bool ContainsApprovalPattern(string buffer)
    {
        // Keyword detection (existing — kept for backward compatibility)
        if (buffer.Contains("Do you want to proceed", StringComparison.OrdinalIgnoreCase) ||
            buffer.Contains("requires approval", StringComparison.OrdinalIgnoreCase) ||
            buffer.Contains("Yes, and don't ask again", StringComparison.OrdinalIgnoreCase) ||
            buffer.Contains("make this edit", StringComparison.OrdinalIgnoreCase))
            return true;

        // Separator detection: ─── (solid, ≥20 chars) followed by numbered options (1. / 2. / 3.)
        if (ContainsSolidSeparator(buffer) && Regex.IsMatch(buffer, @">\s*1\.\s"))
            return true;

        return false;
    }

    /// <summary>
    /// Returns true if the buffer contains a solid separator line (─ repeated ≥ SeparatorMinLen times).
    /// </summary>
    public static bool ContainsSolidSeparator(string buffer)
    {
        int run = 0;
        foreach (var c in buffer)
        {
            if (c == SolidLine)
            {
                if (++run >= SeparatorMinLen) return true;
            }
            else
            {
                run = 0;
            }
        }
        return false;
    }

    /// <summary>
    /// Strips dashed-line (╌) delimited code/diff blocks from the text.
    /// These blocks contain file diffs shown in Edit approval prompts
    /// and pollute the option parsing if left in.
    /// </summary>
    public static string StripDashedBlocks(string text)
    {
        // Find regions between pairs of ╌╌╌ lines (≥10 chars) and remove them.
        var lines = text.Split('\n');

        // Count markers first. If ODD, the 4000-char sliding buffer cut off
        // the OPENING marker (the closing one survives because it's later in
        // time). The leading content is therefore an orphan diff tail that
        // belongs INSIDE a block — start with inBlock=true so it gets stripped
        // and the actual prompt that follows the close marker is preserved.
        int markerCount = 0;
        foreach (var line in lines)
        {
            int dashCount = 0;
            foreach (var c in line)
                if (c == DashedLine) dashCount++;
            if (dashCount >= 10) markerCount++;
        }
        bool inBlock = (markerCount % 2 == 1);

        var result = new List<string>();
        foreach (var line in lines)
        {
            int dashCount = 0;
            foreach (var c in line)
                if (c == DashedLine) dashCount++;

            bool isDashedSep = dashCount >= 10;

            if (isDashedSep)
            {
                inBlock = !inBlock; // toggle: entering or exiting a block
                continue;          // skip the separator line itself
            }

            if (!inBlock)
                result.Add(line);
        }

        return string.Join('\n', result);
    }

    /// <summary>
    /// Strips ANSI/VT escape codes from terminal output.
    /// Converts cursor-position sequences (CSI row;col H) to newlines.
    /// </summary>
    public static string StripAnsiCodes(string input)
    {
        var result = Regex.Replace(input, @"\x1B\[\d+;\d+H", "\n");
        result = Regex.Replace(result,
            @"\x1B(?:\[[0-9;?<>=]*[A-Za-z]|\][^\x07\x1B]*(?:\x07|\x1B\\)|\([A-Za-z]|[>=<])", "");
        return result;
    }

    /// <summary>
    /// Parses an approval prompt from the recent buffer text.
    /// Extracts the command description and numbered options.
    /// </summary>
    public static ApprovalPrompt? ParseApprovalPrompt(string text)
    {
        // Strip dashed-line code/diff blocks (╌╌╌...╌╌╌) to avoid polluting option parsing
        text = StripDashedBlocks(text);

        // Extract command: look for "This command requires approval" and take the line BEFORE it
        var command = "";
        var reqIdx = text.IndexOf("requires approval", StringComparison.OrdinalIgnoreCase);
        if (reqIdx > 0)
        {
            // Find the line containing "requires approval"
            var before = text[..reqIdx];
            var lastNewline = before.LastIndexOf('\n');

            // Check if "This command" is on the same line as "requires approval"
            var reqLine = (lastNewline >= 0 ? before[(lastNewline + 1)..] : before).Trim();
            if (reqLine.StartsWith("This command", StringComparison.OrdinalIgnoreCase)
                || reqLine.Length == 0)
            {
                // The actual command is on the line BEFORE "This command requires approval"
                var beforeReqLine = lastNewline >= 0 ? before[..lastNewline] : "";
                var prevNewline = beforeReqLine.LastIndexOf('\n');
                command = (prevNewline >= 0 ? beforeReqLine[(prevNewline + 1)..] : beforeReqLine).Trim();
            }
            else
            {
                command = reqLine;
            }

            // Strip "Run shell command" prefix if present
            var runIdx = command.IndexOf("Run", StringComparison.OrdinalIgnoreCase);
            if (runIdx >= 0)
            {
                var afterRun = command[(runIdx + 3)..].TrimStart();
                if (afterRun.StartsWith("shell command", StringComparison.OrdinalIgnoreCase))
                    afterRun = afterRun[13..].TrimStart();
                if (afterRun.Length > 0) command = afterRun;
            }
        }

        // Separator-based command extraction: find text after last ─── line
        if (string.IsNullOrWhiteSpace(command) && ContainsSolidSeparator(text))
        {
            command = ExtractCommandAfterSeparator(text);
        }

        // Narrow down to region: from "proceed" to "Esc" (to cancel)
        var proceedIdx = text.IndexOf("proceed", StringComparison.OrdinalIgnoreCase);
        if (proceedIdx < 0) proceedIdx = reqIdx;
        if (proceedIdx < 0) proceedIdx = 0;
        var optionRegion = text[proceedIdx..];

        // Truncate at "Esc" boundary — handles both "Esc to cancel" and "Esctocancel" (ConPTY whitespace collapse)
        var escIdx = Regex.Match(optionRegion, @"Esc\s*to\s*cancel", RegexOptions.IgnoreCase);
        if (escIdx.Success)
            optionRegion = optionRegion[..escIdx.Index];

        // Parse numbered options
        var options = new List<ApprovalOption>();
        var optMatches = Regex.Matches(optionRegion, @"(\d)\.\s*(.+?)(?=\s{2,}\d\.|\n\d\.|\n\s*\d\.|$)",
            RegexOptions.Singleline);

        var seen = new HashSet<int>();
        foreach (Match m in optMatches)
        {
            var num = int.Parse(m.Groups[1].Value);
            if (seen.Contains(num)) continue;
            seen.Add(num);

            var optText = m.Groups[2].Value.Trim();
            optText = optText.TrimStart('>').Trim();
            optText = Regex.Replace(optText, @"\s{2,}", " ").Trim();
            if (optText.Length > 0 && num >= 1 && num <= 5)
                options.Add(new ApprovalOption(num, optText));
        }

        // Fallback: no numbered options were parsed from the buffer.
        // This often means the keyword match was a false positive (e.g. diff/code content).
        if (options.Count == 0)
        {
            options.Add(new ApprovalOption(1, "Yes"));
            options.Add(new ApprovalOption(2, "Yes, and don't ask again"));
            options.Add(new ApprovalOption(3, "No"));
            return new ApprovalPrompt(command, options, IsFallback: true);
        }

        // If command is still empty, try to infer it from context
        if (string.IsNullOrWhiteSpace(command))
            command = InferCommandFromContext(text, options);

        return new ApprovalPrompt(command, options);
    }

    /// <summary>
    /// Infers the command description from option text or buffer context
    /// when direct command extraction fails (the "unknown command" case).
    /// </summary>
    internal static string InferCommandFromContext(string text, List<ApprovalOption> options)
    {
        // 1. "make this edit to <filename>?" pattern in buffer
        var editMatch = Regex.Match(text,
            @"make\s+this\s+edit\s+to\s+(.+?)(?:\?|$)", RegexOptions.IgnoreCase);
        if (editMatch.Success)
            return $"Edit: {editMatch.Groups[1].Value.Trim()}";

        // Try to infer from option 2 text (most descriptive option)
        var opt2 = options.FirstOrDefault(o => o.Number == 2)?.Text ?? "";

        // 2. "don't ask again for: <command>:*"  (shell command with colon-star pattern)
        //    Also handles ConPTY collapsed: "Yes,anddon'taskagainfor:cmd:*"
        var shellMatch = Regex.Match(opt2,
            @"don'?t\s*ask\s*again\s*for:\s*(.+?):\*", RegexOptions.IgnoreCase);
        if (shellMatch.Success)
            return shellMatch.Groups[1].Value.Trim();

        // 3. "don't ask again for <commands> commands in <path>"  (compound shell commands)
        var compoundMatch = Regex.Match(opt2,
            @"don'?t\s*ask\s*again\s*for\s+(.+?)\s+commands?\s+in\s+(.+)",
            RegexOptions.IgnoreCase);
        if (compoundMatch.Success)
            return $"{compoundMatch.Groups[1].Value.Trim()} in {compoundMatch.Groups[2].Value.Trim()}";

        // 4. "allow reading from <path> from this project" or "during this session"
        //    Also handles collapsed: "allowreadingfromtmp\fromthisproject"
        var readMatch = Regex.Match(opt2,
            @"allow\s*reading\s*from\s*(.+?)\s*(?:from\s*this\s*project|during\s*this\s*session)",
            RegexOptions.IgnoreCase);
        if (readMatch.Success)
            return $"Read: {readMatch.Groups[1].Value.Trim()}";

        // 5. "allow all edits during this session"
        if (Regex.IsMatch(opt2, @"allow\s*all\s*edits", RegexOptions.IgnoreCase))
            return "Edit file";

        // 6. "don't ask again for <something>" (generic fallback for other shell patterns)
        //    Handles "Yes, and don't ask again for image-gen in D:\MYNOTE" etc.
        //    Also handles collapsed "don'taskagainfor:grep-o'..." (for: acts as separator)
        var genericMatch = Regex.Match(opt2,
            @"don'?t\s*ask\s*again\s*for[:\s]+(.+)", RegexOptions.IgnoreCase);
        if (genericMatch.Success)
            return genericMatch.Groups[1].Value.Trim();

        // 7. ConPTY collapsed: option text starts with "Yes,and..." — strip prefix and retry
        var yesAndMatch = Regex.Match(opt2,
            @"^Yes,?\s*and\s*", RegexOptions.IgnoreCase);
        if (yesAndMatch.Success)
        {
            var stripped = opt2[yesAndMatch.Length..];
            var retryResult = InferCommandFromContext(text,
                [new ApprovalOption(2, stripped)]);
            if (!string.IsNullOrEmpty(retryResult))
                return retryResult;
        }

        return "";
    }

    /// <summary>
    /// Extracts the command type/description from text after the last ─── solid separator line.
    /// Looks for patterns like "Bash command", "Read file", "Edit file" that appear
    /// right after the separator in Claude Code terminal output.
    /// </summary>
    public static string ExtractCommandAfterSeparator(string text)
    {
        // Find the last occurrence of a solid separator run
        int lastSepEnd = -1;
        int run = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == SolidLine)
            {
                if (++run >= SeparatorMinLen)
                    lastSepEnd = i + 1;
            }
            else
            {
                run = 0;
            }
        }
        if (lastSepEnd < 0) return "";

        // Get the first non-empty line after the separator
        var afterSep = text[lastSepEnd..].TrimStart('\r', '\n', ' ');
        var nlIdx = afterSep.IndexOf('\n');
        var firstLine = (nlIdx >= 0 ? afterSep[..nlIdx] : afterSep).Trim();

        // Sometimes the line after separator has the actual command on the NEXT line
        // e.g. "Bash command\n\n   dotnet build\n..."
        // Return the first line as the command type label
        if (firstLine.Length > 0 && firstLine.Length <= 80)
            return firstLine;

        return "";
    }

    /// <summary>
    /// Simulates the sliding buffer logic: appends new chunk, trims to max length.
    /// Returns the updated buffer.
    /// </summary>
    public static string AppendToBuffer(string currentBuffer, string newChunk, int maxLen = 4000)
    {
        var buffer = currentBuffer + newChunk;
        if (buffer.Length > maxLen)
            buffer = buffer[(buffer.Length - maxLen)..];
        return buffer;
    }

    /// <summary>
    /// Extracts a fingerprint from the approval prompt region for deduplication.
    /// Returns a stable string that identifies this specific approval prompt,
    /// so the same prompt rendered multiple times in the buffer can be detected.
    /// </summary>
    public static string? GetApprovalFingerprint(string buffer)
    {
        var proceedIdx = buffer.IndexOf("proceed", StringComparison.OrdinalIgnoreCase);
        var reqIdx = buffer.IndexOf("requires approval", StringComparison.OrdinalIgnoreCase);
        var editIdx = buffer.IndexOf("make this edit", StringComparison.OrdinalIgnoreCase);
        var startIdx = proceedIdx >= 0 ? proceedIdx : reqIdx >= 0 ? reqIdx : editIdx;

        // Separator-based fallback: use the region after the last ─── line
        if (startIdx < 0 && ContainsSolidSeparator(buffer))
        {
            int run = 0;
            for (int i = 0; i < buffer.Length; i++)
            {
                if (buffer[i] == SolidLine) { if (++run >= SeparatorMinLen) startIdx = i + 1; }
                else run = 0;
            }
        }
        if (startIdx < 0) return null;

        var region = buffer[startIdx..];

        // Truncate at Esc boundary
        var escMatch = Regex.Match(region, @"Esc\s*to\s*cancel", RegexOptions.IgnoreCase);
        if (escMatch.Success)
            region = region[..escMatch.Index];

        // Normalize: collapse whitespace and lowercase for stable comparison
        region = Regex.Replace(region, @"\s+", " ").Trim().ToLowerInvariant();
        return region;
    }
}
